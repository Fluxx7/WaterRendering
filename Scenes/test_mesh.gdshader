shader_type spatial;
render_mode cull_disabled, wireframe;

// 0 = flat facet shading, 1 = per-vertex id field, 2 = per-triangle id
uniform int display_mode : hint_range(0, 2) = 0;
uniform float color_saturation : hint_range(0.0, 1.0) = 0.6;
uniform float color_value : hint_range(0.0, 1.0) = 0.9;

// Vertices are shared between bisectors now, so a vertex belongs to no single triangle.
// That rules out carrying barycentric coordinates as a vertex attribute, and with them the
// screen-space wireframe this shader used to draw. What survives is the id hash: interpolated
// it shows the vertex field, flat it picks out one colour per triangle.
varying vec3 vertex_color;
varying flat vec3 triangle_color;

// PCG-style integer hash: adjacent ids scatter to unrelated outputs.
uint hash_u32(uint x) {
	x ^= x >> 16u;
	x *= 0x7feb352du;
	x ^= x >> 15u;
	x *= 0x846ca68bu;
	x ^= x >> 16u;
	return x;
}

vec3 hsv_to_rgb(vec3 c) {
	vec3 p = abs(fract(c.xxx + vec3(1.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0);
	return c.z * mix(vec3(1.0), clamp(p - 1.0, 0.0, 1.0), c.y);
}

void vertex() {
	// Golden-ratio hue stride keeps even numerically close hashes far apart in hue.
	float h = fract(float(hash_u32(uint(VERTEX_ID)) & 0xFFFFFFu) / 16777216.0 + 0.61803399);
	vec3 c = hsv_to_rgb(vec3(h, color_saturation, color_value));
	// Same value down both paths; the interpolation qualifier is what differs.
	vertex_color = c;
	triangle_color = c;
}

void fragment() {
	// Facet normal from the derivatives of view-space position. With no normals in the vertex
	// format this is the only way to see the tessellation, and it stays per-triangle flat,
	// which is what makes neighbouring bisectors legible once the surface is displaced.
	vec3 n = normalize(cross(dFdx(VERTEX), dFdy(VERTEX)));
	// cull_disabled lets either face reach here, so aim the normal back at the camera,
	// which sits at the origin in view space.
	if (dot(n, -normalize(VERTEX)) < 0.0) {
		n = -n;
	}
	NORMAL = n;

	// Every mode writes EMISSION so a debug view reads identically with or without a light
	// or environment in the scene.
	ALBEDO = vec3(0.0);
	if (display_mode == 1) {
		EMISSION = vertex_color;
	} else if (display_mode == 2) {
		EMISSION = triangle_color;
	} else {
		// Fixed world-space key direction, so the shading stays put as the camera orbits.
		vec3 world_n = normalize((INV_VIEW_MATRIX * vec4(n, 0.0)).xyz);
		float lambert = clamp(dot(world_n, normalize(vec3(0.35, 0.8, 0.45))), 0.0, 1.0);
		EMISSION = mix(vec3(0.07, 0.09, 0.13), vec3(0.82, 0.86, 0.94), lambert);
	}

	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
}
