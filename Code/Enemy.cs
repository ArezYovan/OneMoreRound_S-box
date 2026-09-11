using System;
using System.Linq;
using Sandbox.Citizen;

public enum EnemyState
{
	Idle,
	Circling,
	Engaging,
	Windup,
	Recover,
	Stunned,
	Staggered,
	Dead
}

/// <summary>
/// The Regular: walks at you, telegraphs hard, swings, falls over. Deliberately the dumbest
/// enemy in the design doc, because its job is to tell us whether the combat feels good, not
/// to be a challenge.
///
/// Built on a Rigidbody rather than a CharacterController on purpose. Knockback is the whole
/// point of this game, and a controller would have to reimplement being shoved around; a
/// rigidbody just gets shoved. It also means PlayerCombat's existing impulse works on these
/// with no special case.
/// </summary>
public sealed class Enemy : Component
{
	[RequireComponent] Rigidbody Body { get; set; }
	[RequireComponent] Health Hp { get; set; }

	[Property, Group( "References" ), Description( "Optional, found automatically. Without one they simply never go down and can't be picked up." )]
	public Stagger Stagger { get; set; }

	[Property, Group( "References" ), Description( "Who to walk at. Found automatically from whatever has RBPlayerMovement on it." )]
	public GameObject Target { get; set; }

	[Property, Group( "References" ), Description( "The citizen. Found automatically in children." )]
	public SkinnedModelRenderer Renderer { get; set; }

	[Property, Group( "References" ), Description( "Optional, found automatically. Drives the citizen's locomotion animation." )]
	public CitizenAnimationHelper Animator { get; set; }

	[Property, Group( "References" ), Description( "Optional, found automatically. The upright collision hull, switched off when the ragdoll takes over." )]
	public Collider Hull { get; set; }

	[Property, Group( "Movement" )] public float MoveSpeed { get; set; } = 210f;
	[Property, Group( "Movement" )] public float Acceleration { get; set; } = 11f;
	[Property, Group( "Movement" )] public float TurnSpeed { get; set; } = 8f;

	[Property, Group( "Movement" ), Description( "How close it gets before it plants its feet and swings." )]
	public float StopDistance { get; set; } = 60f;

	[Property, Group( "Crowd" ), Description( "Radius of the ring they wait on. Wide enough to see the whole crowd at once in first person, tight enough to feel surrounded." )]
	public float CircleDistance { get; set; } = 120f;

	[Property, Group( "Crowd" ), Description( "Close enough to their slot to stop shuffling. At zero they twitch on the spot forever, chasing a point they can never stand exactly on." )]
	public float SlotArriveDistance { get; set; } = 22f;

	[Property, Group( "Crowd" ), Description( "How far outside the ring they will still ask for a turn to attack." )]
	public float EngageSlack { get; set; } = 45f;

	[Property, Group( "Crowd" ), Description( "Give the turn back if they cannot reach him in this long — otherwise one man stuck behind a table holds a turn nobody else can use." )]
	public float EngageTimeout { get; set; } = 3f;

	[Property, Group( "Crowd" ), Description( "How close another patron gets before they start elbowing apart." )]
	public float SeparationRadius { get; set; } = 50f;

	[Property, Group( "Crowd" ), Description( "How hard they push off each other. This is what stops the crowd collapsing into one grinding pile." )]
	public float SeparationStrength { get; set; } = 0.9f;

	[Property, Group( "Crowd" ), Description( "Per-man random spread on speed and windup. Nothing reads as artificial faster than nine men marching and swinging on the same frame." )]
	public float Variance { get; set; } = 0.18f;

	[Property, Group( "Attack" )] public float AttackDamage { get; set; } = 14f;
	[Property, Group( "Attack" )] public float AttackRange { get; set; } = 75f;
	[Property, Group( "Attack" )] public float AttackRadius { get; set; } = 14f;

	[Property, Group( "Attack" ), Description( "How far up the swing comes from. Roughly chest height on a citizen." )]
	public float AttackHeight { get; set; } = 50f;

	[Property, Group( "Attack" ), Description( "The telegraph. The design doc calls for 'heavily telegraphed swings', and this is that number — it's the entire difficulty dial for this enemy, so tune it here before touching anything else." )]
	public float WindupTime { get; set; } = 0.45f;

	[Property, Group( "Attack" ), Description( "Dead time after swinging, hit or miss. This is the window you're meant to punish." )]
	public float RecoverTime { get; set; } = 0.6f;

