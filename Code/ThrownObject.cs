using System;
using System.Collections.Generic;

/// <summary>
/// Turns an ordinary prop into a weapon for a couple of seconds. Armed at the moment of the
/// throw and quiet again shortly after, so a barstool hurts because you threw it rather than
/// because it is inherently a dangerous barstool. Nothing needs this component in advance —
/// PlayerGrab adds it on the way out of your hands, and PlayerCombat adds it to anything a hit
/// launches hard enough, which is how a kicked man bowls through the crowd behind him.
///
/// It only ever hurts what it runs INTO. An armed body lying still is not a weapon: without that
/// rule, someone walking into a man on the floor at full speed would take a hit for tripping over him.
/// </summary>
public sealed class ThrownObject : Component, Component.ICollisionListener
{
	[Property, Group( "Damage" ), Description( "Damage floor for any impact that counts at all." )]
	public float BaseDamage { get; set; } = 5f;

	[Property, Group( "Damage" ), Description( "Extra damage per unit/s of closing speed. A hard throw should read as harder than a lob." )]
	public float DamagePerSpeed { get; set; } = 0.045f;

	[Property, Group( "Damage" ), Description( "Impacts slower than this are a nudge, not a hit. Checked twice: this thing has to be travelling that fast, and the two have to meet that fast — so a bottle rolling into an ankle doesn't count, and neither does a man walking into a body on the floor." )]
	public float MinimumSpeed { get; set; } = 150f;

	[Property, Group( "Damage" )] public float MaxDamage { get; set; } = 60f;

	[Property, Group( "Damage" ), Description( "Heavier things hurt more, normalised around a 20-mass prop so a bottle stays a bottle and the cash register really lands." )]
	public bool ScaleWithMass { get; set; } = true;

	[Property, Group( "Damage" ), Description( "Seconds the throw stays dangerous. After this it's just furniture on the floor again. A launch from a punch passes its own, shorter window." )]
	public float ArmedTime { get; set; } = 2f;

	[Property, Group( "Stagger" ), Description( "Stagger dealt by any impact that counts. This is what makes a thrown man knock his friends down rather than just bruise them — and a man on the floor is one you can pick up and throw next." )]
	public float BaseStagger { get; set; } = 20f;

	[Property, Group( "Stagger" ), Description( "Extra stagger per unit/s of closing speed." )]
	public float StaggerPerSpeed { get; set; } = 0.03f;

	[Property, Group( "Ricochet" ), Description( "Farthest the next man can be for a throw to ricochet on to him." )]
	public float RicochetRange { get; set; } = 700f;

	[Property, Group( "Ricochet" ), Description( "Speed it flies on at. Fixed rather than kept from the hit, which bleeds most of it away." )]
	public float RicochetSpeed { get; set; } = 750f;

	[Property, Group( "Ricochet" ), Description( "How high up the next man it aims, from his feet. About his chest." )]
	public float RicochetAimHeight { get; set; } = 70f;

	[Property, Group( "Debug" )] public bool LogHits { get; set; } = true;

	public bool IsArmed => armed && !timeUntilDisarm;

	/// <summary>Multiplies the damage this throw does. Reset by every Arm, so it only ever lasts one throw — the player's grab sets it right after.</summary>
	public float DamageScale { get; set; } = 1f;

	/// <summary>How many more men this throw flies on to after hitting one. Reset by every Arm, like DamageScale.</summary>
	public int Bounces { get; set; }

	/// <summary>Bowling Ball: whoever this crashes into goes straight into a stagger. Reset by every Arm, like DamageScale.</summary>
	public bool KnocksDown { get; set; }

	GameObject thrower;
	Rigidbody body;
	bool armed;

	// this throw's own ceiling on damage, at most MaxDamage. An enemy's throw passes a lower one, so a chair
	// lobbed from across the room is a warning shot rather than a one-hit knockout — and the prop keeps the
	// full MaxDamage for the next time the player throws it
	float damageCap;
	TimeUntil timeUntilDisarm;
	TimeSince timeSinceArmed;

