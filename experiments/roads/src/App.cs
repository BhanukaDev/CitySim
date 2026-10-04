using CitySim.TerrainSystem;

namespace CitySim;

/// <summary>Start-up wiring for this experiment: the map comes from the command line (--load, --flat, --preset, ...).</summary>
internal static class App
{
#pragma warning disable CA2255 // app code: exactly what the attribute is for
    [System.Runtime.CompilerServices.ModuleInitializer]
#pragma warning restore CA2255
    internal static void Init() => TerrainCommandLine.Use();
}