	[Property, Group( "Attack" )] public float AttackCooldown { get; set; } = 0.4f;
	[Property, Group( "Attack" )] public float Knockback { get; set; } = 260f;
	[Property, Group( "Attack" )] public float AttackLift { get; set; } = 0.1f;

	[Property, Group( "Reactions" ), Description( "How long the AI stops steering after being hit. Without this the chase velocity overwrites the knockback on the very next tick and punches feel like they do nothing — this is what lets a hit actually move them." )]
	public float StunTime { get; set; } = 0.35f;

	[Property, Group( "Reactions" )] public bool RagdollOnDeath { get; set; } = true;

	[Property, Group( "Debug" )] public bool LogState { get; set; } = true;

	[Property] public EnemyState State { get; private set; } = EnemyState.Idle;

	/// <summary>Upright and still in the fight, so worth a place in the ring.</summary>
	public bool WantsSlot => State != EnemyState.Dead && State != EnemyState.Staggered;

	TimeSince timeInState;
	TimeSince timeSinceAttack;
	EnemyDirector director;
	float speedScale = 1f;
	float windupScale = 1f;
	Vector3? pendingLaunch;
	ModelPhysics ragdoll;

	protected override void OnStart()
	{
		Renderer ??= Components.Get<SkinnedModelRenderer>( FindMode.Enabled | FindMode.InSelf | FindMode.InDescendants );
		Animator ??= Components.Get<CitizenAnimationHelper>( FindMode.Enabled | FindMode.InSelf | FindMode.InDescendants );
		Hull ??= Components.Get<Collider>( FindMode.EnabledInSelf );
		Stagger ??= Components.Get<Stagger>( FindMode.EnabledInSelf );

		director = Scene.GetSystem<EnemyDirector>();
		director.Register( this );

		// each man is his own slightly different fighter
		speedScale = Game.Random.Float( 1f - Variance, 1f + Variance );
		windupScale = Game.Random.Float( 1f - Variance, 1f + Variance );

		Target ??= Scene.GetAllComponents<RBPlayerMovement>().FirstOrDefault()?.GameObject;

		if ( !Target.IsValid() )
			Log.Warning( $"{GameObject.Name}: Enemy found nothing with RBPlayerMovement to chase — it'll just stand there." );

		if ( Hp.IsValid() )
		{
			Hp.OnDamaged += OnHurt;
			Hp.OnDied += OnDeath;
		}

		if ( Stagger.IsValid() )
		{
			Stagger.OnStaggered += OnStaggered;
			Stagger.OnRecovered += OnRecovered;
		}

		// the animgraph's punch lives under holdtype "melee_punch" (index 5). Left at "none" the
		// b_attack trigger fires into a state that doesn't exist and the swing is invisible — which
		// is exactly what it did before this line
		if ( Animator.IsValid() )
			Animator.HoldType = CitizenAnimationHelper.HoldTypes.Punch;

		// shoved, not toppled: a kick should send them skidding across the floor upright, and the
		// falling over is the ragdoll's job when they die. Locked here rather than left to the
		// inspector so a hand-built test enemy can't get this subtly wrong
		if ( Body.IsValid() )
			Body.Locking = new PhysicsLock { Pitch = true, Roll = true };

		Enter( EnemyState.Circling );
	}

	protected override void OnDestroy()
	{
		director?.Unregister( this );

		if ( !Hp.IsValid() )
			return;

		Hp.OnDamaged -= OnHurt;
		Hp.OnDied -= OnDeath;

		if ( !Stagger.IsValid() )
			return;

		Stagger.OnStaggered -= OnStaggered;
		Stagger.OnRecovered -= OnRecovered;
	}

