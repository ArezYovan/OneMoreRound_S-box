using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Citizen;

/// <summary>
/// The Regular's other move: when the director sends him, a reserve walks to a loose prop, picks it up, carries it
/// until he's in range with a clear view of you, plants his feet and winds up — long enough for the warning on your
/// screen to get you turned round — then throws it where you're going to be.
///
/// Every part of it can be answered. Hit him while he's carrying or winding up and it falls out of his hands. Catch
/// it (PlayerGrab already takes things out of the air) and it's yours to throw back. And the aim is locked the
/// moment it leaves his hand, so however hard he leads you, changing direction once it's in the air dodges it.
/// </summary>
public sealed partial class Enemy
{
	[Property, Group( "Throwing" ), Description( "Whether this man ever goes for something to throw. Off, he only ever uses his fists." )]
	public bool CanThrow { get; set; } = true;

	[Property, Group( "Throwing" ), Range( 0f, 1f ), Description( "The difficulty dial for throws: how far ahead of you he aims. 0 throws at where you stand when it leaves his hand; 1 throws at where you'll be if you keep moving exactly as you are. Either way, changing direction once it's in the air dodges it." )]
	public float ThrowPrediction { get; set; } = 0.6f;

	[Property, Group( "Throwing" ), Description( "Nearest he throws from. Closer than this he backs off first — any nearer and the windup is a free hit for you." )]
	public float ThrowRangeMin { get; set; } = 450f;

	[Property, Group( "Throwing" ), Description( "Farthest he throws from. Farther than this he walks in until he's in range." )]
	public float ThrowRangeMax { get; set; } = 900f;

	[Property, Group( "Throwing" ), Description( "Shortest windup, in seconds. This is the warning — how long the indicator gives you to turn round. Each throw rolls somewhere between this and ThrowWindupMax, so the timing can't be learnt by rote." )]
	public float ThrowWindupMin { get; set; } = 1.5f;

	[Property, Group( "Throwing" ), Description( "Longest windup, in seconds." )]
	public float ThrowWindupMax { get; set; } = 2f;

	[Property, Group( "Throwing" ), Description( "How long a throw from ThrowRangeMin takes to reach you. With FlightTimeFar this decides how fast things fly — and so how much time there is to dodge once it's in the air." )]
	public float FlightTimeNear { get; set; } = 0.6f;

	[Property, Group( "Throwing" ), Description( "How long a throw from ThrowRangeMax takes to reach you." )]
	public float FlightTimeFar { get; set; } = 0.9f;

	[Property, Group( "Throwing" ), Description( "Most damage one of his throws can do, whatever it is. Your own throws aren't affected." )]
	public float ThrowDamageCap { get; set; } = 20f;

	[Property, Group( "Throwing" ), Description( "How far above your FEET he aims — your root is on the floor, not at your chest. Same trap as PlayerCombat's HomingAimHeight: it has to track the rig, and the citizen's 40 lands a bottle somewhere around your shins on the current one. Torso height is about 85." )]
	public float ThrowAimHeight { get; set; } = 85f;

	[Property, Group( "Throwing" ), Description( "Dead time after letting go before he goes back to the crowd." )]
	public float ThrowRecoverTime { get; set; } = 0.5f;

	[Property, Group( "Throwing" ), Description( "Least time between one man's throws, so the throws come from around the room rather than from whoever threw last." )]
	public float ThrowCooldown { get; set; } = 12f;

	[Property, Group( "Throwing Props" ), Description( "How far from where he stands he'll go for something to throw." )]
	public float PropSearchRadius { get; set; } = 900f;

	[Property, Group( "Throwing Props" ), Description( "Props closer to you than this are left alone, so nobody wanders into the middle of the fight to pick up a bottle." )]
	public float PropMinPlayerDistance { get; set; } = 400f;

	[Property, Group( "Throwing Props" ), Description( "Heaviest thing he'll pick up." )]
	public float MaxPropMass { get; set; } = 60f;

	[Property, Group( "Throwing Props" ), Description( "How long the pick-up takes. The prop is in his hand halfway through." )]
	public float GrabTime { get; set; } = 0.7f;

	[Property, Group( "Throwing Props" ), Description( "Give up on a prop he hasn't reached in this long — it's stuck behind something." )]
	public float FetchTimeout { get; set; } = 8f;

