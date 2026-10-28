using Sandbox;
using System;

public sealed class RBPlayerMovement : Component
{
	[Header( "References" )]
	[Property] public GameObject Head { get; set; }
	[Property] public GameObject Orientation { get; set; }
	// The visible body/model child. Auto-resolves to Sliding's PlayerObj if unset.
	[Property] public GameObject Body { get; set; }

	// The owner publishes its facing here; every client rotates Body from it (see
	// OnUpdate) so remote players actually see each other turn.
	[Sync] public float BodyYaw { get; set; }

	[Header( "Movement" )]
	[Property] public float maxSpeed { get; set; } = 1000f;
	// Hard ceiling on total horizontal speed — nothing can ever exceed this.
	[Property] public float absoluteMaxSpeed { get; set; } = 3000f;
	[Property] public float groundDrag { get; set; } = 10f;
	[Property] public float acceleration { get; set; } = 15f;

	[Header( "Momentum" )]
	// When you land above maxSpeed, that speed is preserved for this long — your window to
	// bunny-hop, slide, or wall-run and carry it onward before the ground cap eases it down.
	[Property] public float momentumKeepTime { get; set; } = 0.1f;
	// How fast excess ground speed eases back to maxSpeed once the window expires (soft cap).
	[Property] public float groundSpeedDecay { get; set; } = 7f;

	[Property] public float jumpForce { get; set; } = 5f;
	// Scales the AIR target speed only (air cap = maxSpeed * airMultiplier). Air accel
	// rate is controlled by airAcceleration below, not by this.
	[Property] public float airMultiplier { get; set; } = 2f;
	// Quake-style air acceleration: how many u/s of speed you can add per second while
	// air-strafing. Only adds speed (never decelerates), so momentum is preserved.
	// Keep this MODEST — it's the strafe-jump skill ceiling, not your main steering.
	[Property] public float airAcceleration { get; set; } = 500f;
	// Air STEERING: how fast you can turn your existing velocity toward your input WITHOUT
	// changing speed. This is your directional control in the air — crank it for tight,
	// responsive control; it never makes you faster, so it won't blow up your air speed.
	[Property] public float airControl { get; set; } = 5f;
	[Property] public float extraDownForce { get; set; } = 700f;

	// How long a jump press is remembered. Lets a press slightly before landing
	// still fire, instead of being dropped because you weren't grounded that tick.
	[Property] public float jumpBufferTime { get; set; } = 0.3f;

	[Header( "Knockback" )]
	// Seconds a hit (an enemy's kick or punch) takes your legs: no steering and no ground braking, only
	// knockbackDrag, so the hit actually carries you. Without it the ground friction ate a kick in a few ticks.
	[Property] public float knockbackTime { get; set; } = 0.5f;
	// How fast the knockback bleeds off while it lasts. Lower sends you further.
	[Property] public float knockbackDrag { get; set; } = 1.5f;

	[Header( "Slope Handling" )]
	[Property] public float maxSlopeAngle { get; set; } = 50f;
	// Downward push that keeps you glued to the slope over crests / when descending.
	[Property] public float slopeStickForce { get; set; } = 30f;
	// Brief window after jumping where slope stick is ignored so the jump isn't cancelled.
	[Property] public float slopeExitTime { get; set; } = 0.25f;
	private SceneTraceResult slopeHit;
	private bool onSlope;
	private bool exitingSlope;
	private float slopeExitTimer;

	// Exposed so Sliding can read slope state for downhill slide acceleration.
	public bool OnSlopeNow => onSlope;
	public Vector3 SlopeNormal => slopeHit.Normal;

	// Exposed so the camera can read fall speed for the landing impact effect.
	public Vector3 Velocity => rb is not null ? rb.Velocity : Vector3.Zero;

	[Header("GroundCheck")]
	[Property] public float groundCheckDistance { get; set; } = 20f;
	[Property] public bool isGrounded { get; set; }

	Vector3 moveDirection;
	Rigidbody rb;
	PlayerHealth healthComponent;
	Sliding slidingComponent;
	LockOn lockOnComponent;
	DashAbility dashComponent;
	PlayerCombat combatComponent;
	float jumpBufferTimer;
	float momentumTimer;
	float knockbackTimer;
	float floatTimer;
	bool wasGroundedLast;

	// True while a hit is carrying the player (see Knockback); input and ground braking wait it out.
	public bool IsKnockedBack => knockbackTimer > 0f;

	// True while a dash is carrying the player a set distance (it owns velocity until it ends).
	public bool IsDashing => dashComponent is not null && dashComponent.IsDashing;

