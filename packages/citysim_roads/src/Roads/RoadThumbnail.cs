using Godot;

namespace CitySim.Roads;

/// <summary>
/// A top-down slice of a road drawn from its <see cref="RoadDef"/>: sidewalks, lanes with their markings and the
/// median, scaled to fit. Modded roads get a picture without shipping an icon.
/// </summary>
public partial class RoadThumbnail : Control
{
    private static readonly Color Grass = new(0.34f, 0.47f, 0.27f);
    private static readonly Color Sidewalk = new(0.70f, 0.69f, 0.64f);
    private static readonly Color Asphalt = new(0.26f, 0.28f, 0.31f);
    private static readonly Color Gravel = new(0.56f, 0.50f, 0.40f);
    private static readonly Color MedianTop = new(0.52f, 0.53f, 0.52f);
    private static readonly Color Kerb = new(0.82f, 0.82f, 0.80f);
    private static readonly Color Marking = new(0.93f, 0.93f, 0.90f);

    private RoadDef? _road;

    public RoadDef? Road { get => _road; set { _road = value; QueueRedraw(); } }

    public RoadThumbnail()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = true;
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), Grass);
        if (_road is not { } r) return;

        // Fit the cross-section into the height with a margin of grass, at most 4 px per metre so narrow roads stay small.
        float margin = Size.Y * 0.12f;
        float scale = Mathf.Min((Size.Y - 2f * margin) / r.Width, 4f);
        float y = (Size.Y - r.Width * scale) / 2f, w = Size.X;

        // Bands first, top to bottom, remembering where each line goes; then the lines on top.
        var lines = new System.Collections.Generic.List<(float Y, Color C, bool Dashed, float Thick)>();
        void Band(float metres, Color c) { DrawRect(new Rect2(0, y, w, metres * scale), c); y += metres * scale; }

        if (r.Sidewalks == SidewalkLayout.Both) { Band(r.SidewalkWidth, Sidewalk); lines.Add((y, Kerb, false, 1f)); }
        int firstDir = r.OneWay ? r.Lanes : r.Backward; // first lane of the forward direction (backward lanes are on top)
        bool gravel = r.Surface == RoadSurface.Gravel;
        if (r.StripWidth > 0) { Band(r.StripWidth, gravel ? Gravel : Asphalt); if (!gravel) lines.Add((y, Marking, false, 1f)); }
        for (int lane = 0; lane < r.Lanes; lane++)
        {
            if (lane == firstDir && r.Median == MedianKind.Raised)
            {
                lines.Add((y, Kerb, false, 1f));
                Band(r.MedianWidth, MedianTop);
                lines.Add((y, Kerb, false, 1f));
            }
            else if (gravel) { } // no markings on gravel
            else if (lane == firstDir) lines.Add((y, Marking, true, 1.5f)); // the centre line: white, dashed (RoadStyle)
            else if (lane > 0) lines.Add((y, Marking, true, 1f));
            Band(r.LaneWidth, gravel ? Gravel : Asphalt);
        }
        if (r.StripWidth > 0) { if (!gravel) lines.Add((y, Marking, false, 1f)); Band(r.StripWidth, gravel ? Gravel : Asphalt); }
        if (r.Sidewalks == SidewalkLayout.Both) { lines.Add((y, Kerb, false, 1f)); Band(r.SidewalkWidth, Sidewalk); }

        foreach (var (at, c, dashed, thick) in lines)
        {
            if (!dashed) { DrawLine(new Vector2(0, at), new Vector2(w, at), c, thick); continue; }
            for (float x = 3f; x < w; x += 12f) DrawLine(new Vector2(x, at), new Vector2(Mathf.Min(x + 6f, w), at), c, thick);
        }

        if (r.OneWay)
        {
            var c = new Vector2(w / 2f, Size.Y / 2f);
            DrawLine(c - new Vector2(10, 0), c + new Vector2(10, 0), Marking, 1.5f);
            DrawPolyline([c + new Vector2(5, -4), c + new Vector2(10, 0), c + new Vector2(5, 4)], Marking, 1.5f);
        }
    }
}
