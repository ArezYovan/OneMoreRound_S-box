using System;

/// <summary>
/// A health readout floating over whatever it's attached to. Sits just above the stagger meter
/// so the two read as one nameplate: how close he is to going down, and how close he is to
/// being pickup-able. Works on anything with Health, so it goes on a crate as happily as a man.
/// </summary>
public sealed class HealthBar : Component
{
	[Property, Description( "Optional, found automatically on this object." )]
	public Health Health { get; set; }

	[Property, Group( "Layout" ), Description( "Height above the object's origin. Defaults to just above Stagger's own readout so they stack instead of overlapping." )]
	public float Height { get; set; } = 94f;

	[Property, Group( "Layout" )] public float Size { get; set; } = 13f;

	[Property, Group( "Layout" ), Description( "How many segments the bar is drawn with." )]
	public int Segments { get; set; } = 12;

	[Property, Group( "Visibility" ), Description( "Show it even at full health. Off, an untouched patron stays clean and only the ones you've hit are labelled." )]
	public bool ShowWhenFull { get; set; } = true;

	[Property, Group( "Visibility" )] public bool HideWhenDead { get; set; } = true;

	[Property, Group( "Visibility" ), Description( "Stop drawing past this far away, so a full bar of patrons doesn't turn the back of the room into soup. Zero draws at any distance." )]
	public float MaxDrawDistance { get; set; } = 1500f;

	protected override void OnStart()
	{
		Health ??= Components.Get<Health>( FindMode.EnabledInSelf );

		if ( !Health.IsValid() )
			Log.Warning( $"{GameObject.Name}: HealthBar found no Health to read — it won't draw anything." );
	}

	protected override void OnUpdate()
	{
		if ( !Health.IsValid() )
			return;

		if ( HideWhenDead && Health.IsDead )
			return;

		if ( !ShowWhenFull && Health.Fraction >= 1f )
			return;

		var camera = Scene.Camera;

		if ( MaxDrawDistance > 0f && camera.IsValid() && WorldPosition.Distance( camera.WorldPosition ) > MaxDrawDistance )
			return;

		float fraction = Health.Fraction;
		int filled = (int)MathF.Round( fraction * Segments );

		string bar = string.Concat( new string( '|', filled ), new string( '.', Segments - filled ) );
		string label = $"[{bar}] {Health.CurrentHealth:0}";

		DebugOverlay.Text( WorldPosition + Vector3.Up * Height, label, Size, TextFlag.Center, ColorFor( fraction ), 0f, true );
	}

	/// <summary>Green down through yellow to red, so a glance at the colour is enough mid-fight.</summary>
	static Color ColorFor( float fraction ) => fraction > 0.5f
		? Color.Lerp( Color.Yellow, Color.Green, ( fraction - 0.5f ) * 2f )
		: Color.Lerp( Color.Red, Color.Yellow, fraction * 2f );
}