	// True while an attack's lunge is carrying the player into the target (PlayerCombat owns velocity until it ends).
	public bool IsLunging => combatComponent is not null && combatComponent.IsLunging;

	// True while something other than plain walking owns the player. The wall run, wall climb, grapple,
	// grind and ground pound that used to feed this are gone; the dash is what's left, and abilities still
	// ask this rather than naming it, so bringing another one back is one line here.
	public bool InSpecialMovement => IsDashing;

	[Property] public MovementState state;
	public enum MovementState
	{
		Walking,
		Air
	}

	protected override void OnAwake()
	{
		rb = GameObject.GetComponent<Rigidbody>();
		rb.MassOverride = 10f;

		// The root must never rotate: facing lives on Orientation and the head, and the body is turned by hand in
		// OnUpdate. Locked here rather than trusted to the inspector — a player built without these ticks gets
		// spun by any off-centre hit (an enemy's punch lands with ApplyImpulseAt), and a spun root tips the collider
		// off the floor, swings the camera and sends every input off at an angle.
		rb.Locking = new PhysicsLock { Pitch = true, Yaw = true, Roll = true };
		rb.AngularVelocity = Vector3.Zero;

		healthComponent = GameObject.GetComponent<PlayerHealth>();
		slidingComponent = GameObject.GetComponent<Sliding>();
		Body ??= slidingComponent?.PlayerObj;
		lockOnComponent = GameObject.GetComponent<LockOn>();
		dashComponent = GameObject.GetComponent<DashAbility>();
		combatComponent = GameObject.GetComponent<PlayerCombat>();
	}

	// Lets another system (e.g. wall jump) consume a pending jump press so the same
	// press can't also fire a buffered ground jump moments later.
	public void ClearJumpBuffer()
	{
		jumpBufferTimer = 0f;
	}

	// Countdown freeze (gamemodes call FreezeFor): while active, the body is pinned in
	// place — velocity zeroed every physics tick, buffered jumps discarded — so players
	// can't move before the round starts.
	private float frozenUntil;
	// the pause menu (and the knockout) stop the scene's clock outright — nothing may act then either, or a
	// click on a menu button lands as a jab the moment the game resumes
	public bool IsFrozen => Time.Now < frozenUntil || Scene.GetSystem<HitStop>() is { IsPaused: true };

	// True while this player may not ACT at all — frozen (round countdown, hooked by a
	// grapple strike) or dead waiting to respawn. Weapons and abilities check this before
	// reading input, so a corpse can't shoot and a pinned player can't fight the pin.
	public bool ActionsBlocked => IsFrozen || (healthComponent is not null && healthComponent.IsDead);

	// True when the current freeze came from NetFreeze (another player's ability pinning
	// us). Only those may be released remotely — a stale strike release must never cancel
	// a LOCAL freeze (round countdown, death wait).
	private bool frozenByNet;

	public void FreezeFor( float seconds )
	{
		frozenUntil = Time.Now + seconds;
		frozenByNet = false;
	}

	// Networked freeze: lets an ability on ANY machine (e.g. a grapple strike hooking this
	// player) pin this body. Only the owner applies it — the frozen result replicates out
	// through the normal transform sync. Zero (or negative) seconds releases early, but
	// only a NetFreeze pin — never a local freeze.
	[Rpc.Broadcast]
	public void NetFreeze( float seconds )
	{
		if ( IsProxy )
			return;

		if ( seconds <= 0f )
		{
			if ( frozenByNet )
				frozenUntil = Time.Now;
			return;
		}

		frozenUntil = Time.Now + seconds;
		frozenByNet = true;
	}

	// Opens the landing-style momentum window on demand — the same grace period a fast
	// touchdown gets, where above-max speed is steered (air-style) instead of being braked
	// by ground friction or eased down by the soft cap. An ability (e.g. Dash) calls this so
	// its speed burst actually carries on the ground. (In the air momentum is kept anyway.)
	public void KeepMomentum( float duration )
	{
		momentumTimer = MathF.Max( momentumTimer, duration );
	}

	// Sends the player flying at exactly this velocity, whatever they were doing, and hands them over to it for
	// knockbackTime. Used instead of a physics impulse, which the next tick of ground braking would simply undo.
	public void Knockback( Vector3 velocity )
	{
		combatComponent?.CancelLunge();

		// the slope stick would pin a kick on a ramp to the floor
		slopeExitTimer = slopeExitTime;
		knockbackTimer = knockbackTime;
		rb.Velocity = velocity;
	}

