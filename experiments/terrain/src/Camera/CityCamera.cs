using Godot;
using CitySim.TerrainSystem;

namespace CitySim.CameraSystem;

/// <summary>
/// City-builder orbit camera. The node itself is the pivot (the point on the ground being looked at)
/// and carries yaw; a child "Tilt" node carries pitch; the Camera3D sits Distance metres behind it.
///
/// WASD move (Shift = faster), Q/E rotate, R/F tilt, Z/X (or mouse wheel) zoom, middle-mouse drag rotates/tilts.
///
/// Behaviour depends on zoom: at distance 0 the camera sits on the pivot at eye height (first person, can look
/// slightly up); zoomed out it becomes a god view with a steeper minimum pitch. The camera always stays
/// EdgeMargin inside the terrain edge.
/// </summary>
public partial class CityCamera : Node3D
{
	[Export] public Terrain? Terrain { get; set; }

	[ExportGroup("Speeds")]
	/// <summary>Pan speed as a fraction of the zoom distance per second.</summary>
	[Export] public float MoveSpeed { get; set; } = 1.0f;
	/// <summary>Pan speed floor in m/s, so first-person movement isn't a crawl.</summary>
	[Export] public float MinMoveSpeed { get; set; } = 12f;
	[Export] public float BoostMultiplier { get; set; } = 2f;
	[Export(PropertyHint.Range, "1,360,1,suffix:°/s")] public float RotateSpeed { get; set; } = 90f;
	[Export(PropertyHint.Range, "1,180,1,suffix:°/s")] public float TiltSpeed { get; set; } = 60f;
	/// <summary>Zoom rate for Z/X, in log-distance units per second.</summary>
	[Export] public float ZoomSpeed { get; set; } = 1.5f;
	[Export(PropertyHint.Range, "0.01,0.5,0.01")] public float WheelZoomStep { get; set; } = 0.12f;
	/// <summary>How quickly the camera catches up to its targets. Higher is snappier.</summary>
	[Export] public float Smoothing { get; set; } = 10f;
	/// <summary>Faster follow rate for pivot height, so walking over hills doesn't lag.</summary>
	[Export] public float HeightSmoothing { get; set; } = 25f;

	[ExportGroup("Controls")]
	/// <summary>Flips Q/E and horizontal middle-drag.</summary>
	[Export] public bool InvertRotate { get; set; }
	/// <summary>Flips R/F and vertical middle-drag.</summary>
	[Export] public bool InvertTilt { get; set; }
	[Export(PropertyHint.Range, "0.02,1,0.01,suffix:°/px")] public float MouseDragSensitivity { get; set; } = 0.25f;
	[Export] public bool EdgeScroll { get; set; }
	[Export] public float EdgeScrollPixels { get; set; } = 8f;

	[ExportGroup("Limits")]
	/// <summary>Orbit radius at full zoom-in. 0 = first person.</summary>
	[Export(PropertyHint.Range, "0,500,1,suffix:m")] public float MinDistance { get; set; } = 0f;
	[Export(PropertyHint.Range, "100,5000,10,suffix:m")] public float MaxDistance { get; set; } = 1800f;
	/// <summary>Offset making zoom logarithmic while still reaching distance 0.</summary>
	[Export(PropertyHint.Range, "1,50,1,suffix:m")] public float ZoomOffset { get; set; } = 8f;
	[Export(PropertyHint.Range, "-89,89,1,suffix:°")] public float MinPitchNear { get; set; } = -30f;
	[Export(PropertyHint.Range, "-89,89,1,suffix:°")] public float MinPitchFar { get; set; } = 40f;
	/// <summary>Steepest downward look in first person. 0 keeps the eye level; looking up is still allowed.</summary>
	[Export(PropertyHint.Range, "-89,89,1,suffix:°")] public float MaxPitchNear { get; set; } = 0f;
	[Export(PropertyHint.Range, "1,89,1,suffix:°")] public float MaxPitch { get; set; } = 89f;
	[Export(PropertyHint.Range, "0,4,0.1,suffix:m")] public float EyeHeight { get; set; } = 1.7f;
	[Export(PropertyHint.Range, "0,50,0.5,suffix:m")] public float ClearanceNear { get; set; } = 1f;
	[Export(PropertyHint.Range, "0,50,0.5,suffix:m")] public float ClearanceFar { get; set; } = 10f;
	/// <summary>
	/// Both the pivot and the camera itself stay this far inside the terrain edge, so moving or rotating
	/// never swings the view out over the edge.
	/// </summary>
	[Export(PropertyHint.Range, "0,1000,1,suffix:m")] public float EdgeMargin { get; set; } = 200f;

