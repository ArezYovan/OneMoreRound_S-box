using System;

/// <summary>
/// Makes whatever it's on trail behind your look and settle back, so hands or a held model stop
/// looking bolted to the camera. Add it to anything parented under the head or camera — there are
/// no references to wire up.
///
/// It works on the object's LOCAL transform, taking the pose it's in when the component switches on
/// as the rest pose, so it owns that transform while it runs. If something else needs to move the
/// same object (an animation, a recoil kick), put this on an empty parent instead and let the other
/// thing move the child — two scripts writing one transform just overwrite each other.
/// </summary>
public sealed class Sway : Component
{
	[Property, Group( "Position" ), Description( "How far it slides per unit of mouse movement. Mouse delta is tiny, so this wants to be big." )]
	public float PositionAmount { get; set; } = 8f;

	[Property, Group( "Position" ), Description( "Furthest it can slide from its rest pose." )]
	public float MaxDistance { get; set; } = 9f;

	[Property, Group( "Rotation" ), Description( "How far it tilts (degrees) per unit of mouse movement." )]
	public float RotationAmount { get; set; } = 2.5f;

	[Property, Group( "Rotation" ), Description( "Furthest it can tilt from its rest pose, in degrees." )]
	public float MaxRotation { get; set; } = 9f;

	[Property, Group( "Rotation" ), Description( "Lean into strafes, in degrees. Negative leans the other way, 0 turns it off." )]
	public float StrafeRoll { get; set; } = 4f;

	[Property, Group( "Rotation" ), Description( "Optional. Tilt around this object's position instead of this one's own origin. A model whose origin sits far from what you actually see — well behind the camera, say — swings across the screen when tilted in place. The head, or an empty placed at the grip, makes a good pivot." )]
	public GameObject Pivot { get; set; }

	[Property, Group( "Feel" ), Description( "How quickly the sway chases your mouse. Lower is floatier; 0 follows the mouse raw." )]
	public float Smoothing { get; set; } = 8f;

	[Property, Group( "Feel" ), Description( "How quickly the object follows the sway. This second lag is what gives it weight — lower is heavier; 0 skips it." )]
	public float FollowSpeed { get; set; } = 9f;

	Vector3 restPosition;
	Rotation restRotation = Rotation.Identity;
	// In the parent's space, like the rest pose, so it stays put while the object turns around it.
	Vector3 restPivot;

	// First pass: where the sway wants to be, eased toward the mouse.
	Vector3 swayPosition;
	Angles swayAngles;

	// Second pass: what's actually applied, eased toward the sway.
	Vector3 offsetPosition;
	Rotation offsetRotation = Rotation.Identity;

	bool refused;

	protected override void OnEnabled()
	{
		// Physics is already moving this object. Placing it every frame would pin it in mid-air.
		refused = Components.Get<Rigidbody>() is { MotionEnabled: true };

		if ( refused )
		{
			Log.Warning( $"[Sway] {GameObject.Name} is a physics prop — sway is for things parented to your view, so it's doing nothing." );
			return;
		}

		restPosition = LocalPosition;
		restRotation = LocalRotation;

		var parent = GameObject.Parent;

		restPivot = Pivot.IsValid() && parent.IsValid()
			? parent.WorldTransform.PointToLocal( Pivot.WorldPosition )
			: restPosition;

		swayPosition = Vector3.Zero;
		swayAngles = default;
		offsetPosition = Vector3.Zero;
		offsetRotation = Rotation.Identity;
	}

	protected override void OnUpdate()
	{
		// Driven by this machine's mouse. On everyone else's copy of a player the transform arrives
		// over the network, and moving it here as well would fight that.
		if ( refused || IsProxy )
			return;

		var mouse = Input.MouseDelta;

		// Against the look, so it lags behind: look right and it slides left, look down and it rises.
		var targetPosition = new Vector3(
			0f,
			MathX.Clamp( mouse.x * PositionAmount, -MaxDistance, MaxDistance ),
			MathX.Clamp( mouse.y * PositionAmount, -MaxDistance, MaxDistance ) );

		// AnalogMove.y is +left, so negating it leans toward the side you're strafing to.
		float strafe = -Input.AnalogMove.y * StrafeRoll;

		var targetAngles = new Angles(
			MathX.Clamp( -mouse.y * RotationAmount, -MaxRotation, MaxRotation ),
			MathX.Clamp( mouse.x * RotationAmount, -MaxRotation, MaxRotation ),
			MathX.Clamp( mouse.x * RotationAmount * 0.5f, -MaxRotation, MaxRotation ) + strafe );

		float smooth = Ease( Smoothing );
		swayPosition = Vector3.Lerp( swayPosition, targetPosition, smooth );
		swayAngles = LerpAngles( swayAngles, targetAngles, smooth );

		float follow = Ease( FollowSpeed );
		offsetPosition = Vector3.Lerp( offsetPosition, swayPosition, follow );
		offsetRotation = Rotation.Slerp( offsetRotation, swayAngles.ToRotation(), follow );

		// Both applied in the parent's space, so the sway reads the same however the model sits in
		// it. The pivot term turns the rest position around the pivot; with no pivot it drops out.
		LocalRotation = offsetRotation * restRotation;
		LocalPosition = restPivot + offsetRotation * ( restPosition - restPivot ) + offsetPosition;
	}

	protected override void OnDisabled()
	{
		// Put it back, so switching sway off doesn't leave the object stuck mid-swing.
		if ( refused || IsProxy )
			return;

		LocalPosition = restPosition;
		LocalRotation = restRotation;
	}

	// Fraction of the remaining gap to close this frame. Exponential rather than Time.Delta * rate so
	// the feel holds at any framerate and a hitch can't overshoot. A rate of 0 or less snaps.
	static float Ease( float rate ) => rate > 0f ? 1f - MathF.Exp( -rate * Time.Delta ) : 1f;

	// Per component, no wrapping — the sway never gets anywhere near ±180.
	static Angles LerpAngles( Angles a, Angles b, float t ) => new Angles(
		MathX.Lerp( a.pitch, b.pitch, t ),
		MathX.Lerp( a.yaw, b.yaw, t ),
		MathX.Lerp( a.roll, b.roll, t ) );
}
