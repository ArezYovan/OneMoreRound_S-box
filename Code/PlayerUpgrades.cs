using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// What the night has done to you. Every drink you buy lands here, stays for the run, and this is the only
/// thing in the game that writes the numbers they change.
///
/// It rebakes rather than patches. The standing value of every property a drink can touch is remembered the
/// first time it is read, and applying is always standing to effective from the whole tab at once. Nothing is
/// ever compounded onto an already-changed number, so applying twice does exactly what applying once did, a
/// drink can land mid-round without the maths drifting, and the round loop can re-apply on every reset without
/// having to think about it.
///
/// Drinks name a <see cref="PlayerStat"/> and never a component, so the map from stat to property lives in
/// <see cref="Apply"/> and nowhere else. Adding a stat is a line there and a line in the enum.
/// </summary>
public sealed class PlayerUpgrades : Component
{
	[Property, Group( "Testing" ), Description( "Drinks you start every night with, for trying a combination out without shopping for it first. Empty for a real run — restarting the night puts these back and nothing else." )]
	public List<Drink> StartingDrinks { get; set; } = new();

	[Property, Group( "Debug" )] public bool LogUpgrades { get; set; } = true;

	/// <summary>Everything on your tab this run, in the order you drank it. The same drink twice counts twice.</summary>
	public IReadOnlyList<Drink> Owned => owned;

	/// <summary>Fired once a drink has landed and every number has been rebaked.</summary>
	public Action<Drink> OnDrinkAdded;

	/// <summary>The player's upgrades, or null while there is no player in the scene.</summary>
	public static PlayerUpgrades Find( Scene scene ) => scene?.GetAllComponents<PlayerUpgrades>().FirstOrDefault();

	// The floor under every multiplier. A pile of downsides can take a number to a tenth of what it was and no
	// further — never to zero, and never through it into the negative, where a "faster" attack would run backwards.
	const float MinFactor = 0.1f;

	readonly List<Drink> owned = new();
	readonly Dictionary<PlayerStat, float> percents = new();
	readonly Dictionary<PlayerStat, float> flats = new();
	readonly Dictionary<string, float> standing = new();

	Health health;
	Stamina stamina;
	PlayerCombat combat;
	RBPlayerMovement movement;
	DashAbility dash;
	Sliding sliding;
	SlideTackle tackle;
	PlayerGrab grab;
	PlayerBlock block;
	BarShop bar;

	protected override void OnStart()
	{
		// EverythingInSelfAndDescendants on purpose: an ability that starts switched off is still something a
		// drink can buff, and the default search hands back null for it
		var search = FindMode.EverythingInSelfAndDescendants;

		health = Components.Get<Health>( search );
		stamina = Components.Get<Stamina>( search );
		combat = Components.Get<PlayerCombat>( search );
		movement = Components.Get<RBPlayerMovement>( search );
		dash = Components.Get<DashAbility>( search );
		sliding = Components.Get<Sliding>( search );
		tackle = Components.Get<SlideTackle>( search );
		grab = Components.Get<PlayerGrab>( search );
		block = Components.Get<PlayerBlock>( search );

		// the bar announces what it sold and knows nothing about who drank it, which is the right way round
		bar = Scene.GetAllComponents<BarShop>().FirstOrDefault();

		if ( bar.IsValid() )
			bar.OnDrinkBought += Add;

		// seeds the standing value of every number below, before a drink has touched any of them
		Clear();
	}

	protected override void OnDestroy()
	{
		if ( bar.IsValid() )
			bar.OnDrinkBought -= Add;
	}



	/// <summary>One more on the tab. Permanent for the run — there is no sobering up.</summary>
	public void Add( Drink drink )
	{
		if ( drink is null )
			return;

		owned.Add( drink );
		Rebake();
		Apply();

		if ( LogUpgrades )
			Log.Info( $"[Drinks] {drink.Name} — {owned.Count} on the tab — {Summary()}" );

		OnDrinkAdded?.Invoke( drink );
	}

