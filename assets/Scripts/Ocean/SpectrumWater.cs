using System;
using FluxxiShaderLang;
using Godot;
using Godot.Collections;
using GodotWaterRendering.assets.Scripts.Utility;
using Array = Godot.Collections.Array;
using Range = Godot.Range;

namespace GodotWaterRendering.assets.Scripts.Water;

[GlobalClass]
public partial class SpectrumWater : DynamicMeshInstance3D {
	private ShaderMaterial _shader;
	private Shader _debugTexShader;
	private uint _prevSubdivide;
	private Vector2 _prevSize;
	[Export] private Window debugWindow;
	[Export] private Button debugWindowToggle;
	[Export] private OceanVisualControlsWindow visualsWindow;
	[Export] public float PlaneSidelength;
	private float _prevCameraFov;
	private bool _simulate = true;
	private SpinBox layerSelector = new();
	private SpinBox mipSelector = new();
	private FSLFile oceanVertexFile = FSLFile.FromFile("res://assets/Shaders/Compute/FSL/ocean/ocean_vertex.fsl");
	private ComputeKernel oceanVertex;
	private FastWaterController waterController;


	public struct OceanPhysicalParameters {
		public float Gravity;
		public float FluidSurfaceTension;
		public float FluidDensity;
		public float AirBubbleDensity;
	}

	private OceanPhysicalParameters _physicalParams = new OceanPhysicalParameters() {
		
	};
	
	[ExportGroup("Shader Parameters")] 

	private float _bubbleDensity = 3.4f;

	[Export]
	public float BubbleDensity {
		get => _bubbleDensity;
		set {
			_bubbleDensity = value;
			_shader?.SetShaderParameter("air_bubble_density", value);
		}
	}
	private float _currentSeed;
	private float _time;
	private Dictionary<StringName, ShaderMaterial> _debugRectShaderMats = new();

	public bool UpdateOceanMesh = true;

	private enum OceanShader {
		Standard,
		Debug,
		Wireframe
	}

	private OceanShader currShader = OceanShader.Standard;
	private Shader standardShader = ResourceLoader.Load<Shader>("res://assets/Shaders/ocean.gdshader");
	private Shader debugShader = ResourceLoader.Load<Shader>("res://assets/Shaders/ocean_debug.gdshader");
	private Shader wireframeShader = ResourceLoader.Load<Shader>("res://assets/Shaders/ocean_wireframe.gdshader");
	
	
	private void Simulate(bool simulate) {
		_simulate = simulate;
	}

	private void OnWaterControllerTextureUpdate(StringName texture_name, Texture2DArrayRD tex_rd) {
		_shader?.SetShaderParameter(texture_name, tex_rd);
		if (_debugRectShaderMats.TryGetValue(texture_name, out ShaderMaterial shaderMat)) {
			shaderMat.SetShaderParameter("debug_tex", tex_rd);
		} else if (texture_name == "gradFoamMaps") {
			if (_debugRectShaderMats.TryGetValue("gradientMaps", out ShaderMaterial gradShaderMat)) {
				gradShaderMat.SetShaderParameter("debug_tex", tex_rd);
			}

			if (_debugRectShaderMats.TryGetValue("foamMaps", out ShaderMaterial foamShaderMat)) {
				foamShaderMat.SetShaderParameter("debug_tex", tex_rd);
			}
		} else if (texture_name == "baseSpectrum") {
			if (_debugRectShaderMats.TryGetValue("positiveSpectrum", out ShaderMaterial posSpecShaderMat)) {
				posSpecShaderMat.SetShaderParameter("debug_tex", tex_rd);
			}
			if (_debugRectShaderMats.TryGetValue("negativeSpectrum", out ShaderMaterial negSpecShaderMat)) {
				negSpecShaderMat.SetShaderParameter("debug_tex", tex_rd);
			}
		}

	}

	private void UpdateMesh() {
		if (UpdateOceanMesh) UpdateCBTrees();
		else RedisplaceVerts();
	}