	[Property, Group( "Throwing Props" ), Description( "The bone the prop sits in while he carries it." )]
	public string HandBone { get; set; } = "hand_R";

	[Property, Group( "Throwing Props" ), Description( "How high off the floor the throw leaves from." )]
	public float ReleaseHeight { get; set; } = 60f;

	[Property, Group( "Debug" ), Description( "Throwing practice. He stays out of the ring, never swings, and goes straight for the next prop after every throw — ignoring the throw cooldowns, though still one windup at a time. Stand 450-900 away and he'll keep throwing at you. Can be flipped mid-play." )]
	public bool PracticeThrower { get; set; }

	[Property, Group( "Debug" ), Description( "For practice: a prop (a prefab, or a disabled bottle in the scene) he makes a fresh copy of beside him whenever he has nothing to throw — so practice never runs out. Leave empty to have him use what's lying around." )]
	public GameObject PracticeProp { get; set; }

	[Property, Group( "Sound" ), Description( "As he bends down for whatever he's picking up." )]
	public SoundEvent GrabSound { get; set; }

	[Property, Group( "Sound" ), Description( "As the prop leaves his hand." )]
	public SoundEvent ThrowSound { get; set; }

	[Property, Group( "Throwing Animation" ), Description( "Only used on a model without the citizen's animation helper. Trigger fired as the pick-up starts." )]
	public string GrabParameter { get; set; } = "b_grab";

	[Property, Group( "Throwing Animation" ), Description( "Custom rig only. Bool, true while he's carrying something." )]
	public string CarryParameter { get; set; } = "b_carrying";

	[Property, Group( "Throwing Animation" ), Description( "Custom rig only. Bool, true through the windup." )]
	public string ThrowWindupParameter { get; set; } = "b_throw_windup";

	[Property, Group( "Throwing Animation" ), Description( "Custom rig only. Trigger for the throw itself, fired ThrowAnimLead before the prop leaves his hand." )]
	public string ThrowParameter { get; set; } = "b_throw";

	[Property, Group( "Throwing Animation" ), Description( "How long before the release the throw animation starts, so the arm is coming through as the prop leaves. On the citizen this is the swing." )]
	public float ThrowAnimLead { get; set; } = 0.15f;

	/// <summary>Somewhere in the throw, from setting off for a prop to the follow-through.</summary>
	public bool IsThrowing => IsThrowState( State );

	/// <summary>
	/// Someone the director may send for a prop: a reserve with nothing to do, or a circler waiting for his turn —
	/// after a moment settled, and not straight after his last throw. A practice thrower is always keen.
	/// </summary>
	public bool WantsThrowJob
	{
		get
		{
			if ( !CanThrow || ForceIdle || !Target.IsValid() )
				return false;

			if ( PracticeThrower )
				return State == EnemyState.Lurking && timeInState > 0.5f;

			return State is EnemyState.Lurking or EnemyState.Circling && timeInState > 1f && timeSinceThrew > ThrowCooldown;
		}
	}

	static bool IsThrowState( EnemyState state ) =>
		state is EnemyState.Fetching or EnemyState.Grabbing or EnemyState.Carrying or EnemyState.ThrowWindup or EnemyState.Throwing;

	// the prop he's after or carrying, and what was switched off to carry it
	Rigidbody prop;
	bool holding;
	readonly List<Collider> propColliders = new();
	Rotation holdRotation;
	Vector3 holdCenter;
	float propRadius;
	Vector3 claimedAt;

	float windupDuration;
	bool throwAnimFired;
	TimeSince timeSinceThrew;

	// practice: the copy waiting beside him, and the thrown ones still lying about, cleared up after a while
	GameObject practiceSpare;
	readonly Dictionary<GameObject, float> practiceThrown = new();
	const float PracticeCleanupTime = 10f;
	ThrowThreat threat;
	Rigidbody targetBody;

	/// <summary>Called from Lurk each tick: take a throw job if the director has one for him.</summary>
	bool TryStartThrow()
	{
		if ( !WantsThrowJob || director is null || !director.RequestThrowJob( this, out var picked ) )
			return false;

		prop = picked;
		claimedAt = picked.WorldPosition;
		Enter( EnemyState.Fetching );

		if ( LogState )
			Log.Info( $"[Enemy] {GameObject.Name} goes for {picked.GameObject.Name}" );

		return true;
	}

