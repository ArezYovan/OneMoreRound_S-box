using System;

public enum MeleeAttack
{
	Jab,
	Hook,
	Kick
}

/// <summary>
/// Unarmed melee: a fast jab, a heavier hook, and a shove of a kick. Every attack is the
/// same three beats — windup, one sphere trace on the frame it lands, then recovery — so
/// they only differ by numbers, and "make the hook feel heavier" is a tuning job rather
/// than a code one.
///
/// When the hands have a clip for an attack, the windup belongs to the animation: the trace waits for
/// the clip's hit event, so the damage lands on the frame the fist visibly connects. Attacks without a
/// clip keep their windup timer.
///
/// Damage goes to a <see cref="Health"/> if the thing has one; knockback goes to a
/// <see cref="Rigidbody"/> if the thing has one. Neither needs the other, so a physics
/// crate flies without any health on it and a bolted-down dummy takes damage without moving.
/// </summary>
public sealed class PlayerCombat : Component
{
	[Property, Group( "References" ), Description( "Optional. Attacks are aimed down the camera. Falls back to this object's own rotation and a guessed eye height if unset." )]
	public CameraMovement Look { get; set; }

	[Property, Group( "References" ), Description( "Only used when Look is unset — how far up from this object's origin to swing from. Match it to the Head's height on the rig; the citizen's 64 is a long way under the current one." )]
	public float FallbackEyeHeight { get; set; } = 119f;

	[Property, Group( "References" ), Description( "Optional, found automatically. While something is in your hands, throwing it is the attack — swinging a fist through a barstool you're holding looks broken." )]
	public PlayerGrab Grab { get; set; }

	[Property, Group( "References" ), Description( "Optional, found automatically. The first-person hands. An attack they have a clip for lands on the frame its hit event plays; without hands, or without a clip, it lands on its windup timer." )]
	public HandAnimationHelper Hands { get; set; }

	[Property, Group( "Jab" ), Description( "Quick straight lead. Low commitment: you can throw three of these in the time one hook takes." )]
	public float JabDamage { get; set; } = 8f;
	[Property, Group( "Jab" )] public float JabRange { get; set; } = 70f;
	[Property, Group( "Jab" )] public float JabRadius { get; set; } = 6f;
	[Property, Group( "Jab" ), Description( "When the jab lands if the hands have no jab clip. With one, it lands on the clip's hit event instead, and this is only where HitEventGrace starts counting from." )]
	public float JabWindup { get; set; } = 0.05f;
	[Property, Group( "Jab" )] public float JabRecovery { get; set; } = 0.15f;
	[Property, Group( "Jab" )] public float JabKnockback { get; set; } = 90f;
	[Property, Group( "Jab" )] public float JabLift { get; set; } = 0.05f;

	[Property, Group( "Jab" ), Description( "The jab is the stagger tool. Tuned so one full stamina bar of jabs lands almost exactly one stagger — spend everything you have jabbing and the man goes down, which is the whole reason to throw the weakest punch in the game." )]
	public float JabStagger { get; set; } = 16f;

	[Property, Group( "Jab" )] public float JabStaminaCost { get; set; } = 14f;

	[Property, Group( "Hook" ), Description( "Committed swing from the side. Wider than the jab so it catches things the jab slips past, and slow enough that whiffing one costs you." )]
	public float HookDamage { get; set; } = 22f;
	[Property, Group( "Hook" )] public float HookRange { get; set; } = 64f;
	[Property, Group( "Hook" )] public float HookRadius { get; set; } = 14f;
	[Property, Group( "Hook" )] public float HookWindup { get; set; } = 0.18f;
	[Property, Group( "Hook" )] public float HookRecovery { get; set; } = 0.35f;
	[Property, Group( "Hook" )] public float HookKnockback { get; set; } = 260f;
	[Property, Group( "Hook" )] public float HookLift { get; set; } = 0.15f;

	[Property, Group( "Hook" ), Description( "A big single chunk of stagger. Roughly two and a bit hooks puts someone down, against seven jabs — slower overall, but it survives being interrupted." )]
	public float HookStagger { get; set; } = 45f;

	[Property, Group( "Hook" )] public float HookStaminaCost { get; set; } = 26f;

	[Property, Group( "Kick" ), Description( "The shove. Damage is almost beside the point — this is here to put something on the other side of the room." )]
	public float KickDamage { get; set; } = 15f;
	[Property, Group( "Kick" )] public float KickRange { get; set; } = 85f;
	[Property, Group( "Kick" )] public float KickRadius { get; set; } = 16f;
	[Property, Group( "Kick" )] public float KickWindup { get; set; } = 0.22f;
	[Property, Group( "Kick" )] public float KickRecovery { get; set; } = 0.45f;
	[Property, Group( "Kick" )] public float KickKnockback { get; set; } = 750f;