	private Color _deepColor = new (0x00162bFF);
	private Color _scatterColor = new Color(0x001821FF);
	private float _heightScatter = 7.31f;
	private float _nearScatter = 1.0f;
	private float _globalScatter = 0f;
	private float _deepColorStrength = 0.17f;
	
	private void SetShaderParameters() {
		_shader.SetShaderParameter("water_scatter_color", _scatterColor);
		_shader.SetShaderParameter("air_bubble_color", _deepColor);
		_shader.SetShaderParameter("height_scale", _heightScatter);
		_shader.SetShaderParameter("k2", _nearScatter);
		_shader.SetShaderParameter("k3", _globalScatter);
		_shader.SetShaderParameter("k4", _deepColorStrength);
		_shader.SetShaderParameter("air_bubble_density", BubbleDensity);
		_shader.SetShaderParameter("tileLengths", waterController.TileLengths);
		_shader.SetShaderParameter("cascade_count", waterController.NumCascades);
		_shader.SetShaderParameter("cascade_foam_weights", new Array<float>{1.0f, 1.0f, 1.0f, 1.0f});
		_shader.SetShaderParameter("foamDetailTexture", GD.Load<Texture2D>("res://assets/Textures/foam-texture-2k/foam-texture-2d_displacement.png"));
	}
	public override void _Ready() {
		if (Engine.IsEditorHint()) {
			_simulate = false;
		}

		Update = false;
		oceanVertex = oceanVertexFile.GetKernel("oceanVertex");
		oceanVertex.GetUniformBuffer("VertexParams").SetBuffer(new Dictionary<StringName, Variant> {
			{"cutoff_params", new Vector4(500f, 0.0005f, 0f, 0f)}
		});
		oceanVertex.GetUniformBuffer("PlaneInfoBuffer").SetBuffer(new Dictionary<StringName, Variant> {
			{"plane_sidelength", PlaneSidelength}
		});
		CustomAabb = Engine.IsEditorHint() 
			? new Aabb(new Vector3(-PlaneSidelength * 0.5f, -1f, -PlaneSidelength * 0.5f),
				new Vector3(PlaneSidelength, 1f, PlaneSidelength))
			: new Aabb(new Vector3(-PlaneSidelength * 0.5f, -100f, -PlaneSidelength * 0.5f),
				new Vector3(PlaneSidelength, 100f, PlaneSidelength));;
		EnableCustom0Buffer(oceanVertex.GetVertexBuffer("UVBuffer"));
		VertexKernel = oceanVertex;
		
		_debugTexShader = GD.Load<Shader>("res://assets/Shaders/debug_texture.gdshader");
		_shader = new ShaderMaterial();
		_shader.SetShader(standardShader);
		
		waterController = GetNode<FastWaterController>("WaterController");
		waterController.TextureRidUpdated += OnWaterControllerTextureUpdate;
		waterController.CascadeCountChanged += cascades => {
			layerSelector.MaxValue = cascades - 1;
			_shader?.SetShaderParameter("cascade_count", cascades);
		};
		waterController.TileLengthsChanged += lengths => _shader?.SetShaderParameter("tileLengths", lengths);
		waterController.BindVertexUpdateShader(oceanVertex);
		waterController.SpectrumTexturesReady += UpdateMesh;
		waterController.PostTextures();
		SurfaceMaterial = _shader;
		layerSelector.MaxValue = waterController.NumCascades - 1;
		
		if (debugWindow != null && debugWindowToggle != null) {
			InitDebugWindow();
		}
		if (visualsWindow != null) {
			LinkVisualControls();
		}
		SetShaderParameters();
	}

	public override void _Process(double delta) {


		if (_simulate) {
			_time += (float)delta;
		}
	}

