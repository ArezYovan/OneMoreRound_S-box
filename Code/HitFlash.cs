using System;
using System.Collections.Generic;

/// <summary>
/// A flash of solid colour on whatever gets hit: the moment damage lands, every model on the object is washed
/// over with <see cref="FlashColor"/>, then eases back to normal. Works off <see cref="Health"/>, so it goes on
/// anything that can be hurt — put it next to the enemy's Health.
///
/// The wash is done by the hitflash shader (shaders/hitflash.shader), so the model's material has to use it. A
/// material on any other shader simply doesn't flash. Unlike a tint, which only multiplies and leaves the dark
/// parts dark, this is the flat colour itself at <see cref="Opacity"/> 1 — it reads in a red room.
/// </summary>
public sealed class HitFlash : Component
{
	[Property, Description( "Optional, found automatically on this object or above it." )]
	public Health Health { get; set; }

	[Property, Description( "The colour it flashes." )]
	public Color FlashColor { get; set; } = new Color( 1f, 0.15f, 0.15f );

	[Property, Range( 0f, 1f ), Description( "How much of the flash colour covers the model at the peak. 1 is solid colour, 0 is no flash at all." )]
	public float Opacity { get; set; } = 0.8f;

	[Property, Description( "Seconds from the hit back to normal. Smaller is a snappier flash." )]
	public float Duration { get; set; } = 0.15f;

	[Property, Group( "Debug" ), Description( "Log what it found at the start and every flash, to see which step isn't happening." )]
	public bool LogFlash { get; set; }

	readonly List<ModelRenderer> renderers = new();
	TimeSince timeSinceHit = 999f;
	bool flashing;

	protected override void OnStart()
	{
		Health ??= Components.Get<Health>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );

		if ( Health.IsValid() )
			Health.OnDamaged += OnDamaged;

		// the model and anything worn on it
		renderers.AddRange( Components.GetAll<ModelRenderer>( FindMode.EverythingInSelfAndDescendants ) );

		if ( LogFlash )
			Log.Info( $"[HitFlash] {GameObject.Name}: health {( Health.IsValid() ? "found" : "MISSING" )}, {renderers.Count} model(s) to tint" );
	}

	protected override void OnDestroy()
	{
		if ( Health.IsValid() )
			Health.OnDamaged -= OnDamaged;
	}

	void OnDamaged( float amount, GameObject attacker )
	{
		timeSinceHit = 0;
		flashing = true;

		if ( LogFlash )
			Log.Info( $"[HitFlash] {GameObject.Name} hit for {amount:0.#} — flashing {renderers.Count} model(s)" );
	}

	protected override void OnUpdate()
	{
		if ( !flashing )
			return;

		float t = Duration > 0f ? ( timeSinceHit / Duration ).Clamp( 0f, 1f ) : 1f;

		// full strength on the hit and eased out, so it reads as a hit rather than a fade
		float strength = Opacity * ( 1f - t ) * ( 1f - t );

		foreach ( var renderer in renderers )
		{
			// per model rather than on the material, so one enemy flashing doesn't flash every enemy sharing it
			var attributes = renderer.IsValid() ? renderer.SceneObject?.Attributes : null;

			if ( attributes is null )
				continue;

			attributes.Set( "HitFlash", strength );
			attributes.Set( "HitFlashColor", new Vector3( FlashColor.r, FlashColor.g, FlashColor.b ) );
		}

		if ( t >= 1f )
			flashing = false;
	}
}
