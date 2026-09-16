using Godot;
using System;
using Godot.Collections;
using FluxxiShaderLang;
using GodotWaterRendering.assets.Scripts.Utility;

public partial class FastWaterController : Node {
	
	enum SpectrumFunction {
		Tessendorf,
		AttenuatedTessendorf,
		PiersonMoskowitz,
		Jonswap,
		Tma
	}

	enum DirectionalSpreadingFunction {
		None,
		Tessendorf,
		Mitsuyasu,
		Hasselmann,
		DonelanBanner
	}

	private bool _simulate = true;

	[ExportGroup("Spectrum Controls")] [Export]
	private SpectrumFunction spectrum = SpectrumFunction.Tma;

	[Export] private float _depth = 100f;
	[Export] private float _windSpeed = 19f;
	[Export] private float _windDirection = 0f;
	[Export] private float _fetch = 100000f;

	[ExportSubgroup("Tessendorf Controls")] 
	[Export] private float _philipsAmplitude = 0.02f;

	[Export] private float tessendorfAttenuation = 0.02f;

	[ExportSubgroup("Spreading Controls")] 
	[Export] private DirectionalSpreadingFunction spread = DirectionalSpreadingFunction.DonelanBanner;

	[Export(PropertyHint.Range, "0, 1")] private float _spreadingStrength = 0.9f;

	[Export(PropertyHint.Range, "0, 1, or_greater")]
	private float _swell = 1f;

	[ExportGroup("")] [Export] private Window controlWindow;

	[Export] private Button controlWindowToggle;

	private FSLFile spectrumShader = FSLFile.FromFile("res://assets/Shaders/Compute/FSL/ocean/spectrums.fsl");
	private FSLFile spreadingsShader = FSLFile.FromFile("res://assets/Shaders/Compute/FSL/ocean/spreadings.fsl");
	private FSLFile bufferUpdateShader = FSLFile.FromFile("res://assets/Shaders/Compute/FSL/ocean/temp.fsl");
	private ComputeGroup spectrums;
	private ComputeKernel spreadings;
	private ComputeGroup bufferUpdaters;
	private OptimFftHandler fftHandler;
	private float _currentSeed;
	private float _time;
	public const uint MAX_CASCADES = 4;
	[Export] public uint NumCascades = 2;

	private Array<float> _tileLengths = new() { 1000f, 370f, 80f, 30f};
	

	[Export]
	public Array<float> TileLengths {
		get => _tileLengths;
		set {
			_tileLengths = value;
			// oceanParams?.SetField("tileLengths", value);
			if (fftHandler != null) {
				GenerateSpectrum();
				GenerateWaves(0.0f);
			}
		}
	}

	[Signal]
	public delegate void TextureRidUpdatedEventHandler(StringName texture_name, Texture2DArrayRD new_tex_rd);

	[Signal]
	public delegate void TileLengthsChangedEventHandler(Array<float> tile_lengths);
	
	[Signal]
	public delegate void CascadeCountChangedEventHandler(uint num_cascades);


	[Export(PropertyHint.Range, "8, 10")]
	private uint TexPower {
		get => _texPow;
		set {
			_texPow = value;
			_texSize = (uint)Math.Pow(2, value);
			fftHandler?.Resize(_texSize);
		}
	}

	private const int StartingPow = 8;
	private uint _texPow = StartingPow;
	private uint _texSize = (uint)Math.Pow(2, StartingPow);
	private const float G = 9.81f;

	private FSLTexture2DArray gaussianNoise;
	private FSLStorageBuffer oceanParams;

	private Dictionary<StringName, Texture2DArrayRD> texRdCache = new();
	private Array<Image> gaussianCache = [];
	private Array<Vector2> cascadeFoamParams = [
		new(0.6f, 3.0f),
		new(0.65f, 2.5f),
		new(0.7f, 1.5f),
		new(0.5f, 0.5f)
	];

	public Array<Vector2> CascadeFoamParams => cascadeFoamParams;

