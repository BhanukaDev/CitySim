using System;
using System.Collections.Generic;
using System.Linq;

namespace CitySim.Roads.Geometry;

/// <summary>What a piece of road surface is made of; the renderer picks a material per kind. <see cref="Wear"/> is the
/// tyre wear laid over a junction's asphalt along the paths cars take.</summary>
public enum SurfaceKind { Asphalt, Gravel, Gutter, Kerb, Sidewalk, Paint, Wear }

/// <summary>A road style's numbers, as plain data (see <c>RoadStyle</c>). Metres; <see cref="Crown"/> is a slope.</summary>
public sealed record SectionStyle
{
    public float KerbHeight { get; init; } = 0.15f;
    public float KerbTopWidth { get; init; } = 0.15f;
    public float Crown { get; init; } = 0.02f;
    public float GutterWidth { get; init; } = 0.5f;
    public float SkirtDepth { get; init; } = 0.3f;
    public float LineWidth { get; init; } = 0.12f;
    public float CentreDash { get; init; } = 3f;
    public float CentreGap { get; init; } = 6f;
    public float LaneDash { get; init; } = 3f;
    public float LaneGap { get; init; } = 6f;
    public float StopLineWidth { get; init; } = 0.3f;
    public float CrossingSetback { get; init; } = 0.5f;
    public float CrossingWidth { get; init; } = 3f;
    public float CrossingBar { get; init; } = 0.5f;
    public float CrossingGap { get; init; } = 0.5f;
    public float StopLineGap { get; init; } = 1f;
    public float SolidApproach { get; init; } = 15f;
    public float CrossingMinGap { get; init; } = 25f;
}

/// <summary>A traffic lane: its centre's offset from the centre line, and whether it runs along the road's curve
/// (<see cref="Forward"/>) or against it.</summary>
public readonly record struct SectionLane(float Offset, bool Forward);

/// <summary>A band across the road from <see cref="Left"/> to <see cref="Right"/> (offsets from the centre line, + = left
/// of travel, so Left &gt; Right). <see cref="Raised"/> bands sit at kerb height; the rest are carriageway, crowned.
/// <see cref="Lanes"/>: traffic lanes (worn by wheels), as opposed to a strip or gutter of the same surface.</summary>
public readonly record struct SectionBand(SurfaceKind Surface, float Left, float Right, bool Raised, bool Lanes = false);

/// <summary>A painted line along the road at <see cref="Offset"/>; <see cref="Dash"/> 0 = solid. The <see cref="Centre"/> line
/// carries on through a width transition into the next road.</summary>
public readonly record struct SectionLine(float Offset, float Width, float Dash, float Gap, bool Centre = false);

/// <summary>One point of the cross-section outline, left to right. <see cref="Surface"/> runs from it to the next point,
/// over traffic lanes if <see cref="Lanes"/>.</summary>
public readonly record struct SectionPoint(float Offset, float Height, SurfaceKind Surface, bool Lanes = false);

/// <summary>
/// A road's cross-section from its <see cref="RoadDef"/> and a style: the bands left to right (sidewalk, kerb, gutter,
/// strip, lanes, median, ...), the painted lines, and the outline the mesh is extruded from. Heights are above the
/// ground. Generic over lane counts, strips, medians and sidewalks, so every road type uses it.
/// </summary>
public sealed class RoadSection
{
    public SectionStyle Style { get; }
    public float HalfWidth { get; }
    /// <summary>Centre line to kerb (the carriageway's half width).</summary>
    public float HalfCarriageway { get; }
    public bool HasSidewalks { get; }
    /// <summary>The concrete gutter along each kerb (0 = none).</summary>
    public float GutterWidth { get; }
    public IReadOnlyList<SectionBand> Bands { get; }
    public IReadOnlyList<SectionLine> Lines { get; }
    /// <summary>The traffic lanes, left to right.</summary>
    public IReadOnlyList<SectionLane> Lanes { get; }
    public float LaneWidth => _laneWidth;
    /// <summary>Where the lanes running along the curve start: they're right of this offset, the others left of it
    /// (a one-way road's lane edge). The kerbside lane each way is the one furthest from it.</summary>
    public float Split { get; }
    private readonly float _laneEdge, _leftEnd, _medianEnd, _laneWidth;
    private readonly int _leftLanes;

