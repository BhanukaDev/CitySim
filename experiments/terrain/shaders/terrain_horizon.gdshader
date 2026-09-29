shader_type spatial;
render_mode cull_back, shadows_disabled;

// The ring around the map (TerrainHorizon). Two looks, picked by horizon_mix (set by Terrain.EdgeStyle):
// 0 = the fog floor (UV2.x heights sink below the map, drawn in the edge fog colour with drifting billows), and
// 1 = the horizon ring (VERTEX heights: low hills carrying on from the border, lit like the terrain with the theme's
// materials, fading into the haze). Terrain.cs copies the theme's uniforms of the same names from its material and
// sets the runtime ones (terrain_origin/size, material arrays, sea level, wetness).

uniform float horizon_mix = 1.0;

// Border and edge fog (same meaning as in terrain_sdk/terrain_core.gdshaderinc).
uniform vec2 terrain_origin = vec2(0.0);
uniform vec2 terrain_size = vec2(1024.0);
uniform vec3 edge_fog_color : source_color = vec3(0.80, 0.85, 0.95);
uniform float edge_fog_width = 220.0;
uniform float edge_fog_wobble = 120.0;
uniform float edge_fog_noise_scale = 1100.0;
uniform float edge_fog_cloudiness : hint_range(0.0, 0.5) = 0.08;
uniform float edge_fog_drift = 0.002;
uniform sampler2D macro_noise : hint_default_white, filter_linear_mipmap, repeat_enable;

// Horizon ground.
uniform float horizon_edge_fog = 0.25;
uniform int horizon_flat_material = 0;
uniform int horizon_steep_material = -1;
uniform float horizon_steep_slope = 28.0;
uniform vec3 horizon_tint : source_color = vec3(1.0);
uniform float horizon_haze_start = 400.0;
uniform float horizon_haze_end = 7000.0;
uniform vec3 horizon_sea_color : source_color = vec3(0.10, 0.22, 0.30);
uniform bool has_sea = false;
uniform float sea_level = 0.0;

// The theme's materials (runtime copies of the terrain's).
uniform sampler2DArray albedo_height_array : filter_linear_mipmap_anisotropic, repeat_enable;
uniform vec4 material_tint[16];
uniform vec4 material_params[16];
uniform float detail_contrast = 0.6;
uniform float detail_saturation = 0.65;
uniform float macro_scale = 700.0;
uniform float macro_strength = 0.16;
uniform float roughness = 0.9;
uniform float wet_darken = 0.5;
uniform float global_wetness = 0.0;

// Lighting (same as the terrain's default light()).
uniform float light_wrap = 0.35;
uniform float toon_bands = 0.4;
uniform float band_softness = 0.2;
uniform vec3 terminator_tint : source_color = vec3(1.0, 0.72, 0.5);
uniform float terminator_strength = 0.3;
uniform vec3 shadow_tint : source_color = vec3(0.62, 0.72, 1.0);
uniform float shadow_tint_strength = 0.5;

const float NEUTRAL_GREY = 0.4;

varying vec3 world_pos;
varying float ground_y;

