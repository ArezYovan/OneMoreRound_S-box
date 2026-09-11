using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// The thing that stops a crowd behaving like one animal with nine bodies.
///
/// Two jobs, and they're the whole difference between a mob and a scrum:
///
/// Positions — every enemy is given its own slot on a ring around the player instead of all
/// pathing at his exact feet. Slots are handed out by who is already nearest to them, so nobody
/// crosses the room to swap places with someone else.
///
/// Turns — only a couple of them are allowed to actually swing at any moment. The rest circle
/// and wait. Nine men attacking at once isn't difficult, it's noise: you can't read any single
/// windup, so you can't respond to any of them, and the fight stops being a fight.
///
/// A GameObjectSystem rather than a component, so there's nothing to place in the scene and no
/// way to end up with two of them.
/// </summary>
public sealed class EnemyDirector : GameObjectSystem<EnemyDirector>
{
	public EnemyDirector( Scene scene ) : base( scene ) { }

	/// <summary>How many may be swinging at once. Two is enough to feel surrounded and still be fair.</summary>
	public int MaxAttackers { get; set; } = 2;

	/// <summary>Minimum gap between handing out turns, so attacks arrive one at a time rather than as a volley.</summary>
	public float AttackSpacing { get; set; } = 0.55f;

	/// <summary>How often the ring is recalculated. Often enough to follow the player, rarely enough not to jitter.</summary>
	public float ReassignInterval { get; set; } = 0.25f;

	public IReadOnlyList<Enemy> Enemies => enemies;
	public int AttackerCount => attackers.Count;

	readonly List<Enemy> enemies = new();
	readonly List<Enemy> attackers = new();
	readonly Dictionary<Enemy, float> slotAngles = new();

	RealTimeSince timeSinceAssign;
	RealTimeSince timeSinceGrant;

	public void Register( Enemy enemy )
	{
		if ( enemy.IsValid() && !enemies.Contains( enemy ) )
			enemies.Add( enemy );
	}

	public void Unregister( Enemy enemy )
	{
		enemies.Remove( enemy );
		attackers.Remove( enemy );
		slotAngles.Remove( enemy );
	}

	/// <summary>Ask for a turn to attack. False means circle and wait.</summary>
	public bool RequestAttack( Enemy enemy )
	{
		Prune();

		if ( attackers.Contains( enemy ) )
			return true;

		if ( attackers.Count >= MaxAttackers || timeSinceGrant < AttackSpacing )
			return false;

		attackers.Add( enemy );
		timeSinceGrant = 0;
		return true;
	}

	public void ReleaseAttack( Enemy enemy ) => attackers.Remove( enemy );

	public bool IsAttacking( Enemy enemy ) => attackers.Contains( enemy );

	/// <summary>Where this enemy should stand while it waits its turn.</summary>
	public Vector3 GetSlotPosition( Enemy enemy, Vector3 target, float radius )
	{
		Assign( target );

		float angle = slotAngles.TryGetValue( enemy, out var a ) ? a : 0f;
		return target + Rotation.FromYaw( angle ).Forward * radius;
	}

	void Assign( Vector3 target )
	{
		if ( timeSinceAssign < ReassignInterval )
			return;

		timeSinceAssign = 0;
		Prune();

		// only the ones actually circling get a slot — a staggered man on the floor shouldn't be
		// holding a place in the ring that someone upright could be using
		var active = enemies.Where( e => e.IsValid() && e.WantsSlot ).ToList();

		if ( active.Count == 0 )
			return;

		// sorted by where each already stands, so the ring is handed out in the order they happen
		// to be arranged around him and nobody has to walk through the middle to reach their spot
		var ordered = active.OrderBy( e => AngleOf( target, e.WorldPosition ) ).ToList();

		float step = 360f / ordered.Count;
		float anchor = AngleOf( target, ordered[0].WorldPosition );

		for ( int i = 0; i < ordered.Count; i++ )
			slotAngles[ordered[i]] = anchor + step * i;
	}

	void Prune()
	{
		enemies.RemoveAll( e => !e.IsValid() );
		attackers.RemoveAll( e => !e.IsValid() || !e.WantsSlot );
	}

	static float AngleOf( Vector3 from, Vector3 to )
	{
		var d = ( to - from ).WithZ( 0 );
		return MathF.Atan2( d.y, d.x ) * ( 180f / MathF.PI );
	}
}
