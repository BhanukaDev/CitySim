// Waterfall curtains (WaterFalls.cs): one ribbon per lip cell, arcing off the lip. UV is (metres across the fall, metres
// fallen), UV2 is (0/1 across the ribbon, 0..1 down it), COLOR.a the fall's strength (its flux). Look only: unshaded,
// no shadows, no depth writes; white streaks scroll down over a milky sheet and fade at the lip, the foot, the ribbon's
// sides and with distance (fade_distance is set from the graphics quality). For a cel look: a narrow streak_edge gives
// hard streaks, bottom_mix lightens the sheet toward the foot, lip_foam/foot_foam add ragged white bands.
shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_disabled, shadows_disabled, unshaded;

uniform sampler2D streak_noise : hint_default_white, filter_linear_mipmap, repeat_enable;
uniform vec3 sheet_color : source_color = vec3(0.62, 0.82, 0.82);
uniform vec3 streak_color : source_color = vec3(0.96, 0.98, 0.98);
/// Sheet colour at the foot, and how far the sheet moves to it on the way down (0 = one colour).
uniform vec3 sheet_color_bottom : source_color = vec3(0.8, 0.93, 0.93);
uniform float bottom_mix : hint_range(0.0, 1.0) = 0.0;
/// Noise range that becomes a streak: wide = soft, narrow (e.g. 0.6, 0.62) = hard cel edges.
uniform vec2 streak_edge = vec2(0.45, 0.75);
/// Opacity of the sheet between streaks, and of the streaks.
uniform float sheet_alpha : hint_range(0.0, 1.0) = 0.35;
uniform float streak_alpha : hint_range(0.0, 1.0) = 0.9;
/// Streak size (m) across and down the fall.
uniform vec2 streak_scale = vec2(1.6, 9.0);
/// How fast the streaks fall (m/s); two layers, the second a bit faster.
uniform float fall_speed = 7.0;
/// Fraction of the curtain faded in at the lip and out at the foot.
uniform float top_fade : hint_range(0.0, 0.5) = 0.08;
uniform float foot_fade : hint_range(0.0, 0.5) = 0.25;
/// Fraction of each ribbon faded at its sides (ribbons overlap, so small values still blend into one sheet).
uniform float side_fade : hint_range(0.0, 0.5) = 0.2;
group_uniforms Foam;
uniform vec3 foam_color : source_color = vec3(0.96, 0.98, 0.98);
/// Share of the fall covered by a ragged white band at the lip and at the foot (0 = none).
uniform float lip_foam : hint_range(0.0, 0.5) = 0.0;
uniform float foot_foam : hint_range(0.0, 0.5) = 0.0;
/// Softness of the foam's edge (0 = hard).
uniform float foam_edge : hint_range(0.0, 0.2) = 0.01;
/// Foam blob size (m) across and down the fall.
uniform vec2 foam_scale = vec2(2.5, 3.0);
group_uniforms;
/// Distance (m) from the camera where the curtain starts and ends fading out (set by WaterFalls).
uniform vec2 fade_distance = vec2(420.0, 665.0);

varying float v_dist;

void vertex() {
	v_dist = length((MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz - CAMERA_POSITION_WORLD);
}

void fragment() {
	vec2 uv = vec2(UV.x / streak_scale.x, UV.y / streak_scale.y);
	float a = texture(streak_noise, uv - vec2(0.0, TIME * fall_speed / streak_scale.y)).r;
	float b = texture(streak_noise, uv * vec2(1.7, 0.6) + vec2(0.37, -TIME * fall_speed * 1.3 / streak_scale.y)).r;
	float streak = smoothstep(streak_edge.x, max(streak_edge.y, streak_edge.x + 1e-3), a * 0.6 + b * 0.5);
	float along = UV2.y;
	float fade = smoothstep(0.0, max(top_fade, 1e-3), along) * (1.0 - smoothstep(1.0 - max(foot_fade, 1e-3), 1.0, along));
	float sf = max(side_fade, 1e-3);
	fade *= smoothstep(0.0, sf, UV2.x) * smoothstep(1.0, 1.0 - sf, UV2.x);
	fade *= 1.0 - smoothstep(fade_distance.x, fade_distance.y, v_dist);
	// Seen edge-on a sheet of water is thin: fade it.
	fade *= mix(0.35, 1.0, abs(dot(NORMAL, VIEW)));
	// Ragged foam bands: the band's edge wobbles with noise that churns slowly at the lip and fast at the foot.
	vec2 fp = UV / foam_scale;
	float nl = texture(streak_noise, fp * 0.5 + vec2(0.13, -TIME * 0.15)).r;
	float nf = texture(streak_noise, fp + vec2(0.71, -TIME * fall_speed * 0.08)).r;
	float e = max(foam_edge, 1e-3);
	float foam = lip_foam > 0.0 ? 1.0 - smoothstep(-e, e, along - lip_foam * (0.3 + 1.4 * nl)) : 0.0;
	foam = max(foam, foot_foam > 0.0 ? smoothstep(-e, e, along - (1.0 - foot_foam * (0.3 + 1.4 * nf))) : 0.0);
	vec3 sheet = mix(sheet_color, sheet_color_bottom, bottom_mix * along);
	ALBEDO = mix(mix(sheet, streak_color, streak), foam_color, foam);
	ALPHA = clamp(max(mix(sheet_alpha, streak_alpha, streak), foam) * fade * COLOR.a, 0.0, 1.0);
}
