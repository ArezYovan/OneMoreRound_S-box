using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// The bar between rounds. Put this on the bottle rig on the counter somewhere off to the side of the level, give it
/// a spot for the camera and the rig's slot bones, and it runs the section: when the run reaches the bar the screen
/// fades in on the counter, the cursor appears, and three drinks slide in to choose from.
///
/// The drinks only arrive once "one more round?" has been answered. The choice is made on what you have survived
/// rather than on what happens to be affordable — deciding to push on because a good bottle came up is a different,
/// worse game.
///
/// The rig's animgraph does the moving: <c>Bottles_In</c>, then <c>Bottles_Idle</c>, and the reroll bool sends it
/// through <c>Bottles_out</c> and back in. Two generic events in the animations tell this where it is - the swap event
/// near the end of the way out, when the bottles are out of sight and get replaced, and the ready event near the end
/// of the way in. Opening the bar goes round the same loop with an empty rig, so the first three slide in as well.
///
/// It owns that section and nothing else. The money and what walking out means stay in <see cref="RunManager"/>; the
/// camera move is asked of <see cref="CameraMovement"/>, which is the only thing allowed to place the camera; and what
/// a drink does to you belongs to the upgrades — this only announces the purchase through
/// <see cref="OnDrinkBought"/>.
/// </summary>
public sealed class BarShop : Component
{
	[Property, Group( "Set" ), Description( "Where the camera sits while you're at the bar: an empty in front of the counter, turned to face the bottles." )]
	public GameObject CameraSpot { get; set; }

	[Property, Group( "Set" ), Description( "The animated rig the bottles ride on. Found on this object or under it if left empty. Without one the bottles simply appear in their slots." )]
	public SkinnedModelRenderer Rig { get; set; }

	[Property, Group( "Set" ), Description( "Where the bottles stand, one each: the rig's slot bones (tick Create Bone Objects on the rig), or plain empties without a rig. Three for now — the bar offers as many drinks as there are slots." )]
	public List<GameObject> Slots { get; set; } = new();

	[Property, Group( "Set" ), Description( "The default bottle on the counter, tinted to each drink. A drink with a model of its own uses that instead." )]
	public Model BottleModel { get; set; }

	[Property, Group( "Set" ), Description( "How much fatter than the bottle its box is. The cursor doesn't need it — bottles are picked off the cursor's ray rather than traced against a collider — but it still gives the bottle a shape in the world." )]
	public float HoverPadding { get; set; } = 6f;

	[Property, Group( "Set" ), Description( "How near the cursor's line has to pass a bottle, in world units, to count as pointing at it. Generous on purpose — a fiddly pick is the fastest way to make a menu feel bad." )]
	public float PickRadius { get; set; } = 16f;

	[Property, Group( "Drinks" ), Description( "How likely each slot is to be a blue can, against the other two. Only the ratio between the three matters. The bar pours from every drink asset in the game; which ones are blue is set on the drink." )]
	public float BlueOdds { get; set; } = 55f;

	[Property, Group( "Drinks" ), Description( "How likely each slot is to be a purple glass." )]
	public float PurpleOdds { get; set; } = 35f;

	[Property, Group( "Drinks" ), Description( "How likely each slot is to be a gold bottle. 10 against 55 and 35 is one slot in ten, so roughly one set of three in four has a gold in it." )]
	public float GoldOdds { get; set; } = 10f;

	[Property, Group( "Drinks" ), Description( "Added to the gold odds for every set that comes out with no gold in it, and cleared the moment one shows — so a dry streak can't run on for long. At 5, four gold-less sets in a row make the next slot about one in four." )]
	public float GoldPity { get; set; } = 5f;

	[Property, Group( "Drinks" ), Description( "What a fresh set of three costs. The offers are random every visit, so this is how you fish for the drink you want." )]
	public int RerollPrice { get; set; } = 25;

	[Property, Group( "Feel" ), Description( "How long the fade to and from black takes." )]
	public float FadeTime { get; set; } = 0.35f;

	[Property, Group( "Feel" ), Description( "How long the screen stays fully black once the bar opens, before it fades in on the counter. The camera cuts across underneath it, so raise this if arriving still feels abrupt." )]
	public float BlackHold { get; set; } = 1f;

	[Property, Group( "Feel" ), Description( "How far the view leans toward the cursor: degrees left and right, then up and down. Small — it's a lean, not a look." )]
	public Vector2 CameraLean { get; set; } = new Vector2( 6f, 3f );

