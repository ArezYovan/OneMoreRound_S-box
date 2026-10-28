using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>Where the night is. The HUD and the bar key everything off this.</summary>
public enum RunPhase
{
	/// <summary>Nothing has started yet.</summary>
	Idle,

	/// <summary>The countdown before a wave, with the player held in place.</summary>
	Starting,

	Fighting,

	/// <summary>A beat after clearing a wave, while the payout is on screen.</summary>
	Paid,

	/// <summary>A beat after a knockout. The night is over and scores nothing.</summary>
	Down,

	/// <summary>
	/// At the bar, before the drinks come out: one more round, or walk out on what you've got? Everything
	/// in the game hangs off this one question, so it gets a phase of its own rather than a corner of the
	/// shop — the bottles don't arrive until it's answered.
	/// </summary>
	Deciding,

	/// <summary>Between rounds, drinks on the counter.</summary>
	Shop,

	/// <summary>The night is finished, one way or the other. <see cref="Survived"/> says which.</summary>
	Over
}

/// <summary>What the drinker has said to "one more round?".</summary>
public enum RoundVote
{
	Undecided,
	Yes,
	No
}

/// <summary>
/// The night: how far into it can you get before you quit while you're ahead?
///
/// There's no tab and no last round. Rounds go up forever, each one bigger than the last, and the only
/// question the game ever really asks is at the bar between them: one more round, or walk out and put the
/// round you reached on the board? Push on and clear it and the number goes up. Push on and go down and
/// you score NOTHING — not the round you died on, not the seven you already cleared. That asymmetry is the
/// game; everything else here exists to serve it.
///
/// Money survives with a smaller job: it buys drinks and nothing else.
///
/// This owns the flow from phase to phase and the money, and deliberately nothing else. Who comes through
/// the door is the WaveSpawner's business, what a drink does to your stats is the upgrades', and the HUD
/// only ever reads what's here. Held to that, it stays the thin spine the rest of the run hangs off,
/// instead of becoming the file everything has to edit.
/// </summary>
public sealed class RunManager : Component
{
	[Property, Group( "Night" ), Description( "Begin the night as soon as the scene loads. Off, something has to call StartNight — the main menu." )]
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

	[Property, Group( "Beats" ), Description( "How long a clear and its payout stay on screen before the bar, in real seconds — the finishing slow motion doesn't stretch it." )]
	public float PaidDelay { get; set; } = 2.5f;

	[Property, Group( "Finish" ), Description( "The last man going down slows the world until the screen fades to black for the bar." )]
	public bool FinishSlowMotion { get; set; } = true;

	[Property, Group( "Finish" ), Range( 0.05f, 1f ), Description( "Game speed through the finish. 0.25 is a quarter speed." )]
	public float FinishTimeScale { get; set; } = 0.25f;

	[Property, Group( "Finish" ), Range( 0.1f, 1f ), Description( "Pitch every game sound — hits, grunts, the music — is dragged down to through the finish. 0.5 is an octave down; 1 leaves sound alone. Menu sounds aren't touched." )]
	public float FinishSoundPitch { get; set; } = 0.55f;

	[Property, Group( "Beats" ), Description( "Seconds of black after a knockout, before the restart / quit menu comes up." )]
	public float MenuDelay { get; set; } = 1f;

	[Property, Group( "Beats" ), Description( "How long the end-of-night menu takes to fade in." )]
	public float MenuFadeTime { get; set; } = 0.5f;

	[Property, Group( "Beats" ), Description( "How long the screen stays black leaving the bar. Match it to the shop's fade time, or the round starts before the fade has covered it." )]
	public float LeaveDelay { get; set; } = 0.4f;

	[Property, Group( "Knockout" ), Description( "Played the instant you go down, over the black. The ringing in your ears." )]
	public SoundEvent KnockoutSound { get; set; }

	[Property, Group( "Knockout" ), Description( "Where QUIT TO MENU goes. The main menu scene." )]
	public SceneFile MainMenuScene { get; set; }

