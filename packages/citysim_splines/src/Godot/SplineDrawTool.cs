using System;
using System.Collections.Generic;
using System.Linq;
using CitySim.CameraSystem;
using CitySim.TerrainSystem;
using Godot;
using NumVector2 = System.Numerics.Vector2;
using NumVector3 = System.Numerics.Vector3;

namespace CitySim.Splines.Godot;

/// <summary>
/// The Draw-mode tool (DESIGN.md → Draw tool, mode 1 of 4; ROADMAP.md S2-S4): click to place PIs on the terrain,
/// with auto-rounded corners, Alt hard corner, Shift+wheel/<c>[</c>/<c>]</c> radius of the live corner, RMB/Ctrl+Z
/// undo, Esc/RMB-empty cancel, double-click/Enter finish, and snapping to nodes, edges, angles and guides (S3, via
/// <see cref="SnapEngine"/>), with the storyboard's feedback drawn by <see cref="SplineOverlay"/>.
/// S4: every frame the draw is tried on a copy of the <see cref="SplineNetwork"/>'s graph, so the preview shows the
/// junctions it would make and its issues (amber builds, red is refused unless Anarchy, Ctrl+A). A square branch off
/// a turnout profile offers the legal turnout as a ghost, which a click takes. With no draw in progress, Ctrl+Z/Y
/// undo and redo on the graph (deleting is the Edit tool's: select, then Delete).
/// S6 adds the other modes, each in its own file: Curve (start, bend, end; <c>SplineDrawTool.Curve.cs</c>), Freehand
/// (a dragged stroke; <c>.Freehand.cs</c>) and Grid (corner, width, depth; <c>.Grid.cs</c>). No-ops unless the Draw
/// tool is on (<see cref="ISplineToolHost.Tool"/>); leaving it or changing mode ends the chain.
/// </summary>
public partial class SplineDrawTool : Node
{
    private const float RadiusWheelFactor = 1.1f;
    private const float RadiusKeyFactor = 1.15f;
    private const float CatchPixels = 8f;
    private const double FlashSeconds = 3.0;

    [Export] public Terrain? Terrain { get; set; }
    [Export] public CityCamera? CityCamera { get; set; }
    /// <summary>The consumer's UI node; it must implement <see cref="ISplineToolHost"/>.</summary>
    [Export] public Node? HostNode { get; set; }
    private ISplineToolHost? Host => HostNode as ISplineToolHost;
    [Export] public SplineNetwork? Network { get; set; }

    /// <summary>The draw tried on a copy of the graph: what it made, and the issues it brings (not ones already there).</summary>
    private sealed record Trial(SplineGraph Graph, AddResult Result, List<Issue> Issues)
    {
        public Severity? Worst => Validation.Worst(Issues);
        public Issue? FirstInvalid => Issues.FirstOrDefault(i => i.Severity == Severity.Invalid);
    }

    private readonly DrawSession _session = new();
    private readonly SplineToolView _view;
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

    /// <summary>The current ground hit, world space. Null off the terrain or over UI.</summary>
    public NumVector3? Cursor => _view.Cursor;
    /// <summary>Scripted-demo override: a plan-space (map metres) position that replaces the mouse raycast.</summary>
    public NumVector2? ForcedPlanCursor { get => _view.ForcedPlanCursor; set => _view.ForcedPlanCursor = value; }
    /// <summary>Scripted-demo override: modifier keys treated as held (added to the real ones).</summary>
    public DrawModifiers ForcedModifiers { get => _view.ForcedModifiers; set => _view.ForcedModifiers = value; }
    public int BuiltCount => Network?.Graph.EdgeCount ?? 0;
    /// <summary>A draw is in progress (at least one point placed).</summary>
    public bool IsDrawing => !_session.IsEmpty || _stroke is not null;
    private bool Active => Host is { Tool: SplineTool.Draw };
    private DrawMode Mode => Host?.Mode ?? DrawMode.Draw;

    public SplineDrawTool() => _view = new SplineToolView(this);

