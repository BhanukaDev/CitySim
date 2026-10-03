using System;
using System.Collections.Generic;
using System.Linq;
using CitySim.Splines.Godot;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Splines;

/// <summary>
/// <c>--storyboard=&lt;frame&gt;</c>: rebuilds one frame of <c>docs/spline-controls.html</c> (Draw, Snapping, Guides,
/// Junctions and Editing sections) in the testbed: its roads, the draw in progress, the cursor and any held keys. Pair it with
/// <c>--screenshot=</c> and <c>--cam=560,500,340,89,0</c> to compare the result with the HTML frame side by side.
/// Frame coordinates are the HTML's 320 × 200 SVG units, one unit = one metre, offset to <see cref="Origin"/>.
/// <c>--storyboard=list</c> prints the frame names.
/// </summary>
public partial class StoryboardDemo : Node
{
    [Export] public SplineDrawTool? DrawTool { get; set; }
    [Export] public SplineEditTool? EditTool { get; set; }
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
            ["junction-cross"] = JunctionCross,
            ["junction-sharp"] = JunctionSharp,
            ["junction-turnout"] = JunctionTurnout,
            ["junction-canal"] = JunctionCanal,
            ["junction-curved"] = JunctionCurved,
            ["junction-stubs"] = JunctionStubs,
            ["continue"] = Continue,
            ["continue-built"] = ContinueBuilt,
            ["continue-mix"] = ContinueMix,
            ["continue-mix-draw"] = ContinueMixDraw,
            ["continue-both"] = ContinueBoth,
            ["chain"] = Chain,
            ["chain-loop"] = ChainLoop,
            ["joint-angle"] = JointAngle,
            ["joint-straight"] = JointStraight,
            ["t-into"] = TInto,
            ["continue-straight"] = ContinueStraight,
            ["junction-cluster"] = JunctionCluster,
            ["junction-squeeze"] = JunctionSqueeze,
            ["transition"] = Transition,
            ["edit-drag"] = EditDrag,
            ["edit-knob"] = EditKnob,
            ["edit-join"] = EditJoin,
            ["edit-refused"] = EditRefused,
            ["edit-radial"] = EditRadial,
            ["edit-radial-node"] = EditRadialNode,
            ["edit-smoothed"] = EditSmoothed,
            ["edit-box"] = EditBox,
            ["edit-move"] = EditMove,
            ["edit-stretch"] = () => EditStretch(0),
            ["edit-stretch-deleted"] = () => EditStretch(1),
            ["edit-stretch-move"] = () => EditStretch(2),
            ["mode-curve"] = ModeCurve,
            ["mode-curve-close"] = () => ModeCurveCircle(close: false),
            ["mode-curve-circle"] = () => ModeCurveCircle(close: true),
            ["mode-freehand"] = ModeFreehand,
            ["mode-grid"] = ModeGrid,
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
        // A node needs a junction now (two straight streets end to end are one road): a short stub makes it a T.
        Build("street", (200, 30), (200, 180));
        Build("street", (200, 105), (250, 105));
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

    // --- Junctions ---

    /// <summary>A street drawn across an avenue: both split, a 4-way with curbs at the street's corner radius.</summary>
    private void JunctionCross()
    {
        Use("street");
        Build("avenue", (20, 110), (300, 110));
        Click((160, 20));
        Click((160, 190));
        DrawTool!.FinishForTest();
        Hover((250, 40));
    }

    /// <summary>Junctions on curves: two curving streets crossing a curved avenue.</summary>
    private void JunctionCurved()
    {
        Use("street");
        BuildCurve("avenue", 120, (10, 170), (160, 30), (310, 170));
        BuildCurve("street", 40, (40, 20), (110, 110), (70, 195));
        BuildCurve("street", 40, (190, 195), (215, 110), (300, 60));
        Hover((300, 20));
    }

