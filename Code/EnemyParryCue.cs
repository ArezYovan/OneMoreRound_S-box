using System.Collections.Generic;

/// <summary>
/// What the parry warning needs to know about a swing: which limb is coming, and when it will land.
///
/// "When" is the hard half. A blow lands on the clip's hit event, not at the end of the windup, and the code can't
/// see where in the clip that event sits. So it's measured: the first time any enemy throws a move, the time from
/// starting the swing to its hit event is recorded, and every swing of that move after it is timed off that.
/// Re-time a clip in ModelDoc and the warning follows it on the next swing.
/// </summary>
public sealed partial class Enemy
{
	[Property, Group( "Parry Cue" ), Description( "The bone the punch comes from. The parry warning sits over it." )]
	public string PunchBone { get; set; } = "DEF_hand_R";

	[Property, Group( "Parry Cue" ), Description( "The bone the jab comes from." )]
	public string JabBone { get; set; } = "DEF_hand_L";

	[Property, Group( "Parry Cue" ), Description( "The bone the kick comes from." )]
	public string KickBone { get; set; } = "DEF_foot_R";

	// shared by every enemy: they all play the same clips, so one swing teaches the lot
	static readonly Dictionary<EnemyAttack, float> measuredHitTimes = new();

	/// <summary>Counts up with every swing, so a warning can tell this swing from the last one.</summary>
	public int SwingNumber { get; private set; }

	/// <summary>A blow is on its way and hasn't landed yet — the stretch the parry warning is shown for.</summary>
	public bool IsSwinging => swingPending && !strikeArmed && State != EnemyState.Dead;

	/// <summary>Seconds since this swing started.</summary>
	public float SwingTime => timeSinceAttack;

	/// <summary>
	/// Seconds from the start of a swing to when it lands. Measured once a move has been seen; until then a guess
	/// from the windup and lunge, which is the best the code can do without the clip.
	/// </summary>
	public float ExpectedHitTime => measuredHitTimes.TryGetValue( CurrentAttack, out var time )
		? time
		: Windup + LungeTime * 0.5f;

	/// <summary>How early the guard can come up and still parry this swing. Zero: it can't be parried.</summary>
	public float SwingParryWindow => StatsFor( CurrentAttack ).ParryWindow;

	/// <summary>Where the blow is coming from — the fist or foot throwing it — or his chest if the bone isn't there.</summary>
	public Vector3 StrikePoint
	{
		get
		{
			var bone = CurrentAttack switch
			{
				EnemyAttack.Jab => JabBone,
				EnemyAttack.Kick => KickBone,
				_ => PunchBone
			};

			if ( Renderer.IsValid() && !string.IsNullOrEmpty( bone ) && Renderer.TryGetBoneTransform( bone, out var transform ) )
				return transform.Position;

			return WorldPosition + Vector3.Up * AttackHeight;
		}
	}

	/// <summary>The hit event just fired for this swing: remember how long it took to get there.</summary>
	void LearnHitTime()
	{
		measuredHitTimes[CurrentAttack] = timeSinceAttack;
	}
}