	private static HBoxContainer CreateColorContainer(string text, 
													  Color default_value, 
													  ColorPickerButton.ColorChangedEventHandler colorChange) {
		var newContainer = new HBoxContainer();
		var colorLabel = new Label();
		colorLabel.Text = text + ":";
		
		var colorSelector = new ColorPickerButton();
		colorSelector.Color = default_value;
		colorSelector.Text = text;
		colorSelector.EditAlpha = false;
		colorSelector.ColorChanged += colorChange;
		
		var resetButton = new Button();
		resetButton.Pressed += () => {
			colorSelector.Color = default_value;
			colorChange.Invoke(default_value);
		};
		resetButton.Text = "Reset";
		
		newContainer.AddChild(colorLabel);
		newContainer.AddChild(colorSelector);
		newContainer.AddChild(resetButton);

		return newContainer;
	}

	private VBoxContainer CreateDebugTexColorRect(StringName label, StringName tex_name) {
		var mainContainer = new VBoxContainer();

		var newLabel = new Label();
		newLabel.Text = label;
		newLabel.HorizontalAlignment = HorizontalAlignment.Center;
		
		mainContainer.AddChild(newLabel);
		
		var newRect = new ColorRect();
		var shaderMat = new ShaderMaterial();
		shaderMat.SetShader(_debugTexShader);
		_debugRectShaderMats[tex_name] = shaderMat;
		newRect.Material = shaderMat;
		newRect.CustomMinimumSize = new Vector2(256f, 256f);

		mainContainer.AddChild(newRect);
		return mainContainer;
	}

	private void UpdateCurrentShader() {
		
	}
	
	private void InitDebugWindow() {
		var canvasLayer = new CanvasLayer();
		var mainBox = new VBoxContainer();
		var texBox = new VBoxContainer();
		var buttonBox = new HBoxContainer();

		var upperBox = new HBoxContainer();
		var lowerBox = new HBoxContainer();
		
		upperBox.AddChild(CreateDebugTexColorRect("Initial Positive Spectrum Map", "positiveSpectrum"));
		upperBox.AddChild(CreateDebugTexColorRect("Initial Negative Spectrum Map", "negativeSpectrum"));
		upperBox.AddChild(CreateDebugTexColorRect("Spectrum Map", "spectrumTexture"));
		lowerBox.AddChild(CreateDebugTexColorRect("Displacement Map", "heightMaps"));
		lowerBox.AddChild(CreateDebugTexColorRect("Gradient Map", "gradientMaps"));
		lowerBox.AddChild(CreateDebugTexColorRect("Foam Map", "foamMaps"));
		_debugRectShaderMats["gradientMaps"].SetShaderParameter("tex_type", 1);
		_debugRectShaderMats["foamMaps"].SetShaderParameter("tex_type", 2);
		_debugRectShaderMats["positiveSpectrum"].SetShaderParameter("tex_type", 3);
		_debugRectShaderMats["negativeSpectrum"].SetShaderParameter("tex_type", 4);
		lowerBox.Alignment = BoxContainer.AlignmentMode.Center;
		waterController?.PostTextures();
		
		texBox.AddChild(upperBox);
		texBox.AddChild(lowerBox);

		var newLabel = new Label();
		newLabel.Text = "Debug Render Mode:";

		var newBox = new HBoxContainer();
		newBox.AddChild(newLabel);

		var newButton = new OptionButton();
		newButton.AddItem("Off");
		newButton.AddItem("Wireframe");
		newButton.AddItem("Displacement");
		newButton.AddItem("Gradients");
		newButton.Selected = 0;
		newButton.ItemSelected += index => {
			switch (index) {
				case 0:
					currShader = OceanShader.Standard;
					_shader.SetShader(standardShader);
					SetShaderParameters();
					break;
				case 1:
					currShader = OceanShader.Wireframe;
					_shader.SetShader(wireframeShader);
					SetShaderParameters();
					break;
				default:
					currShader = OceanShader.Debug;
					_shader.SetShader(debugShader);
					SetShaderParameters();
					_shader?.SetShaderParameter("debugRender", index);
					break;
			}
		};
		
		newBox.AddChild(newButton);
		var vBox = new HBoxContainer();
		vBox.Alignment = BoxContainer.AlignmentMode.Center;
		vBox.AddChild(newBox);

		newBox = new HBoxContainer();
		newLabel = new Label();
		newLabel.Text = "Cascade:";
		
		newBox.AddChild(newLabel);
		
		layerSelector.Step = 1.0;
		layerSelector.MinValue = 0.0;
		layerSelector.ValueChanged += value => {
			_shader.SetShaderParameter("debugCascade", (int) value);
			foreach (var (_ ,shader_mat) in _debugRectShaderMats) {
				shader_mat.SetShaderParameter("debug_tex_index", (int) value);
				
			}
		}; 
		
		newBox.AddChild(layerSelector);
		vBox.AddChild(newBox);
		
		newBox = new HBoxContainer();
		newLabel = new Label();
		newLabel.Text = "Update Mesh";
		newBox.AddChild(newLabel);
		var checkBox = new CheckBox();
		checkBox.ButtonPressed = true;
		checkBox.Toggled += (bool value) => UpdateOceanMesh = value;
		newBox.AddChild(checkBox);
		
		vBox.AddChild(newBox);
		
		newBox = new HBoxContainer();
		var resetMeshButton = new Button();
		resetMeshButton.Text = "Reset Mesh";
		resetMeshButton.Pressed += () => rebuildQueued = true;
		newBox.AddChild(resetMeshButton);
		
		vBox.AddChild(newBox);
		
		newBox = new HBoxContainer();
		newLabel = new Label();
		newLabel.Text = "Mipmap Level:";
		
		newBox.AddChild(newLabel);
		
		
		mipSelector.Step = 1.0;
		mipSelector.MinValue = 0.0;
		mipSelector.ValueChanged += value => {
			foreach (var (_ ,shader_mat) in _debugRectShaderMats) {
				shader_mat.SetShaderParameter("mip_level", (int) value);
				
			}
		}; 
		
		newBox.AddChild(mipSelector);
		vBox.AddChild(newBox);
		buttonBox.AddChild(vBox);
		
		
		mainBox.AddChild(texBox);
		mainBox.AddChild(buttonBox);
		
		canvasLayer.AddChild(mainBox);
		debugWindow.AddChild(canvasLayer);
		debugWindowToggle.Toggled += on => debugWindow.SetVisible(on);
		debugWindow.CloseRequested += () => debugWindowToggle.SetPressed(false);
		debugWindow.SetVisible(false);
		debugWindowToggle.SetPressed(false);
	}