	protected override void OnFixedUpdate()
	{
		if ( State == EnemyState.Dead )
		{
			ApplyPendingLaunch();
			return;
		}

		if ( !Target.IsValid() )
		{
			Brake();
			UpdateAnimation();
			return;
		}

		var toTarget = ( Target.WorldPosition - WorldPosition ).WithZ( 0 );
		float distance = toTarget.Length;

		switch ( State )
		{
			// no steering at all here: the hit's impulse is in the rigidbody, and the only way it
			// reads on screen is if we keep our hands off the velocity long enough for it to travel
			case EnemyState.Stunned:
				if ( timeInState > StunTime )
					Enter( EnemyState.Circling );
				break;

			// hands off entirely: they're being carried, thrown, or lying there, and every one of
			// those is somebody else's physics to own. Stagger decides when this ends, not us
			case EnemyState.Staggered:
				break;

			// waiting his turn on his own piece of the ring, instead of piling onto the same spot as
			// everyone else and shoving for it
			case EnemyState.Circling:
				FaceTarget( toTarget );
				MoveTowards( director.GetSlotPosition( this, Target.WorldPosition, CircleDistance ), SlotArriveDistance );

				if ( distance <= CircleDistance + EngageSlack && timeSinceAttack > AttackCooldown && director.RequestAttack( this ) )
					Enter( EnemyState.Engaging );
				break;

			// he has a turn: close the distance and commit to it
			case EnemyState.Engaging:
				FaceTarget( toTarget );
				MoveTowards( Target.WorldPosition, StopDistance * 0.9f );

				if ( distance <= StopDistance )
					BeginSwing();
				else if ( timeInState > EngageTimeout )
					Enter( EnemyState.Circling );
				break;

			case EnemyState.Windup:
				FaceTarget( toTarget );
				Brake();

				if ( timeInState >= WindupTime * windupScale )
				{
					Strike();
					Enter( EnemyState.Recover );
				}
				break;

			case EnemyState.Recover:
				Brake();

				if ( timeInState >= RecoverTime )
					Enter( EnemyState.Circling );
				break;
		}

		Body.AngularVelocity = Vector3.Zero;
		UpdateAnimation();
	}

	void Enter( EnemyState state )
	{
		if ( State == state )
			return;

		// a turn is only held while actually going through with an attack, so being hit, staggered
		// or killed hands it straight back to whoever is waiting for one
		bool keepsTurn = state is EnemyState.Engaging or EnemyState.Windup or EnemyState.Recover;

		if ( !keepsTurn )
			director?.ReleaseAttack( this );

		State = state;
		timeInState = 0;

		if ( LogState )
			Log.Info( $"[Enemy] {GameObject.Name} → {state}" );
	}

	void MoveTowards( Vector3 destination, float arriveDistance )
	{
		var to = ( destination - WorldPosition ).WithZ( 0 );

		// stop short rather than shouldering into things — walking into the player and pushing him
		// around the room reads as a bug, not as pressure
		var wish = to.Length > arriveDistance ? to.Normal * MoveSpeed * speedScale : Vector3.Zero;

		wish += Separation() * MoveSpeed * SeparationStrength;

		// same exponential approach the player uses, so both move with the same weight
		float t = 1f - MathF.Exp( -Acceleration * Time.Delta );
		var velocity = Body.Velocity;

		Body.Velocity = velocity.WithZ( 0 ).LerpTo( wish, t ).WithZ( velocity.z );
	}

	/// <summary>
	/// Elbow room. The ring spreads them out in principle, but two men heading for neighbouring
	/// slots still grind against each other on the way — this is what keeps the crowd from
	/// collapsing back into one shoving pile.
	/// </summary>
	Vector3 Separation()
	{
		if ( director is null )
			return Vector3.Zero;

		var push = Vector3.Zero;

		foreach ( var other in director.Enemies )
		{
			if ( other == this || !other.IsValid() || !other.WantsSlot )
				continue;

			var away = ( WorldPosition - other.WorldPosition ).WithZ( 0 );
			float distance = away.Length;

			if ( distance >= SeparationRadius || distance <= 0.01f )
				continue;

			push += away.Normal * ( 1f - distance / SeparationRadius );
		}

		return push;
	}

	void Brake()
	{
		float t = 1f - MathF.Exp( -Acceleration * Time.Delta );
		var velocity = Body.Velocity;

		Body.Velocity = velocity.WithZ( 0 ).LerpTo( Vector3.Zero, t ).WithZ( velocity.z );
	}

	void FaceTarget( Vector3 toTarget )
	{
		if ( toTarget.IsNearlyZero() )
			return;

		var wanted = Rotation.LookAt( toTarget.Normal, Vector3.Up );
		WorldRotation = Rotation.Slerp( WorldRotation, wanted, TurnSpeed * Time.Delta );
	}

	void BeginSwing()
	{
		Enter( EnemyState.Windup );
		timeSinceAttack = 0;

		// the animation is the telegraph — it starts now and the damage lands WindupTime later,
		// so what you dodge is what you can see
		Renderer?.Set( "b_attack", true );

		if ( LogState )
			Log.Info( $"[Enemy] {GameObject.Name} swings" );
	}

