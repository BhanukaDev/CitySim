// CitySim terrain SDK: the engine side of every terrain theme shader. A theme shader looks like:
//
//   shader_type spatial;
//   #include "res://addons/citysim_terrain/shaders/sdk/terrain_core.gdshaderinc"
//   #include "materials.gdshaderinc"          // written by the theme baker: MATERIAL_COUNT, MAT_<ID>, SLOT_<NAME>
//   void terrain_auto(TerrainInputs t, inout vec4 w[MATERIAL_PACKS]) { lay(w, MAT_ROCK, above(t.slope, 45.0, 8.0)); }
//   #include "res://addons/citysim_terrain/shaders/sdk/terrain_fragment.gdshaderinc"
//
// This file holds Terrain3D's vertex stage and data lookups (MIT, from addons/terrain_3d/extras/shaders/minimum.gdshader),
// the per-material data the game uploads from the theme, sampling helpers and the TerrainInputs a theme reads.
// Everything a theme may use is documented in terrain_sdk/README.md. Don't copy this file into a theme: include it,
// so themes keep working when the engine side changes.
render_mode blend_mix, depth_draw_opaque, cull_back, skip_vertex_transform;

#define SKIP_PASS 0
#define VERTEX_PASS 1
#define FRAGMENT_PASS 2

#if CURRENT_RENDERER == RENDERER_COMPATIBILITY
	#define fma(a, b, c) ((a) * (b) + (c))
	#define dFdxCoarse(a) dFdx(a)
	#define dFdyCoarse(a) dFdy(a)
#endif

// Most materials a theme can have (TerrainTheme.MaxMaterials), and their weights packed four per vec4.
const int MAX_MATERIALS = 16;
#define MATERIAL_PACKS 4
// Erosion slots (TerrainTheme.Slots, same order). Each lays one theme material from the erosion/water masks.
const int SLOT_COUNT = 9;
const int SLOT_I_DEPOSIT = 0, SLOT_I_DEPOSIT_HEAVY = 1, SLOT_I_SCOUR = 2, SLOT_I_SCOUR_HEAVY = 3,
		SLOT_I_SHORE_FRINGE = 4, SLOT_I_SHORE = 5, SLOT_I_STREAM_BANK = 6, SLOT_I_STREAM_BED = 7, SLOT_I_WET = 8;
// Average brightness the baker normalises every material's colour to (ThemeBaker.NeutralGrey).
const float NEUTRAL_GREY = 0.4;

// --- Private uniforms, set by Terrain3D ---
uniform vec3 _camera_pos = vec3(0.f);
uniform float _mesh_size = 48.f;
uniform uint _background_mode = 1u; // NONE = 0, FLAT = 1, NOISE = 2
uniform uint _mouse_layer = 0x80000000u; // Layer 32
uniform float _vertex_spacing = 1.0;
uniform float _vertex_density = 1.0; // = 1/_vertex_spacing
uniform float _region_size = 1024.0;
uniform float _region_texel_size = 0.0009765625; // = 1/1024
uniform int _region_map_size = 32;
uniform int _region_map[1024];
uniform highp sampler2DArray _height_maps : repeat_disable;
// One uint per vertex (as float bits), laid out like SplatMap.cs.
uniform highp sampler2DArray _control_maps : repeat_disable;
// Terrain3D's colour map, used for our ground masks (Terrain3DBridge.PushGround): R shore, G gully, B wear, A deposit.
// Encodings: cs_find_water in native/erosion/erosion.h. Raw bytes, so no source_color.
uniform highp sampler2DArray _color_maps : repeat_disable;

