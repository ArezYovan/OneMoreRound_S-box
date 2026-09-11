using Sandbox;
using System;

// Air jump (double / triple / …). Press Jump while airborne and you jump again. You get
// AirJumps of them per trip through the air and the charges only come back when you touch
// the ground, so the default of 1 is a plain double jump.
//
// The charge count is the real limiter. Stamina is a second gate on top — a jump needs both
// a charge and the wind to pay for it — because stamina alone can't bound this: it refills
// on its own, so a cheap enough jump is an infinite one, which is exactly what a pool-only
// version turned into here.
//
// The ground jump itself is free and belongs to RBPlayerMovement; this only ever handles the
// airborne ones.
public sealed class DoubleJump : Component
{
	[Header( "Jump" )]
	// Air jumps per trip through the air. 1 = a classic double jump (one ground + one air).
	// 2 = triple jump, and so on. Charges are restored on landing, never mid-air.
	[Property] public int AirJumps { get; set; } = 1;
	// Upward launch of an air jump. Matches the ground jump (jumpForce) by default; the *1000
	// and mass-10 convention is the same as RBPlayerMovement.Jump.
	[Property] public float JumpForce { get; set; } = 5f;
	// Stamina bars spent per air jump (shared pool with Dash). 0 = free.
	[Property] public float StaminaCost { get; set; } = 1f;
	// Relaunch horizontal momentum toward your input direction (keeps the same speed) instead
	// of just adding height. Off = pure vertical, momentum untouched.
	[Property] public bool RedirectToInput { get; set; } = false;
	// Brief lockout after leaving any special movement state (wall run/climb, grapple, grind)
	// before an air jump can fire. Stops a wall-jump / climb-jump / grind-jump press from also
	// triggering a double jump on the same frame the state ends.
	[Property] public float SpecialMovementGrace { get; set; } = 0.15f;

	// Give the charges back when a wall run / climb / grind ends, as well as on landing. Off
	// by default: "until you land on the floor" means the floor. Turn it on once wall running
	// is in and you want a wall to read as a surface you can kick off again.
	[Property] public bool RefreshOnSpecialMovement { get; set; } = false;

	[Header( "Feel (optional)" )]
	// A quick FOV punch on the air jump. 0 = none. Uses the camera's per-request FOV speed.
	[Property] public float JumpFov { get; set; } = 120f;
	[Property] public float JumpFovSpeed { get; set; } = 25f;
	[Property] public float JumpFovTime { get; set; } = 0.12f;
	[Property] public CameraMovement cameraMovement { get; set; }
	[Property] public SoundEvent JumpSound { get; set; }

	private Rigidbody rb;
	private RBPlayerMovement playerMovement;
	private Stamina stamina;
	private bool jumpQueued;
	private float lastSpecialTime = -999f;
	private float fovTimer;
	private int airJumpsUsed;
	private bool wasInSpecial;

	/// <summary>Air jumps left before you have to touch down. For the HUD.</summary>
	public int AirJumpsRemaining => Math.Max( 0, AirJumps - airJumpsUsed );

	protected override void OnAwake()
	{
		rb = GameObject.GetComponent<Rigidbody>();
		playerMovement = GameObject.GetComponent<RBPlayerMovement>();
		stamina = GameObject.GetComponent<Stamina>();
	}

	protected override void OnStart()
	{
		cameraMovement ??= GameObject.Root.Components.Get<CameraMovement>( FindMode.EverythingInSelfAndDescendants );
	}

	protected override void OnUpdate()
	{
		if ( IsProxy )
			return;

		// Track when we were last in a special state. A special move (e.g. a wall jump) ends
		// its state in the SAME frame it consumes the jump press, so without this grace a
		// DoubleJump running after WallRunning that frame would see "normal air" and steal the
		// same press — firing a bogus straight-up jump on top of the wall jump.
		if ( playerMovement is not null && playerMovement.InSpecialMovement )
			lastSpecialTime = Time.Now;

		bool offSpecialGrace = Time.Now >= lastSpecialTime + SpecialMovementGrace;

		if ( playerMovement is not null )
		{
			// Touching down is the only thing that gives the charges back. Note this refills on
			// being grounded rather than on a ground JUMP, so walking off a ledge still leaves
			// you your full set — the forgiving reading, and the one that matches how a double
			// jump is expected to behave when you step off something.
			if ( playerMovement.isGrounded )
				airJumpsUsed = 0;

			// Optionally treat leaving a wall run / climb / grind as touching down too. Read on
			// the falling edge so the refill lands when the state ENDS, not while it's running.
			bool inSpecial = playerMovement.InSpecialMovement;
			if ( RefreshOnSpecialMovement && wasInSpecial && !inSpecial )
				airJumpsUsed = 0;
			wasInSpecial = inSpecial;
		}

		// Latch the press only in normal flight: grounded presses are the free ground jump
		// (RBPlayerMovement), special states own jumping themselves, and the grace above keeps
		// us from stealing the press on the frame a special state ends. Edge-triggered,
		// consumed in the physics step — which also dodges the jump-buffer ordering race.
		if ( Input.Pressed( "Jump" ) && playerMovement is not null
			&& !playerMovement.isGrounded && !playerMovement.InSpecialMovement && offSpecialGrace
			&& !playerMovement.ActionsBlocked && airJumpsUsed < AirJumps )
			jumpQueued = true;

		if ( fovTimer > 0f )
		{
			fovTimer -= Time.Delta;
			if ( JumpFov > 0f )
				cameraMovement?.RequestFov( JumpFov, JumpFovSpeed );
		}
	}

	protected override void OnFixedUpdate()
	{
		if ( IsProxy )
			return;

		if ( !jumpQueued )
			return;
		jumpQueued = false;

		if ( rb is null || playerMovement is null )
			return;

		// Re-check state: we may have landed or entered a special state since the press.
		if ( playerMovement.isGrounded || playerMovement.InSpecialMovement )
			return;

		// Out of charges until we touch down — re-checked here because we may have burned the
		// last one between the press and this physics step.
		if ( airJumpsUsed >= AirJumps )
			return;

		// Spend the wind; if a pool exists and it's empty, no jump (press is simply ignored).
		// Checked AFTER the charge so a jump you were never going to get doesn't cost anything.
		if ( stamina is not null && !stamina.TryConsume( StaminaCost ) )
			return;

		airJumpsUsed++;
		AirJump();
	}

	private void AirJump()
	{
		Vector3 horiz = rb.Velocity.WithZ( 0 );

		if ( RedirectToInput )
		{
			Vector3 input = Input.AnalogMove;
			Vector3 wish = playerMovement.Orientation.LocalRotation.Forward * input.x
				+ playerMovement.Orientation.LocalRotation.Right * -input.y;
			if ( wish.Length > 0.01f )
				horiz = wish.Normal * horiz.Length;
		}

		// Zero vertical for a consistent jump height, keep (or redirect) horizontal, then
		// apply the up impulse — same convention as RBPlayerMovement.Jump.
		rb.Velocity = horiz.WithZ( 0 );
		rb.ApplyImpulse( Vector3.Up * JumpForce * 1000f );

		// This press is ours — clear any buffered ground jump so it can't also fire on landing.
		playerMovement.ClearJumpBuffer();

		if ( JumpFov > 0f )
			fovTimer = JumpFovTime;

		if ( JumpSound is not null )
			Sound.Play( JumpSound, WorldPosition );
	}
}
