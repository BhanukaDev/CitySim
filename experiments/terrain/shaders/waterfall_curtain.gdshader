// Waterfall curtains (WaterFalls.cs): one ribbon per lip cell, arcing off the lip. UV is (metres across the fall, metres
// fallen), UV2 is (0/1 across the ribbon, 0..1 down it), COLOR.a the fall's strength (its flux). Look only: unshaded,
// no shadows, no depth writes; white streaks scroll down over a milky sheet and fade at the lip, the foot, the ribbon's
// sides and with distance (fade_distance is set from the graphics quality).
shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_disabled, shadows_disabled, unshaded;

uniform sampler2D streak_noise : hint_default_white, filter_linear_mipmap, repeat_enable;
uniform vec3 sheet_color : source_color = vec3(0.62, 0.82, 0.82);
uniform vec3 streak_color : source_color = vec3(0.96, 0.98, 0.98);
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
	float streak = smoothstep(0.45, 0.75, a * 0.6 + b * 0.5);
	float along = UV2.y;
	float fade = smoothstep(0.0, top_fade, along) * (1.0 - smoothstep(1.0 - foot_fade, 1.0, along));
	fade *= smoothstep(0.0, 0.2, UV2.x) * smoothstep(1.0, 0.8, UV2.x);
	fade *= 1.0 - smoothstep(fade_distance.x, fade_distance.y, v_dist);
	// Seen edge-on a sheet of water is thin: fade it.
	fade *= mix(0.35, 1.0, abs(dot(NORMAL, VIEW)));
	ALBEDO = mix(sheet_color, streak_color, streak);
	ALPHA = clamp(mix(sheet_alpha, streak_alpha, streak) * fade * COLOR.a, 0.0, 1.0);
}
