// CitySim terrain SDK: fragment() and the default light(). Include it last in a theme shader, after
// terrain_core.gdshaderinc, the theme's materials.gdshaderinc and its hooks:
//   void terrain_auto(TerrainInputs t, inout vec4 w[MATERIAL_PACKS])        required: the automatic ground, laid over
//                                                                           the base (the theme's first material)
//   #define TERRAIN_AUTO_LATE   + void terrain_auto_late(TerrainInputs t, inout vec4 w[MATERIAL_PACKS])
//                                   optional: laid after the scour/deposit slots but before the shore/stream slots
//                                   (things that should cover eroded ground, such as cliffs)
//   #define TERRAIN_TINT_HOOK   + vec3 terrain_tint(int m, vec3 tint, TerrainInputs t)   optional: vary a material's tint
//   #define TERRAIN_CUSTOM_LIGHT + your own light()                         optional
//
// Order at each point: base -> terrain_auto -> scour/deposit slots -> terrain_auto_late -> shore/stream slots
// -> painted materials (cover the rest by their coverage) -> the four strongest are sampled and height-blended.

#ifndef MATERIAL_COUNT
#error "Include the theme's materials.gdshaderinc (written by the theme baker) before terrain_fragment.gdshaderinc."
#endif

// One erosion slot's coverage at t: its mask value v past the slot's edge, noise on the edge, limited by slope.
float slot_cover(int s, float v, bool is_below, TerrainInputs t) {
	vec4 e = slot_edge[s];
	vec4 sl = slot_slope[s];
	vec4 noises = vec4(t.noise_fine, t.noise_patchy, t.noise_medium, t.noise_large) - 0.5;
	v += pick4(noises, int(sl.x)) * e.w;
	float c = e.x * (is_below ? 1.0 - ramp((v - e.y) * e.z) : ramp((v - e.y) * e.z + 1.0));
	if (sl.w > 0.5) c *= 1.0 - ramp((t.slope - sl.y) * sl.z);
	return c;
}

// Scour and deposit slots (where running water wears the ground away or drops what it carried).
void apply_ground_slots(TerrainInputs t, inout vec4 w[MATERIAL_PACKS], inout float dbg) {
	float c;
#ifdef SLOT_DEPOSIT
	c = slot_cover(SLOT_I_DEPOSIT, t.deposit, false, t);
	if (slot_debug == SLOT_I_DEPOSIT) dbg = c;
	lay(w, SLOT_DEPOSIT, c);
#endif
#ifdef SLOT_DEPOSIT_HEAVY
	c = slot_cover(SLOT_I_DEPOSIT_HEAVY, t.deposit, false, t);
	if (slot_debug == SLOT_I_DEPOSIT_HEAVY) dbg = c;
	lay(w, SLOT_DEPOSIT_HEAVY, c);
#endif
#ifdef SLOT_SCOUR
	c = slot_cover(SLOT_I_SCOUR, t.wear, false, t);
	if (slot_debug == SLOT_I_SCOUR) dbg = c;
	lay(w, SLOT_SCOUR, c);
#endif
#ifdef SLOT_SCOUR_HEAVY
	c = slot_cover(SLOT_I_SCOUR_HEAVY, t.wear, false, t);
	if (slot_debug == SLOT_I_SCOUR_HEAVY) dbg = c;
	lay(w, SLOT_SCOUR_HEAVY, c);
#endif
}

// Shore and stream slots (along lakes, the sea, rivers and gullies).
void apply_water_slots(TerrainInputs t, inout vec4 w[MATERIAL_PACKS], inout float dbg) {
	float c;
#ifdef SLOT_SHORE_FRINGE
	c = slot_cover(SLOT_I_SHORE_FRINGE, t.shore, true, t);
	if (slot_debug == SLOT_I_SHORE_FRINGE) dbg = c;
	lay(w, SLOT_SHORE_FRINGE, c);
#endif
#ifdef SLOT_SHORE
	c = slot_cover(SLOT_I_SHORE, t.shore, true, t);
	if (slot_debug == SLOT_I_SHORE) dbg = c;
	lay(w, SLOT_SHORE, c);
#endif
#ifdef SLOT_STREAM_BANK
	c = slot_cover(SLOT_I_STREAM_BANK, t.gully, true, t);
	if (slot_debug == SLOT_I_STREAM_BANK) dbg = c;
	lay(w, SLOT_STREAM_BANK, c);
#endif
#ifdef SLOT_STREAM_BED
	c = slot_cover(SLOT_I_STREAM_BED, t.gully, true, t);
	if (slot_debug == SLOT_I_STREAM_BED) dbg = c;
	lay(w, SLOT_STREAM_BED, c);
#endif
#ifdef SLOT_WET
	c = slot_cover(SLOT_I_WET, t.wet, false, t);
	if (slot_debug == SLOT_I_WET) dbg = c;
	lay(w, SLOT_WET, c);
#endif
}

