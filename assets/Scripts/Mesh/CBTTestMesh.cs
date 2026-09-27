using FluxxiShaderLang;
using Godot;
using Godot.Collections;
using GodotWaterRendering.assets.Scripts.Utility;


public partial class CBTTestMesh : DynamicMeshInstance3D {
	/// <summary>
	/// Debug views that survive vertex deduplication. A per-triangle wireframe is deliberately
	/// absent: with vertices shared between bisectors there is no per-vertex attribute able to
	/// carry barycentric coordinates, so edge distance cannot be rebuilt in the fragment shader.
	/// TriangleId is the nearest equivalent.
	/// </summary>
	public enum DisplayMode {
		/// <summary>Flat facet shading off screen-space derivatives. Unlit, so it does not depend
		/// on the scene having a light or an environment.</summary>
		Solid = 0,

		/// <summary>Hashed colour per vertex, interpolated across the triangle. Deduplication reads
		/// directly off this: a shared vertex makes the colour continuous across the edge, while a
		/// vertex that got duplicated leaves a visible seam.</summary>
		VertexId = 1,

		/// <summary>Hashed colour per triangle, taken from its provoking vertex. Reads as a
		/// triangulation view, though two triangles sharing a provoking vertex share a colour.</summary>
		TriangleId = 2
	}

	private static readonly StringName DisplayModeParam = "display_mode";

	private ShaderMaterial _material;
	private bool _warnedMissingMaterial;

	private FSLFile vertTestFile = FSLFile.FromFile("res://assets/Shaders/Compute/FSL/mesh/cbs/vertex_test.fsl");
	private ComputeKernel dummyVertKernel;
	private DisplayMode _mode = DisplayMode.TriangleId;
	private bool UpdatePlanet = true;
	[Export] public PlanetCamera originCamera;

	[Export]
	public DisplayMode Mode {
		get => _mode;
		set {
			_mode = value;
			ApplyMode();
		}
	}

	/// <summary>Key that cycles through the display modes at runtime.</summary>
	[Export] public Key ToggleKey = Key.F1;
	[Export] public Key PauseKey = Key.F1;
	private bool useVertexKernel = false;

	[Export]
	public bool UseVertexKernel {
		get => useVertexKernel;
		set {
			useVertexKernel = value;
			VertexKernel = value ? dummyVertKernel : null;
		}
	}


	private void OnOriginRebased(Vector3 origin, Vector3 _delta) {
		dummyVertKernel.GetUniformBuffer("SphereTestBuffer").SetBuffer(new Dictionary<StringName, Variant> {
			{"origin", origin},
			{"sphere_radius", SphereRadius}
		});
		var sphere_dims = new Vector3(SphereRadius, SphereRadius, SphereRadius);
		CustomAabb = new Aabb(-sphere_dims - origin, sphere_dims * 2f);
	}

	public override void _Ready() {
		Update = false;
		dummyVertKernel = vertTestFile.GetKernel("vertexTestSphere");
		if (originCamera != null) {
			originCamera.OriginRebased += OnOriginRebased;
			OnOriginRebased(originCamera.RebaseOrigin, new Vector3());
		}
		if (useVertexKernel) VertexKernel = dummyVertKernel;
		ApplyMode();
		
	}

	public override void _UnhandledKeyInput(InputEvent @event) {
		if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;
		
		if (key.Keycode != ToggleKey) {
			if (key.Keycode == PauseKey) {
				UpdatePlanet = !UpdatePlanet;
			}
			return;
		}

		Mode = (DisplayMode)(((int)_mode + 1) % 3);
		GetViewport().SetInputAsHandled();
	}

	public override void _Process(double delta) {
		if (UpdatePlanet) UpdateCBTrees();
		else RedisplaceVerts();
	}

	private void ApplyMode() {
		if (ResolveMaterial() is not { } mat) return;
		mat.SetShaderParameter(DisplayModeParam, (int)_mode);
	}

	private ShaderMaterial ResolveMaterial() {
		if (IsInstanceValid(_material)) return _material;

		_material = MaterialOverride as ShaderMaterial
		            ?? SurfaceMaterial as ShaderMaterial;

		if (_material is null && !_warnedMissingMaterial) {
			_warnedMissingMaterial = true;
		}

		return _material;
	}
}