	[ExportGroup("Start")]
	[Export] public float StartDistance { get; set; } = 700f;
	[Export] public float StartPitch { get; set; } = 50f;
	[Export] public float StartYaw { get; set; } = 30f;

	private Node3D _tilt = null!;
	private Camera3D _camera = null!;

	private Vector3 _targetPivot;
	private float _targetYaw, _targetPitch, _targetDistance;
	private float _yaw, _pitch, _distance;
	// Camera rise to clear hills, as a fraction of the orbit distance. Stored relative to distance so zooming in
	// shrinks it with the orbit: an absolute lift left over at distance ~0 would point the view straight down.
	private float _liftRatio;

	public Camera3D Camera => _camera;
	public Vector3 Pivot => Position;
	public float YawDegrees => _yaw;
	public float PitchDegrees => _pitch;
	public float Distance => _distance;

	public override void _Ready()
	{
		_tilt = new Node3D { Name = "Tilt" };
		AddChild(_tilt);
		_camera = new Camera3D { Name = "Camera3D", Fov = 50f, Near = 0.5f, Far = 12000f, Current = true };
		_tilt.AddChild(_camera);

		_targetPivot = Position;
		if (Terrain is not null)
		{
			var b = Terrain.Bounds;
			_targetPivot = new Vector3(b.GetCenter().X, 0f, b.GetCenter().Y);
		}

		_targetYaw = _yaw = StartYaw;
		_targetDistance = _distance = ClampDistance(StartDistance);
		_targetPitch = _pitch = ClampPitch(StartPitch, Zoom01(_distance));
		_targetPivot = ClampPivot(_targetPivot, _yaw, _pitch, _distance);
		Position = _targetPivot;
		ApplyTransforms();
	}

	/// <summary>Moves the camera instantly (no smoothing). Used by debug tooling.</summary>
	public void JumpTo(Vector2 pivotXZ, float distance, float pitch, float yaw)
	{
		_targetDistance = _distance = ClampDistance(distance);
		float z = Zoom01(_distance);
		_targetPitch = _pitch = ClampPitch(pitch, z);
		_targetYaw = _yaw = yaw;
		_targetPivot = ClampPivot(new Vector3(pivotXZ.X, 0f, pivotXZ.Y), _yaw, _pitch, _distance);
		Position = _targetPivot;
		ApplyTransforms();
	}

	/// <summary>0 at full zoom-in (first person), 1 at full zoom-out. Logarithmic in distance.</summary>
	private float Zoom01(float distance)
	{
		float lo = Mathf.Log(MinDistance + ZoomOffset), hi = Mathf.Log(MaxDistance + ZoomOffset);
		return Mathf.Clamp((Mathf.Log(distance + ZoomOffset) - lo) / (hi - lo), 0f, 1f);
	}

	private float ClampDistance(float d) => Mathf.Clamp(d, MinDistance, MaxDistance);

	/// <summary>Multiplies (distance + offset) by e^logDelta, so zoom is even in feel and can reach 0.</summary>
	private float ZoomedDistance(float d, float logDelta)
	{
		float n = ClampDistance(Mathf.Exp(Mathf.Log(d + ZoomOffset) + logDelta) - ZoomOffset);
		// Snap into first person only while zooming in; zooming out from 0 must be able to grow.
		return logDelta < 0f && n < MinDistance + 0.3f ? MinDistance : n;
	}

	/// <remarks>The max opens up quickly (by ~20 m orbit) so a slightly zoomed-out view can already look down.</remarks>
	private float ClampPitch(float pitch, float z) =>
		Mathf.Clamp(pitch, Mathf.Lerp(MinPitchNear, MinPitchFar, Smooth01(z)),
			Mathf.Lerp(MaxPitchNear, MaxPitch, Smooth01(Mathf.Min(1f, 3f * z))));

	private static float Smooth01(float t) => t * t * (3f - 2f * t);

	private float PivotHeight(float x, float zPos, float distance) =>
		GroundHeight(x, zPos, distance) + EyeHeight * (1f - Zoom01(distance));

