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
}

/// <summary>
/// One point of a channel path: where it is (local metres) and the height its cross-section hangs from (the top of
/// the channel: the ground it's cut into, or the grade).
/// </summary>
public readonly record struct ChannelPoint(Vector2 Position, float Reference);

/// <summary>What a straight channel from a to b would do to the ground: see <see cref="ChannelOps.Measure"/>.</summary>
/// <param name="Length">Horizontal length, metres.</param>
/// <param name="Rise">Reference height at b minus at a (negative runs downhill).</param>
/// <param name="MaxCut">Deepest the ground on the centre line sits above the bed.</param>
/// <param name="MaxFill">Highest wall needed: how far the ground on the centre line or along the walls sits below the top.</param>
public readonly record struct ChannelMeasure(float Length, float Rise, float MaxCut, float MaxFill)
{
    /// <summary>Rise over length, as a percentage.</summary>
    public float GradePercent => Length > 1e-3f ? 100f * Rise / Length : 0f;
}

/// <summary>
/// Cuts a channel along a path into a <see cref="HeightMap"/>: the cross-section is swept along each segment, turned
/// across the direction of travel. By default the cut only lowers ground (it keeps the lower of the ground and the
/// channel), so going over a stretch again never digs it deeper, and ground already below the bed (a dip) is left
/// alone. Beyond the top edge, banks rise at a fixed angle, so a cut across a hillside gets a sloped bank instead of a
/// cliff. With fill, dips are filled too: the bed is laid exactly, walls with a flat crest stand at the reference
/// height, and embankments slope down from them at the bank angle to the ground.
/// </summary>
public static class ChannelOps
{
    public const float DefaultBankDegrees = 35f;

    /// <summary>How far past the top edge the cut banks may reach, in widths and depths (the rest stays a cliff).</summary>
    private const float BankReachWidths = 1.5f, BankReachDepths = 3f;

    /// <summary>
    /// Cuts the segment from <paramref name="a"/> to <paramref name="b"/> (the same point for a single stamp): the
    /// reference height is interpolated along it. Returns the vertices it may have changed; call <see cref="Bounds"/>
    /// first (same arguments) to save them for undo. <paramref name="amount"/> (0 to 1) is the share of the way to the
    /// channel each vertex goes: 1 cuts it at once, less digs gradually over repeated calls (never past the channel).
    /// With <paramref name="fill"/>, ground below the channel is raised as well (walls and embankments).
    /// </summary>
    public static VertexRect CarveSegment(HeightMap map, ChannelPoint a, ChannelPoint b, ChannelProfile profile,
        float bankDegrees = DefaultBankDegrees, float amount = 1f, bool fill = false)
    {
        var rect = Bounds(map, a, b, profile, bankDegrees, fill);
        if (rect.IsEmpty) return rect;
        float bankTan = BankTan(bankDegrees);
        Vector2 ab = b.Position - a.Position;
        float lenSq = ab.LengthSquared();
        float cell = map.CellSize;
        float half = 0.5f * profile.Width;
        float cutReach = half + BankReach(half, profile.Depth);
        float crest = Crest(map, profile);
        float fillReach = fill ? half + FillReach(map, a, b, profile, bankTan) : 0f;

        for (int z = rect.MinZ; z <= rect.MaxZ; z++)
            for (int x = rect.MinX; x <= rect.MaxX; x++)
            {
                var p = new Vector2(x * cell, z * cell);
                float t = lenSq > 1e-6f ? Math.Clamp(Vector2.Dot(p - a.Position, ab) / lenSq, 0f, 1f) : 0f;
                float d = Vector2.Distance(p, a.Position + ab * t);
                if (d >= cutReach && d >= fillReach) continue;
                float reference = a.Reference + (b.Reference - a.Reference) * t;
                float h = map[x, z];
                float target;
                if (d < half)
                {
                    target = reference - profile.Depth * ChannelProfile.Fraction(profile.Shape, d / MathF.Max(half, 1e-3f));
                    if (!fill && target >= h) continue;
                }
                else if (d < cutReach && h > reference + (d - half) * bankTan)
                    target = reference + (d - half) * bankTan;
                else if (d < fillReach && h < reference - MathF.Max(d - half - crest, 0f) * bankTan)
                    target = reference - MathF.Max(d - half - crest, 0f) * bankTan;
                else continue;
                map[x, z] = amount >= 1f ? target : h + (target - h) * amount;
            }
        return rect;
    }

