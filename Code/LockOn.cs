using System;

/// <summary>
/// Souls-style lock-on. Press it and the camera settles onto the nearest enemy roughly where you're already
/// looking; from then on the camera keeps him in view, the body turns to face him, and movement is read around
/// him — forward closes, back retreats, sideways circles. Let go of the button, or lose him, and it releases.
///
/// "Roughly where you're looking" is a cone around your aim, not the whole room. A man standing closer behind
/// you never beats the one in front of you, because swinging the view round on a button press is disorienting,
/// not helpful.
///
/// The camera side lives in <see cref="CameraMovement"/>, which asks <see cref="Steer"/> every frame, and the
/// movement side in <see cref="RBPlayerMovement"/>, which asks <see cref="TryGetMoveDirection"/> every physics
/// step. This component only decides who is locked and how to look and move around him.
/// </summary>
public sealed class LockOn : Component
{
	[Property, Group( "Input" ), Description( "Right mouse button in the default input config (the \"Attack2\" action)." )]
	public string InputAction { get; set; } = "Flashlight";

	[Property, Group( "Input" ), Description( "Hold the button to stay locked, let go to release. Off, each press toggles the lock on and off instead." )]
	public bool HoldToLock { get; set; } = false;

	[Property, Group( "Targeting" ), Description( "Farthest an enemy can be to lock onto." )]
	public float MaxDistance { get; set; } = 1500f;

	[Property, Group( "Targeting" ), Description( "How far either side of your aim an enemy can stand and still be picked, in degrees. Only the bearing across the floor counts, so looking down at your feet doesn't rule out the man in front of you." )]
	public float ViewAngle { get; set; } = 50f;

	[Property, Group( "Targeting" ), Description( "How much standing off to the side counts against a candidate. At 1, a man at the edge of ViewAngle is judged as if he were twice as far away — the closest still mostly wins, but the one you're looking at takes a near tie." )]
	public float OffCenterPenalty { get; set; } = 1f;

	[Property, Group( "Aim" ), Range( 0f, 1f ), Description( "How far up his body the camera aims, from his feet (0) to his head (1). Around 0.75 is his chest, 0.5 his hips. Measured on his skeleton, so it scales with his size and follows him when he crouches or staggers." )]
	public float AimFraction { get; set; } = 0.75f;

	[Property, Group( "Free Look" ), Description( "How far the mouse can pull the view off the target, in degrees." )]
	public float FreeLookRadius { get; set; } = 12f;

	[Property, Group( "Free Look" ), Description( "How the mouse loses grip toward the edge. At 1 it fades evenly from the middle out; higher stays loose near the middle and stiffens late; below 1 it's heavy from the start." )]
	public float FreeLookStiffness { get; set; } = 1f;

	[Property, Group( "Free Look" ), Description( "How quickly the view drifts back onto the target. Higher pulls harder; 0 lets the view stay wherever you leave it." )]
	public float FreeLookReturn { get; set; } = 3f;

	[Property, Group( "Keeping Lock" ), Description( "Past this distance the lock lets go." )]
	public float BreakDistance { get; set; } = 2200f;

	[Property, Group( "Keeping Lock" ), Description( "Seconds the target can spend hidden behind the level before the lock lets go. Bodies in the way don't count — in a crowd the target is always half behind somebody." )]
	public float LoseSightTime { get; set; } = 1.5f;

	[Property, Group( "Keeping Lock" ), Description( "When the target goes down — dead or staggered — move straight onto the next one in view instead of letting go." )]
	public bool SwitchWhenDown { get; set; } = true;

	[Property, Group( "Camera" ), Description( "How quickly the camera settles onto the target. Higher is snappier; lower is smoother, and trails the target a little more while you circle." )]
	public float TurnSpeed { get; set; } = 12f;

	[Property, Group( "Camera" ), Description( "Furthest the locked camera pitches up or down, in degrees. Stops it staring at the floor when you jump right over someone." )]
	public float MaxPitch { get; set; } = 35f;

	[Property, Group( "Camera" ), Description( "Closer than this across the floor, the camera stops turning and only pitches, and movement goes back to following the camera. Right on top of someone the bearing to him swings wildly with every inch, which reads as the view spinning." )]
	public float MinTurnDistance { get; set; } = 40f;

	[Property, Group( "Debug" )] public bool LogLock { get; set; } = true;