	/// <summary>
	/// Keeps the pivot and the camera (which orbits it) inside the terrain bounds shrunk by
	/// <see cref="EdgeMargin"/>, and puts the pivot on the ground.
	/// </summary>
	private Vector3 ClampPivot(Vector3 p, float yawDeg, float pitchDeg, float distance)
	{
		float zoom = Zoom01(distance);
		if (Terrain?.Map is null) return p;
		var b = Terrain.Bounds;
		float m = Mathf.Min(EdgeMargin, 0.45f * Mathf.Min(b.Size.X, b.Size.Y));
		// Camera offset from the pivot on the ground plane.
		float horiz = distance * Mathf.Cos(Mathf.DegToRad(pitchDeg));
		float yaw = Mathf.DegToRad(yawDeg);
		p.X = ClampAxis(p.X, horiz * Mathf.Sin(yaw), b.Position.X + m, b.End.X - m);
		p.Z = ClampAxis(p.Z, horiz * Mathf.Cos(yaw), b.Position.Y + m, b.End.Y - m);
		p.Y = PivotHeight(p.X, p.Z, distance);
		return p;
	}

	/// <summary>Clamps p so both p and p + offset lie in [lo, hi]. If impossible, centres the pair.</summary>
	private static float ClampAxis(float p, float offset, float lo, float hi)
	{
		float min = Mathf.Max(lo, lo - offset), max = Mathf.Min(hi, hi - offset);
		return min <= max ? Mathf.Clamp(p, min, max) : 0.5f * (lo + hi - offset);
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		// Shift/Alt/Ctrl + wheel belong to the sculpt brush (size/strength/rotation).
		if (@event is InputEventMouseButton { Pressed: true, ShiftPressed: false, AltPressed: false, CtrlPressed: false } mb
			&& (mb.ButtonIndex == MouseButton.WheelUp || mb.ButtonIndex == MouseButton.WheelDown))
		{
			bool zoomIn = mb.ButtonIndex == MouseButton.WheelUp;
			float oldD = _targetDistance;
			_targetDistance = ZoomedDistance(oldD, zoomIn ? Mathf.Log(1f - WheelZoomStep) : -Mathf.Log(1f - WheelZoomStep));
			if (zoomIn) ZoomTowardCursor(oldD, _targetDistance);
		}
		else if (@event is InputEventPanGesture pan && !pan.ShiftPressed && !pan.AltPressed && !pan.CtrlPressed)
		{
			// macOS trackpad two-finger scroll: swipe up zooms in, like the wheel.
			float oldD = _targetDistance;
			_targetDistance = ZoomedDistance(oldD, pan.Delta.Y * WheelZoomStep * 0.5f);
			if (pan.Delta.Y < 0f) ZoomTowardCursor(oldD, _targetDistance);
		}
		else if (@event is InputEventMagnifyGesture mag)
		{
			_targetDistance = ZoomedDistance(_targetDistance, -Mathf.Log(Mathf.Max(mag.Factor, 0.01f)));
		}
		else if (@event is InputEventMouseMotion { ButtonMask: var mask } mm && (mask & MouseButtonMask.Middle) != 0)
		{
			_targetYaw += mm.Relative.X * MouseDragSensitivity * (InvertRotate ? -1f : 1f);
			_targetPitch = ClampPitch(_targetPitch - mm.Relative.Y * MouseDragSensitivity * (InvertTilt ? -1f : 1f),
				Zoom01(_targetDistance));
		}
	}

	/// <summary>Shifts the pivot toward the ground point under the cursor by the fraction the orbit radius shrank.</summary>
	private void ZoomTowardCursor(float oldD, float newD)
	{
		if (Terrain?.Map is null || oldD < 10f) return; // near first person, zoom stays on the pivot
		var vp = GetViewport();
		var mouse = vp.GetMousePosition();
		if (!Terrain.Raycast(_camera.ProjectRayOrigin(mouse), _camera.ProjectRayNormal(mouse), out var hit)) return;
		float f = (1f - (newD + ZoomOffset) / (oldD + ZoomOffset)) * Smooth01(Mathf.Clamp(oldD / 60f, 0f, 1f));
		_targetPivot.X = Mathf.Lerp(_targetPivot.X, hit.X, f);
		_targetPivot.Z = Mathf.Lerp(_targetPivot.Z, hit.Z, f);
	}

