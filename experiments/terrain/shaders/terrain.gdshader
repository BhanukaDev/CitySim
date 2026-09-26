shader_type spatial;
render_mode blend_mix, cull_back;

// Ground layers, in texture-array order. Must match TerrainLayers.cs.
// 0 grass, 1 grass_dry, 2 grass_dirt, 3 dirt, 4 gravel, 5 sand, 6 rock, 7 snow
const int LAYERS = 8;
const int ROCK = 6;
// Average brightness the baker normalised every layer's colour to (TextureBaker.NeutralGrey).
const float NEUTRAL_GREY = 0.4;

// Set by Terrain.cs after generation and after each stroke.
uniform float height_min = 0.0;
uniform float height_max = 250.0;
// Terrain XZ bounds (world metres), for the edge fog.
uniform vec2 terrain_origin = vec2(0.0);
uniform vec2 terrain_size = vec2(2048.0);

group_uniforms Textures;
// RGB = colour normalised to NEUTRAL_GREY, A = height. Baked by TextureBaker, imported as BC7 arrays.
uniform sampler2DArray albedo_height_array : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2DArray normal_array : filter_linear_mipmap_anisotropic, repeat_enable;
// Painted weights for layers 0-3 and 4-7, one texel per heightmap vertex (Terrain.cs).
uniform sampler2D splat0 : hint_default_black, filter_linear, repeat_disable;
uniform sampler2D splat1 : hint_default_black, filter_linear, repeat_disable;
uniform vec2 splat_size = vec2(1025.0);
// Seamless low-frequency noise for large-scale variation.
uniform sampler2D macro_noise : hint_default_white, filter_linear_mipmap, repeat_enable;

group_uniforms Tiling;
// Metres covered by one texture repeat up close; far away a larger repeat takes over.
uniform float tile_near = 6.0;
uniform float tile_far = 32.0;
uniform float far_blend_start = 40.0;
uniform float far_blend_end = 250.0;
uniform float rock_tile = 19.0;
uniform float macro_scale = 700.0;
// Mid-scale brightness blotches that break up large flat areas of one layer.
uniform float breakup_scale = 45.0;
// Fine noise that wobbles automatic layer borders, so they read as organic rather than contour-smooth.
uniform float edge_noise_scale = 11.0;

group_uniforms Style;
// Each layer's colour. The texture only adds light/dark detail on top of it.
uniform vec3 tint_grass : source_color = vec3(0.40, 0.62, 0.20);
uniform vec3 tint_grass_dry : source_color = vec3(0.68, 0.66, 0.26);
uniform vec3 tint_grass_dirt : source_color = vec3(0.50, 0.55, 0.24);
uniform vec3 tint_dirt : source_color = vec3(0.60, 0.43, 0.28);
uniform vec3 tint_gravel : source_color = vec3(0.66, 0.58, 0.48);
uniform vec3 tint_sand : source_color = vec3(0.87, 0.75, 0.50);
uniform vec3 tint_rock : source_color = vec3(0.60, 0.56, 0.50);
uniform vec3 tint_snow : source_color = vec3(0.96, 0.96, 1.0);
// Ghibli-style colour gradients: highlands drift toward warm gold-green, lowlands toward deep cool green.
uniform vec3 grass_low_tint : source_color = vec3(0.86, 1.0, 0.92);
uniform vec3 grass_high_tint : source_color = vec3(1.12, 1.02, 0.78);
// Rock: mossy, warm-lit tops; cool violet-grey faces; darker crevices from texture height.
uniform vec3 rock_top_tint : source_color = vec3(1.10, 1.08, 0.80);
uniform vec3 rock_face_tint : source_color = vec3(0.88, 0.90, 1.0);
uniform float rock_tile_large = 47.0;
uniform float rock_detail_soft : hint_range(0.0, 1.0) = 0.55;
// How much texture detail shows through the tint (0 = flat colour, 1 = full photo contrast).
uniform float detail_contrast : hint_range(0.0, 1.5) = 0.6;
// Extra contrast for rock, so cliffs keep their cracks and ledges.
uniform float rock_detail : hint_range(0.5, 3.0) = 0.9;
// Keeps some of each texture's own colour variation (0 = only brightness detail).
uniform float detail_saturation : hint_range(0.0, 1.0) = 0.65;
uniform float normal_strength : hint_range(0.0, 2.0) = 1.0;
// Large-scale brightness and warm/cool variation.
uniform float macro_strength : hint_range(0.0, 0.5) = 0.16;
uniform float breakup_strength : hint_range(0.0, 0.5) = 0.06;
// How much texture height decides layer borders (grass fills between stones, etc.).
uniform float height_blend : hint_range(0.0, 1.0) = 0.12;
// Width of height-blended borders: lower is crisper.
uniform float blend_softness : hint_range(0.01, 0.6) = 0.45;
uniform float roughness = 0.9;

