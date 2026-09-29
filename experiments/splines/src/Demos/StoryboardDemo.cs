using System;
using System.Collections.Generic;
using System.Linq;
using CitySim.Splines.Godot;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Splines;

/// <summary>
/// <c>--storyboard=&lt;frame&gt;</c>: rebuilds one frame of <c>docs/spline-controls.html</c> (Draw, Snapping and Guides
/// sections) in the testbed: its roads, the draw in progress, the cursor and any held keys. Pair it with
/// <c>--screenshot=</c> and <c>--cam=560,500,340,89,0</c> to compare the result with the HTML frame side by side.
/// Frame coordinates are the HTML's 320 × 200 SVG units, one unit = one metre, offset to <see cref="Origin"/>.
/// <c>--storyboard=list</c> prints the frame names.
/// </summary>
public partial class StoryboardDemo : Node
{
    [Export] public SplineDrawTool? DrawTool { get; set; }
    [Export] public SplinesTestbed? Testbed { get; set; }

    private static readonly NumVector2 Origin = new(400, 400);

    private readonly Dictionary<string, Action> _frames;

    public StoryboardDemo()
    {
        _frames = new Dictionary<string, Action>
        {
            ["click-start"] = ClickStart,
            ["corner"] = Corner,
            ["hard-corner"] = HardCorner,
            ["finish"] = Finish,
            ["extension"] = Extension,
            ["angle-ctrl"] = AngleCtrl,
            ["length-node"] = LengthNode,
            ["node-align"] = NodeAlign,
            ["parallel"] = Parallel,
            ["parallel-arc"] = ParallelArc,
            ["perpendicular"] = Perpendicular,
            ["equal-length"] = EqualLength,
            ["crossing"] = Crossing,
            ["rail-clamped"] = RailClamped,
        };
    }

    public override void _Ready()
    {
        var arg = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--storyboard="));
        if (arg is null) return;
        string name = arg["--storyboard=".Length..];
        if (name == "list" || !_frames.ContainsKey(name))
        {
            GD.Print($"Storyboard frames: {string.Join(", ", _frames.Keys)}");
            return;
        }
        Callable.From(() =>
        {
            if (DrawTool is null || Testbed is null) { GD.PrintErr("Storyboard: missing DrawTool/Testbed"); return; }
            _frames[name]();
            GD.Print($"Storyboard: {name}");
        }).CallDeferred();
    }

    // --- Story: drawing a road, click by click ---

    private static readonly (float, float) MainA = (20, 50), MainB = (300, 50), S0 = (70, 50), S1 = (140, 160), S2 = (290, 150);

    private void ClickStart()
    {
        Use("street");
        Build("street", MainA, MainB);
        Click(S0);
        Hover((72, 128));
    }

    private void Corner()
    {
        Use("street");
        Build("street", MainA, MainB);
        Click(S0);
        Click(S1);
        DrawTool!.AdjustRadiusForTest(36f / 16f); // the storyboard's "R 36 m" after a few Shift+wheel notches
        Hover((250, 152));
    }

    private void HardCorner()
    {
        Use("street");
        Build("street", MainA, MainB);
        Click(S0);
        Click(S1, hard: true);
        Hover((250, 152));
    }

    private void Finish()
    {
        Use("street");
        Build("street", MainA, MainB);
        Click(S0);
        Click(S1);
        DrawTool!.AdjustRadiusForTest(36f / 16f);
        Click(S2);
        DrawTool.FinishForTest();
        Hover((270, 105)); // clear of guides, as in the storyboard frame

    }

    // --- Snapping ---

    private void Extension()
    {
        Use("street");
        Build("street", (20, 60), (150, 60));
        Build("street", (20, 140), (120, 140));
        Build("street", (160, 185), (315, 185));
        Click((240, 185));
        Hover((240.8f, 60.8f));
    }

    private void AngleCtrl()
    {
        Use("avenue");
        Click((70, 150));
        DrawTool!.ForcedModifiers = DrawModifiers.Ctrl;
        var c = new NumVector2(70, 150) + SplineMath.Direction(-32f * MathF.PI / 180f) * 200f;
        Hover((c.X, c.Y));
    }

    private void LengthNode()
    {
        Use("street");
        Build("street", (200, 30), (200, 105));
        Build("street", (200, 105), (200, 180));
        Click((32, 105));
        Hover((199, 106));
    }

    // --- Guides ---

    private void NodeAlign()
    {
        Use("street");
        Build("street", (70, 20), (70, 70));
        Build("street", (70, 70), (70, 190));
        Click((180, 190));
        Hover((250, 70.8f));
    }

    private void Parallel()
    {
        Use("street");
        Build("avenue", (20, 60), (300, 60));
        const float y = 60 + 12 + 6 + 40; // avenue half width + street half width + 40 m (5 lots)
        Click((60, y));
        Hover((250, y + 0.6f));
    }

    private void ParallelArc()
    {
        Use("street");
        var highway = Profile("highway");
        var arc = new Alignment(new[] { new Pi(P((-100, 250))), new Pi(P((160, -10)), 230), new Pi(P((420, 250))) });
        DrawTool!.AddBuiltForTest(highway, arc);
        // 24 m (3 lots) inside the bend: highway half width 12 + street half width 6 + 24.
        var s = arc.Curve.Sample(arc.Curve.Length * 0.58f);
        var target = s.Position - SplineMath.Left(s.Tangent) * (12 + 6 + 24 + 0.4f);
        var start = target + new NumVector2(-60, 70);
        Click((start.X - Origin.X, start.Y - Origin.Y));
        Hover((target.X - Origin.X, target.Y - Origin.Y));
    }

    private void Perpendicular()
    {
        Use("street");
        NumVector2 a = new(20, 150), b = new(300, 40);
        Build("avenue", (a.X, a.Y), (b.X, b.Y));
        var d = NumVector2.Normalize(b - a);
        var n = new NumVector2(d.Y, -d.X);
        var foot = a + d * 170;
        var q = foot - n * 120;
        Click((q.X, q.Y));
        Hover((foot.X + 0.6f, foot.Y + 0.6f));
    }

    private void EqualLength()
    {
        Use("street");
        Click((30, 170));
        Click((30, 70));
        Hover((130.4f, 70.5f));
    }

    private void Crossing()
    {
        Use("street");
        Build("street", (20, 60), (130, 60));
        Build("street", (250, 195), (250, 150));
        Click((70, 175));
        Hover((250.8f, 60.6f));
    }

    // --- Networks: same three clicks, rail wants more room ---

    private void RailClamped()
    {
        Use("rail");
        Click((20, 178));
        Click((160, 48));
        Hover((300, 178));
    }

    // --- Helpers ---

    private static NumVector2 P((float X, float Y) svg) => Origin + new NumVector2(svg.X, svg.Y);

    private SplineProfile Profile(string id) =>
        Testbed!.Profiles.FirstOrDefault(p => p.Id == id) ?? throw new InvalidOperationException($"no profile {id}");

    private void Use(string id) => Testbed!.SelectProfile(Profile(id));

    /// <summary>A built straight spline of <paramref name="id"/>'s profile.</summary>
    private void Build(string id, (float, float) a, (float, float) b) =>
        DrawTool!.AddBuiltForTest(Profile(id), new Alignment(new[] { new Pi(P(a)), new Pi(P(b)) }));

    private void Click((float, float) at, bool hard = false)
    {
        DrawTool!.ForcedPlanCursor = P(at);
        DrawTool.PlaceForTest(hard);
    }

    private void Hover((float, float) at) => DrawTool!.ForcedPlanCursor = P(at);
}