    /// <summary>Short stub branches off a street, and a 4-way where a street leaves a bend (the user's screenshot).</summary>
    private void JunctionStubs()
    {
        Use("avenue");
        BuildCurve("avenue", 60, (-60, 80), (240, 105), (330, -40));
        Build("avenue", (0, 190), (30, 87.5f));
        Build("avenue", (120, -10), (30, 87.5f));
        Build("avenue", (110, 170), (110, 94.17f));
        Hover((300, 20));
    }

    /// <summary>The user's play-test (2026-10-02): an avenue Y with streets packed round it (short edges between close
    /// junctions, a 5-arm node, a street ending on the avenue's side) and an avenue running on into a street.</summary>
    private void JunctionCluster()
    {
        Use("street");
        Build("avenue", (35, 340), (77, 161));
        Build("avenue", (77, 161), (14, 0));
        Build("avenue", (77, 161), (105, 0));
        Build("street", (55, 255), (470, 80));
        Build("street", (77, 161), (143, 340));
        Build("street", (77, 161), (299, 309));
        Build("street", (340, 36), (414, 308));
        Build("avenue", (340, 36), (326, -20));
        Hover((440, 330));
    }

    /// <summary>Short streets meeting an avenue at 30° and 25°: too sharp and short for even a sharp corner, so both
    /// arms are cut back as far as they can and joined straight (the street used to run on across the avenue).</summary>
    private void JunctionSqueeze()
    {
        Use("street");
        Build("avenue", (20, 100), (300, 100));
        var d = SplineMath.Direction(-30f * MathF.PI / 180f) * 30f;
        Build("street", (100, 100), (100 + d.X, 100 + d.Y));
        var e = new NumVector2(220, 100) + SplineMath.Direction(-155f * MathF.PI / 180f) * 60f;
        Build("street", (220, 100), (e.X, e.Y));
        var x = new NumVector2(0.42f, -0.906f);
        Build("street", (e.X - x.X * 10, e.Y - x.Y * 10), (e.X + x.X * 25, e.Y + x.Y * 25));
        Hover((300, 180));
    }

    /// <summary>An avenue running on into a street, straight and at a bend: the avenue tapers down to the street.</summary>
    private void Transition()
    {
        Use("street");
        Build("avenue", (20, 60), (140, 60));
        Build("street", (140, 60), (300, 60));
        Build("avenue", (20, 160), (140, 160));
        Build("street", (140, 160), (260, 110));
        Hover((300, 180));
    }

    /// <summary>A branch at 22° off a street (min 30°): amber, with the fix in the tag.</summary>
    private void JunctionSharp()
    {
        Use("street");
        Build("street", (20, 150), (300, 150));
        var b = new NumVector2(120, 150) + SplineMath.Direction(-22f * MathF.PI / 180f) * 170f;
        Click((120, 150));
        Hover((b.X, b.Y));
    }

    /// <summary>A square branch off a rail: red, and the legal turnout offered as a ghost.</summary>
    private void JunctionTurnout()
    {
        Use("rail");
        Build("rail", (-200, 150), (800, 150));
        Click((140, 150));
        Hover((140, 40));
    }

    /// <summary>A canal drawn across a street: they don't connect, so it's red.</summary>
    private void JunctionCanal()
    {
        Use("canal");
        Build("street", (20, 100), (300, 100));
        Click((160, 20));
        Hover((160, 190));
    }

    // --- Continuing a dead end (docs/dead-end-joins.html) ---

    private void Continue()
    {
        Use("street");
        Build("street", (40, 190), (220, 190));
        Click((220, 190));
        Hover((220, 40));
    }

    private void ContinueBuilt()
    {
        Use("street");
        Build("street", (40, 190), (220, 190));
        Click((220, 190));
        Click((220, 40));
        DrawTool!.FinishForTest();
        Hover((300, 120));
    }

    private void ContinueBoth()
    {
        Use("street");
        Build("street", (40, 190), (160, 190));
        Build("street", (300, 30), (300, 110));
        Click((160, 190));
        Click((300, 190));
        Hover((300, 110));
    }

