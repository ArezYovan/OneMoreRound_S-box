using System;
using System.Linq;

/// <summary>
/// The night's soundtrack: one track for the fight, one for the bar, crossfaded as the run moves between
/// them. Silent once you're down or done, so the knockout's ringing has the room to itself.
///
/// Reads RunManager's phase and nothing else. Put it anywhere in the scene.
/// </summary>
public sealed class RunMusic : Component
{
	[Property, Group( "Tracks" ), Description( "Plays from the round's countdown until the bar." )]
	public SoundEvent FightMusic { get; set; }

	[Property, Group( "Tracks" ), Description( "Plays at the bar — the question and the drinks." )]
	public SoundEvent BarMusic { get; set; }

	[Property, Group( "Mix" ), Range( 0f, 1f )]
	public float Volume { get; set; } = 0.6f;

	[Property, Group( "Mix" ), Description( "Seconds for one track to fade out and the next to fade in." )]
	public float FadeTime { get; set; } = 1f;

	[Property, Group( "References" ), Description( "Optional, found automatically in the scene." )]
	public RunManager Run { get; set; }

	SoundEvent current;
	SoundHandle playing;
	float fade;

	protected override void OnUpdate()
	{
		Run ??= Scene.GetAllComponents<RunManager>().FirstOrDefault();

		if ( !Run.IsValid() )
			return;

		var wanted = TrackFor( Run.Phase );

		if ( wanted != current )
		{
			// the old one fades on its own; the new one starts silent and comes up below
			if ( playing.IsValid() )
				playing.Stop( FadeTime );

			playing = null;
			current = wanted;
			fade = 0f;
		}

		if ( current is null )
			return;

		// also how it loops: a track that has run out is simply started again, so it works whether or not
		// the sound file itself is set to loop
		if ( !playing.IsValid() || !playing.IsPlaying )
			playing = Sfx.Play( current );

		fade = FadeTime > 0f ? MathF.Min( 1f, fade + Time.Delta / FadeTime ) : 1f;

		if ( playing.IsValid() )
			playing.Volume = Volume * fade;
	}

	protected override void OnDestroy()
	{
		if ( playing.IsValid() )
			playing.Stop( 0f );
	}

	SoundEvent TrackFor( RunPhase phase ) => phase switch
	{
		RunPhase.Starting or RunPhase.Fighting or RunPhase.Paid => FightMusic,
		RunPhase.Deciding or RunPhase.Shop => BarMusic,
		_ => null
	};
}