	public override void _Process(double delta)
	{
		float dt = (float)delta;
		float zNow = Zoom01(_distance);

		// --- Input -> targets ---
		Vector2 move = Input.GetVector("cam_left", "cam_right", "cam_forward", "cam_back");
		if (EdgeScroll && move == Vector2.Zero) move = EdgeScrollVector();
		// Ctrl+Q/E rotate the terrain brush instead.
		float rotate = Input.IsKeyPressed(Key.Ctrl) ? 0f : Input.GetAxis("cam_rotate_left", "cam_rotate_right");
		float tilt = Input.GetAxis("cam_tilt_down", "cam_tilt_up");
		float zoom = Input.GetAxis("cam_zoom_in", "cam_zoom_out");

		// Move relative to where the camera is facing, flattened onto the ground plane.
		float yawRad = Mathf.DegToRad(_targetYaw);
		var forward = new Vector3(-Mathf.Sin(yawRad), 0f, -Mathf.Cos(yawRad));
		var right = new Vector3(Mathf.Cos(yawRad), 0f, -Mathf.Sin(yawRad));
		float speed = Mathf.Max(MinMoveSpeed, MoveSpeed * _targetDistance * Mathf.Max(Mathf.Cos(Mathf.DegToRad(_targetPitch)), 0.3f));
		if (Input.IsKeyPressed(Key.Shift)) speed *= BoostMultiplier;
		_targetPivot += (right * move.X - forward * move.Y) * speed * dt;

		// Rotation and tilt slow down when close, for finer control.
		float fine = Mathf.Lerp(0.6f, 1f, zNow);
		_targetYaw += rotate * RotateSpeed * fine * dt * (InvertRotate ? -1f : 1f);
		_targetDistance = ZoomedDistance(_targetDistance, zoom * ZoomSpeed * dt);
		_targetPitch += tilt * TiltSpeed * fine * dt * (InvertTilt ? -1f : 1f);
		// Pitch limits follow the zoom, so zooming out eases the view up out of an impossible angle.
		_targetPitch = ClampPitch(_targetPitch, Zoom01(_targetDistance));
		_targetPivot = ClampPivot(_targetPivot, _targetYaw, _targetPitch, _targetDistance);

		// --- Smooth current values toward targets (frame-rate independent) ---
		float t = 1f - Mathf.Exp(-Smoothing * dt);
		// Pivot height follows the ground under the *current* position: fast in first person so the eye stays
		// on the ground, slower zoomed out so the view glides over terrain.
		var pos = Position.Lerp(_targetPivot, t);
		float heightRate = Mathf.Lerp(HeightSmoothing, 4f, Smooth01(zNow));
		pos.Y = Mathf.Lerp(Position.Y, PivotHeight(pos.X, pos.Z, _distance), 1f - Mathf.Exp(-heightRate * dt));
		Position = pos;
		_yaw = Mathf.Lerp(_yaw, _targetYaw, t);
		_pitch = Mathf.Lerp(_pitch, _targetPitch, t);
		_distance = Mathf.Lerp(_distance, _targetDistance, t);
		if (Mathf.Abs(_distance - _targetDistance) < 0.05f) _distance = _targetDistance;

		ApplyTransforms(dt);
	}

	private Vector2 EdgeScrollVector()
	{
		var vp = GetViewport();
		var m = vp.GetMousePosition();
		var size = vp.GetVisibleRect().Size;
		if (m.X < 0 || m.Y < 0 || m.X > size.X || m.Y > size.Y) return Vector2.Zero;
		return new Vector2(
			m.X <= EdgeScrollPixels ? -1f : m.X >= size.X - EdgeScrollPixels ? 1f : 0f,
			m.Y <= EdgeScrollPixels ? -1f : m.Y >= size.Y - EdgeScrollPixels ? 1f : 0f);
	}