	// Lifts the player with an exact upward speed — a hop, not a full jump — and holds off the extra fall weight
	// for floatTime so it hangs for a moment instead of being slammed straight back down.
	public void Hop( float upSpeed, float floatTime )
	{
		slopeExitTimer = slopeExitTime;
		floatTimer = MathF.Max( floatTimer, floatTime );
		rb.Velocity = rb.Velocity.WithZ( MathF.Max( rb.Velocity.z, upSpeed ) );
	}

	protected override void OnUpdate()
	{
		// Facing: the physics root never rotates (rotation is locked) and aim lives on the
		// head, so the visible body is rotated here instead — on EVERY machine, from the
		// synced yaw. The owner writes the yaw; proxies just apply it.
		if ( !IsProxy && Orientation is not null )
			BodyYaw = Orientation.WorldRotation.Angles().yaw;
		if ( Body is not null )
			Body.WorldRotation = Rotation.FromYaw( BodyYaw );

		// Only the player who owns this body reads input and drives it. On every other client
		// this is a proxy moved purely by the networked transform — it must never self-steer.
		if ( IsProxy )
			return;

		// Edge-triggered input (Pressed/Released) is only reliable on the render frame,
		// so read it here and latch it for the physics step to consume.
		if ( Input.Pressed( "Jump" ) )
			jumpBufferTimer = jumpBufferTime;
	}

	protected override void OnFixedUpdate()
	{
		// Proxies don't simulate movement — the owner does, and the result is networked here.
		if ( IsProxy )
			return;

		// Pinned during a gamemode countdown: hold still and eat any buffered inputs so
		// nothing fires the instant the freeze lifts.
		if ( IsFrozen )
		{
			rb.Velocity = Vector3.Zero;
			jumpBufferTimer = 0f;
			knockbackTimer = 0f;
			floatTimer = 0f;
			return;
		}

		// All physics runs on the fixed clock so movement is framerate-independent and
		// stays in sync with Sliding, which also drives physics here.
		isGrounded = IsGrounded();
		onSlope = OnSlope();

		// Landing momentum window: when you touch down above maxSpeed, keep that speed for a
		// short grace period so you can bunny-hop, slide, or wall-run to carry it onward.
		if ( isGrounded && !wasGroundedLast )
			momentumTimer = momentumKeepTime;
		wasGroundedLast = isGrounded;
		if ( momentumTimer > 0f )
			momentumTimer -= Time.Delta;
		if ( knockbackTimer > 0f )
			knockbackTimer -= Time.Delta;
		if ( floatTimer > 0f )
			floatTimer -= Time.Delta;

		// Count down the post-jump grace window before slope stick re-engages.
		if ( slopeExitTimer > 0f )
			slopeExitTimer -= Time.Delta;
		exitingSlope = slopeExitTimer > 0f;

		StateHandler();

		Move();

		bool slidingNow = slidingComponent is not null && slidingComponent.sliding;
		// Cap only on the ground, only while not sliding, and only AFTER the landing window
		// expires — so air momentum and freshly-landed speed survive long enough to carry.
		if ( !slidingNow && !IsDashing && !IsLunging && !IsKnockedBack && isGrounded && momentumTimer <= 0f ) SpeedControl();

		// Consume a buffered jump once we're actually grounded.
		if ( jumpBufferTimer > 0f )
		{
			jumpBufferTimer -= Time.Delta;
			if ( isGrounded )
			{
				Jump();
				jumpBufferTimer = 0f;
			}
		}

		// No physics damping on horizontal movement — Move() handles accel/decel itself,
		// so nothing fights the lerp and maxSpeed actually means maxSpeed.
		rb.LinearDamping = 0f;

		// Absolute hard cap — runs every tick regardless of state, so nothing can ever exceed it.
		Vector3 horizVel = rb.Velocity.WithZ( 0 );
		if ( horizVel.Length > absoluteMaxSpeed && !IsDashing )
			rb.Velocity = (horizVel.Normal * absoluteMaxSpeed).WithZ( rb.Velocity.z );

		if ( isGrounded && onSlope && !exitingSlope )
		{
			// Always press into the slope so we stay glued to the surface.
			rb.ApplyForce( Vector3.Down * rb.Mass * slopeStickForce );

			// Cancel gravity ONLY when not sliding. While sliding we WANT gravity so the
			// slope can build downhill speed — Sliding handles the slide velocity itself.
			if ( !slidingNow )
				rb.ApplyForce( -Scene.PhysicsWorld.Gravity * rb.Mass );
		}
		else if ( !isGrounded && !IsDashing && floatTimer <= 0f )
		{
			// Extra fall weight for snappy jumps — but NOT mid-dash, which owns its own
			// velocity and would sag under the extra pull, nor while a hop is meant to hang.
			rb.ApplyForce( Vector3.Down * rb.Mass * extraDownForce );
		}
	}

