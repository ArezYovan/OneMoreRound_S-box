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

	[Property, Group( "Debug" )] public bool LogHits { get; set; } = true;

	public bool IsArmed => armed && !timeUntilDisarm;

	GameObject thrower;
	Rigidbody body;
	bool armed;
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

	public void Arm( GameObject attacker ) => Arm( attacker, ArmedTime );

	public void Arm( GameObject attacker, float armedTime )
	{
		thrower = attacker;
		armed = true;
		timeUntilDisarm = armedTime;
		timeSinceArmed = 0;
		alreadyHit.Clear();

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
	}

	protected override void OnFixedUpdate()
	{
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

		float mass = ScaleWithMass ? ( body?.PhysicsBody?.Mass ?? 20f ) / 20f : 1f;
		float damage = MathF.Min( MaxDamage, ( BaseDamage + speed * DamagePerSpeed ) * mass );
		float rock = BaseStagger + speed * StaggerPerSpeed;

		// stagger before damage, the same order a punch uses, so a hit that does both still knocks down
		if ( stagger.IsValid() )
			stagger.Add( rock, thrower );

		if ( health.IsValid() )
			health.TakeDamage( damage, thrower, collision.Contact.Point );

		if ( LogHits )
			Log.Info( $"[Thrown] {GameObject.Name} struck {other.Name} at {speed:0} u/s for {damage:0.#} and {rock:0} stagger" );
	}

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
