using Sandbox;
using System;
using System.Collections.Generic;

public sealed class GrappleGun : Component
{
	[Header("Grapple Settings")]
	[Property] public float MaxDistance { get; set; } = 2500f;
	// Sphere-cast radius for grapple aiming. Your crosshair can miss a grappleable surface by
	// up to this much and still lock on — the reticle snaps to the surface and you grapple it.
	[Property] public float AimAssistRadius { get; set; } = 35f;
	// How fast the reel pulls you straight to the grapple point. Set above run maxSpeed
	// so the grapple is the fast option.
	[Property] public float MaxReelSpeed { get; set; } = 1500f;
	// How quickly you accelerate up to the reel speed (lerp rate). Higher = snappier yank.
	[Property] public float ReelAccel { get; set; } = 8f;
	// In-air steering strength while grappling (u/s²). Lets movement input nudge your path
	// without breaking the pull-to-point feel.
	[Property] public float GrappleAirControl { get; set; } = 2000f;
	// How close to the anchor counts as "arrived" and ends the grapple (while reeling).
	[Property] public float StopDistance { get; set; } = 60f;
	// Owner-driven, replicated to everyone: proxies use this (plus hitPos) to draw this
	// player's rope on their own screen.
	[Property, Sync] public bool grappling { get; set; }

	[Header("Swing")]
	// How rigidly the rope holds its length: the fraction of any stretch corrected per
	// physics tick. 1 = perfectly rigid rod (tightest arc), lower = a touch of give.
	[Property, Range( 0.1f, 1f )] public float RopeStiffness { get; set; } = 0.9f;

	[Header( "References" )]
	[Property] public CameraMovement cameraMovement { get; set; }
	[Property] public GameObject RopeStart { get; set; }
	[Property] public GameObject RopeEnd { get; set; }

	[Header( "Sounds" )]
	// Fired the instant you shoot the hook (plays at the gun).
	[Property] public SoundEvent LaunchSound { get; set; }
	// Plays at the surface the moment the hook bites in.
	[Property] public SoundEvent ImpactSound { get; set; }
	// Looping whir while you're reeling in (hold Jump). Mark the sound asset as looping
	// so it sustains for the whole pull; it's started/stopped automatically.
	[Property] public SoundEvent ReelSound { get; set; }

	[Header("Rope Animation")]
	// More segments = smoother wave, slightly more cost. 16-32 is plenty.
	[Property] public int RopeSegments { get; set; } = 24;
	// How fast the hook tip flies to the target (u/s). The reel waits until it lands.
	[Property] public float RopeShootSpeed { get; set; } = 9000f;
	// Size of the whip-wave while the rope is flying out. Fades to 0 as it goes taut.
	[Property] public float WaveAmplitude { get; set; } = 30f;
	// How many full waves are visible along the rope.
	[Property] public float WaveCount { get; set; } = 2.5f;
	// How fast the waves travel along the rope.
	[Property] public float WaveSpeed { get; set; } = 18f;
	// Amplitude profile along the rope: keep 0 at both ends, peak in the middle, so the
	// rope stays pinned to the gun and the hook tip.
	[Property] public Curve animationCurve { get; set; }

	// Private stuff
	private RBPlayerMovement playerMovement;
	private Rigidbody rb;
	private LineRenderer Rope;
	// The grapple anchor point. Synced so every client can animate this player's rope
	// toward the same spot the owner actually hooked.
	[Sync] private Vector3 hitPos { get; set; }

	// Camera FOV push while grappling.
	[Property] public float GrappleFov { get; set; } = 110f;

	// 0 → 1 as the hook tip flies from the gun to the target.
	private float ropeProgress;
	// Public: while attached, the grapple owns the player's velocity — RBPlayerMovement
	// checks this and stops applying its own input so steering isn't doubled up.
	public bool HookAttached => ropeProgress >= 1f;

	// Where the rope meets the wall while grappling — the HUD pins the reticle box here.
	public Vector3 AnchorPoint => hitPos;

	// Current magnetically-resolved grapple point, for the crosshair reticle (and TryStart).
	// HasTarget is false when nothing grappleable is in range, or while already grappling.
	public bool HasTarget { get; private set; }
	public Vector3 TargetPoint { get; private set; }
	// True when the center ray hit directly (you're aiming right at a surface), so the reticle
	// should stay pinned to the crosshair. False when the point was snapped from a sphere-cast.
	public bool TargetCentered { get; private set; }

	// Pendulum length while swinging: locked to your distance when the hook lands,
	// shortened as you reel. The rope never lets you get farther than this.
	private float ropeLength;
	private bool ropeLengthSet;

