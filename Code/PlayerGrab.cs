using System;
using System.Linq;

/// <summary>
/// Pick the bar up and throw it at people. Held objects are driven by velocity rather than
/// parented to the camera, so they stay part of the physics world the whole time — they bump
/// furniture, catch on doorframes, and get knocked out of your hands. That friction is the
/// point: a prop that glides through walls on a stick reads as a UI element, not as a chair.
///
/// The carry is a spring with a feed-forward term: the prop is told to match YOUR velocity and
/// then correct whatever error is left. Without the feed-forward the correction alone has to
/// generate all the carry speed, so the prop settles a fixed distance behind you — at a run
/// that lag is most of BreakDistance and the thing falls out of your hands for no reason you
/// can see. Orientation is driven separately, and deliberately not by physics: a prop that
/// tumbles as you turn is impossible to aim, and aiming is the entire point of holding it.
/// </summary>
public sealed class PlayerGrab : Component
{
	[Property, Group( "References" ), Description( "Optional. Where you're aiming from. Falls back to this object's rotation and a guessed eye height." )]
	public CameraMovement Look { get; set; }

	[Property, Group( "References" )] public float FallbackEyeHeight { get; set; } = 64f;

	[Property, Group( "References" ), Description( "Optional, found automatically. Read for your own velocity, which the carry matches and the throw inherits. Without it both still work, they just stop accounting for how fast you're moving." )]
	public RBPlayerMovement Movement { get; set; }

	[Property, Group( "References" ), Description( "Optional, found automatically. You let go of things when you hit the floor." )]
	public PlayerHealth PlayerHealth { get; set; }

	[Property, Group( "Grab" )] public float GrabRange { get; set; } = 110f;
	[Property, Group( "Grab" )] public float GrabRadius { get; set; } = 10f;

	[Property, Group( "Grab" ), Description( "If the trace misses, sweep this far off your aim for something grabbable and take it anyway. A tight crosshair is fine standing still and hopeless at a run — this is what makes grabbing work while you're moving. 0 turns the assist off." )]
	public float AimConeAngle { get; set; } = 12f;

	[Property, Group( "Grab" ), Description( "Assisted grabs still need to see what they're taking, so you can't pull a bottle through a wall." )]
	public bool AssistRequiresLineOfSight { get; set; } = true;

	[Property, Group( "Grab" ), Description( "Anything heavier than this stays where it is. Set to 0 to allow anything, which is funnier but stops the bar meaning anything." )]
	public float MaxMass { get; set; } = 200f;

	[Property, Group( "Grab" ), Description( "Quiet moment after a throw before you can grab again, so the follow-through of one throw isn't read as a grab at the thing you just released." )]
	public float ReGrabDelay { get; set; } = 0.15f;

	[Property, Group( "Hold" ), Description( "How far in front of your eyes the object floats." )]
	public float HoldDistance { get; set; } = 60f;

	[Property, Group( "Hold" ), Description( "How hard the object chases the hold point, as a rate: the gap closes to 1/e of itself every 1/HoldStrength seconds. 18 is about a 55ms catch-up — snappy, with just enough give to read as weight. Crank it for rigid, drop it to let things swing around behind you as you turn." )]
	public float HoldStrength { get; set; } = 18f;

	[Property, Group( "Hold" ), Description( "How fast the object turns to follow your look, same rate units as HoldStrength. Deliberately a touch slower than the position, so a heavy swing round reads as the prop trailing your hands rather than being welded to them." )]
	public float RotationStrength { get; set; } = 14f;

	[Property, Group( "Hold" ), Description( "Keep whatever angle it was lying at when you grabbed it, and rotate that with you. Off, the prop squares up to your aim instead — tidier and more predictable to throw, but it snaps on pickup." )]
	public bool KeepPickupAngle { get; set; } = true;

	[Property, Group( "Hold" ), Description( "Ceiling on the CORRECTION speed only — your own velocity is added on top afterwards. Capping the total instead would mean a prop could never catch up while you were sprinting, which is exactly when it needs to." )]
	public float MaxHoldSpeed { get; set; } = 1200f;

	[Property, Group( "Hold" ), Description( "Drop it if it ends up this far from the hold point — it's behind a wall, wedged, or someone knocked it out of your hands." )]
	public float BreakDistance { get; set; } = 140f;

