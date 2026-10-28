using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// The thing that stops a crowd behaving like one animal with twenty bodies.
///
/// It keeps the room in three tiers, and only the inner two are ever near you:
///
/// Attackers — at most <see cref="MaxAttackers"/> may actually swing at any moment, and the next turn goes to
/// someone you can see before someone behind you. A hit from a man you never saw isn't a challenge, it's unfair.
///
/// Circlers — at most <see cref="MaxCirclers"/> hold a bearing around you, stalking in their band while they
/// wait for a turn. Bearings go out in the order they already stand around you, so nobody crosses the room to
/// swap places with someone else.
///
/// Reserves — everyone else. They keep their distance on posts spread over the map (see Enemy) and wait to be
/// called up, so a full room is a handful of men on you and a threat from the rest rather than one scrum.
///
/// The roster follows you. A circler left far behind is handed back to the reserves and the nearest reserve
/// takes his place, so running away doesn't drag a growing train after you — it runs you into the next men,
/// and the fight re-forms wherever you are.
///
/// A GameObjectSystem rather than a component, so there's nothing to place in the scene and no way to end up
/// with two of them. Its tuning lives here in code for the same reason.
/// </summary>
public sealed partial class EnemyDirector : GameObjectSystem<EnemyDirector>
{
	public EnemyDirector( Scene scene ) : base( scene ) { }

	/// <summary>Most men circling you at once, attackers included. Everyone past this waits in reserve.</summary>
	public int MaxCirclers { get; set; } = 4;

	/// <summary>Most men swinging at once. One or two is a fight you can read; past that the windups blur into noise.</summary>
	public int MaxAttackers { get; set; } = 1;

	/// <summary>Minimum gap between handing out turns, so attacks arrive in quick succession rather than as one volley.</summary>
	public float AttackSpacing { get; set; } = 1f;

	/// <summary>
	/// How much being in front of the camera counts toward the next turn, against simply being close (0–1).
	/// Someone behind you still gets a turn when there's nobody better placed — just later.
	/// </summary>
	public float InViewPriority { get; set; } = 0.65f;

	/// <summary>
	/// The same trade-off for calling up a reserve, weighted the other way: mostly whoever is closest, so a gap in
	/// the ring fills fast, with a little preference for men arriving where you can see them.
	/// </summary>
	public float CallUpViewPriority { get; set; } = 0.25f;

	/// <summary>A circler farther than this from you can be handed back to the reserves.</summary>
	public float LeashDistance { get; set; } = 1200f;

	/// <summary>
	/// ...but only in exchange for a reserve at least this much closer to you than he is. Without the margin two
	/// men at nearly the same distance would trade places every time the roster is looked at.
	/// </summary>
	public float SwapMargin { get; set; } = 400f;

	/// <summary>Least time a man circles before he can be handed back, so someone just called up isn't sent away before he arrives.</summary>
	public float MinCircleTime { get; set; } = 2f;

	/// <summary>How often the roster and the bearings are worked out again. Often enough to follow the player, rarely enough not to jitter.</summary>
	public float ReassignInterval { get; set; } = 0.25f;

	public IReadOnlyList<Enemy> Enemies => enemies;
	public int AttackerCount => attackers.Count;
	public int CirclerCount => circlers.Count;

