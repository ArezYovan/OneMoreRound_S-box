using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Patrons coming through the door. A wave is a total for the round and a cap on how many are up at
/// once — as one goes down, the next walks in. Dumping a whole wave in at once turns the director's
/// ring into noise past about six men, and a bar fills up through its doors; it doesn't materialise.
///
/// Spawn points are this object's children: drop empties at the doors and they're used, with no list
/// to keep in sync.
///
/// It counts its OWN list, never EnemyDirector's. An enemy only registers with the director in its
/// first OnStart, a frame after it's cloned, so for that frame the director reads nobody standing and a
/// wave would count as cleared the moment it began.
/// </summary>
public sealed class WaveSpawner : Component
{
	[Property, Group( "Enemies" ), Description( "The enemy prefab to clone. It needs Enemy and Health on its root." )]
	public GameObject EnemyPrefab { get; set; }

	[Property, Group( "Wave Size" ), Description( "Men in round one." )]
	public int BaseEnemies { get; set; } = 3;

	[Property, Group( "Wave Size" ), Description( "Extra men per round after the first. Fractions build up: 1.5 adds three men every two rounds." )]
	public float EnemiesPerRound { get; set; } = 1f;

	[Property, Group( "Wave Size" ), Description( "Most on their feet at once. The director only lets two swing at a time anyway, and past five or six the ring around you stops being readable." )]
	public int MaxAlive { get; set; } = 5;

	[Property, Group( "Arrivals" ), Description( "Least time between two arrivals, so a door can't spit out three men on the same frame." )]
	public float SpawnInterval { get; set; } = 0.75f;

	[Property, Group( "Arrivals" ), Description( "Doors closer to the player than this are skipped, so nobody appears at your elbow. If every door is that close, the farthest one is used." )]
	public float MinPlayerDistance { get; set; } = 300f;

	[Property, Group( "Arrivals" ), Description( "A door with someone still standing this close to it waits. Two rigidbodies cloned inside each other fling one another across the room." )]
	public float DoorClearance { get; set; } = 60f;

	[Property, Group( "Arrivals" ), Description( "Anyone below this height is killed, so a man knocked out of the map can't hold a round open forever." )]
	public float KillHeight { get; set; } = -1000f;

	[Property, Group( "Debug" ), Description( "Switch off the state, damage and stagger logging on everyone spawned. A full room of them buries the console — turn this off when you need to watch one." )]
	public bool QuietEnemies { get; set; } = true;

	/// <summary>Men in the current wave. Zero between waves.</summary>
	public int WaveTotal { get; private set; }

	/// <summary>How many of the wave have come through the door so far.</summary>
	public int SpawnedCount { get; private set; }

	public int AliveCount => spawned.Count( IsAlive );

	/// <summary>Still to beat: everyone standing, plus everyone who hasn't arrived yet.</summary>
	public int Remaining => Math.Max( 0, WaveTotal - SpawnedCount + AliveCount );

	/// <summary>The whole wave has arrived and nobody is left standing.</summary>
	public bool IsCleared => WaveTotal > 0 && SpawnedCount >= WaveTotal && AliveCount == 0;

	/// <summary>Fired once for each man this spawner brought in, the moment he dies, whoever did it. The run pays Kill Bounty off it.</summary>
	public Action<Enemy> OnEnemyDied;

	readonly record struct Arrival( Enemy Enemy, Health Health );

	readonly List<Arrival> spawned = new();
	bool spawning;
	TimeSince timeSinceSpawn;

	/// <summary>How big a round's wave is. Public so the HUD can announce it before the first man arrives.</summary>
	public int WaveSizeFor( int round ) =>
		Math.Max( 1, BaseEnemies + (int)MathF.Floor( EnemiesPerRound * Math.Max( 0, round - 1 ) ) );

	/// <summary>Start letting this round's wave in. Anything left from an earlier wave is cleared first.</summary>
	public void BeginWave( int round )
	{
		Clear();

		WaveTotal = WaveSizeFor( round );
		spawning = true;

		// the first man is due at once; the interval only spaces out the ones after him
		timeSinceSpawn = SpawnInterval;

		if ( !EnemyPrefab.IsValid() )
			Log.Warning( $"[Wave] {GameObject.Name} has no EnemyPrefab — round {round} will never clear on its own. L skips it." );
	}

	/// <summary>No more arrivals. Whoever is already in the room stays until Clear.</summary>
	public void StopWave() => spawning = false;

	/// <summary>
	/// Everyone standing drops and the rest of the wave never arrives. The debug skip — the clear then
	/// lands through the normal path, payout and all.
	/// </summary>
	public void FinishWave()
	{
		spawning = false;
		SpawnedCount = WaveTotal;

		foreach ( var arrival in spawned )
		{
			if ( IsAlive( arrival ) )
				TakeDown( arrival );
		}
	}

