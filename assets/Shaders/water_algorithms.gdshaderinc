#define REFLECTANCE 0.0201

float dotclamped(vec3 a, vec3 b){
	return max(dot(a, b),2e-5);
}

// Source: https://github.com/godotengine/godot/blob/7b56111c297f24304eb911fe75082d8cdc3d4141/drivers/gles3/shaders/scene.glsl#L995
float ggx_distribution(in vec3 normal, in vec3 halfway, in float alpha) {
	float cos_theta = dot(normal, halfway);
	float a_sq = alpha*alpha;
	float d = 1.0 + (a_sq - 1.0) * cos_theta * cos_theta;
	return a_sq / (PI * d*d);
}


float smith_masking_shadowing(in float cos_theta, in float alpha) {
	float a = cos_theta / (alpha * sqrt(1.0 - cos_theta*cos_theta)); // Approximate: 1.0 / (alpha * tan(acos(cos_theta)))
	float a_sq = a*a; 
	return a < 1.6 ? (1.0 - 1.259*a + 0.396*a_sq) / (3.535*a + 2.181*a_sq) : 0.0;
}


float schlick_fresnel(float cos_theta, float av) {
	float upper_term = pow(1.0-cos_theta,5.0 * exp(-2.69 * av));
	float lower_term = 1.0 + 22.7*pow(av, 1.5);
	return REFLECTANCE + (1.0 - REFLECTANCE) * upper_term / lower_term;
}

group_uniforms colors;
uniform vec4 water_scatter_color : source_color = vec4(0.0,1.052,0.212,1.0); // Subsurface scattering produces a 'greener' color.
uniform vec4 air_bubble_color : source_color = vec4(0.157,0.063,0.827,1.0); // Subsurface scattering produces a 'greener' color.
group_uniforms; 

group_uniforms subsurface_scattering_parameters;
uniform float height_scale : hint_range(0.0, 50.0, 0.01) = 2.5;
uniform float k2 : hint_range(0.0, 50.0, 0.01) = 2.25;
uniform float k3 : hint_range(0.0, 10.0, 0.01) = 0.35;
uniform float k4 : hint_range(0.0, 10.0, 0.01) = 2.15;
uniform float air_bubble_density : hint_range(0.0, 10.0, 0.01) = 3.4;
group_uniforms;
 
float dotpos(vec3 a, vec3 b) {
	return max(dot(a,b), 0.0); 
}

void brdf_lighting(in vec3 light, in vec3 light_color, in vec3 view, in mat4 view_matrix, in vec3 mesonormal, in vec2 slopes, in float wave_height, inout vec3 specular, inout vec3 diffuse){
	vec3 halfway = normalize(light + view);
	vec3 macronormal = normalize(view_matrix * vec4(0.0, 1.0, 0.0, 0.0)).xyz;
	float height = max(0.0, wave_height);
	
	float NdotLun = dot(macronormal, light);
	float NdotL = max(NdotLun, 2e-5);
	float NdotV = dotclamped(macronormal, view);
	
	float light_mask = smith_masking_shadowing(NdotL, REFLECTANCE);
	float view_mask = smith_masking_shadowing(NdotV, REFLECTANCE);
	
	float fresnel = schlick_fresnel(dot(halfway, light), REFLECTANCE);
	float distribution = ggx_distribution(mesonormal, halfway, REFLECTANCE);
	
	 
	vec3 upper_specular_term = light_color * fresnel * distribution;
	float lower_specular_term = 4.0 * NdotV * (1.0 + light_mask + view_mask);
	specular += upper_specular_term / lower_specular_term;
	
	float height_dot = dotpos(light, -view);
	float height_dot_pow4 = height_dot * height_dot * height_dot * height_dot;
	
	float height_term2 = fma(-0.5, NdotLun, 0.5);
	float height_term2_pow3 = height_term2 * height_term2 * height_term2;
	
	float light_reflection_scatter = NdotV * NdotV  * k2;
	
	vec3 diffuse_out = fma(height_scale * height, height_dot_pow4 * height_term2_pow3, light_reflection_scatter) * water_scatter_color.rgb / (1.0 + light_mask);
	diffuse_out += (k3 * max(NdotLun, 0.0) * water_scatter_color.rgb) + k4 * air_bubble_color.rgb * air_bubble_density;
	diffuse_out *= (1.0 - fresnel) * light_color;
	diffuse += diffuse_out;
	
}