group_uniforms Lighting;
// Light wrapping around the terminator: softer, painterly shading.
uniform float light_wrap : hint_range(0.0, 1.0) = 0.35;
// Blends toward a soft two-tone (toon) ramp. 0 = off.
uniform float toon_bands : hint_range(0.0, 1.0) = 0.4;
uniform float band_softness : hint_range(0.01, 0.5) = 0.2;
// Warm glow where lit ground turns to shade.
uniform vec3 terminator_tint : source_color = vec3(1.0, 0.72, 0.5);
uniform float terminator_strength : hint_range(0.0, 1.0) = 0.3;
// Shadowed ground is tinted toward this (teal-violet) instead of going grey.
uniform vec3 shadow_tint : source_color = vec3(0.62, 0.72, 1.0);
uniform float shadow_tint_strength : hint_range(0.0, 1.0) = 0.5;

group_uniforms AutoLayers;
// Automatic layering by height (world metres) and slope (1 - normal.y; 0.3 is about 45°).
uniform float sand_height = 9.0;
uniform float snow_height = 125.0;
uniform float snow_blend = 10.0;
uniform float snow_max_slope : hint_range(0.0, 1.0) = 0.4;
uniform float rock_slope : hint_range(0.0, 1.0) = 0.3;
uniform float dirt_slope : hint_range(0.0, 1.0) = 0.17;
uniform float dry_amount : hint_range(0.0, 1.0) = 0.5;
uniform float gravel_amount : hint_range(0.0, 1.0) = 0.5;

group_uniforms EdgeFog;
// A fog bank that swallows the map border, so its hard edges and corners never show. Everything outside
// the bounds (the TerrainSkirt ring) is pure fog; inside, the ground fades into it near the border.
uniform bool edge_fog_enabled = true;
uniform vec3 edge_fog_color : source_color = vec3(0.80, 0.85, 0.95);
// Metres over which the ground fades from full fog (at the border) to clear.
uniform float edge_fog_width = 220.0;
// Rounds the fog's corners with this radius, so the map doesn't read as a rectangle.
uniform float edge_fog_corner_radius = 500.0;
// How far (metres) noise pushes the fog line inward, and the size of its billows.
uniform float edge_fog_wobble = 120.0;
uniform float edge_fog_noise_scale = 1100.0;
// Brightness variation inside the fog, and how fast the billows drift (texture repeats per second).
uniform float edge_fog_cloudiness : hint_range(0.0, 0.5) = 0.08;
uniform float edge_fog_drift = 0.002;

group_uniforms Grid;
uniform bool show_grid = false;
uniform float grid_size = 64.0;
uniform float grid_strength : hint_range(0.0, 1.0) = 0.2;

group_uniforms Contours;
uniform bool show_contours = false;
// Metres between contour lines; every 5th line is drawn bolder.
uniform float contour_interval = 5.0;
uniform vec3 contour_color : source_color = vec3(0.12, 0.09, 0.06);

// Set at runtime by the sculpt tools (Terrain.SetBrush / SetAnchor).
group_uniforms Brush;
uniform bool brush_visible = false;
uniform vec3 brush_pos = vec3(0.0);
uniform float brush_radius = 50.0;
// Textured brush shape (Terrain.SetBrush): stretched over the brush square, turned by brush_angle (radians).
uniform bool brush_use_mask = false;
uniform sampler2D brush_mask : hint_default_black, filter_linear, repeat_disable;
uniform float brush_angle = 0.0;
uniform bool brush_show_angle = false;
uniform bool anchor_visible = false;
uniform vec3 anchor_pos = vec3(0.0);
uniform vec3 brush_color : source_color = vec3(0.35, 0.85, 1.0);

varying vec3 world_pos;
varying vec3 world_normal;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

// Distance (metres) inside the terrain border, with rounded corners; negative outside.
float edge_distance(vec2 xz) {
	vec2 rel = xz - terrain_origin;
	vec2 dd = min(rel, terrain_size - rel);
	float r = edge_fog_corner_radius;
	return min(min(dd.x, dd.y), r - length(max(vec2(r) - dd, vec2(0.0))));
}