	private Action<Rid> MakeTextureCallback(StringName texture_name) {
		return tex_rid => {
			Texture2DArrayRD texRd = new Texture2DArrayRD();
			texRd.TextureRdRid = tex_rid;
			EmitSignal(SignalName.TextureRidUpdated, texture_name, texRd);
			texRdCache[texture_name] = texRd;
		};
	}

	public void PostTextures() {
		foreach (var (texName, texRd) in texRdCache) {
			EmitSignalTextureRidUpdated(texName, texRd);
		}
	}

	public void SetWhitecap(int cascade_index, float whitecap) {
		cascadeFoamParams[cascade_index] = new Vector2(whitecap, cascadeFoamParams[cascade_index].Y);
		UpdateCascadeParam(cascade_index);
	}
	
	public void SetFoamAmount(int cascade_index, float foam_amount) {
		cascadeFoamParams[cascade_index] = new Vector2(cascadeFoamParams[cascade_index].X, foam_amount);
		UpdateCascadeParam(cascade_index);
	}

	public override void _Ready() {
		InitShaders();
		GenerateSpectrum();
		GenerateWaves(0.0f);
	}

	public override void _Process(double delta) {
		if (_simulate) {
			_time += (float)delta;
			GenerateWaves((float)delta);
		}
	}

	private void RegenerateWaves() {
		GenerateGaussian();
		GenerateSpectrum();
		GenerateWaves(0.0f);
	}

	private void UpdateParamBuffer() {
		bufferUpdaters.Dispatch("updateParams", 1, 1, 1, new Dictionary<StringName, Variant> {
			{"numCascades", NumCascades},
			{"windSpeed", _windSpeed},
			{"windDirection", float.DegreesToRadians(_windDirection)},
			{"depth", _depth},
			{"fetch", _fetch}
		});
	}

	private void UpdateCascadeParam(int index) {
		bufferUpdaters.Dispatch("updateCascadeParam", 1, 1, 1, new Dictionary<StringName, Variant> {
			{"cascade_index", (uint) index},
			{"param_x", _tileLengths[index]},
			{"param_y", cascadeFoamParams[index].X},
			{"param_z", cascadeFoamParams[index].Y}
		});
		EmitSignalTileLengthsChanged(_tileLengths);
	}

	private void UpdateCascadeCount() {
		uint safeCascades = uint.Max(NumCascades, 2);
		spectrums.GetTexture2DArray("spectrumMap").SetTextures(_texSize, _texSize, safeCascades);
		spreadings.GetTexture2DArray("baseSpectrum").SetTextures(_texSize, _texSize, safeCascades);
		gaussianNoise.SetTextures(_texSize, _texSize, safeCascades, gaussianCache[..(int)safeCascades]);
		fftHandler.UpdateCascadeCount(NumCascades);
		UpdateParamBuffer();
		GenerateSpectrum();
		GenerateWaves(0f);
		EmitSignalCascadeCountChanged(NumCascades);
	}