	[Property, Group( "Feel" ), Description( "How quickly the camera settles into the bar, and back out to the player afterwards." )]
	public float BlendSpeed { get; set; } = 6f;

	[Property, Group( "Animation" ), Description( "The rig's bool that sends the bottles out and back in." )]
	public string RerollParameter { get; set; } = "ReRoll";

	[Property, Group( "Animation" ), Description( "Generic event near the end of Bottles_out, when the old bottles are out of sight and the new ones go on." )]
	public string SwapEvent { get; set; } = "bottles_swap";

	[Property, Group( "Animation" ), Description( "Generic event near the end of Bottles_In. Rerolling waits for it, so you can't reroll bottles that are still arriving." )]
	public string ReadyEvent { get; set; } = "bottles_ready";

	[Property, Group( "Sound" ), Description( "Played as a fresh set of bottles comes onto the counter — the first three, and every re-roll." )]
	public SoundEvent BottlesArriveSound { get; set; }

	[Property, Group( "Sound" ), Description( "Played when the pointer moves onto a bottle." )]
	public SoundEvent BottleHoverSound { get; set; }

	[Property, Group( "Sound" ), Description( "Played when a bottle is bought." )]
	public SoundEvent BuySound { get; set; }

	[Property, Group( "Sound" ), Description( "Played when you click a bottle or re-roll you can't pay for." )]
	public SoundEvent CantAffordSound { get; set; }

	[Property, Group( "Sound" ), Description( "Played when a re-roll is paid for, as the old bottles go." )]
	public SoundEvent RerollSound { get; set; }

	[Property, Group( "Debug" )] public bool LogShop { get; set; } = true;

	/// <summary>Fired when a drink is paid for. The upgrades hang off this.</summary>
	public Action<Drink> OnDrinkBought { get; set; }

	/// <summary>Whether the bar is up, so the HUD knows to draw itself.</summary>
	public bool IsOpen { get; private set; }

	/// <summary>How black the screen is, 0 to 1. The shop HUD draws it, and it covers both the way in and the way out.</summary>
	public float Fade { get; private set; }

	/// <summary>The bottle under the cursor, or null. The card follows it.</summary>
	public ShopBottle Hovered { get; private set; }

	/// <summary>The run this bar belongs to. Found once, lazily, so there's nothing to wire up.</summary>
	public RunManager Run => run.IsValid() ? run : ( run = Scene.GetAllComponents<RunManager>().FirstOrDefault() );

	/// <summary>The bottles are on their way out or in. Nothing can be rerolled until they've landed.</summary>
	public bool Restocking => waitingForSwap || arriving;

	/// <summary>The drinks are only out once the question has been answered — that's the whole shape of the section.</summary>
	public bool Stocked => Run.IsValid() && Run.Phase == RunPhase.Shop;

	public bool CanReroll => IsOpen && Stocked && !Restocking && Run.IsValid() && Run.Money >= RerollCost;

	/// <summary>The drinker, if there is one. What they have drunk already is what makes the next round cheaper.</summary>
	public PlayerUpgrades Upgrades => upgrades.IsValid() ? upgrades : ( upgrades = PlayerUpgrades.Find( Scene ) );

	/// <summary>
	/// What this drink costs YOU, which is not always what the asset says: a drink that has taught you to haggle
	/// takes something off everything after it. The card, the bottle and the till all ask this rather than reading
	/// the price off the asset, so there is one answer and they cannot disagree about it.
	/// </summary>
	public int PriceOf( Drink drink ) => drink is null ? 0 : YourPrice( drink.Price );

	/// <summary>What a fresh three costs you. Haggling counts here too — it is the same bar.</summary>
	public int RerollCost => YourPrice( RerollPrice );

	// how long to wait for an animation event before carrying on without it, so a missing or misnamed event is
	// a warning and a pop rather than a bar that never stocks
	const float EventTimeout = 3f;

	RunManager run;
	PlayerUpgrades upgrades;
	CameraMovement look;
	MouseVisibility previousCursor;
	TimeSince timeSinceOpened;
	TimeSince timeSinceRestockAsked;
	bool waitingForSwap;
	bool arriving;
	bool stockedThisVisit;

	// sets poured in a row with no gold in them, for GoldPity
	int goldDrought;

	// the bottle the hover sound last played for, so it sounds once per bottle rather than every frame
	ShopBottle lastHovered;

	readonly List<ShopBottle> bottles = new();