	// Speed going into the physics step, recorded before the step runs. By the time a collision is
	// reported a head-on hit has already bled most of it away, so the live velocity can't be trusted to
	// say whether this thing was the one doing the moving.
	float speedBeforeStep;

	// Arming happens a moment before the launch, and that first physics step lands before any speed has
	// been recorded — so for this long after arming, the launch is taken on trust. It's what lets a man
	// kicked point-blank into the one standing right behind him still count as a hit.
	const float LaunchGrace = 0.06f;

	// one prop shouldn't hit the same guy four times as it tumbles past him, but it absolutely
	// should be able to take out two people on one throw
	readonly HashSet<GameObject> alreadyHit = new();

	// who a ricochet is heading for. Picked in the collision and launched on the next physics tick, since
	// a velocity set in the middle of a collision can be overwritten by the bounce the physics is working out
	Enemy ricochetTarget;

	public void Arm( GameObject attacker ) => Arm( attacker, ArmedTime );

	public void Arm( GameObject attacker, float armedTime ) => Arm( attacker, armedTime, MaxDamage );

	public void Arm( GameObject attacker, float armedTime, float maxDamage )
	{
		thrower = attacker;
		damageCap = MathF.Min( maxDamage, MaxDamage );
		armed = true;
		timeUntilDisarm = armedTime;
		timeSinceArmed = 0;
		alreadyHit.Clear();
		DamageScale = 1f;
		Bounces = 0;
		KnocksDown = false;
		ricochetTarget = null;

		body = Components.Get<Rigidbody>();

		if ( !body.IsValid() )
			return;

		// a prop with collision events switched off never calls OnCollisionStart at all, so the throw
		// would quietly do nothing and look like a damage bug. Forced on here rather than trusted to
		// whatever the prop was authored with
		body.CollisionEventsEnabled = true;

		// and a bottle at 850 u/s is small and fast enough to step straight through the man you aimed
		// it at between two physics ticks
		body.EnhancedCcd = true;
	}

	/// <summary>
	/// Stop being a weapon, without waiting out <see cref="ArmedTime"/>. Catching a thrown chair
	/// out of the air is a move worth having, and it can only work if the thing stops being live
	/// the moment it's in your hands — otherwise the catch is what kills you. An enemy calls this too,
	/// the moment he's back on his feet and steering.
	/// </summary>
	public void Disarm()
	{
		armed = false;
		alreadyHit.Clear();
		ricochetTarget = null;
	}

	protected override void OnFixedUpdate()
	{
		if ( ricochetTarget is not null )
			LaunchRicochet();

		if ( body.IsValid() )
			speedBeforeStep = body.Velocity.Length;
	}

	public void OnCollisionStart( Collision collision )
	{
		if ( !IsArmed )
			return;

		var other = collision.Other.GameObject;

		if ( !other.IsValid() || IsPartOf( other, thrower ) || IsPartOf( other, GameObject ) )
			return;

		// it has to be the one doing the moving — see speedBeforeStep and LaunchGrace
		if ( timeSinceArmed > LaunchGrace && speedBeforeStep < MinimumSpeed )
			return;

		// closing speed along the contact normal, so a glancing scrape doesn't read as a direct hit
		float speed = MathF.Abs( collision.Contact.NormalSpeed );

		if ( speed < MinimumSpeed )
			return;

		var search = FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors;
		var health = other.Components.Get<Health>( search );
		var stagger = other.Components.Get<Stagger>( search );

		var victim = health.IsValid() ? health.GameObject : stagger.IsValid() ? stagger.GameObject : null;

		// counted only once the hit is known to land, so a slow first brush can't use up the one hit
		// this victim is owed
		if ( !victim.IsValid() || !alreadyHit.Add( victim ) )
			return;

		// asked before the hit lands: a throw that kills a man should still ricochet off him, but one that
		// merely bounces off a body already on the floor shouldn't
		bool wasStanding = !health.IsValid() || !health.IsDead;

		float mass = ScaleWithMass ? ( body?.PhysicsBody?.Mass ?? 20f ) / 20f : 1f;
		// scaled after the cap, so a drink moves the damage of a heavy prop that was already at the cap as well
		float damage = MathF.Min( damageCap, ( BaseDamage + speed * DamagePerSpeed ) * mass ) * DamageScale;
		float rock = BaseStagger + speed * StaggerPerSpeed;

		// stagger before damage, the same order a punch uses, so a hit that does both still knocks down
		if ( stagger.IsValid() )
		{
			if ( KnocksDown )
				stagger.ForceStagger( thrower );
			else
				stagger.Add( rock, thrower );
		}

		if ( health.IsValid() )
			health.TakeDamage( damage, thrower, collision.Contact.Point );

		if ( LogHits )
			Log.Info( $"[Thrown] {GameObject.Name} struck {other.Name} at {speed:0} u/s for {damage:0.#} and {( KnocksDown ? "a knockdown" : $"{rock:0} stagger" )}" );

		// only off a man — a ricochet off the wall or a table is just physics
		if ( Bounces > 0 && wasStanding && victim.Components.Get<Enemy>( FindMode.EverythingInSelfAndAncestors ).IsValid() )
		{
			ricochetTarget = FindRicochetTarget();

			if ( ricochetTarget is not null )
				Bounces--;
		}
	}

