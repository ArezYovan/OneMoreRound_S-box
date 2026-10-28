using System.Collections.Generic;
using System.ComponentModel;

/// <summary>
/// How special a drink is: how often the bar pours it, and how big a thing it does. Blue is a can — small, safe,
/// no downside. Purple is a glass — a good solid boost, right in the middle. Gold is a bottle — rare, and the
/// drinks that change how you fight rather than just a number.
/// </summary>
public enum DrinkRarity
{
	[Description( "Cans. Small, safe upgrades with no downside." )]
	Blue,

	[Description( "Glasses. A good boost, and the payout drinks." )]
	Purple,

	[Description( "Bottles. Rare, and the ones that change how you play." )]
	Gold
}

/// <summary>
/// One bottle on the bar: what it's called, what it does for you, and what it costs. Drinks are assets, made in
/// the editor, and the bar offers whichever ones exist.
///
/// The Upsides and Downsides lines are what a drink SAYS; the Effects list is what it DOES. They are separate
/// on purpose — the lines are written by hand so a drink can read like a drink rather than like a spreadsheet
/// — which does mean it is on you to keep them honest when you change a number.
/// </summary>
[GameResource( "Drink", "drink", "A bottle you can buy at the bar between rounds", Icon = "local_bar" )]
public sealed class Drink : GameResource
{
	[Property, Description( "What the bar calls it." )]
	public string Name { get; set; } = "House Lager";

	[Property, Description( "A line of flavour under the name." )]
	public string Flavour { get; set; } = "";

	[Property, Group( "What it does" ), Description( "The good half, a line each. Shown in green." )]
	public List<string> Upsides { get; set; } = new();

	[Property, Group( "What it does" ), Description( "What it costs you, a line each. Shown in red. Every drink wants one — the trade is the whole point." )]
	public List<string> Downsides { get; set; } = new();

	[Property, Group( "What it does" ), Description( "What the drink actually changes. More is better on every stat, so an upside is a positive number and a downside a negative one. Several lines make a trade: a big plus on one stat and a smaller minus on another." )]
	public List<DrinkEffect> Effects { get; set; } = new();

	[Property, Group( "Bar" ), Description( "Which shelf it's on. Decides how often the bar pours it (the odds live on BarShop) and the colour of its card." )]
	public DrinkRarity Rarity { get; set; } = DrinkRarity.Blue;

	[Property, Group( "Bar" ), Description( "How often it comes up against the other drinks of the same rarity. 1 is normal, 0.5 is half as often, 0 is never." )]
	public float Weight { get; set; } = 1f;

	[Property, Group( "Bar" ), Description( "Once you've drunk it, the bar stops pouring it for the rest of the night. For unlocks, where a second one would only buy you its downside again." )]
	public bool OnePerNight { get; set; }

	[Property, Group( "Bar" ), Description( "Price, paid from the same money that settles your tab. Flat for now: scaling it with the round is a change in BarShop, not here." )]
	public int Price { get; set; } = 50;

	[Property, Group( "Bar" ), Description( "The bottle's colour on the counter." )]
	public Color Tint { get; set; } = Color.White;

	[Property, Group( "Bar" ), Description( "Optional. This drink's own bottle on the counter. Left empty it uses the bar's default model, which is what most drinks want — set one once a drink stops being a beer." )]
	public Model Model { get; set; }
}