	// Handle to the looping reel sound so it can be stopped when reeling ends.
	private SoundHandle reelHandle;
	// One-shot guard so the impact sound fires exactly once per grapple.
	private bool impactPlayed;

	private readonly List<GameObject> ropePoints = new();

	protected override void OnAwake()
	{
		playerMovement = GameObject.GetComponent<RBPlayerMovement>();
		rb = GameObject.GetComponent<Rigidbody>();
		// Must include DISABLED components: the rope is toggled off whenever this player
		// isn't grappling, and that enabled-state replicates — so on a machine that receives
		// this player mid-session (e.g. a client joining after the host spawned), the
		// LineRenderer arrives disabled and a default Get() would return null, leaving this
		// player's rope permanently invisible here.
		Rope = Components.Get<LineRenderer>( FindMode.EverythingInSelf );
	}

	protected override void OnStart()
	{
		cameraMovement ??= CameraMovement.Find( GameObject );
	}

	protected override void OnUpdate()
	{
		// Rope visuals run on EVERY client. Proxies receive `grappling` + `hitPos` over the
		// network and animate their own local copy of the rope from that state — so everyone
		// sees where each player is grappling, not just the player doing it.
		UpdateRopeVisuals();

		// Everything below is input/simulation — the owning player only.
		if ( IsProxy )
			return;

		// Dead or frozen: no new grapples, no reeling, no reticle. An already-attached
		// rope survives a strike-freeze (it resumes when released) — death cuts it in
		// BeginRespawnWait. Letting go of the button is still honored while blocked.
		if ( playerMovement is not null && playerMovement.ActionsBlocked )
		{
			HasTarget = false;
			if ( grappling && !Input.Down( "attack2" ) )
				grappling = false;
			UpdateReelSound( false );
			return;
		}

		// Resolve the magnetic grapple point each frame (drives the reticle + TryStart).
		UpdateTarget();

		if ( Input.Pressed( "attack2" ) )
			TryStart();

		// Re-requested per frame; eases back to base FOV on its own when we stop.
		if ( grappling && rb.Velocity.Length > 500f)
			cameraMovement?.RequestFov( GrappleFov );

		// Reel whir loops only while actively reeling (attached + holding Jump). Managed
		// here in OnUpdate so it always stops when the grapple ends, for any reason.
		bool reelingNow = grappling && HookAttached && Input.Down( "Jump" );
		UpdateReelSound( reelingNow );

		if ( Input.Released( "attack2" ) )
			grappling = false;
	}

	// Edge tracking for the rope visuals, per client (owner and proxies alike).
	private bool wasGrappling;

	// Drives everything cosmetic about the rope on this machine. Works from the synced
	// grappling/hitPos state only, so the same code path serves the owner and proxies.
	private void UpdateRopeVisuals()
	{
		// Rising edge: a grapple just started (locally via TryStart, or it just arrived over
		// the network on a proxy) — rewind the hook-flight animation and play the launch sound.
		if ( grappling && !wasGrappling )
		{
			ropeProgress = 0f;
			impactPlayed = false;

			if ( LaunchSound is not null )
				Sound.Play( LaunchSound, RopeStart?.WorldPosition ?? WorldPosition );
		}
		wasGrappling = grappling;

		if ( grappling)
			AnimateRope();

		// The rope is only visible while grappling.
		if ( Rope is not null )
			Rope.Enabled = grappling;
	}

	// Starts/stops the looping reel sound and keeps it on the player while it plays.
	private void UpdateReelSound( bool reeling )
	{
		if ( reeling )
		{
			if ( !reelHandle.IsValid() && ReelSound is not null )
				reelHandle = Sound.Play( ReelSound, WorldPosition );

			if ( reelHandle.IsValid() )
				reelHandle.Position = WorldPosition;
		}
		else if ( reelHandle.IsValid() )
		{
			reelHandle.Stop();
			reelHandle = default;
		}
	}

	protected override void OnFixedUpdate()
	{
		if ( IsProxy )
			return;

		// Frozen (countdown, dead, hooked by a grapple strike): the reel/swing must not
		// fight the pin, so the rope physics stands down entirely.
		if ( playerMovement is not null && playerMovement.IsFrozen )
			return;

		// Nothing happens until the hook actually reaches the surface — fire, the hook
		// flies, THEN the rope goes live. Sells the ODM-gear feel.
		if ( !grappling || !HookAttached || rb is null ) return;

		// The moment the hook lands, lock in the pendulum length at the current distance.
		// (Impact sound is fired in AnimateRope when the tip VISUALLY arrives, slightly
		// before this — the ease-out tail otherwise made the sound lag the visual.)
		if ( !ropeLengthSet )
		{
			ropeLength = Vector3.DistanceBetween( rb.WorldPosition, hitPos );
			ropeLengthSet = true;
		}

		// Hold Jump (space) to reel in; otherwise hang and swing on the rope.
		if ( Input.Down( "Jump" ) )
			ReelToAnchor();
		else
			Swing();
	}