	/// <summary>Resolved the same way the player's attacks are, so both sides obey the same rules.</summary>
	void Strike()
	{
		var origin = WorldPosition + Vector3.Up * AttackHeight;
		var forward = WorldRotation.Forward;

		var trace = Scene.Trace
			.Sphere( AttackRadius, origin, origin + forward * AttackRange )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

		if ( !trace.Hit || !trace.GameObject.IsValid() )
		{
			if ( LogState )
				Log.Info( $"[Enemy] {GameObject.Name} missed" );

			return;
		}

		var hit = trace.GameObject;
		var health = hit.Components.Get<Health>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );
		var body = hit.Components.Get<Rigidbody>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );
		var direction = ( forward + Vector3.Up * AttackLift ).Normal;

		if ( body.IsValid() )
			body.ApplyImpulseAt( trace.HitPosition, direction * Knockback * ( body.PhysicsBody?.Mass ?? 1f ) );

		health?.TakeDamage( AttackDamage, GameObject, trace.HitPosition );

		if ( LogState )
		{
			string dealt = health.IsValid() ? $"for {AttackDamage:0.#}" : "— no Health on it";
			Log.Info( $"[Enemy] {GameObject.Name} hit {hit.Name} {dealt}" );
		}
	}

	void OnStaggered()
	{
		Enter( EnemyState.Staggered );

		// fists come down. There's no stagger animation on the citizen, so dropping out of the
		// punch holdtype is the cheapest honest tell that he's stopped fighting
		if ( Animator.IsValid() )
			Animator.HoldType = CitizenAnimationHelper.HoldTypes.None;
	}

	void OnRecovered()
	{
		if ( State == EnemyState.Dead )
			return;

		if ( Animator.IsValid() )
			Animator.HoldType = CitizenAnimationHelper.HoldTypes.Punch;

		Enter( EnemyState.Circling );
	}

	void OnHurt( float amount, GameObject attacker )
	{
		if ( State == EnemyState.Dead || State == EnemyState.Staggered )
			return;

		// interrupts a windup, so landing a punch first genuinely beats them to the swing
		Enter( EnemyState.Stunned );

		Renderer?.Set( "hit", true );
		Renderer?.Set( "hit_strength", amount.Remap( 0f, 30f, 0.2f, 1f, true ) );
	}

	void OnDeath()
	{
		Enter( EnemyState.Dead );

		// the killing blow's impulse is already sitting in the rigidbody's velocity. Hand it to the
		// ragdoll before switching over, or a kick that kills someone drops them straight down
		// instead of launching them, which is the exact opposite of what a kick should do
		var velocity = Body.IsValid() ? Body.Velocity : Vector3.Zero;

		if ( RagdollOnDeath && Renderer.IsValid() )
		{
			// created on whatever object actually holds the citizen, which is usually a child — the
			// ragdoll's bodies are built around the renderer, so it has to live alongside it
			ragdoll = Renderer.GameObject.Components.GetOrCreate<ModelPhysics>();
			ragdoll.Renderer = Renderer;
			ragdoll.Model = Renderer.Model;
			ragdoll.Enabled = true;

			pendingLaunch = velocity;
			ApplyPendingLaunch();
		}

		if ( Hull.IsValid() )
			Hull.Enabled = false;

		if ( Body.IsValid() )
			Body.Enabled = false;
	}

	/// <summary>
	/// Hands the corpse the velocity it died with. The ragdoll's bodies don't exist the instant
	/// ModelPhysics is switched on, so this is retried each tick until they do rather than assumed
	/// to work first time — miss the window and a kick that kills just drops them on the spot.
	/// </summary>
	void ApplyPendingLaunch()
	{
		if ( !pendingLaunch.HasValue )
			return;

		if ( ragdoll?.Bodies is not { Count: > 0 } bodies )
			return;

		foreach ( var body in bodies )
		{
			if ( body.Component.IsValid() )
				body.Component.Velocity = pendingLaunch.Value;
		}

		pendingLaunch = null;
	}

	void UpdateAnimation()
	{
		if ( !Animator.IsValid() )
			return;

		Animator.IsGrounded = true;
		Animator.WithVelocity( Body.IsValid() ? Body.Velocity : Vector3.Zero );

		if ( Target.IsValid() )
			Animator.WithLook( Target.WorldPosition - WorldPosition );
	}
}
