using System;
using System.Collections.Generic;

/// <summary>
/// Turns an ordinary prop into a weapon for a couple of seconds. Armed at the moment of the
/// throw and quiet again shortly after, so a barstool hurts because you threw it rather than
/// because it is inherently a dangerous barstool. Nothing needs this component in advance —
/// PlayerGrab adds it on the way out of your hands.
/// </summary>
public sealed class ThrownObject : Component, Component.ICollisionListener
{
	[Property, Group( "Damage" ), Description( "Damage floor for any impact that counts at all." )]
	public float BaseDamage { get; set; } = 5f;

	[Property, Group( "Damage" ), Description( "Extra damage per unit/s of closing speed. A hard throw should read as harder than a lob." )]
	public float DamagePerSpeed { get; set; } = 0.045f;

	[Property, Group( "Damage" ), Description( "Impacts slower than this are a nudge, not a hit. Stops a thrown bottle rolling into an ankle and killing someone." )]
	public float MinimumSpeed { get; set; } = 150f;

	[Property, Group( "Damage" )] public float MaxDamage { get; set; } = 60f;

	[Property, Group( "Damage" ), Description( "Heavier things hurt more, normalised around a 20-mass prop so a bottle stays a bottle and the cash register really lands." )]
	public bool ScaleWithMass { get; set; } = true;

	[Property, Group( "Damage" ), Description( "Seconds the throw stays dangerous. After this it's just furniture on the floor again." )]
	public float ArmedTime { get; set; } = 2f;

	[Property, Group( "Debug" )] public bool LogHits { get; set; } = true;

	public bool IsArmed => armed && !timeUntilDisarm;

	GameObject thrower;
	Rigidbody body;
	bool armed;
	TimeUntil timeUntilDisarm;

	// one prop shouldn't hit the same guy four times as it tumbles past him, but it absolutely
	// should be able to take out two people on one throw
	readonly HashSet<GameObject> alreadyHit = new();

	public void Arm( GameObject attacker )
	{
		thrower = attacker;
		armed = true;
		timeUntilDisarm = ArmedTime;
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
	/// the moment it's in your hands — otherwise the catch is what kills you.
	/// </summary>
	public void Disarm()
	{
		armed = false;
		alreadyHit.Clear();
	}

	public void OnCollisionStart( Collision collision )
	{
		if ( !IsArmed )
			return;

		var other = collision.Other.GameObject;

		if ( !other.IsValid() || IsPartOf( other, thrower ) )
			return;

		var health = other.Components.Get<Health>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );

		if ( !health.IsValid() || !alreadyHit.Add( health.GameObject ) )
			return;

		// closing speed along the contact normal, so a glancing scrape doesn't read as a direct hit
		float speed = MathF.Abs( collision.Contact.NormalSpeed );

		if ( speed < MinimumSpeed )
			return;

		float mass = ScaleWithMass ? ( body?.PhysicsBody?.Mass ?? 20f ) / 20f : 1f;
		float damage = MathF.Min( MaxDamage, ( BaseDamage + speed * DamagePerSpeed ) * mass );

		health.TakeDamage( damage, thrower, collision.Contact.Point );

		if ( LogHits )
			Log.Info( $"[Thrown] {GameObject.Name} struck {other.Name} at {speed:0} u/s for {damage:0.#}" );
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