	[Property, Group( "References" ), Description( "Optional, found automatically in the scene." )]
	public WaveSpawner Spawner { get; set; }

	[Property, Group( "References" ), Description( "Optional, found automatically from whatever has RBPlayerMovement on it." )]
	public GameObject Player { get; set; }

	[Property, Group( "References" ), Description( "Optional. Put the player back here at the start of every round. Empty, each round starts wherever you're standing." )]
	public GameObject PlayerStart { get; set; }

	[Property, Group( "Debug" ), Description( "L finishes the current wave, F2 adds $100, F3 restarts the night. Switch off for a build." )]
	public bool DebugKeys { get; set; } = true;

	[Property, Group( "Debug" )] public bool LogRun { get; set; } = true;

	public RunPhase Phase { get; private set; } = RunPhase.Idle;

	/// <summary>Counts from 1. Zero until the first round begins.</summary>
	public int Round { get; private set; }

	public int Money { get; private set; }

	/// <summary>What the last round paid. Zero after a knockout.</summary>
	public int LastPayout { get; private set; }

	/// <summary>Kill Bounty earned this round (or the last one, once it's over). Already in Money — this is only for the banner.</summary>
	public int RoundBounty { get; private set; }

	/// <summary>
	/// The round the night is worth. It follows Round while you're clearing them and drops to nothing the
	/// moment you go down — losing doesn't cost you the round you died on, it costs you all of them.
	/// </summary>
	public int Score { get; private set; }

	/// <summary>Whether the night ended by walking out. False means it ended on the floor, and <see cref="Score"/> is nothing.</summary>
	public bool Survived { get; private set; }

	/// <summary>The night just walked out of beat the saved best, and is the new one. For the end screen.</summary>
	public bool NewBest { get; private set; }

	/// <summary>Everyone is done at the bar and the next round is coming. The shop fades to black on it.</summary>
	public bool LeavingBar { get; private set; }

	/// <summary>What you've said to "one more round?". Reset every time the question comes up.</summary>
	public RoundVote Vote { get; private set; }

	/// <summary>Done shopping and happy to go again.</summary>
	public bool Ready { get; private set; }

	/// <summary>Seconds into the current wave, or how long the last one took once it's over.</summary>
	public float RoundTime => Phase == RunPhase.Fighting ? timeInPhase : lastRoundTime;

	public float TimeInPhase => timeInPhase;

	/// <summary>
	/// Real seconds into the payout beat, stopped while the game is paused. Not the scene clock: the finishing slow
	/// motion runs through this beat and would stretch it, and the fade to the bar has to land on time.
	/// </summary>
	public float PaidTime => paidTime;

	/// <summary>
	/// Like <see cref="TimeInPhase"/>, but on the wall clock. Once you're down the scene is paused and its own
	/// clock stands still, so anything timed after a knockout has to count on this instead.
	/// </summary>
	public float RealTimeInPhase => realTimeInPhase;

	/// <summary>The bar is yours to interact with through these two phases and no others.</summary>
	public bool AtTheBar => Phase is RunPhase.Deciding or RunPhase.Shop;

	/// <summary>Code-side hooks for whatever needs to react to the night — the shop.</summary>
	public Action<RunPhase> OnPhaseChanged;
	public Action<int> OnMoneyChanged;

	/// <summary>
	/// Fired when a night ends with a score worth keeping. NOTHING LISTENS YET — the leaderboard is still to
	/// be built, and this is the one place it will hook into when it is. A night that ended on the floor
	/// never fires it at all, which is the whole point of the design.
	/// </summary>
	public Action<int> OnScored;

	/// <summary>
	/// Set by whatever actually runs the bar (BarShop), so the stand-in keys below stand aside for it. With
	/// nothing running it, the shop phase is still playable on Space and E.
	/// </summary>
	public bool ShopHandled { get; set; }

	SoundHandle knockoutRinging;

	RBPlayerMovement movement;
	PlayerHealth playerHealth;
	Health health;
	Stamina stamina;
	PlayerUpgrades upgrades;
	Rigidbody playerBody;

