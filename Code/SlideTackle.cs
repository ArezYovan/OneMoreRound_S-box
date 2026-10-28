using System;
using System.Collections.Generic;

/// <summary>
/// The slide tackle: slide into a man and he goes flying — the kick's knockback, the kick's turn to face
/// you, and a big chunk of stagger. It's a drink, not a default: nothing happens until something on the
/// tab unlocks it (see <see cref="PlayerStat.SlideTackle"/>).
///
/// The zone it hits with is a box you place in the editor — the Slide_Hitbox child, with a BoxCollider only
/// there to be read — laid out in front of the player as if facing +X. The player's root never turns, so the
/// box is swung round to face the way you're sliding every tick rather than taken where it sits. It never
/// touches movement: the slide carries on at full speed through whoever it hits.
/// </summary>
public sealed class SlideTackle : Component
{
	[Property, Group( "References" ), Description( "Optional, found automatically on this object." )]
	public Sliding Sliding { get; set; }

	[Property, Group( "References" ), Description( "The box the tackle hits with. Found automatically from a child named Slide_Hitbox. Its BoxCollider is only read for its size — keep it a trigger, or switch it off." )]
	public BoxCollider Hitbox { get; set; }

	[Property, Group( "Unlock" ), Description( "Whether you have it. Set by PlayerUpgrades from the drinks you own; tick it here to try it out without buying it." )]
	public bool Unlocked { get; set; }

	[Property, Group( "Unlock" ), Description( "Bowling Ball: a man you tackle knocks whoever he crashes into straight into a stagger. Set by PlayerUpgrades, alongside the kick's." )]
	public bool BowlingBall { get; set; }

	[Property, Group( "Tackle" ), Description( "Share of his stagger meter one tackle fills. 0.6 is most of the way; a drink with a Slide Tackle per cent raises it, and 1 or more is a guaranteed stagger." )]
	public float StaggerFraction { get; set; } = 0.6f;

	[Property, Group( "Tackle" ), Description( "Damage dealt. Keep it above zero: a hit that does no damage doesn't count as a hit, and he'd skip the knockback and the turn to face you." )]
	public float Damage { get; set; } = 5f;

	[Property, Group( "Tackle" ), Description( "How hard he's sent flying, the same units as the kick's knockback." )]
	public float Knockback { get; set; } = 550f;

	[Property, Group( "Tackle" ), Description( "How much of the push is upward. A little lift turns a shove into a launch." )]
	public float Lift { get; set; } = 0.3f;

	[Property, Group( "Tackle" ), Description( "How long a man you tackle stays dangerous to whoever he crashes into — the same as a kicked man. Zero, and he's just pushed." )]
	public float LaunchArmedTime { get; set; } = 1f;

	[Property, Group( "Tackle" ), Description( "Height of the men it's tested against, from their feet. The box has to overlap this to hit." )]
	public float EnemyHeight { get; set; } = 72f;

	[Property, Group( "Hit Feel" )] public float HitStop { get; set; } = 0.06f;
	[Property, Group( "Hit Feel" )] public float HitStopScale { get; set; } = 0.05f;
	[Property, Group( "Hit Feel" ), Description( "Camera shake per tackle, 0–1." )]
	public float Shake { get; set; } = 0.4f;

	[Property, Group( "Debug" ), Description( "Draw the tackle box in-game while you slide, where it actually is — turned to your slide." )]
	public bool DebugDraw { get; set; }

	[Property, Group( "Debug" )] public bool LogHits { get; set; } = true;

	// one hit per man per slide: without it he'd be hit again every tick he's still inside the box
	readonly HashSet<Enemy> hitThisSlide = new();
	bool wasSliding;

	Rigidbody body;
	CameraMovement look;
	RBPlayerMovement movement;