	readonly List<Enemy> enemies = new();
	readonly List<Enemy> circlers = new();
	readonly List<Enemy> attackers = new();
	readonly Dictionary<Enemy, float> slotAngles = new();
	readonly Dictionary<Enemy, float> circlingSince = new();

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
		Dismiss( enemy );
		ReleaseThrowJob( enemy );
	}

	/// <summary>Whether this man is one of the few allowed to circle the player right now. Everyone else is a reserve.</summary>
	public bool IsCircler( Enemy enemy, Vector3 target )
	{
		Refresh( target );
		return circlers.Contains( enemy );
	}

	/// <summary>Ask for a turn to attack. False means hold back and wait — and only a circler ever gets one.</summary>
	public bool RequestAttack( Enemy enemy )
	{
		Prune();

		if ( attackers.Contains( enemy ) )
			return true;

		if ( !circlers.Contains( enemy ) || attackers.Count >= MaxAttackers || timeSinceGrant < AttackSpacing )
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

	/// <summary>Where this circler should stand while he waits: his bearing, at the distance he's chosen.</summary>
	public Vector3 GetSlotPosition( Enemy enemy, Vector3 target, float radius )
	{
		Refresh( target );

		float angle = slotAngles.TryGetValue( enemy, out var a ) ? a : AngleOf( target, enemy.WorldPosition );
		return target + Rotation.FromYaw( angle ).Forward * radius;
	}

	/// <summary>Whether another reserve already holds a post within <paramref name="spacing"/> of this point.</summary>
	public bool IsPostTaken( Enemy asking, Vector3 point, float spacing )
	{
		foreach ( var other in enemies )
		{
			if ( other == asking || !other.IsValid() || other.Post is not Vector3 post )
				continue;

			if ( ( post - point ).WithZ( 0 ).Length < spacing )
				return true;
		}

		return false;
	}

	/// <summary>
	/// The waiting circler most deserving of the next turn. Worked out once per physics step and shared by
	/// everyone who asks in it.
	/// </summary>
	Enemy NextInLine()
	{
		if ( nextInLineAt == Time.Now )
			return nextInLine;

		nextInLineAt = Time.Now;
		nextInLine = null;

		float best = float.MinValue;

		foreach ( var enemy in circlers )
		{
			if ( !enemy.IsValid() || !enemy.WantsTurn || attackers.Contains( enemy ) )
				continue;

			float score = Placement( enemy, enemy.Target.WorldPosition, enemy.BandMax, InViewPriority );

			if ( score > best )
			{
				best = score;
				nextInLine = enemy;
			}
		}

		return nextInLine;
	}

	void Refresh( Vector3 target )
	{
		if ( timeSinceAssign < ReassignInterval )
			return;

		timeSinceAssign = 0;
		Prune();

		FillRing( target );
		AssignBearings( target );
	}

	/// <summary>
	/// Keeps the ring full and keeps it near the player. Open places go to the best-placed reserves, and one
	/// straggler per pass is swapped for a reserve who is closer — one at a time, so the ring drifts after you
	/// instead of lurching.
	/// </summary>
	void FillRing( Vector3 target )
	{
		var reserves = enemies.Where( e => e.IsValid() && e.CanCircle && !circlers.Contains( e ) ).ToList();

		if ( reserves.Count == 0 )
			return;

		float range = reserves.Max( e => FlatDistance( e, target ) );

		while ( circlers.Count < MaxCirclers && reserves.Count > 0 )
		{
			var best = reserves.OrderByDescending( e => Placement( e, target, range, CallUpViewPriority ) ).First();

			reserves.Remove( best );
			CallUp( best );
		}

		if ( reserves.Count == 0 )
			return;

		// the handoff. Never someone mid-attack, and never someone who hasn't had the time to arrive yet
		var straggler = circlers
			.Where( e => !attackers.Contains( e ) && Time.Now - circlingSince.GetValueOrDefault( e ) >= MinCircleTime )
			.OrderByDescending( e => FlatDistance( e, target ) )
			.FirstOrDefault();

		if ( straggler is null )
			return;

		float stragglerDistance = FlatDistance( straggler, target );
		var closest = reserves.OrderBy( e => FlatDistance( e, target ) ).First();

		if ( stragglerDistance < LeashDistance || FlatDistance( closest, target ) > stragglerDistance - SwapMargin )
			return;

		Dismiss( straggler );
		CallUp( closest );
	}

	void CallUp( Enemy enemy )
	{
		circlers.Add( enemy );
		circlingSince[enemy] = Time.Now;
	}

	void Dismiss( Enemy enemy )
	{
		circlers.Remove( enemy );
		circlingSince.Remove( enemy );
		slotAngles.Remove( enemy );
	}

	/// <summary>Bearings for the circlers only — reserves have posts of their own and don't take up a place in the ring.</summary>
	void AssignBearings( Vector3 target )
	{
		if ( circlers.Count == 0 )
			return;

		// sorted by where each already stands, so bearings are handed out in the order they happen to be
		// arranged around him and nobody has to walk through the middle to reach theirs
		var ordered = circlers.OrderBy( e => AngleOf( target, e.WorldPosition ) ).ToList();

		float step = 360f / ordered.Count;
		float anchor = AngleOf( target, ordered[0].WorldPosition );

		for ( int i = 0; i < ordered.Count; i++ )
			slotAngles[ordered[i]] = anchor + step * i;
	}

	/// <summary>
	/// How well placed a man is: part whether he's in front of the camera, part how close he is, weighted by
	/// <paramref name="viewPriority"/>. <paramref name="range"/> is the distance that counts as far.
	/// </summary>
	float Placement( Enemy enemy, Vector3 target, float range, float viewPriority )
	{
		var camera = Scene.Camera;
		bool hasView = camera.IsValid();
		var from = hasView ? camera.WorldPosition : target;
		var view = hasView ? camera.WorldRotation.Forward.WithZ( 0 ).Normal : Vector3.Zero;

		var to = ( enemy.WorldPosition - from ).WithZ( 0 );
		float distance = to.Length;

		// 1 dead ahead, 0 directly behind, and a neutral half when there's no camera to judge by
		float inView = view.IsNearlyZero() || distance < 0.01f
			? 0.5f
			: ( Vector3.Dot( view, to / distance ) + 1f ) * 0.5f;

		float near = 1f - ( distance / MathF.Max( range, 1f ) ).Clamp( 0f, 1f );

		return inView * viewPriority + near * ( 1f - viewPriority );
	}

	void Prune()
	{
		enemies.RemoveAll( e => !e.IsValid() );
		attackers.RemoveAll( e => !e.IsValid() || !e.WantsSlot );

		// a man on the floor, being carried, or turned into a practice dummy gives up his place in the ring.
		// He's a reserve again once he's up, and has to be called back like anyone else
		foreach ( var gone in circlers.Where( e => !e.IsValid() || !e.CanCircle ).ToList() )
			Dismiss( gone );
	}

	static float FlatDistance( Enemy enemy, Vector3 target ) => ( enemy.WorldPosition - target ).WithZ( 0 ).Length;

	static float AngleOf( Vector3 from, Vector3 to )
	{
		var d = ( to - from ).WithZ( 0 );
		return MathF.Atan2( d.y, d.x ) * ( 180f / MathF.PI );
	}
}
