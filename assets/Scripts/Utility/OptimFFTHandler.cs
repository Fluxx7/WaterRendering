using System;
using System.Collections.Generic;
using FluxxiShaderLang;
using Godot;
using Godot.Collections;

namespace GodotWaterRendering.assets.Scripts.Utility;

public partial class OptimFftHandler : RefCounted {
	public struct TextureCallbacks {
		public Action<Rid> SpectrumCallback;
		public Action<Rid> HeightCallback;
		public Action<Rid> GradFoamCallback;
	}
	private FSLFile tessendorfShader = FSLFile.FromFile("res://assets/Shaders/Compute/FSL/ocean/tess_funcs_optim.fsl");
	private FSLFile fftShader = FSLFile.FromFile("res://assets/Shaders/Compute/FSL/ocean/fft_optim.fsl");
	private FSLFile mipShader = FSLFile.FromFile("res://assets/Shaders/Compute/FSL/ocean/mipmaps.fsl");
	public FSLTexture2DArray displacementTexture;
	private FSLTexture2DArray gradFoamTexture;
	private ComputeGroup tessendorfFuncs;
	private ComputeKernel twiddleGen;
	private List<ComputeKernel> mipmapHeightKernels = [], mipmapGradFoamKernels = [];
	private List<ComputeGroup> ffts = [];
	private uint texSize;
	
	private const uint NumFfts = 4;
	private uint numCascades;
	private uint numMipmaps;
	private bool useMips = true;
	[Export] private float foamAmount = 0.5f;
	[Export] private float whitecap = 0.5f;
	private bool twiddlesGenned = false;
	
	private OptimFftHandler() {
		texSize = 256;
	}
	
	public OptimFftHandler(uint n, uint num_cascades, FSLTexture2DArray init_spectrum_texture, FSLStorageBuffer ocean_params, TextureCallbacks callbacks) {
		texSize = n;
		numCascades = num_cascades;
		Init(init_spectrum_texture, ocean_params, callbacks);
	}

	public void UpdateCascadeCount(uint new_count) {
		numCascades = new_count;
		uint safeCascades = uint.Max(new_count, 2);
		tessendorfFuncs.GetTexture2DArray("spectrumTexture").SetTextures(texSize, texSize, safeCascades);
		foreach (var fft in ffts) {
			fft.GetStorageBuffer("fft_buffers").SetUnsizedElementCount(texSize * texSize * numCascades);
		}
		displacementTexture.SetTextures(texSize, texSize, safeCascades);
		gradFoamTexture.SetTextures(texSize, texSize, safeCascades);
	}

	private void Init(FSLTexture2DArray init_spectrum_texture, FSLStorageBuffer ocean_params, TextureCallbacks callbacks) {
		tessendorfFuncs = tessendorfShader.GetKernelGroup();
		
		tessendorfFuncs.AssignResource(init_spectrum_texture, "baseSpectrum");
		tessendorfFuncs.AssignResource(ocean_params, "oceanParams");
		FSLTexture2DArray spectrumTexture = tessendorfFuncs.GetTexture2DArray("spectrumTexture");
		spectrumTexture.SetTextures(texSize, texSize, uint.Max(numCascades, 2));
		spectrumTexture.ConnectAndCall(Callable.From(callbacks.SpectrumCallback));

		twiddleGen = fftShader.GetKernel("twiddleGen");
		FSLStorageBuffer twiddleBuffer = twiddleGen.GetStorageBuffer("twiddleFactors");
		twiddleBuffer.SetUnsizedElementCount(texSize / 2);
		
		tessendorfFuncs.SetSpecializationConstant("halfN", texSize / 2);
		twiddleGen.SetSpecializationConstant("halfN", texSize / 2);
		
		
		for (var i = 0; i < NumFfts; i++) {
			ComputeGroup fft = fftShader.GetKernelGroup();
			fft.SetSpecializationConstant("halfN", texSize / 2);
			FSLStorageBuffer fftBuffer = tessendorfFuncs.GetStorageBuffer($"fft{i + 1}_buffers");
			fftBuffer.SetUnsizedElementCount(texSize * texSize * numCascades);
			fft.AssignResource(fftBuffer, "fft_buffers");
			fft.AssignResource(twiddleBuffer, "twiddleFactors");
			ffts.Add(fft);
		}

		displacementTexture = tessendorfFuncs.GetTexture2DArray("heightTexture");
		gradFoamTexture = tessendorfFuncs.GetTexture2DArray("gradFoamTexture");
		
		displacementTexture.SetTextures(texSize, texSize, uint.Max(numCascades, 2));
		gradFoamTexture.SetTextures(texSize, texSize, uint.Max(numCascades, 2));
		
		displacementTexture.ConnectAndCall(Callable.From(callbacks.HeightCallback));
		gradFoamTexture.ConnectAndCall(Callable.From(callbacks.GradFoamCallback));
		
		numMipmaps = uint.Log2(texSize) + 1;
		displacementTexture.SetMipCount(numMipmaps);
		gradFoamTexture.SetMipCount(numMipmaps);
		FSLTextureView lastHeightMip = displacementTexture.GetMipView(0);
		FSLTextureView lastGradFoamMip = gradFoamTexture.GetMipView(0);
		tessendorfFuncs.AssignResource(lastHeightMip, "heightTexture");
		tessendorfFuncs.AssignResource(lastGradFoamMip, "gradFoamTexture");
		for (uint i = numMipmaps - 1; i > 0; i--) {
			ComputeKernel heightMips = mipShader.GetKernel("genMipmaps");
			ComputeKernel gradientMips = mipShader.GetKernel("genMipmaps");
			
			FSLTextureView heightMipDest = displacementTexture.GetMipView(numMipmaps - i);
			FSLTextureView gradFoamMipDest = gradFoamTexture.GetMipView(numMipmaps - i);
			
			heightMips.AssignResource(lastHeightMip, "mipSource");
			heightMips.AssignResource(heightMipDest, "mipDest");

			gradientMips.AssignResource(lastGradFoamMip, "mipSource");
			gradientMips.AssignResource(gradFoamMipDest, "mipDest");
			
			mipmapHeightKernels.Add(heightMips);
			mipmapGradFoamKernels.Add(gradientMips);
			
			lastHeightMip = heightMipDest;
			lastGradFoamMip = gradFoamMipDest;
		}
	}

