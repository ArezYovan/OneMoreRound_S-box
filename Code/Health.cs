using System;

/// <summary>
/// How hard a blow reads, for whoever reacts to it. Numbered to match the enemy animgraph's hit_type.
/// Anything that doesn't say — a thrown bottle, a fall — counts as heavy. A launch is the kick: no
/// flinch, he's sent flying and plays his knockback instead.
/// </summary>
public enum HitKind
{
	Light = 0,
	Heavy = 1,
	Launch = 2
}

/// <summary>
/// Hit points for anything that can be hit. Deliberately knows nothing about physics —
/// knockback is the attacker's job (see <see cref="PlayerCombat"/>), so this drops onto a
/// static prop and onto a loose rigidbody crate in exactly the same way.
/// </summary>
public sealed class Health : Component
{
	[Property, Group( "Health" )] public float MaxHealth { get; set; } = 100f;

	[Property, Group( "Health" ), Description( "Multiplies every hit that lands here. One is untouched, a half takes half damage. This is what a drink moves: PlayerUpgrades writes it on the player, and nothing else should." )]
	public float DamageTakenScale { get; set; } = 1f;

	[Property, Group( "Health" ), Description( "Filled in from MaxHealth on wake. Shown here so you can watch it drain in the inspector while testing." )]
	public float CurrentHealth { get; private set; }

	[Property, Group( "Death" ), Description( "Remove the object once it dies. Leave off while testing so you can keep hitting a corpse and see the 'already dead' logs." )]
	public bool DestroyOnDeath { get; set; } = false;

	[Property, Group( "Death" ), Description( "Seconds between dying and being removed, so a killing blow's knockback still gets to play out." )]
	public float DestroyDelay { get; set; } = 2f;

	[Property, Group( "Debug" )] public bool LogDamage { get; set; } = true;

	/// <summary>Fired once, the moment health reaches zero.</summary>
	[Property, Group( "Events" )] public Action OnDied { get; set; }

	/// <summary>Code-side hook: the amount actually taken, and who dealt it (may be null).</summary>
	public Action<float, GameObject> OnDamaged;

	/// <summary>
	/// Where the hit OnDamaged is reporting landed. Set just before it fires, so read it from inside a
	/// handler; zero for damage that didn't come from anywhere in particular.
	/// </summary>
	public Vector3 LastHitPosition { get; private set; }

	/// <summary>Which way that hit was travelling, normalized. Zero if whoever dealt it didn't say.</summary>
	public Vector3 LastHitDirection { get; private set; }

	/// <summary>How hard that hit was, for picking a reaction to it.</summary>
	public HitKind LastHitKind { get; private set; }

	public bool IsDead => CurrentHealth <= 0f;

	/// <summary>0–1, for health bars.</summary>
	public float Fraction => MaxHealth <= 0f ? 0f : ( CurrentHealth / MaxHealth ).Clamp( 0f, 1f );

	bool destroyPending;
	TimeUntil timeUntilDestroy;

	protected override void OnAwake()
	{
		CurrentHealth = MaxHealth;
	}

	protected override void OnUpdate()
	{
		// polled rather than scheduled, so a delayed destroy can't outlive the object it belongs to
		if ( destroyPending && timeUntilDestroy )
			GameObject.Destroy();
	}

	public void TakeDamage( float amount, GameObject attacker = null, Vector3 hitPosition = default, Vector3 hitDirection = default, HitKind kind = HitKind.Heavy )
	{
		// whatever has been done to how much you can take lands here rather than at each attacker, so every
		// source of damage — a fist, a thrown bottle, a fall — goes through it without knowing about it
		amount *= MathF.Max( 0f, DamageTakenScale );

		if ( amount <= 0f )
			return;

		if ( IsDead )
		{
			if ( LogDamage )
				Log.Info( $"[Health] {GameObject.Name} is already dead — ignoring {amount:0.#} damage" );

			return;
		}

		// clamped so the log never reports more damage than there was health left to take
		float taken = MathF.Min( amount, CurrentHealth );
		CurrentHealth -= taken;

		if ( LogDamage )
		{
			string from = attacker.IsValid() ? attacker.Name : "unknown";
			Log.Info( $"[Health] {GameObject.Name} took {taken:0.#} from {from} — {CurrentHealth:0.#}/{MaxHealth:0.#}" );
		}

		LastHitPosition = hitPosition;
		LastHitDirection = hitDirection.IsNearlyZero() ? Vector3.Zero : hitDirection.Normal;
		LastHitKind = kind;

		OnDamaged?.Invoke( taken, attacker );

		if ( CurrentHealth <= 0f )
			Die( attacker );
	}

	public void Heal( float amount )
	{
		if ( amount <= 0f || IsDead )
			return;

		CurrentHealth = MathF.Min( MaxHealth, CurrentHealth + amount );

		if ( LogDamage )
			Log.Info( $"[Health] {GameObject.Name} healed {amount:0.#} — {CurrentHealth:0.#}/{MaxHealth:0.#}" );
	}

	/// <summary>
	/// Back on your feet. Distinct from Heal on purpose: Heal deliberately refuses to touch the
	/// dead, because healing a corpse is always a bug — but the round loop resetting a fighter
	/// between rounds isn't healing, it's a new round. This is that.
	/// </summary>
	public void Revive( float amount = 0f )
	{
		CurrentHealth = amount > 0f ? MathF.Min( MaxHealth, amount ) : MaxHealth;

		// a queued destroy from the last death would otherwise fire on the revived object
		destroyPending = false;

		if ( LogDamage )
			Log.Info( $"[Health] {GameObject.Name} revived — {CurrentHealth:0.#}/{MaxHealth:0.#}" );
	}

	public void Kill( GameObject attacker = null )
	{
		if ( IsDead )
			return;

		CurrentHealth = 0f;
		Die( attacker );
	}

	void Die( GameObject attacker )
	{
		string from = attacker.IsValid() ? attacker.Name : "unknown";
		Log.Info( $"[Health] {GameObject.Name} DIED (killed by {from})" );

		OnDied?.Invoke();

		if ( !DestroyOnDeath )
			return;

		destroyPending = true;
		timeUntilDestroy = DestroyDelay;
	}
}
