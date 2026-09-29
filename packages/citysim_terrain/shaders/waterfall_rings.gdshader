// Waterfall splash rings (WaterFalls.cs): a flat quad on the pool where a fall lands (UV 0..1 across it, the landing at
// the centre), one MultiMesh instance per ring set. INSTANCE_CUSTOM.x is a seed (so sets don't pulse in step), COLOR.a
// the fall's strength.
// Hard-edged ripple rings travel outward over a lighter patch of aerated water, broken up by noise. Look only:
// unshaded, no shadows, no depth writes. Like the water it's pulled a little toward the camera so the water surface
// (which is drawn slightly in front of where it is) doesn't hide it.
shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_disabled, shadows_disabled, unshaded, skip_vertex_transform;

uniform sampler2D ring_noise : hint_default_white, filter_linear_mipmap, repeat_enable;
uniform vec3 ring_color : source_color = vec3(0.78, 0.95, 0.95);
uniform float ring_alpha : hint_range(0.0, 1.0) = 0.9;
/// Rings across the radius, how thick each is (share of its spacing) and how fast they travel (rings per second).
uniform float ring_count = 5.0;
uniform float ring_width : hint_range(0.0, 1.0) = 0.22;
uniform float ring_speed = 0.3;
/// Rings thin out toward the rim by this much (0 = even width).
uniform float ring_taper : hint_range(0.0, 1.0) = 0.6;
/// How much noise breaks the rings into dashes and wobbles them (0 = perfect circles).
uniform float breakup : hint_range(0.0, 1.0) = 0.5;
uniform float wobble : hint_range(0.0, 1.0) = 0.3;
uniform float noise_scale = 1.5;
/// Where the rings end (share of the radius), ragged by noise.
uniform float ring_reach : hint_range(0.0, 1.0) = 0.8;
/// Aerated water around the splash: a lighter colour fading out from the centre.
uniform vec3 glow_color : source_color = vec3(0.45, 0.8, 0.8);
uniform float glow_alpha : hint_range(0.0, 1.0) = 0.45;
uniform float glow_radius : hint_range(0.0, 1.0) = 0.65;
/// The quad is drawn this far (m) toward the camera, to sit on top of the water.
uniform float depth_pull = 0.6;
/// Distance (m) from the camera where the rings start and end fading out (set by WaterFalls).
uniform vec2 fade_distance = vec2(250.0, 430.0);

varying float v_fade;
varying float v_seed;

void vertex() {
	v_seed = INSTANCE_CUSTOM.x;
	float cam_dist = length((MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz - CAMERA_POSITION_WORLD);
	v_fade = COLOR.a * (1.0 - smoothstep(fade_distance.x, fade_distance.y, cam_dist));
	vec4 view = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
	float d = length(view.xyz);
	view.xyz *= 1.0 - min(depth_pull, d * 0.5) / max(d, 1e-3);
	VERTEX = view.xyz;
	NORMAL = normalize((MODELVIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

void fragment() {
	vec2 p = UV * 2.0 - 1.0;
	float t = TIME + v_seed * 17.0;
	float r = length(p);
	if (r > 1.0) discard;
	vec2 dir = p / max(r, 1e-4);
	float n = texture(ring_noise, dir * noise_scale * 0.5 + vec2(r * 0.4 - t * 0.03, v_seed)).r;
	float wave = fract(r * ring_count - t * ring_speed + (n - 0.5) * wobble);
	float width = ring_width * (1.0 - ring_taper * r);
	float ring = step(1.0 - width, wave);
	ring *= step(breakup * 0.7, texture(ring_noise, p * noise_scale + vec2(0.37 + v_seed, t * 0.02)).r + 0.2);
	ring *= step(r, ring_reach * (0.8 + 0.4 * n));
	float glow = (1.0 - smoothstep(0.0, max(glow_radius, 1e-3), r)) * glow_alpha;
	ALBEDO = mix(glow_color, ring_color, ring);
	ALPHA = max(glow, ring * ring_alpha) * v_fade;
}
