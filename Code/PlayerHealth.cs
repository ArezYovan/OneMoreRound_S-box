using System;

/// <summary>
/// The player's side of taking a beating. Health itself is deliberately generic — the same
/// component runs a barstool, a patron and you — so everything that is true only of the player
/// lives here: getting knocked out, the beat of downtime that follows, and getting back up.
///
/// Right now that downtime is a timer. When the round loop exists it becomes the end of the
/// round, and this is the component that will fire it.
/// </summary>
public sealed class PlayerHealth : Component
{
	[RequireComponent] Health Hp { get; set; }

	[Property, Group( "Knockout" ), Description( "Seconds on the floor before you're back up. Stands in for the round ending — per the design doc a lost round costs payout, not progress, so going down is downtime rather than a failure state." )]
	public float KnockoutTime { get; set; } = 3f;

	[Property, Group( "Knockout" ), Description( "Get up automatically. Turn this off once the round loop owns the reset." )]
	public bool ReviveAfterKnockout { get; set; } = true;

	[Property, Group( "Debug" )] public bool LogHits { get; set; } = true;

	[Property, Group( "Events" )] public Action OnKnockedOut { get; set; }
	[Property, Group( "Events" )] public Action OnRevived { get; set; }

	public bool IsKnockedOut { get; private set; }

	/// <summary>
	/// The movement scripts ask "may this player act?" in terms of being dead. Here that state
	/// is a knockout you get back up from, so this is the same question in their vocabulary —
	/// a corpse and a man on the floor are equally unable to dash.
	/// </summary>
	public bool IsDead => IsKnockedOut;

	/// <summary>Seconds since the last hit landed. The HUD reads this to flash.</summary>
	public float TimeSinceHurt => timeSinceHurt;

	public float LastDamage { get; private set; }

	/// <summary>Where the last hit came from, so the HUD can eventually point at it.</summary>
	public Vector3 LastHitDirection { get; private set; }

	TimeSince timeSinceHurt = 999f;
	TimeSince timeSinceKnockout;

	protected override void OnStart()
	{
		if ( !Hp.IsValid() )
			return;

		Hp.OnDamaged += OnHurt;
		Hp.OnDied += OnKnockout;
	}

	protected override void OnDestroy()
	{
		if ( !Hp.IsValid() )
			return;

		Hp.OnDamaged -= OnHurt;
		Hp.OnDied -= OnKnockout;
	}

	protected override void OnUpdate()
	{
		if ( !IsKnockedOut || !ReviveAfterKnockout )
			return;

		if ( timeSinceKnockout >= KnockoutTime )
			Revive();
	}

	void OnHurt( float amount, GameObject attacker )
	{
		timeSinceHurt = 0;
		LastDamage = amount;

		LastHitDirection = attacker.IsValid()
			? ( attacker.WorldPosition - WorldPosition ).WithZ( 0 ).Normal
			: Vector3.Zero;

		if ( LogHits )
			Log.Info( $"[Player] took {amount:0.#} from {( attacker.IsValid() ? attacker.Name : "something" )} — {Hp.CurrentHealth:0}/{Hp.MaxHealth:0}" );
	}

	void OnKnockout()
	{
		if ( IsKnockedOut )
			return;

		IsKnockedOut = true;
		timeSinceKnockout = 0;

		Log.Info( $"[Player] KNOCKED OUT — up again in {KnockoutTime:0.#}s" );

		OnKnockedOut?.Invoke();
	}

	/// <summary>Back on your feet at full health. Call this from the round loop when it exists.</summary>
	public void Revive()
	{
		if ( !IsKnockedOut )
			return;

		IsKnockedOut = false;
		timeSinceHurt = 999f;

		Hp?.Revive();

		Log.Info( "[Player] back on your feet" );

		OnRevived?.Invoke();
	}
}