	protected override void OnStart()
	{
		Rig ??= Components.Get<SkinnedModelRenderer>( FindMode.EverythingInSelfAndDescendants );

		if ( Rig.IsValid() )
			Rig.OnGenericEvent += OnRigEvent;

		if ( !Run.IsValid() )
		{
			Log.Warning( $"{GameObject.Name}: BarShop found no RunManager — the bar will never open." );
			return;
		}

		// the run's own Space/E stand-in stands aside while this is here
		Run.ShopHandled = true;
	}

	protected override void OnDestroy()
	{
		if ( IsOpen )
			Close();

		if ( Rig.IsValid() )
			Rig.OnGenericEvent -= OnRigEvent;

		if ( run.IsValid() )
			run.ShopHandled = false;
	}

	protected override void OnUpdate()
	{
		UpdateFade();

		// Polled rather than hooked off the phase event: the player can arrive a frame after the night
		// reaches the bar, and a bar that had missed its one notification would stay dark for the whole
		// visit. Asking every frame is cheap and can't be missed.
		bool wanted = Run.IsValid() && Run.AtTheBar;

		if ( wanted != IsOpen )
		{
			if ( wanted )
				Open();
			else
				Close();
		}

		if ( !IsOpen )
			return;

		// the camera resolves late — it can take a moment for the player to exist — so keep asking rather
		// than giving up on whatever wasn't there when the bar opened
		if ( !look.IsValid() )
			TakeTheView();

		// the drinks arrive only once the question has been answered
		if ( Stocked && !stockedThisVisit )
		{
			stockedThisVisit = true;
			SendBottlesRound();
		}

		UpdateRestockTimeout();
		UpdateHover();

		if ( Hovered != lastHovered )
		{
			lastHovered = Hovered;

			if ( Hovered.IsValid() )
				UiSound.Play( BottleHoverSound );
		}

		foreach ( var bottle in bottles )
		{
			if ( !bottle.IsValid() )
				continue;

			bottle.Hovered = bottle == Hovered;
			bottle.SetAffordable( bottle.Drink is not null && Run.IsValid() && Run.Money >= PriceOf( bottle.Drink ) );
		}

		// a click anywhere else — the counter, the floor — simply misses. The HUD's buttons handle their own
		// not while paused: a click on the pause menu would otherwise buy whatever bottle sits under it
		if ( Hovered.IsValid() && Input.Pressed( "Attack1" ) && Scene.GetSystem<HitStop>() is not { IsPaused: true } )
			Buy( Hovered );
	}

	/// <summary>Point the player's camera at the counter. Separate from Open so it can be retried.</summary>
	void TakeTheView()
	{
		look = CameraMovement.Find( GameObject );

		if ( !look.IsValid() )
			return;

		if ( !CameraSpot.IsValid() )
		{
			Log.Warning( $"{GameObject.Name}: no CameraSpot assigned — the view stays on the player." );
			return;
		}

		look.SetViewOverride( CameraSpot, CameraLean, BlendSpeed, instant: true );
	}

	/// <summary>Open the bar: the screen is already black, so take the camera, show the cursor and fade in.</summary>
	void Open()
	{
		IsOpen = true;
		Fade = 1f;
		timeSinceOpened = 0;
		stockedThisVisit = false;

		ClearBottles();
		TakeTheView();

		previousCursor = Mouse.Visibility;
		Mouse.Visibility = MouseVisibility.Visible;

		if ( LogShop )
			Log.Info( $"[Bar] open — ${( Run.IsValid() ? Run.Money : 0 )} in hand" );
	}

	/// <summary>Leave the bar: bottles away, camera and cursor back to the player. The fade clears itself.</summary>
	void Close()
	{
		IsOpen = false;
		Hovered = null;
		waitingForSwap = false;
		arriving = false;
		stockedThisVisit = false;

		if ( Rig.IsValid() && !string.IsNullOrEmpty( RerollParameter ) )
			Rig.Set( RerollParameter, false );

		ClearBottles();

		if ( look.IsValid() )
			look.ClearViewOverride( instant: Fade > 0.9f );

		look = null;

		Mouse.Visibility = previousCursor;
	}

