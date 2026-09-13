using Sandbox;
using System;

// Wall climbing. Face a wall and hold forward (W) to climb straight up it on a timer.
// Distinct from WallRunning by geometry: climbing needs a wall IN FRONT of you (you're
// looking into it); running needs a wall to your SIDE. So the two never trigger together.
//
// The climb is limited by ClimbTimer and won't refill until you touch the ground again —
// no infinite vertical. Jump off the wall to cancel into a launch, or climb over the top
// (the wall vanishing from the trace) for an automatic mantle hop.
public sealed class WallClimb : Component
{
	[Header( "Climbing" )]
	// How fast you climb up the wall (u/s).
	[Property] public float ClimbSpeed { get; set; } = 550f;
	// Seconds of climb on a full charge. Refills when you touch the ground (always), and
	// also whenever you voluntarily stop if ResetTimerOnStop is on.
	[Property] public float MaxClimbTime { get; set; } = 1.2f;
	[Property] public float ClimbTimer { get; set; }
	// When true, ending a climb voluntarily (release W, look away, mantle, jump off) refills
	// the timer so you can chain climbs without landing. Running the timer fully out still
	// locks you out until you touch ground, so one tall wall can't be milked forever.
	[Property] public bool ResetTimerOnStop { get; set; } = false;
	// Cooldown after a climb ends before you can grab a wall again. Stops a jump off the
	// wall from instantly re-sticking you to it. Also gives jump-off launches room to breathe.
	[Property] public float ReGrabDelay { get; set; } = 0.5f;
	// Keeps you pressed against the wall while climbing.
	[Property] public float WallStickForce { get; set; } = 60f;

	[Header( "Detection" )]
	// How far in front a wall counts as climbable.
	[Property] public float WallCheckDistance { get; set; } = 50f;
	// Only near-vertical surfaces are walls (|normal.z| below this).
	[Property] public float MaxWallNormalZ { get; set; } = 0.4f;
	// How directly you must face the wall to climb it. 1 = dead-on, lower = more lenient.
	[Property, Range( 0f, 1f )] public float LookThreshold { get; set; } = 0.5068f;

	[Header( "Mantle / Jump" )]
	// Auto hop when you climb over the top of the wall (it leaves the trace), to carry you
	// onto the ledge instead of stalling at the lip.
	[Property] public float MantleUpForce { get; set; } = 400f;
	[Property] public float MantleForwardForce { get; set; } = 300f;
	// Jump off the wall: launches up and away from the surface.
	[Property] public float ClimbJumpUpForce { get; set; } = 650f;
	[Property] public float ClimbJumpAwayForce { get; set; } = 600f;

	[Header( "Camera" )]
	[Property] public float ClimbFov { get; set; } = 110f;
	[Property] public CameraMovement camMovement { get; set; }

	[Property] public bool IsClimbing { get; private set; }

	private Rigidbody rb;
	private RBPlayerMovement playerMovement;
	private GameObject orientation;

	private SceneTraceResult wallHit;
	private bool wallInFront;
	// True once the timer is spent; blocks re-climbing until you land.
	private bool exhausted;
	// Re-grab cooldown: climbing is blocked until Time.Now passes this.
	private float reGrabReadyAt;

	protected override void OnAwake()
	{
		rb = GameObject.GetComponent<Rigidbody>();
		playerMovement = GameObject.GetComponent<RBPlayerMovement>();
		orientation = playerMovement.Orientation;
	}

	protected override void OnStart()
	{
		camMovement ??= CameraMovement.Find( GameObject );
	}