// --- Set by the game (Terrain.cs); not for themes to set ---
group_uniforms Game;
uniform float height_min = 0.0;
uniform float height_max = 250.0;
// Terrain XZ bounds (world metres), for the edge fog.
uniform vec2 terrain_origin = vec2(0.0);
uniform vec2 terrain_size = vec2(2048.0);
// The theme's baked materials (ThemeBaker): RGB = colour normalised to NEUTRAL_GREY, A = height; and normals.
uniform sampler2DArray albedo_height_array : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2DArray normal_array : filter_linear_mipmap_anisotropic, repeat_enable;
// Per material (TerrainMaterial): rgb = tint (linear), a = 1 when sampled triplanar.
uniform vec4 material_tint[16];
// Per material: tile size near, tile size far (m), detail contrast factor, normal strength factor.
uniform vec4 material_params[16];
// Per slot (ErosionSlot): strength, edge, 1 / fade, edge noise amount.
uniform vec4 slot_edge[9];
// Per slot: noise field (0 fine, 1 patchy, 2 medium, 3 large), max slope (degrees), 1 / slope fade, slope limit on (1/0).
uniform vec4 slot_slope[9];
// Debug views: 1 strongest material, 2 textures blended per pixel (cost).
uniform int terrain_debug = 0;
// Shows one slot's coverage (slot index), -1 off.
uniform int slot_debug = -1;
// 1 shore, 2 gully, 3 wear, 4 deposit, 5 wet paint, 6 wet look (paint + rain): shows one ground mask in greyscale.
uniform int ground_debug : hint_range(0, 6) = 0;
// Ground marks from the water simulation (WaterSim, cs_water_read_ground in native/water/water.h), one texel per water
// cell from the terrain origin: R distance to water (m × 4 / 255), G wet paint (how long water has stood there).
uniform sampler2D water_ground : filter_linear, repeat_disable;
uniform float water_ground_cell = 3.5;
uniform bool has_water_ground = false;

group_uniforms Textures;
// Seamless low-frequency noise for large-scale variation.
uniform sampler2D macro_noise : hint_default_white, filter_linear_mipmap, repeat_enable;

group_uniforms Tiling;
uniform float far_blend_start = 40.0;
uniform float far_blend_end = 250.0;
uniform float macro_scale = 700.0;
// Fine brightness breakup (metres per repeat).
uniform float breakup_scale = 45.0;
// Size of the fine noise (TerrainInputs.noise_fine, ~10 m) and the patchy noise (noise_patchy, ~35 m).
uniform float edge_noise_scale = 11.0;
uniform float patchy_noise_scale = 35.0;

group_uniforms Style;
uniform float detail_contrast : hint_range(0.0, 1.5) = 0.6;
// Keeps some of each texture's own colour (0 = only its brightness, 1 = all of it).
uniform float detail_saturation : hint_range(0.0, 1.0) = 0.65;
uniform float normal_strength : hint_range(0.0, 2.0) = 1.0;
// Large-scale brightness and warmth variation.
uniform float macro_strength : hint_range(0.0, 0.5) = 0.16;
uniform float breakup_strength : hint_range(0.0, 0.5) = 0.06;
// How much texture height decides material borders (grass fills between stones, etc.).
uniform float height_blend : hint_range(0.0, 1.0) = 0.12;
// Width of height-blended borders: lower is crisper.
uniform float blend_softness : hint_range(0.01, 0.6) = 0.45;
uniform float roughness = 0.9;

group_uniforms Lighting;
// Used by the default light() in terrain_fragment.gdshaderinc (a theme can define TERRAIN_CUSTOM_LIGHT and write its own).
uniform float light_wrap : hint_range(0.0, 1.0) = 0.35;
// 0 = smooth shading, 1 = a soft two-tone ramp.
uniform float toon_bands : hint_range(0.0, 1.0) = 0.4;
uniform float band_softness : hint_range(0.01, 0.5) = 0.2;
// Warm tint where the light grazes the surface.
uniform vec3 terminator_tint : source_color = vec3(1.0, 0.72, 0.5);
uniform float terminator_strength : hint_range(0.0, 1.0) = 0.3;
// Cool fill in shadow instead of grey.
uniform vec3 shadow_tint : source_color = vec3(0.62, 0.72, 1.0);
uniform float shadow_tint_strength : hint_range(0.0, 1.0) = 0.5;