	/// <summary>Who we're locked onto, or null.</summary>
	public Enemy Target { get; private set; }

	public bool IsLocked => Target.IsValid();

	/// <summary>The point on the target the camera aims at.</summary>
	public Vector3 AimPoint => Target.IsValid() ? AimPointOf( Target ) : Vector3.Zero;

	CameraMovement look;
	RBPlayerMovement movement;
	TimeSince timeSinceSeen;
	TimeSince timeSinceLookedForTarget;
	float orbitRadius;

	// how far the mouse has pulled the view off the target, in degrees; only pitch and yaw are used
	Angles freeLook;

	// the enemy rig's head bone, which AimFraction measures up to
	const string HeadBone = "DEF_spine_005";

	// a model without that bone gets aimed this high above his feet instead, about chest height
	const float FallbackAimHeight = 60f;

	// holding the button with nobody locked, look for someone this often rather than every frame
	const float RetryInterval = 0.1f;

	// circling drift this far off the starting radius is pulled back in at full strength
	const float OrbitCorrection = 100f;

	// knocked or dashed further off the circle than this, and the new distance simply becomes the circle
	const float OrbitReset = 200f;

	protected override void OnStart()
	{
		look ??= CameraMovement.Find( GameObject );
		movement = Components.Get<RBPlayerMovement>( FindMode.EverythingInSelfAndAncestors );
	}

	protected override void OnUpdate()
	{
		if ( IsProxy )
			return;

		// can't act, can't hold a lock — knocked out, or held still by the run's countdown or shop
		if ( movement.IsValid() && movement.ActionsBlocked )
		{
			Release( "can't act" );
			return;
		}

		if ( HoldToLock )
		{
			// held: locked. While nobody is, keep looking a few times a second, so turning toward someone with the
			// button already down picks him up — and so does holding on after the last target goes down
			if ( !Input.Down( InputAction ) )
			{
				Release( "let go" );
			}
			else if ( Target is null && timeSinceLookedForTarget > RetryInterval )
			{
				timeSinceLookedForTarget = 0;

				// only the press itself reports finding nobody; the retries would flood the console
				Acquire( quiet: !Input.Pressed( InputAction ) );
			}
		}
		else if ( Input.Pressed( InputAction ) )
		{
			if ( IsLocked )
				Release( "let go" );
			else
				Acquire();
		}

		// checked on Target rather than IsLocked, so a target that has just been destroyed still gets handled
		if ( Target is not null )
			Maintain();
	}

	/// <summary>
	/// Where the camera should look this frame: eased from <paramref name="current"/> toward the target at
	/// TurnSpeed, then pushed off him by the free look the mouse has built up. Framerate-independent, and fed the
	/// same frame's positions the camera places itself with, which is what keeps the view steady while you circle
	/// and jump. <paramref name="mouse"/> is this frame's mouse turn in degrees.
	/// </summary>
	public Angles Steer( Angles current, Vector3 eye, Angles mouse )
	{
		if ( !IsLocked )
			return current;

		// ease the lock's own aim, with last frame's free look taken back out, so the mouse never feeds into
		// the easing and lags behind it
		var aim = new Angles( current.pitch - freeLook.pitch, current.yaw - freeLook.yaw, current.roll );

		var to = AimPoint - eye;
		var wanted = Rotation.LookAt( to ).Angles();

		// right on top of him: hold the heading and let pitch do the looking
		if ( to.WithZ( 0 ).Length < MinTurnDistance )
			wanted.yaw = aim.yaw;

		wanted.pitch = wanted.pitch.Clamp( -MaxPitch, MaxPitch );

		float t = 1f - MathF.Exp( -TurnSpeed * Time.Delta );

		aim.pitch += ( wanted.pitch - aim.pitch ) * t;
		aim.yaw += DeltaDegrees( aim.yaw, wanted.yaw ) * t;

		UpdateFreeLook( mouse );

		return new Angles( aim.pitch + freeLook.pitch, aim.yaw + freeLook.yaw, aim.roll );
	}

