using System;

/// <summary>
/// The guard. Hold the key and your hands come up; a blow swung at your front simply does not land —
/// no damage, no shove, nothing. Let go and it drops.
///
/// Still a plain version of the idea: no chip damage, no slower walk, and no limit on how long you can
/// hold it. Blocking is free until a drink puts a stamina price on it (see <see cref="StaminaCost"/>),
/// and raising the guard just in time is a parry.
///
/// The attacker asks rather than the victim filtering. <see cref="Enemy.Strike"/> calls
/// <see cref="TryBlock"/> before it does anything at all, so a blocked swing costs the man his turn and
/// never touches you — which is also why a blocked hit can't shove you around. Anything else that wants
/// to be blockable (a thrown bottle, say) asks the same question in the same place.
/// </summary>
public sealed class PlayerBlock : Component
{
	[Property, Group( "Input" ), Description( "Held to keep the guard up. Flashlight is F by default and nothing else uses it since the grapple strike went — change it here rather than in the animgraph." )]
	public string InputAction { get; set; } = "Attack2";

	[Property, Group( "Guard" ), Description( "How wide the guard is, in degrees across your front. 140 covers what you are looking at and a good deal either side, and still lets a man who has walked round behind you land one. Turn it up to 360 to see what an unflankable guard feels like." )]
	public float GuardAngle { get; set; } = 140f;

	[Property, Group( "Guard" ), Description( "Your hands are busy: no jab, hook or kick while the guard is up. Off, you can throw punches out of the block pose, which plays wrong but is worth feeling once before deciding." )]
	public bool StopsAttacks { get; set; } = true;

	[Property, Group( "Animation" ), Description( "The body whose animgraph has the block pose. Found automatically on the player if left empty." )]
	public SkinnedModelRenderer Renderer { get; set; }

	[Property, Group( "Animation" ), Description( "The animgraph bool that raises the guard." )]
	public string BlockParameter { get; set; } = "IsBlocking";

	[Property, Group( "Animation" ), Description( "Bool fired once on a parry, for the parry clip. Tick Auto Reset on it in the graph. Leave empty for no parry animation." )]
	public string ParryParameter { get; set; } = "b_parry";

	[Property, Group( "Feel" ), Description( "Played where you stand when a blow is turned aside. Any thud will do to feel it — a real block sound can come later." )]
	public SoundEvent BlockSound { get; set; }

	[Property, Group( "Cost" ), Description( "Wind each blocked blow costs. Free as standing; drinks with a Guard downside raise it. Can't pay and the guard breaks — the blow lands as if it were never up. A parry never costs anything." )]
	public float StaminaCost { get; set; } = 0f;

	[Property, Group( "Cost" ), Description( "Played when a blow breaks through a guard you couldn't pay for. Leave empty for silence." )]
	public SoundEvent GuardBreakSound { get; set; }

	[Property, Group( "Parry" ), Description( "Raise the guard this close before a blow lands and it's a parry, not a block: he's knocked off balance and open, and time slows for your counter. Only used by blows that don't set their own window — the enemy's moves each carry their own, and his kick carries none at all. Zero turns parrying off." )]
	public float ParryWindow { get; set; } = 0.2f;

	[Property, Group( "Parry" ), Description( "How slow time runs after a parry. 0.3 is a third of normal speed." )]
	public float ParrySlowScale { get; set; } = 0.3f;

	[Property, Group( "Parry" ), Description( "How long the slow motion lasts, in real seconds, if you don't counter first. Throwing any attack ends it straight away." )]
	public float ParrySlowTime { get; set; } = 1.5f;

	[Property, Group( "Parry" ), Description( "The freeze-frame on the parry itself, before the slow motion takes over." )]
	public float ParryHitStop { get; set; } = 0.08f;

	[Property, Group( "Parry" ), Description( "Camera jolt on a parry, 0–1." )]
	public float ParryShake { get; set; } = 0.35f;

	[Property, Group( "Parry" ), Description( "Played on a parry instead of the block sound. Leave empty to use the block sound." )]
	public SoundEvent ParrySound { get; set; }

	[Property, Group( "Debug" )] public bool LogBlocks { get; set; } = true;

	/// <summary>Fired when a blow is turned aside, with whoever swung it. For feedback that isn't built yet.</summary>
	public Action<GameObject> OnBlocked;

	/// <summary>Fired on a parry — a block with the guard raised just in time — with whoever swung.</summary>
	public Action<GameObject> OnParried;

	/// <summary>Guard up right now. False while knocked out or held by the run, whatever the key is doing.</summary>
	public bool IsBlocking { get; private set; }

	/// <summary>How many blows this guard has turned aside. Handy while tuning the arc.</summary>
	public int Blocked { get; private set; }

	RBPlayerMovement movement;
	PlayerHealth playerHealth;
	CameraMovement look;
	Stamina stamina;

	// when the guard last came up — a blow meeting it inside ParryWindow is a parry
	TimeSince timeSinceRaised;