	TimeSince timeInPhase;
	RealTimeSince realTimeInPhase;
	TimeSince timeSinceReady;
	float lastRoundTime;
	float paidTime;

	// FreezeFor takes a duration, so "until told otherwise" is just a very long one
	const float HoldIndefinitely = 60f * 60f * 24f;

	protected override void OnStart()
	{
		Spawner ??= Scene.GetAllComponents<WaveSpawner>().FirstOrDefault();
		Player ??= Scene.GetAllComponents<RBPlayerMovement>().FirstOrDefault()?.GameObject;

		if ( !Spawner.IsValid() )
			Log.Warning( "[Run] no WaveSpawner in the scene — rounds will start and nobody will come through the door. L skips a wave." );

		if ( Player.IsValid() )
		{
			var search = FindMode.Enabled | FindMode.InSelf | FindMode.InDescendants;

			movement = Player.Components.Get<RBPlayerMovement>( search );
			playerHealth = Player.Components.Get<PlayerHealth>( search );
			health = Player.Components.Get<Health>( search );
			stamina = Player.Components.Get<Stamina>( search );
			upgrades = Player.Components.Get<PlayerUpgrades>( search );
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

		if ( Spawner.IsValid() )
			Spawner.OnEnemyDied += OnEnemyDied;

		if ( StartOnLoad )
			StartNight();
	}

	protected override void OnDestroy()
	{
		if ( playerHealth.IsValid() )
			playerHealth.OnKnockedOut -= OnPlayerKnockedOut;

		if ( Spawner.IsValid() )
			Spawner.OnEnemyDied -= OnEnemyDied;
	}

	/// <summary>Kill Bounty: paid on the spot for every man who goes down in a fight, however he went down.</summary>
	void OnEnemyDied( Enemy enemy )
	{
		if ( Phase != RunPhase.Fighting || !upgrades.IsValid() )
			return;

		int bounty = upgrades.BountyPerKill;

		if ( bounty <= 0 )
			return;

		AddMoney( bounty );
		RoundBounty += bounty;
		Report( $"bounty — +${bounty} for {( enemy.IsValid() ? enemy.GameObject.Name : "a man" )}, ${Money} in hand" );
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
				if ( Scene.GetSystem<HitStop>() is not { IsPaused: true } )
					paidTime += RealTime.Delta;

				if ( paidTime >= PaidDelay )
					AskTheQuestion();
				break;

			case RunPhase.Down:
				if ( realTimeInPhase >= MenuDelay )
					EndNight( survived: false );
				break;

			// the question. The bar draws it; these are the stand-in keys for when nothing is running one
			case RunPhase.Deciding:
				if ( !ShopHandled )
				{
					if ( Input.Pressed( "Jump" ) )
						Answer( RoundVote.Yes );
					else if ( Input.Pressed( "Use" ) )
						Answer( RoundVote.No );
				}
				break;

			case RunPhase.Shop:
				if ( !ShopHandled && Input.Pressed( "Jump" ) )
					SayReady();

				if ( LeavingBar && timeSinceReady >= LeaveDelay )
					NextRound();
				break;

			// the restart / quit menu is up, and it needs the pointer. Asked for every frame, because anything
			// else that wants the mouse back takes it
			case RunPhase.Over:
				Mouse.Visibility = MouseVisibility.Visible;
				break;
		}
	}

	// ---- the question --------------------------------------------------------------------------------

	/// <summary>Say yes or no to one more round. Only means anything while the question is up.</summary>
	public void Answer( RoundVote vote )
	{
		if ( Phase != RunPhase.Deciding )
			return;

		Vote = vote;

		if ( vote == RoundVote.Yes )
		{
			Report( "one more round it is" );
			OpenTheBar();
		}
		else if ( vote == RoundVote.No )
		{
			Report( $"called it — walking out on round {Score}" );
			EndNight( survived: true );
		}
	}

