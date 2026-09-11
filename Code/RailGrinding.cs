using Sandbox;
using System;

// Rail grinding. When you're airborne and moving along a nearby GrindRail you AUTO-SNAP
// onto it and ride. Speed is pure momentum on the flat, accelerates downhill and bleeds
// uphill (gravity projected along the rail), and you only ever leave by reaching the END of
// the rail (launched off carrying your speed) or pressing JUMP (hop off, keeping speed + an
// upward pop). There is no time limit — the rail decides when it's over.
//
// Like WallRunning, while grinding this component OWNS the player's velocity and gravity.
// Everything else (RBPlayerMovement's Move/SpeedControl/gravity, Sliding, WallRunning,
// WallClimb, Dash) checks RBPlayerMovement.IsGrindingNow and stands down, so
// nothing fights the rail. Grapple and GrappleStrike are the exception — they're deliberate
// inputs, so the rail YIELDS to them instead (see GrindTick).
public sealed class RailGrinding : Component
{
	[Header( "Attach" )]
	// How close to a rail you must be to snap on.
	[Property] public float SnapRadius { get; set; } = 150f;
	// Minimum horizontal speed before a grind will start (don't snap while crawling).
	[Property] public float MinAttachSpeed { get; set; } = 500f;
	// How aligned your motion must be with the rail to grab it (1 = dead-on, 0.5 ≈ 60°).
	// Stops you snapping onto a rail you're crossing perpendicular at speed.
	[Property, Range( 0f, 1f )] public float MinAlignDot { get; set; } = 0.2122905f;
	// Only grab rails while airborne (the "jump onto the rail" feel). Off = grab from ground too.
	[Property] public bool RequireAirborne { get; set; } = false;
	// Lockout after leaving a rail before you can snap again — stops an instant re-grab when
	// you jump off. Mirrors WallClimb's re-grab delay.
	[Property] public float ReGrabDelay { get; set; } = 0.35f;
	// How far above the rail line your body rides (tune to your capsule height).
	[Property] public float RideHeight { get; set; } = 35f;

	[Header( "Speed" )]
	// You're boosted to (at least) this speed the instant you get on a rail, and you can
	// never drop below it while grinding. Get on slower than this and the rail brings you up.
	[Property] public float MinGrindSpeed { get; set; } = 1200f;
	// Scales gravity-along-the-rail. 1 = realistic. Downhill speeds you up; uphill is ignored
	// (you never lose speed on a rail), so this only ever ADDS.
	[Property] public float SlopeAccel { get; set; } = 1f;
	// Safety clamp on grind speed so a long downhill can't run away.
	[Property] public float MaxGrindSpeed { get; set; } = 3000f;
	// Lateral spring that glues you onto the rail line — higher = snappier snap-on, less sway.
	[Property] public float SnapStrength { get; set; } = 18f;

	[Header( "Jump Off" )]
	// Upward speed added when you hop off with Jump (your grind speed is kept on top).
	[Property] public float GrindJumpUpForce { get; set; } = 600f;
	// Momentum-carry window opened on dismount so a fast launch isn't braked by the ground
	// soft cap (same grace a fast landing or dash gets).
	[Property] public float DismountKeepMomentum { get; set; } = 0.3f;

	[Header( "Camera" )]
	[Property] public float GrindFov { get; set; } = 120f;
	[Property] public CameraMovement camMovement { get; set; }

	[Header( "FX (optional)" )]
	// Looping grind/sparks sound (mark the asset as looping). Started/stopped automatically.
	[Property] public SoundEvent GrindSound { get; set; }
	// Optional effect (sparks) cloned + parented to you while grinding, removed on dismount.
	[Property] public GameObject GrindEffect { get; set; }

	public bool IsGrinding => grinding;

	private bool grinding;
	private GrindRail currentRail;
	private float distanceAlong;
	private float railVel;        // signed speed along the rail (+ = toward the far end)
	private float reAttachReadyAt;
	private bool jumpQueued;      // Jump latched on the render frame, consumed in fixed step

	private Rigidbody rb;
	private RBPlayerMovement playerMovement;
	private GrappleGun grapple;
	private Sliding sliding;

	private SoundHandle grindHandle;
	private GameObject grindEffectInstance;

	protected override void OnAwake()
	{
		rb = GameObject.GetComponent<Rigidbody>();
		playerMovement = GameObject.GetComponent<RBPlayerMovement>();
		grapple = GameObject.GetComponent<GrappleGun>();
		sliding = GameObject.GetComponent<Sliding>();
	}