    private RoadSection(SectionStyle style, float halfWidth, float halfCarriageway, bool sidewalks,
        List<SectionBand> bands, List<SectionLine> lines, float laneEdge, float leftEnd, float medianEnd, float laneWidth, int leftLanes,
        int rightLanes)
    {
        (_laneEdge, _leftEnd, _medianEnd, _laneWidth, _leftLanes) = (laneEdge, leftEnd, medianEnd, laneWidth, leftLanes);
        Style = style;
        HalfWidth = halfWidth;
        HalfCarriageway = halfCarriageway;
        HasSidewalks = sidewalks;
        GutterWidth = bands.Where(b => b.Surface == SurfaceKind.Gutter).Select(b => b.Left - b.Right).DefaultIfEmpty(0).Max();
        Bands = bands;
        Lines = lines;
        var lanes = new List<SectionLane>();
        for (int k = 0; k < leftLanes; k++) lanes.Add(new(laneEdge - (k + 0.5f) * laneWidth, false));
        for (int k = 0; k < rightLanes; k++) lanes.Add(new(medianEnd - (k + 0.5f) * laneWidth, true));
        Lanes = lanes;
        Split = leftLanes > 0 ? (leftEnd + medianEnd) / 2 : laneEdge;
    }

    /// <summary>Carriageway height at an offset: the crown falls from the centre line to each kerb.
    /// <paramref name="crownScale"/> fades it out (0 = flat) where the road meets a flat junction.</summary>
    public float CarriagewayHeight(float offset, float crownScale = 1f) =>
        Style.Crown * crownScale * MathF.Max(0, HalfCarriageway - MathF.Abs(offset));

    /// <summary>Where an offset is across the lanes, in lanes counted from the leftmost lane's left edge (1.5 = the middle
    /// of the second lane), skipping a median. Linear inside each lane band, so the shaders can interpolate it.</summary>
    public float LaneCoord(float offset) => offset >= _leftEnd
        ? (_laneEdge - offset) / _laneWidth
        : _leftLanes + (_medianEnd - offset) / _laneWidth;

    public float HeightOf(SectionBand band, float offset, float crownScale = 1f) =>
        band.Raised ? Style.KerbHeight : CarriagewayHeight(offset, crownScale);

    /// <summary>The outline left to right: up the left skirt, across every band (a vertical kerb face where two bands
    /// differ in height, a point on the crown at the centre line), down the right skirt.</summary>
    public List<SectionPoint> Outline(float crownScale = 1f)
    {
        var pts = new List<SectionPoint>();
        var first = Bands[0];
        pts.Add(new(HalfWidth, -Style.SkirtDepth, first.Surface));
        pts.Add(new(HalfWidth, HeightOf(first, HalfWidth, crownScale), first.Surface, first.Lanes));
        for (int i = 0; i < Bands.Count; i++)
        {
            var b = Bands[i];
            if (!b.Raised && b.Left > 0 && b.Right < 0) pts.Add(new(0, HeightOf(b, 0, crownScale), b.Surface, b.Lanes));
            float h = HeightOf(b, b.Right, crownScale);
            if (i + 1 == Bands.Count)
            {
                pts.Add(new(b.Right, h, b.Surface));
                pts.Add(new(b.Right, -Style.SkirtDepth, b.Surface));
                break;
            }
            var next = Bands[i + 1];
            float hn = HeightOf(next, b.Right, crownScale);
            if (MathF.Abs(hn - h) > 1e-4f)
            {
                pts.Add(new(b.Right, h, SurfaceKind.Kerb));
                pts.Add(new(b.Right, hn, next.Surface, next.Lanes));
            }
            else pts.Add(new(b.Right, h, next.Surface, next.Lanes));
        }
        return pts;
    }