	[Property, Group( "Throw" ), Description( "Throw speed, applied as a velocity rather than an impulse so a bottle and a barstool leave your hands at the same pace. Mass then shows up where it should — in how hard the thing hits." )]
	public float ThrowSpeed { get; set; } = 850f;

	[Property, Group( "Throw" )] public float ThrowLift { get; set; } = 0.12f;

	[Property, Group( "Throw" ), Description( "How much of your own speed the throw carries. 1 is what physics would do; at a sprint that's most of a second throw's worth of speed on top, which mostly reads as the prop being hard to aim. Half keeps the sense that running throws hit harder without the forward one going through the wall." )]
	public float InheritThrowVelocity { get; set; } = 0.5f;

	[Property, Group( "Throw" ), Description( "Spin (deg/s) put on the prop as it leaves, purely so a thrown barstool tumbles instead of gliding. Doesn't affect damage — ThrownObject scores off closing speed along the contact normal." )]
	public float ThrowSpin { get; set; } = 0f;

	[Property, Group( "Debug" )] public bool LogGrabs { get; set; } = true;

	public bool IsHolding => held.IsValid();
	public Rigidbody Held => held;

	Rigidbody held;
	Stagger heldStagger;
	// The angle the prop sat at relative to your look when you took it, so the carry can rotate
	// that WITH you instead of forcing everything to one orientation.
	Rotation holdOffset = Rotation.Identity;
	// Restored on release: carrying forces CCD on, and we've no business leaving it on afterwards
	// for a prop that was authored without it.
	bool heldPreviousCcd;
	bool dropPressed;
	bool usePressed;
	TimeSince timeSinceThrow = 999f;

