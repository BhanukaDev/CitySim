shader_type spatial;
render_mode blend_mix, cull_back, diffuse_burley, specular_schlick_ggx;

// Set by Terrain.cs after generation so colours adapt to the map's height range.
uniform float height_min = 0.0;
uniform float height_max = 250.0;

group_uniforms Colours;
uniform vec3 sand_color : source_color = vec3(0.78, 0.72, 0.52);
uniform vec3 grass_color : source_color = vec3(0.29, 0.45, 0.19);
uniform vec3 grass_dry_color : source_color = vec3(0.45, 0.48, 0.26);
uniform vec3 rock_color : source_color = vec3(0.43, 0.41, 0.38);
uniform vec3 snow_color : source_color = vec3(0.94, 0.95, 0.97);

group_uniforms Thresholds;
uniform float sand_level : hint_range(0.0, 1.0) = 0.05;
uniform float snow_level : hint_range(0.0, 1.0) = 0.85;
// 1 - normal.y; 0.3 is roughly 45 degrees.
uniform float rock_slope : hint_range(0.0, 1.0) = 0.3;

group_uniforms Grid;
uniform bool show_grid = true;
uniform float grid_size = 64.0;
uniform float grid_strength : hint_range(0.0, 1.0) = 0.2;

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

void fragment() {
	float h = clamp((world_pos.y - height_min) / max(height_max - height_min, 0.001), 0.0, 1.0);
	float slope = 1.0 - normalize(world_normal).y;

	vec3 col = mix(grass_color, grass_dry_color, smoothstep(0.3, 0.7, h));
	col = mix(sand_color, col, smoothstep(sand_level, sand_level + 0.03, h));
	col = mix(col, rock_color, smoothstep(rock_slope - 0.08, rock_slope + 0.08, slope));
	float snow = smoothstep(snow_level, snow_level + 0.04, h) * (1.0 - smoothstep(0.35, 0.55, slope));
	col = mix(col, snow_color, snow);

	if (show_grid) {
		vec2 g = world_pos.xz / grid_size;
		vec2 fw = max(fwidth(g), vec2(1e-4));
		vec2 dist = abs(fract(g - 0.5) - 0.5) / fw;
		float line = 1.0 - min(min(dist.x, dist.y), 1.0);
		// Fade out once lines get denser than a few pixels apart.
		line *= 1.0 - smoothstep(0.05, 0.25, max(fw.x, fw.y));
		col = mix(col, vec3(0.05), line * grid_strength);
	}

	vec3 glow = vec3(0.0);
	if (brush_visible) {
		float d = length(world_pos.xz - brush_pos.xz);
		float fw = max(fwidth(d), 1e-4);
		float ring = 1.0 - clamp(abs(d - brush_radius) / (fw * 1.5), 0.0, 1.0);
		// Faint fill that follows the brush falloff.
		float t = clamp(1.0 - d / brush_radius, 0.0, 1.0);
		float fill = t * t * (3.0 - 2.0 * t) * 0.18;
		col = mix(col, brush_color, max(ring, fill));
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
			float h = clamp(dot(p - a, ab) / max(dot(ab, ab), 1e-4), 0.0, 1.0);
			float dl = length(p - a - ab * h);
			float lw = max(fw * 1.5, 0.6);
			line = 1.0 - smoothstep(lw, lw + fw, dl);
		}
		float m = max(marker, line * 0.8);
		col = mix(col, vec3(1.0, 0.75, 0.2), m);
		glow += vec3(1.0, 0.75, 0.2) * m * 0.4;
	}

	ALBEDO = col;
	EMISSION = glow;
	ROUGHNESS = 0.95;
}
