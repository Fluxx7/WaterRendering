#include "water_algorithms.gdshaderinc"

uniform sampler2D foamDetailTexture : hint_default_black, filter_linear_mipmap, repeat_enable;

struct OceanLightInfo {
	float specular_strength;
	float diffuse_strength;
	float sss_strength;
};

struct VertexOut {
	vec2 uv;
	vec3 vert_pos;
	float v_displacement;
	vec2[4] coords;
	float dist_factor;
};

VertexOut ocean_vertex(int cascade_count, vec4 custom0, in float[4] tileLengths, in sampler2DArray heightMaps) {
	VertexOut output;
	for (int cascade = 0; cascade < cascade_count; cascade++) {
		output.coords[cascade] = custom0.xy / tileLengths[cascade];
	}
	output.uv = custom0.xy;
	output.vert_pos = vec3(custom0.x, custom0.z, custom0.y);
	output.dist_factor = custom0.w;
	output.v_displacement = custom0.z;
	return output;
}

OceanLightInfo ocean_lighting_intensity(in vec3 light, in vec3 view, in mat4 view_matrix, in vec3 mesonormal, in float wave_height){
	OceanLightInfo output;
	
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
	
	 
	float upper_specular_term = fresnel * distribution;
	float lower_specular_term = 4.0 * NdotV * (1.0 + light_mask + view_mask);
	output.specular_strength = upper_specular_term / lower_specular_term;
	
	float height_dot = dotpos(light, -view);
	float height_dot_pow4 = height_dot * height_dot * height_dot * height_dot;
	
	float height_term2 = fma(-0.5, NdotLun, 0.5);
	float height_term2_pow3 = height_term2 * height_term2 * height_term2;
	
	float light_reflection_scatter = NdotV * NdotV  * k2;
	float fresnel_modifier = 1.0 - fresnel;
	
	output.sss_strength = (fma(height_scale * height, height_dot_pow4 * height_term2_pow3, light_reflection_scatter) / (1.0 + light_mask) + k3 * max(NdotLun, 0.0)) * fresnel_modifier;
	output.diffuse_strength = k4 * air_bubble_density * fresnel_modifier;
	return output;
}


float foam_mask(in vec3 world_pos, in float foam_sample) {
	// multiple sampling scales used to reduce visible tiling
	const float sample_scales[3] = {0.01, 0.03, 0.08};
	const float sample_weights[3] = {0.4, 0.35, 0.25};
	
	
	float detail = 0.0;
	for (int i = 0; i < 3; i++) {
		detail += texture(foamDetailTexture, world_pos.xz * sample_scales[i]).r * sample_weights[i];
	}
	
	float threshold = mix(0.85, 0.05, foam_sample);
	float w = fwidth(detail) * 1.5;
	float foam_amount = smoothstep(threshold - w, threshold + w, detail);
	return foam_amount;
}

struct LightingResults {
	vec3 specular;
	vec3 diffuse;
};

LightingResults ocean_lighting_brdf() {
	LightingResults result;
	
	return result;
}