	/// <summary>Done shopping. The screen goes black and the next round starts behind it.</summary>
	public void SayReady()
	{
		if ( Phase != RunPhase.Shop || LeavingBar )
			return;

		Ready = true;
		LeavingBar = true;
		timeSinceReady = 0;
		Report( "done at the bar" );
	}

	// ---- the night ----------------------------------------------------------------------------------

	/// <summary>A fresh night, reset in place: no money, round zero, nobody in the bar.</summary>
	public void StartNight()
	{
		// back from the menu: hand the pointer back to the camera, and let the ringing go
		Mouse.Visibility = MouseVisibility.Auto;
		StopRinging();
		Scene.GetSystem<HitStop>()?.Resume();

		// a restart from the middle of a finish (F3) mustn't carry its slow motion into round one
		Scene.GetSystem<HitStop>()?.EndSlowMo( force: true );

		Round = 0;
		Score = 0;
		Survived = false;
		NewBest = false;
		LastPayout = 0;
		lastRoundTime = 0f;
		SetMoney( 0 );

		// an empty pocket and an empty drinker. Nothing a drink did carries into the next night
		if ( upgrades.IsValid() )
			upgrades.Clear();

		if ( Spawner.IsValid() )
			Spawner.Clear();

		Report( "a new night — see how far it goes" );

		NextRound();
	}

	/// <summary>Out of the bar and into the next wave: patched up, put back at the start, held for the countdown.</summary>
	public void NextRound()
	{
		Round++;

		if ( Spawner.IsValid() )
			Spawner.Clear();

		Vote = RoundVote.Undecided;
		Ready = false;
		LeavingBar = false;

		ResetPlayer();
		HoldPlayer( true );
		Enter( RunPhase.Starting );
	}

	void BeginWave()
	{
		HoldPlayer( false );
		RoundBounty = 0;

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

		// the round is banked the moment it's cleared. What's at stake at the bar is whether you keep it
		Score = Round;

		SetMoney( Money + LastPayout );
		Report( $"round {Round} cleared in {lastRoundTime:0.0}s — paid ${LastPayout}, ${Money} in hand" );
		Enter( RunPhase.Paid );

		// the last man down, in slow motion, until the screen goes black. Locked, so a punch thrown at nothing
		// can't end it early. AskTheQuestion ends it under the black; the long cap is only a safety net, kept long
		// so a trip to the pause menu mid-finish can't run it out
		if ( FinishSlowMotion )
			Scene.GetSystem<HitStop>()?.SlowMo( 60f, FinishTimeScale, FinishSoundPitch, locked: true );
	}

	void OnPlayerKnockedOut()
	{
		// only a knockout in the fight ends the night. One from a stray swing during the countdown or in
		// the bar just gets you stood back up with everything else
		if ( Phase != RunPhase.Fighting )
			return;

		lastRoundTime = timeInPhase;
		LastPayout = 0;

		if ( Spawner.IsValid() )
			Spawner.StopWave();

		// everything, not just this round. Pushing on and losing is meant to hurt
		Score = 0;

		// the lights go out and the world stops with them: no more swings landing on you, no more thuds
		// under the ringing. Restart or quitting to the menu is what starts it again
		Scene.GetSystem<HitStop>()?.Pause();

		StopRinging();

		if ( KnockoutSound is not null )
			knockoutRinging = Sound.Play( KnockoutSound );

		Report( $"knocked out in round {Round} — the night scores nothing" );
		Enter( RunPhase.Down );
	}

	/// <summary>To the bar, and the only question that matters. The drinks wait until it's answered.</summary>
	void AskTheQuestion()
	{
		// the screen is black by now: the finish is over, and the bar runs at full speed
		Scene.GetSystem<HitStop>()?.EndSlowMo( force: true );

		if ( Spawner.IsValid() )
			Spawner.Clear();

		// up off the floor now rather than at the next countdown, so nobody answers lying down
		ResetPlayer();
		HoldPlayer( true );

		Vote = RoundVote.Undecided;
		Ready = false;

		Report( $"round {Round} cleared — one more?" );
		Enter( RunPhase.Deciding );
	}

