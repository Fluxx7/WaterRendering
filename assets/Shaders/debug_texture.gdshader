shader_type canvas_item;

uniform sampler2DArray debug_tex : filter_linear_mipmap_anisotropic;
uniform int debug_tex_index;
uniform int mip_level = 0;
uniform int tex_type = 0;

void fragment() {
	vec4 texSample = textureLod(debug_tex, vec3(UV, float(debug_tex_index)), float(mip_level));
	switch (tex_type) {
		case 1: { // gradient
			COLOR = vec4(texSample.x, 0.0, texSample.z, 1.0);
		} break;
		case 2: {// foam
			COLOR = vec4(texSample.yyy, 1.0);
		} break;
		case 3: {// base spectrum
			COLOR = vec4(texSample.xy, 0.0, 1.0);
		} break;
		case 4: {// base spectrum inverse
			COLOR = vec4(texSample.zw, 0.0, 1.0);
		} break;
		default:
			COLOR = texSample;
			break;
	}
	
}
