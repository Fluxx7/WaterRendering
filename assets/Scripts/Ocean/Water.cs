using System;
using FluxxiShaderLang;
using Godot;
using Godot.Collections;
using Godot.NativeInterop;
using GodotWaterRendering.assets.Scripts.Utility;
using Wave = Godot.Collections.Dictionary<Godot.StringName, float>;
[Tool]
public partial class Water : MeshInstance3D {
	[Export] public float Time = 0.0f;

	


	private ShaderMaterial _material;
	[Export] public ShaderMaterial SumOfSinesTextureMat = null;
	[ExportGroup("")]
	[Export] public Vector2 Size;
	[Export] public int Subdivide;
	private PlaneMesh _plane;
	
	[Export] public bool Render = true;
	
	

	private ImageTexture _texture;
	[Export] public uint WaveCount = 42;
	
	[ExportGroup("Wave Parameters")]
	[Export]
	public float BaseAmplitude = 1.0f;

	[Export] public float BaseFrequency = 0.1f;
	[Export] public float Gain = 1.18f;
	[Export] public float Lacunarity = 0.82f;
	
	private float _prevAmp = 0.0f;
	private float _prevFreq = 0.0f;
	private float _prevGain = 0.0f;
	private float _prevLac = 0.0f;
	private float _chop = 1.0f;
	private float _prevSeed;

	private Texture2Drd wave_tex;
	// Kept alive so the native FSLTexture callback's delegate isn't GC'd (see WaterController).
	private Callable _waveTexCallback;

	[Export]
	public float ChopModifier {
		set {
			_material?.SetShaderParameter("chopMod", value);
			_chop = value;
		}
		get => _chop;
	}

	[ExportGroup("")]
	[ExportToolButton("Regenerate Waves")]
	public Callable RegenWaves => Callable.From(RegenerateWaves);
	private bool _simulate = true;
	[ExportToolButton("Simulate")]
	private Callable Simulate => Callable.From(() => (_simulate = !_simulate));

	
	// compute shader stuff
	private FSLFile sumOfSinesShader = FSLFile.FromFile("res://assets/Shaders/Compute/FSL/ocean/sum_of_sines/sum_of_sines.fsl");
	private ComputeKernel textureGen;
	private ComputeKernel textureGenAdv;

	public void RegenerateWaves() {
		GenerateMesh();
		GenerateSineWaves();
	}

	private void wave_tex_callback_func(Rid tex_rid) {
		wave_tex.TextureRdRid = tex_rid;
		RenderingServer.GlobalShaderParameterSet("waveTexture", wave_tex);
	}


	#region Overrides

	public override void _Ready() {
		wave_tex = new Texture2Drd();
		textureGen = sumOfSinesShader.GetKernel("textureGen");
		textureGenAdv = sumOfSinesShader.GetKernel("textureGenAdv");
		FSLTexture2D waveTex = textureGen.GetTexture2D("waveTexture");
		waveTex.SetTexture(WaveCount, 2);
		textureGenAdv.AssignResource(waveTex, "waveTexture");
		
		_waveTexCallback = Callable.From<Rid>(wave_tex_callback_func);
		waveTex.ConnectAndCall(_waveTexCallback);
		
		_material = SumOfSinesTextureMat;
		GenerateSineWaves();
		if (Engine.IsEditorHint()) return;
		GenerateMesh();
	}

	public override void _Process(double delta) {
		if (Engine.IsEditorHint()) {
			if (_plane != null && !Render) {
				_plane = null;
				Mesh = null;
			} else if (_plane == null && Render) {
				GenerateMesh();
			}
			
			if (_material == null) return;

			if (!_simulate) {
				return;
			}
		}


		Time += (float)delta;
		_material.SetShaderParameter("time", Time);
	}

	#endregion

	private void GenerateSineWaves() {
		// if (_prevAmp != BaseAmplitude || _prevFreq != BaseFrequency || _prevGain != Gain || _prevLac != Lacunarity) {
		// 	var inputUniforms = new byte[32];
		//
		// 	float[] inputs = [BaseAmplitude, BaseFrequency, Lacunarity, Gain];
		// 	Buffer.BlockCopy(inputs, 0, inputUniforms, 0, sizeof(float) * 4);
		//
		// 	textureGenAdv.SetBuffer("paramBuffer", inputUniforms);
		// 	_prevAmp = BaseAmplitude;
		// 	_prevFreq = BaseFrequency;
		// 	_prevGain = Gain;
		// 	_prevLac = Lacunarity;
		// }
		GenSeed();
		_material.SetShaderParameter("wave_count", WaveCount);
		textureGenAdv.Dispatch(WaveCount, 1, 1, new Dictionary<StringName, Variant> {
			{"waveCount", WaveCount},
			{"baseSeed", _prevSeed},
			{"baseAmplitude", BaseAmplitude},
			{"baseFrequency", BaseFrequency},
			{"lacunarity", Lacunarity},
			{"gain", Gain}
		});

	}

	private void GenSeed() {
		var rng = new RandomNumberGenerator();
		float seedMod = rng.RandfRange(0f, 2f * Mathf.Pi);
		_prevSeed = seedMod;
	}

	#region Helpers

	private void GenerateMesh() {
		_material = SumOfSinesTextureMat;
		_plane = new() {
			Size = Size,
			SubdivideWidth = Subdivide,
			SubdivideDepth = Subdivide
		};
		Mesh = _plane;
		Mesh?.SurfaceSetMaterial(0, _material);
	}

	#endregion
}