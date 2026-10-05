// Road asphalt: dark binder with exposed stones, aged by oxidation, with patches, wheel tracks polished lighter in
// every lane and an oil stripe down each lane's middle, oil spots, and cracks that grow with the road's age (COLOR.r,
// set by the game: 0 = new, no cracks). Tracks and oil follow the lane coordinate (UV2.x) so they suit any lane count;
// junctions get a light general scuffing, and their tracks from the road_wear overlay. Where cars queue for a junction
// (COLOR.g / COLOR.b per direction, split across the road at COLOR.a) the lane's middle is stained with oil and rubber.
// Colours: worn asphalt is about albedo 0.12 (sRGB ~90), fresh 0.04 (sRGB ~55).
shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back, diffuse_burley, specular_schlick_ggx;

#include "res://addons/citysim_roads/content/roads/shaders/road_common.gdshaderinc"

group_uniforms Colour;
uniform vec3 binder_colour : source_color = vec3(0.2, 0.196, 0.19);
/// Binder after years of sun: lighter and greyer, a little warm from the aggregate.
uniform vec3 aged_colour : source_color = vec3(0.4, 0.388, 0.365);
/// How weathered every road looks, 0 = fresh dark asphalt, 1 = old grey. A road's age adds to it.
uniform float weathering : hint_range(0.0, 1.0) = 0.6;
uniform vec3 stone_colour : source_color = vec3(0.52, 0.5, 0.465);
/// How much of the stone shows through the binder.
uniform float stone_amount : hint_range(0.0, 1.0) = 0.35;
/// Tone change between patches and repairs.
uniform float patch_strength : hint_range(0.0, 0.5) = 0.1;
uniform float roughness : hint_range(0.0, 1.0) = 0.92;
group_uniforms;

group_uniforms Wear;
/// Wheel track centres from the lane's middle, in lanes (0.27 of a 3 m lane = 0.8 m, half a car's track).
uniform float track_offset : hint_range(0.0, 0.5) = 0.27;
/// Half the width of a wheel track, in lanes.
uniform float track_width : hint_range(0.0, 0.3) = 0.11;
/// Wheel tracks: binder worn off, stones polished.
uniform vec3 track_colour : source_color = vec3(0.43, 0.42, 0.405);
uniform float track_strength : hint_range(0.0, 1.0) = 0.45;
uniform float track_roughness : hint_range(0.0, 1.0) = 0.72;
/// The drip stripe down the middle of each lane.
uniform vec3 oil_colour : source_color = vec3(0.19, 0.188, 0.185);
uniform float oil_width : hint_range(0.0, 0.3) = 0.09;
uniform float oil_strength : hint_range(0.0, 1.0) = 0.4;
/// Oil spots (where cars stand) anywhere on the lanes.
uniform float spot_strength : hint_range(0.0, 1.0) = 0.35;
/// Scuffing across a junction, where cars turn.
uniform float junction_wear : hint_range(0.0, 1.0) = 0.15;
/// Oil and rubber in the middle of lanes where cars stand waiting for a junction.
uniform float queue_strength : hint_range(0.0, 1.0) = 0.55;
group_uniforms;

group_uniforms Cracks;
/// How readily roads crack as they age (1 = a road at age 1 is cracked over most of it). Age itself comes from the game.
uniform float crack_amount : hint_range(0.0, 2.0) = 1.0;
/// Cracks every road has however new (a road in use for a while); the game's age adds to it.
uniform float base_age : hint_range(0.0, 1.0) = 0.45;
/// Crack width in metres.
uniform float crack_width : hint_range(0.001, 0.05) = 0.01;
uniform vec3 crack_colour : source_color = vec3(0.09, 0.088, 0.085);
group_uniforms;

group_uniforms Scale;
/// Detail texture tiles per metre (0.5 = one 2 m tile: stones about 1.5 cm).
uniform float detail_scale = 0.5;
/// Macro texture tiles per metre (0.0625 = one 16 m tile).
uniform float macro_scale = 0.0625;
uniform float normal_strength : hint_range(0.0, 2.0) = 0.8;
group_uniforms;

void vertex() {
	ROAD_VERTEX
}