    public static RoadSection From(RoadDef d, SectionStyle s)
    {
        float halfW = d.Width / 2f, halfC = d.CarriagewayWidth / 2f;
        bool sidewalks = d.Sidewalks == SidewalkLayout.Both;
        var road = d.Surface == RoadSurface.Gravel ? SurfaceKind.Gravel : SurfaceKind.Asphalt;
        var bands = new List<SectionBand>();
        var lines = new List<SectionLine>();
        float kerbTop = MathF.Min(s.KerbTopWidth, d.SidewalkWidth);
        float gutter = sidewalks && d.StripWidth >= s.GutterWidth ? s.GutterWidth : 0f;

        // Left side, outside in.
        if (sidewalks)
        {
            Band(SurfaceKind.Sidewalk, halfW, halfC + kerbTop, true);
            Band(SurfaceKind.Kerb, halfC + kerbTop, halfC, true);
        }
        if (gutter > 0) Band(SurfaceKind.Gutter, halfC, halfC - gutter, false);
        float laneEdge = halfC - d.StripWidth; // where the lanes start, left side
        if (d.StripWidth > gutter) Band(road, halfC - gutter, laneEdge, false);

        // Lanes: the backward direction on the left (right-hand traffic), the median, then forward.
        int left = d.OneWay ? 0 : d.Backward, right = d.OneWay ? d.Lanes : d.Forward;
        bool median = d.Median == MedianKind.Raised && !d.OneWay;
        float o = laneEdge;
        float leftEnd = o - left * d.LaneWidth;
        float medianEnd = median ? leftEnd - d.MedianWidth : leftEnd;
        float rightEnd = medianEnd - right * d.LaneWidth;
        if (median)
        {
            if (left > 0) Band(road, o, leftEnd, false, true);
            float mt = MathF.Min(kerbTop, d.MedianWidth / 2);
            Band(SurfaceKind.Kerb, leftEnd, leftEnd - mt, true);
            if (d.MedianWidth > 2 * mt) Band(SurfaceKind.Sidewalk, leftEnd - mt, medianEnd + mt, true);
            Band(SurfaceKind.Kerb, medianEnd + mt, medianEnd, true);
            if (right > 0) Band(road, medianEnd, rightEnd, false, true);
        }
        else Band(road, o, rightEnd, false, true);

        // Right side, inside out (mirrors the left).
        if (d.StripWidth > gutter) Band(road, -laneEdge, -(halfC - gutter), false);
        if (gutter > 0) Band(SurfaceKind.Gutter, -(halfC - gutter), -halfC, false);
        if (sidewalks)
        {
            Band(SurfaceKind.Kerb, -halfC, -(halfC + kerbTop), true);
            Band(SurfaceKind.Sidewalk, -(halfC + kerbTop), -halfW, true);
        }
        MergeNeighbours(bands);

        if (road != SurfaceKind.Gravel)
        {
            float w = s.LineWidth;
            // Edge lines where the lanes meet the strips, just inside the strip.
            if (d.StripWidth > w)
            {
                lines.Add(new(laneEdge + w / 2, w, 0, 0));
                lines.Add(new(-(laneEdge + w / 2), w, 0, 0));
            }
            for (int k = 1; k < left; k++) lines.Add(new(laneEdge - k * d.LaneWidth, w, s.LaneDash, s.LaneGap));
            for (int k = 1; k < right; k++) lines.Add(new(medianEnd - k * d.LaneWidth, w, s.LaneDash, s.LaneGap));
            if (!median && !d.OneWay && left > 0 && right > 0) lines.Add(new(leftEnd, w, s.CentreDash, s.CentreGap, Centre: true));
        }
        return new RoadSection(s, halfW, halfC, sidewalks, bands, lines, laneEdge, leftEnd, medianEnd, d.LaneWidth, left, right);

        void Band(SurfaceKind kind, float l, float r, bool raised, bool lanes = false)
        {
            if (l - r > 1e-4f) bands.Add(new(kind, l, r, raised, lanes));
        }
    }

    /// <summary>Joins neighbouring bands of the same kind and height (two lanes become one asphalt band); lanes and strips
    /// stay apart, so the outline has a point where the wheel wear starts.</summary>
    private static void MergeNeighbours(List<SectionBand> bands)
    {
        for (int i = bands.Count - 1; i > 0; i--)
        {
            var (a, b) = (bands[i - 1], bands[i]);
            if (a.Surface != b.Surface || a.Raised != b.Raised || a.Lanes != b.Lanes || MathF.Abs(a.Right - b.Left) > 1e-4f) continue;
            bands[i - 1] = a with { Right = b.Right };
            bands.RemoveAt(i);
        }
    }
}
