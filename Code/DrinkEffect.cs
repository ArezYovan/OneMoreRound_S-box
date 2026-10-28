using System.ComponentModel;

/// <summary>
/// Everything a drink is allowed to change about you, named the way a drinker would name it rather than the
/// way the code does: <b>more is always better</b>. More Attack Speed is a shorter windup, more Toughness is
/// less damage taken, more Haggle is a smaller price on the counter. A downside is the same stat going
/// negative, never a separate kind of thing.
///
/// Which property each one actually writes lives in <see cref="PlayerUpgrades"/> and nowhere else, so a drink
/// never has to know that Attack Speed is four numbers on <see cref="PlayerCombat"/>. Adding a stat is a line
/// here and a line in <c>PlayerUpgrades.Apply</c>.
/// </summary>
public enum PlayerStat
{
	[Description( "How much you can take before you go down." )]
	MaxHealth,

	[Description( "Less damage from everything that lands on you. +100% is half damage, not none." )]
	Toughness,

	[Description( "The size of your wind." )]
	MaxStamina,

	[Description( "How fast your wind comes back." )]
	StaminaRegen,

	[Description( "Everything you throw costs less wind - punches, kicks and the dash together." )]
	StaminaEfficiency,

	[Description( "The kick alone costs less wind. Stacks with Stamina Efficiency; a downside of -30% makes each kick cost about 40% more." )]
	KickEfficiency,

	[Description( "Damage on all three attacks at once." )]
	MeleeDamage,

	[Description( "Damage on the jab alone. Stacks with Melee Damage." )]
	JabDamage,

	[Description( "Damage on the hook alone. Stacks with Melee Damage." )]
	HookDamage,

	[Description( "Damage on the kick alone. Stacks with Melee Damage." )]
	KickDamage,

	[Description( "Shorter windups and recoveries on every attack - you throw faster and get your hands back sooner." )]
	AttackSpeed,

	[Description( "How far your hits send a man." )]
	Knockback,

	[Description( "How badly your hits rattle a man." )]
	Stagger,

	[Description( "A man you kick or slide-tackle knocks down whoever he crashes into - straight into a stagger, like bowling pins. Any Flat unlocks it." )]
	BowlingBall,

	[Description( "Parry a man and your next hit while he's reeling knocks him straight into a stagger. Any Flat unlocks it." )]
	CounterPunch,

	[Description( "The more of this, the harder you hit as your health drops. Per cent is the most it adds, reached at LowHealthFraction on PlayerCombat (+60% is sixty per cent more damage near death). Melee only." )]
	LiquidCourage,

	[Description( "How cheap your guard is to hold. Blocking starts out free; a negative Flat makes every blow you block cost that much wind (-12 is twelve a block), and a block you can't pay for breaks your guard and the blow lands. A parry is always free." )]
	Guard,

	[Description( "How far your hits reach." )]
	Reach,

	[Description( "How fast you move on your feet." )]
	MoveSpeed,

	[Description( "How high you jump." )]
	JumpHeight,

	[Description( "How far a dash carries you." )]
	DashDistance,

	[Description( "Shorter wait between dashes." )]
	DashRecovery,

	[Description( "How fast a slide runs and how hard it kicks off." )]
	SlideSpeed,

	[Description( "Slide into a man and he goes flying, badly rattled. Any Flat unlocks it; Per cent adds to how much of his stagger meter one tackle fills (+67% takes the standing 60% to a guaranteed stagger)." )]
	SlideTackle,

	[Description( "How hard you throw whatever you are carrying." )]
	ThrowForce,

	[Description( "Damage done by whatever you throw, on top of what the speed of the throw already gives it." )]
	ThrowDamage,

	[Description( "Something you throw that hits a man flies on to the nearest other one. Flat is how many times it bounces on (1 is one extra man)." )]
	Ricochet,

	[Description( "More money for clearing a round." )]
	Payout,

	[Description( "Money for every man who goes down in a fight, paid on the spot. Flat is dollars a head (3 is $3 a man)." )]
	KillBounty,

	[Description( "Cheaper drinks and cheaper rerolls at the bar." )]
	Haggle
}

/// <summary>
/// One line of what a drink does: a stat, and how much better it makes it. Several of these on a drink is
/// how a trade is written - a big plus on one line and a smaller minus on another.
/// </summary>
public sealed class DrinkEffect
{
	[Property, Description( "What this changes. More is better on every one of them, so a downside is a negative number and not a different stat." )]
	public PlayerStat Stat { get; set; } = PlayerStat.MeleeDamage;

	[Property, Description( "Per cent better: 25 is a quarter better, -20 is a fifth worse. Stacks by adding, so two +25% drinks make +50% rather than +56%, and no pile of downsides can take a number below a tenth of what it started at." )]
	public float Percent { get; set; }

	[Property, Description( "Added before the per cent, in the stat's own units - 20 on Max Health is twenty more hit points. Leave it at zero on the stats where better means a smaller number (Attack Speed, Dash Recovery, Toughness, Haggle, Stamina Efficiency): per cent is the only thing that reads right on those." )]
	public float Flat { get; set; }

	public override string ToString()
	{
		if ( Percent == 0f && Flat == 0f )
			return $"{Stat} (nothing)";

		if ( Flat == 0f )
			return $"{Stat} {Percent:+0.#;-0.#}%";

		if ( Percent == 0f )
			return $"{Stat} {Flat:+0.#;-0.#}";

		return $"{Stat} {Flat:+0.#;-0.#} and {Percent:+0.#;-0.#}%";
	}
}