	public void Resize(uint new_size) {
		texSize = new_size;
		foreach (var fft in ffts) {
			fft.GetStorageBuffer("fft_buffers").SetUnsizedElementCount(texSize * texSize * numCascades);
			fft.GetStorageBuffer("twiddleFactors").SetUnsizedElementCount(texSize / 2);
		}
	}

	public void UseMips(bool use_mips) {
		useMips = use_mips;
		if (use_mips) {
			displacementTexture.SetMipCount(numMipmaps);
			gradFoamTexture.SetMipCount(numMipmaps);
		} else {
			displacementTexture.SetMipCount(1);
			gradFoamTexture.SetMipCount(1);
		}
	}
	
	public void Run(float delta, float time) {
		if (!twiddlesGenned) {
			twiddleGen.Dispatch(texSize/2, 1, 1);
			twiddlesGenned = true;
		}
		var fftPlan = ComputePlan.MakeNew(RenderingServer.GetRenderingDevice());
		var timePCs = new Godot.Collections.Dictionary<StringName, Variant>{
			{"texSize", texSize}, 
			{"time", time}
		};
		fftPlan.AddKernel(tessendorfFuncs.GetKernel("updateSpectrum"), texSize, texSize, numCascades, timePCs);
		var unpackPCs = new Godot.Collections.Dictionary<StringName, Variant> {
			{"texSize", texSize},
			{"deltaTime", delta}
		};
		fftPlan.AddBarrier();
		
		foreach (ComputeGroup fft in ffts) {
			fftPlan.AddKernelWorkgroups(fft.GetKernel("ifftRow"), 1, texSize, numCascades);
		}
		
		fftPlan.AddBarrier();
		foreach (ComputeGroup fft in ffts) {
			fftPlan.AddKernelWorkgroups(fft.GetKernel("ifftColumn"), 1, texSize, numCascades);
		}
		fftPlan.AddBarrier()
			.AddKernel(tessendorfFuncs.GetKernel("ifftUnpack"), texSize, texSize, numCascades, unpackPCs);
		if (useMips) {
			for (uint i = numMipmaps; i > 1; i--) {
				fftPlan.AddBarrier();
				var destSize = (uint)Math.Pow(2.0, i - 2);
				var arrayIndex = (int)(numMipmaps - i);
				fftPlan.AddKernel(mipmapHeightKernels[arrayIndex], destSize, destSize, numCascades,
					new Godot.Collections.Dictionary<StringName, Variant> {
						{ "destSize", destSize }
					});
				fftPlan.AddKernel(mipmapGradFoamKernels[arrayIndex], destSize, destSize, numCascades,
					new Godot.Collections.Dictionary<StringName, Variant> {
						{ "destSize", destSize }
					});
			}
		}
		
		fftPlan.Dispatch();
	}

}