	protected override void OnStart()
	{
		camMovement ??= GameObject.Root.Components.Get<CameraMovement>( FindMode.EverythingInSelfAndDescendants );
	}

	protected override void OnUpdate()
	{
		if ( IsProxy )
			return;

		if ( grinding )
		{
			// Re-requested per frame; the camera eases FOV back when we stop asking.
			camMovement?.RequestFov( GrindFov );

			// Edge-triggered jump is only reliable on the render frame, so latch it for the
			// physics step — which is also where the grind ends, avoiding any ordering race
			// with RBPlayerMovement's own jump handling. (While grinding, RBPlayerMovement
			// won't buffer this press at all, so it can't double-fire a ground jump.)
			if ( Input.Pressed( "Jump" ) )
				jumpQueued = true;
		}

		UpdateGrindSound( grinding );
	}

	protected override void OnFixedUpdate()
	{
		if ( IsProxy )
			return;

		if ( grinding )
			GrindTick();
		else
			TryAttach();
	}

	// ---------- Attach ----------

	private void TryAttach()
	{
		if ( rb is null || playerMovement is null ) return;
		if ( Time.Now < reAttachReadyAt ) return;
		if ( RequireAirborne && playerMovement.isGrounded ) return;

		// Deliberate, velocity-owning systems take priority — don't fight them.
		if ( grapple is not null && grapple.grappling ) return;
		if ( sliding is not null && sliding.sliding ) return;

		// A ground pound comes straight down with no horizontal speed/alignment, but should
		// still snap onto a rail it drops through — so relax the speed/alignment gates while
		// pounding.
		bool pounding = playerMovement.IsGroundPounding;

		Vector3 horiz = rb.Velocity.WithZ( 0 );
		if ( !pounding && horiz.Length < MinAttachSpeed ) return;
		Vector3 horizDir = horiz.Length > 0.01f ? horiz.Normal : Vector3.Zero;

		GrindRail bestRail = null;
		float bestPerp = SnapRadius;
		float bestDist = 0f;
		Vector3 bestTangent = Vector3.Forward;

		foreach ( var rail in Scene.GetAllComponents<GrindRail>() )
		{
			if ( rail is null || !rail.IsUsable ) continue;
			if ( !rail.FindClosest( rb.WorldPosition, out float dAlong, out Vector3 pt, out Vector3 tan, out float perp ) )
				continue;
			if ( perp > bestPerp ) continue;

			// Must be travelling roughly along the rail (either direction), compared on the
			// horizontal plane. Skipped while ground pounding — you're dropping straight onto it.
			if ( !pounding )
			{
				Vector3 tanFlat = tan.WithZ( 0 );
				if ( tanFlat.Length > 0.01f && horizDir.Length > 0.01f
					&& MathF.Abs( Vector3.Dot( horizDir, tanFlat.Normal ) ) < MinAlignDot )
					continue;
			}

			bestPerp = perp;
			bestRail = rail;
			bestDist = dAlong;
			bestTangent = tan;
		}

		if ( bestRail is null ) return;

		// Direction along the rail from your along-rail speed. If that's tiny (dropped straight
		// on, e.g. a ground pound), pick the sign from where you're facing instead. StartGrind
		// boosts the magnitude up to MinGrindSpeed regardless, so the grind always launches.
		float along = Vector3.Dot( rb.Velocity, bestTangent );
		if ( MathF.Abs( along ) < 50f )
		{
			Vector3 look = playerMovement.Orientation is not null
				? playerMovement.Orientation.WorldRotation.Forward.WithZ( 0 )
				: horiz;
			if ( look.Length > 0.01f )
				along = Vector3.Dot( look.Normal, bestTangent );
		}

		StartGrind( bestRail, bestDist, along );
	}

	private void StartGrind( GrindRail rail, float distAlong, float entrySpeed )
	{
		grinding = true;
		currentRail = rail;
		distanceAlong = distAlong;

		// Boost onto the rail: at least MinGrindSpeed, capped at MaxGrindSpeed, keeping the
		// direction of travel along the rail (default forward if we got on near-stationary).
		float dir = entrySpeed >= 0f ? 1f : -1f;
		railVel = dir * MathX.Clamp( MathF.Abs( entrySpeed ), MinGrindSpeed, MaxGrindSpeed );

		// The rail fully owns vertical motion while we're on it.
		rb.Gravity = false;

		// Kill inherited vertical (e.g. a ground pound's downward slam) so we snap onto the
		// rail cleanly instead of carrying the drop for a tick.
		rb.Velocity = rb.Velocity.WithZ( 0f );

		SpawnEffect();
	}

	// ---------- Grind ----------

