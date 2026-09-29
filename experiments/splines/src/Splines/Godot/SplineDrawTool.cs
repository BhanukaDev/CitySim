using System;
using System.Collections.Generic;
using System.Linq;
using CitySim.CameraSystem;
using CitySim.TerrainSystem;
using Godot;
using NumVector2 = System.Numerics.Vector2;
using NumVector3 = System.Numerics.Vector3;

namespace CitySim.Splines.Godot;

/// <summary>Modifier keys a scripted frame can hold down (<see cref="SplineDrawTool.ForcedModifiers"/>).</summary>
[Flags]
public enum DrawModifiers { None = 0, Ctrl = 1, Shift = 2, Alt = 4, Space = 8 }

/// <summary>
/// The Draw-mode tool (DESIGN.md → Draw tool, mode 1 of 4; ROADMAP.md S2-S4): click to place PIs on the terrain,
/// with auto-rounded corners, Alt hard corner, Shift+wheel/<c>[</c>/<c>]</c> radius of the live corner, RMB/Ctrl+Z
/// undo, Esc/RMB-empty cancel, double-click/Enter finish, and snapping to nodes, edges, angles and guides (S3, via
/// <see cref="SnapEngine"/>), with the storyboard's feedback drawn by <see cref="SplineOverlay"/>.
/// S4: every frame the draw is tried on a copy of the <see cref="SplineNetwork"/>'s graph, so the preview shows the
/// junctions it would make and its issues (amber builds, red is refused unless Anarchy, Ctrl+A). A square branch off
/// a turnout profile offers the legal turnout as a ghost, which a click takes. With no draw in progress, Ctrl+Z/Y
/// undo and redo on the graph, and Delete removes the edge under the cursor. No-ops unless
/// <see cref="SplinesTestbed.Mode"/> is <see cref="DrawMode.Draw"/>.
/// </summary>
public partial class SplineDrawTool : Node
{
    private const float RadiusWheelFactor = 1.1f;
    private const float RadiusKeyFactor = 1.15f;
    private const float CatchPixels = 8f;
    private const double FlashSeconds = 3.0;

    [Export] public Terrain? Terrain { get; set; }
    [Export] public CityCamera? CityCamera { get; set; }
    [Export] public SplinesTestbed? Testbed { get; set; }
    [Export] public SplineNetwork? Network { get; set; }

    /// <summary>The draw tried on a copy of the graph: what it made, and the issues it brings (not ones already there).</summary>
    private sealed record Trial(SplineGraph Graph, AddResult Result, List<Issue> Issues)
    {
        public Severity? Worst => Validation.Worst(Issues);
        public Issue? FirstInvalid => Issues.FirstOrDefault(i => i.Severity == Severity.Invalid);
    }

    private readonly DrawSession _session = new();
    private readonly List<(FlashTag Tag, double Until)> _flashes = new();
    private IGround? _ground;
    private RibbonRenderer? _renderer;
    private SplineOverlay? _overlay;
    private SplineIssueList? _issueList;
    private SplineProfile? _sessionProfile;
    private SnapResult? _snap;
    private NumVector2? _startHeading;
    private double _hardHintUntil;
    private Trial? _trial;
    private Alignment? _suggestion;
    private int? _deleteTarget;

    /// <summary>The current ground hit, world space. Null off the terrain or over UI.</summary>
    public NumVector3? Cursor { get; private set; }
    /// <summary>Scripted-demo override: a plan-space (map metres) position that replaces the mouse raycast.</summary>
    public NumVector2? ForcedPlanCursor { get; set; }
    /// <summary>Scripted-demo override: modifier keys treated as held (added to the real ones).</summary>
    public DrawModifiers ForcedModifiers { get; set; }
    public int BuiltCount => Network?.Graph.EdgeCount ?? 0;
    /// <summary>A draw is in progress (at least one point placed).</summary>
    public bool IsDrawing => !_session.IsEmpty;

