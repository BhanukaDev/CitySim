// Road concrete: sidewalk slabs, kerb stones and gutters (one shader, set per material). Fine grain, stains and dirt,
// joints every `joint_spacing` metres along the road with a tone change per slab, and the odd cracked slab. Junctions
// have no road direction, so their paving has no joints.
shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back, diffuse_burley, specular_schlick_ggx;

#include "res://content/roads/shaders/road_common.gdshaderinc"

group_uniforms Colour;
uniform vec3 colour : source_color = vec3(0.56, 0.55, 0.52);
/// Tone change from slab to slab.
uniform float slab_variation : hint_range(0.0, 0.3) = 0.07;
uniform vec3 stain_colour : source_color = vec3(0.3, 0.29, 0.27);
/// Stains and weathering.
uniform float stain_amount : hint_range(0.0, 1.0) = 0.35;
/// Dark spots (gum, drips).
uniform float spot_amount : hint_range(0.0, 1.0) = 0.15;
uniform float roughness : hint_range(0.0, 1.0) = 0.88;
group_uniforms;

group_uniforms Joints;
/// Metres between joints along the road (0 = none).
uniform float joint_spacing : hint_range(0.0, 10.0) = 1.5;
/// Joint width in metres.
uniform float joint_width : hint_range(0.0, 0.05) = 0.008;
uniform vec3 joint_colour : source_color = vec3(0.2, 0.19, 0.18);
/// Share of slabs with a crack in them.
uniform float cracked_slabs : hint_range(0.0, 1.0) = 0.12;
group_uniforms;

group_uniforms Scale;
/// Detail texture tiles per metre (1 = 1 m tile: grain about 7 mm).
uniform float detail_scale = 1.0;
uniform float macro_scale = 0.0625;
uniform float normal_strength : hint_range(0.0, 2.0) = 0.35;
group_uniforms;

void vertex() {
	ROAD_VERTEX
}

void fragment() {
	vec2 p = surface_coord(UV, UV2.x);
	float dist = length(VERTEX);
	vec4 macro = texture(macro_tex, p * macro_scale);
	vec4 region = texture(macro_tex, p * macro_scale * 0.137 + vec2(0.53, 0.19));
	bool jointed = joint_spacing > 0.0 && UV2.x > -1.5;
	float along = UV.x / max(joint_spacing, 0.01);
	float slab = floor(along);
	float slab_hash = hash12(vec2(slab, floor(UV.y * 0.1)));
	float to_joint = abs(fract(along + 0.5) - 0.5) * joint_spacing; // metres
	float joint_fw = fwidth(to_joint);
	float slab_fw = fwidth(along);
	float to_crack = macro.r * 0.10;
	float crack_fw = fwidth(to_crack);

	vec3 albedo = colour;
	if (jointed) albedo *= 1.0 + (slab_hash - 0.5) * 2.0 * slab_variation * pixel_fade(slab_fw, 1.0);
	float stain = smoothstep(0.35, 0.9, macro.b * 0.6 + region.b * 0.6) * stain_amount;
	albedo = mix(albedo, stain_colour, stain);
	float spots = texture(macro_tex, p * macro_scale * 4.0).a; // ~10 cm spots
	albedo = mix(albedo, stain_colour * 0.4, spots * spot_amount);
	float rough = roughness;
	vec3 nmap = vec3(0.5, 0.5, 1.0);

	float near = near_fade(dist, detail_far);
	if (near > 0.0) {
		vec4 d = texture(detail_tex, p * detail_scale);
		albedo *= mix(1.0, 0.86 + 0.28 * d.b + (d.g - 0.5) * 0.12 * step(0.5, d.r), near);
		nmap = mix(nmap, texture(detail_normal_tex, p * detail_scale).xyz, near * normal_strength);
	}

	if (jointed) {
		float joint = aa_line(to_joint, joint_fw, joint_width) * pixel_fade(joint_fw, joint_width * 4.0);
		albedo = mix(albedo, joint_colour, joint);
		rough = mix(rough, 1.0, joint);
		float crack_near = near_fade(dist, crack_far);
		if (slab_hash < cracked_slabs && crack_near > 0.0) {
			float line = aa_line(to_crack, crack_fw, 0.004);
			albedo = mix(albedo, joint_colour, line * crack_near);
		}
	}

	ALBEDO = albedo;
	ROUGHNESS = rough;
	SPECULAR = 0.4;
	NORMAL_MAP = nmap;
}