vec3 layer_tint(int i) {
	if (i == 0) return tint_grass;
	if (i == 1) return tint_grass_dry;
	if (i == 2) return tint_grass_dirt;
	if (i == 3) return tint_dirt;
	if (i == 4) return tint_gravel;
	if (i == 5) return tint_sand;
	if (i == 6) return tint_rock;
	return tint_snow;
}

// Lays a layer over the ones below it with coverage c.
void lay(inout float w[LAYERS], int layer, float c) {
	for (int i = 0; i < LAYERS; i++) w[i] *= 1.0 - c;
	w[layer] += c;
}

// Unpacks a tangent-space normal (OpenGL convention) and scales its bumpiness.
vec3 unpack_normal(vec3 t) {
	vec3 n = t * 2.0 - 1.0;
	n.xy *= normal_strength;
	return n;
}

struct Sample {
	vec4 albedo_height;
	vec3 normal; // world space
};

// Top-down projection at two scales, blended by distance to hide tiling.
Sample sample_planar(int layer, vec3 p, vec3 n, float far_t) {
	vec3 uv_near = vec3(p.xz / tile_near, float(layer));
	vec3 uv_far = vec3(p.xz / tile_far, float(layer));
	vec4 ah = mix(texture(albedo_height_array, uv_near), texture(albedo_height_array, uv_far), far_t);
	vec3 tn = unpack_normal(mix(texture(normal_array, uv_near).rgb, texture(normal_array, uv_far).rgb, far_t));
	// Whiteout blend onto the surface normal (Y projection: u = +X, v = +Z).
	vec3 w = vec3(tn.xy + n.xz, abs(tn.z) * n.y);
	Sample s;
	s.albedo_height = ah;
	s.normal = normalize(vec3(w.x, w.z, w.y));
	return s;
}

// Triplanar projection for cliffs, so steep faces don't stretch. Two scales are mixed by low-frequency
// noise and the coordinates are warped, so no repeat of the texture is recognisable.
Sample sample_triplanar(int layer, vec3 p, vec3 n) {
	vec3 bw = pow(abs(n), vec3(4.0));
	bw /= bw.x + bw.y + bw.z;
	vec2 wx = vec2(texture(macro_noise, p.zy / 37.0).r, texture(macro_noise, p.zy / 23.0 + 0.4).r) - 0.5;
	vec2 wy = vec2(texture(macro_noise, p.xz / 37.0).r, texture(macro_noise, p.xz / 23.0 + 0.4).r) - 0.5;
	vec2 wz = vec2(texture(macro_noise, p.xy / 37.0).r, texture(macro_noise, p.xy / 23.0 + 0.4).r) - 0.5;
	float mx = smoothstep(0.3, 0.7, texture(macro_noise, p.zy / 61.0 + 0.2).r);
	float my = smoothstep(0.3, 0.7, texture(macro_noise, p.xz / 61.0 + 0.2).r);
	float mz = smoothstep(0.3, 0.7, texture(macro_noise, p.xy / 61.0 + 0.2).r);
	vec3 px = vec3(p.zy / rock_tile + wx * 0.35, float(layer));
	vec3 py = vec3(p.xz / rock_tile + wy * 0.35, float(layer));
	vec3 pz = vec3(p.xy / rock_tile + wz * 0.35, float(layer));
	vec3 qx = vec3(p.zy / rock_tile_large + 0.31, float(layer));
	vec3 qy = vec3(p.xz / rock_tile_large + 0.31, float(layer));
	vec3 qz = vec3(p.xy / rock_tile_large + 0.31, float(layer));
	vec4 ah = mix(texture(albedo_height_array, px), texture(albedo_height_array, qx), mx) * bw.x
			+ mix(texture(albedo_height_array, py), texture(albedo_height_array, qy), my) * bw.y
			+ mix(texture(albedo_height_array, pz), texture(albedo_height_array, qz), mz) * bw.z;
	vec3 tx = unpack_normal(mix(texture(normal_array, px).rgb, texture(normal_array, qx).rgb, mx));
	vec3 ty = unpack_normal(mix(texture(normal_array, py).rgb, texture(normal_array, qy).rgb, my));
	vec3 tz = unpack_normal(mix(texture(normal_array, pz).rgb, texture(normal_array, qz).rgb, mz));
	// Soften the photo's bumpiness so cliffs read as painted planes, not scanned stone.
	tx.xy *= rock_detail_soft; ty.xy *= rock_detail_soft; tz.xy *= rock_detail_soft;
	tx = vec3(tx.xy + n.zy, abs(tx.z) * n.x);
	ty = vec3(ty.xy + n.xz, abs(ty.z) * n.y);
	tz = vec3(tz.xy + n.xy, abs(tz.z) * n.z);
	Sample s;
	s.albedo_height = ah;
	s.normal = normalize(tx.zyx * bw.x + ty.xzy * bw.y + tz.xyz * bw.z);
	return s;
}

