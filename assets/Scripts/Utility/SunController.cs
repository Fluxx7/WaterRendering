using Godot;
using System;
using Godot.Collections;
using GodotWaterRendering.assets.Scripts.Utility;

public partial class SunController : OmniLight3D {
	[Export] public PlanetCamera OriginCamera;
	
	private void OnOriginRebased(Vector3 origin, Vector3 delta) {
		Position += delta;
	}
	
	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		if (OriginCamera != null) {
			OriginCamera.OriginRebased += OnOriginRebased;
		}
	}

	// Called every frame. 'delta' is the elapsed time since the previous frame.
	public override void _Process(double delta)
	{
	}
}