    // --- A draw chain: each click builds its leg, only the next one is a preview (docs/draw-chain.html) ---

    private void Chain()
    {
        Use("street");
        Click((30, 200));
        Click((170, 200));
        Click((250, 90));
        Hover((370, 130));
    }

    private void ChainLoop()
    {
        Use("street");
        Click((40, 190));
        Click((300, 190));
        Click((300, 60));
        Click((150, 60));
        Hover((150, 240));
    }

    private void ContinueMix()
    {
        Use("avenue");
        Build("avenue", (40, 190), (220, 190));
        Use("street");
        Click((220, 190));
        Click((220, 40));
        DrawTool!.FinishForTest();
        Hover((300, 120));
    }

    /// <summary>A street leg being drawn on from an avenue's dead end: the avenue stays solid up to where the joint's
    /// corner starts, and the street ghost carries the corner on.</summary>
    private void ContinueMixDraw()
    {
        Use("avenue");
        Build("avenue", (40, 190), (220, 190));
        Use("street");
        Click((220, 190));
        Hover((280, 60));
    }

    // --- Joint angle arcs (docs/joint-angle-arcs.html) ---

    /// <summary>A street drawn on from an avenue's dead end, at an angle: arms along both roads and the arc.</summary>
    private void JointAngle()
    {
        Use("street");
        Build("avenue", (20, 150), (170, 150));
        Click((170, 150));
        var c = new NumVector2(170, 150) + SplineMath.Direction(-35f * MathF.PI / 180f) * 150f;
        Hover((c.X, c.Y));
    }

    /// <summary>The same, straight on: arms and the 180° rectangle on the line.</summary>
    private void JointStraight()
    {
        Use("street");
        Build("avenue", (20, 150), (170, 150));
        Click((170, 150));
        Hover((300, 150));
    }

    /// <summary>A leg drawn from open ground onto a street's side: the arc at the end, as at a branch's start.</summary>
    private void TInto()
    {
        Use("street");
        Build("street", (10, 160), (350, 160));
        Click((90, 30));
        Hover((200, 160.5f));
    }

    /// <summary>A street continued straight on: the continued joint's 180° mark.</summary>
    private void ContinueStraight()
    {
        Use("street");
        Build("street", (20, 150), (170, 150));
        Click((170, 150));
        Hover((300, 150));
    }

    // --- Editing ---

    /// <summary>Edit · a stretch of the Curve-mode circle clicked (the one from its east joint to the next arc's
    /// middle): <paramref name="then"/> 0 selected, 1 deleted, 2 held moved out by 30 m.</summary>
    private void EditStretch(int then)
    {
        ModeCurveCircle(close: true);
        DrawTool!.ForcedPlanCursor = null;
        Testbed!.SetTool(SplineTool.Edit);
        var on = P((160 + 80 * MathF.Cos(0.35f), 100 + 80 * MathF.Sin(0.35f)));
        EditTool!.ClickForTest(on);
        if (then == 1) EditTool.DeleteForTest();
        if (then == 2) EditTool.MoveSelectionForTest(on, on + new NumVector2(30, 0));
        else EditTool.ForcedPlanCursor = P((300, 20));
    }

    /// <summary>Frame "Drag a node": a street's corner point dragged, the old shape a faint outline until release.</summary>
    private void EditDrag()
    {
        BuildCurve("street", 24, (20, 160), (150, 60), (300, 150));
        Testbed!.SetTool(SplineTool.Edit);
        EditTool!.SelectForTest(P((85, 110)));
        EditTool.DragForTest(P((150, 60)), P((170, 100)));
    }

    /// <summary>Frame "Drag a radius knob": an avenue's corner knob dragged out from R 50 to R 140.</summary>
    private void EditKnob()
    {
        BuildCurve("avenue", 50, (25, 175), (160, 40), (295, 175));
        Testbed!.SetTool(SplineTool.Edit);
        EditTool!.SelectForTest(P((60, 140)));
        var knob = EditTool.Network!.Graph.Edges.Single().Alignment.Corner(1).Mid;
        // A 90° corner's arc middle sits R (√2 − 1) in from its point.
        EditTool.DragForTest(knob, P((160, 40 + 140 * (MathF.Sqrt(2) - 1))));
    }

