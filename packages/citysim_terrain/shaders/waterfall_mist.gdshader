// Waterfall mist (WaterFalls.cs): camera-facing puffs animated entirely here, one MultiMesh for all of them.
// INSTANCE_CUSTOM = (seed 0..1, size m, strength 0..1, height of the puff's origin above the water/ground m). Each puff
// loops: it grows, rises and drifts, fading in and out; the seed sets its phase, drift, tilt and which of the atlas's
// four puffs it draws (assets/particles/mist_puffs.png, tools/fetch_particles.sh), so a refresh with the same seeds
// doesn't restart it. Soft against the ground (depth texture) and the water it sits on, and fades with distance.
shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_disabled, shadows_disabled, unshaded, skip_vertex_transform;

uniform sampler2D depth_texture : hint_depth_texture, filter_nearest, repeat_disable;
/// 2×2 atlas of greyscale puffs (brightness = opacity).
uniform sampler2D puff_atlas : hint_default_white, filter_linear_mipmap, repeat_disable;
uniform vec3 mist_color : source_color = vec3(0.9, 0.95, 0.97);
uniform float mist_alpha : hint_range(0.0, 1.0) = 0.45;
/// The atlas's brightness mapped to opacity: below x clear, above y solid.
uniform vec2 puff_contrast = vec2(0.0, 0.85);
/// Puffs are this much wider than tall, so they lie low over the water as a bank.
uniform float stretch = 1.6;
/// Seconds per puff loop (± 30 % per puff).
uniform float life = 6.0;
/// Rise and downstream drift over a loop, in puff sizes.
uniform float rise = 0.3;
uniform float drift = 0.9;
/// Metres over which a puff fades into the ground (depth buffer) and into the surface it sits on.
uniform float soft_distance = 2.0;
/// Distance (m) from the camera where the mist starts and ends fading out (set by WaterFalls).
uniform vec2 fade_distance = vec2(250.0, 430.0);

varying float v_alpha;
varying vec3 v_world;
varying float v_floor;
varying vec2 v_noise; // atlas UV

void vertex() {
	vec4 c = INSTANCE_CUSTOM;
	float seed = c.x;
	float period = life * (0.7 + 0.6 * fract(seed * 13.7));
	float phase = fract(TIME / period + seed * 7.31);
	vec3 origin = MODEL_MATRIX[3].xyz;
	// The instance's +Z is downstream: puffs billow out over the pool, spreading a little sideways.
	vec3 down = normalize(MODEL_MATRIX[2].xyz);
	vec3 side = normalize(MODEL_MATRIX[0].xyz);
	vec3 move = (down * drift + side * (fract(seed * 5.3) - 0.5) * drift + vec3(0.0, rise, 0.0)) * c.y * phase;
	float size = c.y * (0.6 + 0.8 * phase);
	// Spin each puff by its seed so they don't all look alike.
	// A small tilt per puff (not a full spin, which would undo the stretch), and a flip so shapes repeat less.
	float spin = (fract(seed * 3.7) - 0.5) * 0.6 + phase * 0.15;
	vec2 corner = mat2(vec2(cos(spin), sin(spin)), vec2(-sin(spin), cos(spin))) * (VERTEX.xy * vec2(stretch, 1.0));
	vec3 world = origin + move + (INV_VIEW_MATRIX[0].xyz * corner.x + INV_VIEW_MATRIX[1].xyz * corner.y) * size;
	world.y += size * 0.15; // hug the surface: mostly above it, the bottom fading into it
	v_world = world;
	v_floor = origin.y - c.w;
	float frame = floor(fract(seed * 23.17) * 4.0);
	vec2 uv = fract(seed * 9.1) > 0.5 ? vec2(1.0 - UV.x, UV.y) : UV;
	v_noise = (uv + vec2(mod(frame, 2.0), floor(frame / 2.0))) * 0.5;
	float dist = length(world - CAMERA_POSITION_WORLD);
	v_alpha = c.z * sqrt(sin(PI * phase)) * (1.0 - smoothstep(fade_distance.x, fade_distance.y, dist));
	VERTEX = (VIEW_MATRIX * vec4(world, 1.0)).xyz;
	NORMAL = vec3(0.0, 0.0, 1.0);
}

void fragment() {
	float shape = smoothstep(puff_contrast.x, puff_contrast.y, texture(puff_atlas, v_noise).r);
	// Soft particle: fade where the puff meets the ground behind it.
	float scene = texture(depth_texture, SCREEN_UV).r;
	vec4 view = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, scene, 1.0);
	float soft = clamp((VERTEX.z - view.z / view.w) / soft_distance, 0.0, 1.0);
	// ...and into the water surface or ground the puff rises from (the water doesn't write the depth texture).
	soft *= smoothstep(0.0, soft_distance, v_world.y - v_floor);
	ALBEDO = mist_color;
	ALPHA = clamp(shape * v_alpha * mist_alpha * soft, 0.0, 1.0);
}