	/// <summary>Removes everyone this spawner brought in, standing or not, so corpses don't pile up round after round.</summary>
	public void Clear()
	{
		spawning = false;

		foreach ( var arrival in spawned )
		{
			if ( arrival.Enemy.IsValid() )
				arrival.Enemy.GameObject.Destroy();
		}

		spawned.Clear();
		WaveTotal = 0;
		SpawnedCount = 0;
	}

	protected override void OnFixedUpdate()
	{
		CullFallen();

		if ( !spawning || SpawnedCount >= WaveTotal )
			return;

		if ( AliveCount >= MaxAlive || timeSinceSpawn < SpawnInterval )
			return;

		var door = PickDoor();

		// every door has someone standing in it — try again next tick rather than stacking bodies
		if ( !door.IsValid() )
			return;

		Spawn( door );
	}

	void Spawn( GameObject door )
	{
		if ( !EnemyPrefab.IsValid() )
		{
			spawning = false;
			return;
		}

		var go = EnemyPrefab.Clone( door.WorldPosition, door.WorldRotation );
		SpawnedCount++;
		timeSinceSpawn = 0;
		go.Name = $"Patron {SpawnedCount}";

		var enemy = go.Components.Get<Enemy>();
		var hp = go.Components.Get<Health>();

		if ( !enemy.IsValid() )
		{
			// still counted as arrived, so a broken prefab can't hold the round open forever
			Log.Warning( $"[Wave] {EnemyPrefab.Name} has no Enemy on its root — removed it." );
			go.Destroy();
			return;
		}

		if ( QuietEnemies )
			Quiet( go, enemy, hp );

		// Health only ever dies once, so this fires once a man however many things hit the corpse after
		if ( hp.IsValid() )
			hp.OnDied += () => OnEnemyDied?.Invoke( enemy );

		spawned.Add( new Arrival( enemy, hp ) );
	}

	/// <summary>
	/// A random door far enough from the player with nobody standing in it. Falls back to the farthest
	/// door if they're all too close, and returns null if every door is occupied.
	/// </summary>
	GameObject PickDoor()
	{
		var doors = GameObject.Children.Where( d => d.IsValid() && d.Enabled ).ToList();

		// no children: this object is the only door
		if ( doors.Count == 0 )
			doors.Add( GameObject );

		doors.RemoveAll( IsOccupied );

		if ( doors.Count == 0 )
			return null;

		var player = Scene.GetAllComponents<RBPlayerMovement>().FirstOrDefault();

		if ( !player.IsValid() )
			return doors[Game.Random.Next( doors.Count )];

		var playerPos = player.WorldPosition;
		var far = doors.Where( d => Vector3.DistanceBetween( d.WorldPosition, playerPos ) >= MinPlayerDistance ).ToList();

		return far.Count > 0
			? far[Game.Random.Next( far.Count )]
			: doors.OrderByDescending( d => Vector3.DistanceBetween( d.WorldPosition, playerPos ) ).First();
	}

	bool IsOccupied( GameObject door ) => spawned.Any( a =>
		IsAlive( a ) && Vector3.DistanceBetween( a.Enemy.WorldPosition, door.WorldPosition ) < DoorClearance );

	/// <summary>Anyone standing who has fallen below KillHeight goes down.</summary>
	void CullFallen()
	{
		foreach ( var arrival in spawned )
		{
			if ( IsAlive( arrival ) && arrival.Enemy.WorldPosition.z < KillHeight )
				TakeDown( arrival );
		}
	}

	/// <summary>
	/// Takes one man out of the fight. Killed through Health where possible, so he ragdolls and reads as a
	/// knockout — but a man who hasn't reached his first OnStart hasn't subscribed to his own death yet,
	/// and killing him would leave someone with no health walking around. He's simply removed instead.
	/// </summary>
	static void TakeDown( Arrival arrival )
	{
		bool started = arrival.Enemy.State != EnemyState.Idle;

		if ( started && arrival.Health.IsValid() )
			arrival.Health.Kill();
		else
			arrival.Enemy.GameObject.Destroy();
	}

	static bool IsAlive( Arrival arrival )
	{
		if ( !arrival.Enemy.IsValid() || arrival.Enemy.State == EnemyState.Dead )
			return false;

		// the Health check catches a man killed before his Enemy had started and heard about it
		return !arrival.Health.IsValid() || !arrival.Health.IsDead;
	}

	static void Quiet( GameObject go, Enemy enemy, Health hp )
	{
		enemy.LogState = false;

		if ( hp.IsValid() )
			hp.LogDamage = false;

		var stagger = go.Components.Get<Stagger>();

		if ( stagger.IsValid() )
			stagger.LogStagger = false;
	}
}