	void OpenTheBar()
	{
		HoldPlayer( true );
		Ready = false;
		LeavingBar = false;
		Enter( RunPhase.Shop );
	}

	void EndNight( bool survived )
	{
		Survived = survived;

		if ( !survived )
			Score = 0;

		if ( Spawner.IsValid() )
			Spawner.Clear();

		// down stays down. Standing the body back up would only be seen through the menu, and the night is
		// over either way — RESTART is what gets you back on your feet
		if ( survived )
			ResetPlayer();

		HoldPlayer( true );
		Enter( RunPhase.Over );

		Report( survived
			? $"walked out on round {Score}"
			: "closing time on the floor — nothing to show for it" );

		if ( !survived || Score <= 0 )
			return;

		// saved to disk only here: walking out of the bar. A knockout returned above and never reaches it
		NewBest = HighScore.Submit( Score );
		Report( NewBest ? $"new best — round {Score} saved" : $"best stays at round {HighScore.Best}" );

		// The board doesn't exist yet. When it does, it hooks in here and nowhere else — and it only ever
		// hears about nights that were walked out of, which is what makes the gamble mean anything.
		OnScored?.Invoke( Score );
	}

	/// <summary>From the menu at the end of the night: back to round one, right here.</summary>
	public void Restart() => StartNight();

	/// <summary>From the menu at the end of the night: back to the front door.</summary>
	public void QuitToMenu()
	{
		StopRinging();
		Scene.GetSystem<HitStop>()?.Resume();

		if ( MainMenuScene is null )
		{
			Log.Warning( "[Run] no MainMenuScene set on RunManager — there's nowhere to quit to." );
			return;
		}

		// Game.ChangeScene rather than Scene.Load: this swaps the running scene out for the menu, where Scene.Load
		// left the bar alive underneath it — the player's hands still drawing behind the menu, and a second copy
		// of the whole scene waiting to collide with the next one when Play was pressed
		var options = new SceneLoadOptions();
		options.SetScene( MainMenuScene );
		Game.ChangeScene( options );
	}

	void StopRinging()
	{
		if ( knockoutRinging.IsValid() )
			knockoutRinging.Stop( 0.5f );

		knockoutRinging = null;
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

		int purse = basePay + (int)MathF.Round( SpeedBonus * speed );

		// a drink that pays better is applied last, to the whole purse rather than to either half of it
		return upgrades.IsValid() ? (int)MathF.Round( upgrades.With( purse, PlayerStat.Payout ) ) : purse;
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
		realTimeInPhase = 0;
		paidTime = 0f;
		OnPhaseChanged?.Invoke( phase );
	}

	/// <summary>
	/// Health and wind back to full, off the floor, and back to the start point if there is one. Health
	/// resetting every round is the design, not a convenience: the round is the test, the score is the run.
	/// </summary>
	void ResetPlayer()
	{
		// the drinks go on FIRST, before anything below reads a maximum — otherwise the round you buy a bigger
		// Max Health is the round you spend on the old one
		if ( upgrades.IsValid() )
			upgrades.Apply();

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

		// facing lives on the head and the camera, not the root — turning the root would do nothing, its
		// rotation is locked
		var spawnYaw = PlayerStart.WorldRotation.Angles().yaw;

		if ( movement.IsValid() && movement.Head.IsValid() )
			movement.Head.WorldRotation = Rotation.FromYaw( spawnYaw );

		// the camera has to be told directly as well: it owns the aim outright now, so writing Head alone
		// stopped taking effect once the aim was no longer rebuilt from Head's transform each frame
		CameraMovement.Find( Player )?.SetEyeAngles( new Angles( 0f, spawnYaw, 0f ) );
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
		if ( Input.Keyboard.Pressed( "L" ) && Phase == RunPhase.Fighting )
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
			Report( $"debug — +$100, ${Money} in hand" );
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
