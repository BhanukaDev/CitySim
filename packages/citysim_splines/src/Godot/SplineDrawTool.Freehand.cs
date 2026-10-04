using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Splines.Godot;

/// <summary>
/// Freehand mode (key 3; DESIGN.md → Modes, 3): hold LMB and drag. The stroke is sampled every couple of metres and
/// fitted (<see cref="FreehandFit"/>) into a few PIs with radii each frame, tried on the graph like a Draw leg, and
/// built on release as one undo step. Its start and end snap like Draw clicks, so a stroke can start on a dead end and
/// continue it, or end on a road and make a junction. RMB or Esc during the drag drops it.
/// </summary>
public partial class SplineDrawTool
{
    /// <summary>The stroke recorded so far (plan, raw mouse points after its snapped start), or null.</summary>
    private List<NumVector2>? _stroke;

    private void HandleFreehandButton(InputEventMouseButton mb)
    {
        if (mb.ButtonIndex == MouseButton.Right && mb.Pressed)
        {
            GetViewport().SetInputAsHandled();
            CancelSession();
            return;
        }
        if (mb.ButtonIndex != MouseButton.Left) return;
        if (mb.Pressed && Cursor is { } hit && Host?.Profile is { } profile)
        {
            GetViewport().SetInputAsHandled();
            _sessionProfile = profile;
            _stroke = new List<NumVector2> { _snap?.Position ?? _view.PlanOf(hit) };
            _startBend = _snap?.Bend;
        }
        else if (!mb.Pressed && _stroke is not null)
        {
            GetViewport().SetInputAsHandled();
            FinishStroke();
        }
    }

    private void ProcessFreehand(SplineProfile profile, ProfileRules rules, NumVector2 raw)
    {
        // A release lost to the UI (the mouse let go over the options bar) still ends the stroke.
        if (_stroke is not null && ForcedPlanCursor is null && !Input.IsMouseButtonPressed(MouseButton.Left)) FinishStroke();

        Alignment? fit = null, preview = null;
        bool leadIn = false, leadOut = false;
        List<NumVector2>? samples = null;
        _trial = null;
        _suggestion = null;
        if (_stroke is not null)
        {
            if (NumVector2.Distance(raw, _stroke[^1]) >= FreehandFit.SampleSpacing) _stroke.Add(raw);
            samples = StrokeSamples();
            fit = Fit(samples, rules);
            _trial = Try(fit, rules);
            (preview, leadIn, leadOut) = ShowTrialPreview(fit, rules, profile);
            ShowBends(fit);
        }
        else
        {
            Network!.Hide(Array.Empty<int>());
            _renderer!.SetPreview(null, 0);
            ShowBends(null);
        }
        _renderer!.SetGhost(null, 0);
        _issueList?.Show(_trial?.Issues ?? new List<Issue>(), Network!.Issues, Host!.Anarchy);

        double now = Time.GetTicksMsec() / 1000.0;
        _flashes.RemoveAll(f => now > f.Until);
        _overlay!.Show(new OverlayFrame
        {
            Snap = _snap,
            Rules = rules,
            BuiltEnds = Dots(),
            Mouse = _view.MouseScreen(),
            Flashes = _flashes.Select(f => f.Tag).ToList(),
            Preview = preview,
            LeadIn = leadIn,
            LeadOut = leadOut,
            Stroke = samples,
            StrokeLabel = fit is null ? null : $"{samples!.Count} samples → {fit.Pis.Count} points",
            Junctions = _trial is { } t ? JunctionMarks(t) : Array.Empty<JunctionMark>(),
            Issues = _trial?.Issues ?? (IReadOnlyList<Issue>)Array.Empty<Issue>(),
            Worst = _trial?.Worst,
            SharpAngles = _trial is { } t2 ? SharpAngles(t2) : Array.Empty<(NumVector2, NumVector2, NumVector2)>(),
            PlaceLabel = _stroke is null ? "Hold and drag" : "Release to build",
            Bend = BendMark(),
        });
    }

    /// <summary>The stroke with its end at the snapped cursor (a node or road it lands on).</summary>
    private List<NumVector2> StrokeSamples()
    {
        var samples = new List<NumVector2>(_stroke!);
        if (_snap is { } s && NumVector2.Distance(s.Position, samples[^1]) > 0.1f)
        {
            // The last raw sample sits right by the snapped end: replace it rather than add a stub.
            if (samples.Count > 1 && NumVector2.Distance(s.Position, samples[^1]) < FreehandFit.SampleSpacing) samples[^1] = s.Position;
            else samples.Add(s.Position);
        }
        return samples;
    }

    private Alignment Fit(IReadOnlyList<NumVector2> samples, ProfileRules rules) =>
        FreehandFit.Fit(samples, rules, Host!.Anarchy ? 1f : rules.MinRadius, rules.DefaultRadius);

    /// <summary>LMB released: builds the fitted stroke (refused, with a red flash, if Invalid and not Anarchy).</summary>
    private void FinishStroke()
    {
        if (_stroke is null || Host?.Profile is not { } profile) return;
        var samples = StrokeSamples();
        if (samples.Count >= 2)
        {
            var fit = Fit(samples, profile.ToRules());
            if (fit.Length >= SplineGraph.NodeTolerance && Build(fit, profile))
                _flashes.Add((new FlashTag(fit.Pis[^1].Position, $"Total {fit.Length:0} m"), Time.GetTicksMsec() / 1000.0 + FlashSeconds));
        }
        CancelSession();
    }

    /// <summary>A whole stroke at once (<c>--demo-modes</c>, storyboard): built as on release. With
    /// <paramref name="build"/> false it's left in progress, so the frame shows the preview.</summary>
    public void StrokeForTest(IReadOnlyList<NumVector2> samples, bool build = true)
    {
        _sessionProfile = Host?.Profile;
        _snap = null;
        _stroke = samples.ToList();
        if (build) FinishStroke();
        else ForcedPlanCursor = samples[^1];
    }
}