    public override void _Ready()
    {
        if (Terrain is null) { GD.PushError("SplineDrawTool needs a Terrain"); return; }
        _ground = new TerrainGround(Terrain);
        _renderer = new RibbonRenderer(Terrain, _ground);
        var layer = new CanvasLayer { Name = "SplineDrawHud" };
        AddChild(layer);
        _overlay = new SplineOverlay { Project = ProjectPlan };
        layer.AddChild(_overlay);
        _issueList = new SplineIssueList();
        layer.AddChild(_issueList);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Testbed?.Mode != DrawMode.Draw) return;
        if (@event is InputEventKey { Pressed: true } key) HandleKey(key);
        else if (@event is InputEventMouseButton mb) HandleMouseButton(mb);
    }

    private void HandleKey(InputEventKey key)
    {
        bool handled = true;
        bool drawing = !_session.IsEmpty;
        if (key.Keycode == Key.Z && key.IsCommandOrControlPressed())
        {
            if (key.ShiftPressed) Redo(drawing); else Undo(drawing);
        }
        else if (key.Keycode == Key.Y && key.IsCommandOrControlPressed()) Redo(drawing);
        else if (key.Keycode == Key.A && key.IsCommandOrControlPressed()) Testbed?.SetAnarchy(!Testbed.Anarchy);
        else if (key.Keycode is Key.Delete or Key.Backspace && !drawing && _deleteTarget is { } del) Delete(del);
        else if (key.Keycode == Key.Bracketleft) AdjustRadius(1f / RadiusKeyFactor);
        else if (key.Keycode == Key.Bracketright) AdjustRadius(RadiusKeyFactor);
        else if (key.Keycode is Key.Enter or Key.KpEnter) Finish();
        else if (key.Keycode == Key.Escape) CancelSession();
        else handled = false;
        if (handled) GetViewport().SetInputAsHandled();
    }

    private void HandleMouseButton(InputEventMouseButton mb)
    {
        if (mb.Pressed && mb.ShiftPressed && mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            AdjustRadius(MathF.Pow(RadiusWheelFactor, mb.ButtonIndex == MouseButton.WheelUp ? 1f : -1f));
            GetViewport().SetInputAsHandled();
            return;
        }
        if (mb.ButtonIndex is not (MouseButton.Left or MouseButton.Right) || !mb.Pressed) return;

        if (mb.ButtonIndex == MouseButton.Right)
        {
            GetViewport().SetInputAsHandled();
            RemoveLastOrCancel();
            return;
        }
        if (mb.DoubleClick)
        {
            GetViewport().SetInputAsHandled();
            Finish();
            return;
        }
        if (Cursor is { } hit)
        {
            GetViewport().SetInputAsHandled();
            if (_suggestion is { } turnout) TakeSuggestion(turnout);
            else Place(_snap?.Position ?? PlanOf(hit), hard: mb.AltPressed);
        }
    }

    public override void _Process(double delta)
    {
        if (Testbed is null || _ground is null || _renderer is null || _overlay is null) return;
        if (!_session.IsEmpty && Testbed.Profile != _sessionProfile)
        {
            GD.Print("Splines: profile changed mid-draw, cancelling");
            CancelSession();
        }

        UpdateCursor();

        if (Testbed.Mode != DrawMode.Draw || Testbed.Profile is not { } profile || Cursor is not { } cursor || Network is null)
        {
            Network?.Hide(Array.Empty<int>());
            _renderer.SetPreview(null, 0);
            _renderer.SetGhost(null, 0);
            _overlay.Show(null);
            _snap = null;
            _trial = null;
            _suggestion = null;
            _deleteTarget = null;
            return;
        }

        var rules = profile.ToRules();
        var mods = Modifiers();
        _snap = SnapEngine.Evaluate(BuildSnapQuery(PlanOf(cursor), cursor, rules, mods));
        bool continues = _snap.Kind == SnapKind.Node && ContinuesAt(_snap.Position, rules);
        if (continues) _snap = _snap with { Tag = $"continue · {rules.Id}" };

        Alignment? preview = null, shown = null;
        bool leadIn = false, leadOut = false;
        _trial = null;
        _suggestion = null;
        _deleteTarget = null;
        if (!_session.IsEmpty)
        {
            var drawn = _session.BuildPreview(_snap.Position);
            _trial = Try(drawn, rules);
            _suggestion = TurnoutSuggestion(_trial, rules);
            // Continuing a dead end: the ghost is the whole road it becomes, the old edge is hidden meanwhile, and the
            // overlay gets the old road's last leg as the previous leg (so the joint has its angle and radius pills).
            var continued = _trial?.Result.Continued ?? (IReadOnlyList<int>)Array.Empty<int>();
            Network.Hide(continued);
            shown = continued.Count > 0 ? _trial!.Result.Alignment : drawn;
            (preview, leadIn, leadOut) = continued.Count > 0 ? WithLeads(drawn, rules) : (drawn, false, false);
            _renderer.SetPreview(shown, profile.Width, _trial?.Worst);
        }
        else
        {
            Network.Hide(Array.Empty<int>());
            _renderer.SetPreview(null, 0);
            _deleteTarget = EdgeUnder(PlanOf(cursor));
        }
        _renderer.SetGhost(_suggestion, profile.Width);
        _issueList?.Show(_trial?.Issues ?? new List<Issue>(), Network.Issues, Testbed.Anarchy);

        double now = Time.GetTicksMsec() / 1000.0;
        _flashes.RemoveAll(f => now > f.Until);
        bool ctrl = mods.HasFlag(DrawModifiers.Ctrl) && !mods.HasFlag(DrawModifiers.Space);
        var graph = Network.Graph;
        _overlay.Show(new OverlayFrame
        {
            SessionPis = _session.Pis,
            Preview = preview,
            LeadIn = leadIn,
            LeadOut = leadOut,
            ClickFinishes = continues && !_session.IsEmpty,
            Snap = _snap,
            StartHeading = _session.IsEmpty ? null : _startHeading,
            Rules = rules,
            BuiltEnds = graph.Nodes.Select(n => n.Position).ToList(),
            Mouse = MouseScreen(),
            CtrlStepDegrees = ctrl && !_session.IsEmpty ? (mods.HasFlag(DrawModifiers.Shift) ? 5f : 15f) : 0f,
            HardRefused = now < _hardHintUntil,
            Flashes = _flashes.Select(f => f.Tag).ToList(),
            Junctions = _trial is { } t ? JunctionMarks(t) : Array.Empty<JunctionMark>(),
            Issues = _trial?.Issues ?? (IReadOnlyList<Issue>)Array.Empty<Issue>(),
            Worst = _trial?.Worst,
            SharpAngles = _trial is { } t2 ? SharpAngles(t2) : Array.Empty<(NumVector2, NumVector2, NumVector2)>(),
            Suggestion = _suggestion,
            SuggestionLabel = _suggestion is null ? null
                : $"turnout {Junctions.TurnoutRatio(rules.TurnoutMaxAngle)} · R {rules.MinRadius:0} m",
            DeleteTarget = _deleteTarget is { } d ? graph.Edge(d).Alignment.Curve : null,
            DeleteWidth = _deleteTarget is { } dw ? graph.Edge(dw).Rules.Width : 0,
        });
    }

    // --- Continuing a dead end ---

    /// <summary>Whether a point is a dead end this draw would continue: not the draw's own start edge, which it
    /// closes into a loop instead.</summary>
    private bool ContinuesAt(NumVector2 p, ProfileRules rules)
    {
        if (Network?.Graph.DeadEndAt(p, rules) is not { } end) return false;
        return _session.IsEmpty || Network.Graph.DeadEndAt(_session.Pis[0].Position, rules)?.EdgeId != end.EdgeId;
    }

    /// <summary>
    /// The drawn alignment with the old road's leg added before a continued start and after a continued end, for the
    /// overlay. The leg runs to the old road's next point, or halfway to it when that's a corner: a middle leg only
    /// gives each corner half of it, so the joint's pill then clamps as the built road will.
    /// </summary>
    private (Alignment, bool LeadIn, bool LeadOut) WithLeads(Alignment drawn, ProfileRules rules)
    {
        var g = Network!.Graph;
        var pis = drawn.Pis.ToList();
        var start = g.DeadEndAt(pis[0].Position, rules);
        bool leadIn = start is not null, leadOut = false;
        if (start is { } s) pis.Insert(0, new Pi(Lead(g.Edge(s.EdgeId).Alignment, s.AtStart)));
        if (g.DeadEndAt(pis[^1].Position, rules) is { } e && e.EdgeId != start?.EdgeId)
        {
            pis.Add(new Pi(Lead(g.Edge(e.EdgeId).Alignment, e.AtStart)));
            leadOut = true;
        }
        return (new Alignment(pis), leadIn, leadOut);

        static NumVector2 Lead(Alignment a, bool atStart)
        {
            var pis = a.Pis;
            var (end, next) = atStart ? (pis[0].Position, pis[1].Position) : (pis[^1].Position, pis[^2].Position);
            return pis.Count == 2 ? next : (end + next) / 2;
        }
    }

    // --- The draw tried on the graph ---

    /// <summary>Adds the alignment to a copy of the graph and validates what it touches. Issues the graph already had
    /// (say an edge built red with Anarchy) are left out, so they neither show again nor block this draw.</summary>
    private Trial? Try(Alignment alignment, ProfileRules rules)
    {
        if (Network is null || alignment.Curve.Length < SplineGraph.NodeTolerance) return null;
        var g = Network.Graph.Clone();
        var result = g.AddSpline(alignment, rules);
        var edges = result.Edges.Concat(result.Nodes.SelectMany(n => g.Node(n).Edges)).Distinct();
        var issues = Validation.Check(g, edges, result.Nodes)
            .Where(i => !Network.Issues.Any(old => old.Code == i.Code && old.Message == i.Message && NumVector2.Distance(old.Where, i.Where) < 1f))
            .ToList();
        return new Trial(g, result, issues);
    }

    private static List<JunctionMark> JunctionMarks(Trial t)
    {
        var marks = new List<JunctionMark>();
        foreach (int n in t.Result.Nodes)
        {
            if (Junctions.Label(t.Graph, n) is not { } label) continue;
            float width = t.Graph.Node(n).Edges.Max(e => t.Graph.Edge(e).Rules.Width);
            bool warn = t.Issues.Any(i => i.NodeId == n);
            marks.Add(new JunctionMark(t.Graph.Node(n).Position, label, width, warn));
        }
        return marks;
    }

    /// <summary>The two arms either side of each too-sharp junction angle, for the amber arc.</summary>
    private static List<(NumVector2, NumVector2, NumVector2)> SharpAngles(Trial t)
    {
        var result = new List<(NumVector2, NumVector2, NumVector2)>();
        foreach (var issue in t.Issues)
        {
            if (issue is not { Code: "junction-angle", NodeId: { } n }) continue;
            var sorted = Junctions.Sorted(t.Graph.Arms(n));
            int i = sorted.FindIndex(x => x.Gap == sorted.Min(y => y.Gap));
            result.Add((t.Graph.Node(n).Position, sorted[i].Arm.Direction, sorted[(i + 1) % sorted.Count].Arm.Direction));
        }
        return result;
    }

    /// <summary>For the first leg of a turnout-profile draw refused as a square branch: the nearest legal turnout,
    /// leaving along the line it starts on and curving at the profile's minimum radius to the cursor.</summary>
    private Alignment? TurnoutSuggestion(Trial? t, ProfileRules rules)
    {
        if (t is null || rules.JunctionKind != JunctionKind.Turnout || _session.Pis.Count != 1) return null;
        int start = t.Result.Nodes[0];
        if (!t.Issues.Any(i => i.Code == "turnout" && i.NodeId == start)) return null;
        var line = t.Graph.Arms(start).FirstOrDefault(a => !t.Result.Edges.Contains(a.EdgeId));
        if (line.EdgeId == 0) return null;
        return Junctions.TurnoutGhost(t.Graph.Node(start).Position, line.Direction, _snap?.Position ?? _session.Pis[0].Position, rules.MinRadius);
    }

    private void TakeSuggestion(Alignment turnout)
    {
        _session.ReplaceWith(turnout.Pis.Take(2));
        _session.Place(turnout.Pis[^1].Position, hard: false);
    }

    /// <summary>The built edge whose corridor is under the cursor (for Delete).</summary>
    private int? EdgeUnder(NumVector2 p)
    {
        if (Network is null) return null;
        int? best = null;
        float bestDist = float.PositiveInfinity;
        foreach (var e in Network.Graph.Edges)
        {
            float d = NumVector2.Distance(e.Alignment.Curve.ClosestPoint(p).Position, p);
            if (d > e.Rules.Width / 2 + 1f || d >= bestDist) continue;
            bestDist = d;
            best = e.Id;
        }
        return best;
    }

    private DrawModifiers Modifiers()
    {
        var m = ForcedModifiers;
        if (Input.IsKeyPressed(Key.Ctrl)) m |= DrawModifiers.Ctrl;
        if (Input.IsKeyPressed(Key.Shift)) m |= DrawModifiers.Shift;
        if (Input.IsKeyPressed(Key.Alt)) m |= DrawModifiers.Alt;
        if (Input.IsKeyPressed(Key.Space)) m |= DrawModifiers.Space;
        return m;
    }

    /// <summary>Every currently-held-input flag and existing alignment <see cref="SnapEngine"/> needs, plus the
    /// screen-pixel catch distance converted to plan units for this frame's cursor depth.</summary>
    private SnapQuery BuildSnapQuery(NumVector2 rawPlan, NumVector3 worldCursor, ProfileRules rules, DrawModifiers mods)
    {
        bool ctrl = mods.HasFlag(DrawModifiers.Ctrl);
        return new SnapQuery
        {
            Cursor = rawPlan,
            SessionPis = _session.Pis,
            StartHeading = _session.IsEmpty ? null : _startHeading,
            Candidates = Candidates(),
            Rules = rules,
            EnabledProviders = Testbed?.EnabledSnaps ?? SnapProviders.All,
            CatchDistance = PixelsToPlanUnits(CatchPixels, worldCursor),
            CtrlSteps = ctrl,
            FineSteps = ctrl && mods.HasFlag(DrawModifiers.Shift),
            Disabled = mods.HasFlag(DrawModifiers.Space),
        };
    }

    /// <summary>Every built edge as a snap source; extension guides only leave dead ends.</summary>
    private List<SnapCandidate> Candidates()
    {
        if (Network is null) return new List<SnapCandidate>();
        var g = Network.Graph;
        return g.Edges.Select(e => new SnapCandidate(e.Alignment, e.Rules.Width,
            OpenStart: g.Node(e.Start).Edges.Count == 1, OpenEnd: g.Node(e.End).Edges.Count == 1)).ToList();
    }

    /// <summary>Converts a screen-pixel distance to plan units at <paramref name="worldHit"/>'s depth, so the catch
    /// distance feels the same at every zoom (DESIGN.md → Snapping and guides).</summary>
    private float PixelsToPlanUnits(float pixels, NumVector3 worldHit)
    {
        var world = new Vector3(worldHit.X, worldHit.Y, worldHit.Z);
        if (CityCamera?.Camera is not { } cam || cam.IsPositionBehind(world)) return pixels;
        var origin = cam.UnprojectPosition(world);
        var offset = cam.UnprojectPosition(world + new Vector3(1f, 0, 0));
        float pxPerMeter = origin.DistanceTo(offset);
        return pxPerMeter > 1e-3f ? pixels / pxPerMeter : pixels;
    }

    /// <summary>A plan point draped on the ground, on screen (null when behind the camera) — for the overlay.</summary>
    private Vector2? ProjectPlan(NumVector2 plan)
    {
        if (Terrain is null || _ground is null || CityCamera?.Camera is not { } cam) return null;
        var o = Terrain.GlobalPosition;
        var world = new Vector3(o.X + plan.X, _ground.GetHeight(plan), o.Z + plan.Y);
        return cam.IsPositionBehind(world) ? null : cam.UnprojectPosition(world);
    }

    /// <summary>The mouse on screen, or where the forced cursor projects to in a scripted frame.</summary>
    private Vector2 MouseScreen() =>
        ForcedPlanCursor is { } forced && ProjectPlan(forced) is { } p ? p : GetViewport().GetMousePosition();

    private void UpdateCursor()
    {
        if (Terrain is null || _ground is null) { Cursor = null; return; }
        if (ForcedPlanCursor is { } forced)
        {
            var world = Terrain.MapToWorld(forced.X, forced.Y, _ground.GetHeight(forced));
            Cursor = new NumVector3(world.X, world.Y, world.Z);
            return;
        }
        Cursor = null;
        if (CityCamera?.Camera is not { } cam) return;
        var viewport = GetViewport();
        if (viewport.GuiGetHoveredControl() is not null) return;
        var mouse = viewport.GetMousePosition();
        var origin = ToNumerics(cam.ProjectRayOrigin(mouse));
        var dir = ToNumerics(cam.ProjectRayNormal(mouse));
        if (_ground.Raycast(origin, dir, out var hit)) Cursor = hit;
    }

    /// <summary>Places a PI. A later point on a dead end the draw continues also finishes it: the road is complete
    /// once it joins the other one (a refused finish leaves the draw open, the point placed).</summary>
    private void Place(NumVector2 position, bool hard)
    {
        if (Testbed?.Profile is not { } profile) return;
        bool finishes = !_session.IsEmpty && ContinuesAt(position, profile.ToRules());
        if (_session.IsEmpty)
        {
            _sessionProfile = profile;
            _session.Reset(profile.DefaultRadius);
            // Starting on an edge makes it the soft-angle reference for the whole draw ("∡ 90° · square to edge").
            _startHeading = _snap is { Kind: SnapKind.Node or SnapKind.Edge or SnapKind.PerpendicularFoot } s &&
                            NumVector2.Distance(s.Position, position) < 1e-3f ? s.EdgeTangent : null;
            _session.StartIsCorner = Network?.Graph.DeadEndAt(position, profile.ToRules()) is not null;
        }
        bool allowHard = hard && profile.AllowHardCorners;
        if (hard && !allowHard) _hardHintUntil = Time.GetTicksMsec() / 1000.0 + 1.2;
        _session.Place(position, allowHard);
        if (finishes) Finish();
    }

    private void AdjustRadius(float factor)
    {
        if (Testbed?.Profile is not { } profile) return;
        // Anarchy lifts the profile's minimum (the corner then shows red but builds).
        _session.SetPendingRadius(_session.PendingRadius * factor, Testbed.Anarchy ? 1f : profile.MinRadius);
    }

    /// <summary>Builds the draw as one undo step, unless it has an Invalid issue and Anarchy is off: then it flashes
    /// why in red and the draw stays open to fix.</summary>
    private void Finish()
    {
        if (Testbed?.Profile is not { } profile || Network is null || _session.Pis.Count < 2) return;
        var alignment = _session.Finish();
        var rules = profile.ToRules();
        double until = Time.GetTicksMsec() / 1000.0 + FlashSeconds;
        if (Try(alignment, rules) is { FirstInvalid: { } bad } && !Testbed.Anarchy)
        {
            _flashes.Add((new FlashTag(alignment.Pis[^1].Position, $"Can't build: {bad.Message}", Bad: true), until));
            return;
        }
        Network.RegisterProfile(profile);
        var result = Network.Apply(g => g.AddSpline(alignment, rules));
        _flashes.Add((new FlashTag(alignment.Pis[^1].Position, $"Total {alignment.Length:0} m"), until));
        foreach (int n in result.Nodes)
            if (Junctions.Label(Network.Graph, n) is { } label)
                _flashes.Add((new FlashTag(Network.Graph.Node(n).Position, label), until));
        CancelSession();
    }

    private void Undo(bool drawing)
    {
        if (drawing) _session.Undo();
        else Network?.Undo();
    }

    private void Redo(bool drawing)
    {
        if (drawing) _session.Redo();
        else Network?.Redo();
    }

    private void Delete(int edgeId)
    {
        if (Network is null || !Network.Graph.HasEdge(edgeId)) return;
        Network.Apply(g => g.RemoveEdge(edgeId));
    }

    private void RemoveLastOrCancel()
    {
        if (!_session.Undo()) CancelSession();
    }

    private void CancelSession()
    {
        _session.Reset(Testbed?.Profile?.DefaultRadius ?? 0f);
        _sessionProfile = Testbed?.Profile;
        _startHeading = null;
        _renderer?.SetPreview(null, 0);
    }

    private NumVector2 PlanOf(NumVector3 worldHit)
    {
        var map = Terrain!.WorldToMap(new Vector3(worldHit.X, worldHit.Y, worldHit.Z));
        return new NumVector2(map.X, map.Y);
    }

    private static NumVector3 ToNumerics(Vector3 v) => new(v.X, v.Y, v.Z);

    // --- Direct-API test hooks for --demo-draw and --storyboard (no simulated InputEvents) ---

    /// <summary>Places a PI at <see cref="ForcedPlanCursor"/> exactly (no snapping, so golden coordinates stay exact),
    /// but still records the start edge's heading if that point is on a built spline.</summary>
    public void PlaceForTest(bool hard)
    {
        UpdateCursor(); // ForcedPlanCursor was just set; Cursor otherwise only refreshes in _Process
        if (Cursor is not { } hit || Testbed?.Profile is not { } profile) return;
        var plan = PlanOf(hit);
        if (_session.IsEmpty)
            _snap = SnapEngine.Evaluate(new SnapQuery
            {
                Cursor = plan, Rules = profile.ToRules(), CatchDistance = 0.5f,
                EnabledProviders = SnapProviders.Node | SnapProviders.Edge,
                Candidates = Candidates(),
            }) with { Position = plan };
        Place(plan, hard);
    }

    public void FinishForTest() => Finish();

    /// <summary>Shift+wheel / <c>[</c> <c>]</c>: scales the live corner's radius.</summary>
    public void AdjustRadiusForTest(float factor) => AdjustRadius(factor);

    /// <summary>Adds an already-made alignment as built (storyboard scenes with exact radii), junctions and all.</summary>
    public void AddBuiltForTest(SplineProfile profile, Alignment alignment)
    {
        if (Network is null) return;
        Network.RegisterProfile(profile);
        Network.Apply(g => g.AddSpline(alignment, profile.ToRules()));
    }

    /// <summary>LMB while a turnout is offered: takes it.</summary>
    public bool TakeSuggestionForTest()
    {
        if (_suggestion is not { } s) return false;
        TakeSuggestion(s);
        return true;
    }
}