	private void StateHandler()
	{
		if (isGrounded)
			state = MovementState.Walking;
		else
			state = MovementState.Air;
	}

	private void Move()
	{
		// Knocked back: the hit owns you. No steering and no ground braking, just a gentle drag.
		if ( IsKnockedBack )
		{
			Vector3 flatVel = Vector3.Lerp( rb.Velocity.WithZ( 0 ), Vector3.Zero, Time.Delta * knockbackDrag );
			rb.Velocity = flatVel.WithZ( rb.Velocity.z );
			return;
		}

		// Let sliding handle its own velocity
		if ( slidingComponent is not null && slidingComponent.sliding ) return;

		// The dash fully owns velocity AND steering — applying input here too would fight it. So does an attack lunge.
		if ( IsDashing || IsLunging ) return;

		// Get inputs and direction
		Vector3 input = Input.AnalogMove;
		// Locked on, input is read around the target instead of down the camera: forward closes, sideways circles.
		if ( lockOnComponent is null || !lockOnComponent.TryGetMoveDirection( WorldPosition, input, rb.Velocity.WithZ( 0 ).Length, out moveDirection ) )
			moveDirection = Orientation.WorldRotation.Forward * input.x + Orientation.WorldRotation.Right * -input.y;

		Vector3 currentHorizVel = new Vector3( rb.Velocity.x, rb.Velocity.y, 0 );
		bool hasInput = moveDirection.Length > 0.01f;
		bool slopeMove = onSlope && isGrounded && !exitingSlope;

		// Behave like the air (no ground accel/friction) while carrying landing momentum, so a
		// fast touchdown slides on instead of being braked by ground friction.
		bool keepMomentum = momentumTimer > 0f && currentHorizVel.Length > maxSpeed;

		// Ground-only targets: every airborne path below goes through AirAccelerate, which
		// derives its own target from maxSpeed * airMultiplier.
		float targetSpeed = maxSpeed;
		float accel = acceleration;

		if ( hasInput )
		{
			if ( keepMomentum )
			{
				// Carrying momentum on the ground (grapple slide / fast landing): steer it
				// like the air — redirect + accel, never braked by ground friction.
				AirMove( moveDirection.Normal );
			}
			else if ( slopeMove )
			{
				// Drive the FULL 3D velocity along the slope surface so we climb and
				// descend with it instead of staying flat and skipping off.
				Vector3 targetVel = GetSlopeMovementDirection() * targetSpeed;
				rb.Velocity = Vector3.Lerp( rb.Velocity, targetVel, Time.Delta * accel );
			}
			else if ( isGrounded )
			{
				// Flat ground: target magnitude is exactly maxSpeed, so the lerp approaches
				// it without overshooting. acceleration ONLY controls how fast we get there.
				Vector3 targetHorizVel = moveDirection.Normal * targetSpeed;
				Vector3 newHorizVel = Vector3.Lerp( currentHorizVel, targetHorizVel, Time.Delta * accel );
				rb.Velocity = new Vector3( newHorizVel.x, newHorizVel.y, rb.Velocity.z );
			}
			else
			{
				// Air: steer (control, no speed change) + accelerate (speed, capped). The two
				// are separate knobs so you can have tight control without flying too fast.
				AirMove( moveDirection.Normal );
			}
		}
		else if ( isGrounded && !keepMomentum )
		{
			if ( slopeMove )
			{
				// No input on a slope: kill ALL motion (incl. down-slope) so we hold still.
				rb.Velocity = Vector3.Lerp( rb.Velocity, Vector3.Zero, Time.Delta * groundDrag );
			}
			else
			{
				// No input on flat ground: ease to a stop. groundDrag controls how fast
				// you decelerate (higher = snappier stop), nothing else.
				Vector3 newHorizVel = Vector3.Lerp( currentHorizVel, Vector3.Zero, Time.Delta * groundDrag );
				rb.Velocity = new Vector3( newHorizVel.x, newHorizVel.y, rb.Velocity.z );
			}
		}
		// No input while airborne or carrying momentum: preserve velocity (don't touch it).
	}

