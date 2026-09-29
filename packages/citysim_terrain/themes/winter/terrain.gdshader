// CitySim winter terrain theme (demo): snow everywhere, wind-scoured slopes showing dry grass and earth, snow-capped
// rock, frozen streams. Materials and erosion slots are set in theme.tres.
shader_type spatial;
#include "res://terrain_sdk/terrain_core.gdshaderinc"
#include "materials.gdshaderinc"

group_uniforms Theme;
// Slope (degrees) where snow starts sliding off and the ground shows through.
uniform float bare_slope = 30.0;
// Height (0-1 of the map's range) above which slopes stay snowier.
uniform float snowy_above = 0.55;
// Snow caught on rock ledges: how much of an upward-facing rock face turns white.
uniform float rock_snow : hint_range(0.0, 1.0) = 0.75;
uniform vec3 snow_shadow_tint : source_color = vec3(0.86, 0.9, 1.0);
group_uniforms;

void terrain_auto(TerrainInputs t, inout vec4 w[MATERIAL_PACKS]) {
	float fine = t.noise_fine - 0.5;
	float patchy = t.noise_patchy - 0.5;
	// Drifts of rougher, bluer snow.
	lay(w, MAT_SNOW_ROUGH, 0.7 * above(t.noise_medium + patchy * 0.4, 0.55, 0.25));
	// Wind-scoured slopes: dry grass pokes through, less of it high up.
	float scoured = above(t.slope + fine * 6.0, bare_slope, 8.0) * above(t.noise_large + patchy * 0.3, 0.45, 0.3);
	lay(w, MAT_GRASS_DRY, 0.8 * scoured * below(t.h01, snowy_above, 0.2));
	// Bare earth on the steepest of those.
	lay(w, MAT_DIRT, 0.7 * above(t.slope + fine * 5.0, bare_slope + 8.0, 6.0) * above(t.noise_medium, 0.4, 0.3));
}

#define TERRAIN_AUTO_LATE
void terrain_auto_late(TerrainInputs t, inout vec4 w[MATERIAL_PACKS]) {
	float slope = t.slope + (t.noise_fine - 0.5) * 5.0;
	lay(w, MAT_GRAVEL, 0.5 * above(slope, 40.0, 6.0) * above(t.noise_medium, 0.6, 0.3));
	lay(w, MAT_ROCK, above(slope, 45.0, 7.0));
}

#define TERRAIN_TINT_HOOK
vec3 terrain_tint(int m, vec3 tint, TerrainInputs t) {
	// Snow on rock ledges and tops.
	if (m == MAT_ROCK)
		return mix(tint, vec3(0.9, 0.93, 1.0), rock_snow * smoothstep(0.55, 0.8, t.normal.y));
	// Snow in the valleys is a touch bluer than on the tops.
	if (m == MAT_SNOW || m == MAT_SNOW_ROUGH)
		return tint * mix(snow_shadow_tint, vec3(1.0), smoothstep(0.1, 0.7, t.h01));
	return tint;
}

#include "res://terrain_sdk/terrain_fragment.gdshaderinc"