	/// <param name="dt">Frame time for smoothing the hill lift; negative applies it instantly.</param>
	private void ApplyTransforms(float dt = -1f)
	{
		float zoom = Zoom01(_distance);
		float p = Mathf.DegToRad(_pitch);
		float horiz = _distance * Mathf.Cos(p), up = _distance * Mathf.Sin(p);

		// Never let the camera or its line of sight sink into a hill: lift the camera smoothly (fast up,
		// slow down) instead of stepping the pitch, so crossing mountains doesn't pop.
		bool terrain = Terrain?.Map is not null && _distance > 0.5f;
		float required = terrain ? RequiredLift(horiz, up, Mathf.Lerp(ClearanceNear, ClearanceFar, zoom)) : 0f;
		float requiredRatio = terrain ? required / _distance : 0f;
		_liftRatio = dt < 0f ? requiredRatio
			: Mathf.Lerp(_liftRatio, requiredRatio, 1f - Mathf.Exp(-(requiredRatio > _liftRatio ? 12f : 2.5f) * dt));
		// The smoothed lift may eat into the clearance but must never let the camera touch the ground.
		if (terrain) _liftRatio = Mathf.Max(_liftRatio, RequiredLift(horiz, up, 0.3f) / _distance);

		// Angle from the unit orbit, so it stays defined at distance 0 (first person looks along the pitch).
		float camUpUnit = Mathf.Sin(p) + _liftRatio, horizUnit = Mathf.Cos(p);
		Rotation = new Vector3(0f, Mathf.DegToRad(_yaw), 0f);
		_tilt.Rotation = new Vector3(-Mathf.Atan2(camUpUnit, horizUnit), 0f, 0f);
		_camera.Position = new Vector3(0f, 0f, _distance * Mathf.Sqrt(horizUnit * horizUnit + camUpUnit * camUpUnit));
		_camera.Near = Mathf.Lerp(0.1f, 2f, zoom);
	}

	/// <summary>
	/// How far the camera must rise so every point on the line to the pivot clears the terrain,
	/// and the camera itself clears the water (under the surface the water isn't drawn, it's one-sided).
	/// Raising the camera by L raises the point a fraction f along the line by L*f.
	/// </summary>
	private float RequiredLift(float horiz, float up, float clearance)
	{
		float yaw = Mathf.DegToRad(_yaw);
		var dir = new Vector2(Mathf.Sin(yaw), Mathf.Cos(yaw));
		var o = Position;
		float lift = 0f;
		const int samples = 32;
		for (int i = 5; i <= samples; i++) // skip the stretch right next to the pivot
		{
			float f = i / (float)samples;
			float x = o.X + dir.X * horiz * f, z = o.Z + dir.Y * horiz * f;
			float need = Terrain!.GetHeight(x, z) + clearance * f - (o.Y + up * f);
			lift = Mathf.Max(lift, need / f);
		}
		if (Terrain!.GetWaterSurface(o.X + dir.X * horiz, o.Z + dir.Y * horiz) is { } surface)
			lift = Mathf.Max(lift, surface + clearance - (o.Y + up));
		return lift;
	}

	/// <summary>
	/// Ground height under the pivot, averaged over a footprint that grows with zoom. Zoomed out, single
	/// ridges no longer bounce the view; in first person it is the exact height underfoot.
	/// </summary>
	private float GroundHeight(float x, float z, float distance)
	{
		if (Terrain?.Map is null) return 0f;
		float r = 0.12f * distance;
		if (r < 1f) return Terrain.GetHeight(x, z);
		float sum = 2f * Terrain.GetHeight(x, z), weight = 2f;
		for (int ring = 1; ring <= 2; ring++)
		for (int i = 0; i < 8; i++)
		{
			float a = i * Mathf.Tau / 8f + ring * 0.39f;
			float rr = r * ring * 0.5f;
			sum += Terrain.GetHeight(x + Mathf.Cos(a) * rr, z + Mathf.Sin(a) * rr);
			weight += 1f;
		}
		return sum / weight;
	}

	// --- Scripted self-test (--demo-camera): feeds real input events and checks the results ---

