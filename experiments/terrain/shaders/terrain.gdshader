shader_type spatial;
render_mode blend_mix, cull_back;

// Ground layers, in texture-array order. Must match TerrainLayers.cs.
// 0 grass, 1 grass_dry, 2 grass_dirt, 3 dirt, 4 gravel, 5 sand, 6 rock, 7 snow, 8 snow_grass (automatic only)
const int LAYERS = 9;
const int ROCK = 6;
// Average brightness the baker normalised every layer's colour to (TextureBaker.NeutralGrey).
const float NEUTRAL_GREY = 0.4;

// Set by Terrain.cs after generation and after each stroke.
uniform float height_min = 0.0;
uniform float height_max = 250.0;

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
uniform float rock_tile = 14.0;
uniform float macro_scale = 700.0;
// Mid-scale brightness blotches that break up large flat areas of one layer.
uniform float breakup_scale = 45.0;

group_uniforms Style;
// Each layer's colour. The texture only adds light/dark detail on top of it.
uniform vec3 tint_grass : source_color = vec3(0.42, 0.60, 0.25);
uniform vec3 tint_grass_dry : source_color = vec3(0.60, 0.63, 0.33);
uniform vec3 tint_grass_dirt : source_color = vec3(0.47, 0.53, 0.28);
uniform vec3 tint_dirt : source_color = vec3(0.54, 0.42, 0.30);
uniform vec3 tint_gravel : source_color = vec3(0.60, 0.57, 0.52);
uniform vec3 tint_sand : source_color = vec3(0.80, 0.71, 0.52);
uniform vec3 tint_rock : source_color = vec3(0.50, 0.48, 0.45);
uniform vec3 tint_snow : source_color = vec3(0.94, 0.96, 1.0);
uniform vec3 tint_snow_grass : source_color = vec3(0.82, 0.86, 0.84);
// How much texture detail shows through the tint (0 = flat colour, 1 = full photo contrast).
uniform float detail_contrast : hint_range(0.0, 1.5) = 0.55;
// Extra contrast for rock, so cliffs keep their cracks and ledges.
uniform float rock_detail : hint_range(0.5, 3.0) = 1.4;
// Keeps some of each texture's own colour variation (0 = only brightness detail).
uniform float detail_saturation : hint_range(0.0, 1.0) = 0.5;
uniform float normal_strength : hint_range(0.0, 2.0) = 0.6;
// Large-scale brightness and warm/cool variation.
uniform float macro_strength : hint_range(0.0, 0.5) = 0.12;
uniform float breakup_strength : hint_range(0.0, 0.5) = 0.08;
// How much texture height decides layer borders (grass fills between stones, etc.).
uniform float height_blend : hint_range(0.0, 1.0) = 0.35;
// Width of height-blended borders: lower is crisper.
uniform float blend_softness : hint_range(0.01, 0.5) = 0.2;
uniform float roughness = 0.9;

group_uniforms Lighting;
// Light wrapping around the terminator: softer, painterly shading.
uniform float light_wrap : hint_range(0.0, 1.0) = 0.25;
// Blends toward a soft two-tone (toon) ramp. 0 = off.
uniform float toon_bands : hint_range(0.0, 1.0) = 0.3;
uniform float band_softness : hint_range(0.01, 0.5) = 0.2;
// Warm glow where lit ground turns to shade.
uniform vec3 terminator_tint : source_color = vec3(1.0, 0.72, 0.5);
uniform float terminator_strength : hint_range(0.0, 1.0) = 0.25;

group_uniforms AutoLayers;
// Automatic layering by height (world metres) and slope (1 - normal.y; 0.3 is about 45°).
uniform float sand_height = 9.0;
uniform float snow_height = 125.0;
uniform float snow_blend = 6.0;
uniform float snow_max_slope : hint_range(0.0, 1.0) = 0.4;
uniform float rock_slope : hint_range(0.0, 1.0) = 0.3;
uniform float dirt_slope : hint_range(0.0, 1.0) = 0.17;
uniform float dry_amount : hint_range(0.0, 1.0) = 0.7;
uniform float gravel_amount : hint_range(0.0, 1.0) = 0.5;

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
uniform bool anchor_visible = false;
uniform vec3 anchor_pos = vec3(0.0);
uniform vec3 brush_color : source_color = vec3(0.35, 0.85, 1.0);

varying vec3 world_pos;
varying vec3 world_normal;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