	/// <summary>The nearest man this hasn't already hit, standing, in range and in plain sight of where it is now.</summary>
	Enemy FindRicochetTarget()
	{
		Enemy best = null;
		float bestDistance = RicochetRange;

		foreach ( var enemy in Scene.GetAllComponents<Enemy>() )
		{
			if ( !enemy.IsValid() || enemy.State == EnemyState.Dead || alreadyHit.Contains( enemy.GameObject ) || IsPartOf( enemy.GameObject, thrower ) )
				continue;

			// a thrown man is an Enemy too, and the nearest one to himself — without this he'd ricochet straight up
			if ( IsPartOf( GameObject, enemy.GameObject ) )
				continue;

			float distance = ( RicochetAim( enemy ) - WorldPosition ).Length;

			if ( distance >= bestDistance )
				continue;

			// bodies don't block it — it can clip past a man on the way — but the level does
			var trace = Scene.Trace.Ray( WorldPosition, RicochetAim( enemy ) )
				.IgnoreGameObjectHierarchy( GameObject )
				.Run();

			if ( trace.Hit && trace.GameObject.IsValid() && !IsPartOf( trace.GameObject, enemy.GameObject )
				&& !trace.GameObject.Components.Get<Rigidbody>( FindMode.EverythingInSelfAndAncestors ).IsValid() )
				continue;

			best = enemy;
			bestDistance = distance;
		}

		return best;
	}

	/// <summary>Sends it straight at the man picked in the collision, with a little lift to cancel the drop on the way.</summary>
	void LaunchRicochet()
	{
		var target = ricochetTarget;
		ricochetTarget = null;

		if ( !target.IsValid() || !body.IsValid() )
			return;

		var to = RicochetAim( target ) - WorldPosition;
		float travelTime = to.Length / MathF.Max( RicochetSpeed, 1f );

		body.Velocity = to.Normal * RicochetSpeed + Vector3.Up * ( 0.5f * -Scene.PhysicsWorld.Gravity.z * travelTime );

		// dangerous again for the whole trip, and taken on trust for the first moment like a fresh throw
		timeUntilDisarm = ArmedTime;
		timeSinceArmed = 0;

		if ( LogHits )
			Log.Info( $"[Thrown] {GameObject.Name} ricochets on to {target.GameObject.Name} — {Bounces} bounce(s) left" );
	}

	Vector3 RicochetAim( Enemy enemy ) => enemy.WorldPosition + Vector3.Up * RicochetAimHeight;

	public void OnCollisionUpdate( Collision collision ) { }
	public void OnCollisionStop( CollisionStop collision ) { }

	/// <summary>Walks up the hierarchy so the throw can't clip the person who threw it on the way out.</summary>
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
}
