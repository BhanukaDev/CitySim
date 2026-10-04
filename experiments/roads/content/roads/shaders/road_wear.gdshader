// Tyre wear over a junction's asphalt, along the paths cars take (RoadVisual.Tracks): the same wheel tracks and oil stripe
// as the lanes leading in, so wear carries on into the turns, plus dark rubber on the turns. Blended over the asphalt
// (it only adds wear), so ribbons that overlap where paths share a lane add up like real traffic does.
// UV = (metres along, how hard the path turns 0..1); UV2 = (across the lane, -0.5..0.5; share of the lane's traffic).
// Keep the track and oil numbers in step with road_asphalt, so the lanes and their ribbons meet without a seam.
shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_back, diffuse_burley, specular_schlick_ggx;

#include "res://content/roads/shaders/road_common.gdshaderinc"

group_uniforms Wear;
uniform float track_offset : hint_range(0.0, 0.5) = 0.27;
uniform float track_width : hint_range(0.0, 0.3) = 0.11;
uniform vec3 track_colour : source_color = vec3(0.43, 0.42, 0.405);
uniform float track_strength : hint_range(0.0, 1.0) = 0.45;
uniform float track_roughness : hint_range(0.0, 1.0) = 0.72;
uniform vec3 oil_colour : source_color = vec3(0.19, 0.188, 0.185);
uniform float oil_width : hint_range(0.0, 0.3) = 0.09;
uniform float oil_strength : hint_range(0.0, 1.0) = 0.4;
/// Black rubber scrubbed off by tyres on a turn, in the wheel tracks.
uniform vec3 rubber_colour : source_color = vec3(0.11, 0.108, 0.105);
uniform float rubber_strength : hint_range(0.0, 1.0) = 0.5;
uniform float roughness : hint_range(0.0, 1.0) = 0.9;
group_uniforms;

group_uniforms Scale;
uniform float detail_scale = 0.5;
uniform float macro_scale = 0.0625;
uniform float normal_strength : hint_range(0.0, 2.0) = 0.8;
group_uniforms;

void vertex() {
	ROAD_VERTEX
}

void fragment() {
	vec2 p = world_pos.xz;
	float dist = length(VERTEX);
	vec4 macro = texture(macro_tex, p * macro_scale);
	vec4 region = texture(macro_tex, p * macro_scale * 0.137 + vec2(0.31, 0.67));
	float across_fw = fwidth(UV2.x);
	float wear = UV2.y * mix(0.45, 1.25, region.b);
	float turn = UV.y;

	float f = UV2.x;
	float t = abs(abs(f) - track_offset);
	float sharp_track = 1.0 - smoothstep(track_width * 0.3, track_width, t);
	float sharp_oil = 1.0 - smoothstep(oil_width * 0.2, oil_width, abs(f));
	float lanes_visible = pixel_fade(across_fw, track_width);
	// Turning tyres smear sideways: wider tracks on a turn.
	float track = mix(track_width * 2.6, mix(sharp_track, 1.0 - smoothstep(track_width * 0.5, track_width * 1.8, t), turn * 0.6), lanes_visible);
	float oil = mix(oil_width * 1.2, sharp_oil, lanes_visible);
	track *= mix(0.55, 1.0, macro.g);
	oil *= mix(0.45, 1.0, region.r) * mix(0.7, 1.0, macro.g);
	// Fade at the ribbon's sides, where it meets the next lane's.
	float edge = 1.0 - smoothstep(0.42, 0.5, abs(f));

	float a_track = track * track_strength * wear * edge;
	float a_oil = oil * oil_strength * wear * edge;
	// Rubber in the tracks on a turn, in streaks.
	float rubber = track * turn * rubber_strength * wear * edge * smoothstep(0.3, 0.7, macro.r * 0.5 + macro.a * 0.5 + 0.2);
	vec3 track_tint = mix(track_colour, rubber_colour, clamp(rubber * 2.0, 0.0, 1.0));
	float alpha = clamp(a_track + a_oil + rubber * 0.5, 0.0, 1.0);
	vec3 albedo = (track_tint * (a_track + rubber * 0.5) + oil_colour * a_oil) / max(alpha, 1e-4);

	vec3 nmap = vec3(0.5, 0.5, 1.0);
	float near = near_fade(dist, detail_far);
	if (near > 0.0) nmap = mix(nmap, texture(detail_normal_tex, p * detail_scale).xyz, near * normal_strength);

	ALBEDO = albedo;
	ALPHA = alpha;
	ROUGHNESS = mix(roughness, track_roughness, clamp(track * wear, 0.0, 1.0));
	SPECULAR = 0.35;
	NORMAL_MAP = nmap;
}
