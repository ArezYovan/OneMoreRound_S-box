/// <summary>
/// Gameplay sounds: punches, grunts, blocks, the dash. Played through here rather than Sound.Play so that slow
/// motion can reach them — <see cref="HitStop"/> drags their pitch down with the clock at the end of a round.
/// Menus use <see cref="UiSound"/> instead, and stay at their own pitch whatever the game is doing.
/// </summary>
public static class Sfx
{
	/// <summary>Plays a sound at a spot in the world. An empty slot is simply silence.</summary>
	public static SoundHandle Play( SoundEvent sound, Vector3 position )
	{
		if ( sound is null )
			return null;

		var handle = Sound.Play( sound, position );
		Game.ActiveScene?.GetSystem<HitStop>()?.Track( handle );
		return handle;
	}

	/// <summary>Plays a sound flat, at the listener. An empty slot is simply silence.</summary>
	public static SoundHandle Play( SoundEvent sound )
	{
		if ( sound is null )
			return null;

		var handle = Sound.Play( sound );
		Game.ActiveScene?.GetSystem<HitStop>()?.Track( handle );
		return handle;
	}
}