	protected override void OnUpdate()
	{
		if ( IsProxy )
			return;

		CheckForWall();

		// Recharge only resets on the ground — no chaining climbs forever in the air.
		if ( playerMovement.isGrounded )
		{
			exhausted = false;
			ClimbTimer = MaxClimbTime;
		}

		bool holdingForward = Input.AnalogMove.x > 0f;
		bool offCooldown = Time.Now >= reGrabReadyAt;
		bool wantsClimb = wallInFront && holdingForward && !exhausted && ClimbTimer > 0f && offCooldown && !playerMovement.IsGrindingNow && !playerMovement.IsGroundPounding;

		if ( wantsClimb )
		{
			if ( !IsClimbing ) StartClimb();

			ClimbTimer -= Time.Delta;
			if ( ClimbTimer <= 0f )
			{
				// Ran the timer all the way out — not a voluntary stop, so no refill.
				// Locked out until you touch the ground.
				exhausted = true;
				EndClimb( voluntary: false );
			}

			camMovement?.RequestFov( ClimbFov );

			// Jump off the wall to cancel the climb into a launch.
			if ( Input.Pressed( "Jump" ) )
				ClimbJump();
		}
		else if ( IsClimbing )
		{
			// Lost the wall while climbing = climbed over the top → mantle onto the ledge.
			// Otherwise (let go of W / looked away) just stop. Both are voluntary.
			if ( !wallInFront )
				Mantle();
			else
				EndClimb( voluntary: true );
		}
	}

	protected override void OnFixedUpdate()
	{
		if ( IsProxy )
			return;

		if ( IsClimbing )
			ClimbMovement();
	}

	private void CheckForWall()
	{
		// Probe straight ahead of where the body faces (orientation yaw follows your look).
		wallHit = Scene.Trace
			.Ray( orientation.WorldPosition, orientation.WorldPosition + orientation.WorldRotation.Forward * WallCheckDistance )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

		if ( !wallHit.Hit )
		{
			wallInFront = false;
			return;
		}

		// Must be a near-vertical surface AND we must be facing into it (looking at it).
		bool vertical = MathF.Abs( wallHit.Normal.z ) < MaxWallNormalZ;
		bool facingIt = Vector3.Dot( orientation.WorldRotation.Forward, -wallHit.Normal ) > LookThreshold;
		wallInFront = vertical && facingIt;
	}

	private void StartClimb()
	{
		IsClimbing = true;
		// No gravity while climbing — ClimbMovement drives velocity directly.
		rb.Gravity = false;
	}

	private void ClimbMovement()
	{
		// Drive straight up at climb speed, with a push into the wall so we stay glued.
		rb.Velocity = Vector3.Up * ClimbSpeed;
		rb.ApplyForce( -wallHit.Normal * WallStickForce * rb.Mass );
	}

	// Ends the climb. A "voluntary" stop (release / look away / mantle / jump) refills the
	// timer when ResetTimerOnStop is on, enabling chains. A forced stop (timer ran out)
	// never refills.
	private void EndClimb( bool voluntary )
	{
		IsClimbing = false;
		rb.Gravity = true;

		// Brief lockout so we don't immediately re-stick to the same wall (e.g. right
		// after a jump off it).
		reGrabReadyAt = Time.Now + ReGrabDelay;

		if ( voluntary && ResetTimerOnStop )
		{
			ClimbTimer = MaxClimbTime;
			exhausted = false;
		}
	}

	private void Mantle()
	{
		EndClimb( voluntary: true );
		// Pop up and forward over the lip so you land on top instead of stalling.
		Vector3 forward = orientation.WorldRotation.Forward;
		rb.Velocity = rb.Velocity.WithZ( 0f );
		rb.ApplyImpulse( (Vector3.Up * MantleUpForce + forward * MantleForwardForce) * rb.Mass );
	}

	private void ClimbJump()
	{
		Vector3 away = wallHit.Normal;
		// Jumping off is voluntary — chains when ResetTimerOnStop is on. With it off, spend
		// the wall so a re-grab needs a ground touch (the original behavior).
		if ( !ResetTimerOnStop )
			exhausted = true;
		EndClimb( voluntary: true );

		rb.Velocity = rb.Velocity.WithZ( 0f );
		rb.ApplyImpulse( (Vector3.Up * ClimbJumpUpForce + away * ClimbJumpAwayForce) * rb.Mass );
	}
}