	[Property, Group( "Kick" ), Description( "How much of the blast is upwards. A little lift is what turns a push into a launch — at 0 a kick just scrapes things along the floor." )]
	public float KickLift { get; set; } = 0.35f;

	[Property, Group( "Kick" ), Description( "Middling on purpose. The kick already pays out in distance; letting it lead on stagger too would make the other two pointless." )]
	public float KickStagger { get; set; } = 30f;

	[Property, Group( "Kick" )] public float KickStaminaCost { get; set; } = 30f;

	[Property, Group( "Feel" ), Description( "Multiply knockback by the target's mass, so a heavy crate and a light can fly off at the same speed. Off, heavier things barely budge — realistic, but the kick stops reading as a kick." )]
	public bool ScaleKnockbackWithMass { get; set; } = true;

	[Property, Group( "Feel" ), Description( "A press this long before the current attack finishes still comes out, so mashing chains cleanly instead of eating inputs." )]
	public float InputBufferTime { get; set; } = 0.15f;

	[Property, Group( "Feel" ), Description( "Hold the attack button this long and it throws a hook instead of a jab. The hook comes out the instant the hold passes this; a tap throws its jab when you let go. Too short and quick taps come out as hooks, too long and the hook feels late." )]
	public float HookHoldTime { get; set; } = 0.18f;

	[Property, Group( "Feel" ), Description( "How long past its windup an animated attack keeps waiting for the hit event before landing anyway. A safety net, not a timing — if its warning shows up, the event is missing, misnamed, or placed later than this." )]
	public float HitEventGrace { get; set; } = 0.3f;

	[Property, Group( "Feel" ), Description( "A hit with at least this much knockback launches what it hits as a weapon: whoever it crashes into takes damage and stagger, exactly as if you'd thrown it. The kick clears this easily, the hook only just does, the jab never will — so it's also the line a knockback drink pushes an attack over." )]
	public float LaunchKnockback { get; set; } = 250f;

	[Property, Group( "Feel" ), Description( "How long something you launch stays dangerous. Short on purpose: a man still sliding a second after a kick is a nudge, not a hit — and an enemy is disarmed anyway the moment he's back on his feet." )]
	public float LaunchArmedTime { get; set; } = 1f;

	[Property, Group( "Drinks" ), Description( "Counter Punch: parry a man and your next hit while he's still reeling from it knocks him straight into a stagger, whatever his meter says. Unlocked by a drink; tick it here to try it without buying it." )]
	public bool CounterPunch { get; set; }

	[Property, Group( "Drinks" ), Description( "Bowling Ball: a man you kick flat into another knocks that one straight into a stagger. Only the kick — the hook's launch is too slow to read as bowling. Unlocked by a drink; tick it here to try it." )]
	public bool BowlingBall { get; set; }

	[Property, Group( "Drinks" ), Description( "Liquid Courage: the most extra melee damage low health gives you, in per cent. 0 is off; set by drinks. It grows as your health drops and is all there by LowHealthFraction." )]
	public float CourageBonus { get; set; }

	[Property, Group( "Drinks" ), Range( 0.05f, 1f ), Description( "Health fraction at which Liquid Courage is at its full bonus. At 0.2, a fifth of your health left is the whole +60%, and half your health is a little under half of it." )]
	public float LowHealthFraction { get; set; } = 0.2f;

	[Property, Group( "Hit Feel" ), Description( "Freeze-frame when a jab lands, in seconds. Short: the jab is a tap, not a thud." )]
	public float JabHitStop { get; set; } = 0.03f;
	[Property, Group( "Hit Feel" )] public float HookHitStop { get; set; } = 0.06f;
	[Property, Group( "Hit Feel" ), Description( "The kick gets the longest freeze — the beat before he flies is most of what sells it." )]
	public float KickHitStop { get; set; } = 0.1f;
	[Property, Group( "Hit Feel" ), Description( "Game speed during a freeze. Not quite zero, so it reads as a jolt rather than a hitch." )]
	public float HitStopScale { get; set; } = 0.05f;

	[Property, Group( "Hit Feel" ), Description( "Camera shake when a jab lands, 0–1. The overall strength lives on CameraMovement's ShakeStrength." )]
	public float JabShake { get; set; } = 0.2f;
	[Property, Group( "Hit Feel" )] public float HookShake { get; set; } = 0.4f;
	[Property, Group( "Hit Feel" )] public float KickShake { get; set; } = 0.6f;