	/// <summary>The throw states' share of OnFixedUpdate.</summary>
	void UpdateThrow( Vector3 toTarget, float distance )
	{
		switch ( State )
		{
			case EnemyState.Fetching:
			{
				if ( !PropStillThere() )
				{
					AbandonThrow( "lost what he was going for" );
					return;
				}

				var to = ( prop.WorldPosition - WorldPosition ).WithZ( 0 );
				float reach = BodyRadius + 30f;

				FaceTarget( Body.Velocity.WithZ( 0 ).Length > 50f ? Body.Velocity.WithZ( 0 ) : to );
				Navigate( prop.WorldPosition, reach * 0.8f, 1f );

				if ( to.Length <= reach )
					BeginGrab();
				else if ( timeInState > FetchTimeout )
				{
					// left alone for a while, or the next man is sent straight back for the same stuck prop
					director.MarkUnreachable( prop );
					AbandonThrow( "couldn't reach it" );
				}
				break;
			}

			case EnemyState.Grabbing:
				Brake();

				if ( !holding )
				{
					if ( !PropStillThere() )
					{
						AbandonThrow( "it was gone before he got a hand on it" );
						return;
					}

					FaceTarget( ( prop.WorldPosition - WorldPosition ).WithZ( 0 ) );

					if ( timeInState >= GrabTime * 0.5f )
						AttachProp();
				}

				if ( timeInState >= GrabTime )
					Enter( EnemyState.Carrying );
				break;

			// walks until he's in range, backs off if he's too close, and waits there for his turn to wind up
			case EnemyState.Carrying:
			{
				if ( !prop.IsValid() )
				{
					AbandonThrow( "the prop broke" );
					return;
				}

				bool inRange = distance >= ThrowRangeMin && distance <= ThrowRangeMax;

				FaceTarget( inRange || Body.Velocity.WithZ( 0 ).Length < 50f ? toTarget : Body.Velocity.WithZ( 0 ) );
				Navigate( ThrowSpot( distance ), SpotTolerance, inRange ? StalkSpeedScale : 1f );

				if ( inRange && HasThrowSight() && director.RequestThrowWindup( this ) )
					BeginThrowWindup();
				break;
			}

			// committed: feet planted, eyes on you, and the warning is on your screen
			case EnemyState.ThrowWindup:
				Brake();
				FaceTarget( toTarget );

				if ( !throwAnimFired && timeInState >= windupDuration - ThrowAnimLead )
					FireThrowAnimation();

				if ( timeInState >= windupDuration )
				{
					ReleaseThrow();
					Enter( EnemyState.Throwing );
				}
				break;

			case EnemyState.Throwing:
				Brake();

				if ( timeInState >= ThrowRecoverTime )
					EnterWaiting();
				break;
		}
	}

	void BeginGrab()
	{
		Enter( EnemyState.Grabbing );
		PlaySound( GrabSound, WorldPosition );

		if ( !Animator.IsValid() && !string.IsNullOrEmpty( GrabParameter ) )
			Renderer?.Set( GrabParameter, true );
	}

	void BeginThrowWindup()
	{
		Enter( EnemyState.ThrowWindup );

		windupDuration = Game.Random.Float( MathF.Min( ThrowWindupMin, ThrowWindupMax ), MathF.Max( ThrowWindupMin, ThrowWindupMax ) );
		throwAnimFired = false;
		threat = director.AddThreat( this, prop, windupDuration );

		if ( LogState )
			Log.Info( $"[Enemy] {GameObject.Name} winds up a throw ({windupDuration:0.00}s)" );
	}

	void FireThrowAnimation()
	{
		throwAnimFired = true;
		PlaySound( ThrowSound, WorldPosition );

		// the citizen's overhand swing is the throw; a custom rig gets a trigger of its own
		if ( Animator.IsValid() )
			Renderer?.Set( "b_attack", true );
		else if ( !string.IsNullOrEmpty( ThrowParameter ) )
			Renderer?.Set( ThrowParameter, true );
	}

