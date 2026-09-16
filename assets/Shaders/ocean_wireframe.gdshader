shader_type spatial;
render_mode world_vertex_coords, shadows_disabled, specular_occlusion_disabled, wireframe;
#include "brdf_ocean.gdshaderinc"
global uniform vec3 camera_coords;

#define MAX_CASCADES 4

uniform sampler2DArray heightMaps : filter_linear_mipmap_anisotropic;
uniform sampler2DArray gradFoamMaps : filter_linear_mipmap_anisotropic;
uniform float tileLengths[MAX_CASCADES];
uniform int cascade_count = 4;

uniform int debugRender;
uniform int debugCascade = 0;
global uniform uint renderFlags;
uniform bool renderDiffuse = true;
uniform bool renderSpecular = true;
uniform bool renderReflections = true;
uniform bool renderFoam = true;

uniform float base_roughness = 0.15;
uniform float cutoff_dist : hint_range(0.0, 8000.0, 10.0) = 500.0;
uniform float cutoff_strength : hint_range(0.0, 0.005, 0.0001) = 0.0007;

varying float dist_factor;
varying vec2 coords[MAX_CASCADES];
varying vec3 heightMapSamples[MAX_CASCADES];
varying float v_displacement;
varying vec3 world_pos;

void vertex() {
	vec3 vertex_displacement = vec3(0);
	for (int cascade = 0; cascade < cascade_count; cascade++) {
		coords[cascade] = VERTEX.xz / tileLengths[cascade];
		vec3 sample = texture(heightMaps, vec3(coords[cascade], float(cascade))).xyz;
		heightMapSamples[cascade] = sample;
		vertex_displacement += sample;
	}
	world_pos = VERTEX;
	dist_factor = clamp(exp(-(length(VERTEX.xz - CAMERA_POSITION_WORLD.xz) - cutoff_dist)*cutoff_strength), 0.05, 1.0);
	v_displacement = vertex_displacement.y;
	VERTEX += vertex_displacement * vec3(1.0, dist_factor, 1.0);
}

varying vec2 v_derivatives;
varying float wave_height;
varying float foam_factor;
uniform float[MAX_CASCADES] cascade_foam_weights;
void fragment() {
	float dist = length(VERTEX.xz);
	float foamSample = 0.0;
	vec2 gradientSample = vec2(0);
	wave_height = v_displacement;
	for (int cascade = 0; cascade < cascade_count; cascade++) {
		vec3 gradFoamSample = texture(gradFoamMaps, vec3(coords[cascade], float(cascade))).rgb;
		foamSample += gradFoamSample.y * cascade_foam_weights[cascade];
		gradientSample += gradFoamSample.xz;
	}
	 
	foam_factor = 0.0;
	if (renderFoam) {
		foam_factor = foam_mask(world_pos, clamp(foamSample, 0.0, 1.0));
	}
	v_derivatives = gradientSample;
	
	vec3 local_normal = normalize(vec3(-gradientSample.x, 1.0, -gradientSample.y));
	ROUGHNESS = mix(0.35, base_roughness, dist_factor) + foam_factor * 0.5;
	float specular = 0.0;
	if (renderReflections) {
		specular = 0.25;
	}
	
	switch (debugRender) {
		default:
			SPECULAR = specular;
			ALBEDO = vec3(1);
			IRRADIANCE = vec4(0.0, 0.0, 0.0, 1.0);
			NORMAL = mix((VIEW_MATRIX * vec4(0.0, 1.0, 0.0, 0.0)).xyz, (VIEW_MATRIX * vec4(local_normal,0.0)).xyz, dist_factor);
			break;
		case 1: // Displacement
			ALBEDO = heightMapSamples[debugCascade] * exp(-dist*0.00075) / 5.0;
			ROUGHNESS = 1.0;
			SPECULAR = 0.0;
			break;
		case 2: { // Gradients
			vec4 gradFoamDebugSample = texture(gradFoamMaps, vec3(coords[debugCascade], float(debugCascade)));
			ALBEDO = vec3(gradFoamDebugSample.r, 0.0, gradFoamDebugSample.b) * exp(-dist*0.00075);
			ROUGHNESS = 1.0;
			SPECULAR = 0.0;
			break;
		}
	}
}

void light() {
	if (debugRender == 0) {
		vec3 diffuse = vec3(0);
		vec3 specular = vec3(0);
		brdf_lighting(LIGHT, LIGHT_COLOR, VIEW, VIEW_MATRIX, NORMAL, v_derivatives, wave_height, specular, diffuse);
		if (!renderDiffuse) {
			diffuse = vec3(0);
		}
		if (!renderSpecular) {
			specular = vec3(0);
		}
		if (renderFoam) {
			diffuse = mix(diffuse, vec3(1), foam_factor);
		}
		SPECULAR_LIGHT += specular;
		DIFFUSE_LIGHT += diffuse;
	} else {
		SPECULAR_LIGHT = vec3(0);
		DIFFUSE_LIGHT = ALBEDO;
	}
	
}
