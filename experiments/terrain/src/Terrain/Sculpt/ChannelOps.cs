using System;
using System.Numerics;

namespace CitySim.TerrainSystem.Sculpt;

/// <summary>Cross-section of a <see cref="ChannelOps"/> cut, seen along the path.</summary>
public enum ChannelShape
{
    /// <summary>\/ : straight sides meeting at the bottom.</summary>
    V,
    /// <summary>U : a rounded (parabolic) bed, like a natural river.</summary>
    Rounded,
    /// <summary>\_/ : a flat bed, 40 % of the width, with sloped sides.</summary>
    FlatBed,
    /// <summary>|_| : a flat bed with vertical walls, like a canal.</summary>
    Box,
}

/// <summary>A cross-section: its shape, width at the top and depth below the reference height, in metres.</summary>
public readonly record struct ChannelProfile(ChannelShape Shape, float Width, float Depth)
{
    /// <summary>Share of the full depth at <paramref name="u"/> = distance from the centre line / half width (0 to 1).</summary>
    public static float Fraction(ChannelShape shape, float u)
    {
        if (u >= 1f) return 0f;
        return shape switch
        {
            ChannelShape.V => 1f - u,
            ChannelShape.Rounded => 1f - u * u,
            ChannelShape.FlatBed => u <= FlatBedShare ? 1f : 1f - (u - FlatBedShare) / (1f - FlatBedShare),
            _ => 1f,
        };
    }

    /// <summary>Share of the width the <see cref="ChannelShape.FlatBed"/> bed takes.</summary>
    public const float FlatBedShare = 0.4f;

    public static ChannelProfile Lerp(ChannelProfile a, ChannelProfile b, float t) =>
        new(t < 0.5f ? a.Shape : b.Shape, a.Width + (b.Width - a.Width) * t, a.Depth + (b.Depth - a.Depth) * t);
}

/// <summary>
/// One point of a channel path: where it is (local metres), the height its cross-section hangs from (the ground surface
/// the channel is cut into) and its cross-section there.
/// </summary>
public readonly record struct ChannelPoint(Vector2 Position, float Reference, ChannelProfile Profile);

/// <summary>
/// Cuts a channel along a path into a <see cref="HeightMap"/>: the cross-section is swept along each segment, turned
/// across the direction of travel. The cut only ever lowers ground (it keeps the lower of the ground and the channel),
/// so going over a stretch again never digs it deeper, and ground already below the bed (a dip) is left alone. Beyond
/// the top edge, banks rise at a fixed angle, so a cut across a hillside gets a sloped bank instead of a cliff.
/// </summary>
public static class ChannelOps
{
    public const float DefaultBankDegrees = 35f;

    /// <summary>How far past the top edge the banks may reach, in widths and depths (the rest stays a cliff).</summary>
    private const float BankReachWidths = 1.5f, BankReachDepths = 3f;

    /// <summary>
    /// Cuts the segment from <paramref name="a"/> to <paramref name="b"/> (the same point for a single stamp): reference
    /// height and cross-section are interpolated along it. Returns the vertices it may have changed; call
    /// <see cref="Bounds"/> first to save them for undo. <paramref name="amount"/> (0 to 1) is the share of the way to the
    /// channel each vertex goes: 1 cuts it at once, less digs gradually over repeated calls (never past the channel).
    /// </summary>
    public static VertexRect CarveSegment(HeightMap map, ChannelPoint a, ChannelPoint b, float bankDegrees = DefaultBankDegrees,
        float amount = 1f)
    {
        var rect = Bounds(map, a, b);
        if (rect.IsEmpty) return rect;
        float bankTan = MathF.Tan(Math.Clamp(bankDegrees, 5f, 89f) * (MathF.PI / 180f));
        Vector2 ab = b.Position - a.Position;
        float lenSq = ab.LengthSquared();
        float cell = map.CellSize;

        for (int z = rect.MinZ; z <= rect.MaxZ; z++)
            for (int x = rect.MinX; x <= rect.MaxX; x++)
            {
                var p = new Vector2(x * cell, z * cell);
                float t = lenSq > 1e-6f ? Math.Clamp(Vector2.Dot(p - a.Position, ab) / lenSq, 0f, 1f) : 0f;
                float d = Vector2.Distance(p, a.Position + ab * t);
                float half = 0.5f * (a.Profile.Width + (b.Profile.Width - a.Profile.Width) * t);
                float depth = a.Profile.Depth + (b.Profile.Depth - a.Profile.Depth) * t;
                float reference = a.Reference + (b.Reference - a.Reference) * t;
                float target;
                if (d < half)
                {
                    // Blend the two shapes, so a start and end of different shapes change smoothly along the path.
                    float u = d / MathF.Max(half, 1e-3f);
                    float fa = ChannelProfile.Fraction(a.Profile.Shape, u), fb = ChannelProfile.Fraction(b.Profile.Shape, u);
                    target = reference - depth * (fa + (fb - fa) * t);
                }
                else if (d < half + BankReach(half, depth))
                    target = reference + (d - half) * bankTan;
                else continue;
                float h = map[x, z];
                if (target < h) map[x, z] = amount >= 1f ? target : h + (target - h) * amount;
            }
        return rect;
    }

    /// <summary>The vertices <see cref="CarveSegment"/> can reach for this segment.</summary>
    public static VertexRect Bounds(HeightMap map, ChannelPoint a, ChannelPoint b)
    {
        float reach = MathF.Max(Reach(a.Profile), Reach(b.Profile));
        float minX = MathF.Min(a.Position.X, b.Position.X) - reach, maxX = MathF.Max(a.Position.X, b.Position.X) + reach;
        float minZ = MathF.Min(a.Position.Y, b.Position.Y) - reach, maxZ = MathF.Max(a.Position.Y, b.Position.Y) + reach;
        var r = new VertexRect(
            Math.Max(0, (int)MathF.Floor(minX / map.CellSize)), Math.Max(0, (int)MathF.Floor(minZ / map.CellSize)),
            Math.Min(map.Width - 1, (int)MathF.Ceiling(maxX / map.CellSize)), Math.Min(map.Depth - 1, (int)MathF.Ceiling(maxZ / map.CellSize)));
        return r;
    }

    private static float BankReach(float half, float depth) => BankReachWidths * 2f * half + BankReachDepths * depth;

    private static float Reach(ChannelProfile p) => 0.5f * p.Width + BankReach(0.5f * p.Width, p.Depth);
}
