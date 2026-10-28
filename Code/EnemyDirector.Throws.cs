using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// One throw on its way to the player, from the moment the windup starts until the prop lands, is caught or is gone.
/// The HUD draws its warning from these and nothing else, so what you're warned about is exactly what's coming.
/// </summary>
public sealed class ThrowThreat
{
	public Enemy Thrower { get; init; }
	public Rigidbody Prop { get; init; }

	/// <summary>How long the windup lasts, start to release.</summary>
	public float WindupTime { get; init; }

	public TimeSince SinceWindup { get; init; }

	/// <summary>Out of his hand and in the air.</summary>
	public bool Released { get; private set; }

	public TimeSince SinceRelease { get; private set; }

	/// <summary>How long it was thrown to take to reach you.</summary>
	public float FlightTime { get; private set; }

	/// <summary>0 when the windup starts, 1 at the release.</summary>
	public float Windup => Released ? 1f : ( SinceWindup / MathF.Max( WindupTime, 0.01f ) ).Clamp( 0f, 1f );

	/// <summary>Where to point: the prop while there is one, in his hand or in the air, otherwise the man.</summary>
	public Vector3 Position
	{
		get
		{
			if ( Prop.IsValid() )
				last = Prop.WorldPosition;
			else if ( Thrower.IsValid() && !Released )
				last = Thrower.WorldPosition + Vector3.Up * 60f;

			return last;
		}
	}

	Vector3 last;

	public void Release( float flightTime )
	{
		Released = true;
		SinceRelease = 0;
		FlightTime = flightTime;
	}

}

/// <summary>
/// The throwing half of the director. Throws are handed out the same way swings are, so they stay events you can
/// read rather than a barrage: a few men may be off fetching or carrying something, but only one is ever winding up,
/// and windups start at least <see cref="ThrowSpacing"/> apart.
///
/// Who fetches: a reserve by preference — they're the ones out at range with nothing to do — or, when no reserve
/// has anything near him, a circler who isn't mid-swing. That's what keeps a small room throwing: with only a few men
/// up there are no reserves at all. Either way it's someone with a loose prop near him and not near the player, so
/// nobody walks into the fight to pick up a bottle. The prop is claimed while he's on his way, so two men never go
/// for the same one.
/// </summary>
public sealed partial class EnemyDirector
{
	/// <summary>Most men in the throw pipeline at once — fetching, carrying or winding up.</summary>
	public int MaxThrowers { get; set; } = 2;

	/// <summary>Least time from one windup starting to the next. Only one man is ever winding up at a time.</summary>
	public float ThrowSpacing { get; set; } = 5f;

	/// <summary>Least time between sending one man off for a prop and the next, so they don't all peel off at once.</summary>
	public float ThrowJobSpacing { get; set; } = 1.5f;

	/// <summary>
	/// How much farther a circler's fetch counts than a reserve's when choosing who goes, in units. A reserve is
	/// idle and a circler is part of the fight, so a circler only goes when no reserve is anywhere near a prop.
	/// </summary>
	public float CirclerThrowPenalty { get; set; } = 1000f;

	/// <summary>Says in the console why nobody is being sent for a prop, once each time the reason changes.</summary>
	public bool LogThrows { get; set; } = true;

	/// <summary>Props with this tag are never picked up by enemies — set dressing, the bar's own bottles.</summary>
	public const string NoThrowTag = "nothrow";

	readonly List<Enemy> throwers = new();
	readonly Dictionary<Rigidbody, Enemy> claims = new();
	readonly List<ThrowThreat> threats = new();

	Enemy windingUp;
	float lastWindupAt = float.MinValue;
	float lastJobAt = float.MinValue;

	Enemy nextThrower;
	Rigidbody nextThrowerProp;
	float nextThrowerAt = -1f;

	// props a man gave up on reaching, and when they may be tried again
	readonly Dictionary<Rigidbody, float> unreachable = new();

	// the last reason logged for nobody throwing, so it's said once rather than every search
	string lastNoThrowReason;

	// how often the room is searched for the next fetch. Every man in reserve asks every tick; the answer only
	// needs to be fresh to within a few frames
	const float ThrowerSearchInterval = 0.25f;
	const float UnreachableTime = 15f;

