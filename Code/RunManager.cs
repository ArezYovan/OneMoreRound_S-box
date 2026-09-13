using System;
using System.Linq;

/// <summary>Where the night is. The HUD, and later the shop, key everything off this.</summary>
public enum RunPhase
{
	/// <summary>Nothing has started yet.</summary>
	Idle,

	/// <summary>The countdown before a wave, with the player held in place.</summary>
	Starting,

	Fighting,

	/// <summary>A beat after clearing a wave, while the payout is on screen.</summary>
	Paid,

	/// <summary>A beat after a knockout. The round is lost and pays nothing.</summary>
	Down,

	/// <summary>Between rounds. A stand-in until the shop panel exists: Space moves on, E walks out.</summary>
	Shop,

	Won,
	Banned
}

/// <summary>
/// The night: you owe the house a tab, and you have ten rounds to earn it.
///
/// This owns the flow from phase to phase and the money, and deliberately nothing else. Who comes
/// through the door is the WaveSpawner's business, what a drink does to your stats will be the
/// upgrades', and the HUD only ever reads what's here. Held to that, it stays the thin spine the rest
/// of the run hangs off, instead of becoming the file everything has to edit.
/// </summary>
public sealed class RunManager : Component
{
	[Property, Group( "Night" ), Description( "Rounds before closing time. End the last one short of the tab and you're banned." )]
	public int Rounds { get; set; } = 10;

	[Property, Group( "Night" ), Description( "What you owe the house. The biggest difficulty dial in the game: round count, wave size and drink prices all end up measured against this one number." )]
	public int Tab { get; set; } = 500;

	[Property, Group( "Night" ), Description( "Begin the night as soon as the scene loads. Off, something has to call StartNight — a main menu, eventually." )]
	public bool StartOnLoad { get; set; } = true;

	[Property, Group( "Payout" ), Description( "What clearing round one pays, before any speed bonus." )]
	public int BasePay { get; set; } = 30;

	[Property, Group( "Payout" ), Description( "Added for each round after the first, so the later, bigger waves are worth more than the early ones." )]
	public int PayPerRound { get; set; } = 5;

	[Property, Group( "Payout" ), Description( "The most a clear can earn on top for speed. A clear in no time gets all of it, one at par or slower gets none, and in between it falls off in a straight line." )]
	public int SpeedBonus { get; set; } = 40;

	[Property, Group( "Payout" ), Description( "Par time in seconds for each man in the wave. Set per enemy so the speed bonus asks the same pace of a three-man round as a twelve-man one." )]
	public float ParSecondsPerEnemy { get; set; } = 10f;

	[Property, Group( "Beats" ), Description( "Seconds held in place before each wave, so a round never starts with you mid-stride." )]
	public float StartDelay { get; set; } = 2f;

	[Property, Group( "Beats" ), Description( "How long a clear and its payout stay on screen before the bar." )]
	public float PaidDelay { get; set; } = 2.5f;

	[Property, Group( "Beats" ), Description( "Seconds on the floor after a knockout. This replaces PlayerHealth's own revive timer — the run decides when you get up now." )]
	public float DownDelay { get; set; } = 3f;

	[Property, Group( "References" ), Description( "Optional, found automatically in the scene." )]
	public WaveSpawner Spawner { get; set; }

	[Property, Group( "References" ), Description( "Optional, found automatically from whatever has RBPlayerMovement on it." )]
	public GameObject Player { get; set; }

	[Property, Group( "References" ), Description( "Optional. Put the player back here at the start of every round. Empty, each round starts wherever you're standing." )]
	public GameObject PlayerStart { get; set; }

	[Property, Group( "Debug" ), Description( "F1 finishes the current wave, F2 adds $100, F3 restarts the night. Switch off for a build." )]
	public bool DebugKeys { get; set; } = true;

	[Property, Group( "Debug" )] public bool LogRun { get; set; } = true;

	public RunPhase Phase { get; private set; } = RunPhase.Idle;

	/// <summary>Counts from 1. Zero until the first round begins.</summary>
	public int Round { get; private set; }

	public int Money { get; private set; }

	/// <summary>What the last round paid. Zero after a knockout.</summary>
	public int LastPayout { get; private set; }

	public bool IsCovered => Money >= Tab;
	public int Short => Math.Max( 0, Tab - Money );
	public bool IsLastRound => Round >= Rounds;

	/// <summary>Seconds into the current wave, or how long the last one took once it's over.</summary>
	public float RoundTime => Phase == RunPhase.Fighting ? timeInPhase : lastRoundTime;

	public float TimeInPhase => timeInPhase;

	/// <summary>Code-side hooks for whatever needs to react to the night — the shop, once it exists.</summary>
	public Action<RunPhase> OnPhaseChanged;
	public Action<int> OnMoneyChanged;

	RBPlayerMovement movement;
	PlayerHealth playerHealth;
	Health health;
	Stamina stamina;
	Rigidbody playerBody;

	TimeSince timeInPhase;
	float lastRoundTime;

