// Simulated water (WaterSurface.cs), stylized. A grid per tile is lifted to the water surface from water_data (per
// cell: surface, depth, velocity x, velocity z; see cs_water_read in native/water/water.h), sampled bilinearly so
// the surface and the shoreline are smoother than the sim grid. Cells two or more away from water are hidden
// (depth -1); dry cells next to water reach into the bank so the ground cuts the shoreline.
// water_prev holds the previous sim tick; `blend` moves from it to water_data between ticks.
//
// Colour comes from absorption: the ground behind the water (screen texture, slightly refracted) fades into the
// water tint and then the deep colour as the water gets deeper, so even a 30 cm stream reads as water. Depth is the
// simulated depth, not the depth buffer, so the look doesn't change with the terrain's distance LODs. Caustics
// play on the bed, foam rings the shore, breakers roll in over shallow water, and fast or steep water (rapids, falls) turns milky
// with white streaks. Far away the water is drawn a little in front of the ground so coarse terrain LODs don't swallow
// it. Direct light is toon-shaded with crisp sun glints; sky reflections are kept low.
shader_type spatial;
render_mode blend_mix, depth_draw_always, cull_back, shadows_disabled;

uniform sampler2D depth_texture : hint_depth_texture, filter_nearest, repeat_disable;
uniform sampler2D screen_texture : hint_screen_texture, filter_linear_mipmap, repeat_disable;
/// Set per page by WaterSurface; tile_offset (the tile's corner in the page, metres) and lod_step per tile.
uniform sampler2D water_data : filter_nearest, repeat_disable;
uniform sampler2D water_prev : filter_nearest, repeat_disable;
/// 0 = the previous sim tick (water_prev), 1 = the latest (water_data).
uniform float blend = 1.0;
/// Pollutant concentration per cell (kg/m³), same layout as water_data.
uniform sampler2D pollution : hint_default_black, filter_linear, repeat_disable;
uniform float cell_size = 3.5;
/// Map size in metres (set by WaterSurface): the water fades out toward the border, into the terrain's edge fog.
uniform vec2 map_size = vec2(3584.0);
uniform float edge_fade = 250.0;
instance uniform vec2 tile_offset;
/// Grid spacing of this tile's mesh, in cells (0.5 = two vertices per cell).
instance uniform float lod_step = 1.0;

