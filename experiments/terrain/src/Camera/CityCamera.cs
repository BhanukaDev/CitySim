using Godot;
using CitySim.TerrainSystem;

namespace CitySim.CameraSystem;

/// <summary>
/// City-builder orbit camera. The node itself is the pivot (the point on the ground being looked at)
/// and carries yaw; a child "Tilt" node carries pitch; the Camera3D sits Distance metres behind it.
///
/// WASD move, Q/E rotate, R/F tilt, Z/X (or mouse wheel) zoom.
/// </summary>
public partial class CityCamera : Node3D
{
	[Export] public Terrain? Terrain { get; set; }

	[ExportGroup("Speeds")]
	/// <summary>Pan speed as a fraction of the zoom distance per second.</summary>
	[Export] public float MoveSpeed { get; set; } = 1.0f;
	[Export(PropertyHint.Range, "1,360,1,suffix:°/s")] public float RotateSpeed { get; set; } = 90f;
	[Export(PropertyHint.Range, "1,180,1,suffix:°/s")] public float TiltSpeed { get; set; } = 60f;
	/// <summary>Zoom rate for Z/X: distance changes by e^ZoomSpeed per second.</summary>
	[Export] public float ZoomSpeed { get; set; } = 1.5f;
	[Export(PropertyHint.Range, "0.01,0.5,0.01")] public float WheelZoomStep { get; set; } = 0.12f;
	/// <summary>How quickly the camera catches up to its targets. Higher is snappier.</summary>
	[Export] public float Smoothing { get; set; } = 10f;

	[ExportGroup("Limits")]
	[Export(PropertyHint.Range, "1,500,1,suffix:m")] public float MinDistance { get; set; } = 15f;
	[Export(PropertyHint.Range, "100,5000,10,suffix:m")] public float MaxDistance { get; set; } = 1500f;
	[Export(PropertyHint.Range, "1,89,1,suffix:°")] public float MinPitch { get; set; } = 10f;
	[Export(PropertyHint.Range, "1,89,1,suffix:°")] public float MaxPitch { get; set; } = 85f;
	[Export(PropertyHint.Range, "0,50,0.5,suffix:m")] public float GroundClearance { get; set; } = 2f;

	[ExportGroup("Start")]
	[Export] public float StartDistance { get; set; } = 700f;
	[Export] public float StartPitch { get; set; } = 50f;
	[Export] public float StartYaw { get; set; } = 30f;

	private Node3D _tilt = null!;
	private Camera3D _camera = null!;

	private Vector3 _targetPivot;
	private float _targetYaw, _targetPitch, _targetDistance;
	private float _yaw, _pitch, _distance;

	public Camera3D Camera => _camera;
	public Vector3 Pivot => Position;
	public float YawDegrees => _yaw;
	public float PitchDegrees => _pitch;
	public float Distance => _distance;

	public override void _Ready()
	{
		_tilt = new Node3D { Name = "Tilt" };
		AddChild(_tilt);
		_camera = new Camera3D { Name = "Camera3D", Fov = 50f, Near = 0.5f, Far = 8000f, Current = true };
		_tilt.AddChild(_camera);

		_targetPivot = Position;
		if (Terrain is not null)
		{
			var b = Terrain.Bounds;
			_targetPivot = new Vector3(b.GetCenter().X, 0f, b.GetCenter().Y);
			_targetPivot.Y = Terrain.GetHeight(_targetPivot.X, _targetPivot.Z);
		}

		_targetYaw = _yaw = StartYaw;
		_targetPitch = _pitch = Mathf.Clamp(StartPitch, MinPitch, MaxPitch);
		_targetDistance = _distance = Mathf.Clamp(StartDistance, MinDistance, MaxDistance);
		Position = _targetPivot;
		ApplyTransforms();
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		// Shift/Alt + wheel belong to the sculpt brush (size/strength).
		if (@event is InputEventMouseButton { Pressed: true, ShiftPressed: false, AltPressed: false } mb)
		{
			if (mb.ButtonIndex == MouseButton.WheelUp)
				_targetDistance *= 1f - WheelZoomStep;
			else if (mb.ButtonIndex == MouseButton.WheelDown)
				_targetDistance *= 1f + WheelZoomStep;
			_targetDistance = Mathf.Clamp(_targetDistance, MinDistance, MaxDistance);
		}
	}

	public override void _Process(double delta)
	{
		float dt = (float)delta;

		// --- Input -> targets ---
		Vector2 move = Input.GetVector("cam_left", "cam_right", "cam_forward", "cam_back");
		float rotate = Input.GetAxis("cam_rotate_left", "cam_rotate_right");
		float tilt = Input.GetAxis("cam_tilt_down", "cam_tilt_up");
		float zoom = Input.GetAxis("cam_zoom_in", "cam_zoom_out");

		// Move relative to where the camera is facing, flattened onto the ground plane.
		float yawRad = Mathf.DegToRad(_targetYaw);
		var forward = new Vector3(-Mathf.Sin(yawRad), 0f, -Mathf.Cos(yawRad));
		var right = new Vector3(Mathf.Cos(yawRad), 0f, -Mathf.Sin(yawRad));
		_targetPivot += (right * move.X - forward * move.Y) * MoveSpeed * _targetDistance * dt;

		_targetYaw -= rotate * RotateSpeed * dt;
		// R ("tilt up") raises the view toward the horizon, which means a smaller pitch.
		_targetPitch = Mathf.Clamp(_targetPitch - tilt * TiltSpeed * dt, MinPitch, MaxPitch);
		_targetDistance = Mathf.Clamp(_targetDistance * Mathf.Exp(zoom * ZoomSpeed * dt), MinDistance, MaxDistance);

		if (Terrain?.Map is not null)
		{
			var b = Terrain.Bounds;
			_targetPivot.X = Mathf.Clamp(_targetPivot.X, b.Position.X, b.End.X);
			_targetPivot.Z = Mathf.Clamp(_targetPivot.Z, b.Position.Y, b.End.Y);
			_targetPivot.Y = Terrain.GetHeight(_targetPivot.X, _targetPivot.Z);
		}

		// --- Smooth current values toward targets (frame-rate independent) ---
		float t = 1f - Mathf.Exp(-Smoothing * dt);
		Position = Position.Lerp(_targetPivot, t);
		_yaw = Mathf.Lerp(_yaw, _targetYaw, t);
		_pitch = Mathf.Lerp(_pitch, _targetPitch, t);
		_distance = Mathf.Lerp(_distance, _targetDistance, t);

		ApplyTransforms();
	}

	private void ApplyTransforms()
	{
		Rotation = new Vector3(0f, Mathf.DegToRad(_yaw), 0f);
		_tilt.Rotation = new Vector3(-Mathf.DegToRad(_pitch), 0f, 0f);
		_camera.Position = new Vector3(0f, 0f, _distance);

		// Never let the camera sink into a hill between it and the pivot.
		if (Terrain?.Map is not null)
		{
			var gp = _camera.GlobalPosition;
			float minY = Terrain.GetHeight(gp.X, gp.Z) + GroundClearance;
			if (gp.Y < minY)
				_camera.GlobalPosition = new Vector3(gp.X, minY, gp.Z);
		}
	}
}