	[Property, Group( "Homing" ), Description( "Attacks home in on an enemy: you're carried the last stretch toward him as you swing, and the punch is aimed at him rather than wherever the camera happens to point. Off, every attack goes exactly where you look." )]
	public bool Homing { get; set; } = true;

	[Property, Group( "Homing" ), Description( "How far away an enemy can be and still be homed in on. Locked on, your target counts out to half again this." )]
	public float HomingRange { get; set; } = 260f;

	[Property, Group( "Homing" ), Description( "How far off where you're looking an enemy can be and still be picked, in degrees either side. Wide makes attacks forgiving; narrow keeps them where you aim." )]
	public float HomingAngle { get; set; } = 35f;

	[Property, Group( "Homing" ), Description( "Most a jab carries you toward its target. Enough to close the gap a jab's own push opens, so you can keep jabbing into a man as he backs off." )]
	public float JabLunge { get; set; } = 70f;
	[Property, Group( "Homing" )] public float HookLunge { get; set; } = 100f;
	[Property, Group( "Homing" )] public float KickLunge { get; set; } = 110f;

	[Property, Group( "Homing" ), Description( "How long the lunge takes. Short so it reads as stepping into the punch, not sliding." )]
	public float LungeTime { get; set; } = 0.1f;

	[Property, Group( "Homing" ), Description( "The lunge stops once you're this close to him, centre to centre, so you punch him rather than walk into him." )]
	public float LungeStopDistance { get; set; } = 55f;

	[Property, Group( "Homing" ), Description( "How high on him the punch is aimed, measured up from his FEET — his root is on the floor, not at his chest. This has to track the rigs: the swing runs from your eye down to this point, so a value well below your own eye height turns every homing punch into a dive and costs you most of your horizontal reach. Chest on the current enemy is about 100." )]
	public float HomingAimHeight { get; set; } = 100f;

	[Property, Group( "References" ), Description( "Optional, found automatically. Without one, attacks are free and never tire you out." )]
	public Stamina Stamina { get; set; }

	[Property, Group( "References" ), Description( "Optional, found automatically. A man on the floor doesn't get to keep punching." )]
	public PlayerHealth PlayerHealth { get; set; }

	[Property, Group( "Sound" ), Description( "Played the moment the jab is thrown, hit or miss." )]
	public SoundEvent JabSwingSound { get; set; }
	[Property, Group( "Sound" ), Description( "Played where the jab connects." )]
	public SoundEvent JabHitSound { get; set; }
	[Property, Group( "Sound" )] public SoundEvent HookSwingSound { get; set; }
	[Property, Group( "Sound" )] public SoundEvent HookHitSound { get; set; }
	[Property, Group( "Sound" )] public SoundEvent KickSwingSound { get; set; }
	[Property, Group( "Sound" )] public SoundEvent KickHitSound { get; set; }
	[Property, Group( "Sound" ), Description( "Played when a swing reaches the end of its reach without touching anything. Leave empty if the swing sound already sells the whiff." )]
	public SoundEvent MissSound { get; set; }
	[Property, Group( "Sound" ), Description( "Played when you press attack without the stamina to throw it." )]
	public SoundEvent WindedSound { get; set; }

	[Property, Group( "Debug" )] public bool LogAttacks { get; set; } = true;

	[Property, Group( "Debug" ), Description( "Draw each swing in-game as the swept sphere the hit test actually used — not an approximation of it. White where it travelled through nothing, red where it connected." )]
	public bool DebugDraw { get; set; } = true;

	[Property, Group( "Debug" ), Description( "Seconds a swing's debug shapes linger before fading. Long enough to read after a fast jab, short enough that mashing doesn't bury the view." )]
	public float DebugDuration { get; set; } = 1.5f;

	[Property, Group( "Debug" ), Description( "Which attack's reach to outline in the scene view while this object is selected." )]
	public MeleeAttack GizmoPreview { get; set; } = MeleeAttack.Kick;

	public bool IsAttacking { get; private set; }
	public MeleeAttack CurrentAttack { get; private set; }

	/// <summary>True while an attack is carrying you into its target. RBPlayerMovement stands down for it, the same as for a dash.</summary>
	public bool IsLunging { get; private set; }

	/// <summary>Who this swing is homing in on, or null.</summary>
	public Enemy HomingTarget { get; private set; }

	Rigidbody ownBody;
	LockOn lockOn;
	Vector3 lungeVelocity;
	TimeSince timeSinceLunge;

	TimeSince timeSinceAttackStarted;
	TimeSince timeSinceResolved;
	bool resolved;

