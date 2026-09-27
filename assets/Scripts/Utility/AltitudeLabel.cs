using Godot;

namespace GodotWaterRendering.assets.Scripts.Utility;

/// <summary>Shows a <see cref="PlanetCamera"/>'s height above the surface, in units that fit.</summary>
public partial class AltitudeLabel : Label {
	/// <summary>Camera to read. Falls back to the viewport's current camera when that is a
	/// <see cref="PlanetCamera"/>, so the label follows camera switches unassigned.</summary>
	[Export] public PlanetCamera Camera;

	public override void _Process(double delta) {
		PlanetCamera camera = Camera ?? GetViewport().GetCamera3D() as PlanetCamera;
		Text = camera is null ? "Altitude: -" : "Altitude: " + FormatDistance(camera.Altitude);
	}

	private static string FormatDistance(float units) {
		float magnitude = Mathf.Abs(units);
		if (magnitude < 1_000f) return $"{units:0.00} m";
		if (magnitude < 1_000_000f) return $"{units / 1_000f:0.00} km";
		return $"{units / 1_000f:N0} km";
	}
}