	private void InitShaders() {
		spectrums = spectrumShader.GetKernelGroup();
		spreadings = spreadingsShader.GetKernel("applySpreading");
		bufferUpdaters = bufferUpdateShader.GetKernelGroup();
		
		spectrums.SetSpecializationConstant("halfN", _texSize / 2);
		spreadings.SetSpecializationConstant("halfN", _texSize / 2);
		
		FSLTexture2DArray spectrumMap = spectrums.GetTexture2DArray("spectrumMap");
		spectrumMap.SetTextures(_texSize, _texSize, uint.Max(NumCascades, 2));
		FSLTexture2DArray baseSpectrum = spreadings.GetTexture2DArray("baseSpectrum");
		baseSpectrum.SetTextures(_texSize, _texSize, uint.Max(NumCascades, 2));
		gaussianNoise = spreadings.GetTexture2DArray("spectrumCoefficients");
		oceanParams = bufferUpdaters.GetStorageBuffer("oceanParams");
		oceanParams.SetUnsizedElementCount(MAX_CASCADES);
		cascadeFoamParams.Resize((int) MAX_CASCADES);
		FSLStorageBuffer spectrumData = spreadings.GetStorageBuffer("spectrumDataBuffer");
		spectrums.AssignResource(spectrumData, "spectrumDataBuffer");
		UpdateParamBuffer();
		for (var tlIndex = 0; tlIndex < _tileLengths.Count; tlIndex++) {
			UpdateCascadeParam(tlIndex);
		}
		
		spectrums.AssignResource(oceanParams, "oceanParams");
		spreadings.AssignResource(spectrumMap, "spectrumMap");
		spreadings.AssignResource(oceanParams, "oceanParams");
		
		baseSpectrum.ConnectAndCall(Callable.From(MakeTextureCallback("baseSpectrum")));

		GenerateGaussian();
		OptimFftHandler.TextureCallbacks callbacks = new OptimFftHandler.TextureCallbacks{
			SpectrumCallback = MakeTextureCallback("spectrumTexture"),
			HeightCallback = MakeTextureCallback("heightMaps"),
			GradFoamCallback = MakeTextureCallback("gradFoamMaps")
		};
		fftHandler = new OptimFftHandler(_texSize, NumCascades, baseSpectrum, oceanParams, callbacks);

		if (controlWindow != null && controlWindowToggle != null) {
			InitControlWindow();
		}
	}
	
	private void GenerateWaves(float delta) {
		
		fftHandler.Run(delta, _time);
	}

	private void GenerateGaussian() {
		
		var rng = new RandomNumberGenerator();
		gaussianCache = [];
		for (int cascade = 0; cascade < (int)MAX_CASCADES; cascade++) {
			gaussianCache.Add(Image.CreateEmpty((int) _texSize, (int) _texSize, false, Image.Format.Rgbaf));
		}
		for (int u = 0; u < _texSize; u++) {
			for (int v = 0; v < _texSize; v++) {
				for (int cascade = 0; cascade < (int)MAX_CASCADES; cascade++) {
					Color newPixel = new Color();
					newPixel.R = rng.Randfn();
					newPixel.G = rng.Randfn();
					newPixel.B = rng.Randfn();
					newPixel.A = rng.Randfn();
					gaussianCache[cascade].SetPixel(u,v, newPixel);
				}
			}
		}
		

		gaussianNoise.SetTextures(_texSize, _texSize, NumCascades, gaussianCache[..(int)NumCascades]);
	}

	private void GenerateSpectrum() {
		Dictionary<StringName, Variant> pushConstants = new();
		StringName spectrumKernelName = "";
		switch (spectrum) {
			case SpectrumFunction.Jonswap:
				spectrumKernelName = "jonswapSpectrum";
				break;
			case SpectrumFunction.Tessendorf:
				spectrumKernelName = "tessendorfSpectrum";
				pushConstants.Add("A", _philipsAmplitude);
				break;
			case SpectrumFunction.AttenuatedTessendorf:
				spectrumKernelName = "attenuatedTessendorfSpectrum";
				pushConstants.Add("A", _philipsAmplitude);
				pushConstants.Add("l", tessendorfAttenuation);
				break;
			case SpectrumFunction.PiersonMoskowitz:
				pushConstants.Add("A", 8.1e-3f * G * G);
				pushConstants.Add("B", 0.6858f * float.Pow(G / _windSpeed, 4.0f));
				spectrumKernelName = "abSpectrum";
				break;
			case SpectrumFunction.Tma:
				spectrumKernelName = "tmaSpectrum";
				break;
		}
		spectrums.Dispatch(spectrumKernelName, _texSize, _texSize, NumCascades, pushConstants);
		
		spreadings.Dispatch(_texSize, _texSize, NumCascades, new Dictionary<StringName, Variant> {
			{"strength", _spreadingStrength},
			{"spread_id", (uint) spread},
			{"swell", _swell}
		});
	}