	private HBoxContainer createFloatSelector(string text, float starting_value, 
											  StringName uniform_name, 
											  float min_val = 0f, float max_val = 100f, float step = 1f, bool allow_greater = false) {
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
			_shader?.SetShaderParameter(uniform_name, new_val);
		};
		
		newContainer.AddChild(colorLabel);
		newContainer.AddChild(valueSelector);
		return newContainer;
	}
	
	private void LinkVisualControls() {
		visualsWindow.ScatterColorChanged += new_color => {
			_scatterColor = new_color;
			_shader.SetShaderParameter("water_scatter_color", new_color);
		};
		visualsWindow.DeepColorChanged += new_color => {
			_deepColor = new_color;
			_shader.SetShaderParameter("air_bubble_color", new_color);
		};
		visualsWindow.HeightScatterChanged += new_val => {
			_heightScatter = new_val;
			_shader.SetShaderParameter("height_scale", new_val);
		};
		visualsWindow.NearScatterChanged += new_val => {
			_nearScatter = new_val;
			_shader.SetShaderParameter("k2", new_val);
		};
		visualsWindow.GlobalScatterChanged += new_val => {
			_globalScatter = new_val;
			_shader.SetShaderParameter("k3", new_val);
		};
		visualsWindow.DeepColorStrengthChanged += new_val => {
			_deepColorStrength = new_val;
			_shader.SetShaderParameter("k4", new_val);
		};
	}
}