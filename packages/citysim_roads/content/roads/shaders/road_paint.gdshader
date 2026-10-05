// Road paint: thin paint over asphalt, so the stones show through it and it follows their bumps. Frayed sides and
// ends close up, worn-through patches where it's old (cut out, so the asphalt below shows), dirt and grey fading.
// Cut-outs use alpha scissor, which stays in the opaque pass (no sorting); fraying fades out with distance so far
// lines don't shimmer.
shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back, diffuse_burley, specular_schlick_ggx;

#include "res://content/roads/shaders/road_common.gdshaderinc"

group_uniforms Colour;
uniform vec3 colour : source_color = vec3(0.8, 0.8, 0.77);
uniform vec3 dirt_colour : source_color = vec3(0.45, 0.44, 0.42);
/// Greying from dirt and age.
uniform float dirt_amount : hint_range(0.0, 1.0) = 0.35;
uniform float roughness : hint_range(0.0, 1.0) = 0.65;
group_uniforms;

group_uniforms Wear;
/// How much of the paint is worn through, in patches (0 = fresh); the road's age (COLOR.r) adds to it.
uniform float worn : hint_range(0.0, 1.0) = 0.3;
/// How far the sides and ends fray, in metres.
uniform float fray : hint_range(0.0, 0.05) = 0.015;
group_uniforms;

group_uniforms Scale;
uniform float detail_scale = 0.5;
uniform float macro_scale = 0.0625;
uniform float normal_strength : hint_range(0.0, 2.0) = 0.6;
group_uniforms;

void vertex() {
	ROAD_VERTEX
}

void fragment() {
	vec2 p = world_pos.xz;
	float dist = length(VERTEX);
	vec4 macro = texture(macro_tex, p * macro_scale);
	vec4 d = texture(detail_tex, p * detail_scale);
	vec4 fine = texture(detail_tex, p * detail_scale * 7.3);

	// Metres to the nearest side or end of the strip.
	float to_edge = min(UV.x - abs(UV.y), min(UV2.x, UV2.y));
	float near = near_fade(dist, detail_far * 0.5);
	float edge_noise = (fine.a - 0.5) * 2.0 + (d.a - 0.5);
	float keep = to_edge - fray * near * (0.5 + edge_noise);

	// Worn through in patches; less in the middle of a line than at its sides.
	float wear_noise = macro.b * 0.5 + d.a * 0.35 + macro.g * 0.15;
	float w = clamp(worn + COLOR.r * 0.25, 0.0, 1.0);
	float through = smoothstep(1.0 - w * 0.9, 1.05 - w * 0.9, wear_noise + (1.0 - d.r) * 0.08);
	ALPHA = keep > 0.0 ? 1.0 - through : 0.0;
	ALPHA_SCISSOR_THRESHOLD = 0.5;

	// Thin over the binder between stones, so those spots are darker; dirt where it's worn.
	float thin = 1.0 - smoothstep(0.3, 0.6, d.r);
	vec3 albedo = mix(colour, dirt_colour, clamp(dirt_amount * (0.5 + macro.a + wear_noise * 0.6), 0.0, 1.0));
	albedo *= mix(1.0, 1.0 - thin * 0.25, near);
	ALBEDO = albedo;
	ROUGHNESS = roughness;
	SPECULAR = 0.4;
	NORMAL_MAP = mix(vec3(0.5, 0.5, 1.0), texture(detail_normal_tex, p * detail_scale).xyz, near * normal_strength);
}