// Tint the neutral-grey texture: the material's tint sets the colour, the texture adds detail.
vec3 stylize(vec3 tex, int m, TerrainInputs t) {
	vec3 ratio = tex / NEUTRAL_GREY;
	float lum = dot(ratio, vec3(0.299, 0.587, 0.114));
	ratio = mix(vec3(lum), ratio, detail_saturation);
	// The ratio is in sRGB space; ^2.2 converts it to a linear-light factor.
	float contrast = detail_contrast * material_params[m].z;
	vec3 tint = material_tint[m].rgb;
#ifdef TERRAIN_TINT_HOOK
	tint = terrain_tint(m, tint, t);
#endif
	return tint * pow(max(ratio, vec3(0.0)), vec3(2.2 * contrast));
}

void fragment() {
	vec3 world_pos = v_vertex;
	ivec3 corner[4];
	vec4 corner_w;
	vec3 n = terrain_normal(UV, corner, corner_w);

	float noise_a = texture(macro_noise, world_pos.xz / macro_scale).r;
	float noise_b = texture(macro_noise, world_pos.xz / (macro_scale * 0.21) + vec2(0.37, 0.71)).r;

	// Ground masks, bilinear between the four surrounding vertices.
	vec4 gm = vec4(0.0);
	for (int k = 0; k < 4; k++)
		if (corner[k].z >= 0) gm += texelFetch(_color_maps, corner[k], 0) * corner_w[k];

	TerrainInputs t;
	t.pos = world_pos;
	t.normal = n;
	t.height = world_pos.y;
	t.h01 = clamp((world_pos.y - height_min) / max(height_max - height_min, 0.001), 0.0, 1.0);
	t.slope = degrees(acos(clamp(n.y, 0.0, 1.0)));
	t.shore = 64.0 - gm.r * 80.0;
	t.gully = 24.0 - gm.g * 32.0;
	t.wear = gm.b * 255.0 / 32.0;
	t.deposit = gm.a * 255.0 / 32.0;
	t.wet = 0.0;
	vec2 wg = vec2(1.0, 0.0);
	if (has_water_ground) {
		vec2 cells = (world_pos.xz - terrain_origin) / water_ground_cell + 0.5;
		wg = texture(water_ground, cells / vec2(textureSize(water_ground, 0))).rg;
		t.shore = min(t.shore, wg.r * 255.0 / 4.0);
		t.wet = wg.g;
	}
	t.noise_large = noise_a;
	t.noise_medium = noise_b;
	t.noise_fine = edge_noise(world_pos.xz, edge_noise_scale) + 0.5;
	t.noise_patchy = edge_noise(world_pos.xz, patchy_noise_scale) + 0.5;

	// How wet the ground looks: the sim's wet paint, a band at the waterline, and rain over the whole map (flat ground a
	// little more, since it drains slowest). Rain puddles collect on flat ground in patches once it's wet enough.
	float flat_ground = 1.0 - smoothstep(puddle_max_slope * 0.5, puddle_max_slope, t.slope);
	// Damp bank beside the water: fully wet for the first 20 % of wet_shore_band, fading out by its end, with a
	// noisy edge so it isn't a clean stripe.
	float bank_d = wg.r * 255.0 / 4.0 + (t.noise_patchy - 0.5) * wet_shore_band * 0.8;
	float waterline = has_water_ground ? 1.0 - smoothstep(wet_shore_band * 0.2, wet_shore_band, bank_d) : 0.0;
	float rain = global_wetness * mix(0.8, 1.0, 1.0 - smoothstep(10.0, 35.0, t.slope));
	float puddle_p = ramp((global_wetness - puddle_start) / max(1.0 - puddle_start, 0.01));
	// Where water gathers: gully and stream beds, and the ground around lakes and rivers (not hilltops).
	float low_ground = max(1.0 - ramp((t.gully - 2.0) / 14.0), 1.0 - ramp((t.shore - 6.0) / 24.0));
	float puddle = puddle_p > 0.0 ? flat_ground * low_ground
			* smoothstep(0.62 - 0.2 * puddle_p, 0.66 - 0.2 * puddle_p, t.noise_patchy) : 0.0;
	t.wet_look = clamp(max(max(t.wet, waterline) * wet_amount, max(rain, puddle)), 0.0, 1.0);

	// --- Automatic ground: the base, the theme's rules and the erosion slots, bottom to top ---
	vec4 w[MATERIAL_PACKS];
	w[0] = vec4(1.0, 0.0, 0.0, 0.0);
	for (int j = 1; j < MATERIAL_PACKS; j++) w[j] = vec4(0.0);
	float debug_cov = 0.0;
	terrain_auto(t, w);
	apply_ground_slots(t, w, debug_cov);
#ifdef TERRAIN_AUTO_LATE
	terrain_auto_late(t, w);
#endif
	apply_water_slots(t, w, debug_cov);

	// --- Painted materials override the automatic ones by their coverage ---
	// Bilinear between the four surrounding vertices' control values.
	vec4 paint[MATERIAL_PACKS];
	for (int j = 0; j < MATERIAL_PACKS; j++) paint[j] = vec4(0.0);
	for (int k = 0; k < 4; k++) add_paint(paint, corner[k], corner_w[k], MATERIAL_COUNT);
	float cover = 0.0;
	for (int j = 0; j < MATERIAL_PACKS; j++) cover += dot(paint[j], vec4(1.0));
	cover = min(cover, 1.0);
	float wf[MAX_MATERIALS];
	for (int j = 0; j < MATERIAL_PACKS; j++) {
		vec4 v = w[j] * (1.0 - cover) + paint[j];
		wf[j * 4] = v.x; wf[j * 4 + 1] = v.y; wf[j * 4 + 2] = v.z; wf[j * 4 + 3] = v.w;
	}

	// --- Keep the four strongest materials; only those get sampled ---
	int top_i[4];
	float top_w[4];
	for (int k = 0; k < 4; k++) {
		int best = 0;
		float bw = -1.0;
		for (int i = 0; i < MATERIAL_COUNT; i++) {
			if (wf[i] > bw) { bw = wf[i]; best = i; }
		}
		top_i[k] = best;
		top_w[k] = max(bw, 0.0);
		wf[best] = -1.0;
	}

	float dist = length(world_pos - CAMERA_POSITION_WORLD);
	float far_t = smoothstep(far_blend_start, far_blend_end, dist);
	// Far away puddles are smaller than a pixel: fade them into plain wet ground (no sparkling specks).
	puddle *= 1.0 - smoothstep(far_blend_start * 4.0, far_blend_end * 4.0, dist);

	// --- Sample and height-blend ---
	vec3 col[4];
	vec3 nrm[4];
	float score[4];
	float best_score = -1.0;
	for (int k = 0; k < 4; k++) {
		col[k] = vec3(0.0);
		nrm[k] = n;
		score[k] = -1.0;
		// A material blends in only if its score (weight + texture height * height_blend) comes within blend_softness
		// of the best, which is at least top_w[0]. Materials that can't are skipped without changing the result.
		if (top_w[k] <= 0.001 || top_w[k] + height_blend <= top_w[0] - blend_softness) continue;
		int m = top_i[k];
		Sample s;
		if (material_tint[m].a > 0.5) s = sample_triplanar(m, world_pos, n);
		else s = sample_planar(m, world_pos, n, far_t);
		col[k] = stylize(s.albedo_height.rgb, m, t);
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
	float mv = noise_a * 0.6 + noise_b * 0.4 - 0.5;
	albedo *= 1.0 + mv * macro_strength * 2.0;
	albedo = mix(albedo, albedo * vec3(1.06, 1.0, 0.88), clamp(mv * 2.0 + 0.5, 0.0, 1.0) * macro_strength * 2.0);
	float breakup = texture(macro_noise, world_pos.xz / breakup_scale + vec2(0.13, 0.57)).r - 0.5;
	albedo *= 1.0 + breakup * breakup_strength * 2.0;
	// Wet ground is darker; puddles a little more (standing water over the ground).
	float wet_lum = dot(albedo, vec3(0.299, 0.587, 0.114));
	albedo = max(mix(vec3(wet_lum), albedo, 1.0 + wet_saturation * t.wet_look), vec3(0.0));
	albedo *= 1.0 - wet_darken * t.wet_look;
	albedo = mix(albedo, puddle_color, puddle * puddle_tint) * (1.0 - puddle_darken * puddle);
	// Smoother where wet, so it reflects the sky and catches the sun glint in light(). The fine noise jitters the
	// roughness so the glint glitters in patches (light() only sees ROUGHNESS).
	float rough = roughness;
	float spec = 0.2;
	if (wet_shine) {
		float sparkle = mix(1.0, smoothstep(0.35, 0.65, t.noise_fine), glint_sparkle);
		rough = mix(roughness, mix(wet_roughness, 1.0, 0.35 * (1.0 - sparkle)), t.wet_look);
		rough = mix(rough, puddle_roughness, puddle);
		spec = mix(mix(0.2, wet_specular, t.wet_look), puddle_specular, puddle);
	}

	vec3 col_out = albedo;
	if (slot_debug >= 0) col_out = mix(albedo * 0.35, vec3(1.0, 0.3, 0.6), debug_cov);
	if (terrain_debug == 2) {
		// Cost: textures blended here (green 1, yellow 2, orange 3, red 4). Each one is several texture reads.
		float blended = 0.0;
		for (int k = 0; k < 4; k++) blended += score[k] >= 0.0 ? 1.0 : 0.0;
		col_out = blended <= 1.0 ? vec3(0.1, 0.6, 0.1) : blended <= 2.0 ? vec3(0.9, 0.9, 0.1)
				: blended <= 3.0 ? vec3(1.0, 0.5, 0.0) : vec3(1.0, 0.0, 0.0);
	}
	if (terrain_debug == 1) col_out = debug_material_color(top_i[0]) * (0.75 + 0.25 * n.y);
	if (ground_debug > 0) {
		float v = ground_debug == 1 ? clamp(1.0 - t.shore / 64.0, 0.0, 1.0) : ground_debug == 2 ? gm.g
				: ground_debug == 3 ? gm.b : ground_debug == 4 ? gm.a : ground_debug == 5 ? t.wet : t.wet_look;
		col_out = vec3(v * v);
	}

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
		float tt = clamp(1.0 - d / brush_radius, 0.0, 1.0);
		float fill = tt * tt * (3.0 - 2.0 * tt) * 0.18;
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
			if (anchor_band > 0.0) {
				// Channel corridor: its edges plus a faint fill, over the full width of the cut.
				float edge = 1.0 - clamp(abs(dl - anchor_band) / (fw * 1.5), 0.0, 1.0);
				line = max(max(line, edge), step(dl, anchor_band) * 0.25);
			}
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
		// With the horizon ring the corners stay square, so the band meets the ring's (which fades outward from the border).
		vec2 rel_sq = world_pos.xz - terrain_origin;
		vec2 dd_sq = min(rel_sq, terrain_size - rel_sq);
		float d = (horizon_enabled ? min(dd_sq.x, dd_sq.y) : edge_distance(world_pos.xz)) - billow * edge_fog_wobble;
		fog = 1.0 - smoothstep(0.0, edge_fog_width, d);
		fog *= fog;
		// With the horizon ring the band is lighter (the ring's hills carry on past the border); without it everything
		// outside the bounds is fog.
		// (It only hides the seam up close; from far out it would frame the map, so it fades with the camera distance.)
		if (horizon_enabled) fog *= horizon_edge_fog * (1.0 - smoothstep(3000.0, 15000.0, length(world_pos - CAMERA_POSITION_WORLD)));
		else fog = max(fog, step(edge_distance(world_pos.xz), 0.0));
		fog_col *= 1.0 + (billow - 0.5) * edge_fog_cloudiness * 2.0;
	} else {
		// No fog: a thin line on the border, square corners, the same width in pixels at any distance.
		vec2 rel = world_pos.xz - terrain_origin;
		vec2 dd = min(rel, terrain_size - rel);
		float de = min(dd.x, dd.y);
		float px = max(fwidth(de), 1e-4);
		float edge_line = 1.0 - smoothstep(edge_line_pixels * px, (edge_line_pixels + 1.0) * px, de);
		col_out = mix(col_out, edge_line_color, edge_line);
		glow += edge_line_color * edge_line * 0.5;
	}

	ALBEDO = col_out * (1.0 - fog);
	NORMAL = normalize((VIEW_MATRIX * vec4(normal_ws, 0.0)).xyz);
	EMISSION = glow * (1.0 - fog) + fog_col * fog;
	ROUGHNESS = mix(rough, 1.0, fog);
	SPECULAR = spec * (1.0 - fog);
}

#ifndef TERRAIN_CUSTOM_LIGHT
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
	// Sun glint on wet, smooth ground: a crisp toon highlight. Dry ground (roughness ~0.9) gets none.
	float smooth_g = 1.0 - smoothstep(0.2, 0.6, ROUGHNESS);
	if (wet_shine && smooth_g > 0.0) {
		float nh = max(dot(NORMAL, normalize(LIGHT + VIEW)), 0.0);
		float g = smoothstep(0.5, 0.6, pow(nh, glint_size * mix(0.4, 1.0, smooth_g)));
		SPECULAR_LIGHT += LIGHT_COLOR * ATTENUATION * g * glint_strength * smooth_g * max(ndl, 0.0);
	}
}
#endif