    /// <summary>The vertices <see cref="CarveSegment"/> can reach for this segment (pass the same arguments).</summary>
    public static VertexRect Bounds(HeightMap map, ChannelPoint a, ChannelPoint b, ChannelProfile profile,
        float bankDegrees = DefaultBankDegrees, bool fill = false)
    {
        float half = 0.5f * profile.Width;
        float reach = half + BankReach(half, profile.Depth);
        if (fill) reach = MathF.Max(reach, half + FillReach(map, a, b, profile, BankTan(bankDegrees)));
        float minX = MathF.Min(a.Position.X, b.Position.X) - reach, maxX = MathF.Max(a.Position.X, b.Position.X) + reach;
        float minZ = MathF.Min(a.Position.Y, b.Position.Y) - reach, maxZ = MathF.Max(a.Position.Y, b.Position.Y) + reach;
        var r = new VertexRect(
            Math.Max(0, (int)MathF.Floor(minX / map.CellSize)), Math.Max(0, (int)MathF.Floor(minZ / map.CellSize)),
            Math.Min(map.Width - 1, (int)MathF.Ceiling(maxX / map.CellSize)), Math.Min(map.Depth - 1, (int)MathF.Ceiling(maxZ / map.CellSize)));
        return r;
    }

    /// <summary>
    /// Measures a straight channel from <paramref name="a"/> to <paramref name="b"/> on the ground as it is now: its
    /// length and rise, how deep it cuts and how high its walls would stand (sampled every cell along the centre line
    /// and both wall crests).
    /// </summary>
    public static ChannelMeasure Measure(HeightMap map, ChannelPoint a, ChannelPoint b, ChannelProfile profile)
    {
        Vector2 ab = b.Position - a.Position;
        float len = ab.Length();
        Vector2 side = len > 1e-3f ? new Vector2(-ab.Y, ab.X) / len : Vector2.Zero;
        float wall = 0.5f * profile.Width + 0.5f * Crest(map, profile);
        int steps = Math.Max(1, (int)MathF.Ceiling(len / map.CellSize));
        float maxCut = 0f, maxFill = 0f;
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            var p = a.Position + ab * t;
            float reference = a.Reference + (b.Reference - a.Reference) * t;
            float h = map.SampleHeight(p.X, p.Y);
            maxCut = MathF.Max(maxCut, h - (reference - profile.Depth));
            float low = MathF.Min(h, MathF.Min(map.SampleHeight(p.X + side.X * wall, p.Y + side.Y * wall),
                map.SampleHeight(p.X - side.X * wall, p.Y - side.Y * wall)));
            maxFill = MathF.Max(maxFill, reference - low);
        }
        return new ChannelMeasure(len, b.Reference - a.Reference, maxCut, maxFill);
    }

    /// <summary>Width of the flat top of the walls <see cref="CarveSegment"/> builds with fill.</summary>
    private static float Crest(HeightMap map, ChannelProfile p) => MathF.Max(2f * map.CellSize, 0.1f * p.Width);

    /// <summary>
    /// How far past the top edge a fill's crest and embankment reach: down from the highest wall at the bank angle,
    /// doubled for ground that keeps falling away beside the walls (a hillside); past that the embankment ends in a cliff.
    /// </summary>
    private static float FillReach(HeightMap map, ChannelPoint a, ChannelPoint b, ChannelProfile profile, float bankTan) =>
        Crest(map, profile) + 2f * Measure(map, a, b, profile).MaxFill / bankTan + map.CellSize;

    private static float BankTan(float degrees) => MathF.Tan(Math.Clamp(degrees, 5f, 89f) * (MathF.PI / 180f));

    private static float BankReach(float half, float depth) => BankReachWidths * 2f * half + BankReachDepths * depth;
}
