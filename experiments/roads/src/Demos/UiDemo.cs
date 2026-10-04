using System;
using System.Linq;
using CitySim.Content;
using CitySim.Roads;
using CitySim.UI;
using Godot;

namespace CitySim.Demos;

/// <summary>
/// Command-line checks for the build UI, to verify without a person:
/// <list type="bullet">
/// <item><c>--demo-content</c>: lists every loaded category, tab and item, checks them, prints "Demo content: all ok"
///   (or the problems) and quits.</item>
/// <item><c>--ui=open:roads,tab:medium,pick:four_lane,hover:four_lane,search:lane,mode:grid,anarchy</c>: puts the UI in that
///   state (in that order) for a <c>--screenshot</c>. <c>hover</c> shows the item's hover card above its card, since a
///   real tooltip needs the mouse.</item>
/// </list>
/// </summary>
public partial class UiDemo : Node
{
    [Export] public GameHud? Hud { get; set; }

    public override void _Ready()
    {
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg == "--demo-content") Callable.From(CheckContent).CallDeferred();
            else if (arg.StartsWith("--ui=")) Callable.From(() => ApplyUi(arg["--ui=".Length..])).CallDeferred();
        }
    }

    private void CheckContent()
    {
        var lib = Hud!.Library;
        var problems = lib.Warnings.ToList();
        foreach (var c in lib.Categories)
        {
            GD.Print($"category {c.Id} \"{c.Label}\" ({c.Source}){(c.Icon is null ? " — no icon" : "")}");
            foreach (var t in lib.TabsOf(c))
            {
                GD.Print($"  tab {t.Id} \"{t.Label}\" ({t.Source})");
                foreach (var i in lib.ItemsOf(t))
                {
                    GD.Print($"    {i.Id} \"{i.Label}\" ({i.Source}) " + string.Join(", ", i.Details().Select(d => $"{d.Label} {d.Value}")));
                    if (i is RoadType r)
                    {
                        if (r.ToDef().Width <= 0) problems.Add($"{r.Id}: width is {r.ToDef().Width}");
                        if (r.Median == MedianKind.Raised && (r.OneWay || r.Lanes < 2)) problems.Add($"{r.Id}: a median needs two directions");
                        if (!r.OneWay && r.ForwardLanes >= r.Lanes) problems.Add($"{r.Id}: ForwardLanes {r.ForwardLanes} leaves no lane going back");
                        if (r.OneWay && r.ForwardLanes != 0) problems.Add($"{r.Id}: ForwardLanes only applies to two-way roads");
                    }
                    if (i.Description == "") problems.Add($"{i.Id}: no description");
                }
            }
        }
        if (!lib.CategoriesWithContent().Any()) problems.Add("no category has content");
        foreach (var p in problems) GD.PrintErr($"Demo content: {p}");
        GD.Print(problems.Count == 0 ? "Demo content: all ok" : $"Demo content: {problems.Count} problem(s)");
        GetTree().Quit(problems.Count == 0 ? 0 : 1);
    }

    private async void ApplyUi(string spec)
    {
        var hud = Hud!;
        string? hover = null;
        foreach (string part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':', 2);
            string key = kv[0], value = kv.Length > 1 ? kv[1] : "";
            switch (key)
            {
                case "open": hud.OpenById(value); break;
                case "tab": hud.Tray.OpenTab(value); break;
                case "pick": hud.Tray.Pick(hud.Library.Item(value)); break;
                case "search": hud.Tray.SetSearch(value); break;
                case "mode": hud.RoadOptions.SetMode(Enum.Parse<RoadDrawMode>(value, ignoreCase: true)); break;
                case "anarchy": hud.RoadOptions.ToggleAnarchy(); break;
                case "hover": hover = value; break;
                default: GD.PushError($"--ui: unknown part \"{part}\""); break;
            }
        }
        if (hover is null || hud.Library.Item(hover) is not { } item) return;

        // Wait for the cards to lay out, then place the card above the item's card like a tooltip.
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (hud.Tray.CardFor(item) is not { } card) { GD.PushError($"--ui hover: {hover} has no card on screen"); return; }
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", UiTheme.Theme.GetStylebox("panel", "TooltipPanel"));
        panel.AddChild(DetailCard.Create(item, hud.Tray.Searching ? hud.Library.TabOf(item)?.Label : null));
        hud.Root.AddChild(panel);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var r = card.GetGlobalRect();
        panel.Position = new Vector2(r.Position.X + 16, r.Position.Y - panel.Size.Y - 6);
    }
}
