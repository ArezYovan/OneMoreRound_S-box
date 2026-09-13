using Sandbox;
using System;

// A ground/air dash (press "Run" / Shift by default). Fires a burst of speed in the
// direction you're inputting — or straight ahead when you're not steering — and lets you
// KEEP that speed afterwards instead of being braked back to walking pace. Runs on a simple
// cooldown so it can't be spammed.
//
// Momentum: the dash is a pure impulse — it stacks on whatever velocity you already had
// rather than overwriting it, so dashing mid-run really launches you. A plain GROUND dash
// would normally be eaten instantly by ground friction and the soft speed cap, so we open
// RBPlayerMovement's momentum window (KeepMomentum) for a moment — the same grace period a
// fast landing gets, your window to bunny-hop / slide / wall-run and carry the dash onward.
// In the air momentum is preserved for free.
//
// (This reuses the "Run"/Shift key that TimeSlowAbility used to own.)
public sealed class DashAbility : Component
{
	[Header( "Input" )]
	// "Run" is Shift in the default s&box input config.
	[Property] public string InputAction { get; set; } = "Run";

	[Header( "Dash" )]
	// Speed added along the dash direction (u/s), on top of the speed you carry in.
	[Property] public float DashSpeed { get; set; } = 500f;
	// A dash never leaves you slower than this (u/s), so a standing or slow dash still
	// gives a real burst instead of feeling weak. Set at or above run speed.
	[Property] public float MinDashSpeed { get; set; } = 1000f;
	// Zero vertical velocity on dash so it's a purely horizontal blink (cancels any rise/fall).
	// Off = keep your vertical momentum and just add the horizontal burst.
	[Property] public bool ZeroVerticalDash { get; set; } = true;
	// Optional minimum time between dashes. Stamina is the real limiter now, so this defaults
	// to 0 (dash as fast as you have bars for). Raise it if you want a hard rate cap on top.
	[Property] public float Cooldown { get; set; } = 0.2f;
	// Stamina bars spent per dash (shared pool with DoubleJump). 0 = free.
	[Property] public float StaminaCost { get; set; } = 1f;
	// How long the burst is protected from ground friction / the soft speed cap after it
	// fires (the momentum carry window). Irrelevant in the air — momentum is kept regardless.
	// Keep it short; it's a window to convert the dash into a slide/bhop, not free flight.
	[Property] public float MomentumKeepTime { get; set; } = 0.1f;

	[Header( "Feel (optional)" )]
	// A quick FOV punch on dash that eases back on its own (same pattern as the other
	// abilities). Held for DashFovTime so the lerp has time to read.
	[Property] public float DashFov { get; set; } = 120f;
	[Property] public float DashFovTime { get; set; } = 0.2f;
	// How fast the dash FOV punch ramps in (lerp rate). Higher = snappier. This is a
	// per-request override of the camera's global FOVspeed (8), so the dash can punch
	// much faster than the smooth slide/wallrun/grapple FOV changes.
	[Property] public float DashFovSpeed { get; set; } = 50f;
	[Property] public CameraMovement cameraMovement { get; set; }
	[Property] public SoundEvent DashSound { get; set; }

	// --- Public state for the HUD (mirrors GrappleStrike) ---
	public bool IsReady => cooldownTimer <= 0f;
	// 1 right after use → 0 when ready again (drives a HUD cooldown drain).
	public float CooldownFraction => Cooldown > 0f ? MathX.Clamp( cooldownTimer / Cooldown, 0f, 1f ) : 0f;

	private float cooldownTimer;
	private float fovTimer;
	private RBPlayerMovement playerMovement;
	private Stamina stamina;
	private Rigidbody rb;

	protected override void OnAwake()
	{
		rb = GameObject.GetComponent<Rigidbody>();
		playerMovement = GameObject.GetComponent<RBPlayerMovement>();
		stamina = GameObject.GetComponent<Stamina>();
	}

	protected override void OnStart()
	{
		cameraMovement ??= CameraMovement.Find( GameObject );
	}

	protected override void OnUpdate()
	{
		if ( IsProxy )
			return;

		if ( cooldownTimer > 0f )
			cooldownTimer -= Time.Delta;

		// Edge-triggered (one dash per press), read on the render frame like the other inputs.
		if ( IsReady && Input.Pressed( InputAction ) && (playerMovement is null || (!playerMovement.IsGrindingNow && !playerMovement.IsGroundPounding && !playerMovement.ActionsBlocked)) )
		{
			// Spend a stamina bar to dash; if a pool exists and it's empty, no dash.
			if ( stamina is null || stamina.TryConsume( StaminaCost ) )
				Dash();
		}

		// Re-request the FOV punch each frame it's active, then it eases back once we stop.
		if ( fovTimer > 0f )
		{
			fovTimer -= Time.Delta;
			cameraMovement?.RequestFov( DashFov, DashFovSpeed );
		}
	}

	private void Dash()
	{
		if ( rb is null ) return;

		Vector3 dashDir = GetDashDirection();
		if ( dashDir.Length < 0.01f ) return;

		cooldownTimer = Cooldown;

		// Redirect + boost: ALL current horizontal speed is carried into the dash direction,
		// plus a flat DashSpeed on top, floored at MinDashSpeed. This makes every dash worth
		// the same — a standing dash still launches you (the floor), a running dash gains
		// exactly DashSpeed, and dashing sideways at speed actually TURNS your momentum.
		// (The old vector-add barely changed the path when dashing across your motion, and a
		// standing dash came out slower than plain running — the source of the "weak and
		// inconsistent" feel.) The absoluteMaxSpeed hard cap still clamps the result.
		float newSpeed = MathF.Max( rb.Velocity.WithZ( 0f ).Length + DashSpeed, MinDashSpeed );
		Vector3 newVel = dashDir * newSpeed;
		newVel = ZeroVerticalDash ? newVel.WithZ( 0f ) : newVel.WithZ( rb.Velocity.z );
		rb.Velocity = newVel;

		// Protect the burst from ground friction / the soft cap so it actually carries.
		playerMovement?.KeepMomentum( MomentumKeepTime );

		fovTimer = DashFovTime;

		if ( DashSound is not null )
			Sound.Play( DashSound, WorldPosition );
	}

	// Horizontal dash direction from movement input, built exactly like RBPlayerMovement's
	// move direction so the dash goes where your run would. Falls back to straight ahead
	// (where you're facing) when you're not steering, so a standing dash still fires.
	private Vector3 GetDashDirection()
	{
		var orientation = playerMovement?.Orientation;
		if ( orientation is null ) return Vector3.Zero;

		Vector3 input = Input.AnalogMove;
		Vector3 dir = orientation.LocalRotation.Forward * input.x + orientation.LocalRotation.Right * -input.y;
		dir = dir.WithZ( 0 );

		if ( dir.Length < 0.01f )
			dir = orientation.LocalRotation.Forward.WithZ( 0 );

		return dir.Normal;
	}
}
