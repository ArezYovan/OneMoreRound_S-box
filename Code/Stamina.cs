using System;

/// <summary>
/// How much fight you've got left in your arms. Every swing costs; standing off for a moment
/// buys it back.
///
/// The part that matters is the lockout. Running dry doesn't just make the next punch
/// unaffordable, it takes attacking away from you until the bar is meaningfully back — because
/// without that you get one punch per sliver of regen, which reads as the game stuttering
/// rather than as you being out of breath.
/// </summary>
public sealed class Stamina : Component
{
	[Property, Group( "Meter" )] public float MaxStamina { get; set; } = 100f;

	[Property, Group( "Meter" ), Description( "Shown here so you can watch it drain while testing." )]
	public float Current { get; private set; }

	[Property, Group( "Regen" ), Description( "Points per second recovered once regen kicks in." )]
	public float RegenRate { get; set; } = 40f;

	[Property, Group( "Regen" ), Description( "Quiet time needed after a swing before any of it comes back. This is the whole 'stop and breathe' beat — at zero you'd regen through a flurry and never feel tired." )]
	public float RegenDelay { get; set; } = 0.5f;

	[Property, Group( "Regen" ), Description( "Once you bottom out you can't swing again until the bar reaches this fraction. Hysteresis on purpose: a hard floor with no recovery threshold turns exhaustion into a stutter instead of a pause." )]
	public float RecoverFraction { get; set; } = 0.35f;

	[Property, Group( "Low" ), Range( 0f, 1f ), Description( "Below this share of the bar you're running low: the screen edges start to darken and the camera to sway, before you've actually run dry. Most moves cost more than the last sliver of the bar, so this — not being winded — is what you'll usually feel." )]
	public float LowFraction { get; set; } = 0.3f;

	[Property, Group( "Debug" )] public bool LogStamina { get; set; } = true;

	[Property, Group( "Debug" ), Description( "Corner readout. There's no UI yet and you can't tune a resource you can't see." )]
	public bool DebugDraw { get; set; } = true;

	[Property, Group( "Debug" )] public Vector2 DebugPosition { get; set; } = new( 32f, 32f );

	[Property, Group( "Events" )] public Action OnExhausted { get; set; }
	[Property, Group( "Events" )] public Action OnRecovered { get; set; }

	/// <summary>Bottomed out and locked out until <see cref="RecoverFraction"/> is back.</summary>
	public bool IsExhausted { get; private set; }

	public float Fraction => MaxStamina <= 0f ? 0f : ( Current / MaxStamina ).Clamp( 0f, 1f );

	/// <summary>
	/// How out of breath you are, 0–1: nothing with wind to spare, rising as the bar empties below LowFraction, and
	/// all the way while winded. What the screen edges and the camera sway both follow, so they always agree.
	/// </summary>
	public float Tiredness => IsExhausted ? 1f
		: LowFraction <= 0f ? 0f
		: ( 1f - Fraction / LowFraction ).Clamp( 0f, 1f );

	/// <summary>
	/// Seconds since something asked for wind and was turned down — a punch, a dash or a block you couldn't pay for.
	/// Every spend comes from a button press, so this is the moment you pressed and nothing happened; the HUD
	/// flashes on it so a punch that didn't come out never reads as the game eating your input.
	/// </summary>
	public float TimeSinceRefused => timeSinceRefused;

	TimeSince timeSinceSpend;
	TimeSince timeSinceRefused;

	protected override void OnAwake()
	{
		Current = MaxStamina;

		// long ago, so nothing flashes on the first frame
		timeSinceRefused = 100f;
	}

	/// <summary>Can you afford this right now? Doesn't spend anything.</summary>
	public bool CanSpend( float amount ) => !IsExhausted && Current >= amount;

	/// <summary>Spend if you can afford it. Returns false and changes nothing if you can't.</summary>
	public bool TrySpend( float amount )
	{
		if ( !CanSpend( amount ) )
		{
			timeSinceRefused = 0;
			return false;
		}

		Current = MathF.Max( 0f, Current - amount );
		timeSinceSpend = 0;

		if ( Current <= 0f )
		{
			IsExhausted = true;

			if ( LogStamina )
				Log.Info( $"[Stamina] spent out — no attacks until {RecoverFraction * 100f:0}%" );

			OnExhausted?.Invoke();
		}

		return true;
	}

	/// <summary>
	/// <see cref="TrySpend"/> under the name the movement abilities call it by. They were
	/// written against a pool of whole bars; here the same call draws points out of this one
	/// pool, which is the point — a dash and a hook come out of the same wind.
	/// </summary>
	public bool TryConsume( float amount = 1f ) => TrySpend( amount );

	/// <summary>
	/// Straight back to full and out of any lockout. The run calls this at the start of every round —
	/// wind doesn't carry between fights any more than health does.
	/// </summary>
	public void Refill()
	{
		Current = MaxStamina;
		IsExhausted = false;
	}

	protected override void OnUpdate()
	{
		if ( Current < MaxStamina && timeSinceSpend > RegenDelay )
			Current = MathF.Min( MaxStamina, Current + RegenRate * Time.Delta );

		if ( IsExhausted && Fraction >= RecoverFraction )
		{
			IsExhausted = false;

			if ( LogStamina )
				Log.Info( "[Stamina] got your breath back" );

			OnRecovered?.Invoke();
		}

		DrawDebug();
	}

	void DrawDebug()
	{
		if ( !DebugDraw )
			return;

		int filled = (int)MathF.Round( Fraction * 20f );
		string bar = string.Concat( new string( '|', filled ), new string( '.', 20 - filled ) );
		string label = IsExhausted ? $"[{bar}] {Current:0} WINDED" : $"[{bar}] {Current:0}";

		DebugOverlay.ScreenText( DebugPosition, label, 15f, TextFlag.LeftTop, IsExhausted ? Color.Red : Color.White, 0f );
	}
}