vec3 layer_tint(int i) {
	if (i == 0) return tint_grass;
	if (i == 1) return tint_grass_dry;
	if (i == 2) return tint_grass_dirt;
	if (i == 3) return tint_dirt;
	if (i == 4) return tint_gravel;
	if (i == 5) return tint_sand;
	if (i == 6) return tint_rock;
	if (i == 7) return tint_snow;
	return tint_snow_grass;
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

// Triplanar projection for cliffs, so steep faces don't stretch.
Sample sample_triplanar(int layer, vec3 p, vec3 n) {
	vec3 bw = pow(abs(n), vec3(4.0));
	bw /= bw.x + bw.y + bw.z;
	vec3 px = vec3(p.zy / rock_tile, float(layer));
	vec3 py = vec3(p.xz / rock_tile, float(layer));
	vec3 pz = vec3(p.xy / rock_tile, float(layer));
	vec4 ah = texture(albedo_height_array, px) * bw.x
			+ texture(albedo_height_array, py) * bw.y
			+ texture(albedo_height_array, pz) * bw.z;
	vec3 tx = unpack_normal(texture(normal_array, px).rgb);
	vec3 ty = unpack_normal(texture(normal_array, py).rgb);
	vec3 tz = unpack_normal(texture(normal_array, pz).rgb);
	tx = vec3(tx.xy + n.zy, abs(tx.z) * n.x);
	ty = vec3(ty.xy + n.xz, abs(ty.z) * n.y);
	tz = vec3(tz.xy + n.xy, abs(tz.z) * n.z);
	Sample s;
	s.albedo_height = ah;
	s.normal = normalize(tx.zyx * bw.x + ty.xzy * bw.y + tz.xyz * bw.z);
	return s;
}

// Tint the neutral-grey texture: the tint sets the colour, the texture adds detail.
vec3 stylize(vec3 tex, int layer) {
	vec3 ratio = tex / NEUTRAL_GREY;
	float lum = dot(ratio, vec3(0.299, 0.587, 0.114));
	ratio = mix(vec3(lum), ratio, detail_saturation);
	// The ratio is in sRGB space; ^2.2 converts it to a linear-light factor.
	float contrast = detail_contrast * (layer == ROCK ? rock_detail : 1.0);
	return layer_tint(layer) * pow(max(ratio, vec3(0.0)), vec3(2.2 * contrast));
}

void fragment() {
	vec3 n = normalize(world_normal);
	float slope = 1.0 - n.y;
	float y = world_pos.y;
	float h01 = clamp((y - height_min) / max(height_max - height_min, 0.001), 0.0, 1.0);

	float noise_a = texture(macro_noise, world_pos.xz / macro_scale).r;
	float noise_b = texture(macro_noise, world_pos.xz / (macro_scale * 0.21) + vec2(0.37, 0.71)).r;
	float slope_n = slope + (noise_b - 0.5) * 0.08;
	float y_n = y + (noise_b - 0.5) * 8.0;

	// --- Automatic layers, laid bottom to top ---
	float w[LAYERS];
	for (int i = 0; i < LAYERS; i++) w[i] = 0.0;
	w[0] = 1.0;
	lay(w, 1, smoothstep(0.35, 0.95, noise_a + h01 * 0.35) * dry_amount);
	lay(w, 2, smoothstep(dirt_slope - 0.07, dirt_slope + 0.07, slope_n));
	float rock = smoothstep(rock_slope - 0.03, rock_slope + 0.03, slope_n);
	// Scree at the foot of cliffs, broken up by noise so it doesn't outline every rock patch.
	float scree = smoothstep(rock_slope - 0.08, rock_slope - 0.02, slope_n) * smoothstep(0.35, 0.65, noise_b);
	lay(w, 4, scree * gravel_amount);
	lay(w, ROCK, rock);
	lay(w, 5, 1.0 - smoothstep(sand_height - 1.5, sand_height + 1.5, y_n));
	float snow_ok = 1.0 - smoothstep(snow_max_slope - 0.05, snow_max_slope + 0.05, slope_n);
	lay(w, 8, smoothstep(snow_height - snow_blend * 3.0, snow_height - snow_blend * 1.5, y_n) * snow_ok);
	lay(w, 7, smoothstep(snow_height - snow_blend, snow_height, y_n) * snow_ok);

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
		col[k] = stylize(s.albedo_height.rgb, top_i[k]);
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
		// Faint fill that follows the brush falloff.
		float t = clamp(1.0 - d / brush_radius, 0.0, 1.0);
		float fill = t * t * (3.0 - 2.0 * t) * 0.18;
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

	ALBEDO = col_out;
	NORMAL = normalize((VIEW_MATRIX * vec4(normal_ws, 0.0)).xyz);
	EMISSION = glow;
	ROUGHNESS = roughness;
	SPECULAR = 0.2;
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
}
