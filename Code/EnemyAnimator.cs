using System;

/// <summary>
/// Drives the custom enemy rig's animgraph, the way CitizenAnimationHelper drives the citizen's.
/// Nothing tells it anything: it reads the enemy's state and the rigidbody's velocity every frame,
/// so Enemy doesn't need to know which rig it's wearing. Leave CitizenAnimationHelper off a prefab
/// that uses this — Enemy skips every citizen-only call when it can't find one.
///
/// Parameters it writes (names must match the animgraph exactly):
///   move_x      float  forward (+) / back (-), in world units per second
///   move_y      float  right (+) / left (-), in world units per second
/// Place the blendspace clips at their real speeds (what ModelDoc reports for each walk) and the
/// blend lands on the right clip at the right pace.
///   b_stagger   bool   staggered: down, grabbable, being carried
///   b_parried   bool   true while he's reeling from a parry — for the parried clip once there is one
///   b_knockback bool   stunned by a hit and still sliding fast, or kicked — staggered or not — and
///                      still flying. Drops back to false when he lands, so a staggered man returns to
///                      his stagger
///   b_hit       bool   fired once per hit taken while on his feet (set it to auto reset)
///   hit_type    int    0 jab (light), 1 hook or anything heavy — see HitKind. A kick (2) fires no b_hit:
///                      it plays the knockback instead
///   move_blend  float  0 standing still, 1 walking — the weight between the full-body upper
///                      animation and the legs-from-the-blendspace mix
/// </summary>
public sealed class EnemyAnimator : Component
{
	[Property, Group( "References" ), Description( "Optional, found automatically in children. The model whose animgraph this drives." )]
	public SkinnedModelRenderer Renderer { get; set; }

	[Property, Group( "References" ), Description( "Optional, found automatically on this object or above." )]
	public Enemy Enemy { get; set; }

	[Property, Group( "References" ), Description( "Optional, found automatically on this object or above." )]
	public Rigidbody Body { get; set; }

	[Property, Group( "References" ), Description( "Optional, found automatically on this object or above. Every hit it reports plays a hit reaction." )]
	public Health Health { get; set; }

	[Property, Group( "Locomotion" ), Description( "How quickly the blend follows the real velocity. Lower is smoother; too low and he keeps walking a beat after he's stopped." )]
	public float Smoothing { get; set; } = 10f;

	[Property, Group( "Locomotion" ), Description( "Below this speed he counts as standing still and move_blend heads to 0: the whole body plays the fight stance or the punch, pelvis twist and all." )]
	public float StillSpeed { get; set; } = 15f;

	[Property, Group( "Locomotion" ), Description( "At this speed and above move_blend is 1: legs from the walk blendspace, everything from the spine up from the fight stance or punch." )]
	public float MovingSpeed { get; set; } = 60f;

	[Property, Group( "Reactions" ), Description( "While stunned, the knockback loop only plays above this speed. Below it the hit was a jab rather than a launch, and he just stands there taking it." )]
	public float KnockbackSpeed { get; set; } = 250f;

	[Property, Group( "Debug" ), Description( "Shows what's being sent, so you can tell a graph problem from a code problem." )]
	public bool DebugDraw { get; set; }

	Vector2 move;

	// kicked and not yet landed. Tracked apart from the state, because a kicked man who's staggered stays
	// Staggered the whole time he's flying — and a staggered man being carried or thrown moves just as fast
	// without anyone having kicked him
	bool launched;
	TimeSince timeSinceLaunched;

	// how long a kick is given to get him moving before a slow reading counts as having landed — the impulse
	// only shows up in his velocity on the next physics step
	const float LaunchGrace = 0.15f;

	protected override void OnStart()
	{
		Renderer ??= Components.Get<SkinnedModelRenderer>( FindMode.Enabled | FindMode.InSelf | FindMode.InDescendants );
		Enemy ??= Components.Get<Enemy>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );
		Body ??= Components.Get<Rigidbody>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );
		Health ??= Components.Get<Health>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );

		if ( Health.IsValid() )
			Health.OnDamaged += OnDamaged;
	}

	protected override void OnDestroy()
	{
		if ( Health.IsValid() )
			Health.OnDamaged -= OnDamaged;
	}

	void OnDamaged( float amount, GameObject attacker )
	{
		if ( !Renderer.IsValid() )
			return;

		var state = Enemy.IsValid() ? Enemy.State : EnemyState.Idle;

		if ( state == EnemyState.Dead || Health.IsDead )
			return;

		// a kick sends him flying whether he's on his feet or down — the knockback plays either way
		if ( Health.LastHitKind == HitKind.Launch )
		{
			launched = true;
			timeSinceLaunched = 0;
			return;
		}

		// down, the stagger owns the body — a flinch on top would just fight it
		if ( state == EnemyState.Staggered )
			return;

		Renderer.Set( "hit_type", (int)Health.LastHitKind );
		Renderer.Set( "b_hit", true );
	}

	protected override void OnUpdate()
	{
		if ( !Renderer.IsValid() )
			return;

		var state = Enemy.IsValid() ? Enemy.State : EnemyState.Idle;
		var velocity = Body.IsValid() ? Body.Velocity.WithZ( 0 ) : Vector3.Zero;

		// measured against the way the enemy faces, not the renderer, in case the model is rotated to fit
		var facing = Enemy.IsValid() ? Enemy.WorldRotation : WorldRotation;
		var local = facing.Inverse * velocity;

		// being flung, carried or dead isn't walking — the legs stay out of it
		bool walking = state is not ( EnemyState.Stunned or EnemyState.Staggered or EnemyState.Dead or EnemyState.Parried );

		// s&box's local +y is left; flipped so right is positive, which is how the blendspace reads
		var wanted = walking ? new Vector2( local.x, -local.y ) : Vector2.Zero;
		move = Vector2.Lerp( move, wanted, 1f - MathF.Exp( -Smoothing * Time.Delta ) );

		bool staggered = state == EnemyState.Staggered;
		bool flying = velocity.Length > KnockbackSpeed;

		// landed once he's slowed, but only after the kick has had a moment to get him moving
		if ( launched && !flying && timeSinceLaunched > LaunchGrace )
			launched = false;

		bool held = Enemy.IsValid() && Enemy.Stagger.IsValid() && Enemy.Stagger.IsHeld;
		bool knockedBack = !held && state != EnemyState.Dead && ( launched || ( state == EnemyState.Stunned && flying ) );

		Renderer.Set( "move_x", move.x );
		Renderer.Set( "move_y", move.y );
		Renderer.Set( "b_stagger", staggered );
		Renderer.Set( "b_knockback", knockedBack );
		Renderer.Set( "b_parried", state == EnemyState.Parried );

		// taken from the smoothed move, so it eases in and out with the legs instead of snapping
		float moveBlend = ( ( move.Length - StillSpeed ) / MathF.Max( MovingSpeed - StillSpeed, 1f ) ).Clamp( 0f, 1f );
		Renderer.Set( "move_blend", moveBlend );

		if ( DebugDraw )
			DebugOverlay.Text( WorldPosition + Vector3.Up * 100f, $"{state}\nmove {move.x:0.00}, {move.y:0.00}  blend {moveBlend:0.00}\nstagger {staggered}  knockback {knockedBack}", 14, TextFlag.Center );
	}
}
