using System;
using Godot;

namespace GodotWaterRendering.assets.Scripts.Utility;

/// <summary>
/// Free camera for exploring a body whose radius is measured in millions of units.
/// <para>
/// Four things break a conventional fly camera at that scale, and this one addresses each:
/// a fixed movement speed is either unusably slow in orbit or uncontrollable near the ground,
/// so speed is derived from altitude; a world-space Y axis stops being "up" as soon as you
/// cross the equator, so yaw runs around the local surface normal and the horizon is levelled
/// against it; a static near/far pair either z-fights or clips the body away, so the frustum is
/// refitted from the horizon every frame; and <see cref="Node3D.GlobalPosition"/> is float32,
/// whose grid at a radius of 2.5e6 is 0.25 units, so integrating velocity into it drops any
/// step below an eighth of a unit and the camera simply refuses to move slowly.
/// </para>
/// <para>
/// That last one is why this camera carries a floating origin. The authoritative position is
/// held in double precision in <em>planet-local</em> space - measured from the body's centre -
/// and the scene is rendered in a <em>rebased</em> frame whose origin is
/// <see cref="RebaseOrigin"/>, a snapped point that follows the camera. So
/// <c>GlobalPosition</c> stays small and smooth, and everything else in the scene is expected
/// to render in that same rebased frame. See <see cref="RebaseOrigin"/> for what that means for
/// the rest of the scene.
/// </para>
/// <para>
/// Controls: right mouse captures the pointer and looks. WASD moves, Q/E descend/ascend along
/// the local vertical, Shift boosts, Alt crawls. The wheel biases the altitude-derived speed.
/// <see cref="OrbitToggleKey"/> swaps between free flight and orbiting the body,
/// <see cref="LevelToggleKey"/> releases the horizon lock for unconstrained 6DoF in deep space,
/// and <see cref="FrameKey"/> pulls back until the whole body is in frame.
/// </para>
/// </summary>
public partial class PlanetCamera : Camera3D {
	/// <summary>Minimal double-precision vector. Godot's own is float32 in this build, which is
	/// exactly the precision this camera exists to avoid.</summary>
	private readonly struct Vec3D {
		public readonly double X, Y, Z;

		public Vec3D(double x, double y, double z) { X = x; Y = y; Z = z; }
		public Vec3D(Vector3 v) : this(v.X, v.Y, v.Z) { }

		public double Length() => Math.Sqrt(X * X + Y * Y + Z * Z);

		public Vec3D Normalized() {
			double length = Length();
			return length > 1e-12 ? new Vec3D(X / length, Y / length, Z / length) : new Vec3D(0, 1, 0);
		}

		public double Dot(Vec3D b) => X * b.X + Y * b.Y + Z * b.Z;

		public static Vec3D operator +(Vec3D a, Vec3D b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
		public static Vec3D operator -(Vec3D a, Vec3D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
		public static Vec3D operator *(Vec3D a, double s) => new(a.X * s, a.Y * s, a.Z * s);

		public Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);
	}

	/// <summary>Raised when <see cref="RebaseOrigin"/> moves. <c>delta</c> is the shift that every
	/// node rendering in the rebased frame must add to its position to stay put.</summary>
	[Signal] public delegate void OriginRebasedEventHandler(Vector3 origin, Vector3 delta);

	/// <summary>Body being explored. Sampled once in <see cref="_Ready"/> and then left alone, so
	/// that rebasing this node's own transform cannot feed back into the camera's idea of where
	/// the centre is. Call <see cref="RefreshCenter"/> if the body genuinely moves.</summary>
	[Export] public Node3D Planet;

	/// <summary>Centre used when <see cref="Planet"/> is null.</summary>
	[Export] public Vector3 FallbackCenter = Vector3.Zero;

	/// <summary>Distance from the centre to the surface. Altitude, and therefore speed, the near
	/// plane and the ground clamp, are all measured against this.</summary>
	[Export] public float PlanetRadius = 2_500_000f;

	[ExportGroup("Look")]
	[Export] public float Sensitivity = 3f;
	[Export] public bool InvertY;

	/// <summary>How fast the horizon rolls back to level, in e-foldings per second. Zero holds
	/// whatever roll the camera has.</summary>
	[Export] public float LevelSpeed = 8f;

	/// <summary>Pitch limit away from the horizon, in degrees, while the horizon is locked.
	/// Below 90 so that straight up and straight down never reach the gimbal singularity.</summary>
	[Export] public float MaxPitchDegrees = 89f;

	[ExportGroup("Movement")]
	/// <summary>Cruise speed as a fraction of altitude per second. At 0.5 the camera covers half
	/// its height every second, which keeps the apparent rate of approach roughly constant from
	/// orbit all the way down to the surface.</summary>
	[Export] public float SpeedPerAltitude = 0.5f;

	[Export] public float MinSpeed = 0.5f;
	[Export] public float MaxSpeed = 5_000_000f;

	/// <summary>Multiplier applied per wheel notch to the altitude-derived speed.</summary>
	[Export] public float SpeedScale = 1.2f;

	/// <summary>Clamp on the accumulated wheel bias, in notches, either way.</summary>
	[Export] public int MaxSpeedNotches = 20;

	[Export] public float BoostMultiplier = 8f;
	[Export] public float CrawlMultiplier = 0.1f;

	/// <summary>Acceleration in e-foldings per second; larger is snappier. Zero disables smoothing.</summary>
	[Export] public float Damping = 12f;

	/// <summary>Altitude the camera refuses to sink below. Also the altitude used for speed when
	/// sitting on the surface, so that ground-level movement does not stall.</summary>
	[Export] public float MinAltitude = 2f;

	/// <summary>Whether the surface blocks descent.</summary>
	[Export] public bool ClampToSurface = true;

	[ExportGroup("Floating origin")]
	/// <summary>Whether the scene renders in a rebased frame. Turn this off until the rest of the
	/// scene subtracts <see cref="RebaseOrigin"/>: with it on and the CBT mesh still emitting
	/// planet-local vertices, the camera renders a hundred units from the world origin while the
	/// surface is still 2.5e6 away, so you end up stranded at the body's centre.
	/// <para>
	/// While off, <see cref="RebaseOrigin"/> reads as zero, so consumer code written against it is
	/// already correct and becomes live the moment this is switched on. Position still accumulates
	/// in double, so crawl speeds advance at the right average rate instead of stalling outright -
	/// but <c>GlobalPosition</c> is float32, so motion still lands on a 0.25 unit grid and the
	/// surface still shimmers under rotation. Those are what the rebase buys.
	/// </para></summary>
	[Export] public bool FloatingOrigin = true;

	/// <summary>Spacing of the grid <see cref="RebaseOrigin"/> snaps to. Rounded up to a power of
	/// two so that every origin is exactly representable in float32 and the subtraction consumers
	/// perform against it introduces no error of its own. Larger means fewer rebase events and a
	/// larger residual <c>GlobalPosition</c>; 256 leaves roughly 2e-5 of positional resolution.</summary>
	[Export] public float SnapGrid = 256f;

	[ExportGroup("Frustum")]
	/// <summary>Whether the near and far planes are refitted from altitude each frame.</summary>
	[Export] public bool AutoFrustum = true;

	/// <summary>Near plane as a fraction of altitude, bounded below by <see cref="MinNear"/> and
	/// above by the <see cref="MaxDepthRatio"/> cap.</summary>
	[Export] public float NearPerAltitude = 0.002f;

	[Export] public float MinNear = 0.05f;

	/// <summary>Smallest far plane, so scene content nearer than the horizon does not get clipped
	/// when the camera is on the ground and the horizon is only a few kilometres out.</summary>
	[Export] public float MinFar = 5_000f;

	/// <summary>Hard cap on far/near. Above roughly 1.7e7 the depth row of a float32 projection
	/// collapses: (far+near)/(far-near) rounds to exactly 1.0, every fragment resolves to the same
	/// depth, and the scene disappears. Kept well under that for depth precision as well.</summary>
	[Export] public float MaxDepthRatio = 1e6f;

	[ExportGroup("Keys")]
	[Export] public Key OrbitToggleKey = Key.Tab;
	[Export] public Key LevelToggleKey = Key.F2;
	[Export] public Key FrameKey = Key.F3;

	/// <summary>Camera orientation in global space. Held as a quaternion rather than Euler angles
	/// because there is no fixed axis to measure yaw and pitch against once the camera can be
	/// anywhere on a sphere.</summary>
	private Quaternion _orientation = Quaternion.Identity;

	/// <summary>Authoritative position, in planet-local space, in double precision. Everything the
	/// camera decides is derived from this; <c>GlobalPosition</c> is an output, never an input.</summary>
	private Vec3D _planetLocal;

	private Vector3 _center;
	private Vector3 _rebaseOrigin;
	private float _snapGrid = 256f;

	private Vector3 _velocity;
	private Vector2 _lookDelta;
	private int _speedNotches;
	private bool _levelHorizon = true;

	private bool _orbiting;
	private float _orbitLatitude;
	private float _orbitLongitude;
	private double _orbitAltitude;

	/// <summary>Centre of the body, in the original unrebased world frame, as sampled at startup.</summary>
	public Vector3 Center => _center;

	/// <summary>Origin of the rendered frame, expressed in planet-local space. Snapped to
	/// <see cref="SnapGrid"/> so it is exactly representable in float32.
	/// <para>
	/// Everything drawn in the scene renders at <c>planetLocalPosition - RebaseOrigin</c>. For the
	/// CBT mesh that means the vertex kernel subtracts this from each planet-local vertex while
	/// the mesh node's own transform stays at identity; for ordinary nodes such as lights it means
	/// their transform carries the subtraction. Subscribe to <see cref="OriginRebasedEventHandler"/>
	/// to be told when it moves.
	/// </para></summary>
	public Vector3 RebaseOrigin => _rebaseOrigin;

	/// <summary>Camera position in planet-local space - the frame the CBT's <c>input_vertices</c>
	/// live in. This is what belongs in the classifier's <c>camera_pos</c>.</summary>
	public Vector3 PlanetLocalPosition => _planetLocal.ToVector3();

	/// <summary>Camera transform in planet-local space. The classifier's <c>modelToView</c> wants
	/// <c>projection * GetPlanetLocalTransform().AffineInverse() * planetModel</c>; building it
	/// from <c>GetCameraTransform()</c> instead would mix the rebased frame into a matrix that is
	/// applied to unrebased vertices.</summary>
	public Transform3D GetPlanetLocalTransform() => new(GlobalBasis, PlanetLocalPosition);

	/// <summary>Local vertical: the outward surface normal under the camera.</summary>
	public Vector3 UpReference => _planetLocal.Normalized().ToVector3();

	/// <summary>Height above the surface. Negative once the camera is inside the body.</summary>
	public float Altitude => (float)(_planetLocal.Length() - PlanetRadius);

	/// <summary>Speed the camera would cruise at right now, before boost and crawl.</summary>
	public float CruiseSpeed {
		get {
			float reference = Mathf.Max(Altitude, MinAltitude);
			float bias = Mathf.Pow(SpeedScale, _speedNotches);
			return Mathf.Clamp(reference * SpeedPerAltitude * bias, MinSpeed, MaxSpeed);
		}
	}

	private Vector3 NorthPole => Planet is not null && IsInstanceValid(Planet) ? Planet.GlobalBasis.Y.Normalized() : Vector3.Up;

	public override void _Ready() {
		_center = Planet is not null && IsInstanceValid(Planet) ? Planet.GlobalPosition : FallbackCenter;
		_snapGrid = NormalizeSnapGrid(SnapGrid);
		_orientation = GlobalBasis.GetRotationQuaternion().Normalized();

		// The authored transform is read once, in the unrebased frame it was authored in.
		_planetLocal = new Vec3D(GlobalPosition - _center);
		_rebaseOrigin = FloatingOrigin ? SnapToGrid(_planetLocal) : Vector3.Zero;
		ApplyPosition();

		// A rebase expressed in planet-local space only lines up with the rest of the scene when
		// the two frames share an origin.
		if (FloatingOrigin && !_center.IsZeroApprox()) {
			GD.PushWarning($"{Name}: FloatingOrigin needs the body centred on the world origin, but Center is {_center}.");
		}
	}

	/// <summary>Re-samples the body's centre from <see cref="Planet"/>. Only needed if the body
	/// itself moves in the unrebased frame; a rebase of its node is not such a move.</summary>
	public void RefreshCenter() {
		if (Planet is not null && IsInstanceValid(Planet)) _center = Planet.GlobalPosition;
	}

	public override void _Input(InputEvent @event) {
		if (!Current) return;

		if (@event is InputEventMouseMotion motion && Input.GetMouseMode() == Input.MouseModeEnum.Captured) {
			_lookDelta += motion.Relative;
		}

		if (@event is InputEventMouseButton { Pressed: true } wheel) {
			switch (wheel.ButtonIndex) {
				case MouseButton.WheelUp:
					_speedNotches = Mathf.Min(_speedNotches + 1, MaxSpeedNotches);
					break;
				case MouseButton.WheelDown:
					_speedNotches = Mathf.Max(_speedNotches - 1, -MaxSpeedNotches);
					break;
			}
		}

		if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Right } look) {
			Input.SetMouseMode(look.Pressed ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible);
		}