	/// <summary>
	/// The mouse nudges the view off the target inside FreeLookRadius. Pushing outward gets heavier the further
	/// out you are and fades to nothing at the edge; pulling back in is always free. All the while the view
	/// drifts back onto him at FreeLookReturn.
	/// </summary>
	void UpdateFreeLook( Angles mouse )
	{
		float radius = MathF.Max( FreeLookRadius, 0.01f );
		float pitch = mouse.pitch;
		float yaw = mouse.yaw;

		float r = MathF.Sqrt( freeLook.pitch * freeLook.pitch + freeLook.yaw * freeLook.yaw );

		if ( r > 0.001f )
		{
			float outPitch = freeLook.pitch / r;
			float outYaw = freeLook.yaw / r;
			float outward = pitch * outPitch + yaw * outYaw;

			// only the part of the move heading away from the middle meets resistance
			if ( outward > 0f )
			{
				float grip = MathF.Pow( MathF.Max( 1f - r / radius, 0f ), MathF.Max( FreeLookStiffness, 0.01f ) );
				pitch -= outPitch * outward * ( 1f - grip );
				yaw -= outYaw * outward * ( 1f - grip );
			}
		}

		freeLook.pitch += pitch;
		freeLook.yaw += yaw;

		// sliding round the edge can still creep past it; hold it there
		r = MathF.Sqrt( freeLook.pitch * freeLook.pitch + freeLook.yaw * freeLook.yaw );

		if ( r > radius )
		{
			freeLook.pitch *= radius / r;
			freeLook.yaw *= radius / r;
		}

		float keep = MathF.Exp( -FreeLookReturn * Time.Delta );
		freeLook.pitch *= keep;
		freeLook.yaw *= keep;
	}

	/// <summary>
	/// Movement input as a direction around the target: forward closes on him, back retreats, sideways circles.
	/// Worked out from where the player actually is on this physics step rather than from the camera — the camera
	/// eases onto the target and always trails a little, and steering by it would spiral a circle strafe outward.
	/// False when there's nothing to move around, and the caller keeps its normal camera-relative movement.
	/// </summary>
	public bool TryGetMoveDirection( Vector3 position, Vector3 input, float speed, out Vector3 direction )
	{
		direction = Vector3.Zero;

		if ( !IsLocked )
			return false;

		var toTarget = ( Target.WorldPosition - position ).WithZ( 0 );
		float radius = toTarget.Length;

		if ( radius < MinTurnDistance )
		{
			orbitRadius = 0f;
			return false;
		}

		var forward = toTarget / radius;
		var right = Rotation.LookAt( forward ).Right;

		float advance = input.x;
		float strafe = -input.y; // AnalogMove.y is +left

		// a sideways step of speed·dt round a circle turns by (speed·dt)/radius. Aiming half of that turn inward
		// walks the chord instead of the tangent, which keeps the radius instead of spiralling out
		float halfTurn = MathF.Min( speed * Time.Delta / radius, 0.5f ) * 0.5f;
		var sideways = right * strafe * MathF.Cos( halfTurn ) + forward * MathF.Abs( strafe ) * MathF.Sin( halfTurn );

		// purely sideways: hold the distance the circle started at. The chord covers the geometry; this mops up
		// what acceleration lag leaves behind, most of all in the air where steering is loose
		if ( MathF.Abs( advance ) < 0.1f && MathF.Abs( strafe ) > 0.1f )
		{
			if ( orbitRadius <= 0f || MathF.Abs( radius - orbitRadius ) > OrbitReset )
				orbitRadius = radius;

			float drift = ( radius - orbitRadius ) / OrbitCorrection;
			sideways += forward * drift.Clamp( -1f, 1f ) * MathF.Abs( strafe );
		}
		else
		{
			orbitRadius = 0f;
		}

		direction = forward * advance + sideways;
		return true;
	}

	void Acquire( bool quiet = false )
	{
		var picked = FindTarget( null );

		if ( picked is null )
		{
			if ( LogLock && !quiet )
				Log.Info( "[LockOn] nobody in view to lock onto" );

			return;
		}

		LockTo( picked );
	}

	/// <summary>Keeps the lock honest: moves on when the target goes down, and lets go when he's too far or out of sight too long.</summary>
	void Maintain()
	{
		if ( !Target.IsValid() || !Target.WantsSlot )
		{
			var next = SwitchWhenDown ? FindTarget( Target ) : null;

			if ( next is not null )
				LockTo( next );
			else
				Release( "target down" );

			return;
		}

		var eye = EyePosition;

		if ( ( AimPoint - eye ).Length > BreakDistance )
		{
			Release( "too far" );
			return;
		}

		if ( HasLineOfSight( eye, Target ) )
			timeSinceSeen = 0;
		else if ( timeSinceSeen > LoseSightTime )
			Release( "lost sight" );
	}