	/// <summary>
	/// Three fresh drinks. Each slot rolls its rarity first — blue, purple or gold, on the odds above — then picks a
	/// drink off that shelf. Never two the same on the counter while there are enough to go round, and never a
	/// one-per-night drink you've already had.
	/// </summary>
	public void Restock()
	{
		ClearBottles();

		var pool = Available();

		if ( pool.Count == 0 )
			return;

		var poured = new List<Drink>();

		for ( int i = 0; i < Slots.Count; i++ )
		{
			if ( !Slots[i].IsValid() )
				continue;

			var drink = Pick( pool, poured );

			if ( drink is null )
				break;

			poured.Add( drink );
			bottles.Add( Build( Slots[i], drink, i ) );
		}

		goldDrought = poured.Any( d => d.Rarity == DrinkRarity.Gold ) ? 0 : goldDrought + 1;

		if ( poured.Count > 0 )
			UiSound.Play( BottlesArriveSound );

		if ( LogShop )
			Log.Info( $"[Bar] poured {string.Join( ", ", poured.Select( d => $"{d.Name} ({d.Rarity})" ) )}" );
	}

	/// <summary>One drink for one slot: a rarity rolled on the odds, then a drink off that shelf by its weight.</summary>
	Drink Pick( List<Drink> pool, List<Drink> onTheCounter )
	{
		var open = pool.Where( d => Pourable( d ) && !onTheCounter.Contains( d ) ).ToList();

		// fewer drinks left than slots: a repeat on the counter beats an empty slot
		if ( open.Count == 0 )
			open = pool.Where( Pourable ).ToList();

		if ( open.Count == 0 )
			return null;

		var rarity = RollRarity( open );
		return PickByWeight( open.Where( d => d.Rarity == rarity ).ToList() );
	}

	/// <summary>Can this come up at all: weighted above nothing, and not a one-per-night drink already on your tab.</summary>
	bool Pourable( Drink drink ) =>
		drink is not null && drink.Weight > 0f && !( drink.OnePerNight && Upgrades.IsValid() && Upgrades.CountOf( drink ) > 0 );

	/// <summary>
	/// Which shelf this slot comes off. A shelf with nothing left on it can't come up — its odds go to the others —
	/// so owning every gold simply means golds stop appearing, never an empty slot.
	/// </summary>
	DrinkRarity RollRarity( List<Drink> open )
	{
		float blue = open.Any( d => d.Rarity == DrinkRarity.Blue ) ? MathF.Max( 0f, BlueOdds ) : 0f;
		float purple = open.Any( d => d.Rarity == DrinkRarity.Purple ) ? MathF.Max( 0f, PurpleOdds ) : 0f;
		float gold = open.Any( d => d.Rarity == DrinkRarity.Gold ) ? MathF.Max( 0f, GoldOdds + GoldPity * goldDrought ) : 0f;

		float total = blue + purple + gold;

		// every shelf that's left is set to never: pour off whichever one there is rather than nothing
		if ( total <= 0f )
			return open[Game.Random.Next( open.Count )].Rarity;

		float roll = (float)Game.Random.NextDouble() * total;

		if ( roll < blue )
			return DrinkRarity.Blue;

		if ( roll < blue + purple )
			return DrinkRarity.Purple;

		return DrinkRarity.Gold;
	}

	/// <summary>One drink off a shelf, a Weight of 2 coming up twice as often as a 1.</summary>
	static Drink PickByWeight( List<Drink> shelf )
	{
		float total = shelf.Sum( d => d.Weight );
		float roll = (float)Game.Random.NextDouble() * total;

		foreach ( var drink in shelf )
		{
			roll -= drink.Weight;

			if ( roll < 0f )
				return drink;
		}

		// floating-point leftovers at the very top of the roll
		return shelf[^1];
	}

	/// <summary>Pay for a bottle. Nothing happens if you can't afford it — its card already says so.</summary>
	public bool Buy( ShopBottle bottle )
	{
		if ( !IsOpen || !Stocked || waitingForSwap || !bottle.IsValid() || bottle.Taken || bottle.Drink is null )
			return false;

		if ( !Run.IsValid() )
			return false;

		if ( !Run.TrySpend( PriceOf( bottle.Drink ) ) )
		{
			UiSound.Play( CantAffordSound );
			return false;
		}

		UiSound.Play( BuySound );
		bottle.Take();
		Hovered = null;

		if ( LogShop )
			Log.Info( $"[Bar] bought {bottle.Drink.Name} for ${PriceOf( bottle.Drink )} — ${Run.Money} left" );

		OnDrinkBought?.Invoke( bottle.Drink );
		return true;
	}

