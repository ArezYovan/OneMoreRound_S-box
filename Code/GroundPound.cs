using Sandbox;
using System;

// Ground pound. Press Duck (Ctrl) while airborne to slam STRAIGHT DOWN to the ground fast.
// Costs a stamina bar (shared pool with Dash / DoubleJump). While pounding this component
// OWNS velocity — RBPlayerMovement and the other movement systems stand down via
// RBPlayerMovement.IsGroundPounding so nothing fights the slam — and it ends the instant you
// touch the ground (your camera's landing spring fires the impact dip for free off the high
// downward speed).
//
// Tip: hold Duck through the slam and you'll slide on landing (Sliding's buffered press), for
// a pound-into-slide combo. Tap Duck and you just pound.
public sealed class GroundPound : Component
{
	[Header( "Input" )]
	// "Duck" is Ctrl in the default input config.
	[Property] public string InputAction { get; set; } = "Duck";

	[Header( "Pound" )]
	// Downward slam speed (u/s). Big — this is a committed dive to the floor.
	[Property] public float PoundSpeed { get; set; } = 2200f;
	// Stamina bars spent per pound (shared pool). 0 = free.
	[Property] public float StaminaCost { get; set; } = 1f;
	// Kill horizontal velocity so the slam is dead straight down. Off = keep your horizontal
	// momentum and just drive the downward speed (a diagonal slam).
	[Property] public bool ZeroHorizontal { get; set; } = false;
	// Don't pound if there's less than this much clearance below you — avoids a pointless
	// tiny slam (and the wasted stamina) when you're basically already landing. 0 = always.
	[Property] public float MinAirHeight { get; set; } = 60f;

	[Header( "Land Slide" )]
	// Hold Duck through the slam to slide on landing. The straight-down slam zeroes your
	// horizontal speed, so this hands back the momentum you had BEFORE the pound as a slide.
	[Property] public bool SlideOnLand { get; set; } = true;
	// Fraction of your pre-pound horizontal speed carried into the slide. 1 = keep it all.
	[Property] public float MomentumKeep { get; set; } = 1f;
	// Flat speed ADDED on top of the kept momentum for the landing slide. 0 = pure maintain.
	[Property] public float SlideBoost { get; set; } = 100f;
	// When you pound onto a SLOPE, how much of the slam (plus kept momentum) is redirected
	// down the slope so you flow into it instead of hammering in and bouncing. 0..1.
	[Property] public float SlopeRedirect { get; set; } = 1f;

	[Header( "Feel (optional)" )]
	// FOV push while slamming. 0 = none. Uses the camera's per-request FOV speed.
	[Property] public float PoundFov { get; set; } = 120f;
	[Property] public float PoundFovSpeed { get; set; } = 25f;
	[Property] public CameraMovement cameraMovement { get; set; }
	[Property] public SoundEvent StartSound { get; set; }
	[Property] public SoundEvent ImpactSound { get; set; }
	// Spawned at your feet on impact (dust / shockwave), if set.
	[Property] public GameObject ImpactEffect { get; set; }

	public bool IsPounding => pounding;

	private bool pounding;
	private bool poundQueued;

	private Rigidbody rb;
	private RBPlayerMovement playerMovement;
	private Stamina stamina;
	private GrappleGun grapple;
	private Sliding slide;

	// Horizontal velocity captured the instant the pound starts, so it can be handed into a
	// landing slide (the slam itself zeroes horizontal speed).
	private Vector3 entryHorizVel;

	protected override void OnAwake()
	{
		rb = GameObject.GetComponent<Rigidbody>();
		playerMovement = GameObject.GetComponent<RBPlayerMovement>();
		stamina = GameObject.GetComponent<Stamina>();
		grapple = GameObject.GetComponent<GrappleGun>();
		slide = GameObject.GetComponent<Sliding>();
	}

	protected override void OnStart()
	{
		cameraMovement ??= GameObject.Root.Components.Get<CameraMovement>( FindMode.EverythingInSelfAndDescendants );
	}

	protected override void OnUpdate()
	{
		if ( IsProxy )
			return;

		// Latch the press only in normal flight (airborne, no special state already owning us).
		// Edge-triggered, consumed in the physics step.
		if ( Input.Pressed( InputAction ) && playerMovement is not null
			&& !pounding && !playerMovement.isGrounded && !playerMovement.InSpecialMovement
			&& !playerMovement.ActionsBlocked )
			poundQueued = true;

		if ( pounding && PoundFov > 0f )
			cameraMovement?.RequestFov( PoundFov, PoundFovSpeed );
	}