    /// <summary>A street's dead end dragged across an avenue and let go: it's built, the crossing a new 4-way (its tag
    /// flashes), and the selection follows the street's new pieces.</summary>
    private void EditJoin()
    {
        Build("avenue", (20, 60), (300, 60));
        Build("street", (160, 100), (160, 190));
        Testbed!.SetTool(SplineTool.Edit);
        EditTool!.SelectForTest(P((160, 150)));
        EditTool.DragForTest(P((160, 100)), P((150, 20)));
        EditTool.ReleaseForTest();
        EditTool.ForcedPlanCursor = P((240, 150));
    }

    /// <summary>The same drag across a canal, which a street doesn't join: refused, it springs back with a red flash.</summary>
    private void EditRefused()
    {
        Build("canal", (20, 60), (300, 60));
        Build("street", (160, 100), (160, 190));
        Testbed!.SetTool(SplineTool.Edit);
        EditTool!.SelectForTest(P((160, 150)));
        EditTool.DragForTest(P((160, 100)), P((150, 20)));
        EditTool.ReleaseForTest();
        EditTool.ForcedPlanCursor = P((240, 150));
    }

    /// <summary>Frame "Right-click a node": the radial menu on a street's corner point, Smooth hovered (tried live:
    /// the largest radius that fits).</summary>
    private void EditRadial()
    {
        BuildCurve("street", 20, (20, 170), (160, 100), (300, 170));
        Testbed!.SetTool(SplineTool.Edit);
        EditTool!.SelectForTest(P((60, 150)));
        EditTool.MenuForTest(P((160, 100)), 0);
    }

    /// <summary>The radial menu on a kinked joint between two streets (a T's third arm deleted): Smooth rounds it into
    /// one road; Hard is greyed, a joint already is.</summary>
    private void EditRadialNode()
    {
        Build("street", (20, 60), (300, 60));
        Build("street", (160, 60), (160, 190));
        Testbed!.SetTool(SplineTool.Edit);
        EditTool!.SelectForTest(P((240, 60)));
        EditTool.Network!.Apply(g => { g.RemoveEdge(EditTool.Selected.Single()); return 0; });
        EditTool.MenuForTest(P((160, 60)), 0);
    }

    /// <summary>The same joint with Smooth chosen: built as one rounded street, one undo step.</summary>
    private void EditSmoothed()
    {
        EditRadialNode();
        EditTool!.MenuForTest(P((160, 60)), 0, choose: true);
        GD.Print($"Storyboard: edges after Smooth {EditTool.Network!.Graph.EdgeCount} (want 1)");
        EditTool.ForcedPlanCursor = P((240, 150));
    }

    /// <summary>A box dragged over an avenue with two streets off it: the streets and the avenue's middle piece are
    /// wholly inside (accent outlines), the avenue's ends aren't.</summary>
    private void EditBox()
    {
        Build("avenue", (20, 60), (300, 60));
        Build("street", (100, 60), (100, 190));
        Build("street", (220, 60), (220, 190));
        Testbed!.SetTool(SplineTool.Edit);
        EditTool!.BoxForTest(P((80, 30)), P((240, 195)), release: false);
    }

    /// <summary>One street and its junction box-selected and dragged: the street moves rigidly, the avenue stretches
    /// to follow the junction.</summary>
    private void EditMove()
    {
        Build("avenue", (20, 60), (300, 60));
        Build("street", (100, 60), (100, 190));
        Build("street", (220, 60), (220, 190));
        Testbed!.SetTool(SplineTool.Edit);
        EditTool!.BoxForTest(P((200, 40)), P((240, 195)), release: true);
        EditTool.MoveSelectionForTest(P((220, 150)), P((250, 170)));
    }