	/// <summary>Pay for a fresh three. Whatever was on the counter goes back under it.</summary>
	public bool Reroll()
	{
		if ( !CanReroll )
		{
			// pressed while the bottles are still moving is simply ignored; pressed short of money is told so
			if ( IsOpen && Stocked && !Restocking && Run.IsValid() && Run.Money < RerollCost )
				UiSound.Play( CantAffordSound );

			return false;
		}

		if ( !Run.TrySpend( RerollCost ) )
			return false;

		UiSound.Play( RerollSound );
		Hovered = null;
		SendBottlesRound();

		if ( LogShop )
			Log.Info( $"[Bar] rerolled for ${RerollCost} — ${Run.Money} left" );

		return true;
	}

	// never below nothing, however good a haggler you become
	int YourPrice( int price ) => Upgrades.IsValid()
		? Math.Max( 0, (int)MathF.Round( Upgrades.Against( price, PlayerStat.Haggle ) ) )
		: price;

	/// <summary>
	/// Black while the bar opens — the camera cuts to the counter under cover of it — over the last moment of
	/// the payout beat, so the switch to the bar has already gone dark before it happens, and again on the way
	/// out to the next round.
	/// </summary>
	void UpdateFade()
	{
		float target = WantsBlack() ? 1f : 0f;

		// real time, not the scene's: the fade out of a round runs through the finishing slow motion, and has to
		// be fully black by the time the bar cuts in underneath it
		float step = RealTime.Delta / MathF.Max( FadeTime, 0.01f );

		Fade = target > Fade
			? MathF.Min( target, Fade + step )
			: MathF.Max( target, Fade - step );
	}

	bool WantsBlack()
	{
		if ( !Run.IsValid() )
			return false;

		if ( Run.LeavingBar )
			return true;

		if ( IsOpen )
			return timeSinceOpened < BlackHold;

		// the beat between clearing a round and the bar
		float left = Run.Phase switch
		{
			RunPhase.Paid => Run.PaidDelay - Run.PaidTime,
			_ => float.MaxValue
		};

		return left <= FadeTime;
	}

	/// <summary>
	/// Which bottle the cursor is on: whichever of the three sits nearest to the cursor's own ray into the
	/// world, within <see cref="PickRadius"/> of it.
	///
	/// Picked in the world rather than traced, because a trace does not work here: a bottle is built at
	/// runtime with a collider and no rigidbody, and it re-places itself from the animated bone every frame in
	/// OnPreRender — which runs after the frame's tracing, so the shape a trace could find is always behind
	/// where the bottle was drawn, when the bar rig's own geometry isn't in front of it entirely.
	/// </summary>
	void UpdateHover()
	{
		Hovered = null;

		var camera = Scene.Camera;

		// the bottles on their way out are already sold off - the new ones are pointable the moment they're on
		if ( !camera.IsValid() || !Stocked || waitingForSwap )
			return;

		var ray = camera.ScreenPixelToRay( Mouse.Position );
		float best = PickRadius;

		foreach ( var bottle in bottles )
		{
			if ( !bottle.IsValid() || bottle.Taken )
				continue;

			var to = bottle.CardAnchor - ray.Position;
			float along = Vector3.Dot( to, ray.Forward );

			// behind the camera
			if ( along <= 0f )
				continue;

			float off = ( to - ray.Forward * along ).Length;

			if ( off > best )
				continue;

			best = off;
			Hovered = bottle;
		}
	}

	/// <summary>
	/// Out and back in: the rig slides whatever's on the counter away, the new three go on at the swap event, and
	/// they slide in. With no rig there's nothing to wait for, so they're simply stocked.
	/// </summary>
	void SendBottlesRound()
	{
		if ( !Rig.IsValid() || string.IsNullOrEmpty( RerollParameter ) )
		{
			Restock();
			return;
		}

		waitingForSwap = true;
		arriving = true;
		timeSinceRestockAsked = 0;
		Rig.Set( RerollParameter, true );
	}

	void OnRigEvent( SceneModel.GenericEvent e )
	{
		if ( !IsOpen )
			return;

		if ( waitingForSwap && string.Equals( e.Type, SwapEvent, StringComparison.OrdinalIgnoreCase ) )
		{
			Swap();
			return;
		}

		if ( arriving && string.Equals( e.Type, ReadyEvent, StringComparison.OrdinalIgnoreCase ) )
			arriving = false;
	}

	/// <summary>The old bottles are out of sight: the new ones go on, and the bool is let go so the loop runs once.</summary>
	void Swap()
	{
		waitingForSwap = false;
		timeSinceRestockAsked = 0;

		if ( !string.IsNullOrEmpty( RerollParameter ) )
			Rig.Set( RerollParameter, false );

		Restock();
	}

