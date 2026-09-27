// Simulated water (WaterSurface.cs): a grid per tile, lifted to the water surface from water_data (per cell: surface,
// depth, velocity x, velocity z; see cs_water_read in native/water/water.h). Cells two or more away from water are
// hidden (depth -1); dry cells next to water reach into the bank so the ground cuts the shoreline.
// Colour and opacity come from the water depth under each pixel (depth buffer vs. the surface). Ripples are dragged
// along the flow (two-phase flow map), and fast or steep water (rapids, the lip of a fall) turns to foam.
shader_type spatial;
render_mode blend_mix, depth_draw_always, cull_back, specular_schlick_ggx, shadows_disabled;

uniform sampler2D depth_texture : hint_depth_texture, filter_nearest, repeat_disable;
/// Set per page by WaterSurface; tile_offset (the tile's corner in the page, metres) and lod_step per tile.
uniform sampler2D water_data : filter_nearest, repeat_disable;
uniform float cell_size = 3.5;
/// Map size in metres (set by WaterSurface): the water fades out toward the border, into the terrain's edge fog.
uniform vec2 map_size = vec2(3584.0);
uniform float edge_fade = 250.0;
instance uniform vec2 tile_offset;
instance uniform int lod_step = 1;

group_uniforms Colour;
uniform vec3 shallow_color : source_color = vec3(0.36, 0.62, 0.62);
uniform vec3 deep_color : source_color = vec3(0.07, 0.22, 0.34);
/// Depth (m) at which the water reaches deep_color.
uniform float deep_depth = 6.0;
/// Depth (m) over which the shoreline fades in.
uniform float shore_fade = 0.6;
/// Simulated depth (m) over which thin films of running water (rain-like sheets on slopes) fade in.
uniform vec2 film_fade = vec2(0.02, 0.08);
uniform float min_alpha : hint_range(0.0, 1.0) = 0.8;
uniform float max_alpha : hint_range(0.0, 1.0) = 0.97;
group_uniforms Surface;
uniform float roughness_value : hint_range(0.0, 1.0) = 0.06;
uniform float ripple_strength : hint_range(0.0, 1.0) = 0.12;
uniform float ripple_scale = 9.0;
uniform float ripple_speed = 0.35;
/// Ripples fade out between these distances (m), before they alias into a pattern.
uniform vec2 ripple_fade = vec2(120.0, 450.0);
group_uniforms Flow;
/// Seconds per flow-map phase: ripples slide along the flow this long before fading into a fresh copy.
uniform float flow_cycle = 1.2;
/// How far ripples slide per second, relative to the water speed.
uniform float flow_drag = 0.6;
/// Extra ripple strength per m/s of flow (faster water looks rougher).
uniform float flow_roughen = 0.25;
group_uniforms Foam;
uniform vec3 foam_color : source_color = vec3(0.9, 0.94, 0.95);
/// Water speed (m/s) where foam starts and where it's full.
uniform vec2 foam_speed = vec2(2.5, 7.0);
/// Surface slope (rise over run) where foam starts and where it's full: rapids and waterfalls.
uniform vec2 foam_slope = vec2(0.12, 0.5);
uniform float foam_scale = 3.0;
group_uniforms;

varying vec3 world_pos;
varying vec3 surface_normal;
varying vec2 flow;
varying float wet;
varying float slope;
varying float sim_depth;

vec4 cell(ivec2 c) {
	return texelFetch(water_data, clamp(c, ivec2(0), textureSize(water_data, 0) - 1), 0);
}