	private void GrindTick()
	{
		// A grapple fired mid-grind takes over — yield velocity to it untouched.
		if ( (grapple is not null && grapple.grappling) )
		{
			Dismount( jump: false, launch: false );
			return;
		}

		if ( currentRail is null || !currentRail.IsUsable )
		{
			Dismount( jump: false, launch: false );
			return;
		}

		if ( jumpQueued )
		{
			jumpQueued = false;
			Dismount( jump: true, launch: true );
			return;
		}

		Vector3 tangent = currentRail.GetTangent( distanceAlong );

		// Gravity projected along the rail, applied ONLY when it speeds you up (downhill).
		// Uphill is ignored, so you never lose speed on a rail. Then clamp the magnitude into
		// [MinGrindSpeed, MaxGrindSpeed] (keeping travel direction) so you stay above the floor
		// and a long downhill can't run away.
		float gravAlong = Vector3.Dot( Scene.PhysicsWorld.Gravity, tangent ) * SlopeAccel;
		float dir = railVel >= 0f ? 1f : -1f;
		if ( gravAlong * dir > 0f )
			railVel += gravAlong * Time.Delta;
		railVel = dir * MathX.Clamp( MathF.Abs( railVel ), MinGrindSpeed, MaxGrindSpeed );

		distanceAlong += railVel * Time.Delta;

		// Ran off either end — launch off carrying the speed (the satisfying payoff).
		float railLength = currentRail.Length;
		if ( distanceAlong >= railLength || distanceAlong <= 0f )
		{
			distanceAlong = MathX.Clamp( distanceAlong, 0f, railLength );
			Dismount( jump: false, launch: true );
			return;
		}

		// Drive position THROUGH velocity (collision-safe, exactly like the grapple rope):
		// full speed along the rail, plus a lateral spring that pulls us onto the line so the
		// snap-on eases in instead of teleporting.
		Vector3 target = currentRail.GetPoint( distanceAlong ) + Vector3.Up * RideHeight;
		Vector3 offset = target - rb.WorldPosition;
		Vector3 perp = offset - Vector3.Dot( offset, tangent ) * tangent;
		rb.Velocity = tangent * railVel + perp * SnapStrength;
	}

	// ---------- Dismount ----------

	private void Dismount( bool jump, bool launch )
	{
		if ( !grinding ) return;

		// Capture the exit direction before we drop the rail.
		Vector3 tangent = currentRail is not null ? currentRail.GetTangent( distanceAlong ) : Vector3.Forward;

		grinding = false;
		// Always restore gravity (GravityScale too, in case a prior state left it scaled).
		rb.Gravity = true;
		rb.GravityScale = 1f;
		reAttachReadyAt = Time.Now + ReGrabDelay;

		if ( launch )
		{
			Vector3 v = tangent * railVel;
			if ( jump )
				v += Vector3.Up * GrindJumpUpForce;
			rb.Velocity = v;

			// Let a fast launch carry on the ground instead of being braked by the soft cap.
			playerMovement?.KeepMomentum( DismountKeepMomentum );
		}
		// On a yield (grapple/strike takes over) we leave velocity alone for a clean handoff.

		DestroyEffect();
		currentRail = null;
		jumpQueued = false;
	}

	// ---------- FX ----------

	private void UpdateGrindSound( bool on )
	{
		if ( on )
		{
			if ( !grindHandle.IsValid() && GrindSound is not null )
				grindHandle = Sound.Play( GrindSound, WorldPosition );
			if ( grindHandle.IsValid() )
				grindHandle.Position = WorldPosition;
		}
		else if ( grindHandle.IsValid() )
		{
			grindHandle.Stop();
			grindHandle = default;
		}
	}

	private void SpawnEffect()
	{
		if ( GrindEffect is null || grindEffectInstance is not null ) return;
		grindEffectInstance = GrindEffect.Clone( WorldPosition );
		grindEffectInstance.SetParent( GameObject );
	}

	private void DestroyEffect()
	{
		grindEffectInstance?.Destroy();
		grindEffectInstance = null;
	}

	protected override void OnDisabled()
	{
		// Never leave the player stuck with gravity off (or a sound looping) if we're
		// disabled / torn down mid-grind.
		if ( grinding )
		{
			grinding = false;
			if ( rb is not null )
			{
				rb.Gravity = true;
				rb.GravityScale = 1f;
			}
		}
		jumpQueued = false;
		UpdateGrindSound( false );
		DestroyEffect();
	}

	protected override void OnDestroy()
	{
		UpdateGrindSound( false );
		DestroyEffect();
	}
}
