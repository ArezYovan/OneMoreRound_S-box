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

	[Property, Group( "References" ), Description( "Only used when Look is unset — how far up from this object's origin to swing from." )]
	public float FallbackEyeHeight { get; set; } = 64f;

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

	[Property, Group( "Feel" ), Description( "How long past its windup an animated attack keeps waiting for the hit event before landing anyway. A safety net, not a timing — if its warning shows up, the event is missing, misnamed, or placed later than this." )]
	public float HitEventGrace { get; set; } = 0.3f;

	[Property, Group( "Feel" ), Description( "A hit with at least this much knockback launches what it hits as a weapon: whoever it crashes into takes damage and stagger, exactly as if you'd thrown it. The kick clears this easily, the hook only just does, the jab never will — so it's also the line a knockback drink pushes an attack over." )]
	public float LaunchKnockback { get; set; } = 250f;

	[Property, Group( "Feel" ), Description( "How long something you launch stays dangerous. Short on purpose: a man still sliding a second after a kick is a nudge, not a hit — and an enemy is disarmed anyway the moment he's back on his feet." )]
	public float LaunchArmedTime { get; set; } = 1f;

	[Property, Group( "References" ), Description( "Optional, found automatically. Without one, attacks are free and never tire you out." )]
	public Stamina Stamina { get; set; }

	[Property, Group( "References" ), Description( "Optional, found automatically. A man on the floor doesn't get to keep punching." )]
	public PlayerHealth PlayerHealth { get; set; }

	[Property, Group( "Debug" )] public bool LogAttacks { get; set; } = true;

	[Property, Group( "Debug" ), Description( "Draw each swing in-game as the swept sphere the hit test actually used — not an approximation of it. White where it travelled through nothing, red where it connected." )]
	public bool DebugDraw { get; set; } = true;

	[Property, Group( "Debug" ), Description( "Seconds a swing's debug shapes linger before fading. Long enough to read after a fast jab, short enough that mashing doesn't bury the view." )]
	public float DebugDuration { get; set; } = 1.5f;

	[Property, Group( "Debug" ), Description( "Which attack's reach to outline in the scene view while this object is selected." )]
	public MeleeAttack GizmoPreview { get; set; } = MeleeAttack.Kick;

	public bool IsAttacking { get; private set; }
	public MeleeAttack CurrentAttack { get; private set; }

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

	// read only for ActionsBlocked: the run holds you through movement's freeze, and punching obeys it too
	RBPlayerMovement movement;

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
		if ( PlayerHealth.IsValid() && PlayerHealth.IsKnockedOut )
			return;

		// held by the run — the countdown, the bar, the end of the night. Movement and every ability
		// already stop for this; without it you could still swing at thin air through the shop
		if ( movement.IsValid() && movement.ActionsBlocked )
			return;

		if ( Grab.IsValid() && Grab.IsHolding )
			return;

		// latched on the render tick, same as the jump in RBPlayerMovement — a click between
		// two fixed updates would otherwise just vanish
		if ( Input.Pressed( "Attack1" ) )
			Queue( MeleeAttack.Jab );
		else if ( Input.Pressed( "Attack2" ) )
			Queue( MeleeAttack.Hook );
		else if ( Input.Pressed( "Kick" ) )
			Queue( MeleeAttack.Kick );
	}

	protected override void OnFixedUpdate()
	{
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

		// a buffered press waits out the current attack, but only for so long — otherwise a
		// click you'd long since given up on fires by itself
		if ( timeSinceQueued > InputBufferTime )
			queued = null;
		else if ( !IsAttacking )
			TryBegin( queued.Value );
	}

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
		var forward = EyeForward;

		// swept as a sphere rather than a ray so a fist has some width to it — a ray means
		// clipping the edge of a target counts as a clean miss, which feels broken to play
		var trace = Scene.Trace
			.Sphere( stats.Radius, origin, origin + forward * stats.Range )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

		DrawSwing( stats, origin, forward, trace );

		if ( !trace.Hit || !trace.GameObject.IsValid() )
		{
			if ( LogAttacks )
				Log.Info( $"[Combat] {stats.Name} missed" );

			return;
		}

		var hit = trace.GameObject;
		var health = hit.Components.Get<Health>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );
		var stagger = hit.Components.Get<Stagger>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );
		var body = hit.Components.Get<Rigidbody>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );

		var direction = ( forward + Vector3.Up * stats.Lift ).Normal;

		// knockback before damage, so a killing blow still launches the thing it kills
		float impulse = 0f;

		if ( body.IsValid() )
		{
			// armed before it's launched, the same as a throw, so a man kicked point-blank into the one
			// standing behind him still counts as a hit
			if ( stats.Knockback >= LaunchKnockback )
				body.GameObject.Components.GetOrCreate<ThrownObject>().Arm( GameObject, LaunchArmedTime );

			float mass = ScaleKnockbackWithMass ? ( body.PhysicsBody?.Mass ?? 1f ) : 1f;
			impulse = stats.Knockback * mass;

			// applied at the contact point rather than the centre of mass, so a hit off to one
			// side spins the thing round as it goes instead of sliding it away square-on
			body.ApplyImpulseAt( trace.HitPosition, direction * impulse );
		}

		DrawImpact( trace.HitPosition, direction, impulse );

		// stagger before damage, so a blow that both staggers and kills still reports the stagger
		stagger?.Add( stats.Stagger, GameObject );
		health?.TakeDamage( stats.Damage, GameObject, trace.HitPosition, direction );

		if ( LogAttacks )
		{
			string damaged = health.IsValid() ? $"{stats.Damage:0.#} damage" : "no Health";
			string rocked = stagger.IsValid() ? $"+{stats.Stagger:0.#} stagger ({stagger.Fraction * 100f:0}%)" : "no Stagger";
			string pushed = body.IsValid() ? $"impulse {impulse:0}" : "no Rigidbody";
			Log.Info( $"[Combat] {stats.Name} hit {hit.Name} — {damaged}, {rocked}, {pushed}" );
		}
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
