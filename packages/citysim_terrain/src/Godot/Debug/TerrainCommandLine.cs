using System.Globalization;
using CitySim.TerrainSystem.Generation;
using Godot;

namespace CitySim.TerrainSystem;

/// <summary>
/// Command-line map choice for projects without their own start menu (experiments): call <see cref="Use"/> once at start-up
/// (e.g. from a module initializer) and every Terrain opens what the flags ask for:
/// <c>--load=path.csmap</c>, or a new map from <c>--flat[=height]</c>, <c>--preset=name</c>, <c>--seed=n</c>, <c>--size=cells</c>.
/// No flag = the Terrain's exports decide.
/// </summary>
public static class TerrainCommandLine
{
    public static void Use() => TerrainHost.TakeStartupRequest = Request;

    public static MapRequest? Request()
    {
        GenSettings? gen = null;
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--load="))
            {
                string path = arg["--load=".Length..];
                var (map, splat, water) = MapFile.LoadWithWater(path);
                return new LoadedMapRequest(map, splat, path, water);
            }
            if (arg == "--flat" || arg.StartsWith("--flat="))
            {
                float h = arg.Length > "--flat=".Length ? float.Parse(arg["--flat=".Length..], CultureInfo.InvariantCulture) : 40f;
                gen = (gen ?? new GenSettings()) with { Source = TerrainSource.Flat, FlatHeight = h };
            }
            else if (arg.StartsWith("--preset="))
            {
                if (GenPresets.Find(arg["--preset=".Length..]) is { } preset) gen = preset.ApplyTo(gen ?? new GenSettings());
                else GD.PushError($"Unknown preset '{arg["--preset=".Length..]}'");
            }
            else if (arg.StartsWith("--seed=") && int.TryParse(arg["--seed=".Length..], out int seed))
                gen = (gen ?? new GenSettings()) with { Noise = (gen ?? new GenSettings()).Noise with { Seed = seed } };
            else if (arg.StartsWith("--size=") && int.TryParse(arg["--size=".Length..], out int cells))
                gen = (gen ?? new GenSettings()) with { Cells = cells };
        }
        return gen is null ? null : new GeneratedMapRequest(gen);
    }
}
