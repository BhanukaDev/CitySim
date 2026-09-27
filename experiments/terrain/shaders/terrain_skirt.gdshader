shader_type spatial;
render_mode unshaded, cull_back, shadows_disabled;

// The fog floor around the map (TerrainSkirt). It lies entirely outside the terrain bounds, where the terrain
// shader's edge fog is solid, so it is only the fog colour with the same drifting billows. Terrain.cs copies
// these uniforms from the terrain material.
uniform vec3 edge_fog_color : source_color = vec3(0.80, 0.85, 0.95);
uniform float edge_fog_noise_scale = 1100.0;
uniform float edge_fog_cloudiness : hint_range(0.0, 0.5) = 0.08;
uniform float edge_fog_drift = 0.002;
uniform sampler2D macro_noise : hint_default_white, filter_linear_mipmap, repeat_enable;

varying vec3 world_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	vec2 drift = vec2(TIME * edge_fog_drift, TIME * edge_fog_drift * 0.6);
	float billow = texture(macro_noise, world_pos.xz / edge_fog_noise_scale + drift).r * 0.65
			+ texture(macro_noise, world_pos.xz / (edge_fog_noise_scale * 0.37) - drift * 1.7 + 0.43).r * 0.35;
	ALBEDO = edge_fog_color * (1.0 + (billow - 0.5) * edge_fog_cloudiness * 2.0);
}