	// FreezeFor takes a duration, so "until told otherwise" is just a very long one
	const float HoldIndefinitely = 60f * 60f * 24f;

	protected override void OnStart()
	{
		Spawner ??= Scene.GetAllComponents<WaveSpawner>().FirstOrDefault();
		Player ??= Scene.GetAllComponents<RBPlayerMovement>().FirstOrDefault()?.GameObject;

		if ( !Spawner.IsValid() )
			Log.Warning( "[Run] no WaveSpawner in the scene — rounds will start and nobody will come through the door. F1 skips a wave." );

		if ( Player.IsValid() )
		{
			var search = FindMode.Enabled | FindMode.InSelf | FindMode.InDescendants;

			movement = Player.Components.Get<RBPlayerMovement>( search );
			playerHealth = Player.Components.Get<PlayerHealth>( search );
			health = Player.Components.Get<Health>( search );
			stamina = Player.Components.Get<Stamina>( search );
			playerBody = Player.Components.Get<Rigidbody>( search );
		}
		else
		{
			Log.Warning( "[Run] found no player (nothing with RBPlayerMovement) — there's nobody to run a night for." );
		}

		if ( playerHealth.IsValid() )
		{
			// left on, PlayerHealth would stand you back up on its own clock — in the middle of the bar,
			// or on the verdict screen. Getting up is the run's call now
			playerHealth.ReviveAfterKnockout = false;
			playerHealth.OnKnockedOut += OnPlayerKnockedOut;
		}

		if ( StartOnLoad )
			StartNight();
	}

	protected override void OnDestroy()
	{
		if ( playerHealth.IsValid() )
			playerHealth.OnKnockedOut -= OnPlayerKnockedOut;
	}

	protected override void OnUpdate()
	{
		if ( DebugKeys )
			UpdateDebugKeys();

		switch ( Phase )
		{
			case RunPhase.Starting:
				if ( timeInPhase >= StartDelay )
					BeginWave();
				break;

			case RunPhase.Fighting:
				if ( Spawner.IsValid() && Spawner.IsCleared )
					ClearRound();
				break;

			case RunPhase.Paid:
				if ( timeInPhase >= PaidDelay )
					AfterRound();
				break;

			case RunPhase.Down:
				if ( timeInPhase >= DownDelay )
					AfterRound();
				break;

			// stand-in controls until the shop panel exists. The player is held here, so the Space that
			// leaves the bar is eaten by the freeze instead of also firing a jump
			case RunPhase.Shop:
				if ( Input.Pressed( "Jump" ) )
					NextRound();
				else if ( Input.Pressed( "Use" ) )
					WalkOut();
				break;

			case RunPhase.Won:
			case RunPhase.Banned:
				if ( Input.Pressed( "Jump" ) )
					StartNight();
				break;
		}
	}

	/// <summary>A fresh night, reset in place: no money, round zero, nobody in the bar.</summary>
	public void StartNight()
	{
		Round = 0;
		LastPayout = 0;
		lastRoundTime = 0f;
		SetMoney( 0 );

		if ( Spawner.IsValid() )
			Spawner.Clear();

		Report( $"a new night — ${Tab} owed, {Rounds} rounds to earn it" );

		NextRound();
	}

	/// <summary>Out of the bar and into the next wave: patched up, put back at the start, held for the countdown.</summary>
	public void NextRound()
	{
		// the last round closes the night from AfterRound, so arriving here past it means a caller got the
		// order wrong — end the night rather than play an eleventh round
		if ( IsLastRound && Round > 0 )
		{
			EndNight();
			return;
		}

		Round++;

		if ( Spawner.IsValid() )
			Spawner.Clear();

		ResetPlayer();
		HoldPlayer( true );
		Enter( RunPhase.Starting );
	}

	void BeginWave()
	{
		HoldPlayer( false );

		if ( Spawner.IsValid() )
			Spawner.BeginWave( Round );

		Report( $"round {Round} — {( Spawner.IsValid() ? Spawner.WaveTotal : 0 )} coming through the door" );
		Enter( RunPhase.Fighting );
	}

	void ClearRound()
	{
		lastRoundTime = timeInPhase;
		LastPayout = PayoutFor( Round, lastRoundTime );

		if ( Spawner.IsValid() )
			Spawner.StopWave();

		SetMoney( Money + LastPayout );
		Report( $"round {Round} cleared in {lastRoundTime:0.0}s — paid ${LastPayout}, ${Money} of ${Tab}" );
		Enter( RunPhase.Paid );
	}

	void OnPlayerKnockedOut()
	{
		// only a knockout in the fight costs the round. One from a stray swing during the countdown or in
		// the bar just gets you stood back up with everything else
		if ( Phase != RunPhase.Fighting )
			return;

		lastRoundTime = timeInPhase;
		LastPayout = 0;

		if ( Spawner.IsValid() )
			Spawner.StopWave();

		Report( $"knocked out in round {Round} — it pays nothing" );
		Enter( RunPhase.Down );
	}