	/// <summary>A fresh night: back to whatever the inspector says you start with, and every number to standing.</summary>
	public void Clear()
	{
		owned.Clear();

		foreach ( var drink in StartingDrinks )
		{
			if ( drink is not null )
				owned.Add( drink );
		}

		Rebake();
		Apply();
	}

	/// <summary>
	/// How many of this drink are on the tab. Matched on the asset's path as well as the object, so a drink reloaded by
	/// the editor mid-night still counts as the one you bought.
	/// </summary>
	public int CountOf( Drink drink ) => drink is null ? 0 : owned.Count( d => d == drink
		|| ( d is not null && !string.IsNullOrEmpty( drink.ResourcePath ) && d.ResourcePath == drink.ResourcePath ) );

	/// <summary>Dollars paid for every man who goes down in a fight (Kill Bounty). The run pays it; this only says how much.</summary>
	public int BountyPerKill => Math.Max( 0, (int)MathF.Round( With( 0f, PlayerStat.KillBounty ) ) );

	/// <summary>
	/// What a set of stats multiplies by, all of them added together. One when no drink has touched them, and
	/// never less than a tenth.
	/// </summary>
	public float Factor( params PlayerStat[] stats )
	{
		float total = 1f;

		foreach ( var stat in stats )
			total += PercentOf( stat ) * 0.01f;

		return MathF.Max( total, MinFactor );
	}

	/// <summary>A number that moves WITH the stat: more Melee Damage, bigger number.</summary>
	public float With( float value, params PlayerStat[] stats ) => ( value + FlatOf( stats ) ) * Factor( stats );

	/// <summary>A number that moves AGAINST the stat: more Attack Speed, shorter windup.</summary>
	public float Against( float value, params PlayerStat[] stats ) => ( value + FlatOf( stats ) ) / Factor( stats );