	/// <summary>
	/// If an event never comes - a name typed differently in ModelDoc, or the reroll landing while the rig was still
	/// arriving - the bar carries on without it rather than sitting empty or locking the reroll forever.
	/// </summary>
	void UpdateRestockTimeout()
	{
		if ( !Restocking || timeSinceRestockAsked < EventTimeout )
			return;

		if ( waitingForSwap )
		{
			Log.Warning( $"[Bar] no '{SwapEvent}' event from the rig in {EventTimeout}s - stocking without it" );
			Swap();
			return;
		}

		Log.Warning( $"[Bar] no '{ReadyEvent}' event from the rig in {EventTimeout}s - letting rerolls through" );
		arriving = false;
	}

	/// <summary>A bottle in a slot: the model tinted to the drink, a forgiving box to point at, and the behaviour.</summary>
	ShopBottle Build( GameObject slot, Drink drink, int index )
	{
		var go = new GameObject( true, $"Bottle {index + 1} — {drink.Name}" );
		// under the bar rather than the bone: the bottle places itself from the animated bone every frame, and the
		// bone's own GameObject isn't moved by the animation
		go.SetParent( GameObject, false );

		// a drink can bring its own bottle; most do not, and fall back to the bar's
		var model = drink.Model ?? BottleModel;

		var renderer = go.Components.Create<ModelRenderer>();
		renderer.Model = model;

		var bounds = model is not null ? model.Bounds : BBox.FromPositionAndSize( Vector3.Up * 8f, 16f );
		var box = go.Components.Create<BoxCollider>();
		box.Center = bounds.Center;
		box.Scale = bounds.Size + HoverPadding * 2f;

		var bottle = go.Components.Create<ShopBottle>();
		bool onBone = Rig.IsValid() && Rig.Model is not null && Rig.Model.Bones.HasBone( slot.Name );
		bottle.Setup( drink, renderer, box, slot, onBone ? Rig : null, onBone ? slot.Name : null, UprightIn( slot ) );

		return bottle;
	}

	/// <summary>
	/// What turns a slot's axes into a standing bottle. A bone points wherever the rig put it, so this undoes the
	/// bone's rest pose: at rest the bottle then stands exactly as the rig does, and any animated tilt comes through
	/// on top. A plain empty needs nothing.
	/// </summary>
	Rotation UprightIn( GameObject slot )
	{
		var model = Rig.IsValid() ? Rig.Model : null;

		if ( model is null || !model.Bones.HasBone( slot.Name ) )
			return Rotation.Identity;

		return model.GetBoneTransform( slot.Name ).Rotation.Inverse;
	}

	void ClearBottles()
	{
		foreach ( var bottle in bottles )
		{
			if ( bottle.IsValid() )
				bottle.GameObject.Destroy();
		}

		bottles.Clear();
		Hovered = null;
	}

	/// <summary>What this bar can pour: every drink asset there is, or placeholders while there are none.</summary>
	List<Drink> Available()
	{
		var all = ResourceLibrary.GetAll<Drink>().ToList();

		if ( all.Count > 0 )
			return all;

		if ( LogShop )
			Log.Info( "[Bar] no drink assets yet — pouring placeholders so the bar still plays" );

		return Placeholders();
	}

	/// <summary>
	/// Stand-ins so the section can be played before a single drink asset exists. Written in code on purpose:
	/// the moment there's one real .drink asset these are never poured again.
	/// </summary>
	static List<Drink> Placeholders() => new()
	{
		new Drink
		{
			Name = "House Lager",
			Flavour = "Flat, warm, honest.",
			Price = 40,
			Tint = new Color( 0.85f, 0.65f, 0.25f ),
			Upsides = { "Steadier hands" },
			Downsides = { "A little slower on your feet" }
		},
		new Drink
		{
			Name = "Well Whiskey",
			Flavour = "Burns on the way down.",
			Price = 70,
			Tint = new Color( 0.7f, 0.35f, 0.15f ),
			Upsides = { "Hits harder", "Gets up quicker" },
			Downsides = { "The room tilts" }
		},
		new Drink
		{
			Name = "Something Green",
			Flavour = "Nobody remembers ordering it.",
			Price = 55,
			Tint = new Color( 0.35f, 0.8f, 0.4f ),
			Upsides = { "Lasts longer between breaths" },
			Downsides = { "Glass jaw" }
		}
	};
}