	protected override void OnStart()
	{
		// Resolved rather than trusted to the editor reference: this used to point at a
		// CameraLook that no longer exists, and an unset Look fails silently by grabbing from
		// the player's origin.
		Look ??= CameraMovement.Find( GameObject );
		Movement ??= Components.Get<RBPlayerMovement>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );
		PlayerHealth ??= Components.Get<PlayerHealth>( FindMode.Enabled | FindMode.InSelf | FindMode.InDescendants );
	}

	protected override void OnUpdate()
	{
		if ( PlayerHealth.IsValid() && PlayerHealth.IsKnockedOut )
			return;

		// held by the run (the countdown, the bar, the end of the night) — the same gate movement and
		// punching obey
		if ( Movement.IsValid() && Movement.ActionsBlocked )
			return;

		// latched on the render tick — a tap between two fixed updates would otherwise vanish
		if ( Input.Pressed( "Use" ) )
			usePressed = true;

		if ( Input.Pressed( "Drop" ) )
			dropPressed = true;
	}

	protected override void OnFixedUpdate()
	{
		// hands open the moment you go down, wherever the prop happens to be
		if ( PlayerHealth.IsValid() && PlayerHealth.IsKnockedOut )
		{
			if ( IsHolding )
				Release( "went down" );

			usePressed = false;
			dropPressed = false;
			return;
		}

		if ( dropPressed )
		{
			if ( IsHolding )
				Release( "dropped" );

			dropPressed = false;
			usePressed = false;
		}

		if ( usePressed )
		{
			if ( IsHolding )
				Throw();
			else if ( timeSinceThrow >= ReGrabDelay )
				TryGrab();

			usePressed = false;
		}

		UpdateHold();
	}

	// ---------- Carry ----------

	void UpdateHold()
	{
		if ( !IsHolding )
			return;

		// died in your hands: the rigidbody gets switched off for the ragdoll, so there's nothing
		// left to carry. Grab the corpse again if you still want it
		if ( !held.Enabled )
		{
			Release( "it went limp" );
			return;
		}

		var delta = HoldPoint - held.WorldPosition;

		// gone somewhere we can't follow — behind a wall, or wrestled off us
		if ( delta.Length > BreakDistance )
		{
			Release( "lost it" );
			return;
		}

		// Correction first, capped on its own, THEN your own velocity added on top. The prop is
		// asked to travel with you and separately to close the gap, which is what keeps the gap
		// from growing with speed the way a bare proportional chase does.
		var correction = delta * HoldStrength;

		if ( correction.Length > MaxHoldSpeed )
			correction = correction.Normal * MaxHoldSpeed;

		held.Velocity = PlayerVelocity + correction;

		// Orientation is set, not pushed. Driving it through angular velocity means guessing at
		// the solver's units and fighting every contact the prop makes; easing the rotation
		// directly is framerate-independent, always settles, and can't be knocked into a spin.
		// Position is still fully physical, so the prop keeps colliding with the world.
		var target = EyeRotation * holdOffset;
		float t = 1f - MathF.Exp( -RotationStrength * Time.Delta );

		held.WorldRotation = Rotation.Slerp( held.WorldRotation, target, t );
		held.AngularVelocity = Vector3.Zero;
	}

	// ---------- Grabbing ----------

	void TryGrab()
	{
		var origin = EyePosition;
		var forward = EyeForward;

		var trace = Scene.Trace
			.Sphere( GrabRadius, origin, origin + forward * GrabRange )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

		var target = trace.Hit ? trace.GameObject : null;

		// Nothing under the crosshair. Before giving up, sweep the cone — at a sprint the thing
		// you meant to grab is rarely still dead centre by the time the press lands.
		if ( !target.IsValid() && AimConeAngle > 0f )
			target = FindInCone( origin, forward );

		if ( !target.IsValid() )
		{
			Report( "grabbed at nothing" );
			return;
		}

		if ( !CanGrab( target, out var body, out var stagger, out string refusal ) )
		{
			Report( refusal );
			return;
		}

		Attach( body, stagger );
	}

	/// <summary>
	/// Nearest grabbable thing inside the aim cone. Scored by angle rather than distance so the
	/// thing you're most obviously pointing at wins, instead of whatever happens to be closest to
	/// your feet.
	/// </summary>
	GameObject FindInCone( Vector3 origin, Vector3 forward )
	{
		GameObject best = null;
		float bestDot = MathF.Cos( AimConeAngle * MathF.PI / 180f );

		foreach ( var body in Scene.GetAllComponents<Rigidbody>() )
		{
			if ( !body.IsValid() || !body.Enabled )
				continue;

			var candidate = body.GameObject;

			if ( !candidate.IsValid() || IsPartOfUs( candidate ) )
				continue;

			var to = candidate.WorldPosition - origin;
			float distance = to.Length;

			if ( distance > GrabRange || distance < 0.01f )
				continue;

			float dot = Vector3.Dot( to / distance, forward );

			if ( dot < bestDot )
				continue;

			// cheap tests first — line of sight is a trace, and only worth paying for on
			// something that already beat everything else in the cone
			if ( !CanGrab( candidate, out _, out _, out _ ) )
				continue;

			if ( AssistRequiresLineOfSight && !HasLineOfSight( origin, candidate ) )
				continue;

			bestDot = dot;
			best = candidate;
		}

		return best;
	}

	bool HasLineOfSight( Vector3 origin, GameObject target )
	{
		var trace = Scene.Trace
			.Ray( origin, target.WorldPosition )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

		return !trace.Hit || IsPartOf( trace.GameObject, target );
	}

	/// <summary>
	/// Everything that decides whether a thing may be lifted, in one place so the crosshair grab
	/// and the assisted grab can never disagree about what counts. <paramref name="refusal"/> is
	/// the log line for the direct path, which is the only one that reports failures — the cone
	/// sweep just moves on to the next candidate.
	/// </summary>
	bool CanGrab( GameObject target, out Rigidbody body, out Stagger stagger, out string refusal )
	{
		var search = FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors;

		body = target.Components.Get<Rigidbody>( search );
		stagger = target.Components.Get<Stagger>( search );
		refusal = null;

		if ( !body.IsValid() )
		{
			refusal = $"{target.Name} is part of the room, not loose in it";
			return false;
		}

		// a man on his feet doesn't get picked up. Beat the stagger meter out of him first, or
		// kill him — either way what you're picking up is someone who has stopped fighting
		var enemy = target.Components.Get<Enemy>( search );

		// only asked of actual people — a bottle has no Enemy on it and no opinion about being lifted
		if ( enemy.IsValid() )
		{
			bool down = enemy.State == EnemyState.Dead || ( stagger.IsValid() && stagger.IsStaggered );

			if ( !down )
			{
				string meter = stagger.IsValid() ? $"{stagger.Fraction * 100f:0}%" : "no Stagger";
				refusal = $"{target.Name} is still on their feet — stagger them first ({meter})";
				return false;
			}
		}

		float mass = body.PhysicsBody?.Mass ?? 0f;

		if ( MaxMass > 0f && mass > MaxMass )
		{
			refusal = $"{target.Name} is too heavy ({mass:0} > {MaxMass:0})";
			return false;
		}

		return true;
	}

	void Attach( Rigidbody body, Stagger stagger )
	{
		held = body;

		// Catching someone else's throw is a real move, so the prop has to stop being a weapon
		// the instant it's in your hands — otherwise it damages the man who caught it.
		var thrown = body.GameObject.Components.Get<ThrownObject>();
		thrown?.Disarm();

		// A prop carried at a sprint covers more ground per tick than it is wide, and would step
		// straight through walls between two of them.
		heldPreviousCcd = body.EnhancedCcd;
		body.EnhancedCcd = true;

		// Hold the angle it was already at, expressed relative to your look so it turns with you.
		holdOffset = KeepPickupAngle
			? EyeRotation.Inverse * body.WorldRotation
			: Rotation.Identity;

		// stops their stagger timing out in your hands — you decide when they get put down
		heldStagger = stagger;

		if ( heldStagger.IsValid() )
			heldStagger.IsHeld = true;

		Report( $"picked up {body.GameObject.Name} ({( body.PhysicsBody?.Mass ?? 0f ):0} mass)" );
	}

	// ---------- Throwing & letting go ----------

	void Throw()
	{
		var body = held;
		var direction = ( EyeForward + Vector3.Up * ThrowLift ).Normal;

		Detach();

		// armed before it's launched, so a throw at point blank still counts
		var thrown = body.GameObject.Components.GetOrCreate<ThrownObject>();
		thrown.Arm( GameObject );

		// Your own speed comes along, so throwing while running forward really does hit harder
		// and throwing backwards off a sprint mostly just drops the thing.
		body.Velocity = direction * ThrowSpeed + PlayerVelocity * InheritThrowVelocity;
		body.AngularVelocity = ThrowSpin > 0f
			? Vector3.Random.Normal * ThrowSpin
			: Vector3.Zero;

		timeSinceThrow = 0;

		Report( $"threw {body.GameObject.Name} at {body.Velocity.Length:0} u/s" );
	}

	void Release( string why )
	{
		var name = held.IsValid() ? held.GameObject.Name : "nothing";

		Detach();

		Report( $"{why} ({name})" );
	}

	/// <summary>
	/// Everything that has to be undone when a prop leaves your hands, however it leaves them.
	/// Throw and Release both go through here so a dropped prop can't keep the carry's CCD or a
	/// carried man's stagger clock frozen forever.
	/// </summary>
	void Detach()
	{
		if ( held.IsValid() )
			held.EnhancedCcd = heldPreviousCcd;

		held = null;
		holdOffset = Rotation.Identity;

		if ( heldStagger.IsValid() )
			heldStagger.IsHeld = false;

		heldStagger = null;
	}

	protected override void OnDisabled()
	{
		if ( IsHolding )
			Release( "put down" );
	}

	// ---------- Helpers ----------

	void Report( string message )
	{
		if ( LogGrabs )
			Log.Info( $"[Grab] {message}" );
	}

	bool IsPartOfUs( GameObject obj ) => IsPartOf( obj, GameObject );

	static bool IsPartOf( GameObject obj, GameObject root )
	{
		if ( !root.IsValid() )
			return false;

		for ( var g = obj; g.IsValid(); g = g.Parent )
		{
			if ( g == root )
				return true;
		}

		return false;
	}

	Vector3 PlayerVelocity => Movement.IsValid() ? Movement.Velocity : Vector3.Zero;

	Vector3 HoldPoint => EyePosition + EyeForward * HoldDistance;

	Rotation EyeRotation => Look.IsValid()
		? Look.EyeAngles.ToRotation()
		: WorldRotation;

	Vector3 EyePosition => Look.IsValid()
		? Look.WorldPosition
		: WorldPosition + Vector3.Up * FallbackEyeHeight;

	Vector3 EyeForward => EyeRotation.Forward;
}