	/// <summary>
	/// Lets go. The prop leaves from just in front of him rather than from his hand, so it can't clip his own body
	/// on the way out and be knocked off line, and it's aimed to arrive where you'll be after its flight.
	/// </summary>
	void ReleaseThrow()
	{
		var body = prop;

		if ( !body.IsValid() )
			return;

		var forward = ( Target.WorldPosition - WorldPosition ).WithZ( 0 );
		forward = forward.IsNearlyZero() ? WorldRotation.Forward : forward.Normal;

		var origin = WorldPosition + Vector3.Up * ReleaseHeight + forward * ( BodyRadius + propRadius + 6f );
		var velocity = AimThrow( body, origin, out float flight );

		LetGo();
		director.Unclaim( body );
		director.EndThrowWindup( this );

		// armed before it's launched, the same order the player's throw uses, so a close one still counts
		var thrown = body.GameObject.Components.GetOrCreate<ThrownObject>();
		thrown.Arm( GameObject, thrown.ArmedTime, ThrowDamageCap );

		body.WorldPosition = origin;
		body.Velocity = velocity;
		body.AngularVelocity = Vector3.Random.Normal * 8f;

		threat?.Release( flight );
		threat = null;
		prop = null;
		timeSinceThrew = 0;

		// a practice copy gets tidied away once it's done its job
		if ( body.GameObject == practiceSpare )
		{
			practiceThrown[practiceSpare] = Time.Now + PracticeCleanupTime;
			practiceSpare = null;
		}

		if ( LogState )
			Log.Info( $"[Enemy] {GameObject.Name} throws {body.GameObject.Name} at {velocity.Length:0} u/s, {flight:0.00}s to land" );
	}

	/// <summary>
	/// The launch velocity that puts the prop on the aim point after its flight time, under the same gravity it'll
	/// fly in. The aim point is where you are now, pushed along your current velocity by ThrowPrediction of the
	/// flight — worked out twice, because leading you changes the distance and the distance changes the flight.
	/// </summary>
	Vector3 AimThrow( Rigidbody body, Vector3 origin, out float flight )
	{
		targetBody ??= Target.Components.Get<Rigidbody>( FindMode.EverythingInSelf );

		// only your running is led: aiming off a jump would put the throw somewhere you'll never be
		var velocity = targetBody.IsValid() ? targetBody.Velocity.WithZ( 0 ) : Vector3.Zero;
		var aim = Target.WorldPosition + Vector3.Up * ThrowAimHeight;

		flight = FlightTimeFor( FlatDistance( aim, origin ) );
		var predicted = aim + velocity * flight * ThrowPrediction;

		flight = FlightTimeFor( FlatDistance( predicted, origin ) );
		predicted = aim + velocity * flight * ThrowPrediction;

		var gravity = body.Gravity ? Scene.PhysicsWorld.Gravity * body.GravityScale : Vector3.Zero;

		// where it starts, plus velocity times time, plus half gravity times time squared, lands on the aim point
		return ( predicted - origin ) / flight - gravity * ( 0.5f * flight );
	}

	/// <summary>Flight time by distance: FlightTimeNear at the near edge of his range, FlightTimeFar at the far one.</summary>
	float FlightTimeFor( float distance )
	{
		float span = MathF.Max( ThrowRangeMax - ThrowRangeMin, 1f );
		float t = ( ( distance - ThrowRangeMin ) / span ).Clamp( 0f, 1.5f );

		return MathF.Max( MathX.Lerp( FlightTimeNear, FlightTimeFar, t, false ), 0.2f );
	}

	/// <summary>Where to stand to throw: on the line between you and him, inside his range with a little room either side.</summary>
	Vector3 ThrowSpot( float distance )
	{
		var away = ( WorldPosition - Target.WorldPosition ).WithZ( 0 );
		away = away.IsNearlyZero() ? Vector3.Forward : away.Normal;

		float margin = MathF.Min( 75f, ( ThrowRangeMax - ThrowRangeMin ) * 0.25f );
		float want = distance.Clamp( ThrowRangeMin + margin, ThrowRangeMax - margin );

		return Target.WorldPosition + away * want;
	}