	protected override void OnStart()
	{
		var search = FindMode.EverythingInSelfAndDescendants;

		movement = Components.Get<RBPlayerMovement>( search );
		playerHealth = Components.Get<PlayerHealth>( search );
		stamina = Components.Get<Stamina>( search );
		Renderer ??= Components.Get<SkinnedModelRenderer>( search );
		look = CameraMovement.Find( GameObject );

		if ( !Renderer.IsValid() )
			Log.Warning( $"{GameObject.Name}: PlayerBlock found no SkinnedModelRenderer — blocking will work, but nothing will show it." );
	}

	protected override void OnUpdate()
	{
		if ( IsProxy )
			return;

		bool wasBlocking = IsBlocking;
		IsBlocking = CanGuard() && Input.Down( InputAction );

		if ( IsBlocking && !wasBlocking )
			timeSinceRaised = 0;

		// set every frame rather than on the edge: the graph then cannot be left holding a pose it was
		// put into on a frame something else happened to eat
		if ( Renderer.IsValid() && !string.IsNullOrEmpty( BlockParameter ) )
			Renderer.Set( BlockParameter, IsBlocking );
	}

	/// <summary>
	/// Does this blow land, or does it come off the guard? Ask BEFORE dealing damage or applying an
	/// impulse — a blocked blow does neither. <paramref name="from"/> is where the blow is coming from:
	/// the attacker for a punch, the prop itself for something thrown.
	/// </summary>
	public bool TryBlock( GameObject attacker, Vector3 from ) => TryBlock( attacker, from, ParryWindow, out _ );

	/// <summary>
	/// As <see cref="TryBlock(GameObject, Vector3)"/>, and says whether it was a parry: the guard came up within
	/// <paramref name="parryWindow"/> of the blow — the blow's own window, since a fast jab should be harder to
	/// catch than a slow swing, and some blows (his kick) can't be caught at all. A parry is still a block — nothing lands — and on top of it time slows for your
	/// counter. What a parry does to whoever swung is his to decide; he's the one who asked.
	/// </summary>
	public bool TryBlock( GameObject attacker, Vector3 from, float parryWindow, out bool parried )
	{
		parried = false;

		if ( !IsBlocking || !InGuard( from ) )
			return false;

		parried = parryWindow > 0f && timeSinceRaised <= parryWindow;

		string who = attacker.IsValid() ? attacker.Name : "something";

		// a plain block has to be paid for once a drink puts a price on it; the parry is the reward, and stays free
		if ( !parried && StaminaCost > 0f && stamina.IsValid() && !stamina.TrySpend( StaminaCost ) )
		{
			if ( GuardBreakSound is not null )
				Sfx.Play( GuardBreakSound, WorldPosition );

			if ( LogBlocks )
				Log.Info( $"[Block] too winded to hold the guard — {who} breaks through" );

			return false;
		}

		Blocked++;

		var sound = parried && ParrySound is not null ? ParrySound : BlockSound;

		if ( sound is not null )
			Sfx.Play( sound, WorldPosition );

		if ( parried )
		{
			var clock = Scene.GetSystem<HitStop>();
			clock.Freeze( ParryHitStop, 0.05f );
			clock.SlowMo( ParrySlowTime, ParrySlowScale );
			look?.Shake( ParryShake );

			if ( Renderer.IsValid() && !string.IsNullOrEmpty( ParryParameter ) )
				Renderer.Set( ParryParameter, true );

			if ( LogBlocks )
				Log.Info( $"[Block] PARRIED {who} — guard up {(float)timeSinceRaised:0.00}s before the blow" );

			OnParried?.Invoke( attacker );
		}
		else if ( LogBlocks )
		{
			Log.Info( $"[Block] turned aside {who} — {Blocked} this run" );
		}

		OnBlocked?.Invoke( attacker );
		return true;
	}

	/// <summary>Is that direction inside the guard? Flat, so a man on a table is still in front of you.</summary>
	public bool InGuard( Vector3 from )
	{
		if ( GuardAngle >= 360f )
			return true;

		var toward = ( from - WorldPosition ).WithZ( 0f );

		// standing inside each other: nothing to measure, so give it to the guard
		if ( toward.IsNearlyZero() )
			return true;

		return Vector3.GetAngle( Facing, toward.Normal ) <= GuardAngle * 0.5f;
	}

	/// <summary>
	/// Where the guard points. Orientation rather than the camera: the camera carries bob, tilt and the
	/// landing spring, and a guard that wobbled with them would drop men through it at random.
	/// </summary>
	Vector3 Facing
	{
		get
		{
			if ( movement.IsValid() && movement.Orientation.IsValid() )
				return movement.Orientation.WorldRotation.Forward;

			return WorldRotation.Forward;
		}
	}

	bool CanGuard()
	{
		if ( playerHealth.IsValid() && playerHealth.IsKnockedOut )
			return false;

		// the same gate punching and every ability obey: the countdown, the bar, the end of the night
		return !( movement.IsValid() && movement.ActionsBlocked );
	}
}
