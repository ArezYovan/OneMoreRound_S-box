using System;
using System.Collections.Generic;

/// <summary>
/// Everything that bends the game's clock, in one place so nothing fights over it.
///
/// Two layers. The freeze-frame when a blow lands (<see cref="Freeze"/>): the scene drops to a crawl for a few
/// hundredths of a second, then snaps back — it's what makes a hit feel like it connected. And slow motion
/// (<see cref="SlowMo"/>): a longer, gentler slowdown, like the beat after a parry. Both can be running at
/// once; the slower of the two wins, and each ends on its own without undoing the other.
///
/// A GameObjectSystem so there's nothing to place in a scene — anything can call
/// <c>Scene.GetSystem&lt;HitStop&gt;()</c>. Timed on real time, since the scene's own clock is exactly what it
/// slows down.
/// </summary>
public sealed class HitStop : GameObjectSystem<HitStop>
{
	// the game speed with neither layer running, remembered when the first one starts
	float restoreScale = 1f;

	bool frozen;
	float freezeScale = 1f;
	RealTimeUntil timeUntilUnfreeze;

	bool slowed;
	float slowScale = 1f;
	RealTimeUntil timeUntilUnslow;

	// a locked slow motion (the finish of a round) ignores a plain EndSlowMo — throwing a punch ends a parry's slow
	// motion on purpose, and must not cut the last kill's short
	bool slowLocked;

	// what the slow motion does to sound: 1 leaves it alone, 0.5 drops everything tracked an octave
	float slowPitch = 1f;

	bool paused;

	// Gameplay sounds, with the pitch each started at, so slow motion can drag them down and give them back. Only
	// sounds played through Track (Sfx.Play) are here — menus stay at their own pitch whatever the game is doing.
	readonly List<(SoundHandle Handle, float Pitch)> sounds = new();

	// the pitch multiplier being applied right now, eased toward the target so nothing jumps with a click
	float soundPitch = 1f;
	float lastSoundTick;

	// how quickly sound slides to a new pitch, in full changes per second
	const float PitchEase = 6f;

	/// <summary>True while the whole scene is stopped dead — see <see cref="Pause"/>.</summary>
	public bool IsPaused => paused;

	/// <summary>True while slow motion is running.</summary>
	public bool IsSlowMo => slowed;

	public HitStop( Scene scene ) : base( scene )
	{
		Listen( Stage.StartUpdate, 0, Tick, "HitStop" );
	}

	/// <summary>
	/// Slow the scene to <paramref name="scale"/> for <paramref name="duration"/> real seconds. Hits that
	/// land during a freeze extend it rather than stacking on top, so a flurry can't lock the game up.
	/// </summary>
	public void Freeze( float duration, float scale = 0.05f )
	{
		if ( duration <= 0f )
			return;

		RememberRestore();

		if ( !frozen || timeUntilUnfreeze < duration )
			timeUntilUnfreeze = duration;

		freezeScale = frozen ? MathF.Min( freezeScale, scale ) : scale;
		frozen = true;
		ApplyScale();
	}

	/// <summary>
	/// Slow motion: the scene runs at <paramref name="scale"/> for <paramref name="duration"/> real seconds, or
	/// until <see cref="EndSlowMo"/>. A second call while it runs takes whichever lasts longer.
	/// <paramref name="soundPitch"/> below 1 drags every tracked sound down with it — 1 leaves sound alone.
	/// <paramref name="locked"/> slow motion only ends on its own or on EndSlowMo( force: true ).
	/// </summary>
	public void SlowMo( float duration, float scale, float soundPitch = 1f, bool locked = false )
	{
		if ( duration <= 0f )
			return;

		RememberRestore();

		if ( !slowed || timeUntilUnslow < duration )
			timeUntilUnslow = duration;

		slowScale = slowed ? MathF.Min( slowScale, scale ) : scale;
		slowPitch = slowed ? MathF.Min( slowPitch, soundPitch ) : soundPitch;
		slowLocked = slowed ? slowLocked || locked : locked;
		slowed = true;
		ApplyScale();
	}

	/// <summary>
	/// Back to full speed now, whatever was left of the slow motion. A freeze in progress still plays out. A locked
	/// slow motion stays unless <paramref name="force"/> — so a punch thrown into the end of a round can't cut it.
	/// </summary>
	public void EndSlowMo( bool force = false )
	{
		if ( !slowed || ( slowLocked && !force ) )
			return;

		slowed = false;
		ApplyScale();
	}

	/// <summary>
	/// Hand a playing gameplay sound over, so slow motion can change its pitch. Sfx.Play does this for you; call it
	/// directly for a sound started some other way, like a music track.
	/// </summary>
	public void Track( SoundHandle handle )
	{
		if ( !handle.IsValid() )
			return;

		float pitch = handle.Pitch;
		sounds.Add( (handle, pitch) );

		// a sound that starts in the middle of slow motion starts slowed, not at full pitch for a frame
		handle.Pitch = pitch * soundPitch;
	}

	/// <summary>
	/// Stop the scene outright: nothing moves, nothing swings, nothing lands, until <see cref="Resume"/>. Lives
	/// here rather than in whoever wants it so a hit-stop finishing mid-pause can't quietly start time again.
	/// </summary>
	public void Pause()
	{
		if ( paused )
			return;

		RememberRestore();
		paused = true;
		ApplyScale();
	}

	/// <summary>Time runs again, with any freeze or slow motion from before the pause dropped — except a locked slow motion, which carries on.</summary>
	public void Resume()
	{
		if ( !paused )
			return;

		paused = false;
		frozen = false;

		// a locked slow motion — the finish of a round — carries on through a trip to the pause menu
		if ( !slowLocked )
			slowed = false;

		ApplyScale();
	}

	void RememberRestore()
	{
		if ( !frozen && !slowed && !paused )
			restoreScale = Scene.TimeScale;
	}

	void ApplyScale()
	{
		float scale = 1f;

		if ( frozen )
			scale = MathF.Min( scale, freezeScale );

		if ( slowed )
			scale = MathF.Min( scale, slowScale );

		if ( paused )
			scale = 0f;

		Scene.TimeScale = restoreScale * scale;
	}

	void Tick()
	{
		bool changed = false;

		if ( frozen && timeUntilUnfreeze )
		{
			frozen = false;
			changed = true;
		}

		if ( slowed && timeUntilUnslow )
		{
			slowed = false;
			changed = true;
		}

		if ( changed )
			ApplyScale();

		TickSounds();
	}

	/// <summary>
	/// Eases the sound pitch toward what the slow motion wants — only the slow motion: a freeze-frame is over in a
	/// few hundredths of a second, and dropping the pitch for it would only swallow the very hit sound it's for.
	/// On real time, since the scene's clock is the thing being slowed.
	/// </summary>
	void TickSounds()
	{
		float now = RealTime.Now;
		float delta = MathF.Min( now - lastSoundTick, 0.1f );
		lastSoundTick = now;

		float target = slowed ? slowPitch : 1f;
		soundPitch = soundPitch.LerpTo( target, 1f - MathF.Exp( -PitchEase * delta ) );

		if ( MathF.Abs( soundPitch - target ) < 0.001f )
			soundPitch = target;

		for ( int i = sounds.Count - 1; i >= 0; i-- )
		{
			var (handle, pitch) = sounds[i];

			if ( !handle.IsValid() || handle.Finished )
			{
				sounds.RemoveAt( i );
				continue;
			}

			handle.Pitch = pitch * soundPitch;
		}
	}
}