	/// <summary>Whether the throw has a clear line to you. Anything in the way — a wall, a pillar, one of his friends — and he keeps moving.</summary>
	bool HasThrowSight()
	{
		var from = WorldPosition + Vector3.Up * ReleaseHeight;
		var to = Target.WorldPosition + Vector3.Up * ThrowAimHeight;

		var trace = Scene.Trace.Ray( from, to ).IgnoreGameObjectHierarchy( GameObject );

		if ( prop.IsValid() )
			trace = trace.IgnoreGameObjectHierarchy( prop.GameObject );

		var result = trace.Run();

		return !result.Hit || IsPartOf( result.GameObject, Target );
	}

	/// <summary>Still where he last saw it, still loose, and nobody else has it — the player snatching it first is a fine answer to a man walking over for it.</summary>
	bool PropStillThere()
	{
		if ( !prop.IsValid() || !prop.Enabled || !prop.MotionEnabled || director.IsHeldByPlayer( prop ) )
			return false;

		return FlatDistance( prop.WorldPosition, claimedAt ) < 120f;
	}

	/// <summary>
	/// In his hand: physics and collision off while he carries it, so it can't shove him around, catch on the crowd,
	/// or be grabbed out of his hand. It's placed on his hand bone every frame from OnPreRender.
	/// </summary>
	void AttachProp()
	{
		holding = true;

		prop.Velocity = Vector3.Zero;
		prop.AngularVelocity = Vector3.Zero;
		prop.MotionEnabled = false;

		propColliders.Clear();
		propColliders.AddRange( prop.GameObject.Components.GetAll<Collider>( FindMode.EnabledInSelfAndDescendants ) );

		foreach ( var collider in propColliders )
			collider.Enabled = false;

		// a prop that was still live from someone else's throw stops being one the moment it's picked up
		prop.GameObject.Components.Get<ThrownObject>()?.Disarm();

		// held by its middle, at whatever angle it was lying at
		var bounds = prop.GameObject.GetBounds();
		holdCenter = prop.WorldTransform.PointToLocal( bounds.Center );
		propRadius = MathF.Max( bounds.Size.x, MathF.Max( bounds.Size.y, bounds.Size.z ) ) * 0.5f;
		holdRotation = HandTransform().Rotation.Inverse * prop.WorldRotation;

		if ( LogState )
			Log.Info( $"[Enemy] {GameObject.Name} picks up {prop.GameObject.Name}" );
	}

	/// <summary>Physics and collision back on. Everything that takes the prop out of his hand goes through here.</summary>
	void LetGo()
	{
		if ( !holding )
			return;

		holding = false;

		foreach ( var collider in propColliders )
		{
			if ( collider.IsValid() )
				collider.Enabled = true;
		}

		propColliders.Clear();

		if ( prop.IsValid() )
			prop.MotionEnabled = true;
	}

	protected override void OnPreRender()
	{
		HoldPropInHand();
	}

	// Placed as the frame renders, when his hand has been posed for this frame — earlier, it trails a frame behind.
	void HoldPropInHand()
	{
		if ( !holding || !prop.IsValid() )
			return;

		var hand = HandTransform();

		prop.WorldRotation = hand.Rotation * holdRotation;
		prop.WorldPosition += hand.Position - prop.WorldTransform.PointToWorld( holdCenter );
	}

	/// <summary>His hand as the animation has it this frame, or a spot in front of his chest on a model without that bone.</summary>
	Transform HandTransform()
	{
		if ( Renderer.IsValid() && !string.IsNullOrEmpty( HandBone ) && Renderer.TryGetBoneTransform( HandBone, out var hand ) )
			return hand;

		return new Transform( WorldPosition + Vector3.Up * ReleaseHeight + WorldRotation.Forward * ( BodyRadius + 10f ), WorldRotation );
	}

	/// <summary>
	/// Practice only: keeps a fresh copy of PracticeProp beside him while he's waiting, and clears away thrown copies
	/// once they've lain around a while, so a long practice doesn't bury the floor in bottles.
	/// </summary>
	void SupplyPracticeProp()
	{
		foreach ( var done in practiceThrown.Where( p => !p.Key.IsValid() || Time.Now >= p.Value ).Select( p => p.Key ).ToList() )
		{
			practiceThrown.Remove( done );

			if ( done.IsValid() )
				done.Destroy();
		}

		if ( !PracticeProp.IsValid() || timeInState < 0.5f )
			return;

		// the last copy is still there and still in reach: he'll go for that one
		if ( practiceSpare.IsValid() && FlatDistance( practiceSpare.WorldPosition, WorldPosition ) < PropSearchRadius )
			return;

		var spot = WorldPosition + WorldRotation.Forward * ( BodyRadius + 40f ) + Vector3.Up * 20f;

		practiceSpare = PracticeProp.Clone( spot );
		practiceSpare.Enabled = true;
		practiceSpare.Tags.Remove( EnemyDirector.NoThrowTag );
	}