	// Pendulum swing with a HARD rope constraint. The rope is taut from the moment the
	// hook lands (ropeLength = attach distance), so instead of free-falling and getting
	// caught near the bottom, you follow the circular arc immediately:
	//   1. any velocity stretching the rope is removed (tangential speed untouched), and
	//   2. any stretch that still happened is corrected THROUGH velocity this tick —
	//      collision-safe (physics still resolves walls/floor), but rigid like a rod.
	// Gravity then has only one option: accelerate you along the arc. That's the swing.
	void Swing()
	{
		// Pump the swing with movement input.
		ApplySteering();

		Vector3 toAnchor = hitPos - rb.WorldPosition;
		float distance = toAnchor.Length;
		Vector3 dir = toAnchor.Normal;

		// Ratchet: the rope reels in slack but never pays it back out. Any time you get
		// closer to the anchor (swing apex, jump, nudge forward), the new shorter distance
		// becomes the rope length — so the rope is ALWAYS taut and you're always arcing,
		// never falling through slack.
		ropeLength = MathF.Min( ropeLength, distance );

		if ( distance > ropeLength )
		{
			// Kill the stretching component of velocity (negative dot = moving away).
			float radialVel = Vector3.Dot( rb.Velocity, dir );
			if ( radialVel < 0f )
				rb.Velocity -= dir * radialVel;

			// Pull the existing stretch closed this tick via velocity, so the constraint
			// stays rigid instead of sagging — this is what makes the arc feel tight.
			float stretch = distance - ropeLength;
			rb.Velocity += dir * (stretch / Time.Delta) * RopeStiffness;
		}
	}

	void ReelToAnchor()
	{
		Vector3 toAnchor = hitPos - rb.WorldPosition;
		float distance = toAnchor.Length;
		Vector3 dir = toAnchor.Normal;

		// Reel straight toward the grapple point — lerp the whole velocity toward the anchor
		// so it pulls you right to it (the snappy, direct feel you wanted).
		Vector3 targetVel = dir * MaxReelSpeed;
		rb.Velocity = Vector3.Lerp( rb.Velocity, targetVel, Time.Delta * ReelAccel );

		// ...then let movement input nudge the path for some in-air control. This is applied
		// AFTER the reel so it isn't immediately overwritten — you can steer the pull.
		ApplySteering();

		// Reeling takes up rope: release space and you swing from the shorter length.
		ropeLength = MathF.Min( ropeLength, distance );

		// Arrived at the anchor — end the grapple.
		if ( distance < StopDistance )
			grappling = false;
	}

	// Movement input nudges your path while on the rope (shared by swing and reel).
	void ApplySteering()
	{
		Vector3 input = Input.AnalogMove;
		Vector3 wishDir = playerMovement.Orientation.WorldRotation.Forward * input.x
			+ playerMovement.Orientation.WorldRotation.Right * -input.y;
		if ( wishDir.Length > 0.01f )
			rb.Velocity += wishDir.Normal * GrappleAirControl * Time.Delta;
	}

	// Resolve the current grapple point each frame: a sphere-cast forward whose radius is the
	// aim assist, so the crosshair can miss a grappleable surface by up to AimAssistRadius and
	// still lock on. The reticle and the actual grapple both read this, so they never disagree.
	private void UpdateTarget()
	{
		HasTarget = false;
		TargetCentered = false;

		// No reticle while already grappling — the rope is showing instead.
		if ( grappling ) return;

		var cam = Scene.Camera;
		if ( cam is null ) return;

		var start = cam.WorldPosition;
		var end = start + cam.WorldRotation.Forward * MaxDistance;

		// First a precise center ray: if you're looking right at a surface, the grapple point
		// is exactly where you're aiming, so the reticle stays pinned to the crosshair.
		var center = Scene.Trace.Ray( start, end )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

		if ( center.Hit )
		{
			HasTarget = true;
			TargetCentered = true;
			TargetPoint = center.HitPosition;
			return;
		}

		// Center missed (aiming into the air past an edge) — sweep a fat sphere to snap onto
		// the nearest grappleable surface within the aim-assist radius.
		var snap = Scene.Trace.Ray( start, end )
			.Radius( AimAssistRadius )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

		if ( snap.Hit )
		{
			HasTarget = true;
			TargetPoint = snap.HitPosition;
		}
	}