group_uniforms WetGround;
// Wet ground reads darker, smoother and catches the sun. Driven by the sim's wet paint (TerrainInputs.wet), the band at
// the waterline, and the whole map's wetness from rain (global_wetness, set by the game). Keep it subtle: too shiny
// turns plastic.
// How much the sim's wet paint counts (0 = only rain wets the ground).
uniform float wet_amount : hint_range(0.0, 1.0) = 1.0;
// Width (m) of the damp bank beside water: fully wet near the water, fading out by this distance (each metre above
// the water counts as 4, so steep banks stay narrower).
uniform float wet_shore_band = 16.0;
// Albedo multiplier lost when fully wet (damp soil and sand are darker).
uniform float wet_darken : hint_range(0.0, 0.8) = 0.5;
// Wet ground's colour gets richer (more saturated) by this much.
uniform float wet_saturation : hint_range(0.0, 1.0) = 0.3;
uniform float wet_roughness : hint_range(0.0, 1.0) = 0.5;
uniform float wet_specular : hint_range(0.0, 1.0) = 0.3;
// Sun glint on smooth ground (toon highlight in light()): strength, and its size (higher = smaller, crisper).
uniform float glint_strength : hint_range(0.0, 4.0) = 0.8;
uniform float glint_size : hint_range(1.0, 400.0) = 150.0;
// 0 = the glint shines evenly, 1 = only in scattered patches (fine noise).
uniform float glint_sparkle : hint_range(0.0, 1.0) = 0.6;
// Rain: from this global wetness, flat low ground (below puddle_max_slope degrees, near stream beds, gullies and water)
// collects puddles in patches.
uniform float puddle_start : hint_range(0.0, 1.0) = 0.55;
uniform float puddle_max_slope = 4.0;
uniform float puddle_roughness : hint_range(0.0, 1.0) = 0.12;
// Puddles: sky reflection (specular) and how much darker than wet ground (standing water over dark mud).
uniform float puddle_specular : hint_range(0.0, 1.0) = 0.35;
uniform float puddle_darken : hint_range(0.0, 0.8) = 0.35;
// Puddles take on this colour (muddy water) by puddle_tint.
uniform vec3 puddle_color : source_color = vec3(0.30, 0.33, 0.36);
uniform float puddle_tint : hint_range(0.0, 1.0) = 0.5;
// Set by the game: the whole map's wetness from rain (0-1), and the Wet ground shine graphics setting.
uniform float global_wetness : hint_range(0.0, 1.0) = 0.0;
uniform bool wet_shine = true;

group_uniforms EdgeFog;
// A fog bank that swallows the map border, so its hard edges and corners never show. Everything outside
// the bounds (the TerrainHorizon ring in Fog mode) is pure fog; inside, the ground fades into it near the border.
// Set by the game (Terrain.EdgeFog): on in game mode, off in the Map Editor unless previewing.
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
// With the fog off (the Map Editor's default), a thin line marks the border instead, about this many pixels wide.
uniform vec3 edge_line_color : source_color = vec3(1.0, 0.95, 0.8);
uniform float edge_line_pixels = 2.0;

group_uniforms Horizon;
// The horizon ring (TerrainHorizon, shaders/terrain_horizon.gdshader): low-poly hills past the map border that fade into
// the haze, shown in game mode instead of the plain fog bank. Terrain.cs copies these to the ring's material.
// Set by the game: on when the ring is shown; the fog bank at the border then thins to horizon_edge_fog.
uniform bool horizon_enabled = false;
uniform float horizon_edge_fog : hint_range(0.0, 1.0) = 0.25;
// Hill height past the border, as a share of the map's height range (read when the ring is built).
uniform float horizon_relief : hint_range(0.0, 1.0) = 0.35;
// The ring's ground: this material on flat ground, horizon_steep_material (-1 none) above horizon_steep_slope degrees,
// both multiplied by horizon_tint.
uniform int horizon_flat_material = 0;
uniform int horizon_steep_material = -1;
uniform float horizon_steep_slope = 28.0;
uniform vec3 horizon_tint : source_color = vec3(1.0);
// Metres past the border where the haze starts and where the ring is lost in it.
uniform float horizon_haze_start = 400.0;
uniform float horizon_haze_end = 7000.0;
// The sea past the border (where the map has a Sea source).
uniform vec3 horizon_sea_color : source_color = vec3(0.10, 0.22, 0.30);

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
// Above 0: the guide from the anchor to the brush is a band this wide on each side (Channel corridor).
uniform float anchor_band = 0.0;
uniform vec3 brush_color : source_color = vec3(0.35, 0.85, 1.0);
group_uniforms;