group_uniforms Colour;
/// Colour of the light scattered back by the water (the first metre or so).
uniform vec3 water_tint : source_color = vec3(0.10, 0.38, 0.36);
/// Colour of deep water.
uniform vec3 deep_color : source_color = vec3(0.02, 0.11, 0.17);
/// How fast the ground behind fades into water_tint, per metre (2.5: half gone after 30 cm, 92 % after 1 m).
uniform float absorption = 2.5;
/// Tint that even the thinnest drawn water gets, so shallow streams never look like bare ground.
uniform float min_tint : hint_range(0.0, 1.0) = 0.4;
/// How fast water_tint turns into deep_color, per metre.
uniform float deep_absorption = 0.22;
/// Soft toon steps in the depth colour (0 = smooth).
uniform float color_bands = 4.0;
/// How much the slanted path through the water counts (0 = vertical depth only; grazing views get darker).
uniform float view_path = 0.35;
/// Depth (m) over which the shoreline fades in.
uniform float shore_fade = 0.12;
/// Simulated depth (m) over which thin films of running water (rain-like sheets on slopes) fade in.
uniform vec2 film_fade = vec2(0.02, 0.08);
/// Small streams run as sheets a few cm deep, which the fades above would hide except where they pool (blobs). Water
/// carrying this much (depth × speed, m²/s: start, full) shows anyway, coloured as if this deep (m).
uniform vec2 stream_flux = vec2(0.001, 0.008);
uniform float stream_depth = 0.3;
/// How far the ground behind is shifted by the ripples (screen UV per unit of normal).
uniform float refraction = 0.025;
/// Murky colour of polluted water, and the concentration (kg/m³) where it takes over fully.
uniform vec3 pollution_color : source_color = vec3(0.45, 0.38, 0.17);
uniform float pollution_full = 0.05;
group_uniforms Surface;
/// Sky reflection (0.5 = physically based water). Low keeps it from looking shiny.
uniform float reflection : hint_range(0.0, 1.0) = 0.22;
uniform float roughness_value : hint_range(0.0, 1.0) = 0.2;
/// 0 = soft (Lambert) light on the water, 1 = two-tone toon.
uniform float toon : hint_range(0.0, 1.0) = 0.7;
uniform vec3 glint_color : source_color = vec3(1.0, 0.97, 0.88);
uniform float glint_strength = 1.5;
/// Higher = smaller, sharper sun sparkles.
uniform float glint_size : hint_range(0.9, 1.0) = 0.993;
/// Share of the surface that can sparkle (0 = none, 1 = one mirror-like highlight).
uniform float glint_density : hint_range(0.0, 1.0) = 0.3;
/// Scrolling detail normals (a seamless normal map, e.g. a NoiseTexture2D with As Normal Map).
uniform sampler2D detail_normal : hint_normal, filter_linear_mipmap, repeat_enable;
uniform float ripple_strength : hint_range(0.0, 2.0) = 0.45;
/// Metres per repeat of detail_normal.
uniform float ripple_scale = 18.0;
/// Wind drift of the ripples on still water, m/s.
uniform float ripple_speed = 0.5;
/// Ripples fade out between these distances (m), before they alias into a pattern.
uniform vec2 ripple_fade = vec2(150.0, 600.0);
group_uniforms Flow;
/// Seconds per flow-map phase: ripples slide along the flow this long before fading into a fresh copy.
uniform float flow_cycle = 1.6;
/// How far ripples and foam slide per second, relative to the water speed.
uniform float flow_drag = 0.6;
/// Extra ripple strength per m/s of flow (faster water looks rougher).
uniform float flow_roughen = 0.25;
group_uniforms Caustics;
uniform vec3 caustic_color : source_color = vec3(1.0, 0.98, 0.85);
uniform float caustic_strength = 0.6;
/// Metres per caustic cell.
uniform float caustic_scale = 5.0;
uniform float caustic_speed = 0.6;
/// Caustics fade in over the first depth and out by the second (m).
uniform vec2 caustic_depth = vec2(0.2, 3.0);
/// Caustics fade out between these camera distances (m).
uniform vec2 caustic_fade = vec2(120.0, 450.0);
group_uniforms Foam;
uniform vec3 foam_color : source_color = vec3(0.95, 0.97, 0.97);
/// Foam glows a little so it stays white in shade and at grazing angles.
uniform float foam_glow = 0.35;
/// Seamless noise that breaks foam up (e.g. a NoiseTexture2D).
uniform sampler2D foam_noise : hint_default_white, filter_linear_mipmap, repeat_enable;
/// Metres per repeat of foam_noise.
uniform float foam_scale = 14.0;
/// Depth (m) of the white band along the shore.
uniform float shore_foam_width = 0.18;
/// Foam lines that pulse out from the shore: how far out (m of depth) and how fast.
uniform float foam_line_depth = 0.6;
uniform float foam_line_speed = 0.8;
group_uniforms White_water;
/// Water speed (m/s) where white water starts and where it's full.
uniform vec2 foam_speed = vec2(2.0, 5.5);
/// Surface slope (rise over run) where white water starts and where it's full: rapids and waterfalls.
uniform vec2 foam_slope = vec2(0.1, 0.4);
/// Milky colour of aerated water under the white streaks, and how much of it full white water gets.
uniform vec3 whitewater_color : source_color = vec3(0.55, 0.78, 0.76);
uniform float whitewater_tint : hint_range(0.0, 1.0) = 0.6;
/// Share of full white water covered by foam streaks (the rest shows the milky water between them).
uniform float whitewater_coverage : hint_range(0.0, 1.0) = 0.5;
/// Streaks are stretched this much along the flow.
uniform float foam_streak = 6.0;
/// Metres per repeat of the streak pattern (across the flow).
uniform float whitewater_scale = 6.0;
/// Streaks are laid out per cell of this size (m), each rigid along the flow at its centre, and cross-faded into its
/// neighbours. Smaller follows bends better; larger keeps longer streaks.
uniform float whitewater_cell = 8.0;
group_uniforms Depth_bias;
/// The water is drawn this far (m) in front of where it is, so thin sheets on steep ground (a fall's face, a fast
/// stream) don't hide inside the ground's triangles up close. Only the depth moves, not the pixel.
uniform float near_bias = 0.3;
/// Beyond this distance (m) the water is drawn a little more in front of the ground, by this share of the distance,
/// so the coarser far terrain doesn't swallow narrow streams and falls.
uniform float far_bias_start = 250.0;
uniform float far_bias = 0.012;
group_uniforms Waves;
/// White breaker lines rolling toward the shore over shallow, calm water.
uniform float breaker_strength : hint_range(0.0, 1.0) = 0.8;
/// Breakers appear where the water is shallower than this (m).
uniform float breaker_depth = 1.6;
/// Crests per metre of depth, and how fast they roll in.
uniform float breaker_frequency = 0.9;
uniform float breaker_speed = 0.25;
/// Sparse whitecaps on deep open water, on the crests of wind waves.
uniform float whitecap_strength : hint_range(0.0, 1.0) = 0.8;
/// Share of the crests that break white.
uniform float whitecap_coverage : hint_range(0.0, 1.0) = 0.18;
/// Wind direction (x, z) and the whitecap wave length in metres.
uniform vec2 wind_direction = vec2(0.8, 0.6);
uniform float wave_length = 22.0;
group_uniforms;

