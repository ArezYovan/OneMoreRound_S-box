using System;
using System.Collections.Generic;
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
///
/// Small things go in your hand instead, when the body has one. The prop travels from where it
/// lay into the palm over the grab animation, rides the hand bone while you hold it, and leaves
/// partway through the throw animation rather than the instant you press. It's placed with its
/// <see cref="Grip"/> over your palm, so props of any size sit right without a hold of their own.
/// Anything too big for a fist — a barstool, a staggered man — floats the old way.
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

	[Property, Group( "Throw" ), Description( "Multiplies the damage whatever you throw does. Set by drinks (Throw Damage); 1 is standing." )]
	public float ThrowDamageScale { get; set; } = 1f;

	[Property, Group( "Throw" ), Description( "How many more men a throw flies on to after it hits one. Set by drinks (Ricochet); 0 is standing, and tick it up here to try it without buying it." )]
	public int RicochetBounces { get; set; } = 0;

	[Property, Group( "Throw" ), Description( "How much of your own speed the throw carries. 1 is what physics would do; at a sprint that's most of a second throw's worth of speed on top, which mostly reads as the prop being hard to aim. Half keeps the sense that running throws hit harder without the forward one going through the wall." )]
	public float InheritThrowVelocity { get; set; } = 0.5f;

	[Property, Group( "Throw" ), Description( "Spin (deg/s) put on the prop as it leaves, purely so a thrown barstool tumbles instead of gliding. Doesn't affect damage — ThrownObject scores off closing speed along the contact normal." )]
	public float ThrowSpin { get; set; } = 0f;

	[Property, Group( "Hand" ), Description( "The body whose animgraph plays the grab, hold and throw, and whose hand the prop rides in. Found on the player automatically if left empty." )]
	public SkinnedModelRenderer Body { get; set; }

	[Property, Group( "Hand" ), Description( "The bone the prop rides on. On a Rigify rig the right hand is DEF_hand_R. If the body hasn't got this bone, everything is carried the old floating way — so a scene without the new rig still plays." )]
	public string HandBone { get; set; } = "DEF_hand_R";

	[Property, Group( "Hand" ), Description( "Where your palm is, measured from the hand bone along its own axes. A bone sits at the wrist joint, so at zero every prop is held by your wrist. Tick Show Palm, select the player and nudge this until the marker sits in the middle of the palm." )]
	public Vector3 PalmOffset { get; set; }

	[Property, Group( "Hand" ), Description( "Turns the palm marker. Get its blue axis pointing out of the top of the fist — where a bottle's neck would stick out — and every prop modelled standing upright sits upright in your hand. A prop that needs something different gets a Grip." )]
	public Angles PalmAngles { get; set; }

	[Property, Group( "Hand" ), Description( "Anything bigger than this across is carried the old way, floating in front of you, instead of in your hand. A barstool doesn't fit in a fist, and a staggered man certainly doesn't. 0 puts everything in your hand." )]
	public float HandHoldMaxSize { get; set; } = 40f;

	[Property, Group( "Hand" ), Description( "How long a prop takes to travel from where it lay into your hand. Match it to the moment the grab animation closes its fingers." )]
	public float GrabTime { get; set; } = 0.25f;

	[Property, Group( "Hand" ), Description( "How far into the throw animation the prop actually leaves your hand. Match it to the frame your arm is furthest forward — too early and it flies out of a hand still drawn back." )]
	public float ThrowReleaseTime { get; set; } = 0.2f;

	[Property, Group( "Hand" ), Description( "How far in front of your eyes a prop must be when it leaves your hand, so it can't catch on your own body on the way out. A hand closer than this has the prop nudged forward along your aim, with the prop's own size added on top." )]
	public float ReleaseClearance { get; set; } = 40f;

	[Property, Group( "Hand" ), Description( "The animgraph bool. True plays the grab and settles into the hold; false plays the throw." )]
	public string HoldParameter { get; set; } = "IsHolding";

	[Property, Group( "Debug" )] public bool LogGrabs { get; set; } = true;

	[Property, Group( "Debug" ), Description( "Draws your palm and which way it faces, with the player selected. For tuning Palm Offset and Palm Angles — turn it off again after." )]
	public bool ShowPalm { get; set; }

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

	// In-hand carrying. None means nothing is held, or it's floating the old way.
	enum HandState { None, Grabbing, Holding, Throwing }

	HandState hand;
	TimeSince timeInHand;
	// where the prop lay when you took it, so it can travel into the hand instead of teleporting there
	Transform pickedUpFrom;
	// the point and angle on the prop that the palm closes on, in the prop's own space
	Transform grip;
	float heldRadius;
	// switched off while it rides in your hand, and exactly these switched back on after
	readonly List<Collider> handColliders = new();
	SkinnedModelRenderer bodyFound;

	bool InHand => hand != HandState.None;

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
		// the pose follows the hands: up for the grab and the hold, down — which plays the throw — the moment a
		// throw starts or the prop leaves you any other way. Set before the gates below, so a knockout drops it too
		var body = BodyRenderer;

		if ( body.IsValid() && !string.IsNullOrEmpty( HoldParameter ) )
			body.Set( HoldParameter, IsHolding && hand != HandState.Throwing );

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
		{
			// destroyed in your hand: nothing left to let go of, just forget it was there
			hand = HandState.None;
			handColliders.Clear();
			return;
		}

		// died in your hands: the rigidbody gets switched off for the ragdoll, so there's nothing
		// left to carry. Grab the corpse again if you still want it
		if ( !held.Enabled )
		{
			Release( "it went limp" );
			return;
		}

		// in the hand, OnPreRender places it on the bone every frame. All that's left here is the clock: finishing
		// the reach, and letting go at the right moment of the throw
		if ( InHand )
		{
			if ( hand == HandState.Grabbing && timeInHand >= GrabTime )
				hand = HandState.Holding;

			if ( hand == HandState.Throwing && timeInHand >= ThrowReleaseTime )
				Launch();

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
		var target = FindGrabTarget( out var body, out var stagger, out string refusal );

		if ( target is null )
		{
			Report( refusal );
			return;
		}

		Attach( body, stagger );
	}

	/// <summary>
	/// What a press of Use would pick up right now, or null — for the HUD's prompt. The same search the grab itself
	/// runs, so the prompt can never offer something the press won't take. Refreshed a few times a second rather than
	/// every frame: the cone half of the search looks at every loose thing in the room.
	/// </summary>
	public GameObject GrabTarget
	{
		get
		{
			if ( timeSinceTargetChecked >= TargetCheckInterval )
			{
				timeSinceTargetChecked = 0;
				grabTarget = CanReach && !IsHolding ? FindGrabTarget( out _, out _, out _ ) : null;
			}

			return grabTarget.IsValid() ? grabTarget : null;
		}
	}

	/// <summary>True while something is held and a press of Use would throw it — not once the throw is already on its way.</summary>
	public bool CanThrow => IsHolding && hand != HandState.Throwing && CanReach;

	// whether a press would be acted on at all: not down, and not held by the run (the countdown, the bar)
	bool CanReach => !( PlayerHealth.IsValid() && PlayerHealth.IsKnockedOut )
		&& !( Movement.IsValid() && Movement.ActionsBlocked );

	GameObject grabTarget;
	TimeSince timeSinceTargetChecked;
	const float TargetCheckInterval = 0.1f;

	/// <summary>
	/// The thing under the crosshair if it can be lifted; if the crosshair is on nothing at all, the best thing in the
	/// aim cone. Null with the reason in <paramref name="refusal"/> otherwise.
	/// </summary>
	GameObject FindGrabTarget( out Rigidbody body, out Stagger stagger, out string refusal )
	{
		body = null;
		stagger = null;

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
			refusal = "grabbed at nothing";
			return null;
		}

		return CanGrab( target, out body, out stagger, out refusal ) ? target : null;
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

		// someone else is carrying it: its physics is off while it rides in their hand. Knock it out of his
		// hand and it's fair game — until then, hitting him is the answer, not reaching for his bottle
		if ( !body.MotionEnabled )
		{
			refusal = $"{target.Name} is in someone's hand";
			return false;
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

		bool intoHand = TakeIntoHand( body );

		Report( $"picked up {body.GameObject.Name} ({( body.PhysicsBody?.Mass ?? 0f ):0} mass){( intoHand ? " — in hand" : "" )}" );
	}

	// ---------- Throwing & letting go ----------

	void Throw()
	{
		// in the hand a throw is an animation first: the pose drops, the arm comes through, and the prop only leaves
		// at ThrowReleaseTime. It's aimed at that moment rather than now, so you can still line it up
		if ( InHand )
		{
			if ( hand != HandState.Throwing )
			{
				hand = HandState.Throwing;
				timeInHand = 0;
			}

			return;
		}

		Launch();
	}

	void Launch()
	{
		var body = held;
		var direction = ( EyeForward + Vector3.Up * ThrowLift ).Normal;

		Detach();

		// armed before it's launched, so a throw at point blank still counts
		var thrown = body.GameObject.Components.GetOrCreate<ThrownObject>();
		thrown.Arm( GameObject );
		thrown.DamageScale = ThrowDamageScale;
		thrown.Bounces = RicochetBounces;

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
		var body = held;
		var name = held.IsValid() ? held.GameObject.Name : "nothing";
		bool wasInHand = InHand;

		Detach();

		// out of a hand it has no speed of its own — give it yours, so a drop at a run falls where you'd expect
		// instead of stopping dead in mid air
		if ( wasInHand && body.IsValid() )
			body.Velocity = PlayerVelocity;

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
		{
			held.EnhancedCcd = heldPreviousCcd;

			if ( InHand )
				LetGoOfHand( held );
		}

		hand = HandState.None;
		handColliders.Clear();
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

	// ---------- In the hand ----------

	/// <summary>
	/// Puts a prop in your hand if it's small enough and the body has a hand to put it in. Physics and collision go
	/// off for the carry, the same as when an enemy carries one, so it can't shove you about or catch on the crowd.
	/// Returns false, and changes nothing, for anything that should float the old way instead.
	/// </summary>
	bool TakeIntoHand( Rigidbody body )
	{
		if ( !TryGetPalm( out _ ) )
			return false;

		// people never go in a fist, whatever size they are
		if ( body.GameObject.Components.Get<Enemy>( FindMode.EnabledInSelfAndDescendants ).IsValid() )
			return false;

		var bounds = body.GameObject.GetBounds();
		float size = MathF.Max( bounds.Size.x, MathF.Max( bounds.Size.y, bounds.Size.z ) );

		if ( HandHoldMaxSize > 0f && size > HandHoldMaxSize )
			return false;

		hand = HandState.Grabbing;
		timeInHand = 0;
		pickedUpFrom = body.WorldTransform;
		grip = GripFor( body, bounds );
		heldRadius = size * 0.5f;

		body.Velocity = Vector3.Zero;
		body.AngularVelocity = Vector3.Zero;
		body.MotionEnabled = false;

		handColliders.Clear();
		handColliders.AddRange( body.GameObject.Components.GetAll<Collider>( FindMode.EnabledInSelfAndDescendants ) );

		foreach ( var collider in handColliders )
			collider.Enabled = false;

		return true;
	}

	/// <summary>Physics and collision back on, clear of your own body. Every way out of the hand comes through here.</summary>
	void LetGoOfHand( Rigidbody body )
	{
		// moved BEFORE collision comes back, so it never exists for a tick inside you
		body.WorldPosition = ClearOfBody( body.WorldPosition );

		foreach ( var collider in handColliders )
		{
			if ( collider.IsValid() )
				collider.Enabled = true;
		}

		handColliders.Clear();
		body.MotionEnabled = true;
	}

	/// <summary>
	/// Placed as the frame renders, once the animation has posed the hand for this frame — any earlier and the prop
	/// trails a frame behind the hand, which shows on a fast throw.
	/// </summary>
	protected override void OnPreRender()
	{
		if ( !InHand || !held.IsValid() || !TryGetPalm( out var palm ) )
			return;

		var target = HandTarget( palm );

		// travels from where it lay into the hand over the reach, eased at both ends, so it meets the hand rather
		// than teleporting into it
		if ( hand == HandState.Grabbing && GrabTime > 0f )
		{
			float t = MathX.Clamp( (float)timeInHand / GrabTime, 0f, 1f );
			t = t * t * ( 3f - 2f * t );

			held.WorldPosition = Vector3.Lerp( pickedUpFrom.Position, target.Position, t );
			held.WorldRotation = Rotation.Slerp( pickedUpFrom.Rotation, target.Rotation, t );
			return;
		}

		held.WorldPosition = target.Position;
		held.WorldRotation = target.Rotation;
	}

	/// <summary>Where the prop sits when its grip is in your palm: the grip's frame laid exactly over the palm's.</summary>
	Transform HandTarget( Transform palm )
	{
		var rotation = palm.Rotation * grip.Rotation.Inverse;
		var position = palm.Position - rotation * ( grip.Position * held.WorldScale );

		return new Transform( position, rotation );
	}

	/// <summary>Your palm this frame, as the animation has posed it. False without a body, or without the bone.</summary>
	bool TryGetPalm( out Transform palm )
	{
		palm = default;

		var body = BodyRenderer;

		if ( !body.IsValid() || string.IsNullOrEmpty( HandBone ) || !body.TryGetBoneTransform( HandBone, out var bone ) )
			return false;

		palm = new Transform( bone.Position + bone.Rotation * PalmOffset, bone.Rotation * PalmAngles.ToRotation() );
		return true;
	}

	/// <summary>
	/// Where on the prop the palm closes, in the prop's own space. A Grip on it says exactly; without one it's held by
	/// the middle, standing the way it was modelled — which is right for anything modelled upright.
	/// </summary>
	static Transform GripFor( Rigidbody body, BBox bounds )
	{
		var authored = body.GameObject.Components.Get<Grip>( FindMode.EnabledInSelfAndDescendants );

		if ( authored.IsValid() )
		{
			return new Transform(
				body.WorldTransform.PointToLocal( authored.WorldPosition ),
				body.WorldRotation.Inverse * authored.WorldRotation );
		}

		return new Transform( body.WorldTransform.PointToLocal( bounds.Center ), Rotation.Identity );
	}

	/// <summary>
	/// Nudges a position forward along your aim until it's clear of your own body, so a prop leaving your hand can't
	/// catch on you and be knocked off line. Anything already far enough out is left exactly where it is.
	/// </summary>
	Vector3 ClearOfBody( Vector3 position )
	{
		var forward = EyeForward.WithZ( 0f );

		if ( forward.IsNearlyZero() )
			return position;

		forward = forward.Normal;

		float needed = ReleaseClearance + heldRadius;
		float along = Vector3.Dot( position - EyePosition, forward );

		return along < needed ? position + forward * ( needed - along ) : position;
	}

	/// <summary>
	/// The body, found lazily and cached privately rather than written back to Body — the editor gizmo asks for it
	/// before OnStart has ever run, and writing a property from there would dirty the scene.
	/// </summary>
	SkinnedModelRenderer BodyRenderer
	{
		get
		{
			if ( Body.IsValid() )
				return Body;

			if ( !bodyFound.IsValid() )
				bodyFound = Components.Get<SkinnedModelRenderer>( FindMode.EverythingInSelfAndDescendants );

			return bodyFound;
		}
	}

	protected override void DrawGizmos()
	{
		if ( !ShowPalm || !Gizmo.IsSelected || !TryGetPalm( out var palm ) )
			return;

		// gizmos draw in this object's space; the palm is in the world's
		var at = WorldTransform.PointToLocal( palm.Position );
		var turn = WorldRotation.Inverse * palm.Rotation;

		Gizmo.Draw.Color = Color.Orange;
		Gizmo.Draw.LineSphere( at, 3f, 8 );

		Gizmo.Draw.Color = Color.Red;
		Gizmo.Draw.Line( at, at + turn.Forward * 10f );

		Gizmo.Draw.Color = Color.Green;
		Gizmo.Draw.Line( at, at + turn.Left * 10f );

		Gizmo.Draw.Color = Color.Blue;
		Gizmo.Draw.Line( at, at + turn.Up * 10f );
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