	/// <summary>Gives up on this throw and goes back to the crowd; leaving the throw states cleans up after it.</summary>
	void AbandonThrow( string why )
	{
		if ( LogState )
			Log.Info( $"[Enemy] {GameObject.Name} gives up the throw — {why}" );

		EnterWaiting();
	}

	/// <summary>
	/// Out of the throw, however it ended — thrown, hit, knocked down, killed, given up. Anything still in his hand
	/// falls out of it, what he'd claimed is free again, and his fists come back up.
	/// </summary>
	void EndThrow()
	{
		if ( holding && prop.IsValid() )
		{
			LetGo();

			// knocked out of his hands, not placed down: a little pop so it reads as dropped
			prop.Velocity = Body.IsValid() ? Body.Velocity + Vector3.Up * 120f : Vector3.Up * 120f;
			prop.AngularVelocity = Vector3.Random.Normal * 4f;

			if ( LogState )
				Log.Info( $"[Enemy] {GameObject.Name} drops {prop.GameObject.Name}" );
		}

		holding = false;
		director?.Unclaim( prop );
		director?.ReleaseThrowJob( this );

		prop = null;
		threat = null;

		if ( Animator.IsValid() )
		{
			Animator.HoldType = CitizenAnimationHelper.HoldTypes.Punch;
			Animator.DuckLevel = 0f;
			Animator.IkRightHand = null;
		}
		else if ( Renderer.IsValid() )
		{
			if ( !string.IsNullOrEmpty( CarryParameter ) )
				Renderer.Set( CarryParameter, false );

			if ( !string.IsNullOrEmpty( ThrowWindupParameter ) )
				Renderer.Set( ThrowWindupParameter, false );
		}
	}

	/// <summary>
	/// The throw's poses. On the citizen they're borrowed — a crouch with the hand reaching for the pick-up, the
	/// hold-item pose to carry, the melee swing held back through the windup — until the custom rig has its own,
	/// which only needs the parameters named in "Throwing Animation".
	/// </summary>
	void AnimateThrow()
	{
		if ( !IsThrowing )
			return;

		if ( !Animator.IsValid() )
		{
			if ( !Renderer.IsValid() )
				return;

			if ( !string.IsNullOrEmpty( CarryParameter ) )
				Renderer.Set( CarryParameter, holding );

			if ( !string.IsNullOrEmpty( ThrowWindupParameter ) )
				Renderer.Set( ThrowWindupParameter, State == EnemyState.ThrowWindup );

			return;
		}

		switch ( State )
		{
			// arms down while he walks over: going for something, not squaring up
			case EnemyState.Fetching:
				Animator.HoldType = CitizenAnimationHelper.HoldTypes.None;
				break;

			// down and back up again, the hand reaching for the prop until it has it
			case EnemyState.Grabbing:
				Animator.HoldType = CitizenAnimationHelper.HoldTypes.None;
				Animator.DuckLevel = MathF.Sin( MathF.PI * ( timeInState / MathF.Max( GrabTime, 0.01f ) ).Clamp( 0f, 1f ) );
				Animator.IkRightHand = holding || !prop.IsValid() ? null : prop.GameObject;
				break;

			case EnemyState.Carrying:
				Animator.HoldType = CitizenAnimationHelper.HoldTypes.HoldItem;
				Animator.DuckLevel = 0f;
				Animator.IkRightHand = null;
				break;

			case EnemyState.ThrowWindup:
			case EnemyState.Throwing:
				Animator.HoldType = CitizenAnimationHelper.HoldTypes.Swing;
				break;
		}
	}

	static bool IsPartOf( GameObject obj, GameObject root )
	{
		if ( !obj.IsValid() || !root.IsValid() )
			return false;

		for ( var g = obj; g.IsValid(); g = g.Parent )
		{
			if ( g == root )
				return true;
		}

		return false;
	}
}