varying vec3 surface_normal;
varying vec2 page_pos;
varying float slope;
// Set in fragment for light(): where sun glints may show.
varying float sparkle;

vec4 cell(ivec2 c) {
	c = clamp(c, ivec2(0), textureSize(water_data, 0) - 1);
	return mix(texelFetch(water_prev, c, 0), texelFetch(water_data, c, 0), blend);
}

// Bilinear water at p (in cells from the page corner) over the cells that are drawn (depth >= 0), so the surface
// doesn't dip toward hidden cells. vis is the bilinear share of drawn cells: the shoreline sits where it crosses 0.5.
vec4 sample_water(vec2 p, out float vis) {
	vec2 b = floor(p), f = p - b;
	ivec2 c = ivec2(b);
	vec4 a = cell(c), r = cell(c + ivec2(1, 0)), t = cell(c + ivec2(0, 1)), u = cell(c + ivec2(1, 1));
	vec4 w = vec4((1.0 - f.x) * (1.0 - f.y), f.x * (1.0 - f.y), (1.0 - f.x) * f.y, f.x * f.y);
	vec4 on = step(vec4(0.0), vec4(a.g, r.g, t.g, u.g));
	vis = dot(w, on);
	// The small bias lets a point right on a hidden cell take its drawn neighbours' surface.
	vec4 wv = (w + 1e-3) * on;
	float sum = wv.x + wv.y + wv.z + wv.w;
	if (sum <= 0.0) return vec4(min(min(a.r, r.r), min(t.r, u.r)), -1.0, 0.0, 0.0);
	return (a * wv.x + r * wv.y + t * wv.z + u * wv.w) / sum;
}