    public override void _Ready()
    {
        if (Terrain is null) { GD.PushError("SplineDrawTool needs a Terrain"); return; }
        _ground = new TerrainGround(Terrain);
        _view.Terrain = Terrain;
        _view.CityCamera = CityCamera;
        _view.Ground = _ground;
        _renderer = new RibbonRenderer(Terrain, _ground);
        var layer = new CanvasLayer { Name = "SplineDrawHud" };
        AddChild(layer);
        _overlay = new SplineOverlay { Project = _view.ProjectPlan };
        layer.AddChild(_overlay);
        _issueList = new SplineIssueList();
        layer.AddChild(_issueList);
        if (Host is not null) Host.ModeChanged += _ => EndChain();
        if (Network is not null) Network.Changed += () => (_bendShown, _bentGraph) = (null, null);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Active) return;
        if (@event is InputEventKey { Pressed: true } key) HandleKey(key);
        else if (@event is InputEventMouseButton mb)
        {
            // macOS turns Ctrl+click into a right click, which would stop the draw; here Ctrl means angle steps.
            if (mb.ButtonIndex == MouseButton.Right && mb.CtrlPressed && OS.GetName() == "macOS") mb.ButtonIndex = MouseButton.Left;
            HandleMouseButton(mb);
        }
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
        else if (key.Keycode == Key.A && key.IsCommandOrControlPressed()) Host?.SetAnarchy(!Host.Anarchy);
        else if (key.Keycode is Key.Bracketleft or Key.Bracketright && Mode == DrawMode.Grid)
            AdjustGridBlocks(key.Keycode == Key.Bracketright ? 1 : -1, across: key.ShiftPressed);
        else if (key.Keycode == Key.Bracketleft) AdjustRadius(1f / RadiusKeyFactor);
        else if (key.Keycode == Key.Bracketright) AdjustRadius(RadiusKeyFactor);
        else if (key.Keycode is Key.Enter or Key.KpEnter) Finish();
        else if (key.Keycode == Key.Escape) EndChain();
        else handled = false;
        if (handled) GetViewport().SetInputAsHandled();
    }

    private void HandleMouseButton(InputEventMouseButton mb)
    {
        if (mb.Pressed && mb.ShiftPressed && mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            int dir = mb.ButtonIndex == MouseButton.WheelUp ? 1 : -1;
            if (Mode == DrawMode.Grid) AdjustGridBlocks(dir, across: mb.CtrlPressed);
            else AdjustRadius(MathF.Pow(RadiusWheelFactor, dir));
            GetViewport().SetInputAsHandled();
            return;
        }
        if (Mode == DrawMode.Freehand) { HandleFreehandButton(mb); return; }
        if (mb.ButtonIndex is not (MouseButton.Left or MouseButton.Right) || !mb.Pressed) return;

        if (mb.ButtonIndex == MouseButton.Right)
        {
            GetViewport().SetInputAsHandled();
            EndChain();
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
            var at = _snap?.Position ?? _view.PlanOf(hit);
            if (Mode == DrawMode.Grid) GridClick(at, _view.PlanOf(hit));
            else if (_suggestion is { } turnout) TakeSuggestion(turnout);
            else Click(at, hard: mb.AltPressed);
        }
    }

    public override void _Process(double delta)
    {
        if (Host is null || _ground is null || _renderer is null || _overlay is null) return;
        if (!_session.IsEmpty && Host.Profile != _sessionProfile)
        {
            GD.Print("Splines: profile changed mid-draw, cancelling");
            CancelSession();
        }

        _view.UpdateCursor();

        if (!Active && IsDrawing) EndChain();
        if (!Active || Host.Profile is not { } profile || Cursor is not { } cursor || Network is null)
        {
            ClearGridTrial();
            ClearBends();
            Network?.Hide(Array.Empty<int>());
            _renderer.SetPreview(null, 0);
            _renderer.SetGhost(null, 0);
            _overlay.Show(null);
            _snap = null;
            _trial = null;
            _suggestion = null;
            return;
        }

        var rules = profile.ToRules();
        var mods = _view.Modifiers();
        var query = BuildSnapQuery(_view.PlanOf(cursor), cursor, rules, mods);
        _snap = SnapEngine.Evaluate(query);
        if (Mode == DrawMode.Freehand) { ProcessFreehand(profile, rules, _view.PlanOf(cursor)); return; }
        if (Mode == DrawMode.Grid && _gridAlongEnd is not null) { ClearBends(); ProcessGrid(profile, rules, _view.PlanOf(cursor)); return; }
        ClearGridTrial();
        TagLoopClose(rules);
        bool continues = _snap.Kind == SnapKind.Node && ContinuesAt(_snap.Position, rules);
        if (continues) _snap = _snap with { Tag = ContinueTag(_snap.Position, rules) };
        bool clickFinishes = !_session.IsEmpty && Mode != DrawMode.Grid && (Mode != DrawMode.Curve || _session.Bend is not null) && _snap.Kind == SnapKind.Node && Network.Graph.DeadEndAt(_snap.Position, rules) is not null;

        Alignment? preview = null;
        bool leadIn = false, leadOut = false;
        _trial = null;
        _suggestion = null;
        if (!_session.IsEmpty)
        {
            var drawn = Mode switch
            {
                DrawMode.Grid => GridWidthPreview(rules),
                _ => _session.BuildPreview(_snap.Position, BendRadius(rules, continues)),
            };
            _trial = Try(drawn, rules, Mode == DrawMode.Grid ? Ends.None : Ends.Both);
            if (Mode == DrawMode.Draw) _suggestion = TurnoutSuggestion(_trial, rules);
            (preview, leadIn, leadOut) = ShowTrialPreview(drawn, rules, profile);
            ShowBends(drawn);
        }
        else
        {
            Network.Hide(Array.Empty<int>());
            _renderer.SetPreview(null, 0);
            ShowBends(null);
        }
        _renderer.SetGhost(_suggestion, profile.Width);
        _issueList?.Show(_trial?.Issues ?? new List<Issue>(), Network.Issues, Host.Anarchy);

        double now = Time.GetTicksMsec() / 1000.0;
        _flashes.RemoveAll(f => now > f.Until);
        bool ctrl = mods.HasFlag(DrawModifiers.Ctrl) && !mods.HasFlag(DrawModifiers.Space);
        var graph = Network.Graph;
        // What the leg's ends land on, for their angle arcs: a dead end they join without continuing (the old road's
        // direction), or the side of a road (its line). A perpendicular foot already has its square mark.
        NumVector2? startArm = null, endArm = null, endHeading = null;
        if (!_session.IsEmpty)
        {
            if (!leadIn) startArm = DeadEndArm(_session.Pis[0].Position);
            if (!leadOut && _snap is { Kind: SnapKind.Node or SnapKind.Edge } es)
            {
                endArm = DeadEndArm(es.Position);
                if (endArm is null) endHeading = es.EdgeTangent;
            }
        }
        _overlay.Show(new OverlayFrame
        {
            SessionPis = _session.Placed,
            Preview = preview,
            LeadIn = leadIn,
            LeadOut = leadOut,
            ClickFinishes = clickFinishes,
            Snap = _snap,
            StartHeading = _session.IsEmpty ? null : _startHeading,
            StartArm = startArm,
            EndArm = endArm,
            EndHeading = endHeading,
            Rules = rules,
            BuiltEnds = Dots(),
            Mouse = _view.MouseScreen(),
            CtrlStepDegrees = ctrl && !_session.IsEmpty ? (mods.HasFlag(DrawModifiers.Shift) ? 5f : 15f) : 0f,
            CtrlFanFrom = SnapEngine.CtrlReference(query)?.Direction ?? NumVector2.UnitX,
            HardRefused = now < _hardHintUntil,
            HeightAt = _trial is { } th ? HeightOf(th) : null,
            Flashes = _flashes.Select(f => f.Tag).ToList(),
            Junctions = _trial is { } t ? JunctionMarks(t) : Array.Empty<JunctionMark>(),
            Issues = _trial?.Issues ?? (IReadOnlyList<Issue>)Array.Empty<Issue>(),
            Worst = _trial?.Worst,
            SharpAngles = _trial is { } t2 ? SharpAngles(t2) : Array.Empty<(NumVector2, NumVector2, NumVector2)>(),
            Suggestion = _suggestion,
            SuggestionLabel = _suggestion is null ? null
                : $"turnout {Junctions.TurnoutRatio(rules.TurnoutMaxAngle)} · R {rules.MinRadius:0} m",
            PlaceLabel = PlaceLabel(),
            LiveRadiusNote = BendAtFit ? "fit" : null,
            GridLabel = Mode == DrawMode.Grid ? GridWidthLabel(rules) : null,
            Bend = BendMark(),
        });
    }

    /// <summary>What LMB does next, for the hint stack (null: Draw's own wording).</summary>
    private string? PlaceLabel() => Mode switch
    {
        DrawMode.Curve => _session.IsEmpty ? "Place start" : _session.Bend is null ? "Place bend" : "Place end",
        DrawMode.Grid => _session.IsEmpty ? "Place corner" : "Place width",
        _ => null,
    };

    /// <summary>
    /// Shows <see cref="_trial"/> (the drawn alignment tried on the graph) as the preview ribbon, and returns what the
    /// overlay draws its legs and pills along. Continuing a dead end: the old edge is hidden and the whole road it
    /// becomes is drawn instead, the unchanged old part solid; the overlay gets the old road's last leg as the previous
    /// leg (so the joint has its angle and radius pills).
    /// </summary>
    private (Alignment Preview, bool LeadIn, bool LeadOut) ShowTrialPreview(Alignment drawn, ProfileRules rules, SplineProfile profile)
    {
        var continued = _trial?.Result.Continued ?? (IReadOnlyList<int>)Array.Empty<int>();
        Network!.Hide(continued);
        // The old road it continues stays drawn as built; only the new part (from the joint's corner) is the ghost.
        // Another profile's road keeps its own edge up to the joint's corner, drawn as built in its colour.
        if (continued.Count > 0)
        {
            var kept = _trial!.Result.Kept.Select(id => _trial.Graph.Edge(id))
                .Select(e => (e.Alignment, e.Rules.Width, Network.ColorOf(e.Rules.Id))).ToList();
            _renderer!.SetPreview(_trial.Result.Alignment, profile.Width, _trial.Worst, _trial.Result.SolidUntil, _trial.Result.SolidFrom, Network.ColorOf(profile.Id), kept);
            return WithLeads(drawn, rules);
        }
        _renderer!.SetPreview(drawn, profile.Width, _trial?.Worst);
        return (drawn, false, false);
    }

    // --- Continuing a dead end ---

    /// <summary>The direction out along the one road ending at a point, when it's a dead end.</summary>
    private NumVector2? DeadEndArm(NumVector2 p)
    {
        var g = Network!.Graph;
        return g.NodeAt(p) is { } n && g.Arms(n) is { Count: 1 } arms ? arms[0].Direction : null;
    }

    /// <summary>The snap tag on a dead end the leg continues: <c>continue · street</c>, or <c>continue · avenue → street</c>
    /// onto another profile.</summary>
    private string ContinueTag(NumVector2 p, ProfileRules rules)
    {
        var old = Network!.Graph.Edge(Network.Graph.DeadEndAt(p, rules)!.Value.EdgeId).Rules.Id;
        return old == rules.Id ? $"continue · {rules.Id}" : $"continue · {old} → {rules.Id}";
    }

    /// <summary>Whether a point is a dead end the preview leg would continue: not the road the leg starts from,
    /// which it closes into a loop instead.</summary>
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
    private Trial? Try(Alignment alignment, ProfileRules rules, Ends continueAt = Ends.Both)
    {
        if (Network is null || alignment.Curve.Length < SplineGraph.NodeTolerance) return null;
        var g = Network.Graph.Clone();
        ApplyBends(g, alignment);
        var result = g.AddSpline(alignment, rules, continueAt);
        Network.Conform(g);
        var edges = result.Edges.Concat(result.Nodes.SelectMany(n => g.Node(n).Edges)).Distinct();
        var issues = Validation.Check(g, edges, result.Nodes)
            .Where(i => !Network.Issues.Any(old => old.Code == i.Code && old.Message == i.Message && NumVector2.Distance(old.Where, i.Where) < 1f))
            .ToList();
        return new Trial(g, result, issues);
    }

    /// <summary>The trial road's height at a plan point: the height line of its nearest new edge there.</summary>
    private static Func<NumVector2, float?> HeightOf(Trial t)
    {
        var edges = t.Result.Edges.Where(t.Graph.HasEdge).Select(t.Graph.Edge).Where(e => e.Heights is not null).ToList();
        return p =>
        {
            float? h = null;
            float best = float.MaxValue;
            foreach (var e in edges)
            {
                var cp = e.Alignment.Curve.ClosestPoint(p);
                float d = NumVector2.DistanceSquared(cp.Position, p);
                if (d < best) (best, h) = (d, e.Heights!.At(cp.S));
            }
            return h;
        };
    }

    /// <summary>The junctions the draw makes or changes; one already built as it is (say the T the chain started
    /// from, which a continued leg passes back through) isn't marked again.</summary>
    private List<JunctionMark> JunctionMarks(Trial t)
    {
        var marks = new List<JunctionMark>();
        foreach (int n in t.Result.Nodes)
        {
            if (Junctions.Label(t.Graph, n) is not { } label) continue;
            if (Network?.Graph.NodeAt(t.Graph.Node(n).Position) is { } built && Junctions.Label(Network.Graph, built) == label) continue;
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
        if (t is null || rules.JunctionKind != JunctionKind.Turnout || _session.LegsBuilt != 0) return null;
        int start = t.Result.Nodes[0];
        if (!t.Issues.Any(i => i.Code == "turnout" && i.NodeId == start)) return null;
        var line = t.Graph.Arms(start).FirstOrDefault(a => !t.Result.Edges.Contains(a.EdgeId));
        if (line.EdgeId == 0) return null;
        return Junctions.TurnoutGhost(t.Graph.Node(start).Position, line.Direction, _snap?.Position ?? _session.Pis[0].Position, rules.MinRadius);
    }

    /// <summary>LMB on an offered turnout builds it as this chain's leg; the chain goes on from its end.</summary>
    private void TakeSuggestion(Alignment turnout)
    {
        if (Host?.Profile is not { } profile || !Build(turnout, profile)) return;
        _session.Place(turnout.Pis[^1].Position, hard: false);
        _session.StartIsCorner = Network!.Graph.DeadEndAt(turnout.Pis[^1].Position, profile.ToRules()) is not null;
    }

    /// <summary>Every currently-held-input flag and existing alignment <see cref="SnapEngine"/> needs, plus the
    /// screen-pixel catch distance converted to plan units for this frame's cursor depth.</summary>
    private SnapQuery BuildSnapQuery(NumVector2 rawPlan, NumVector3 worldCursor, ProfileRules rules, DrawModifiers mods)
    {
        bool ctrl = mods.HasFlag(DrawModifiers.Ctrl);
        return new SnapQuery
        {
            Cursor = rawPlan,
            SessionPis = _session.Placed,
            StartHeading = _session.IsEmpty ? null : _startHeading,
            TangentLock = BendTangent(),
            Candidates = SplineToolView.Candidates(Network!.Graph),
            Rules = rules,
            EnabledProviders = Host?.EnabledSnaps ?? SnapProviders.All,
            CatchDistance = _view.PixelsToPlanUnits(CatchPixels, worldCursor),
            CtrlSteps = ctrl,
            FineSteps = ctrl && mods.HasFlag(DrawModifiers.Shift),
            Disabled = mods.HasFlag(DrawModifiers.Space),
            BendSliders = Mode != DrawMode.Grid, // a grid's corner doesn't reshape a bend
        };
    }

    /// <summary>
    /// A click: the first starts the chain; each later one builds the leg up to it at once (one undo step) and the
    /// chain goes on from there, the new point the next leg's live corner. A leg with an Invalid issue is refused
    /// (red flash, nothing placed) unless Anarchy. A click on a dead end also ends the chain: the road is complete
    /// once it joins another one (or closes on itself).
    /// </summary>
    private void Place(NumVector2 position, bool hard)
    {
        if (Host?.Profile is not { } profile || Network is null) return;
        var rules = profile.ToRules();
        bool allowHard = hard && profile.AllowHardCorners;
        if (hard && !allowHard) _hardHintUntil = Time.GetTicksMsec() / 1000.0 + 1.2;
        if (_session.IsEmpty)
        {
            _sessionProfile = profile;
            _session.Reset(profile.DefaultRadius);
            // Starting on an edge makes it the soft-angle reference for the whole draw ("∡ 90° · square to edge").
            _startHeading = _snap is { Kind: SnapKind.Node or SnapKind.Edge or SnapKind.PerpendicularFoot } s &&
                            NumVector2.Distance(s.Position, position) < 1e-3f ? s.EdgeTangent : null;
            _session.Place(position, allowHard);
            _session.StartIsCorner = Network.Graph.DeadEndAt(position, rules) is not null;
            _startBend = _snap?.Bend is { } b && Near(b.Position, position) ? b : null;
            return;
        }
        if (NumVector2.Distance(position, _session.Pis[0].Position) < SplineGraph.NodeTolerance) return;
        bool finishes = Network.Graph.DeadEndAt(position, rules) is not null;
        float bend = BendRadius(rules, ContinuesAt(position, rules), position);
        if (!Build(_session.LegTo(position, allowHard, bend), profile)) return;
        _session.Place(position, allowHard, bend);
        _bendRadius = null;
        _session.StartIsCorner = Network.Graph.DeadEndAt(position, rules) is not null;
        if (finishes) EndChain();
    }

    private void AdjustRadius(float factor)
    {
        if (Host?.Profile is not { } profile) return;
        if (_session.Bend is not null) { AdjustBendRadius(factor, profile); return; }
        // Stepping starts from what the corner builds now, so the first step down always shows; it can't grow past
        // what fits (there's nothing to gain). Anarchy lifts the profile's minimum (the corner then shows red but builds).
        float fit = LiveCornerFit();
        _session.SetPendingRadius(MathF.Min(_session.PendingRadius, fit) * factor, Host.Anarchy ? 1f : profile.MinRadius, fit);
    }

    /// <summary>The largest radius the live corner (the continued joint the preview leg rounds) fits right now, from
    /// the road the draw would build; ∞ when there's no live corner.</summary>
    private float LiveCornerFit()
    {
        if (!_session.StartIsCorner || _session.IsEmpty || _trial?.Result.Alignment is not { } a) return float.PositiveInfinity;
        var at = _session.Pis[0].Position;
        for (int i = 1; i < a.Pis.Count - 1; i++)
            if (NumVector2.Distance(a.Pis[i].Position, at) < 1e-3f) return a.MaxRadius(i);
        return float.PositiveInfinity;
    }

    /// <summary>Builds one leg as one undo step, unless it has an Invalid issue and Anarchy is off: then it flashes
    /// why in red and nothing is built. The junctions it makes flash their tags.</summary>
    private bool Build(Alignment leg, SplineProfile profile)
    {
        if (Network is null || Host is null) return false;
        var rules = profile.ToRules();
        double until = Time.GetTicksMsec() / 1000.0 + FlashSeconds;
        if (Try(leg, rules) is { FirstInvalid: { } bad } && !Host.Anarchy)
        {
            _flashes.Add((new FlashTag(leg.Pis[^1].Position, $"Can't build: {bad.Message}", Bad: true), until));
            return false;
        }
        Network.RegisterProfile(profile);
        var prior = Network.Graph.Clone();
        var result = Network.Apply(g =>
        {
            ApplyBends(g, leg);
            return g.AddSpline(leg, rules);
        });
        foreach (int n in result.Nodes)
        {
            if (Junctions.Label(Network.Graph, n) is not { } label) continue;
            var at = Network.Graph.Node(n).Position;
            // A continued leg passes back through the junction the chain started at: it's not new.
            if (prior.NodeAt(at) is { } was && Junctions.Label(prior, was) == label) continue;
            _flashes.Add((new FlashTag(at, label), until));
        }
        return true;
    }

    /// <summary>Double-click / Enter: ends the chain. The legs are already built (the double-click's first click built
    /// the last one).</summary>
    private void Finish() => EndChain();

    /// <summary>Ends the chain (RMB, Esc, a finish): the preview leg goes, the built legs stay, and the length drawn
    /// in this chain flashes at its end.</summary>
    private void EndChain()
    {
        if (_session.LegsBuilt > 0)
        {
            float total = new Alignment(_session.Placed).Length;
            _flashes.Add((new FlashTag(_session.Pis[0].Position, $"Total {total:0} m"), Time.GetTicksMsec() / 1000.0 + FlashSeconds));
        }
        CancelSession();
    }

    /// <summary>Ctrl+Z: mid-draw, takes back the last built leg and steps the chain back a point (with no leg built,
    /// ends the draw); otherwise undoes on the graph.</summary>
    private void Undo(bool drawing)
    {
        if (!drawing) { Network?.Undo(); return; }
        if (_session.Bend is not null) { _session.SetBend(null); _bendRadius = null; return; }
        if (_gridAlongEnd is not null) { _gridAlongEnd = null; return; }
        if (_session.LegsBuilt == 0 || Network is null || Host?.Profile is not { } profile) { CancelSession(); return; }
        Network.Undo();
        _session.Undo();
        _session.StartIsCorner = Network.Graph.DeadEndAt(_session.Pis[0].Position, profile.ToRules()) is not null;
    }

    private void Redo(bool drawing)
    {
        if (!drawing) { Network?.Redo(); return; }
        if (!_session.CanRedo || Network is null || Host?.Profile is not { } profile || !Network.Redo()) return;
        _session.Redo();
        _session.StartIsCorner = Network.Graph.DeadEndAt(_session.Pis[0].Position, profile.ToRules()) is not null;
    }

    private void CancelSession()
    {
        _session.Reset(Host?.Profile?.DefaultRadius ?? 0f);
        _sessionProfile = Host?.Profile;
        _startHeading = null;
        _startBend = null;
        _bendRadius = null;
        _stroke = null;
        _gridAlongEnd = null;
        ClearGridTrial();
        _renderer?.SetPreview(null, 0);
    }

    // --- Direct-API test hooks for --demo-draw and --storyboard (no simulated InputEvents) ---

    /// <summary>Places a PI at <see cref="ForcedPlanCursor"/> exactly (no snapping, so golden coordinates stay exact),
    /// but still records the start edge's heading if that point is on a built spline.</summary>
    public void PlaceForTest(bool hard)
    {
        _view.UpdateCursor(); // ForcedPlanCursor was just set; Cursor otherwise only refreshes in _Process
        if (Cursor is not { } hit || Host?.Profile is not { } profile) return;
        var plan = _view.PlanOf(hit);
        if (_session.IsEmpty)
            _snap = SnapEngine.Evaluate(new SnapQuery
            {
                Cursor = plan, Rules = profile.ToRules(), CatchDistance = 0.5f,
                EnabledProviders = SnapProviders.Node | SnapProviders.Edge,
                Candidates = SplineToolView.Candidates(Network!.Graph),
            }) with { Position = plan };
        else _snap = SnapResult.None(plan);
        if (Mode == DrawMode.Grid) GridClick(plan, plan);
        else Click(plan, hard);
    }

    /// <summary>A click at <see cref="ForcedPlanCursor"/> snapped as the mouse's would be (one frame processed first).
    /// Returns the snap's tag.</summary>
    public string SnapClickForTest()
    {
        _Process(0);
        if (_snap is not { } s) return "";
        if (Mode == DrawMode.Grid && Cursor is { } hit) GridClick(s.Position, _view.PlanOf(hit));
        else Click(s.Position, hard: false);
        return s.Tag;
    }

    public void FinishForTest() => Finish();

    /// <summary>Ctrl+Z (mid-draw: takes back the last leg).</summary>
    public void UndoForTest() => Undo(IsDrawing);

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