	/// <summary>
	/// Write every number a drink can reach. Cheap and safe to call as often as you like — it always rebuilds
	/// from the standing values rather than from whatever is there now.
	/// </summary>
	public void Apply()
	{
		if ( health.IsValid() )
		{
			float wasMax = health.MaxHealth;
			// floored like every multiplier: stacked health downsides (a pile of Bloody Marys) take it down to a tenth
			// and no further, where at zero you'd start the next round already dead
			float standingMax = Standing( "health.max", health.MaxHealth );
			health.MaxHealth = MathF.Max( With( standingMax, PlayerStat.MaxHealth ), standingMax * MinFactor );
			health.DamageTakenScale = Against( 1f, PlayerStat.Toughness );

			// a drink that makes you harder to put down should feel like it at the bar, not next round
			if ( health.MaxHealth > wasMax )
				health.Heal( health.MaxHealth - wasMax );
		}

		if ( stamina.IsValid() )
		{
			stamina.MaxStamina = With( Standing( "stamina.max", stamina.MaxStamina ), PlayerStat.MaxStamina );
			stamina.RegenRate = With( Standing( "stamina.regen", stamina.RegenRate ), PlayerStat.StaminaRegen );
		}

		if ( combat.IsValid() )
		{
			combat.JabDamage = With( Standing( "jab.damage", combat.JabDamage ), PlayerStat.MeleeDamage, PlayerStat.JabDamage );
			combat.HookDamage = With( Standing( "hook.damage", combat.HookDamage ), PlayerStat.MeleeDamage, PlayerStat.HookDamage );
			combat.KickDamage = With( Standing( "kick.damage", combat.KickDamage ), PlayerStat.MeleeDamage, PlayerStat.KickDamage );

			combat.JabRange = With( Standing( "jab.range", combat.JabRange ), PlayerStat.Reach );
			combat.HookRange = With( Standing( "hook.range", combat.HookRange ), PlayerStat.Reach );
			combat.KickRange = With( Standing( "kick.range", combat.KickRange ), PlayerStat.Reach );

			combat.JabKnockback = With( Standing( "jab.knockback", combat.JabKnockback ), PlayerStat.Knockback );
			combat.HookKnockback = With( Standing( "hook.knockback", combat.HookKnockback ), PlayerStat.Knockback );
			combat.KickKnockback = With( Standing( "kick.knockback", combat.KickKnockback ), PlayerStat.Knockback );

			combat.JabStagger = With( Standing( "jab.stagger", combat.JabStagger ), PlayerStat.Stagger );
			combat.HookStagger = With( Standing( "hook.stagger", combat.HookStagger ), PlayerStat.Stagger );
			combat.KickStagger = With( Standing( "kick.stagger", combat.KickStagger ), PlayerStat.Stagger );

			// windup and recovery are times, so a faster attack is a smaller number on both
			combat.JabWindup = Against( Standing( "jab.windup", combat.JabWindup ), PlayerStat.AttackSpeed );
			combat.HookWindup = Against( Standing( "hook.windup", combat.HookWindup ), PlayerStat.AttackSpeed );
			combat.KickWindup = Against( Standing( "kick.windup", combat.KickWindup ), PlayerStat.AttackSpeed );

			combat.JabRecovery = Against( Standing( "jab.recovery", combat.JabRecovery ), PlayerStat.AttackSpeed );
			combat.HookRecovery = Against( Standing( "hook.recovery", combat.HookRecovery ), PlayerStat.AttackSpeed );
			combat.KickRecovery = Against( Standing( "kick.recovery", combat.KickRecovery ), PlayerStat.AttackSpeed );

			combat.JabStaminaCost = Against( Standing( "jab.stamina", combat.JabStaminaCost ), PlayerStat.StaminaEfficiency );
			combat.HookStaminaCost = Against( Standing( "hook.stamina", combat.HookStaminaCost ), PlayerStat.StaminaEfficiency );
			combat.KickStaminaCost = Against( Standing( "kick.stamina", combat.KickStaminaCost ), PlayerStat.StaminaEfficiency, PlayerStat.KickEfficiency );

			// unlocks, like the tackle: any Flat hands it over, and ticked in the inspector counts too
			combat.CounterPunch = FlatOf( PlayerStat.CounterPunch ) > 0f || StandingFlag( "combat.counter", combat.CounterPunch );
			combat.BowlingBall = FlatOf( PlayerStat.BowlingBall ) > 0f || StandingFlag( "combat.bowling", combat.BowlingBall );

			// the per cent IS the bonus at its fullest — PlayerCombat works out how much of it low health earns
			combat.CourageBonus = Standing( "combat.courage", combat.CourageBonus ) + PercentOf( PlayerStat.LiquidCourage );
		}

		if ( block.IsValid() )
		{
			// free to begin with, so the Flat does the work: a negative Guard is a price per block. Per cent and
			// Stamina Efficiency then make whatever it costs cheaper, the same as they do for punches
			float price = MathF.Max( 0f, Standing( "block.stamina", block.StaminaCost ) - FlatOf( PlayerStat.Guard ) );
			block.StaminaCost = price / Factor( PlayerStat.Guard, PlayerStat.StaminaEfficiency );
		}

		if ( movement.IsValid() )
		{
			movement.maxSpeed = With( Standing( "move.speed", movement.maxSpeed ), PlayerStat.MoveSpeed );
			movement.jumpForce = With( Standing( "move.jump", movement.jumpForce ), PlayerStat.JumpHeight );
		}

		if ( dash.IsValid() )
		{
			dash.DashDistance = With( Standing( "dash.distance", dash.DashDistance ), PlayerStat.DashDistance );
			dash.Cooldown = Against( Standing( "dash.cooldown", dash.Cooldown ), PlayerStat.DashRecovery );
			dash.StaminaCost = Against( Standing( "dash.stamina", dash.StaminaCost ), PlayerStat.StaminaEfficiency );
		}

		if ( sliding.IsValid() )
		{
			sliding.maxSlideSpeed = With( Standing( "slide.speed", sliding.maxSlideSpeed ), PlayerStat.SlideSpeed );
			sliding.maxSlopeSlideSpeed = With( Standing( "slide.slope", sliding.maxSlopeSlideSpeed ), PlayerStat.SlideSpeed );
			sliding.slideBoost = With( Standing( "slide.boost", sliding.slideBoost ), PlayerStat.SlideSpeed );
		}

		if ( tackle.IsValid() )
		{
			// an unlock, not a number: any drink with a Flat on it hands you the tackle. Ticked in the inspector
			// counts too, so it can be tried without buying it
			tackle.Unlocked = FlatOf( PlayerStat.SlideTackle ) > 0f || StandingFlag( "tackle.unlocked", tackle.Unlocked );
			tackle.StaggerFraction = Standing( "tackle.stagger", tackle.StaggerFraction ) * Factor( PlayerStat.SlideTackle );
			tackle.BowlingBall = FlatOf( PlayerStat.BowlingBall ) > 0f || StandingFlag( "tackle.bowling", tackle.BowlingBall );
		}

		if ( grab.IsValid() )
		{
			grab.ThrowSpeed = With( Standing( "grab.throw", grab.ThrowSpeed ), PlayerStat.ThrowForce );
			grab.ThrowDamageScale = Standing( "grab.damage", grab.ThrowDamageScale ) * Factor( PlayerStat.ThrowDamage );
			grab.RicochetBounces = (int)MathF.Round( Standing( "grab.ricochet", grab.RicochetBounces ) + FlatOf( PlayerStat.Ricochet ) );
		}
	}

