shader_type sky;

// Import the image with Import As: Cubemap. Keep source_color for PNG/JPG star maps, which are
// stored in sRGB; float formats (HDR/EXR) are already linear and ignore it.
uniform samplerCube sky_texture : source_color, filter_linear_mipmap, hint_default_black;

// Linear multiplier. Downloaded star maps tend to be mastered bright; pull this down until the
// sun reads as the brightest thing in the frame.
uniform float exposure : hint_range(0.0, 16.0) = 1.0;

// Euler rotation of the star field, in degrees, for lining up the galactic band. Applied to the
// lookup direction only, so it never touches scene lighting.
uniform vec3 rotation_degrees = vec3(0.0);

mat3 rotation_matrix(vec3 degrees) {
	vec3 r = radians(degrees);
	vec3 c = cos(r);
	vec3 s = sin(r);
	mat3 rx = mat3(vec3(1.0, 0.0, 0.0), vec3(0.0, c.x, s.x), vec3(0.0, -s.x, c.x));
	mat3 ry = mat3(vec3(c.y, 0.0, -s.y), vec3(0.0, 1.0, 0.0), vec3(s.y, 0.0, c.y));
	mat3 rz = mat3(vec3(c.z, s.z, 0.0), vec3(-s.z, c.z, 0.0), vec3(0.0, 0.0, 1.0));
	return ry * rx * rz;
}

void sky() {
	// EYEDIR is the world-space view direction for this pixel. It's direction-only, so the
	// camera's position - rebased or not - never shifts the stars.
	vec3 dir = rotation_matrix(rotation_degrees) * EYEDIR;
	COLOR = texture(sky_texture, dir).rgb * exposure;
}