	public async void RunDemo()
	{
		var tree = GetTree();
		async System.Threading.Tasks.Task Frames(int n) { for (int i = 0; i < n; i++) await ToSignal(tree, SceneTree.SignalName.ProcessFrame); }
		async System.Threading.Tasks.Task Hold(string action, int n) { Input.ActionPress(action); await Frames(n); Input.ActionRelease(action); await Frames(30); }
		void Wheel(MouseButton b)
		{
			foreach (bool down in new[] { true, false })
				Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = b, Pressed = down, Position = GetViewport().GetVisibleRect().Size / 2 });
		}
		bool ok = true;
		void Check(string what, bool cond) { GD.Print($"Demo camera: {what} {(cond ? "ok" : "FAILED")}  (pivot {Position:0}, d {_distance:0.0}, pitch {_pitch:0}, yaw {_yaw:0}, cam {_camera.GlobalPosition:0})"); ok &= cond; }
		bool CamInside()
		{
			var b = Terrain!.Bounds; var c = _camera.GlobalPosition; float m = EdgeMargin - 5f;
			return c.X >= b.Position.X + m && c.X <= b.End.X - m && c.Z >= b.Position.Y + m && c.Z <= b.End.Y - m;
		}

		var center = Terrain!.Bounds.GetCenter();
		JumpTo(center, 0f, 0f, 0f);
		await Frames(5);
		await Hold("cam_zoom_out", 60);
		Check("Z zooms out of first person", _distance > 5f);
		JumpTo(center, 0f, 0f, 0f);
		await Frames(5);
		for (int i = 0; i < 5; i++) { Wheel(MouseButton.WheelDown); await Frames(2); }
		await Frames(40);
		Check("wheel zooms out of first person", _distance > 3f);
		for (int i = 0; i < 40; i++) { Wheel(MouseButton.WheelUp); await Frames(2); }
		await Frames(60);
		Check("wheel zooms back into first person", _distance == 0f);
		// Z from a hilly zoomed-out view into first person: the view must follow the pitch, not stay pointed down.
		JumpTo(center, 150f, 20f, 0f);
		await Frames(30);
		await Hold("cam_zoom_in", 180);
		await Frames(90);
		float viewPitch = -Mathf.RadToDeg(_tilt.Rotation.X);
		Check($"Z into first person looks at eye level (view {viewPitch:0})",
			_distance == 0f && Mathf.Abs(viewPitch - _pitch) < 3f && viewPitch <= MaxPitchNear + 0.5f);
		await Hold("cam_tilt_up", 60);
		Check($"first person can't look down (view {-Mathf.RadToDeg(_tilt.Rotation.X):0})", -Mathf.RadToDeg(_tilt.Rotation.X) <= MaxPitchNear + 0.5f);
		await Hold("cam_tilt_down", 20);
		Check($"first person can look up (view {-Mathf.RadToDeg(_tilt.Rotation.X):0})", -Mathf.RadToDeg(_tilt.Rotation.X) < MaxPitchNear - 3f);

		// Walk west into the edge, then rotate while zoomed out: camera must stay inside the margin.
		JumpTo(center, 300f, 30f, 90f);
		await Frames(5);
		await Hold("cam_forward", 300);
		Check("walking stops at the margin", CamInside());
		await Hold("cam_rotate_left", 120);
		Check("rotating keeps the camera inside", CamInside());
		JumpTo(center, 1800f, 30f, 45f);
		await Frames(60);
		Check("god view clamps pitch and stays inside", _pitch >= MinPitchFar - 0.5f && CamInside());
		// Fly across the map over the mountains at several zooms and measure frame-to-frame jerk
		// (second difference of camera height), which is what reads as stutter.
		foreach (float d in new[] { 0f, 60f, 250f, 800f })
		{
			var b = Terrain!.Bounds;
			foreach (float zRow in new[] { 0.3f, 0.5f, 0.7f })
			{
				JumpTo(new Vector2(b.Position.X + EdgeMargin + 10f, b.Position.Y + b.Size.Y * zRow), d, 35f, 270f);
				await Frames(20);
				Input.ActionPress("cam_forward");
				float y0 = _camera.GlobalPosition.Y, y1 = y0, maxJerk = 0f;
				for (int i = 0; i < 240; i++)
				{
					await Frames(1);
					float y2 = _camera.GlobalPosition.Y;
					if (i > 30) maxJerk = Mathf.Max(maxJerk, Mathf.Abs(y2 - 2f * y1 + y0));
					y0 = y1; y1 = y2;
				}
				Input.ActionRelease("cam_forward");
				// Jerk allowance scales with distance: a far camera may move more metres per frame.
				float limit = 0.15f + 0.002f * d;
				Check($"smooth flight d={d} row={zRow} (max jerk {maxJerk:0.000} m/frame², limit {limit:0.000})", maxJerk < limit);
			}
		}
		GD.Print(ok ? "Demo camera: all ok" : "Demo camera: FAILURES");
		tree.Quit(ok ? 0 : 1);
	}
}