    // --- Helpers ---

    // --- Four ways to shape a curve (S6) ---

    /// <summary>Curve · three clicks: start and bend placed, the end under the cursor; the arc at the fit.</summary>
    private void ModeCurve()
    {
        Testbed!.SetMode(DrawMode.Curve);
        Use("avenue");
        Click((30, 170));
        Click((160, 20));
        Hover((295, 165));
    }

    /// <summary>Curve · a circle in four quarters (R 80): each bend held on the tangent. <paramref name="close"/> false:
    /// three built, the last bend under the cursor at the close-loop snap; true: closed on the start.</summary>
    private void ModeCurveCircle(bool close)
    {
        Testbed!.SetMode(DrawMode.Curve);
        Use("street");
        Click((160, 20));
        Click((240, 20));
        Click((240, 100));
        SnapClick((244, 180));
        Click((160, 180));
        SnapClick((80, 184));
        Click((80, 100));
        if (!close) { Hover((81, 22)); return; }
        SnapClick((82, 24));
        Click((160, 20));
    }

    /// <summary>Freehand · drag: the storyboard's wobbly S, mid-drag, fitted to a few points.</summary>
    private void ModeFreehand()
    {
        Testbed!.SetMode(DrawMode.Freehand);
        Use("street");
        var stroke = Enumerable.Range(0, 61).Select(i =>
        {
            float t = i / 60f, n = 3.5f * MathF.Sin(i * 1.9f) + 2.2f * MathF.Sin(i * 0.73f);
            return P((25 + t * 275, 160 - 120 * t + 38 * MathF.Sin(t * MathF.PI * 1.6f) + n));
        }).ToList();
        DrawTool!.StrokeForTest(stroke, build: false);
    }

    /// <summary>Grid · three clicks: corner and width placed along a slightly turned first edge, the depth under the
    /// cursor: 3 × 2 blocks of 8 × 8 lots.</summary>
    private void ModeGrid()
    {
        Testbed!.SetMode(DrawMode.Grid);
        Use("street");
        var ux = NumVector2.Normalize(new NumVector2(10, 1.3f));
        var uy = new NumVector2(-ux.Y, ux.X);
        (float, float) At(float a, float b) => (10 + ux.X * a + uy.X * b, 20 + ux.Y * a + uy.Y * b);
        Click(At(0, 0));
        Click(At(228, 0));
        Hover(At(200, 150));
    }

    private static NumVector2 P((float X, float Y) svg) => Origin + new NumVector2(svg.X, svg.Y);

    private SplineProfile Profile(string id) =>
        Testbed!.Profiles.FirstOrDefault(p => p.Id == id) ?? throw new InvalidOperationException($"no profile {id}");

    private void Use(string id) => Testbed!.SelectProfile(Profile(id));

    /// <summary>A built straight spline of <paramref name="id"/>'s profile.</summary>
    private void Build(string id, (float, float) a, (float, float) b) =>
        DrawTool!.AddBuiltForTest(Profile(id), new Alignment(new[] { new Pi(P(a)), new Pi(P(b)) }));

    /// <summary>A built spline through <paramref name="pts"/>, every corner at <paramref name="radius"/>.</summary>
    private void BuildCurve(string id, float radius, params (float, float)[] pts) =>
        DrawTool!.AddBuiltForTest(Profile(id), new Alignment(pts.Select((p, i) =>
            new Pi(P(p), i == 0 || i == pts.Length - 1 ? 0 : radius)).ToArray()));

    private void Click((float, float) at, bool hard = false)
    {
        DrawTool!.ForcedPlanCursor = P(at);
        DrawTool.PlaceForTest(hard);
    }

    private void SnapClick((float, float) at)
    {
        DrawTool!.ForcedPlanCursor = P(at);
        DrawTool.SnapClickForTest();
    }

    private void Hover((float, float) at) => DrawTool!.ForcedPlanCursor = P(at);
}