	// whether this swing lands on the hands' hit event rather than its windup timer
	bool waitsForHitFrame;

	// set by the animation, which runs on the render tick, and spent on the next fixed tick where the
	// rest of the attack lives — the same latch the attack buttons go through
	bool hitFrameReached;

	MeleeAttack? queued;
	TimeSince timeSinceQueued;

	// the attack button, read on the render tick: whether it's down, since when, and whether this hold has
	// already turned into a hook
	bool attackHeld;
	bool heldIntoHook;
	TimeSince timeSinceAttackPressed;

	// read only for ActionsBlocked: the run holds you through movement's freeze, and punching obeys it too
	RBPlayerMovement movement;

	// your hands are either up or swinging, never both
	PlayerBlock block;

	// a slide is your legs out in front of you — there's no punch to throw from it
	Sliding sliding;

	// your own health, for Liquid Courage
	Health ownHealth;

	protected override void OnStart()
	{
		// Resolved rather than trusted to the editor reference: this used to point at a
		// CameraLook that no longer exists, and a null Look doesn't fail loudly — it quietly
		// falls back to swinging from the player's origin, which reads as a movement bug
		// rather than a missing reference.
		Look ??= CameraMovement.Find( GameObject );
		Grab ??= Components.Get<PlayerGrab>( FindMode.Enabled | FindMode.InSelf | FindMode.InDescendants );
		Stamina ??= Components.Get<Stamina>( FindMode.Enabled | FindMode.InSelf | FindMode.InDescendants );
		PlayerHealth ??= Components.Get<PlayerHealth>( FindMode.Enabled | FindMode.InSelf | FindMode.InDescendants );
		Hands ??= Components.Get<HandAnimationHelper>( FindMode.Enabled | FindMode.InSelf | FindMode.InDescendants );
		movement = Components.Get<RBPlayerMovement>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );
		block = Components.Get<PlayerBlock>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );
		sliding = Components.Get<Sliding>( FindMode.EverythingInSelf );
		ownBody = Components.Get<Rigidbody>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );
		lockOn = Components.Get<LockOn>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );
		ownHealth = Components.Get<Health>( FindMode.Enabled | FindMode.InSelf | FindMode.InDescendants )
			?? Components.Get<Health>( FindMode.Enabled | FindMode.InAncestors );

		if ( Hands.IsValid() )
			Hands.OnHitFrame += OnHandsHitFrame;
	}

	protected override void OnDestroy()
	{
		if ( Hands.IsValid() )
			Hands.OnHitFrame -= OnHandsHitFrame;
	}

	protected override void OnUpdate()
	{
		if ( !CanReadAttacks() )
		{
			// a hold that began before you were stopped mustn't come out as a punch the moment you let go
			attackHeld = false;
			return;
		}

		// latched on the render tick, same as the jump in RBPlayerMovement — a click between
		// two fixed updates would otherwise just vanish
		ReadAttackButton();

		if ( Input.Pressed( "Kick" ) )
			Queue( MeleeAttack.Kick );
	}

	bool CanReadAttacks()
	{
		if ( PlayerHealth.IsValid() && PlayerHealth.IsKnockedOut )
			return false;

		// held by the run — the countdown, the bar, the end of the night. Movement and every ability
		// already stop for this; without it you could still swing at thin air through the shop
		if ( movement.IsValid() && movement.ActionsBlocked )
			return false;

		// hands busy holding the guard up. Switchable on the block itself, so both can be felt
		if ( block.IsValid() && block.StopsAttacks && block.IsBlocking )
			return false;

		if ( IsSliding )
			return false;

		return !( Grab.IsValid() && Grab.IsHolding );
	}

	/// <summary>
	/// One button, two punches. A tap is a jab, thrown when you let go; a hold becomes a hook the instant it passes
	/// HookHoldTime, so the hook doesn't also wait for the release. The jab can't come out on the press itself —
	/// until the button comes back up there's no telling which punch you meant.
	/// </summary>
	void ReadAttackButton()
	{
		if ( Input.Pressed( "Attack1" ) )
		{
			attackHeld = true;
			heldIntoHook = false;
			timeSinceAttackPressed = 0;
		}

		if ( !attackHeld )
			return;

		if ( !heldIntoHook && timeSinceAttackPressed >= HookHoldTime )
		{
			heldIntoHook = true;
			Queue( MeleeAttack.Hook );
		}

		if ( Input.Released( "Attack1" ) )
		{
			if ( !heldIntoHook )
				Queue( MeleeAttack.Jab );

			attackHeld = false;
		}
	}

	protected override void OnFixedUpdate()
	{
		TickLunge();

		if ( IsAttacking )
		{
			var stats = StatsFor( CurrentAttack );

			// the hit lands on one tick only: the one after the hands say the fist connected, or the end
			// of the windup for an attack with no clip to go by
			if ( !resolved && ReadyToLand( stats ) )
			{
				Resolve( stats );
				resolved = true;
				timeSinceResolved = 0;
			}

			// counted from the hit rather than from the start, so a clip that connects later than the
			// windup said doesn't quietly eat into the recovery
			if ( resolved && timeSinceResolved >= stats.Recovery )
				IsAttacking = false;
		}

		if ( !queued.HasValue )
			return;

		// pressed just before the slide began: dropped, not saved to come out the moment you stand up
		if ( IsSliding )
		{
			queued = null;
			return;
		}

		// a buffered press waits out the current attack, but only for so long — otherwise a
		// click you'd long since given up on fires by itself
		if ( timeSinceQueued > InputBufferTime )
			queued = null;
		else if ( !IsAttacking )
			TryBegin( queued.Value );
	}

	bool IsSliding => sliding.IsValid() && sliding.sliding;

	void Queue( MeleeAttack attack )
	{
		queued = attack;
		timeSinceQueued = 0;
	}

	/// <summary>Buys the swing if you can afford it. Nothing happens at all if you can't.</summary>
	void TryBegin( MeleeAttack attack )
	{
		var stats = StatsFor( attack );

		if ( Stamina.IsValid() && !Stamina.TrySpend( stats.StaminaCost ) )
		{
			// thrown away rather than left in the buffer: a punch that finally comes out a second
			// later, the instant your wind returns, is a punch you stopped asking for
			queued = null;
			PlaySound( WindedSound, WorldPosition );

			if ( LogAttacks )
				Log.Info( $"[Combat] too winded to throw the {stats.Name.ToLower()}" );

			return;
		}

		Begin( attack );
	}

	void Begin( MeleeAttack attack )
	{
		queued = null;
		CurrentAttack = attack;
		IsAttacking = true;
		resolved = false;
		hitFrameReached = false;
		timeSinceAttackStarted = 0;

		// the hands are only ever told to swing from here, after the punch is paid for — so a punch
		// you're too winded to throw can't play its clip and fire a hit event with nothing behind it
		waitsForHitFrame = Hands.IsValid() && Hands.TryPlay( attack );

		HomingTarget = Homing ? FindHomingTarget() : null;
		StartLunge( attack );

		// the counter is thrown: a parry's slow motion was only ever there to give you time to throw it
		Scene.GetSystem<HitStop>().EndSlowMo();

		PlaySound( attack switch
		{
			MeleeAttack.Hook => HookSwingSound,
			MeleeAttack.Kick => KickSwingSound,
			_ => JabSwingSound,
		}, EyePosition );

		if ( LogAttacks )
		{
			string timing = waitsForHitFrame ? "waiting on its hit frame" : "on its windup timer";
			Log.Info( $"[Combat] {StatsFor( attack ).Name} thrown, {timing}" );
		}
	}

	/// <summary>
	/// Whether the swing connects this tick. An animated attack waits for its clip's hit event, so the
	/// damage lands on the frame the fist visibly arrives; anything without a clip keeps its timer.
	/// </summary>
	bool ReadyToLand( AttackStats stats )
	{
		if ( !waitsForHitFrame )
			return timeSinceAttackStarted >= stats.Windup;

		if ( hitFrameReached )
			return true;

		if ( timeSinceAttackStarted < stats.Windup + HitEventGrace )
			return false;

		// landed late rather than lost: a punch that silently never connects reads as the enemy being
		// broken, where this one says exactly what to go and fix
		Log.Warning( $"[Combat] {stats.Name} never got a '{Hands.HitEvent}' event from the hands — landed on the timer instead. Check the Type Name on the clip's generic event, and that the graph actually plays the clip." );
		return true;
	}

	void OnHandsHitFrame()
	{
		// only counts for a swing still waiting on one. Anything else — a clip playing on after its hit
		// already landed on the timer, say — is the animation's business, not a second punch
		if ( IsAttacking && waitsForHitFrame && !resolved )
			hitFrameReached = true;
	}

	void Resolve( AttackStats stats )
	{
		var origin = EyePosition;
		var forward = AttackForward( origin );

		// swept as a sphere rather than a ray so a fist has some width to it — a ray means
		// clipping the edge of a target counts as a clean miss, which feels broken to play
		var trace = Scene.Trace
			.Sphere( stats.Radius, origin, origin + forward * stats.Range )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

		DrawSwing( stats, origin, forward, trace );

		if ( !trace.Hit || !trace.GameObject.IsValid() )
		{
			PlaySound( MissSound, origin + forward * stats.Range );

			if ( LogAttacks )
				Log.Info( $"[Combat] {stats.Name} missed" );

			return;
		}

		var hit = trace.GameObject;
		var health = hit.Components.Get<Health>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );
		var stagger = hit.Components.Get<Stagger>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );
		var body = hit.Components.Get<Rigidbody>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );

		// pushed flat away from you, whatever the camera's pitch: looking down at a man shouldn't drive the
		// blow into the floor and leave him standing there
		var flat = forward.WithZ( 0 );
		var direction = ( ( flat.IsNearlyZero() ? WorldRotation.Forward : flat.Normal ) + Vector3.Up * stats.Lift ).Normal;

		// knockback before damage, so a killing blow still launches the thing it kills
		float impulse = 0f;

		if ( body.IsValid() )
		{
			// armed before it's launched, the same as a throw, so a man kicked point-blank into the one
			// standing behind him still counts as a hit
			if ( stats.Knockback >= LaunchKnockback )
			{
				var launched = body.GameObject.Components.GetOrCreate<ThrownObject>();
				launched.Arm( GameObject, LaunchArmedTime );
				launched.KnocksDown = BowlingBall && CurrentAttack == MeleeAttack.Kick;
			}

			float mass = ScaleKnockbackWithMass ? ( body.PhysicsBody?.Mass ?? 1f ) : 1f;
			impulse = stats.Knockback * mass;

			// applied at the contact point rather than the centre of mass, so a hit off to one
			// side spins the thing round as it goes instead of sliding it away square-on
			body.ApplyImpulseAt( trace.HitPosition, direction * impulse );
		}

		DrawImpact( trace.HitPosition, direction, impulse );

		// the feel of it landing: a beat of freeze and a jolt of the view, sized to the attack
		Scene.GetSystem<HitStop>().Freeze( HitStopFor( CurrentAttack ), HitStopScale );
		Look?.Shake( ShakeFor( CurrentAttack ) );

		PlaySound( CurrentAttack switch
		{
			MeleeAttack.Hook => HookHitSound,
			MeleeAttack.Kick => KickHitSound,
			_ => JabHitSound,
		}, trace.HitPosition );

		// Counter Punch: he's still reeling from your parry, so this one puts him down outright
		var enemy = hit.Components.Get<Enemy>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );
		bool counter = CounterPunch && stagger.IsValid() && enemy.IsValid() && enemy.State == EnemyState.Parried;

		float damage = stats.Damage * CourageFactor;

		// stagger before damage, so a blow that both staggers and kills still reports the stagger
		if ( counter )
			stagger.ForceStagger( GameObject );
		else
			stagger?.Add( stats.Stagger, GameObject );

		health?.TakeDamage( damage, GameObject, trace.HitPosition, direction, KindFor( CurrentAttack ) );

		if ( LogAttacks )
		{
			string damaged = health.IsValid() ? $"{damage:0.#} damage{( CourageFactor > 1.001f ? $" (courage x{CourageFactor:0.00})" : "" )}" : "no Health";
			string rocked = counter ? "COUNTER — staggered" : stagger.IsValid() ? $"+{stats.Stagger:0.#} stagger ({stagger.Fraction * 100f:0}%)" : "no Stagger";
			string pushed = body.IsValid() ? $"impulse {impulse:0}" : "no Rigidbody";
			Log.Info( $"[Combat] {stats.Name} hit {hit.Name} — {damaged}, {rocked}, {pushed}" );
		}
	}

	/// <summary>
	/// Liquid Courage's multiplier on melee damage: nothing at full health, rising as health drops, and the whole
	/// CourageBonus from LowHealthFraction down.
	/// </summary>
	float CourageFactor
	{
		get
		{
			if ( CourageBonus <= 0f || !ownHealth.IsValid() )
				return 1f;

			float missing = 1f - ownHealth.Fraction;
			float ramp = ( missing / MathF.Max( 1f - LowHealthFraction, 0.01f ) ).Clamp( 0f, 1f );

			return 1f + CourageBonus * 0.01f * ramp;
		}
	}

	/// <summary>
	/// The enemy this swing homes in on: your lock-on target if you have one and he's in reach, otherwise
	/// whoever is closest to where you're looking inside HomingRange and HomingAngle. Dead or out of sight
	/// doesn't count.
	/// </summary>
	Enemy FindHomingTarget()
	{
		var eye = EyePosition;
		var look = EyeForward.WithZ( 0 ).Normal;

		if ( lockOn.IsValid() && lockOn.IsLocked && IsHomeable( lockOn.Target, HomingRange * 1.5f, 180f, eye, look ) )
			return lockOn.Target;

		Enemy best = null;
		float bestScore = float.MaxValue;

		foreach ( var enemy in Scene.GetAllComponents<Enemy>() )
		{
			if ( !IsHomeable( enemy, HomingRange, HomingAngle, eye, look ) )
				continue;

			var to = ( enemy.WorldPosition - WorldPosition ).WithZ( 0 );
			float angle = Vector3.GetAngle( look, to.Normal );

			// how far off your aim counts for as much as how far away: the man you're looking at wins
			// over a closer one at the edge of the screen
			float score = angle / MathF.Max( HomingAngle, 1f ) + to.Length / MathF.Max( HomingRange, 1f );

			if ( score < bestScore )
			{
				bestScore = score;
				best = enemy;
			}
		}

		return best;
	}

	bool IsHomeable( Enemy enemy, float range, float maxAngle, Vector3 eye, Vector3 look )
	{
		if ( !enemy.IsValid() || enemy.State == EnemyState.Dead )
			return false;

		var to = ( enemy.WorldPosition - WorldPosition ).WithZ( 0 );

		if ( to.Length > range || Vector3.GetAngle( look, to.Normal ) > maxAngle )
			return false;

		// no homing through walls: a punch that curves round a pillar onto someone you can't see is a bug
		var trace = Scene.Trace.Ray( eye, AimPointOf( enemy ) )
			.IgnoreGameObjectHierarchy( GameObject.Root )
			.IgnoreGameObjectHierarchy( enemy.GameObject )
			.Run();

		return !trace.Hit;
	}

	Vector3 AimPointOf( Enemy enemy ) => enemy.WorldPosition + Vector3.Up * HomingAimHeight;

	/// <summary>Down the camera, or straight at the homing target when there is one — that's what makes it land.</summary>
	Vector3 AttackForward( Vector3 origin )
	{
		if ( !HomingTarget.IsValid() || HomingTarget.State == EnemyState.Dead )
			return EyeForward;

		var to = AimPointOf( HomingTarget ) - origin;
		return to.IsNearlyZero() ? EyeForward : to.Normal;
	}

	/// <summary>
	/// Carries you toward the homing target as the swing starts, far enough to close to LungeStopDistance and no
	/// further than this attack's lunge. No target, or already close enough, and you stay where you are.
	/// </summary>
	void StartLunge( MeleeAttack attack )
	{
		IsLunging = false;

		if ( !HomingTarget.IsValid() || !ownBody.IsValid() )
			return;

		var to = ( HomingTarget.WorldPosition - WorldPosition ).WithZ( 0 );
		float distance = ( to.Length - LungeStopDistance ).Clamp( 0f, LungeFor( attack ) );

		if ( distance < 1f )
			return;

		lungeVelocity = to.Normal * distance / MathF.Max( LungeTime, 0.01f );
		timeSinceLunge = 0;
		IsLunging = true;
	}

	/// <summary>Drops the lunge on the spot, speed and all left as it is — something else (a kick landing on you) has just taken over.</summary>
	public void CancelLunge() => IsLunging = false;

	void TickLunge()
	{
		if ( !IsLunging )
			return;

		bool arrived = HomingTarget.IsValid()
			&& ( HomingTarget.WorldPosition - WorldPosition ).WithZ( 0 ).Length <= LungeStopDistance;

		bool stopped = PlayerHealth.IsValid() && PlayerHealth.IsKnockedOut;

		if ( timeSinceLunge >= LungeTime || arrived || stopped || !ownBody.IsValid() )
		{
			IsLunging = false;

			// most of the speed shed on arrival, so you plant in front of him instead of skating on past
			if ( ownBody.IsValid() )
				ownBody.Velocity = ( ownBody.Velocity.WithZ( 0 ) * 0.2f ).WithZ( ownBody.Velocity.z );

			return;
		}

		ownBody.Velocity = lungeVelocity.WithZ( ownBody.Velocity.z );
	}

	static HitKind KindFor( MeleeAttack attack ) => attack switch
	{
		MeleeAttack.Kick => HitKind.Launch,
		MeleeAttack.Hook => HitKind.Heavy,
		_ => HitKind.Light,
	};

	float LungeFor( MeleeAttack attack ) => attack switch
	{
		MeleeAttack.Hook => HookLunge,
		MeleeAttack.Kick => KickLunge,
		_ => JabLunge,
	};

	float HitStopFor( MeleeAttack attack ) => attack switch
	{
		MeleeAttack.Hook => HookHitStop,
		MeleeAttack.Kick => KickHitStop,
		_ => JabHitStop,
	};

	float ShakeFor( MeleeAttack attack ) => attack switch
	{
		MeleeAttack.Hook => HookShake,
		MeleeAttack.Kick => KickShake,
		_ => JabShake,
	};

	// an empty slot is just silence, so sounds can be filled in one at a time while trying them out
	static void PlaySound( SoundEvent sound, Vector3 position )
	{
		if ( sound is not null )
			Sfx.Play( sound, position );
	}

	Vector3 EyePosition => Look.IsValid()
		? Look.WorldPosition
		: WorldPosition + Vector3.Up * FallbackEyeHeight;

	Vector3 EyeForward => Look.IsValid()
		? Look.EyeAngles.ToRotation().Forward
		: WorldRotation.Forward;

	/// <summary>
	/// Draws the swing exactly as the hit test saw it: same origin, same radius, same length, and
	/// stopping where the trace stopped rather than at full reach. A debug shape that doesn't match
	/// the volume that was actually queried is worse than none — it sends you tuning the wrong number.
	/// </summary>
	void DrawSwing( AttackStats stats, Vector3 origin, Vector3 forward, SceneTraceResult trace )
	{
		if ( !DebugDraw )
			return;

		var end = trace.Hit ? trace.EndPosition : origin + forward * stats.Range;
		var color = trace.Hit ? Color.Red : Color.White.WithAlpha( 0.4f );

		DebugOverlay.Capsule( new Capsule( origin, end, stats.Radius ), color, DebugDuration );

		string label = trace.Hit && trace.GameObject.IsValid()
			? $"{stats.Name} → {trace.GameObject.Name}"
			: $"{stats.Name} — miss";

		DebugOverlay.Text( end + Vector3.Up * ( stats.Radius + 4f ), label, 14f, TextFlag.Center, color, DebugDuration, true );
	}

	/// <summary>Marks the contact point the impulse is applied at, and which way it threw things.</summary>
	void DrawImpact( Vector3 hitPosition, Vector3 direction, float impulse )
	{
		if ( !DebugDraw )
			return;

		DebugOverlay.Sphere( new Sphere( hitPosition, 2f ), Color.Yellow, DebugDuration );

		if ( impulse <= 0f )
			return;

		// scaled right down — a 7500 impulse drawn at true length is a line across the map. Read it
		// as a relative measure and as the direction, including how much of it is lift
		float length = MathF.Min( 96f, impulse * 0.05f );
		DebugOverlay.Line( hitPosition, hitPosition + direction * length, Color.Yellow, DebugDuration );
	}

	/// <summary>
	/// Scene-view outline of one attack's reach. Drawn flat down the body's forward rather than the
	/// camera's, because what you're sizing up here is how far the attack gets you, not where you
	/// happen to be looking — and in the editor there's no camera aim to speak of anyway.
	/// </summary>
	protected override void DrawGizmos()
	{
		if ( !DebugDraw || !Gizmo.IsSelected )
			return;

		var stats = StatsFor( GizmoPreview );

		// measured between world heights so it stays right however deep the camera sits in the hierarchy
		float eyeHeight = Look.IsValid() ? Look.WorldPosition.z - WorldPosition.z : FallbackEyeHeight;

		var origin = Vector3.Up * eyeHeight;
		var end = origin + Vector3.Forward * stats.Range;

		Gizmo.Draw.Color = Color.Cyan.WithAlpha( 0.6f );
		Gizmo.Draw.LineCylinder( origin, end, stats.Radius, stats.Radius, 12 );
		Gizmo.Draw.LineSphere( end, stats.Radius, 8 );
	}

	readonly record struct AttackStats( string Name, float Damage, float Range, float Radius, float Windup, float Recovery, float Knockback, float Lift, float Stagger, float StaminaCost );

	AttackStats StatsFor( MeleeAttack attack ) => attack switch
	{
		MeleeAttack.Hook => new AttackStats( "Hook", HookDamage, HookRange, HookRadius, HookWindup, HookRecovery, HookKnockback, HookLift, HookStagger, HookStaminaCost ),
		MeleeAttack.Kick => new AttackStats( "Kick", KickDamage, KickRange, KickRadius, KickWindup, KickRecovery, KickKnockback, KickLift, KickStagger, KickStaminaCost ),
		_ => new AttackStats( "Jab", JabDamage, JabRange, JabRadius, JabWindup, JabRecovery, JabKnockback, JabLift, JabStagger, JabStaminaCost ),
	};
}