void vertex() {
	float h = mix(UV2.x, VERTEX.y, horizon_mix);
	ground_y = h;
	if (has_sea && horizon_mix > 0.5) h = max(h, sea_level);
	VERTEX.y = h;
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

// Distance (metres) outside the terrain's rectangle (0 inside).
float outside_distance(vec2 xz) {
	vec2 rel = xz - terrain_origin;
	vec2 o = max(max(-rel, rel - terrain_size), vec2(0.0));
	return length(o);
}

// A material's colour at p: its far tiling, tinted like the terrain's stylize().
vec3 ground(int m, vec2 p, float cam_d) {
	vec3 uvw = vec3(p / max(material_params[m].y, 0.1), float(m));
	// Far away the texture only shows its tiling pattern: fade to its average colour (the smallest mip).
	float detail = 1.0 - smoothstep(300.0, 1500.0, cam_d);
	vec3 tex = mix(textureLod(albedo_height_array, uvw, 12.0).rgb, texture(albedo_height_array, uvw).rgb, detail);
	vec3 ratio = tex / NEUTRAL_GREY;
	float lum = dot(ratio, vec3(0.299, 0.587, 0.114));
	ratio = mix(vec3(lum), ratio, detail_saturation);
	return material_tint[m].rgb * pow(max(ratio, vec3(0.0)), vec3(2.2 * detail_contrast * material_params[m].z));
}

void fragment() {
	vec2 drift = vec2(TIME * edge_fog_drift, TIME * edge_fog_drift * 0.6);
	float billow = texture(macro_noise, world_pos.xz / edge_fog_noise_scale + drift).r * 0.65
			+ texture(macro_noise, world_pos.xz / (edge_fog_noise_scale * 0.37) - drift * 1.7 + 0.43).r * 0.35;
	vec3 fog_col = edge_fog_color * (1.0 + (billow - 0.5) * edge_fog_cloudiness * 2.0);

	if (horizon_mix < 0.5) {
		// Fog floor: only the fog colour.
		ALBEDO = vec3(0.0);
		EMISSION = fog_col;
		ROUGHNESS = 1.0;
		SPECULAR = 0.0;
	} else {
		vec3 n = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
		float slope = degrees(acos(clamp(n.y, 0.0, 1.0)));
		float cam_d = length(world_pos - CAMERA_POSITION_WORLD);
		vec3 col = ground(horizon_flat_material, world_pos.xz, cam_d);
		if (horizon_steep_material >= 0)
			col = mix(col, ground(horizon_steep_material, world_pos.xz, cam_d),
					smoothstep(horizon_steep_slope - 6.0, horizon_steep_slope + 6.0, slope + (billow - 0.5) * 10.0));
		float mv = texture(macro_noise, world_pos.xz / macro_scale).r - 0.5;
		col *= (1.0 + mv * macro_strength * 2.0) * horizon_tint;
		col *= 1.0 - wet_darken * global_wetness;
		float rough = roughness;
		float spec = 0.2;
		bool sea = has_sea && ground_y < sea_level - 0.05;
		if (sea) {
			col = horizon_sea_color;
			rough = 0.15;
			spec = 0.3;
			n = vec3(0.0, 1.0, 0.0);
		}

		// Haze by distance past the border, plus the light fog band along the border itself (the terrain draws its
		// inner half with the same formula, so they meet).
		float out_d = outside_distance(world_pos.xz);
		float haze = smoothstep(horizon_haze_start, horizon_haze_end, out_d + (billow - 0.5) * edge_fog_wobble * 4.0);
		float band = 1.0 - smoothstep(0.0, edge_fog_width, out_d - billow * edge_fog_wobble);
		// No band over the sea: the map's water is drawn over the terrain's band, so it has none either.
		// The band only hides the seam up close; from far out it would frame the map.
		band *= 1.0 - smoothstep(3000.0, 15000.0, cam_d);
		float fog = max(haze, sea ? 0.0 : band * band * horizon_edge_fog);

		ALBEDO = col * (1.0 - fog);
		NORMAL = normalize((VIEW_MATRIX * vec4(n, 0.0)).xyz);
		EMISSION = fog_col * fog;
		ROUGHNESS = mix(rough, 1.0, fog);
		SPECULAR = spec * (1.0 - fog);
	}
}

void light() {
	float ndl = dot(NORMAL, LIGHT);
	float wrapped = clamp((ndl + light_wrap) / (1.0 + light_wrap), 0.0, 1.0);
	float banded = smoothstep(0.5 - band_softness, 0.5 + band_softness, wrapped);
	float d = mix(wrapped, banded, toon_bands);
	float edge = d * (1.0 - d) * 4.0;
	vec3 tint = mix(vec3(1.0), terminator_tint, edge * terminator_strength);
	DIFFUSE_LIGHT += LIGHT_COLOR * ATTENUATION * d * tint / PI;
	float shade = 1.0 - d * ATTENUATION;
	DIFFUSE_LIGHT += shadow_tint * shade * shadow_tint_strength * 0.12;
}
