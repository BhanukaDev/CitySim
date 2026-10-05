// Shared by the road shaders. Textures come from RoadTextureBaker (content/roads/textures/), sampled in world metres
// so the noise is seamless across segments and junctions. Per-vertex data (RoadMesh):
//   UV   = metres along the road, metres across it from the centre line (paint: half width, across from its middle)
//   UV2  = lane coordinate (lanes from the left edge of the first lane; -1 off the lanes, -2 on a junction), wear 0..1
//          (paint: metres from the strip's start, metres to its end)
// Performance: a handful of texture reads near the camera; past `detail_far` only the large-scale ones. Small repeating
// features (wheel tracks, slab joints) fade to their average once they get under a pixel, so they don't shimmer.

uniform sampler2D detail_tex : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D detail_normal_tex : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D macro_tex : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;

group_uniforms Distance;
/// Stones, grain and the normal map fade out by this distance (m).
uniform float detail_far : hint_range(10.0, 1000.0) = 150.0;
/// Cracks fade out by this distance (m).
uniform float crack_far : hint_range(10.0, 2000.0) = 350.0;
group_uniforms;

group_uniforms Depth_bias;
/// Beyond this distance (m) the road is drawn in front of where it is by this share of the distance, so the coarser
/// far terrain LODs don't swallow it on uneven ground. Same pixel, nearer depth; the fragment sees the true position.
uniform float far_bias_start : hint_range(0.0, 1000.0) = 40.0;
uniform float far_bias : hint_range(0.0, 0.05) = 0.01;
group_uniforms;

varying vec3 world_pos;
varying vec3 world_normal;

// Varyings can only be set in vertex() itself, so this is a macro for it. It also pulls the road toward the camera
// along the view ray (see far_bias); not in the shadow pass, whose view is the light's.
#define ROAD_VERTEX world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz); \
	if (!IN_SHADOW_PASS) { vec4 view = MODELVIEW_MATRIX * vec4(VERTEX, 1.0); float d = length(view.xyz); \
		view.xyz *= 1.0 - min(far_bias * max(d - far_bias_start, 0.0), d * 0.5) / max(d, 1e-3); POSITION = PROJECTION_MATRIX * view; } \
	else { POSITION = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)); }

float hash12(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

/// 1 near the camera, 0 past `far` (fades over the last half).
float near_fade(float dist, float far) {
	return 1.0 - smoothstep(far * 0.5, far, dist);
}

// The helpers take fwidth() values worked out by the caller outside any branch (derivatives in branches are undefined).

/// 1 while a feature `feature` units wide is a few pixels across (`fw` = fwidth of its coordinate), 0 once it's sub-pixel.
float pixel_fade(float fw, float feature) {
	return 1.0 - smoothstep(feature * 0.25, feature, fw);
}

/// An anti-aliased line: 1 where `d` (distance to it, `fw` = fwidth(d)) is under `width`.
float aa_line(float d, float fw, float width) {
	float aa = max(fw, 1e-5);
	return 1.0 - smoothstep(width - aa, width + aa, d);
}

/// Plan coordinates for the noise: world X/Z on tops; along the road and up on kerb faces and skirts.
vec2 surface_coord(vec2 uv, float lane) {
	if (abs(world_normal.y) > 0.5) return world_pos.xz;
	float along = lane < -1.5 ? world_pos.x + world_pos.z : uv.x;
	return vec2(along, world_pos.y);
}
