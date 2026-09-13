using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// The thing that stops a crowd behaving like one animal with twenty bodies.
///
/// Two jobs, and they're the whole difference between a mob and a scrum:
///
/// Positions — every enemy is given his own bearing around the player, handed out by who is already
/// nearest to it, so nobody crosses the room to swap places with someone else. How far out along that
/// bearing he stands is his own (see Enemy's band), which spreads the crowd through depth instead of
/// piling everyone onto one circle.
///
/// Turns — only a few of them may actually swing at any moment; the rest hold back and wait. How many
/// scales with how full the room is, and the next turn goes to someone you can see before someone
/// behind you. A crowd you can't read isn't difficult, it's noise — and a hit from a man you never saw
/// isn't a challenge, it's unfair.
///
/// A GameObjectSystem rather than a component, so there's nothing to place in the scene and no way to
/// end up with two of them. Its tuning lives here in code for the same reason.
/// </summary>
public sealed class EnemyDirector : GameObjectSystem<EnemyDirector>
{
	public EnemyDirector( Scene scene ) : base( scene ) { }

	/// <summary>Turns handed out however few are standing. Three is where a crowd starts to feel like one.</summary>
	public int BaseAttackers { get; set; } = 3;

	/// <summary>One extra turn for every this-many men standing, so a packed room gets more hectic rather than just more crowded.</summary>
	public int EnemiesPerExtraAttacker { get; set; } = 8;

	/// <summary>Never more than this swinging at once, however full the room. Past about six you can't read the windups.</summary>
	public int MaxAttackersCap { get; set; } = 6;

	/// <summary>Minimum gap between handing out turns, so attacks arrive in quick succession rather than as one volley.</summary>
	public float AttackSpacing { get; set; } = 0.3f;

	/// <summary>
	/// How much being in front of the camera counts toward the next turn, against simply being close
	/// (0–1). Someone behind you still gets a turn when there's nobody better placed — just later.
	/// </summary>
	public float InViewPriority { get; set; } = 0.65f;

	/// <summary>How often the bearings are recalculated. Often enough to follow the player, rarely enough not to jitter.</summary>
	public float ReassignInterval { get; set; } = 0.25f;

	public IReadOnlyList<Enemy> Enemies => enemies;
	public int AttackerCount => attackers.Count;

	/// <summary>Turns available right now, scaled to how many are still standing.</summary>
	public int MaxAttackers => Math.Min( MaxAttackersCap, BaseAttackers + StandingCount / Math.Max( 1, EnemiesPerExtraAttacker ) );

	int StandingCount => enemies.Count( e => e.IsValid() && e.WantsSlot );

	readonly List<Enemy> enemies = new();
	readonly List<Enemy> attackers = new();
	readonly Dictionary<Enemy, float> slotAngles = new();

	RealTimeSince timeSinceAssign;
	RealTimeSince timeSinceGrant;

	Enemy nextInLine;
	float nextInLineAt = -1f;

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

	/// <summary>Ask for a turn to attack. False means hold back and wait.</summary>
	public bool RequestAttack( Enemy enemy )
	{
		Prune();

		if ( attackers.Contains( enemy ) )
			return true;

		if ( attackers.Count >= MaxAttackers || timeSinceGrant < AttackSpacing )
			return false;

		// a free turn goes to whoever deserves it most this tick, not whoever's update happened to run
		// first — otherwise the order they were spawned in would decide who attacks
		if ( enemy != NextInLine() )
			return false;

		attackers.Add( enemy );
		timeSinceGrant = 0;
		return true;
	}

	public void ReleaseAttack( Enemy enemy ) => attackers.Remove( enemy );

	public bool IsAttacking( Enemy enemy ) => attackers.Contains( enemy );

	/// <summary>Where this enemy should stand while he waits: his bearing, at the distance he's chosen.</summary>
	public Vector3 GetSlotPosition( Enemy enemy, Vector3 target, float radius )
	{
		Assign( target );

		float angle = slotAngles.TryGetValue( enemy, out var a ) ? a : AngleOf( target, enemy.WorldPosition );
		return target + Rotation.FromYaw( angle ).Forward * radius;
	}

	/// <summary>
	/// The waiting man most deserving of the next turn — mostly whoever is in front of the camera, then
	/// whoever is closest. Worked out once per physics step and shared by everyone who asks in it.
	/// </summary>
	Enemy NextInLine()
	{
		if ( nextInLineAt == Time.Now )
			return nextInLine;

		nextInLineAt = Time.Now;
		nextInLine = null;

		var camera = Scene.Camera;
		bool hasView = camera.IsValid();
		var eye = hasView ? camera.WorldPosition : Vector3.Zero;
		var view = hasView ? camera.WorldRotation.Forward.WithZ( 0 ).Normal : Vector3.Zero;

		float best = float.MinValue;

		foreach ( var enemy in enemies )
		{
			if ( !enemy.IsValid() || !enemy.WantsTurn || attackers.Contains( enemy ) )
				continue;

			var from = hasView ? eye : enemy.Target.WorldPosition;
			var to = ( enemy.WorldPosition - from ).WithZ( 0 );
			float distance = to.Length;

			// 1 dead ahead, 0 directly behind, and a neutral half when there's no camera to judge by
			float inView = !hasView || view.IsNearlyZero() || distance < 0.01f
				? 0.5f
				: ( Vector3.Dot( view, to / distance ) + 1f ) * 0.5f;

			float near = 1f - ( distance / MathF.Max( enemy.BandMax, 1f ) ).Clamp( 0f, 1f );
			float score = inView * InViewPriority + near * ( 1f - InViewPriority );

			if ( score > best )
			{
				best = score;
				nextInLine = enemy;
			}
		}

		return nextInLine;
	}

	void Assign( Vector3 target )
	{
		if ( timeSinceAssign < ReassignInterval )
			return;

		timeSinceAssign = 0;
		Prune();

		// only the ones actually on their feet get a bearing — a staggered man on the floor shouldn't be
		// holding a place that someone upright could be using
		var active = enemies.Where( e => e.IsValid() && e.WantsSlot ).ToList();

		if ( active.Count == 0 )
			return;

		// sorted by where each already stands, so bearings are handed out in the order they happen to be
		// arranged around him and nobody has to walk through the middle to reach theirs
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
