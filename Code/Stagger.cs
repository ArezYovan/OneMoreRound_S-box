using System;

/// <summary>
/// The meter that decides whether a man is still fighting or is currently furniture.
///
/// Stagger is a second, parallel health bar, and the interesting one: health decides when
/// someone dies, stagger decides when they become something you can pick up and throw at
/// their friends. Keeping it off Health on purpose — a barstool will never need hit points,
/// and a Blocker will eventually want a stagger threshold without being any easier to kill.
/// </summary>
public sealed class Stagger : Component
{
	[Property, Group( "Meter" )] public float MaxStagger { get; set; } = 100f;

	[Property, Group( "Meter" ), Description( "Shown here so you can watch it fill while testing." )]
	public float Current { get; private set; }

	[Property, Group( "Meter" ), Description( "Grace period after the last hit before the meter starts draining. Without it, anything short of an unbroken flurry never accumulates and staggering becomes impossible — this number is what makes trading a few jabs and backing off a viable way to set someone up." )]
	public float DecayDelay { get; set; } = 1.2f;

	[Property, Group( "Meter" ), Description( "Points drained per second, once draining starts." )]
	public float DecayRate { get; set; } = 35f;

	[Property, Group( "Staggered" ), Description( "How long they stay down and grabbable. The clock stops while someone is holding them." )]
	public float StaggerTime { get; set; } = 3f;

	[Property, Group( "Debug" )] public bool LogStagger { get; set; } = true;

	[Property, Group( "Debug" ), Description( "Float the meter above their head. There's no UI yet and this is the fastest way to tell whether the numbers are anywhere near right." )]
	public bool DebugDraw { get; set; } = true;

	[Property, Group( "Debug" )] public float DebugHeight { get; set; } = 80f;

	[Property, Group( "Events" )] public Action OnStaggered { get; set; }
	[Property, Group( "Events" )] public Action OnRecovered { get; set; }

	public bool IsStaggered { get; private set; }

	public float Fraction => MaxStagger <= 0f ? 0f : ( Current / MaxStagger ).Clamp( 0f, 1f );

	/// <summary>
	/// Set by whoever is carrying them. While held the meter neither drains nor times out, so a
	/// man can't wake up in your hands — you decide when he gets put down.
	/// </summary>
	public bool IsHeld { get; set; }

	TimeSince timeSinceHit;
	TimeSince timeSinceStaggered;

	public void Add( float amount, GameObject source = null )
	{
		// already on the floor: further hits are damage, not stagger. Otherwise a staggered man
		// would be re-staggered by the very hits you use to punish him, and never get back up
		if ( amount <= 0f || IsStaggered )
			return;

		Current = MathF.Min( MaxStagger, Current + amount );
		timeSinceHit = 0;

		if ( Current >= MaxStagger )
			EnterStagger( source );
	}

	protected override void OnUpdate()
	{
		if ( IsStaggered )
		{
			if ( IsHeld )
				timeSinceStaggered = 0;
			else if ( timeSinceStaggered >= StaggerTime )
				Recover();
		}
		else if ( Current > 0f && timeSinceHit > DecayDelay )
		{
			Current = MathF.Max( 0f, Current - DecayRate * Time.Delta );
		}

		DrawDebug();
	}

	void EnterStagger( GameObject source )
	{
		IsStaggered = true;
		Current = MaxStagger;
		timeSinceStaggered = 0;

		if ( LogStagger )
			Log.Info( $"[Stagger] {GameObject.Name} STAGGERED — grabbable for {StaggerTime:0.#}s" );

		OnStaggered?.Invoke();
	}

	void Recover()
	{
		IsStaggered = false;
		Current = 0f;

		if ( LogStagger )
			Log.Info( $"[Stagger] {GameObject.Name} recovered" );

		OnRecovered?.Invoke();
	}

	/// <summary>Put someone straight on the floor, whatever the meter says.</summary>
	public void ForceStagger( GameObject source = null )
	{
		if ( IsStaggered )
			return;

		Current = MaxStagger;
		EnterStagger( source );
	}

	void DrawDebug()
	{
		if ( !DebugDraw )
			return;

		var position = WorldPosition + Vector3.Up * DebugHeight;

		string label = IsStaggered
			? ( IsHeld ? "HELD" : "STAGGERED" )
			: $"{Bar( Fraction )} {Fraction * 100f:0}%";

		var color = IsStaggered ? Color.Yellow : Color.White;

		DebugOverlay.Text( position, label, 13f, TextFlag.Center, color, 0f, true );
	}

	static string Bar( float fraction )
	{
		int filled = (int)MathF.Round( fraction * 10f );
		return string.Concat( "[", new string( '|', filled ), new string( '.', 10 - filled ), "]" );
	}
}