	/// <summary>Every stat a drink has moved, written out. For the log, and for a HUD line later.</summary>
	public string Summary()
	{
		var lines = percents.Keys
			.Concat( flats.Keys )
			.Distinct()
			.Where( stat => MathF.Abs( PercentOf( stat ) ) > 0.01f || MathF.Abs( FlatOf( stat ) ) > 0.01f )
			.OrderBy( stat => stat.ToString() )
			.Select( stat => FlatOf( stat ) == 0f
				? $"{stat} {PercentOf( stat ):+0.#;-0.#}%"
				: $"{stat} {FlatOf( stat ):+0.#;-0.#} and {PercentOf( stat ):+0.#;-0.#}%" )
			.ToList();

		return lines.Count == 0 ? "nothing yet" : string.Join( ", ", lines );
	}

	/// <summary>Add up every drink on the tab into one number per stat. Per cents add rather than compound.</summary>
	void Rebake()
	{
		percents.Clear();
		flats.Clear();

		foreach ( var drink in owned )
		{
			if ( drink?.Effects is null )
				continue;

			foreach ( var effect in drink.Effects )
			{
				if ( effect is null )
					continue;

				percents[effect.Stat] = PercentOf( effect.Stat ) + effect.Percent;
				flats[effect.Stat] = FlatOf( effect.Stat ) + effect.Flat;
			}
		}
	}

	float PercentOf( PlayerStat stat ) => percents.TryGetValue( stat, out var value ) ? value : 0f;

	float FlatOf( PlayerStat stat ) => flats.TryGetValue( stat, out var value ) ? value : 0f;

	float FlatOf( PlayerStat[] stats )
	{
		float total = 0f;

		foreach ( var stat in stats )
			total += FlatOf( stat );

		return total;
	}

	/// <summary>A yes/no property's standing value, remembered the same way as the numbers.</summary>
	bool StandingFlag( string key, bool current ) => Standing( key, current ? 1f : 0f ) > 0.5f;

	/// <summary>
	/// What a property was set to before any drink touched it, remembered the first time it is asked for. Every
	/// effective value is built from these, which is what makes applying twice the same as applying once.
	/// </summary>
	float Standing( string key, float current )
	{
		if ( standing.TryGetValue( key, out var value ) )
			return value;

		standing[key] = current;
		return current;
	}
}
