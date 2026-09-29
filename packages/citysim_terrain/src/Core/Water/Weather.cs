using System;

namespace CitySim.WaterSystem;

/// <summary>
/// Rain, as far as the ground's look goes: while it rains the whole map's <see cref="Wetness"/> rises toward
/// <see cref="Intensity"/> over <see cref="RampMinutes"/>, and once it stops it dries over <see cref="DryHours"/>.
/// Stepped on simulated time, so it pauses and speeds up with the water sim. Look only (no water is added to the sim) and
/// not saved with the map. Engine-agnostic.
/// </summary>
public sealed class Weather
{
    public bool Raining { get; set; }
    /// <summary>How wet the ground gets while it rains (0-1).</summary>
    public float Intensity { get; set; } = 0.7f;
    /// <summary>Simulated minutes of rain until the ground is nearly as wet as <see cref="Intensity"/>.</summary>
    public float RampMinutes { get; set; } = 10f;
    /// <summary>Simulated hours after the rain until the ground is nearly dry.</summary>
    public float DryHours { get; set; } = 2f;
    /// <summary>The whole map's wetness now (0-1).</summary>
    public float Wetness { get; private set; }

    /// <summary>Advances by <paramref name="simSeconds"/>: an exponential approach, ~95 % of the way in the ramp/dry time.</summary>
    public void Step(double simSeconds)
    {
        if (simSeconds <= 0) return;
        float target = Raining ? Math.Clamp(Intensity, 0f, 1f) : 0f;
        double time = target > Wetness ? RampMinutes * 60.0 : DryHours * 3600.0;
        if (time <= 0) { Wetness = target; return; }
        double k = 1.0 - Math.Exp(-3.0 * simSeconds / time);
        Wetness = (float)(Wetness + (target - Wetness) * k);
        if (Math.Abs(Wetness - target) < 1e-4f) Wetness = target;
    }

    /// <summary>Sets the wetness at once (debug, screenshots).</summary>
    public void SetWetness(float wetness) => Wetness = Math.Clamp(wetness, 0f, 1f);
}