	/// <summary>
	/// Ask to go and fetch something to throw. True hands back the prop, already claimed for him. Only the best
	/// placed reserve this tick gets one, so the order men happen to update in never decides it.
	/// </summary>
	public bool RequestThrowJob( Enemy enemy, out Rigidbody prop )
	{
		prop = null;
		PruneThrows();

		if ( throwers.Contains( enemy ) )
			return false;

		// a practice thrower isn't held to the pacing — he's there to be thrown at, over and over
		if ( !enemy.PracticeThrower && ( throwers.Count >= MaxThrowers || Time.Now - lastJobAt < ThrowJobSpacing ) )
			return false;

		NextThrower();

		// the pick can be a few frames old, so check the prop is still free before handing it over
		if ( nextThrower != enemy || !nextThrowerProp.IsValid() || !nextThrowerProp.MotionEnabled || claims.ContainsKey( nextThrowerProp ) )
			return false;

		prop = nextThrowerProp;
		throwers.Add( enemy );
		claims[prop] = enemy;
		lastJobAt = Time.Now;

		// someone else asking this same tick must not be handed the prop just claimed
		nextThrowerAt = -1f;
		return true;
	}

	/// <summary>Ask to start winding up. Only one man at a time, and never sooner than ThrowSpacing after the last.</summary>
	public bool RequestThrowWindup( Enemy enemy )
	{
		if ( windingUp == enemy )
			return true;

		if ( windingUp.IsValid() || ( !enemy.PracticeThrower && Time.Now - lastWindupAt < ThrowSpacing ) )
			return false;

		windingUp = enemy;
		lastWindupAt = Time.Now;
		return true;
	}

	public void EndThrowWindup( Enemy enemy )
	{
		if ( windingUp == enemy )
			windingUp = null;
	}

	/// <summary>
	/// Out of the throw pipeline, however he left it: thrown, knocked down, or given up. Whatever he'd claimed is
	/// free again and a windup that never became a throw stops being warned about.
	/// </summary>
	public void ReleaseThrowJob( Enemy enemy )
	{
		throwers.Remove( enemy );
		EndThrowWindup( enemy );

		foreach ( var prop in claims.Where( c => c.Value == enemy ).Select( c => c.Key ).ToList() )
			claims.Remove( prop );

		threats.RemoveAll( t => t.Thrower == enemy && !t.Released );
	}

	public void Unclaim( Rigidbody prop )
	{
		if ( prop is not null )
			claims.Remove( prop );
	}

	/// <summary>Nobody goes for this one again for a while — someone tried and couldn't get to it.</summary>
	public void MarkUnreachable( Rigidbody prop )
	{
		if ( prop is not null )
			unreachable[prop] = Time.Now + UnreachableTime;
	}

	/// <summary>A windup has started: warn about it.</summary>
	public ThrowThreat AddThreat( Enemy thrower, Rigidbody prop, float windupTime )
	{
		var threat = new ThrowThreat { Thrower = thrower, Prop = prop, WindupTime = windupTime, SinceWindup = 0 };
		threats.Add( threat );
		return threat;
	}

	/// <summary>
	/// Every throw worth warning about right now. A thrown prop stops counting once it's caught, gone, or has come to
	/// rest or slowed right down — it either hit something or it's no longer a danger.
	/// </summary>
	public IReadOnlyList<ThrowThreat> CurrentThreats()
	{
		threats.RemoveAll( t => !t.Released ? !t.Thrower.IsValid() : ThrowIsOver( t ) );
		return threats;
	}

	/// <summary>Whether the player has this in his hands — caught out of the air, or snatched off the floor.</summary>
	public bool IsHeldByPlayer( Rigidbody prop )
	{
		foreach ( var grab in Scene.GetAllComponents<PlayerGrab>() )
		{
			if ( grab.Held == prop )
				return true;
		}

		return false;
	}

	bool ThrowIsOver( ThrowThreat threat )
	{
		var prop = threat.Prop;

		if ( !prop.IsValid() || IsHeldByPlayer( prop ) )
			return true;

		// a moment's grace off the hand, then anything slow has either hit something or dropped out of the air
		if ( threat.SinceRelease > 0.15f && prop.Velocity.Length < 200f )
			return true;

		return threat.SinceRelease > threat.FlightTime + 1.5f;
	}