	protected override void OnFixedUpdate()
	{
		if ( IsProxy )
			return;

		if ( pounding )
		{
			PoundTick();
			return;
		}

		if ( poundQueued )
		{
			poundQueued = false;
			TryStart();
		}
	}

	private void TryStart()
	{
		if ( rb is null || playerMovement is null ) return;
		if ( playerMovement.isGrounded || playerMovement.InSpecialMovement ) return;

		// Too close to the ground to bother (and waste a bar)?
		if ( MinAirHeight > 0f && !EnoughAirRoom() ) return;

		// Spend a bar; if a pool exists and it's empty, no pound (and nothing was spent).
		if ( stamina is not null && !stamina.TryConsume( StaminaCost ) ) return;

		pounding = true;
		// Remember the momentum we're about to slam through, for the landing slide handoff.
		entryHorizVel = rb.Velocity.WithZ( 0 );

		if ( StartSound is not null )
			Sound.Play( StartSound, WorldPosition );
	}

	private void PoundTick()
	{
		// A grapple fired mid-pound takes over — yield velocity to it untouched.
		if ( (grapple is not null && grapple.grappling) )
		{
			pounding = false;
			return;
		}

		// Pounded into a rail — the grind grabbed us, so stop pounding and let it own velocity.
		if ( playerMovement.IsGrindingNow )
		{
			pounding = false;
			return;
		}

		// Done the instant we touch down.
		if ( playerMovement.isGrounded )
		{
			Impact();
			return;
		}

		// Drive straight down (optionally keeping horizontal momentum).
		Vector3 horiz = ZeroHorizontal ? Vector3.Zero : rb.Velocity.WithZ( 0 );
		rb.Velocity = horiz.WithZ( -PoundSpeed );
	}

	private void Impact()
	{
		pounding = false;

		if ( ImpactSound is not null )
			Sound.Play( ImpactSound, WorldPosition );

		ImpactEffect?.Clone( WorldPosition );

		bool duckHeld = SlideOnLand && slide is not null && Input.Down( InputAction );

		if ( playerMovement.OnSlopeNow )
		{
			// Redirect the slam (plus any kept momentum) ALONG the slope so we flow down it
			// instead of hammering straight in and bouncing back up. VectorPlaneProject strips
			// the "into the surface" component, leaving pure down-slope velocity to bounce off.
			Vector3 normal = playerMovement.SlopeNormal;
			Vector3 raw = entryHorizVel + Vector3.Down * PoundSpeed;
			Vector3 slopeVel = Vector3.VectorPlaneProject( raw, normal ) * SlopeRedirect;

			if ( duckHeld )
				slide.BeginSlideWith( slopeVel );   // flow straight into a downhill slide
			else
				rb.Velocity = slopeVel;             // at least redirect, so it never bounces
			return;
		}

		// Flat ground: hand horizontal momentum into a slide if you're still holding Duck,
		// otherwise just kill the downward slam so it lands cleanly (no slam-bounce).
		if ( duckHeld )
		{
			float entrySpeed = entryHorizVel.Length;
			Vector3 dir = entrySpeed > 50f ? entryHorizVel.Normal : ForwardFlat();
			float speed = entrySpeed * MomentumKeep + SlideBoost;
			if ( speed > 100f && dir.Length > 0.01f )
			{
				slide.BeginSlideWith( dir * speed );
				return;
			}
		}

		rb.Velocity = rb.Velocity.WithZ( 0f );
	}

	// Flattened facing direction, for when you pounded with no horizontal momentum to keep.
	private Vector3 ForwardFlat()
	{
		var o = playerMovement?.Orientation;
		Vector3 fwd = o is not null ? o.WorldRotation.Forward.WithZ( 0 ) : rb.Velocity.WithZ( 0 );
		return fwd.Length > 0.01f ? fwd.Normal : Vector3.Forward;
	}

	// True if there's at least MinAirHeight of clearance straight below us.
	private bool EnoughAirRoom()
	{
		return !Scene.Trace
			.Ray( WorldPosition, WorldPosition + Vector3.Down * MinAirHeight )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run()
			.Hit;
	}

	protected override void OnDisabled()
	{
		pounding = false;
		poundQueued = false;
	}
}