void vertex() {
	ivec2 c = ivec2(round((VERTEX.xz + tile_offset) / cell_size));
	vec4 d = cell(c);
	VERTEX.y = d.r;
	wet = d.g >= 0.0 ? 1.0 : 0.0;
	sim_depth = max(d.g, 0.0);
	flow = d.ba;
	// Surface normal from neighbours that hold water (dry ones would tilt it toward the bank).
	ivec2 s = ivec2(lod_step, 0);
	vec4 l = cell(c - s.xy), r = cell(c + s.xy), t = cell(c - s.yx), b = cell(c + s.yx);
	float hl = l.g > 0.0 ? l.r : d.r, hr = r.g > 0.0 ? r.r : d.r;
	float ht = t.g > 0.0 ? t.r : d.r, hb = b.g > 0.0 ? b.r : d.r;
	float span = 2.0 * float(lod_step) * cell_size;
	vec2 grad = vec2(hr - hl, hb - ht) / span;
	slope = length(grad);
	surface_normal = normalize(vec3(-grad.x, 1.0, -grad.y));
	NORMAL = surface_normal;
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

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float value_noise(vec2 p) {
	vec2 i = floor(p), f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1, 0)), u.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), u.x), u.y);
}

void fragment() {
	if (wet < 0.999) discard;

	// Ground behind this pixel, in view space.
	float raw = texture(depth_texture, SCREEN_UV).r;
	vec4 ground = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, raw, 1.0);
	ground.xyz /= ground.w;
	// Distance through the water along the view ray, scaled to roughly vertical depth by the view angle.
	float through = max(VERTEX.z - ground.z, 0.0);
	float depth = through * abs(dot(normalize(-VERTEX), NORMAL));

	float t = clamp(depth / deep_depth, 0.0, 1.0);
	vec3 col = mix(shallow_color, deep_color, sqrt(t));
	float alpha = mix(min_alpha, max_alpha, t) * smoothstep(0.0, shore_fade, depth);
	// Lake shores keep their look (the bank vertex has depth 0 but the water beside it is deep); thin sheets fade out.
	float film = smoothstep(film_fade.x, film_fade.y, sim_depth + step(0.5, depth) * film_fade.y);
	alpha *= film;

	// Two-phase flow map: two copies of the ripples slide along the flow, each fading out as it resets.
	float speed = length(flow);
	float phase = TIME / flow_cycle;
	float p0 = fract(phase), p1 = fract(phase + 0.5);
	float w0 = 1.0 - abs(1.0 - 2.0 * p0);
	vec2 drift = flow * flow_drag * flow_cycle / ripple_scale;
	vec2 base = world_pos.xz / ripple_scale;
	float wave_t = TIME * ripple_speed * 6.2831853;
	vec2 s = mix(ripple_slope(base - drift * p1 + vec2(3.7, 1.3), wave_t), ripple_slope(base - drift * p0, wave_t), w0);
	float fade = 1.0 - smoothstep(ripple_fade.x, ripple_fade.y, length(VERTEX));
	s *= ripple_strength * (1.0 + flow_roughen * min(speed, 6.0)) * fade;

	// Foam on fast or steep water, broken up by noise that moves with the flow.
	// Slope foam needs moving water (a still surface can't be steep for long) and some depth.
	float foam = max(smoothstep(foam_speed.x, foam_speed.y, speed),
		smoothstep(foam_slope.x, foam_slope.y, slope) * smoothstep(0.5, 2.0, speed));
	foam *= smoothstep(0.05, 0.4, depth);
	vec2 fp = world_pos.xz / foam_scale;
	float breakup = mix(value_noise(fp - flow * TIME * 0.35 / foam_scale), value_noise(fp * 2.3 + vec2(TIME * 0.2)), 0.4);
	foam *= smoothstep(0.35, 0.75, breakup + foam * 0.3);
	col = mix(col, foam_color, foam);
	alpha = max(alpha, foam * 0.95 * film);
	vec2 to_edge = min(world_pos.xz, map_size - world_pos.xz);
	alpha *= smoothstep(0.0, edge_fade, min(to_edge.x, to_edge.y));

	ALBEDO = col;
	ALPHA = alpha;
	ROUGHNESS = mix(roughness_value, 0.6, foam);
	METALLIC = 0.0;
	SPECULAR = 0.6;
	vec3 n_world = normalize(surface_normal + vec3(-s.x, 0.0, -s.y));
	NORMAL = normalize((VIEW_MATRIX * vec4(n_world, 0.0)).xyz);
}