// World position of the vertex (Terrain3D's editor tools expect these names).
varying vec3 v_vertex;
varying float v_vertex_xz_dist;

// What a theme's terrain_auto() reads at each point of the ground.
struct TerrainInputs {
	vec3 pos;           // world position (m)
	vec3 normal;        // world normal
	float height;       // height (m)
	float h01;          // height within the map's lowest..highest point, 0-1
	float slope;        // degrees
	float shore;        // distance to water (simulated lakes, sea, streams) and to river beds (m; each metre above the
	                    // water counts as 4; 64 and beyond = 64)
	float gully;        // distance to gully and stream beds (m; negative inside the bed; 24 and beyond = 24)
	float wear;         // how hard running water scours (log stream power; ~10% of an eroded map is above 1.8)
	float deposit;      // sediment settling where water slows: fans and deltas (~1% of the ground is above 1.2)
	float wet;          // 0-1: how long simulated water has stood or run here (fades slowly once dry)
	float wet_look;     // 0-1: how wet the ground looks (wet paint, the waterline band and rain); darkens and shines it
	float noise_large;  // 0-1, patches a few hundred metres across
	float noise_medium; // 0-1, ~150 m
	float noise_fine;   // 0-1, ~10 m
	float noise_patchy; // 0-1, ~35 m
};

// --- Helpers for themes ---

// smoothstep(0, 1, t) without a division.
float ramp(float t) {
	t = clamp(t, 0.0, 1.0);
	return t * t * (3.0 - 2.0 * t);
}
// 1 at and above `from`, fading to 0 over `fade` below it.
float above(float v, float from, float fade) { return ramp((v - from + fade) / fade); }
// 1 at and below `to`, fading to 0 over `fade` above it.
float below(float v, float to, float fade) { return 1.0 - ramp((v - to) / fade); }

// Lays material m over the weights by coverage c (0-1): everything below loses c, m gains it.
void lay(inout vec4 w[MATERIAL_PACKS], int m, float c) {
	c = clamp(c, 0.0, 1.0);
	for (int j = 0; j < MATERIAL_PACKS; j++) w[j] *= 1.0 - c;
	w[m >> 2] += c * vec4(equal(ivec4(m & 3), ivec4(0, 1, 2, 3)));
}

// World XZ (in vertices) -> ivec3(texel in region, region layer), layer -1 outside every region.
ivec3 get_index_coord(const vec2 uv, const int search) {
	vec2 r_uv = round(uv);
	vec2 o_uv = mod(r_uv,_region_size);
	ivec2 pos;
	int bounds, layer_index = -1;
	for (int i = -1; i < clamp(search, SKIP_PASS, FRAGMENT_PASS); i++) {
		if ((layer_index == -1 && _background_mode == 0u ) || i < 0) {
			r_uv -= i == -1 ? vec2(0.0) : vec2(float(o_uv.x <= o_uv.y), float(o_uv.y <= o_uv.x));
			pos = ivec2(floor((r_uv) * _region_texel_size)) + (_region_map_size / 2);
			bounds = int(uint(pos.x | pos.y) < uint(_region_map_size));
			layer_index = (_region_map[ pos.y * _region_map_size + pos.x ] * bounds - 1);
		}
	}
	return ivec3(ivec2(mod(r_uv,_region_size)), layer_index);
}

