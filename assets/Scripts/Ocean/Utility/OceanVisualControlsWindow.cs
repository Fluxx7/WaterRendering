using System;
using Godot;
using Godot.Collections;
using Range = Godot.Range;

namespace GodotWaterRendering.assets.Scripts.Utility;

public partial class OceanVisualControlsWindow : Button {
	[Export] private Window window;
	public struct OceanVisualParameters {
		public Color DeepColor;
		public Color ScatterColor;
		public float HeightScatter;
		public float NearScatter;
		public float GlobalScatter;
		public float DeepColorStrength;
	}
	
	
	public struct OceanFoamParameters {
		public Array<float> Whitecaps;
		public Array<float> FoamAmounts;
		public Array<float> FoamWeights;
	}
	
	[Signal] public delegate void DeepColorChangedEventHandler(Color new_color);
	[Signal] public delegate void ScatterColorChangedEventHandler(Color new_color);
	[Signal] public delegate void HeightScatterChangedEventHandler(float new_val);
	[Signal] public delegate void NearScatterChangedEventHandler(float new_val);
	[Signal] public delegate void GlobalScatterChangedEventHandler(float new_val);
	[Signal] public delegate void DeepColorStrengthChangedEventHandler(float new_val);

	private OceanVisualParameters waterParams = new() {
		DeepColor = new Color(0x00162bFF),
		ScatterColor = new Color(0x001821FF),
		HeightScatter = 7.31f,
		NearScatter = 1.0f,
		GlobalScatter = 0f,
		DeepColorStrength = 0.17f
	};
	
	private OceanFoamParameters foamParams = new() {
		
	};
	
#region parameters
	[Export]
	public Color ScatterColor {
		get => waterParams.ScatterColor;
		set {
			waterParams.ScatterColor = value;
			EmitSignalScatterColorChanged(value);
		}
	}

	[Export]
	public Color DeepWaterColor {
		get => waterParams.DeepColor;
		set {
			waterParams.DeepColor = value;
			EmitSignalDeepColorChanged(value);
		}
	}

	[Export]
	public float HeightScatterStrength {
		get => waterParams.HeightScatter;
		set {
			waterParams.HeightScatter = value;
			EmitSignalHeightScatterChanged(value);
		}
	}

	[Export]
	public float NearScatterStrength {
		get => waterParams.NearScatter;
		set {
			waterParams.NearScatter = value;
			EmitSignalNearScatterChanged(value);
		}
	}

	[Export]
	public float GlobalScatterStrength {
		get => waterParams.GlobalScatter;
		set {
			waterParams.GlobalScatter = value;
			EmitSignalGlobalScatterChanged(value);
		}
	}

	[Export]
	public float DeepWaterColorStrength {
		get => waterParams.DeepColorStrength;
		set {
			waterParams.DeepColorStrength = value;
			EmitSignalDeepColorStrengthChanged(value);
		}
	}
#endregion
	