	void LockTo( Enemy enemy )
	{
		Target = enemy;
		timeSinceSeen = 0;
		orbitRadius = 0f;

		if ( LogLock )
		{
			var renderer = enemy.Renderer;
			bool hasBone = renderer.IsValid() && renderer.TryGetBoneTransform( HeadBone, out _ );

			Log.Info( hasBone
				? $"[LockOn] locked onto {enemy.GameObject.Name}, aiming {AimFraction:0.00} of the way up his body"
				: $"[LockOn] locked onto {enemy.GameObject.Name}, but found no {HeadBone} bone on {( renderer.IsValid() ? renderer.Model?.ResourcePath : "no renderer" )} — aiming a fixed {FallbackAimHeight} up, AimFraction does nothing" );
		}
	}

	void Release( string why )
	{
		if ( Target is null )
			return;

		if ( LogLock )
			Log.Info( $"[LockOn] released {( Target.IsValid() ? Target.GameObject.Name : "target" )} — {why}" );

		Target = null;
		orbitRadius = 0f;
		freeLook = default;
	}

	/// <summary>
	/// The enemy to lock: standing, in range, inside ViewAngle of your aim, in sight, and the closest once standing
	/// off to the side has been counted against him.
	/// </summary>
	Enemy FindTarget( Enemy exclude )
	{
		var eye = EyePosition;
		var aim = AimForward;

		Enemy best = null;
		float bestScore = float.MaxValue;

		foreach ( var enemy in Scene.GetAllComponents<Enemy>() )
		{
			if ( enemy == exclude || !enemy.IsValid() || !enemy.WantsSlot )
				continue;

			var to = AimPointOf( enemy ) - eye;
			var flat = to.WithZ( 0 );
			float distance = to.Length;

			if ( distance > MaxDistance || flat.Length < 1f )
				continue;

			float angle = Vector3.GetAngle( aim, flat.Normal );

			if ( angle > ViewAngle )
				continue;

			float score = distance * ( 1f + OffCenterPenalty * angle / MathF.Max( ViewAngle, 1f ) );

			// the trace is the expensive part, so it only runs for someone who would actually win
			if ( score >= bestScore || !HasLineOfSight( eye, enemy ) )
				continue;

			best = enemy;
			bestScore = score;
		}

		return best;
	}

	/// <summary>Whether the level is in the way. Anything with a rigidbody doesn't count as cover — other enemies, bottles, the target himself.</summary>
	bool HasLineOfSight( Vector3 eye, Enemy enemy )
	{
		var trace = Scene.Trace.Ray( eye, AimPointOf( enemy ) )
			.IgnoreGameObjectHierarchy( GameObject.Root )
			.Run();

		if ( !trace.Hit || !trace.GameObject.IsValid() )
			return true;

		return trace.GameObject.Components.Get<Rigidbody>( FindMode.EverythingInSelfAndAncestors ).IsValid();
	}

	Vector3 AimPointOf( Enemy enemy )
	{
		var renderer = enemy.Renderer;

		if ( renderer.IsValid() && renderer.TryGetBoneTransform( HeadBone, out var head ) )
			return Vector3.Lerp( enemy.WorldPosition, head.Position, AimFraction );

		return enemy.WorldPosition + Vector3.Up * FallbackAimHeight;
	}

	Vector3 EyePosition => Scene.Camera.IsValid() ? Scene.Camera.WorldPosition : WorldPosition + Vector3.Up * 64f;

	/// <summary>Your aim flattened onto the floor. Read from the eye angles rather than the camera, so head bob and tilt can't nudge who gets picked.</summary>
	Vector3 AimForward
	{
		get
		{
			var forward = look.IsValid()
				? look.EyeAngles.ToRotation().Forward
				: Scene.Camera.IsValid() ? Scene.Camera.WorldRotation.Forward : WorldRotation.Forward;

			forward = forward.WithZ( 0 );
			return forward.IsNearlyZero() ? Vector3.Forward : forward.Normal;
		}
	}

	/// <summary>Shortest signed turn from one heading to another, so easing from 170° to -170° goes 20° rather than 340°.</summary>
	static float DeltaDegrees( float from, float to )
	{
		float delta = ( to - from ) % 360f;

		if ( delta > 180f )
			delta -= 360f;
		else if ( delta < -180f )
			delta += 360f;

		return delta;
	}
}