	public void UseMips(bool use_mips) {
		fftHandler.UseMips(use_mips);
		GenerateWaves(0f);
	}

	private HBoxContainer CreateFloatSelector(string text, float starting_value, Action<float> setter, float min_val = 0f, float max_val = 100f, float step = 1f, bool allow_greater = false) {
		var newContainer = new HBoxContainer();
		var colorLabel = new Label();
		colorLabel.Text = text;
		
		var valueSelector = new SpinBox();
		valueSelector.MinValue = min_val;
		valueSelector.MaxValue = max_val;
		valueSelector.AllowGreater = allow_greater;
		valueSelector.Step = step;
		valueSelector.Value = starting_value;
		valueSelector.ValueChanged += (new_val) => {
			setter((float)new_val);
			UpdateParamBuffer();
			GenerateSpectrum();
			GenerateWaves(0f);
		};
		
		newContainer.AddChild(colorLabel);
		newContainer.AddChild(valueSelector);
		return newContainer;
	}

	private SpinBox CreateTileLengthSpinBox(int index) {
		var tileLength = new SpinBox();
		tileLength.Step = 1f;
		tileLength.MaxValue = 2048f;
		tileLength.MinValue = 1.0f;
		tileLength.Value = _tileLengths[index];
		tileLength.ValueChanged += value => {
			_tileLengths[index] = (float)value;
			UpdateCascadeParam(index);
			GenerateSpectrum();
			GenerateWaves(0f);
		};
		tileLength.Name = $"tileLength{index}";
		return tileLength;
	}