	// Air movement in two decoupled parts so control and speed are tuned separately:
	//
	//  1) STEER (airControl): blend the velocity VECTOR toward wishDir at your CURRENT speed.
	//     Lerping the vector (not just its angle) lets a reversal pass straight through zero
	//     (decelerate then re-accelerate) instead of arcing around. Steady-state speed is
	//     preserved, so it never makes you faster — crank airControl for snappier redirects.
	//
	//  2) ACCELERATE (airAcceleration): Quake-style additive term, only adds speed up to
	//     the air target measured ALONG wishDir, never decelerates. This is the strafe-jump
	//     skill ceiling and the baseline "hold forward to reach air speed" — keep it modest.
	//
	// External momentum (grapple/jump/fall) is preserved: step 1 keeps the magnitude, and
	// step 2 can't push past the air target in the wish direction.
	private void AirMove( Vector3 wishDir )
	{
		float airTargetSpeed = maxSpeed * airMultiplier;
		Vector3 horizVel = rb.Velocity.WithZ( 0 );
		float speed = horizVel.Length;

		// 1) Steer toward input by blending the velocity VECTOR toward wishDir at your current
		//    speed. Lerping the vector (not just its angle) means reversing input passes
		//    straight through zero — decelerate, then re-accelerate the other way — instead of
		//    swinging around in a wide arc. End speed is preserved (target magnitude == current
		//    speed), so a grapple/dash launch carries; only hard turns dip speed while redirecting.
		if ( speed > 1f )
		{
			Vector3 targetVel = wishDir * speed;
			horizVel = Vector3.Lerp( horizVel, targetVel, Time.Delta * airControl );
		}

		// 2) Accelerate up to the air target along wishDir (never slows you down). After the
		//    steer above, velocity already points more toward wishDir, so this naturally
		//    stops adding speed once you're steering straight into your motion.
		float speedInWishDir = Vector3.Dot( horizVel, wishDir );
		float addSpeed = airTargetSpeed - speedInWishDir;
		if ( addSpeed > 0f )
		{
			float accelSpeed = MathF.Min( airAcceleration * Time.Delta, addSpeed );
			horizVel += wishDir * accelSpeed;
		}

		rb.Velocity = horizVel.WithZ( rb.Velocity.z );
	}

	private void Jump()
	{
		// Briefly suspend slope stick/gravity-cancel so they don't eat the jump.
		slopeExitTimer = slopeExitTime;

		// Zero vertical velocity first so jump height is consistent regardless of
		// whether you were rising, falling, or flat when you pressed jump.
		rb.Velocity = rb.Velocity.WithZ( 0 );
		rb.ApplyImpulse( Vector3.Up * jumpForce * 1000f );
	}


	//HELPER FUNCTIONS
	//-----------------------------------------------------------------------------------------------------------------------------

	// Helper function to check if player in on slope
	private void SpeedControl()
	{
		Vector3 flatVel = new Vector3( rb.Velocity.x, rb.Velocity.y, 0 );
		float speed = flatVel.Length;

		// Soft cap: ease excess speed down to maxSpeed instead of snapping, so a plain landing
		// keeps a little carry rather than a hard stop. (Skipped during the landing window.)
		if ( speed > maxSpeed )
		{
			float newSpeed = MathX.Lerp( speed, maxSpeed, Time.Delta * groundSpeedDecay );
			rb.Velocity = (flatVel.Normal * newSpeed).WithZ( rb.Velocity.z );
		}
	}

	private bool OnSlope()
	{
		slopeHit = Scene.Trace.Ray( WorldPosition, WorldPosition + Vector3.Down * 40)
			.Radius( 16f )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

		if (slopeHit.Hit)
		{
			float angle = Vector3.GetAngle( slopeHit.Normal, Vector3.Up );
			// > 1° so near-flat geometry imperfections don't trigger slope handling
			// (gravity cancel + stick force) on what is effectively flat ground.
			return angle > 1f && angle <= maxSlopeAngle;
		}
		return false;
	}

	// Get the movement direction for the playeer on the slope
	private Vector3 GetSlopeMovementDirection()
	{
		return Vector3.VectorPlaneProject(moveDirection, slopeHit.Normal ).Normal;
	}

	// Check if player is grounded
	bool IsGrounded()
	{
		// Short sphere sweep just below the feet (the root rests ~10 units above the floor).
		// This used to be a 50-unit ray — half a body height of phantom ground — which kept
		// ground steering/friction engaged while visibly airborne (the floaty, mushy feel)
		// and made near-ground double-jump presses turn into buffered ground jumps.
		return Scene.Trace
			.Ray( WorldPosition, WorldPosition + Vector3.Down * groundCheckDistance )
			.Radius( 8f )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run()
			.Hit;
	}
}