void fragment() {
	vec2 p = surface_coord(UV, UV2.x);
	float dist = length(VERTEX);
	vec4 macro = texture(macro_tex, p * macro_scale);
	// A much larger, offset read: which stretches of road are newer or older, so repeats don't line up.
	vec4 region = texture(macro_tex, p * macro_scale * 0.137 + vec2(0.31, 0.67));
	float to_crack = macro.r * 0.10; // metres (the texture stores up to 10 cm)
	float crack_fw = fwidth(to_crack);
	float lane_fw = fwidth(UV2.x);
	float road_age = COLOR.r;
	float wear = UV2.y * mix(0.45, 1.25, region.b);

	vec3 binder = mix(binder_colour, aged_colour, clamp(weathering + road_age * 0.3 + (region.g - 0.5) * 0.3, 0.0, 1.0));
	binder *= 1.0 + (macro.b - 0.5) * 2.0 * patch_strength;

	// Wheel tracks and the oil stripe, by where this is across its lane.
	float track = 0.0, oil = 0.0, spots = 0.0;
	if (UV2.x > -0.5) {
		float f = fract(UV2.x) - 0.5;
		float t = abs(abs(f) - track_offset);
		float sharp_track = 1.0 - smoothstep(track_width * 0.3, track_width, t);
		float sharp_oil = 1.0 - smoothstep(oil_width * 0.2, oil_width, abs(f));
		// Under a pixel per feature, use their average cover instead.
		float lanes_visible = pixel_fade(lane_fw, track_width);
		track = mix(track_width * 2.6, sharp_track, lanes_visible);
		oil = mix(oil_width * 1.2, sharp_oil, lanes_visible);
		track *= mix(0.55, 1.0, macro.g);
		oil *= mix(0.45, 1.0, region.r) * mix(0.7, 1.0, macro.g);
		// Drips where cars stand, round the lane's middle.
		spots = macro.a * spot_strength * (1.0 - smoothstep(0.1, 0.3, abs(f)));
		// Queuing for a junction: drips and a dark band down the lane's middle, patchy.
		float queue = UV.y < (COLOR.a - 0.5) * 32.0 ? COLOR.g : COLOR.b;
		float band = 1.0 - smoothstep(0.08, mix(0.3, 0.2, lanes_visible), abs(f));
		oil = max(oil, queue * band * mix(0.5, 1.0, macro.a));
		spots = max(spots, queue * queue_strength * macro.a * band);
	} else if (UV2.x < -1.5) {
		track = junction_wear * mix(0.4, 1.0, macro.b);
	}
	track *= wear;
	oil *= wear;
	spots *= wear;

	// Far: the average of binder and stone. Near: the real stones.
	float exposure = clamp(stone_amount + track * 0.35, 0.0, 1.0);
	vec3 albedo = mix(binder, stone_colour, 0.3 * exposure);
	float rough = roughness;
	vec3 nmap = vec3(0.5, 0.5, 1.0);
	float near = near_fade(dist, detail_far);
	if (near > 0.0) {
		vec4 d = texture(detail_tex, p * detail_scale);
		float stone = smoothstep(0.45, 0.65, d.r) * step(0.3, d.g + exposure * 0.5);
		vec3 stones = mix(binder, stone_colour * (0.85 + 0.3 * d.g), stone * exposure);
		stones *= 0.92 + 0.16 * d.b;
		albedo = mix(albedo, stones, near);
		rough = mix(rough, rough - 0.12 * stone * track, near);
		vec3 n = texture(detail_normal_tex, p * detail_scale).xyz;
		nmap = mix(nmap, n, near * normal_strength);
	}

	albedo = mix(albedo, track_colour, track * track_strength);
	rough = mix(rough, track_roughness, track);
	albedo = mix(albedo, oil_colour, max(oil * oil_strength, spots));
	rough = mix(rough, 0.55, max(oil, spots) * 0.6);

	// Cracks: where the crack mask and the wear say so; a dark halo of dirt round each.
	float crack_near = near_fade(dist, crack_far);
	if (crack_near > 0.0 && base_age + road_age > 0.0) {
		// Where cracks start is fixed by the textures; age spreads them out from there (wheel tracks first).
		float grow = (base_age + road_age * (1.0 - base_age)) * crack_amount;
		// The crack-prone noise (about 0.5 on average) must beat a bar that falls from 1 to 0.15 as the road ages.
		float prone = macro.g * 0.55 + region.g * 0.45 + track * grow * 0.15;
		float area = smoothstep(0.0, 0.12, prone - (1.0 - grow * 0.85));
		if (area > 0.0) {
			// Some stretches open, some hairline, some not cracked through yet.
			float open = smoothstep(0.25, 0.65, texture(macro_tex, p * macro_scale * 5.0).g);
			float line = aa_line(to_crack, crack_fw, crack_width * (0.3 + 0.9 * area) * open) * step(0.05, open);
			float halo = 1.0 - smoothstep(0.0, 0.04, to_crack);
			albedo *= 1.0 - halo * 0.15 * area * crack_near;
			albedo = mix(albedo, crack_colour, line * area * crack_near);
			rough = mix(rough, 1.0, line * area);
		}
	}

	ALBEDO = albedo;
	ROUGHNESS = clamp(rough, 0.0, 1.0);
	SPECULAR = 0.35;
	NORMAL_MAP = nmap;
}