	/// <summary>The reserve and prop that make the best next fetch. Worked out once per tick and shared by everyone who asks.</summary>
	void NextThrower()
	{
		if ( nextThrowerAt >= 0f && Time.Now - nextThrowerAt < ThrowerSearchInterval )
			return;

		nextThrowerAt = Time.Now;
		nextThrower = null;
		nextThrowerProp = null;

		var candidates = enemies.Where( e => e.IsValid() && e.WantsThrowJob ).ToList();

		if ( candidates.Count == 0 )
		{
			ExplainNoThrow( "nobody is free to throw — everyone is swinging, down, busy or on ThrowCooldown" );
			return;
		}

		var grabs = Scene.GetAllComponents<PlayerGrab>().ToList();
		var props = Scene.GetAllComponents<Rigidbody>().Where( b => IsLooseProp( b, grabs ) ).ToList();

		if ( props.Count == 0 )
		{
			ExplainNoThrow( "there are no loose props — nothing with a Rigidbody at rest that isn't a person, held, claimed or tagged 'nothrow'" );
			return;
		}

		float best = float.MaxValue;

		foreach ( var enemy in candidates )
		{
			var player = enemy.Target.WorldPosition;

			// reserves first: a circler only wins when no reserve is anywhere near something to throw
			float penalty = enemy.State == EnemyState.Circling ? CirclerThrowPenalty : 0f;

			foreach ( var prop in props )
			{
				if ( ( prop.PhysicsBody?.Mass ?? 0f ) > enemy.MaxPropMass )
					continue;

				float fetch = ( prop.WorldPosition - enemy.WorldPosition ).WithZ( 0 ).Length;

				if ( fetch > enemy.PropSearchRadius || fetch + penalty >= best )
					continue;

				if ( ( prop.WorldPosition - player ).WithZ( 0 ).Length < enemy.PropMinPlayerDistance )
					continue;

				best = fetch + penalty;
				nextThrower = enemy;
				nextThrowerProp = prop;
			}
		}

		if ( nextThrower is null )
		{
			ExplainNoThrow( $"{candidates.Count} could throw and {props.Count} loose props exist, but none is within PropSearchRadius " +
				"of a free man and at least PropMinPlayerDistance from the player (or they're all over MaxPropMass)" );
			return;
		}

		lastNoThrowReason = null;
	}

	void ExplainNoThrow( string reason )
	{
		if ( !LogThrows || reason == lastNoThrowReason )
			return;

		lastNoThrowReason = reason;
		Log.Info( $"[Throw] no throw yet: {reason}" );
	}

	/// <summary>
	/// Something lying around that a man could pick up and throw: loose, at rest, nobody's body, not in anyone's
	/// hands, not already on its way somewhere, and not spoken for.
	/// </summary>
	bool IsLooseProp( Rigidbody body, List<PlayerGrab> grabs )
	{
		if ( !body.IsValid() || !body.Enabled || !body.MotionEnabled || claims.ContainsKey( body ) )
			return false;

		if ( unreachable.TryGetValue( body, out float until ) && Time.Now < until )
			return false;

		var go = body.GameObject;

		if ( go.Tags.Has( NoThrowTag ) )
			return false;

		var search = FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors;

		// people aren't props — a downed man is the player's to throw, not his friends'
		if ( go.Components.Get<Health>( search ).IsValid() || go.Components.Get<RBPlayerMovement>( search ).IsValid() )
			return false;

		if ( body.Velocity.Length > 60f )
			return false;

		var thrown = go.Components.Get<ThrownObject>();

		if ( thrown.IsValid() && thrown.IsArmed )
			return false;

		foreach ( var grab in grabs )
		{
			if ( grab.Held == body )
				return false;
		}

		return true;
	}

	void PruneThrows()
	{
		throwers.RemoveAll( e => !e.IsValid() || !e.IsThrowing );

		foreach ( var dead in claims.Where( c => !c.Key.IsValid() || !c.Value.IsValid() ).Select( c => c.Key ).ToList() )
			claims.Remove( dead );

		foreach ( var expired in unreachable.Where( u => !u.Key.IsValid() || Time.Now >= u.Value ).Select( u => u.Key ).ToList() )
			unreachable.Remove( expired );

		if ( !windingUp.IsValid() || windingUp.State != EnemyState.ThrowWindup )
			windingUp = null;
	}
}
