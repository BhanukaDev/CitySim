// CitySim default terrain theme: temperate, between realism and toon, leaning Ghibli.
// The engine side (Terrain3D, painting, erosion slots, overlays, fog) comes from terrain_sdk; this file only decides
// the automatic ground and a few tint variations. Materials and erosion slots are set in theme.tres.
shader_type spatial;
#include "res://addons/citysim_terrain/shaders/sdk/terrain_core.gdshaderinc"
#include "materials.gdshaderinc"

group_uniforms Theme;
// Grass drifts from cool green down low to gold-green up high (multiplies the grass materials' tints).
uniform vec3 grass_low_tint : source_color = vec3(0.92, 1.0, 0.94);
uniform vec3 grass_high_tint : source_color = vec3(1.06, 1.0, 0.86);
// Rock: warm mossy tops, cool violet faces.
uniform vec3 rock_top_tint : source_color = vec3(1.10, 1.08, 0.80);
uniform vec3 rock_face_tint : source_color = vec3(0.88, 0.90, 1.0);
group_uniforms;

// Laid over grass (the first material), before the scour and deposit slots.
void terrain_auto(TerrainInputs t, inout vec4 w[MATERIAL_PACKS]) {
	float fine = t.noise_fine - 0.5;
	// Dry grass patches, and more of them high up.
	lay(w, MAT_GRASS_DRY, 0.5 * above(t.noise_large + fine * 0.15, 0.8, 0.45));
	lay(w, MAT_GRASS_DRY, 0.4 * above(t.h01, 0.7, 0.5) * above(t.noise_large + fine * 0.15, 0.55, 0.3));
	// Worn slopes.
	lay(w, MAT_GRASS_DIRT, above(t.slope + fine * 4.0, 41.0, 17.0));
}

// After the scour and deposit slots, so cliffs cover eroded ground.
#define TERRAIN_AUTO_LATE
void terrain_auto_late(TerrainInputs t, inout vec4 w[MATERIAL_PACKS]) {
	float slope = t.slope + (t.noise_fine - 0.5) * 4.0;
	// Scree just below the cliffs, broken up by noise.
	lay(w, MAT_GRAVEL, 0.5 * above(slope, 44.0, 7.0) * above(t.noise_medium, 0.65, 0.3));
	// Cliffs, and rock at gentler slopes where water scours hard.
	lay(w, MAT_ROCK, above(slope, 49.5, 8.0));
	lay(w, MAT_ROCK, above(slope, 45.5, 7.0) * above(t.wear, 4.0, 2.0));
}

#define TERRAIN_TINT_HOOK
vec3 terrain_tint(int m, vec3 tint, TerrainInputs t) {
	if (m == MAT_GRASS || m == MAT_GRASS_DRY || m == MAT_GRASS_DIRT)
		return tint * mix(grass_low_tint, grass_high_tint, smoothstep(0.05, 0.6, t.h01));
	if (m == MAT_ROCK)
		return tint * mix(rock_face_tint, rock_top_tint, smoothstep(0.35, 0.85, t.normal.y));
	return tint;
}

#include "res://addons/citysim_terrain/shaders/sdk/terrain_fragment.gdshaderinc"