void vertex() {
	page_pos = VERTEX.xz + tile_offset;
	float vis;
	vec2 pc = page_pos / cell_size;
	VERTEX.y = sample_water(pc, vis).r;
	if (lod_step > 1.5) {
		// A coarse grid cuts the chord across convex water (the lip of a fall) and sinks it into the ground; the highest
		// water around each vertex keeps it on top.
		float h = lod_step * 0.5;
		VERTEX.y = max(VERTEX.y, max(max(sample_water(pc + vec2(h, h), vis).r, sample_water(pc + vec2(-h, h), vis).r),
			max(sample_water(pc + vec2(h, -h), vis).r, sample_water(pc + vec2(-h, -h), vis).r)));
	}
	// Surface normal from the surface one grid step to each side.
	float e = max(lod_step, 1.0), v;
	vec2 p = page_pos / cell_size;
	float hl = sample_water(p - vec2(e, 0.0), v).r, hr = sample_water(p + vec2(e, 0.0), v).r;
	float ht = sample_water(p - vec2(0.0, e), v).r, hb = sample_water(p + vec2(0.0, e), v).r;
	vec2 grad = vec2(hr - hl, hb - ht) / (2.0 * e * cell_size);
	slope = length(grad);
	surface_normal = normalize(vec3(-grad.x, 1.0, -grad.y));
	NORMAL = surface_normal;
	// Pull the water toward the camera along the view ray (same pixel, nearer depth): a little everywhere, more far
	// away, so neither fine nor coarse terrain triangles hide it. The fragment still sees the true position.
	vec4 view = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
	float d = length(view.xyz);
	float pull = min(near_bias + far_bias * max(d - far_bias_start, 0.0), d * 0.5);
	view.xyz *= 1.0 - pull / max(d, 1e-3);
	POSITION = PROJECTION_MATRIX * view;
}

vec2 hash2(vec2 p) {
	return fract(sin(vec2(dot(p, vec2(127.1, 311.7)), dot(p, vec2(269.5, 183.3)))) * 43758.5453);
}

// Bright lines along the edges of moving Voronoi cells: cheap stylized caustics.
float caustic_layer(vec2 p, float t) {
	vec2 i = floor(p), f = fract(p);
	float d1 = 8.0, d2 = 8.0;
	for (int y = -1; y <= 1; y++)
		for (int x = -1; x <= 1; x++) {
			vec2 g = vec2(float(x), float(y));
			vec2 o = 0.5 + 0.45 * sin(t + 6.2831853 * hash2(i + g));
			vec2 r = g + o - f;
			float d = dot(r, r);
			if (d < d1) { d2 = d1; d1 = d; }
			else if (d < d2) d2 = d;
		}
	return 1.0 - smoothstep(0.0, 0.12, sqrt(d2) - sqrt(d1));
}

// White-water streaks of one cell: noise stretched along the flow v at the cell's centre and carried by the two-phase
// flow map. Rotating around the cell's own centre (not the world origin) keeps a small change of direction a small
// change of pattern, so the streaks don't fan out or shear across a fall.
// dx, dy: screen derivatives of x. The noise coordinates jump between cells, so the mip level comes from these.
float streak_cell(vec2 x, vec2 dx, vec2 dy, vec2 centre, vec2 v, float p0, float p1, float w0) {
	float speed = length(v);
	vec2 dir = speed > 0.05 ? v / speed : vec2(0.0, 1.0), perp = vec2(-dir.y, dir.x);
	vec2 drift = v * flow_drag * flow_cycle;
	vec2 q0 = x - centre - drift * p0, q1 = x - centre - drift * p1;
	// Each cell gets its own part of the noise, so neighbours don't repeat.
	vec2 seed = hash2(centre);
	vec2 a0 = vec2(dot(q0, perp), dot(q0, dir) / foam_streak) / whitewater_scale + seed;
	vec2 a1 = vec2(dot(q1, perp), dot(q1, dir) / foam_streak) / whitewater_scale + seed + vec2(0.5, 0.21);
	vec2 gx = vec2(dot(dx, perp), dot(dx, dir) / foam_streak) / whitewater_scale;
	vec2 gy = vec2(dot(dy, perp), dot(dy, dir) / foam_streak) / whitewater_scale;
	float s0 = textureGrad(foam_noise, a0, gx, gy).r * 0.65 + textureGrad(foam_noise, a0 * 2.3 + 0.37, gx * 2.3, gy * 2.3).r * 0.35;
	float s1 = textureGrad(foam_noise, a1, gx, gy).r * 0.65 + textureGrad(foam_noise, a1 * 2.3 + 0.61, gx * 2.3, gy * 2.3).r * 0.35;
	// Cross-fading two noises flattens them toward 0.5; stretch back so the streaks keep their contrast.
	return (mix(s1, s0, w0) - 0.5) / mix(0.7, 1.0, abs(2.0 * w0 - 1.0));
}