	private void InitControlWindow() {
		var canvasLayer = new CanvasLayer();
		var spectrumControlsBox = new VBoxContainer();
		var parameterControls = new VBoxContainer();
		var simulationControls = new HBoxContainer();


		var tileLengthControls = new VBoxContainer();
		
		#region tile_length_control_code
		{
			var tileLengthParameters = new HBoxContainer();
			
			var tileLengthLabel = new Label();
			tileLengthLabel.Text = "Tile Lengths";
			
			var addCascadeButton = new Button();
			var removeCascadeButton = new Button();
			
			addCascadeButton.Text = "Add Cascade";
			removeCascadeButton.Text = "Remove Cascade";
			
			
			tileLengthParameters.AddChild(tileLengthLabel);
			tileLengthParameters.AddSpacer(false);
			tileLengthParameters.AddChild(addCascadeButton);
			tileLengthParameters.AddChild(removeCascadeButton);
			
			var tileLengthValues = new HBoxContainer();

			for (var i = 0; i < NumCascades; i++) {
				tileLengthValues.AddChild(CreateTileLengthSpinBox(i));
			}

			tileLengthControls.AddChild(tileLengthParameters);
			tileLengthControls.AddChild(tileLengthValues);
			
			addCascadeButton.Pressed += () => {
				if (NumCascades == 1) {
					removeCascadeButton.Disabled = false;
				}
				tileLengthValues.AddChild(CreateTileLengthSpinBox((int)NumCascades));
				NumCascades++;
				if (NumCascades == MAX_CASCADES) {
					addCascadeButton.Disabled = true;
				}
				UpdateCascadeCount();
			};
			removeCascadeButton.Pressed += () => {
				if (NumCascades == 4) {
					addCascadeButton.Disabled = false;
				}
				NumCascades--;
				tileLengthValues.GetNode<SpinBox>($"tileLength{NumCascades}").QueueFree();
				if (NumCascades == 1) {
					removeCascadeButton.Disabled = true;
				}
				UpdateCascadeCount();
			};

		}
		#endregion
		
		var spectrumSelector = new HBoxContainer();
		{
			var spectrumLabel = new Label();
			spectrumLabel.Text = "Spectrum Function:";

			var spectrumOption = new OptionButton();
			spectrumOption.AddItem("Tessendorf", 0);
			spectrumOption.AddItem("Attenuated Tessendorf", 1);
			spectrumOption.AddItem("Pierson-Moskowitz", 2);
			spectrumOption.AddItem("JONSWAP", 3);
			spectrumOption.AddItem("TMA", 4);
			spectrumOption.Selected = (int) spectrum;
			spectrumOption.ItemSelected += index => {
				spectrum = (SpectrumFunction)index;
				GenerateSpectrum();
				GenerateWaves(0f);
			};
			
			spectrumSelector.AddChild(spectrumLabel);
			spectrumSelector.AddChild(spectrumOption);
		}
		HBoxContainer windSpeedControls = CreateFloatSelector("Wind Speed:", _windSpeed, new_val => _windSpeed = new_val, 0.1f, 1000f, 0.1f);
		HBoxContainer windDirectionControls = CreateFloatSelector("Wind Direction:", _windDirection, new_val => _windDirection = new_val, -180f, 180f, 5f);
		HBoxContainer depthControls = CreateFloatSelector("Depth:", _depth, new_val => _depth = new_val, 5f, 100000f, 5f);
		HBoxContainer fetchControls = CreateFloatSelector("Fetch:", _fetch, new_val => _fetch = new_val, 100f, 10_000_000f, 100f);
		var spreadingSelector = new HBoxContainer();
		{
			var spreadingLabel = new Label();
			spreadingLabel.Text = "Directional Spreading Function:";

			var spreadingOption = new OptionButton();
			spreadingOption.AddItem("None", 0);
			spreadingOption.AddItem("Positive Cosine", 1);
			spreadingOption.AddItem("Mitsuyasu", 2);
			spreadingOption.AddItem("Hasselmann", 3);
			spreadingOption.AddItem("Donelan-Banner", 4);
			spreadingOption.Selected = (int) spread;
			spreadingOption.ItemSelected += index => {
				spread = (DirectionalSpreadingFunction)index;
				GenerateSpectrum();
				GenerateWaves(0f);
			};
			
			spreadingSelector.AddChild(spreadingLabel);
			spreadingSelector.AddChild(spreadingOption);
		}
		HBoxContainer spreadStrengthControls = CreateFloatSelector("Spreading Strength:", _spreadingStrength,new_val => _spreadingStrength = new_val, 0f, 1f, 0.05f);
		HBoxContainer swellControls = CreateFloatSelector("Swell:", _swell,new_val => _swell = new_val, 0f, 1f, 0.05f, true);
		
		parameterControls.AddChild(spectrumSelector);
		parameterControls.AddChild(windSpeedControls);
		parameterControls.AddChild(windDirectionControls);
		parameterControls.AddChild(depthControls);
		parameterControls.AddChild(fetchControls);
		parameterControls.AddChild(spreadingSelector);
		parameterControls.AddChild(spreadStrengthControls);
		parameterControls.AddChild(swellControls);
		{
			var regenWaves = new Button();
			regenWaves.Text = "Regenerate Waves";
			regenWaves.Pressed += RegenerateWaves;
			
			var toggleSim = new Button();
			toggleSim.Text = "Simulate";
			toggleSim.ToggleMode = true;
			toggleSim.ButtonPressed = true;
			toggleSim.Toggled += pressed => _simulate = pressed;
			
			simulationControls.AddChild(regenWaves);
			simulationControls.AddChild(toggleSim);
		}
		

		spectrumControlsBox.AddChild(parameterControls);
		spectrumControlsBox.AddChild(tileLengthControls);
		spectrumControlsBox.AddChild(simulationControls);
		
		canvasLayer.AddChild(spectrumControlsBox);
		
		controlWindow.AddChild(canvasLayer);
		
		
		
		controlWindowToggle.Toggled += on => controlWindow.SetVisible(on);
		controlWindow.CloseRequested += () => controlWindowToggle.SetPressed(false);
		controlWindow.SetVisible(false);
		controlWindowToggle.SetPressed(false);
		
	}
}