	private void TryStart()
	{
		// Grapple the magnetically-resolved point (the same one the reticle shows), so the
		// indicator and the actual grapple always agree.
		if ( !HasTarget ) return;

		hitPos = TargetPoint;
		grappling = true;
		ropeProgress = 0f;      // hook not attached yet — physics reads this immediately
		ropeLengthSet = false;  // pendulum length locks in when the hook lands
		// The visual reset + launch sound happen in UpdateRopeVisuals on the grapple-start
		// edge, so owner and proxies share one code path.
	}

	// Animates the rope as a chain of points: the tip shoots out toward the target over
	// time, and a traveling wave whips along the rope, fading out as it goes taut.
	private void AnimateRope()
	{
		if ( Rope is null ) return;

		EnsureRopePoints();

		Vector3 gunPos = RopeStart.WorldPosition;
		float totalDistance = MathF.Max( Vector3.DistanceBetween( gunPos, hitPos ), 1f );

		// Advance the hook tip at a constant world speed so close targets attach fast
		// and far shots visibly travel.
		ropeProgress = MathF.Min( ropeProgress + Time.Delta * RopeShootSpeed / totalDistance, 1f );

		// Ease-out on the tip so it launches fast and decelerates into the surface.
		float eased = 1f - (1f - ropeProgress) * (1f - ropeProgress);
		Vector3 tipPos = Vector3.Lerp( gunPos, hitPos, eased );

		// Fire the impact the instant the tip has visually all-but-arrived (95%), instead
		// of waiting for ropeProgress to fully complete. The ease-out tail spends its last
		// stretch creeping the final few percent, so "looks landed" happens noticeably
		// before "progress == 1" — that gap was the perceived sound delay.
		if ( !impactPlayed && eased >= 0.95f )
		{
			impactPlayed = true;
			if ( ImpactSound is not null )
				Sound.Play( ImpactSound, hitPos );
			
		}

		// Wave basis perpendicular to the rope.
		Rotation ropeRot = Rotation.LookAt( tipPos - gunPos );
		Vector3 up = ropeRot.Up;
		Vector3 right = ropeRot.Right;

		// The whip fades as the rope reaches the target → taut straight line when attached.
		float amplitude = WaveAmplitude * (1f - eased);

		int count = ropePoints.Count;
		for ( int i = 0; i < count; i++ )
		{
			float delta = (float)i / (count - 1);
			Vector3 basePos = Vector3.Lerp( gunPos, tipPos, delta );

			// Traveling wave: phase moves along the rope over time; a smaller cosine on the
			// side axis gives it a lively corkscrew instead of a flat flag-wave.
			float phase = delta * WaveCount * MathF.PI * 2f - Time.Now * WaveSpeed;
			float profile = animationCurve.Evaluate( delta );
			Vector3 offset = up * MathF.Sin( phase ) * amplitude * profile
				+ right * MathF.Cos( phase ) * amplitude * 0.35f * profile;

			ropePoints[i].WorldPosition = basePos + offset;
		}

		// Keep RopeEnd on the tip in case anything else (hook model, sounds) follows it.
		// Owner only: RopeEnd is a networked child, so proxies receive its transform over
		// the network — moving it locally too would fight the incoming sync and jitter.
		if ( !IsProxy && RopeEnd is not null )
			RopeEnd.WorldPosition = tipPos;
	}

	// Builds the chain of invisible point objects the LineRenderer draws through.
	private void EnsureRopePoints()
	{
		int needed = Math.Max( RopeSegments, 2 );
		if ( ropePoints.Count == needed ) return;

		foreach ( var p in ropePoints )
			p?.Destroy();
		ropePoints.Clear();

		Rope.Points ??= new();
		Rope.Points.Clear();

		for ( int i = 0; i < needed; i++ )
		{
			var point = new GameObject( true, $"RopePoint_{i}" );
			ropePoints.Add( point );
			Rope.Points.Add( point );
		}
	}

	protected override void OnDestroy()
	{
		// Don't leave the reel whir hanging if we're torn down mid-pull.
		if ( reelHandle.IsValid() )
			reelHandle.Stop();

		foreach ( var p in ropePoints )
			p?.Destroy();
		ropePoints.Clear();
	}
}