	public override void _Ready() {
		if (window != null) InitVisualsWindow();
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
	
	private HBoxContainer createFloatSelector(string text, float starting_value,
											  Action<float> signal, 
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
		valueSelector.ValueChanged += (new_val) => signal((float) new_val);
		
		newContainer.AddChild(colorLabel);
		newContainer.AddChild(valueSelector);
		return newContainer;
	}
	
	private HBoxContainer createFloatSelectorAction(string text, float starting_value, 
													Range.ValueChangedEventHandler action, 
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
		valueSelector.ValueChanged += action;
		
		newContainer.AddChild(colorLabel);
		newContainer.AddChild(valueSelector);
		return newContainer;
	}
	
	private void InitVisualsWindow() {
		var canvasLayer = new CanvasLayer();
		var visualControlsBox = new VBoxContainer();
		var colorParametersBox = new VBoxContainer();
		var lightingControlsBox = new HBoxContainer();
		
		HBoxContainer waterColorControls = CreateColorContainer("Deep Water Color", waterParams.DeepColor, EmitSignalDeepColorChanged);
		HBoxContainer scatterColorControls = CreateColorContainer("Scatter Color", waterParams.ScatterColor, EmitSignalScatterColorChanged);
		colorParametersBox.AddChild(waterColorControls);
		colorParametersBox.AddChild(scatterColorControls);

		var subParametersBox = new HBoxContainer();
		
		var shaderParamatersBox = new VBoxContainer();
		{
			var shaderParamLabel = new Label();
			shaderParamLabel.Text = "Water Shader Parameters";
			shaderParamatersBox.AddChild(shaderParamLabel);
		}
		shaderParamatersBox.AddChild(createFloatSelector("Height Scatter Strength", waterParams.HeightScatter, 
			EmitSignalHeightScatterChanged, 
			0f, 50f, 0.01f, true));
		shaderParamatersBox.AddChild(createFloatSelector("Near Scatter Strength", waterParams.NearScatter, 
			EmitSignalNearScatterChanged, 
			0f, 20f, 0.01f, true));
		shaderParamatersBox.AddChild(createFloatSelector("Global Scatter Strength", 
			waterParams.GlobalScatter, EmitSignalGlobalScatterChanged, 
			0f, 20f, 0.01f, true));
		shaderParamatersBox.AddChild(createFloatSelector("Deep Water Color Strength", waterParams.DeepColorStrength, 
			EmitSignalDeepColorStrengthChanged, 
			0f, 20f, 0.01f, true));

		var foamParamsTabs = new TabContainer(); 
		{
			for (var i = 0; i < FastWaterController.MAX_CASCADES; i++) {
				var foamParametersBox = new VBoxContainer();
				foamParametersBox.Name = $"Cascade {i}";
				{
					var foamParamLabel = new Label();
					foamParamLabel.Text = "Foam Parameters";
					foamParametersBox.AddChild(foamParamLabel);
				}
				var index = i;
				// foamParametersBox.AddChild(createFloatSelectorAction("Whitecap", waterController.CascadeFoamParams[i].X,
				// 	value => {waterController?.SetWhitecap(index, (float) value); }, 0f, 50f, 0.01f, true));
				// foamParametersBox.AddChild(createFloatSelectorAction("Foam Amount", waterController.CascadeFoamParams[i].Y,
				// 	value => {waterController?.SetFoamAmount(index, (float) value); }, 0f, 50f, 0.01f, true));
				// foamParamsTabs.AddChild(foamParametersBox);
			}
		}
		subParametersBox.AddChild(shaderParamatersBox);
		subParametersBox.AddChild(foamParamsTabs);
		
		colorParametersBox.AddChild(subParametersBox);
		
		
		{
			var lightingLabel = new Label();
			lightingLabel.Text = "Lighting Controls";
			Func<string, string, bool, Button> makeButton = (label, uniform_name, needs_debug_shader) => {
				var newButton = new Button();
				newButton.Text = label;
				newButton.ToggleMode = true;
				newButton.ButtonPressed = true;
				// newButton.Toggled += (pressed) => {
				// 	if (currShader == OceanShader.Standard && needs_debug_shader) {
				// 		currShader = OceanShader.Debug;
				// 		_shader.SetShader(debugShader);
				// 		SetShaderParameters();
				// 	}
				// 	_shader.SetShaderParameter(uniform_name, pressed);
				// 	
				// };
				return newButton;
			};

			Button diffuseButton = makeButton("Diffuse", "renderDiffuse", true);
			Button specularButton = makeButton("Specular", "renderSpecular", true);
			Button reflectionsButton = makeButton("Reflections", "renderReflections", true);
			Button foamButton = makeButton("Foam", "renderFoam", false);
			
			lightingControlsBox.AddChild(lightingLabel);
			lightingControlsBox.AddChild(diffuseButton);
			lightingControlsBox.AddChild(specularButton);
			lightingControlsBox.AddChild(reflectionsButton);
			lightingControlsBox.AddChild(foamButton);
		}
		var togglesBox = new HBoxContainer();
		
		var mipsButton = new Button();
		mipsButton.Text = "Use Mipmaps";
		mipsButton.ToggleMode = true;
		mipsButton.ButtonPressed = true;
		// mipsButton.Toggled += (pressed) => {
		// 	waterController?.UseMips(pressed);
		// };
		
		var vsyncButton = new Button();
		vsyncButton.Text = "Use Vsync";
		vsyncButton.ToggleMode = true;
		vsyncButton.ButtonPressed = false;
		vsyncButton.Toggled += (pressed) => {
			DisplayServer.WindowSetVsyncMode(pressed ? DisplayServer.VSyncMode.Adaptive : DisplayServer.VSyncMode.Disabled);
		};
		
		var hdrButton = new Button();
		hdrButton.Text = "Use HDR";
		hdrButton.ToggleMode = true;
		hdrButton.ButtonPressed = true;
		hdrButton.Toggled += (pressed) => {
			GetWindow().SetHdrOutputRequested(pressed);
		};
		
		togglesBox.AddChild(mipsButton);
		togglesBox.AddChild(vsyncButton);
		togglesBox.AddChild(hdrButton);

		visualControlsBox.AddChild(colorParametersBox);
		visualControlsBox.AddChild(lightingControlsBox);
		visualControlsBox.AddChild(togglesBox);
		
		canvasLayer.AddChild(visualControlsBox);
		
		window.AddChild(canvasLayer);
		
		
		
		Toggled += on => window.SetVisible(on);
		window.CloseRequested += () => SetPressed(false);
		window.SetVisible(false);
		SetPressed(false);
	}
}