using Sandbox;
using System;

// A ground/air dash (press "Run" / Shift by default). Covers a set DISTANCE in a set TIME in the
// direction you're inputting — or straight ahead when you're not steering — then hands you back
// to normal movement at a set exit speed.
//
// Distance-based rather than a speed boost: a boost's reach depends on whatever friction, caps and
// input do to it afterwards, so the same press could carry you twice as far on a different frame.
// Here the dash owns velocity for its whole duration (RBPlayerMovement stands down via IsDashing),
// so it always covers the same ground unless a wall stops it — which is what makes it a dodge you
// can plan around.
//
// Jumping or sliding cancels the dash on the spot, keeping its exit speed, so it still chains into
// the rest of the movement.
public sealed class DashAbility : Component
{
	[Header( "Input" )]
	// "Run" is Shift in the default s&box input config.
	[Property] public string InputAction { get; set; } = "Run";

	[Header( "Dash" )]
	// How far one dash takes you (units), wall permitting.
	[Property] public float DashDistance { get; set; } = 250f;
	// How long covering that distance takes (seconds). Distance / duration is the average speed.
	[Property] public float DashDuration { get; set; } = 0.2f;
	// 0 = constant speed the whole way. 1 = starts at twice the average and eases to a stop, so the
	// dash bursts out and settles — the heavier-feeling option. Same distance either way.
	[Property, Range( 0f, 1f )] public float Snappiness { get; set; } = 0.7f;
	// Horizontal speed you leave the dash with (u/s). Never slower than what you came in with, so
	// dashing out of a grapple or a slide doesn't throw your momentum away.
	[Property] public float ExitSpeed { get; set; } = 300f;
	// Hold height for the dash so it's a purely horizontal move. Off = gravity acts as normal.
	[Property] public bool ZeroVerticalDash { get; set; } = true;
	// Optional minimum time between dashes, measured from the start of one to the start of the next.
	[Property] public float Cooldown { get; set; } = 0.2f;
	// Stamina bars spent per dash (shared pool with the rest of the kit). 0 = free.
	[Property] public float StaminaCost { get; set; } = 1f;
	// If the exit speed is above run speed, how long it's protected from ground friction so it can
	// be carried into a jump or slide.
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

	// --- Public state for the HUD and RBPlayerMovement ---
	public bool IsReady => cooldownTimer <= 0f && !IsDashing;
	// 1 right after use → 0 when ready again (drives a HUD cooldown drain).
	public float CooldownFraction => Cooldown > 0f ? MathX.Clamp( cooldownTimer / Cooldown, 0f, 1f ) : 0f;
	// True while the dash owns the player's velocity.
	public bool IsDashing { get; private set; }

	private float cooldownTimer;
	private float fovTimer;
	private float dashTime;
	private float entrySpeed;
	private Vector3 dashDir;
	private RBPlayerMovement playerMovement;
	private Sliding sliding;
	private Stamina stamina;
	private Rigidbody rb;

	protected override void OnAwake()
	{
		rb = GameObject.GetComponent<Rigidbody>();
		playerMovement = GameObject.GetComponent<RBPlayerMovement>();
		sliding = GameObject.GetComponent<Sliding>();
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
		if ( IsReady && Input.Pressed( InputAction ) && (playerMovement is null || !playerMovement.ActionsBlocked) )
		{
			// Spend a stamina bar to dash; if a pool exists and it's empty, no dash.
			if ( stamina is null || stamina.TryConsume( StaminaCost ) )
				StartDash();
		}

		// Jump cancels here, on the render frame, so the dash has let go before the physics step
		// that consumes the buffered jump — otherwise the dash would flatten the jump's velocity.
		if ( IsDashing && Input.Pressed( "Jump" ) )
			EndDash();

		// Re-request the FOV punch each frame it's active, then it eases back once we stop.
		if ( fovTimer > 0f )
		{
			fovTimer -= Time.Delta;
			cameraMovement?.RequestFov( DashFov, DashFovSpeed );
		}
	}

	protected override void OnFixedUpdate()
	{
		if ( IsProxy || !IsDashing )
			return;

		// Anything else that takes over the player ends the dash rather than fighting it. InSpecialMovement
		// is deliberately NOT asked here any more: with the other movement states gone it reports the dash
		// itself, so this would cancel every dash on its own first tick.
		if ( playerMovement is not null && playerMovement.ActionsBlocked || sliding is not null && sliding.sliding )
		{
			EndDash();
			return;
		}

		float duration = MathF.Max( DashDuration, 0.01f );
		float t = MathX.Clamp( dashTime / duration, 0f, 1f );

		// Speed profile that always integrates to DashDistance over DashDuration: a flat average
		// blended with a linear ease-out (2 × average at the start, 0 at the end).
		float average = DashDistance / duration;
		float speed = average * ((1f - Snappiness) + Snappiness * 2f * (1f - t));

		Vector3 vel = dashDir * speed;
		rb.Velocity = ZeroVerticalDash ? vel : vel.WithZ( rb.Velocity.z );

		dashTime += Time.Delta;
		if ( dashTime >= duration )
			EndDash();
	}

	private void StartDash()
	{
		if ( rb is null ) return;

		Vector3 dir = GetDashDirection();
		if ( dir.Length < 0.01f ) return;

		dashDir = dir;
		dashTime = 0f;
		entrySpeed = rb.Velocity.WithZ( 0f ).Length;
		IsDashing = true;
		cooldownTimer = Cooldown;
		fovTimer = DashFovTime;

		if ( ZeroVerticalDash )
			rb.Velocity = rb.Velocity.WithZ( 0f );

		if ( DashSound is not null )
			Sfx.Play( DashSound, WorldPosition );
	}

	private void EndDash()
	{
		if ( !IsDashing ) return;
		IsDashing = false;

		// Leave along the dash direction at the exit speed — or faster if that's what you came in
		// with. Sliding sets its own velocity on entry, so it's left alone there.
		if ( sliding is not null && sliding.sliding )
			return;

		float exit = MathF.Max( ExitSpeed, entrySpeed );
		rb.Velocity = (dashDir * exit).WithZ( rb.Velocity.z );

		// Above run speed the ground would brake this instantly; give it the carry window.
		if ( playerMovement is not null && exit > playerMovement.maxSpeed )
			playerMovement.KeepMomentum( MomentumKeepTime );
	}

	protected override void OnDisabled()
	{
		IsDashing = false;
	}

	// Horizontal dash direction from movement input, built exactly like RBPlayerMovement's
	// move direction so the dash goes where your run would. Falls back to straight ahead
	// (where you're facing) when you're not steering, so a standing dash still fires.
	private Vector3 GetDashDirection()
	{
		var orientation = playerMovement?.Orientation;
		if ( orientation is null ) return Vector3.Zero;

		Vector3 input = Input.AnalogMove;
		Vector3 dir = orientation.WorldRotation.Forward * input.x + orientation.WorldRotation.Right * -input.y;
		dir = dir.WithZ( 0 );

		if ( dir.Length < 0.01f )
			dir = orientation.WorldRotation.Forward.WithZ( 0 );

		return dir.Normal;
	}
}