// Soft toon steps: flat bands joined by short ramps.
float bands(float t, float n) {
	if (n < 1.0) return t;
	float x = t * n;
	return (floor(x) + smoothstep(0.3, 0.7, fract(x))) / n;
}

vec3 view_at(vec2 uv, mat4 inv_projection) {
	float raw = texture(depth_texture, uv).r;
	vec4 v = inv_projection * vec4(uv * 2.0 - 1.0, raw, 1.0);
	return v.xyz / v.w;
}

void fragment() {
	float vis;
	vec4 w = sample_water(page_pos / cell_size, vis);
	if (vis < 0.5) discard;
	float sim_depth = max(w.g, 0.0);
	vec2 flow = w.ba;
	float speed = length(flow);
	vec3 world_pos = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
	float dist = length(VERTEX);

	// Two-phase flow map: two copies of the ripples slide along the flow, each fading out as it resets.
	float phase = TIME / flow_cycle;
	float p0 = fract(phase), p1 = fract(phase + 0.5);
	float w0 = 1.0 - abs(1.0 - 2.0 * p0);
	vec2 drift = flow * flow_drag * flow_cycle;
	vec2 wind = vec2(0.8, 0.6) * ripple_speed * TIME;
	vec2 n0 = texture(detail_normal, (world_pos.xz - drift * p0 - wind) / ripple_scale).rg * 2.0 - 1.0;
	vec2 n1 = texture(detail_normal, (world_pos.xz - drift * p1 - wind) / ripple_scale + vec2(0.37, 0.71)).rg * 2.0 - 1.0;
	vec2 n2 = texture(detail_normal, (world_pos.xz + wind * 0.5) / (ripple_scale * 2.9)).rg * 2.0 - 1.0;
	vec2 s = mix(n1, n0, w0) + n2 * 0.5;
	float fade = 1.0 - smoothstep(ripple_fade.x, ripple_fade.y, dist);
	s *= ripple_strength * (1.0 + flow_roughen * min(speed, 6.0)) * fade;
	vec3 n_world = normalize(surface_normal + vec3(s.x, 0.0, s.y));
	vec3 n_view = normalize((VIEW_MATRIX * vec4(n_world, 0.0)).xyz);

	// Depth comes from the sim, not from the ground in the depth buffer: the terrain is drawn coarser with distance
	// (and the water mesh too), so on shallow water the screen depth was off by more than the water is deep, and the
	// colour, the fades and the shape changed as the camera zoomed.
	vec3 ground = view_at(SCREEN_UV, INV_PROJECTION_MATRIX);
	float depth = sim_depth;
	// Thin running water (a small stream) shows by what it carries, so it reads as one stream, not a string of pools.
	float flux = sim_depth * speed;
	float running = smoothstep(stream_flux.x, stream_flux.y, flux);
	depth = max(depth, stream_depth * running);
	// Slanted path through the water: longer at grazing angles (capped at 5× the depth).
	vec3 up_view = (VIEW_MATRIX * vec4(0.0, 1.0, 0.0, 0.0)).xyz;
	float path = depth / max(abs(dot(normalize(VERTEX), up_view)), 0.2);

	// Refracted view of the bed, unless that lands on something in front of the water.
	vec2 ruv = SCREEN_UV + n_view.xy * refraction * clamp(depth, 0.0, 1.0);
	vec3 bed_view = view_at(ruv, INV_PROJECTION_MATRIX);
	if (bed_view.z > VERTEX.z) { ruv = SCREEN_UV; bed_view = ground; }
	vec3 bed = textureLod(screen_texture, ruv, 0.0).rgb;
	vec3 bed_world = (INV_VIEW_MATRIX * vec4(bed_view, 1.0)).xyz;

	// Caustics on the bed, moving with the flow.
	// Real water only: on thin sheets (and small streams drawn as if deeper) they read as cracked mud.
	float caustic = caustic_strength * smoothstep(caustic_depth.x, caustic_depth.x + 0.3, sim_depth)
		* (1.0 - smoothstep(caustic_depth.x, caustic_depth.y, sim_depth))
		* (1.0 - smoothstep(caustic_fade.x, caustic_fade.y, dist));
	if (caustic > 0.001) {
		vec2 cp = (bed_world.xz - flow * TIME * 0.3) / caustic_scale;
		float ct = TIME * caustic_speed;
		float c1 = caustic_layer(cp, ct), c2 = caustic_layer(cp * 1.37 + vec2(4.1, 1.7), ct * 1.3);
		caustic *= max(c1, c2) * 0.7 + c1 * c2 * 0.6;
	}

	// Absorption: the simulated depth counts too, so a shallow stream is tinted right to its banks.
	float optical = max(mix(depth, path, view_path), sim_depth);
	float t1 = bands(mix(min_tint, 1.0, 1.0 - exp(-optical * absorption)), color_bands);
	float t2 = bands(1.0 - exp(-optical * deep_absorption), color_bands);
	// Pollution clouds the water: the bed and the caustics fade, the tint turns murky.
	vec2 puv = (page_pos / cell_size + 0.5) / vec2(textureSize(pollution, 0));
	float murk = smoothstep(0.0, pollution_full, texture(pollution, puv).r);
	t1 = mix(t1, 1.0, murk * 0.85);
	caustic *= 1.0 - murk;
	vec3 transmitted = (bed + caustic_color * caustic * bed) * (1.0 - t1);
	vec3 water = mix(mix(water_tint, deep_color, t2), pollution_color, murk) * t1;

	float alpha = smoothstep(0.0, shore_fade, depth);
	// Thin sheets fade out. Running water (a fall, a small stream) is thin but carries a lot (depth × speed), so it
	// counts as water too.
	float film = max(smoothstep(film_fade.x, film_fade.y, sim_depth + flux * 0.1), running);
	alpha *= film;

	// --- Foam ---
	float n_foam = texture(foam_noise, world_pos.xz / foam_scale + vec2(TIME * 0.013, TIME * 0.007)).r;
	// A crisp white band along the shore, and thin lines pulsing out from it.
	float shore = 1.0 - depth / shore_foam_width;
	// Only where there's real water nearby, not on thin sheets running down slopes.
	float body = smoothstep(0.25, 0.6, sim_depth);
	float foam = smoothstep(0.45, 0.55, shore + (n_foam - 0.5) * 0.8) * body;
	float line = sin(depth / foam_line_depth * 9.0 - TIME * foam_line_speed * 6.2831853 + n_foam * 4.0);
	foam = max(foam, smoothstep(0.93, 0.97, line) * (1.0 - smoothstep(0.3, 1.0, depth / foam_line_depth))
		* step(0.35, n_foam) * 0.9 * body);
	// Rapids and falls: milky aerated water with white streaks racing along the flow (a flow map, so they don't smear),
	// never a solid sheet of white.
	float rapid = max(smoothstep(foam_speed.x, foam_speed.y, speed),
		smoothstep(foam_slope.x, foam_slope.y, slope) * smoothstep(0.5, 2.0, speed));
	rapid *= smoothstep(0.05, 0.3, sim_depth + flux * 0.3);
	if (rapid > 0.001) {
		// Four cells around the pixel, each rigid at the flow of its centre, cross-faded (see streak_cell).
		vec2 page_origin = world_pos.xz - page_pos;
		vec2 g = world_pos.xz / whitewater_cell - 0.5, gb = floor(g), gf = smoothstep(0.15, 0.85, fract(g));
		vec2 dx = dFdx(world_pos.xz), dy = dFdy(world_pos.xz);
		float streak = 0.0, wsq = 0.0;
		for (int k = 0; k < 4; k++) {
			vec2 o = vec2(float(k & 1), float(k >> 1));
			vec2 centre = (gb + o + 0.5) * whitewater_cell;
			vec4 c = cell(ivec2(round((centre - page_origin) / cell_size)));
			// A dry or hidden centre (by a bank) borrows this pixel's flow.
			vec2 v = c.g > 0.05 ? c.ba : flow;
			vec2 wk = mix(1.0 - gf, gf, o);
			float wt = wk.x * wk.y;
			streak += wt * streak_cell(world_pos.xz, dx, dy, centre, v, p0, p1, w0);
			wsq += wt * wt;
		}
		// Same contrast fix for the cell cross-fade.
		streak = streak / sqrt(max(wsq, 0.25)) + 0.5;
		float cut = 1.0 - whitewater_coverage * rapid;
		float white = smoothstep(cut - 0.04, cut + 0.06, streak) * 0.95;
		float milk = rapid * whitewater_tint;
		water = mix(water, whitewater_color * mix(0.85, 1.05, streak), milk);
		transmitted *= 1.0 - milk;
		alpha = max(alpha, milk * film * smoothstep(0.0, 0.03, depth));
		foam = max(foam, white);
	}
	// Breakers: crest lines along depth contours (so parallel to the shore), rolling toward it over calm shallows.
	float crest = sin(sim_depth * breaker_frequency * 6.2831853 + TIME * breaker_speed * 6.2831853 + n_foam * 3.0);
	float n_wave = texture(foam_noise, world_pos.xz / (foam_scale * 4.0) - vec2(TIME * 0.01)).r;
	float breaker = smoothstep(0.88, 0.94, crest) * smoothstep(0.15, 0.4, sim_depth)
		* (1.0 - smoothstep(breaker_depth * 0.5, breaker_depth, sim_depth))
		* (1.0 - smoothstep(0.2, 0.8, speed)) * smoothstep(0.42, 0.5, n_wave) * step(0.3, n_foam) * breaker_strength;
	foam = max(foam, breaker);
	// Whitecaps: sparse flecks on deep open water.
	vec2 wd = normalize(wind_direction);
	float n_big = texture(foam_noise, world_pos.xz / (foam_scale * 6.0) + wd * TIME * 0.01).r;
	float swell = sin(dot(world_pos.xz, wd) / wave_length * 6.2831853 - TIME * 0.9 + n_big * 5.0) * 0.5 + 0.5;
	float fleck = texture(foam_noise, world_pos.xz / (foam_scale * 0.6) - wd * TIME * 0.08).r;
	float cap = smoothstep(0.82, 0.95, swell) * smoothstep(1.0 - whitecap_coverage, 1.0 - whitecap_coverage * 0.6, n_big)
		* smoothstep(0.45, 0.55, fleck);
	foam = max(foam, cap * smoothstep(2.0, 5.0, sim_depth) * whitecap_strength * fade);
	foam *= film;
	float sp = texture(foam_noise, world_pos.xz / (foam_scale * 0.15) + s * 2.0 + vec2(TIME * 0.11, -TIME * 0.07)).r;
	sparkle = smoothstep(1.0 - glint_density, 1.0 - glint_density * 0.8, sp) * (1.0 - foam) * fade;

	vec2 to_edge = min(world_pos.xz, map_size - world_pos.xz);
	alpha = max(alpha, foam * 0.95 * film * smoothstep(0.0, 0.03, depth)) * smoothstep(0.0, edge_fade, min(to_edge.x, to_edge.y));

	ALBEDO = mix(water, foam_color, foam);
	EMISSION = transmitted * (1.0 - foam) + foam_color * foam * foam_glow;
	ALPHA = alpha;
	ROUGHNESS = mix(roughness_value, 1.0, foam);
	METALLIC = 0.0;
	SPECULAR = reflection * (1.0 - foam);
	NORMAL = n_view;
}

void light() {
	float nl = dot(NORMAL, LIGHT);
	float lit = mix(clamp(nl, 0.0, 1.0), smoothstep(0.0, 0.25, nl) * 0.8 + 0.2, toon);
	DIFFUSE_LIGHT += lit * ATTENUATION * LIGHT_COLOR / PI;
	// Sun glints: a hard-edged highlight on the ripples, none on foam.
	float nh = max(dot(NORMAL, normalize(LIGHT + VIEW)), 0.0);
	float glint = smoothstep(glint_size, glint_size + (1.0 - glint_size) * 0.3, nh) * sparkle;
	SPECULAR_LIGHT += glint * glint_strength * glint_color * ATTENUATION * LIGHT_COLOR / PI;
}