	/// <summary>Where a round goes once its beat is done: the verdict after the last one, the bar otherwise.</summary>
	void AfterRound()
	{
		if ( Spawner.IsValid() )
			Spawner.Clear();

		// up off the floor now rather than at the next countdown, so nobody shops lying down
		ResetPlayer();

		if ( IsLastRound )
		{
			EndNight();
			return;
		}

		HoldPlayer( true );
		Enter( RunPhase.Shop );
	}

	/// <summary>
	/// Settle up and leave — the only way to win before closing time, and only once you can cover the
	/// tab. Returns false, and changes nothing, if you can't.
	/// </summary>
	public bool WalkOut()
	{
		if ( Phase != RunPhase.Shop || !IsCovered )
			return false;

		Report( $"walked out after round {Round} with ${Money} — tab settled" );
		Finish( RunPhase.Won );
		return true;
	}

	void EndNight()
	{
		Report( IsCovered
			? $"closing time — ${Money} covers the ${Tab} tab"
			: $"closing time — ${Money} of ${Tab}. Banned" );

		Finish( IsCovered ? RunPhase.Won : RunPhase.Banned );
	}

	void Finish( RunPhase verdict )
	{
		if ( Spawner.IsValid() )
			Spawner.Clear();

		HoldPlayer( true );
		Enter( verdict );
	}

	/// <summary>
	/// What clearing this round in this many seconds pays. Base pay climbs with the round; the speed
	/// bonus is the full amount for an instant clear, nothing at par, and a straight line between.
	/// </summary>
	public int PayoutFor( int round, float clearSeconds )
	{
		int basePay = BasePay + PayPerRound * Math.Max( 0, round - 1 );
		int enemies = Spawner.IsValid() ? Spawner.WaveSizeFor( round ) : 0;
		float par = MathF.Max( 1f, ParSecondsPerEnemy * enemies );
		float speed = ( 1f - clearSeconds / par ).Clamp( 0f, 1f );

		return basePay + (int)MathF.Round( SpeedBonus * speed );
	}

	/// <summary>Spend if you can afford it; false, and nothing spent, if you can't. For the shop.</summary>
	public bool TrySpend( int amount )
	{
		if ( amount < 0 || Money < amount )
			return false;

		SetMoney( Money - amount );
		return true;
	}

	public void AddMoney( int amount ) => SetMoney( Money + amount );

	void SetMoney( int value )
	{
		if ( Money == value )
			return;

		Money = value;
		OnMoneyChanged?.Invoke( Money );
	}

	void Enter( RunPhase phase )
	{
		Phase = phase;
		timeInPhase = 0;
		OnPhaseChanged?.Invoke( phase );
	}

	/// <summary>
	/// Health and wind back to full, off the floor, and back to the start point if there is one. Health
	/// resetting every round is the design, not a convenience: the round is the test, the tab is the run.
	/// </summary>
	void ResetPlayer()
	{
		// PlayerHealth has to do the reviving when you're down — reviving Health directly would leave it
		// still believing you're knocked out
		if ( playerHealth.IsValid() && playerHealth.IsKnockedOut )
			playerHealth.Revive();
		else if ( health.IsValid() )
			health.Revive();

		if ( stamina.IsValid() )
			stamina.Refill();

		if ( !PlayerStart.IsValid() || !Player.IsValid() )
			return;

		Player.WorldPosition = PlayerStart.WorldPosition;

		if ( playerBody.IsValid() )
		{
			playerBody.Velocity = Vector3.Zero;
			playerBody.AngularVelocity = Vector3.Zero;
		}

		// facing lives on the head, which CameraMovement rebuilds the aim from every frame — turning the
		// root would do nothing, its rotation is locked
		if ( movement.IsValid() && movement.Head.IsValid() )
			movement.Head.WorldRotation = Rotation.FromYaw( PlayerStart.WorldRotation.Angles().yaw );
	}

	/// <summary>
	/// Holds the player through RBPlayerMovement's freeze. That's the one gate movement, every ability,
	/// punching and grabbing all check, so holding here stops all of it at once.
	/// </summary>
	void HoldPlayer( bool hold )
	{
		if ( movement.IsValid() )
			movement.FreezeFor( hold ? HoldIndefinitely : 0f );
	}

	void UpdateDebugKeys()
	{
		if ( Input.Keyboard.Pressed( "F1" ) && Phase == RunPhase.Fighting )
		{
			Report( "debug — wave finished" );

			// the clear then lands through the normal path next frame, payout and all
			if ( Spawner.IsValid() )
				Spawner.FinishWave();
			else
				ClearRound();
		}

		if ( Input.Keyboard.Pressed( "F2" ) )
		{
			AddMoney( 100 );
			Report( $"debug — +$100, ${Money} of ${Tab}" );
		}

		if ( Input.Keyboard.Pressed( "F3" ) )
		{
			Report( "debug — restarting the night" );
			StartNight();
		}
	}

	void Report( string message )
	{
		if ( LogRun )
			Log.Info( $"[Run] {message}" );
	}
}
