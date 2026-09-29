using Godot;

namespace CitySim.TerrainSystem;

/// <summary>
/// Developer knobs for the terrain and water (not player settings): timings, thresholds and distances that used to be
/// constants. Defaults are the tuned values. A <see cref="Terrain"/> applies changes live unless a property says it needs
/// a map reload. Point <see cref="Terrain.Tuning"/> at a project's own copy to tweak without touching the package.
/// </summary>
[Tool]
[GlobalClass]
public partial class TerrainTuning : Resource
{
    public const string DefaultPath = TerrainPaths.Root + "/settings/tuning.tres";

    private float _lakeDelay = 0.3f, _fullLakeDelay = 15f;
    private int _waterMaxCells = 4096;
    private Vector4 _waterLodDistances = new(1.5f, 3f, 7f, 14f);
    private float _fallMinHeight = 3f, _fallMinFlux = 0.02f, _fallFullFlux = 0.4f, _curtainDistance = 700f, _mistDistance = 450f;
    private float _horizonHillsEnd = 6000f, _horizonMinRelief = 200f, _horizonSinkDepth = 10f, _horizonSinkDistance = 600f;
    private float _rainRampMinutes = 10f, _dryHours = 2f;
    private int _undoSteps = 50, _undoMemoryMb = 768;

    /// <summary>Wait after the last height edit before finding lakes near it again (s; a stroke edits every tick).</summary>
    [ExportGroup("Lakes")]
    [Export(PropertyHint.Range, "0.05,5,0.05,suffix:s")]
    public float LakeDelay { get => _lakeDelay; set => Set(ref _lakeDelay, value); }
    /// <summary>Quiet time after the last nearby search before a full-map one puts right what they can't see (s).</summary>
    [Export(PropertyHint.Range, "1,120,1,suffix:s")]
    public float FullLakeDelay { get => _fullLakeDelay; set => Set(ref _fullLakeDelay, value); }

    /// <summary>Most water cells a side; bigger maps simulate every 2nd/4th vertex. Needs a map reload. 2048 = the old 14 m cells on 28.7 km.</summary>
    [ExportGroup("Water")]
    [Export(PropertyHint.Range, "512,8192,512")]
    public int WaterMaxCells { get => _waterMaxCells; set => Set(ref _waterMaxCells, value); }
    /// <summary>Water mesh LOD switch distances, in water tiles from the camera (LOD 0→1, 1→2, 2→3, 3→4).</summary>
    [Export]
    public Vector4 WaterLodDistances { get => _waterLodDistances; set => Set(ref _waterLodDistances, value); }

    /// <summary>A fall is at least this high; lower drops are rapids (no curtain).</summary>
    [ExportGroup("Waterfalls")]
    [Export(PropertyHint.Range, "0.5,30,0.5,suffix:m")]
    public float FallMinHeight { get => _fallMinHeight; set => Set(ref _fallMinHeight, value); }
    /// <summary>Flux (depth × speed, m²/s) where a curtain starts to show.</summary>
    [Export(PropertyHint.Range, "0.001,1,0.001")]
    public float FallMinFlux { get => _fallMinFlux; set => Set(ref _fallMinFlux, value); }
    /// <summary>Flux (m²/s) where a curtain is at full strength.</summary>
    [Export(PropertyHint.Range, "0.05,5,0.05")]
    public float FallFullFlux { get => _fallFullFlux; set => Set(ref _fallFullFlux, value); }
    /// <summary>Farthest curtain drawn at High (m from the camera); Low draws half as far.</summary>
    [Export(PropertyHint.Range, "100,3000,10,suffix:m")]
    public float CurtainDistance { get => _curtainDistance; set => Set(ref _curtainDistance, value); }
    /// <summary>Farthest mist drawn at High (m); Low draws about half as far.</summary>
    [Export(PropertyHint.Range, "50,2000,10,suffix:m")]
    public float MistDistance { get => _mistDistance; set => Set(ref _mistDistance, value); }

    /// <summary>Distance past the border where the horizon's hills reach full height and fade into haze.</summary>
    [ExportGroup("Horizon")]
    [Export(PropertyHint.Range, "1500,20000,100,suffix:m")]
    public float HorizonHillsEnd { get => _horizonHillsEnd; set => Set(ref _horizonHillsEnd, value); }
    /// <summary>The least hill height past the border at the default relief, so flat maps still get a skyline.</summary>
    [Export(PropertyHint.Range, "0,1000,10,suffix:m")]
    public float HorizonMinRelief { get => _horizonMinRelief; set => Set(ref _horizonMinRelief, value); }
    /// <summary>The fog floor drops this far below the lowest terrain point ...</summary>
    [Export(PropertyHint.Range, "0,200,1,suffix:m")]
    public float HorizonSinkDepth { get => _horizonSinkDepth; set => Set(ref _horizonSinkDepth, value); }
    /// <summary>... over this distance from the border.</summary>
    [Export(PropertyHint.Range, "50,5000,10,suffix:m")]
    public float HorizonSinkDistance { get => _horizonSinkDistance; set => Set(ref _horizonSinkDistance, value); }

    /// <summary>Simulated minutes of rain until the ground is nearly as wet as the rain's intensity.</summary>
    [ExportGroup("Weather")]
    [Export(PropertyHint.Range, "0,120,1,suffix:min")]
    public float RainRampMinutes { get => _rainRampMinutes; set => Set(ref _rainRampMinutes, value); }
    /// <summary>Simulated hours after the rain until the ground is nearly dry.</summary>
    [Export(PropertyHint.Range, "0,48,0.25,suffix:h")]
    public float DryHours { get => _dryHours; set => Set(ref _dryHours, value); }

    /// <summary>Most undo steps kept.</summary>
    [ExportGroup("Undo")]
    [Export(PropertyHint.Range, "1,500,1")]
    public int UndoSteps { get => _undoSteps; set => Set(ref _undoSteps, value); }
    /// <summary>Most memory the undo history may hold.</summary>
    [Export(PropertyHint.Range, "16,8192,16,suffix:MB")]
    public int UndoMemoryMb { get => _undoMemoryMb; set => Set(ref _undoMemoryMb, value); }

    private void Set<T>(ref T field, T value)
    {
        if (Equals(field, value)) return;
        field = value;
        EmitChanged();
    }
}