// Tint the neutral-grey texture: the tint sets the colour, the texture adds detail.
vec3 stylize(vec3 tex, int layer, float h01, float up) {
	vec3 ratio = tex / NEUTRAL_GREY;
	float lum = dot(ratio, vec3(0.299, 0.587, 0.114));
	ratio = mix(vec3(lum), ratio, detail_saturation);
	// The ratio is in sRGB space; ^2.2 converts it to a linear-light factor.
	float contrast = detail_contrast * (layer == ROCK ? rock_detail : 1.0);
	vec3 tint = layer_tint(layer);
	if (layer <= 2) tint *= mix(grass_low_tint, grass_high_tint, smoothstep(0.05, 0.6, h01));
	if (layer == ROCK) tint *= mix(rock_face_tint, rock_top_tint, smoothstep(0.35, 0.85, up));
	return tint * pow(max(ratio, vec3(0.0)), vec3(2.2 * contrast));
}

void fragment() {
	vec3 n = normalize(world_normal);
	float slope = 1.0 - n.y;
	float y = world_pos.y;
	float h01 = clamp((y - height_min) / max(height_max - height_min, 0.001), 0.0, 1.0);

	float noise_a = texture(macro_noise, world_pos.xz / macro_scale).r;
	float noise_b = texture(macro_noise, world_pos.xz / (macro_scale * 0.21) + vec2(0.37, 0.71)).r;
	// Two fine octaves at unrelated scales, so the wobble itself doesn't repeat visibly.
	float edge = texture(macro_noise, world_pos.xz / edge_noise_scale + vec2(0.61, 0.23)).r * 0.6
			+ texture(macro_noise, world_pos.xz / (edge_noise_scale * 2.7) + vec2(0.11, 0.89)).r * 0.4 - 0.5;
	float slope_n = slope + (noise_b - 0.5) * 0.08 + edge * 0.05;
	float y_n = y + (noise_b - 0.5) * 8.0 + edge;

	// --- Automatic layers, laid bottom to top ---
	float w[LAYERS];
	for (int i = 0; i < LAYERS; i++) w[i] = 0.0;
	w[0] = 1.0;
	lay(w, 1, smoothstep(0.35, 0.95, noise_a + h01 * 0.35 + edge * 0.15) * dry_amount);
	lay(w, 2, smoothstep(dirt_slope - 0.08, dirt_slope + 0.08, slope_n));
	float rock = smoothstep(rock_slope - 0.05, rock_slope + 0.05, slope_n);
	// Scree at the foot of cliffs, broken up by noise so it doesn't outline every rock patch.
	float scree = smoothstep(rock_slope - 0.1, rock_slope - 0.02, slope_n) * smoothstep(0.35, 0.65, noise_b);
	lay(w, 4, scree * gravel_amount);
	lay(w, ROCK, rock);
	lay(w, 5, 1.0 - smoothstep(sand_height - 3.0, sand_height + 3.0, y_n));
	// Snow line: wanders with noise and thins out over snow_blend metres; height blending then lets
	// grass tufts and stones poke through the thin edge instead of a separate "snowy grass" texture.
	float snow_ok = 1.0 - smoothstep(snow_max_slope - 0.08, snow_max_slope + 0.08, slope_n);
	float y_snow = y_n + (noise_a - 0.5) * snow_blend * 1.5 + edge * snow_blend;
	lay(w, 7, smoothstep(snow_height - snow_blend, snow_height + snow_blend * 0.5, y_snow) * snow_ok);

	// --- Painted layers override the automatic ones by their coverage ---
	// Vertex UVs run 0..1 across vertex centres; shift them onto texel centres.
	vec2 suv = (UV * (splat_size - 1.0) + 0.5) / splat_size;
	vec4 p0 = texture(splat0, suv);
	vec4 p1 = texture(splat1, suv);
	float cover = min(dot(p0, vec4(1.0)) + dot(p1, vec4(1.0)), 1.0);
	for (int i = 0; i < LAYERS; i++) w[i] *= 1.0 - cover;
	w[0] += p0.r; w[1] += p0.g; w[2] += p0.b; w[3] += p0.a;
	w[4] += p1.r; w[5] += p1.g; w[6] += p1.b; w[7] += p1.a;

	// --- Keep the four strongest layers; only those get sampled ---
	int top_i[4];
	float top_w[4];
	for (int k = 0; k < 4; k++) {
		int best = 0;
		float bw = -1.0;
		for (int i = 0; i < LAYERS; i++) {
			if (w[i] > bw) { bw = w[i]; best = i; }
		}
		top_i[k] = best;
		top_w[k] = max(bw, 0.0);
		w[best] = -1.0;
	}

	float dist = length(world_pos - CAMERA_POSITION_WORLD);
	float far_t = smoothstep(far_blend_start, far_blend_end, dist);

	// --- Sample and height-blend ---
	vec3 col[4];
	vec3 nrm[4];
	float score[4];
	float best_score = -1.0;
	for (int k = 0; k < 4; k++) {
		col[k] = vec3(0.0);
		nrm[k] = n;
		score[k] = -1.0;
		if (top_w[k] <= 0.001) continue;
		Sample s;
		if (top_i[k] == ROCK) s = sample_triplanar(ROCK, world_pos, n);
		else s = sample_planar(top_i[k], world_pos, n, far_t);
		col[k] = stylize(s.albedo_height.rgb, top_i[k], h01, n.y);
		nrm[k] = s.normal;
		score[k] = top_w[k] + s.albedo_height.a * height_blend;
		best_score = max(best_score, score[k]);
	}
	vec3 albedo = vec3(0.0);
	vec3 normal_ws = vec3(0.0);
	float total = 0.0;
	for (int k = 0; k < 4; k++) {
		float b = max(score[k] - (best_score - blend_softness), 0.0) * smoothstep(0.0, 0.05, top_w[k]);
		albedo += col[k] * b;
		normal_ws += nrm[k] * b;
		total += b;
	}
	albedo /= max(total, 1e-4);
	normal_ws = normalize(total > 0.0 ? normal_ws : n);

	// Large-scale variation: brightness plus a warm/cool shift.
	float m = noise_a * 0.6 + noise_b * 0.4 - 0.5;
	albedo *= 1.0 + m * macro_strength * 2.0;
	albedo = mix(albedo, albedo * vec3(1.06, 1.0, 0.88), clamp(m * 2.0 + 0.5, 0.0, 1.0) * macro_strength * 2.0);
	float breakup = texture(macro_noise, world_pos.xz / breakup_scale + vec2(0.13, 0.57)).r - 0.5;
	albedo *= 1.0 + breakup * breakup_strength * 2.0;

	vec3 col_out = albedo;

	if (show_grid) {
		vec2 g = world_pos.xz / grid_size;
		vec2 fw = max(fwidth(g), vec2(1e-4));
		vec2 gd = abs(fract(g - 0.5) - 0.5) / fw;
		float line = 1.0 - min(min(gd.x, gd.y), 1.0);
		// Fade out once lines get denser than a few pixels apart.
		line *= 1.0 - smoothstep(0.05, 0.25, max(fw.x, fw.y));
		col_out = mix(col_out, vec3(0.05), line * grid_strength);
	}

	if (show_contours) {
		float c = world_pos.y / contour_interval;
		float fw = max(fwidth(c), 1e-4);
		float minor = 1.0 - min(abs(fract(c - 0.5) - 0.5) / fw, 1.0);
		float cm = c / 5.0;
		float fwm = max(fwidth(cm), 1e-4);
		float major = 1.0 - min(abs(fract(cm - 0.5) - 0.5) / (fwm * 1.8), 1.0);
		// Fade minor lines where they get denser than a few pixels apart (steep or distant ground).
		minor *= 1.0 - smoothstep(0.2, 0.5, fw);
		major *= 1.0 - smoothstep(0.2, 0.5, fwm);
		col_out = mix(col_out, contour_color, max(minor * 0.35, major * 0.7));
	}

	vec3 glow = vec3(0.0);
	if (brush_visible) {
		float d = length(world_pos.xz - brush_pos.xz);
		float fw = max(fwidth(d), 1e-4);
		float ring = 1.0 - clamp(abs(d - brush_radius) / (fw * 1.5), 0.0, 1.0);
		// Faint fill that follows the brush falloff, or the brush shape (same mapping as Brush.Weight in C#).
		vec2 off = world_pos.xz - brush_pos.xz;
		float t = clamp(1.0 - d / brush_radius, 0.0, 1.0);
		float fill = t * t * (3.0 - 2.0 * t) * 0.18;
		float ca = cos(brush_angle), sa = sin(brush_angle);
		if (brush_use_mask) {
			vec2 local = vec2(ca * off.x - sa * off.y, sa * off.x + ca * off.y);
			fill = d < brush_radius ? texture(brush_mask, 0.5 + 0.5 * local / brush_radius).r * 0.3 : 0.0;
		}
		if (brush_show_angle) {
			// Tick just inside the ring along the brush's +X axis, world direction (cos a, -sin a).
			vec2 dir = vec2(ca, -sa);
			float along = dot(off, dir);
			float across = abs(dot(off, vec2(-dir.y, dir.x)));
			float tw = max(fw * 1.5, brush_radius * 0.012);
			float tick = (1.0 - smoothstep(tw, tw + fw, across))
					* step(brush_radius * 0.8, along) * step(along, brush_radius);
			ring = max(ring, tick);
		}
		col_out = mix(col_out, brush_color, max(ring, fill));
		glow += brush_color * ring * 0.5;
	}
	if (anchor_visible) {
		vec2 p = world_pos.xz;
		float fw = max(fwidth(length(p)), 1e-4);
		float marker_r = max(brush_radius * 0.08, 2.0);
		float marker = 1.0 - smoothstep(marker_r - fw, marker_r + fw, length(p - anchor_pos.xz));
		float line = 0.0;
		if (brush_visible) {
			// Guide line from the start point to the brush: the ramp the slope tool builds.
			vec2 a = anchor_pos.xz, ab = brush_pos.xz - a;
			float hh = clamp(dot(p - a, ab) / max(dot(ab, ab), 1e-4), 0.0, 1.0);
			float dl = length(p - a - ab * hh);
			float lw = max(fw * 1.5, 0.6);
			line = 1.0 - smoothstep(lw, lw + fw, dl);
		}
		float mk = max(marker, line * 0.8);
		col_out = mix(col_out, vec3(1.0, 0.75, 0.2), mk);
		glow += vec3(1.0, 0.75, 0.2) * mk * 0.4;
	}

	// Edge fog: an unlit colour (emission) replaces the lit ground.
	float fog = 0.0;
	vec3 fog_col = edge_fog_color;
	if (edge_fog_enabled) {
		vec2 drift = vec2(TIME * edge_fog_drift, TIME * edge_fog_drift * 0.6);
		float billow = texture(macro_noise, world_pos.xz / edge_fog_noise_scale + drift).r * 0.65
				+ texture(macro_noise, world_pos.xz / (edge_fog_noise_scale * 0.37) - drift * 1.7 + 0.43).r * 0.35;
		float d = edge_distance(world_pos.xz) - billow * edge_fog_wobble;
		fog = 1.0 - smoothstep(0.0, edge_fog_width, d);
		fog *= fog;
		fog = max(fog, step(edge_distance(world_pos.xz), 0.0));
		fog_col *= 1.0 + (billow - 0.5) * edge_fog_cloudiness * 2.0;
	}

	ALBEDO = col_out * (1.0 - fog);
	NORMAL = normalize((VIEW_MATRIX * vec4(normal_ws, 0.0)).xyz);
	EMISSION = glow * (1.0 - fog) + fog_col * fog;
	ROUGHNESS = mix(roughness, 1.0, fog);
	SPECULAR = 0.2 * (1.0 - fog);
}

void light() {
	// Wrapped diffuse, optionally pulled toward a soft two-tone ramp.
	float ndl = dot(NORMAL, LIGHT);
	float wrapped = clamp((ndl + light_wrap) / (1.0 + light_wrap), 0.0, 1.0);
	float banded = smoothstep(0.5 - band_softness, 0.5 + band_softness, wrapped);
	float d = mix(wrapped, banded, toon_bands);
	// Warm tint where the light grazes the surface.
	float edge = d * (1.0 - d) * 4.0;
	vec3 tint = mix(vec3(1.0), terminator_tint, edge * terminator_strength);
	DIFFUSE_LIGHT += LIGHT_COLOR * ATTENUATION * d * tint / PI;
	// Painterly shadows: where light falls off, add a cool-tinted fill rather than grey.
	float shade = 1.0 - d * ATTENUATION;
	DIFFUSE_LIGHT += shadow_tint * shade * shadow_tint_strength * 0.12;
}
