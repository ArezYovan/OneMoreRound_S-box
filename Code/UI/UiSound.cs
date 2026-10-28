using System.Collections.Generic;
using Sandbox.UI;

/// <summary>
/// The menus' sounds. Every one is an optional slot on the component it belongs to — left empty it plays nothing —
/// so a menu with no sounds set behaves exactly as it did before they existed.
/// </summary>
public static class UiSound
{
	/// <summary>Plays a UI sound flat, at the listener rather than somewhere in the world. Nothing if the slot is empty.</summary>
	public static void Play( SoundEvent sound )
	{
		if ( sound is not null )
			Sound.Play( sound );
	}
}

/// <summary>
/// One hover sound per button, played the moment the pointer arrives on it. Watches the buttons' :hover state rather
/// than listening for mouse-over events: those fire again every time the pointer crosses onto a label inside the
/// button, and a button would tick twice as you slid across its own text. :hover stays on the button the whole time,
/// so this sounds exactly when the highlight comes on.
/// </summary>
public sealed class HoverSounds
{
	HashSet<Panel> hovered = new();
	HashSet<Panel> next = new();

	/// <summary>
	/// Call every frame. Plays <paramref name="sound"/> once when any panel under <paramref name="root"/> carrying one of
	/// <paramref name="classes"/> starts being hovered.
	/// </summary>
	public void Update( Panel root, SoundEvent sound, params string[] classes )
	{
		next.Clear();
		bool arrived = false;

		if ( root is not null )
		{
			foreach ( var panel in root.Descendants )
			{
				if ( !panel.HasHovered || !HasAny( panel, classes ) )
					continue;

				next.Add( panel );
				arrived |= !hovered.Contains( panel );
			}
		}

		( hovered, next ) = ( next, hovered );

		if ( arrived )
			UiSound.Play( sound );
	}

	static bool HasAny( Panel panel, string[] classes )
	{
		foreach ( var name in classes )
		{
			if ( panel.HasClass( name ) )
				return true;
		}

		return false;
	}
}
