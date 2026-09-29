using Godot;

namespace CitySim.TerrainSystem;

/// <summary>
/// The terrain's player-facing graphics settings (the game's Graphics menu): each optional effect as a bool, a level or a
/// number, plus an overall <see cref="Preset"/> that only sets the others (changing one by hand makes it Custom). A
/// <see cref="Terrain"/> applies changes live. The player's choice is saved to <see cref="UserPath"/>.
/// Rules for new effects: ROADMAP "Graphics settings" (off must still look fine; note the measured cost here).
/// </summary>
[Tool]
[GlobalClass]
public partial class TerrainGraphics : Resource
{
    public const string UserPath = "user://settings/terrain_graphics.cfg";
    public const string DefaultPath = TerrainPaths.Root + "/settings/graphics.tres";

    public enum PresetLevel { Low, Medium, High, Custom }
    public enum DetailLevel { Low, Medium, High }

    private PresetLevel _preset = PresetLevel.High;
    private bool _wetShine = true, _horizonRing = true;
    private WaterfallQuality _waterfallFx = WaterfallQuality.High;
    private DetailLevel _terrainDetail = DetailLevel.Medium;
    private bool _applyingPreset;

    /// <summary>Sets every setting below to the level's values; Custom leaves them.</summary>
    [Export]
    public PresetLevel Preset
    {
        get => _preset;
        set
        {
            _preset = value;
            if (value != PresetLevel.Custom) ApplyPreset(value);
            EmitChanged();
        }
    }

    /// <summary>Wet ground shine (M3.7): off keeps wet ground darker but matte, with no sun glint. Cost: not measurable.</summary>
    [Export]
    public bool WetShine { get => _wetShine; set => Set(ref _wetShine, value); }

    /// <summary>
    /// Waterfall curtains and mist (M5.8). Low = half the draw distance and half the mist. Cost at the demo's wide fall:
    /// +0.1–0.3 ms/frame and +2 draws; the mist bank ~0.5 ms there.
    /// </summary>
    [Export]
    public WaterfallQuality WaterfallFx { get => _waterfallFx; set => Set(ref _waterfallFx, value); }

    /// <summary>Hills past the map's border (M6 3e) in the game; off = the plain fog edge.</summary>
    [Export]
    public bool HorizonRing { get => _horizonRing; set => Set(ref _horizonRing, value); }

    /// <summary>Terrain mesh detail: Terrain3D's vertices per clipmap ring (Low 32, Medium 48, High 64). More = finer far ground.</summary>
    [Export]
    public DetailLevel TerrainDetail { get => _terrainDetail; set => Set(ref _terrainDetail, value); }

    public int MeshSize => _terrainDetail switch { DetailLevel.Low => 32, DetailLevel.High => 64, _ => 48 };

    private void Set<T>(ref T field, T value)
    {
        if (Equals(field, value)) return;
        field = value;
        if (!_applyingPreset) _preset = PresetLevel.Custom;
        EmitChanged();
    }

    private void ApplyPreset(PresetLevel level)
    {
        _applyingPreset = true;
        WetShine = level != PresetLevel.Low;
        WaterfallFx = level switch { PresetLevel.Low => WaterfallQuality.Off, PresetLevel.Medium => WaterfallQuality.Low, _ => WaterfallQuality.High };
        HorizonRing = level != PresetLevel.Low;
        TerrainDetail = level switch { PresetLevel.Low => DetailLevel.Low, _ => DetailLevel.Medium };
        _applyingPreset = false;
    }

    /// <summary>Saves the settings to a ConfigFile (default: the player's <see cref="UserPath"/>).</summary>
    public Error Save(string path = UserPath)
    {
        DirAccess.MakeDirRecursiveAbsolute(path.GetBaseDir());
        var cfg = new ConfigFile();
        cfg.SetValue("graphics", "preset", _preset.ToString());
        cfg.SetValue("graphics", "wet_shine", _wetShine);
        cfg.SetValue("graphics", "waterfall_fx", _waterfallFx.ToString());
        cfg.SetValue("graphics", "horizon_ring", _horizonRing);
        cfg.SetValue("graphics", "terrain_detail", _terrainDetail.ToString());
        return cfg.Save(path);
    }

    /// <summary>Loads settings saved with <see cref="Save"/>; missing or unknown values keep their current value.</summary>
    public bool Load(string path = UserPath)
    {
        var cfg = new ConfigFile();
        if (!FileAccess.FileExists(path) || cfg.Load(path) != Error.Ok) return false;
        _applyingPreset = true;
        WetShine = cfg.GetValue("graphics", "wet_shine", _wetShine).AsBool();
        if (System.Enum.TryParse(cfg.GetValue("graphics", "waterfall_fx", "").AsString(), out WaterfallQuality fx)) WaterfallFx = fx;
        HorizonRing = cfg.GetValue("graphics", "horizon_ring", _horizonRing).AsBool();
        if (System.Enum.TryParse(cfg.GetValue("graphics", "terrain_detail", "").AsString(), out DetailLevel detail)) TerrainDetail = detail;
        if (System.Enum.TryParse(cfg.GetValue("graphics", "preset", "").AsString(), out PresetLevel preset)) _preset = preset;
        _applyingPreset = false;
        EmitChanged();
        return true;
    }
}
