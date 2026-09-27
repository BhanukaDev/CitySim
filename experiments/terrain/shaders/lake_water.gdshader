// Lake surface: flat quads at each lake's level (LakeWater.cs). Colour and opacity come from the water depth under each
// pixel (depth buffer vs. the surface), so shorelines fade out on their own and deep water turns dark. Small moving
// ripples in the normal give the sun something to glint on.
shader_type spatial;
render_mode blend_mix, depth_draw_always, cull_back, specular_schlick_ggx, shadows_disabled;

uniform sampler2D depth_texture : hint_depth_texture, filter_nearest, repeat_disable;

group_uniforms Colour;
uniform vec3 shallow_color : source_color = vec3(0.36, 0.62, 0.62);
uniform vec3 deep_color : source_color = vec3(0.07, 0.22, 0.34);
/// Depth (m) at which the water reaches deep_color.
uniform float deep_depth = 6.0;
/// Depth (m) over which the shoreline fades in.
uniform float shore_fade = 0.6;
uniform float min_alpha : hint_range(0.0, 1.0) = 0.8;
uniform float max_alpha : hint_range(0.0, 1.0) = 0.97;
group_uniforms Surface;
uniform float roughness_value : hint_range(0.0, 1.0) = 0.06;
uniform float ripple_strength : hint_range(0.0, 1.0) = 0.12;
uniform float ripple_scale = 9.0;
uniform float ripple_speed = 0.35;
/// Ripples fade out between these distances (m), before they alias into a pattern.
uniform vec2 ripple_fade = vec2(120.0, 450.0);
group_uniforms;

varying vec3 world_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

// Slope of a few crossing sine waves: cheap ripples without a texture.
vec2 ripple_slope(vec2 p, float t) {
	vec2 s = vec2(0.0);
	s += vec2(0.8, 0.6) * cos(dot(p, vec2(0.8, 0.6)) + t * 1.3);
	s += vec2(-0.5, 0.86) * cos(dot(p, vec2(-0.5, 0.86)) * 1.7 + t * 1.7) * 0.6;
	s += vec2(0.97, -0.26) * cos(dot(p, vec2(0.97, -0.26)) * 2.9 + t * 2.3) * 0.35;
	s += vec2(-0.3, -0.95) * cos(dot(p, vec2(-0.3, -0.95)) * 4.3 + t * 2.9) * 0.2;
	return s;
}

void fragment() {
	// Ground behind this pixel, in view space.
	float raw = texture(depth_texture, SCREEN_UV).r;
	vec4 ground = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, raw, 1.0);
	ground.xyz /= ground.w;
	// Distance through the water along the view ray, scaled to roughly vertical depth by the view angle.
	float through = max(VERTEX.z - ground.z, 0.0);
	float depth = through * abs(dot(normalize(-VERTEX), NORMAL));

	float t = clamp(depth / deep_depth, 0.0, 1.0);
	ALBEDO = mix(shallow_color, deep_color, sqrt(t));
	ALPHA = mix(min_alpha, max_alpha, t) * smoothstep(0.0, shore_fade, depth);
	ROUGHNESS = roughness_value;
	METALLIC = 0.0;
	SPECULAR = 0.6;

	float fade = 1.0 - smoothstep(ripple_fade.x, ripple_fade.y, length(VERTEX));
	vec2 s = ripple_slope(world_pos.xz / ripple_scale, TIME * ripple_speed * 6.2831853) * ripple_strength * fade;
	vec3 n_world = normalize(vec3(-s.x, 1.0, -s.y));
	NORMAL = normalize((VIEW_MATRIX * vec4(n_world, 0.0)).xyz);
}