void vertex() {
	// Flat clipmap vertex in world space.
	v_vertex = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	v_vertex_xz_dist = length(v_vertex.xz - _camera_pos.xz);

	// Geomorph between LODs.
	float scale = MODEL_MATRIX[0][0];
	float vertex_lerp = smoothstep(0.55, 0.95, (v_vertex_xz_dist / scale - _mesh_size - 4.0) / (_mesh_size - 2.0));
	vec2 v_fract = fract(VERTEX.xz * 0.5) * 2.0;
	vec2 shift = (scale < _vertex_spacing + 1e-6) ?
		mix(v_fract, vec2(v_fract.x, -v_fract.y),
			round(fract(round(mod(v_vertex.z * _vertex_density, 4.0)) *
			round(mod(v_vertex.x * _vertex_density, 4.0)) * 0.25))
			) :
		v_fract * round((fract(v_vertex.xz * 0.25 / scale) - 0.5) * 4.0);
	vec2 start_pos = v_vertex.xz * _vertex_density;
	vec2 end_pos = (v_vertex.xz - shift * scale) * _vertex_density;
	v_vertex.xz -= shift * scale * vertex_lerp;

	// UV = world position in vertices; UV2 = region space.
	UV = v_vertex.xz * _vertex_density;
	UV2 = fma(UV, vec2(_region_texel_size), vec2(0.5 * _region_texel_size));

	// Holes (padding past the map) and everything outside the regions are dropped (NaN vertex).
	ivec3 v_region = get_index_coord(start_pos, VERTEX_PASS);
	uint control = floatBitsToUint(texelFetch(_control_maps, v_region, 0)).r;
	bool hole = bool(control >>2u & 0x1u);
	if ( !(CAMERA_VISIBLE_LAYERS == _mouse_layer) &&
			(hole || (_background_mode == 0u && v_region.z < 0))) {
		v_vertex.x = 0. / 0.;
	} else {
		ivec3 uv_a = get_index_coord(start_pos, VERTEX_PASS);
		ivec3 uv_b = get_index_coord(end_pos, VERTEX_PASS);
		v_vertex.y = mix(texelFetch(_height_maps, uv_a, 0).r, texelFetch(_height_maps, uv_b, 0).r, vertex_lerp);
	}

	VERTEX = (VIEW_MATRIX * vec4(v_vertex, 1.0)).xyz;
	NORMAL = normalize((MODELVIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
	BINORMAL = normalize((MODELVIEW_MATRIX * vec4(BINORMAL, 0.0)).xyz);
	TANGENT = normalize((MODELVIEW_MATRIX * vec4(TANGENT, 0.0)).xyz);
}

// Adds one vertex's painted materials (SplatMap.cs bits: 27-31 base, 22-26 overlay, 14-21 blend, 6-13 coverage),
// scaled by its bilinear weight k. Indices past the theme's materials (paint kept from another theme) are skipped,
// so automatic ground shows there.
void add_paint(inout vec4 p[MATERIAL_PACKS], ivec3 at, float k, int count) {
	if (at.z < 0) return;
	uint c = floatBitsToUint(texelFetch(_control_maps, at, 0).r);
	float cov = float(c >> 6u & 0xFFu) / 255.0 * k;
	if (cov <= 0.0) return;
	float blend = float(c >> 14u & 0xFFu) / 255.0;
	int base = int(c >> 27u & 0x1Fu);
	int over = int(c >> 22u & 0x1Fu);
	if (base < count) p[base >> 2] += cov * (1.0 - blend) * vec4(equal(ivec4(base & 3), ivec4(0, 1, 2, 3)));
	if (over < count) p[over >> 2] += cov * blend * vec4(equal(ivec4(over & 3), ivec4(0, 1, 2, 3)));
}

// World normal from the heightmap, bilinear between the four surrounding vertices up close.
// Also returns the four vertex coords and their weights, for the painted materials and ground masks.
vec3 terrain_normal(vec2 uv, out ivec3 index[4], out vec4 weights) {
	const vec3 offsets = vec3(0, 1, 2);
	vec2 index_id = floor(uv);
	vec2 weight = fract(uv);
	vec2 invert = 1.0 - weight;
	weights = vec4(invert.x * weight.y, weight.x * weight.y, weight.x * invert.y, invert.x * invert.y);

	vec3 base_ddx = dFdxCoarse(v_vertex);
	vec3 base_ddy = dFdyCoarse(v_vertex);
	float region_mip = log2(max(length(base_ddx.xz), length(base_ddy.xz)) * _vertex_density);

	index[0] = get_index_coord(index_id + offsets.xy, FRAGMENT_PASS);
	index[1] = get_index_coord(index_id + offsets.yy, FRAGMENT_PASS);
	index[2] = get_index_coord(index_id + offsets.yx, FRAGMENT_PASS);
	index[3] = get_index_coord(index_id + offsets.xx, FRAGMENT_PASS);

	float h0 = texelFetch(_height_maps, index[0], 0).r;
	float h2 = texelFetch(_height_maps, index[2], 0).r;
	float h3 = texelFetch(_height_maps, index[3], 0).r;
	vec3 n3 = normalize(vec3(h3 - h2, _vertex_spacing, h3 - h0));
	// Far away a pixel covers several vertices: one lookup is enough.
	if (region_mip >= 0.0) return n3;

	float h1 = texelFetch(_height_maps, index[1], 0).r;
	float h4 = texelFetch(_height_maps, get_index_coord(index_id + offsets.yz, FRAGMENT_PASS), 0).r;
	float h5 = texelFetch(_height_maps, get_index_coord(index_id + offsets.zy, FRAGMENT_PASS), 0).r;
	float h6 = texelFetch(_height_maps, get_index_coord(index_id + offsets.zx, FRAGMENT_PASS), 0).r;
	float h7 = texelFetch(_height_maps, get_index_coord(index_id + offsets.xz, FRAGMENT_PASS), 0).r;
	vec3 n0 = normalize(vec3(h0 - h1, _vertex_spacing, h0 - h7));
	vec3 n1 = normalize(vec3(h1 - h5, _vertex_spacing, h1 - h4));
	vec3 n2 = normalize(vec3(h2 - h6, _vertex_spacing, h2 - h1));
	return normalize(n0 * weights[0] + n1 * weights[1] + n2 * weights[2] + n3 * weights[3]);
}

// Distance (metres) inside the terrain border, with rounded corners; negative outside.
float edge_distance(vec2 xz) {
	vec2 rel = xz - terrain_origin;
	vec2 dd = min(rel, terrain_size - rel);
	float r = edge_fog_corner_radius;
	return min(min(dd.x, dd.y), r - length(max(vec2(r) - dd, vec2(0.0))));
}

// Unpacks a tangent-space normal (OpenGL convention) and scales its bumpiness.
vec3 unpack_normal(vec3 t, float strength) {
	vec3 n = t * 2.0 - 1.0;
	n.xy *= normal_strength * strength;
	return n;
}

struct Sample {
	vec4 albedo_height;
	vec3 normal; // world space
};

// Top-down projection at two scales, blended by distance to hide tiling.
Sample sample_planar(int m, vec3 p, vec3 n, float far_t) {
	vec4 mp = material_params[m];
	vec3 uv_near = vec3(p.xz / mp.x, float(m));
	vec3 uv_far = vec3(p.xz / mp.y, float(m));
	vec4 ah = mix(texture(albedo_height_array, uv_near), texture(albedo_height_array, uv_far), far_t);
	vec3 tn = unpack_normal(mix(texture(normal_array, uv_near).rgb, texture(normal_array, uv_far).rgb, far_t), mp.w);
	// Whiteout blend onto the surface normal (Y projection: u = +X, v = +Z).
	vec3 w = vec3(tn.xy + n.xz, abs(tn.z) * n.y);
	Sample s;
	s.albedo_height = ah;
	s.normal = normalize(vec3(w.x, w.z, w.y));
	return s;
}

// Triplanar projection for cliffs, so steep faces don't stretch. Two scales are mixed by low-frequency
// noise and the coordinates are warped, so no repeat of the texture is recognisable.
Sample sample_triplanar(int m, vec3 p, vec3 n) {
	vec4 mp = material_params[m];
	float layer = float(m);
	vec3 bw = pow(abs(n), vec3(4.0));
	bw /= bw.x + bw.y + bw.z;
	vec2 wx = vec2(texture(macro_noise, p.zy / 37.0).r, texture(macro_noise, p.zy / 23.0 + 0.4).r) - 0.5;
	vec2 wy = vec2(texture(macro_noise, p.xz / 37.0).r, texture(macro_noise, p.xz / 23.0 + 0.4).r) - 0.5;
	vec2 wz = vec2(texture(macro_noise, p.xy / 37.0).r, texture(macro_noise, p.xy / 23.0 + 0.4).r) - 0.5;
	float mx = smoothstep(0.3, 0.7, texture(macro_noise, p.zy / 61.0 + 0.2).r);
	float my = smoothstep(0.3, 0.7, texture(macro_noise, p.xz / 61.0 + 0.2).r);
	float mz = smoothstep(0.3, 0.7, texture(macro_noise, p.xy / 61.0 + 0.2).r);
	vec3 px = vec3(p.zy / mp.x + wx * 0.35, layer);
	vec3 py = vec3(p.xz / mp.x + wy * 0.35, layer);
	vec3 pz = vec3(p.xy / mp.x + wz * 0.35, layer);
	vec3 qx = vec3(p.zy / mp.y + 0.31, layer);
	vec3 qy = vec3(p.xz / mp.y + 0.31, layer);
	vec3 qz = vec3(p.xy / mp.y + 0.31, layer);
	vec4 ah = mix(texture(albedo_height_array, px), texture(albedo_height_array, qx), mx) * bw.x
			+ mix(texture(albedo_height_array, py), texture(albedo_height_array, qy), my) * bw.y
			+ mix(texture(albedo_height_array, pz), texture(albedo_height_array, qz), mz) * bw.z;
	vec3 tx = unpack_normal(mix(texture(normal_array, px).rgb, texture(normal_array, qx).rgb, mx), mp.w);
	vec3 ty = unpack_normal(mix(texture(normal_array, py).rgb, texture(normal_array, qy).rgb, my), mp.w);
	vec3 tz = unpack_normal(mix(texture(normal_array, pz).rgb, texture(normal_array, qz).rgb, mz), mp.w);
	tx = vec3(tx.xy + n.zy, abs(tx.z) * n.x);
	ty = vec3(ty.xy + n.xz, abs(ty.z) * n.y);
	tz = vec3(tz.xy + n.xy, abs(tz.z) * n.z);
	Sample s;
	s.albedo_height = ah;
	s.normal = normalize(tx.zyx * bw.x + ty.xzy * bw.y + tz.xyz * bw.z);
	return s;
}

// Two octaves of fine noise at unrelated scales (so the wobble doesn't repeat visibly), about -0.5..0.5.
float edge_noise(vec2 xz, float size) {
	size = max(size, 0.1);
	return texture(macro_noise, xz / size + vec2(0.61, 0.23)).r * 0.6
			+ texture(macro_noise, xz / (size * 2.7) + vec2(0.11, 0.89)).r * 0.4 - 0.5;
}

// Picks component i of v (0-3) without dynamic indexing, which GPUs may spill to memory.
float pick4(vec4 v, int i) { return dot(v, vec4(equal(ivec4(i), ivec4(0, 1, 2, 3)))); }

// Distinct colours for the "strongest material" debug view.
vec3 debug_material_color(int i) {
	vec3 k = fract(float(i) * 0.618034 + vec3(0.0, 0.333, 0.667));
	return clamp(abs(k * 6.0 - 3.0) - 1.0, 0.0, 1.0) * 0.7 + 0.2;
}