		if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) {
			Input.SetMouseMode(Input.MouseModeEnum.Visible);
		}
	}

	public override void _UnhandledKeyInput(InputEvent @event) {
		if (!Current || @event is not InputEventKey { Pressed: true, Echo: false } key) return;

		if (key.Keycode == OrbitToggleKey) {
			SetOrbiting(!_orbiting);
		} else if (key.Keycode == LevelToggleKey) {
			_levelHorizon = !_levelHorizon;
		} else if (key.Keycode == FrameKey) {
			FrameBody();
		} else {
			return;
		}

		GetViewport().SetInputAsHandled();
	}

	public override void _Process(double delta) {
		if (!Current) return;

		float dt = (float)delta;
		Vector2 look = _lookDelta * (Sensitivity / 1000f);
		_lookDelta = Vector2.Zero;
		if (InvertY) look.Y = -look.Y;

		if (_orbiting) {
			UpdateOrbit(look, dt);
		} else {
			UpdateFly(look, dt);
		}

		ApplyPosition();
		if (AutoFrustum) UpdateFrustum();
	}

	// --- floating origin ---------------------------------------------------

	/// <summary>Pushes the authoritative position out to the node, re-snapping the rebase origin
	/// first if the camera has drifted a full cell away from it.</summary>
	private void ApplyPosition() {
		if (FloatingOrigin) {
			// Hysteresis of one whole cell: snapping on every crossing would fire a rebase every
			// few frames while the camera loiters on a boundary, and each rebase moves every node
			// in the scene.
			if ((_planetLocal - new Vec3D(_rebaseOrigin)).Length() > _snapGrid) {
				SetRebaseOrigin(SnapToGrid(_planetLocal));
			}
		} else {
			SetRebaseOrigin(Vector3.Zero);
		}

		GlobalPosition = _center + (_planetLocal - new Vec3D(_rebaseOrigin)).ToVector3();
		GlobalBasis = new Basis(_orientation);
	}

	/// <summary>Moves the rebase origin and tells the scene by how much, if it actually changed.</summary>
	private void SetRebaseOrigin(Vector3 origin) {
		if (origin == _rebaseOrigin) return;

		Vector3 shift = _rebaseOrigin - origin;
		_rebaseOrigin = origin;
		EmitSignal(SignalName.OriginRebased, origin, shift);
	}

	private Vector3 SnapToGrid(Vec3D p) {
		double grid = _snapGrid;
		return new Vector3(
			(float)(Math.Round(p.X / grid) * grid),
			(float)(Math.Round(p.Y / grid) * grid),
			(float)(Math.Round(p.Z / grid) * grid));
	}

	/// <summary>Rounds the requested spacing up to a power of two, so that snapped origins land on
	/// values float32 holds exactly and consumers' subtraction against them stays lossless.</summary>
	private static float NormalizeSnapGrid(float requested) {
		float grid = Mathf.Max(requested, 1f);
		return Mathf.Pow(2f, Mathf.Ceil(Mathf.Log(grid) / Mathf.Log(2f)));
	}

	// --- free flight -------------------------------------------------------

	private void UpdateFly(Vector2 look, float dt) {
		Vector3 up = UpReference;

		// Yaw about the surface normal rather than the camera's own up, so that heading stays
		// meaningful while pitched and the camera cannot walk its roll around on its own.
		Vector3 yawAxis = _levelHorizon ? up : new Basis(_orientation).Y;
		if (!Mathf.IsZeroApprox(look.X)) {
			_orientation = new Quaternion(yawAxis, -look.X) * _orientation;
		}

		float pitch = -look.Y;
		if (_levelHorizon) pitch = ClampPitch(pitch, up);
		if (!Mathf.IsZeroApprox(pitch)) {
			_orientation = _orientation * new Quaternion(Vector3.Right, pitch);
		}

		_orientation = _orientation.Normalized();
		if (_levelHorizon && LevelSpeed > 0f) LevelHorizon(up, dt);

		Basis basis = new(_orientation);
		Vector3 input = ReadMoveInput();
		Vector3 direction = basis.X * input.X + basis.Z * input.Z;
		// Vertical runs along the local vertical, not the camera's up, so that ascending while
		// pitched down still gains altitude.
		direction += (_levelHorizon ? up : basis.Y) * input.Y;
		if (direction.LengthSquared() > 0f) direction = direction.Normalized();

		float speed = CruiseSpeed * ModifierMultiplier();
		Vector3 target = direction * speed;
		_velocity = Damping > 0f ? _velocity.Lerp(target, 1f - Mathf.Exp(-Damping * dt)) : target;

		// Integration happens in double. In float32 at a radius of 2.5e6 any step below an eighth
		// of a unit vanishes entirely, which freezes the camera solid at crawl speed.
		_planetLocal += new Vec3D(_velocity) * dt;

		if (ClampToSurface) {
			double length = _planetLocal.Length();
			double floorDistance = PlanetRadius + MinAltitude;
			if (length < floorDistance && length > 1e-9) {
				Vec3D normal = _planetLocal * (1.0 / length);
				_planetLocal = normal * floorDistance;
				// Drop the inward component so the camera slides along the surface instead of
				// grinding into it and re-accelerating out every frame.
				Vector3 n = normal.ToVector3();
				_velocity -= n * Mathf.Min(_velocity.Dot(n), 0f);
			}
		}
	}

	/// <summary>Limits pitch so the view direction never reaches the local vertical, where yaw
	/// about that same axis would lose all meaning.</summary>
	private float ClampPitch(float pitch, Vector3 up) {
		Vector3 forward = -new Basis(_orientation).Z;
		float elevation = Mathf.Asin(Mathf.Clamp(forward.Dot(up), -1f, 1f));
		float limit = Mathf.DegToRad(MaxPitchDegrees);
		return Mathf.Clamp(pitch, -limit - elevation, limit - elevation);
	}

	/// <summary>Rolls the camera toward zero roll about the surface normal, leaving the view
	/// direction untouched.</summary>
	private void LevelHorizon(Vector3 up, float dt) {
		Basis basis = new(_orientation);
		Vector3 forward = -basis.Z;
		Vector3 right = forward.Cross(up);
		// Degenerate when looking along the local vertical; the pitch clamp normally prevents it,
		// but not in the frame where the camera was moved externally.
		if (right.LengthSquared() < 1e-8f) return;

		right = right.Normalized();
		Basis level = new(right, right.Cross(forward).Normalized(), -forward);
		Quaternion target = level.GetRotationQuaternion().Normalized();
		_orientation = _orientation.Slerp(target, 1f - Mathf.Exp(-LevelSpeed * dt)).Normalized();
	}

	// --- orbit -------------------------------------------------------------

	private void SetOrbiting(bool value) {
		_orbiting = value;
		_velocity = Vector3.Zero;
		if (!value) return;

		double distance = Math.Max(_planetLocal.Length(), PlanetRadius + MinAltitude);
		Vector3 north = NorthPole;
		Vec3D direction = _planetLocal.Normalized();

		_orbitAltitude = distance - PlanetRadius;
		_orbitLatitude = (float)Math.Asin(Math.Clamp(direction.Dot(new Vec3D(north)), -1.0, 1.0));

		Basis frame = FrameFromNorth(north);
		_orbitLongitude = (float)Math.Atan2(direction.Dot(new Vec3D(frame.Z)), direction.Dot(new Vec3D(frame.X)));
	}

	private void UpdateOrbit(Vector2 look, float dt) {
		_orbitLongitude -= look.X;
		_orbitLatitude = Mathf.Clamp(_orbitLatitude + look.Y, Mathf.DegToRad(-89.5f), Mathf.DegToRad(89.5f));

		// Altitude changes multiplicatively: a constant rate in decades covers orbit-to-ground in
		// a few seconds without overshooting near the surface.
		Vector3 input = ReadMoveInput();
		float zoom = input.Y - input.Z;
		if (!Mathf.IsZeroApprox(zoom)) {
			float rate = SpeedPerAltitude * ModifierMultiplier() * Mathf.Pow(SpeedScale, _speedNotches);
			_orbitAltitude *= Math.Exp(zoom * rate * dt);
		}
		_orbitAltitude = Math.Max(_orbitAltitude, MinAltitude);

		Vector3 north = NorthPole;
		Basis frame = FrameFromNorth(north);
		double cos = Math.Cos(_orbitLatitude);
		Vec3D direction = (new Vec3D(frame.X) * (cos * Math.Cos(_orbitLongitude))
		                   + new Vec3D(frame.Z) * (cos * Math.Sin(_orbitLongitude))
		                   + new Vec3D(north) * Math.Sin(_orbitLatitude)).Normalized();

		_planetLocal = direction * (PlanetRadius + _orbitAltitude);
		LookAlong(-direction.ToVector3(), north);
	}

	/// <summary>Points the camera down <paramref name="forward"/>, rolled level against
	/// <paramref name="north"/>.</summary>
	private void LookAlong(Vector3 forward, Vector3 north) {
		Vector3 up = Mathf.Abs(forward.Dot(north)) > 0.999f ? FrameFromNorth(north).X : north;
		Vector3 right = forward.Cross(up);
		if (right.LengthSquared() < 1e-8f) return;

		right = right.Normalized();
		_orientation = new Basis(right, right.Cross(forward).Normalized(), -forward).GetRotationQuaternion().Normalized();
	}

	/// <summary>Any pair of axes perpendicular to <paramref name="north"/>, used as the zero
	/// meridian for orbit longitude.</summary>
	private static Basis FrameFromNorth(Vector3 north) {
		Vector3 seed = Mathf.Abs(north.Dot(Vector3.Right)) > 0.9f ? Vector3.Forward : Vector3.Right;
		Vector3 x = seed.Cross(north).Cross(north).Normalized() * -1f;
		Vector3 z = north.Cross(x).Normalized();
		return new Basis(x, north, z);
	}

	// --- framing and frustum ----------------------------------------------

	/// <summary>Backs off along the current view direction until the whole body fits the frustum.</summary>
	public void FrameBody() {
		Vec3D direction = _planetLocal.Length() > 1e-9 ? _planetLocal.Normalized() : new Vec3D(NorthPole);

		// Half-angle subtended by the body must fit inside the narrower half-FOV.
		float aspect = GetViewport() is { } viewport ? viewport.GetVisibleRect().Size.Aspect() : 1f;
		float halfFov = Mathf.DegToRad(Fov) * 0.5f;
		if (KeepAspect == KeepAspectEnum.Width && aspect > 1f) halfFov = Mathf.Atan(Mathf.Tan(halfFov) / aspect);
		if (KeepAspect == KeepAspectEnum.Height && aspect < 1f) halfFov = Mathf.Atan(Mathf.Tan(halfFov) * aspect);

		double distance = PlanetRadius / Math.Sin(Math.Max(halfFov, 0.001f)) * 1.1;
		_planetLocal = direction * distance;

		if (_orbiting) {
			_orbitAltitude = distance - PlanetRadius;
		} else {
			_velocity = Vector3.Zero;
			LookAlong(-direction.ToVector3(), NorthPole);
		}

		ApplyPosition();
	}

	/// <summary>Refits the depth range to the current altitude. The far plane comes from the
	/// horizon rather than from the far side of the body: nothing on a sphere is farther from the
	/// camera than its tangent point, so that distance bounds everything visible of it and keeps
	/// far/near inside what a float32 projection can represent.</summary>
	private void UpdateFrustum() {
		double altitude = Math.Max(_planetLocal.Length() - PlanetRadius, MinAltitude);

		// sqrt(d^2 - R^2) rewritten as sqrt(h*(2R + h)): the direct form subtracts two numbers
		// near 6.25e12 to get 1e7, which float cannot hold at that magnitude.
		double horizon = Math.Sqrt(altitude * (2.0 * PlanetRadius + altitude));
		float far = Mathf.Max((float)(horizon * 1.1), MinFar);

		float near = Mathf.Max((float)altitude * NearPerAltitude, MinNear);
		near = Mathf.Max(near, far / Mathf.Max(MaxDepthRatio, 1f));

		Near = near;
		Far = far;
	}

	// --- input -------------------------------------------------------------

	/// <summary>Raw movement axes in camera-local terms: X right, Y up, Z back.</summary>
	private static Vector3 ReadMoveInput() {
		Vector3 input = Vector3.Zero;
		if (Input.IsPhysicalKeyPressed(Key.D)) input.X += 1f;
		if (Input.IsPhysicalKeyPressed(Key.A)) input.X -= 1f;
		if (Input.IsPhysicalKeyPressed(Key.E)) input.Y += 1f;
		if (Input.IsPhysicalKeyPressed(Key.Q)) input.Y -= 1f;
		if (Input.IsPhysicalKeyPressed(Key.S)) input.Z += 1f;
		if (Input.IsPhysicalKeyPressed(Key.W)) input.Z -= 1f;
		return input;
	}

	private float ModifierMultiplier() {
		if (Input.IsPhysicalKeyPressed(Key.Shift)) return BoostMultiplier;
		if (Input.IsPhysicalKeyPressed(Key.Alt)) return CrawlMultiplier;
		return 1f;
	}
}