	protected override void OnStart()
	{
		Sliding ??= Components.Get<Sliding>( FindMode.EverythingInSelf );
		body = Components.Get<Rigidbody>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );
		movement = Components.Get<RBPlayerMovement>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );
		look = CameraMovement.Find( GameObject );

		if ( !Hitbox.IsValid() )
			Hitbox = GameObject.Children.Find( c => c.Name == "Slide_Hitbox" )?.Components.Get<BoxCollider>( FindMode.EverythingInSelf );

		if ( !Hitbox.IsValid() )
			Log.Warning( $"{GameObject.Name}: SlideTackle has no hitbox — add a Slide_Hitbox child with a BoxCollider, or assign one." );
	}

	protected override void OnFixedUpdate()
	{
		bool sliding = Sliding.IsValid() && Sliding.sliding;

		// a fresh slide can hit everyone again
		if ( sliding && !wasSliding )
			hitThisSlide.Clear();

		wasSliding = sliding;

		if ( !sliding || !Unlocked || !Hitbox.IsValid() )
			return;

		var facing = SlideFacing();
		GetBox( facing, out var center, out var half );

		if ( DebugDraw )
			DebugOverlay.Box( BBox.FromPositionAndSize( Vector3.Zero, half * 2f ), Color.Orange, 0f, new Transform( center, facing ) );

		bool landed = false;

		foreach ( var enemy in Scene.GetAllComponents<Enemy>() )
		{
			if ( !enemy.IsValid() || enemy.State == EnemyState.Dead || hitThisSlide.Contains( enemy ) )
				continue;

			if ( !Overlaps( enemy, center, facing, half ) )
				continue;

			hitThisSlide.Add( enemy );
			Tackle( enemy, facing.Forward );
			landed = true;
		}

		// once per tick however many he bowls over, so a pile-up is one big jolt rather than a stutter
		if ( landed )
		{
			Scene.GetSystem<HitStop>().Freeze( HitStop, HitStopScale );
			look?.Shake( Shake );
		}
	}

	/// <summary>Which way the box faces: the way you're actually sliding, or the way you look if you've all but stopped.</summary>
	Rotation SlideFacing()
	{
		var velocity = body.IsValid() ? body.Velocity.WithZ( 0 ) : Vector3.Zero;

		if ( velocity.Length > 20f )
			return Rotation.LookAt( velocity.Normal, Vector3.Up );

		var forward = movement.IsValid() && movement.Orientation.IsValid() ? movement.Orientation.WorldRotation.Forward : Vector3.Forward;
		return Rotation.LookAt( forward.WithZ( 0 ).Normal, Vector3.Up );
	}

	/// <summary>
	/// The box as placed in the editor, relative to the player root as if he faced +X, turned to face the slide.
	/// Read fresh every tick so dragging it around mid-play shows up straight away.
	/// </summary>
	void GetBox( Rotation facing, out Vector3 center, out Vector3 half )
	{
		var local = Hitbox.GameObject.LocalTransform;

		center = WorldPosition + facing * local.PointToWorld( Hitbox.Center );
		var size = Hitbox.Scale * local.Scale;
		half = new Vector3( MathF.Abs( size.x ), MathF.Abs( size.y ), MathF.Abs( size.z ) ) * 0.5f;
	}

	/// <summary>
	/// Whether the box touches him: his upright column — his width either side of his centre, feet to
	/// EnemyHeight — against the box turned to the slide.
	/// </summary>
	bool Overlaps( Enemy enemy, Vector3 center, Rotation facing, Vector3 half )
	{
		var local = facing.Inverse * ( enemy.WorldPosition - center );
		float radius = enemy.BodyRadius;

		if ( MathF.Abs( local.x ) > half.x + radius || MathF.Abs( local.y ) > half.y + radius )
			return false;

		// his column runs from his feet up; the box from its bottom to its top
		float feet = local.z;
		return feet <= half.z && feet + EnemyHeight >= -half.z;
	}

	void Tackle( Enemy enemy, Vector3 forward )
	{
		var target = enemy.GameObject;
		var direction = ( forward.WithZ( 0 ).Normal + Vector3.Up * Lift ).Normal;

		var enemyBody = target.Components.Get<Rigidbody>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );
		var stagger = target.Components.Get<Stagger>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );
		var health = target.Components.Get<Health>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );

		// knockback before damage, the same order the punches use, so a tackle that kills still launches
		if ( enemyBody.IsValid() )
		{
			if ( LaunchArmedTime > 0f )
			{
				var launched = enemyBody.GameObject.Components.GetOrCreate<ThrownObject>();
				launched.Arm( GameObject, LaunchArmedTime );
				launched.KnocksDown = BowlingBall;
			}

			enemyBody.Velocity = Vector3.Zero;
			enemyBody.ApplyImpulse( direction * Knockback * ( enemyBody.PhysicsBody?.Mass ?? 1f ) );
		}

		if ( stagger.IsValid() )
			stagger.Add( stagger.MaxStagger * StaggerFraction, GameObject );

		// a Launch, like the kick: he turns to face you and plays his knockback rather than a flinch
		health?.TakeDamage( Damage, GameObject, enemy.WorldPosition + Vector3.Up * 30f, direction, HitKind.Launch );

		if ( LogHits )
			Log.Info( $"[Tackle] slid into {target.Name} — +{StaggerFraction * 100f:0}% stagger" );
	}
}
