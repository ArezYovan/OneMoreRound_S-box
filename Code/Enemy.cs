using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Citizen;
using Sandbox.Navigation;

public enum EnemyState
{
	Idle,
	Circling,
	Lurking,
	Engaging,
	Windup,
	Lunge,
	Recover,
	Parried,
	Fetching,
	Grabbing,
	Carrying,
	ThrowWindup,
	Throwing,
	Stunned,
	Staggered,
	Dead
}

/// <summary>
/// The moves he has. Which one he throws is picked from how far away you are with a little randomness on
/// top, so the same distance doesn't always get the same answer — see <see cref="Enemy.ChooseAttack"/>.
/// </summary>
public enum EnemyAttack
{
	Jab,
	Punch,
	Kick
}

/// <summary>
/// The Regular: walks at you, telegraphs hard, swings, falls over. Deliberately the dumbest
/// enemy in the design doc, because its job is to tell us whether the combat feels good, not
/// to be a challenge.
///
/// Built on a Rigidbody rather than a CharacterController on purpose. Knockback is the whole
/// point of this game, and a controller would have to reimplement being shoved around; a
/// rigidbody just gets shoved. It also means PlayerCombat's existing impulse works on these
/// with no special case.
///
/// Built for a crowd, in three tiers the director keeps: a couple of men swing at a time, a
/// handful circle you in a band waiting their turn, and everyone else is a reserve — out on a
/// post of his own somewhere in the map, drifting and watching until he's called up. That's
/// what keeps twenty of them a fight instead of a pile.
/// </summary>
public sealed partial class Enemy : Component
{
	[RequireComponent] Rigidbody Body { get; set; }
	[RequireComponent] Health Hp { get; set; }

	[Property, Group( "References" ), Description( "Optional, found automatically. Without one they simply never go down and can't be picked up." )]
	public Stagger Stagger { get; set; }

	[Property, Group( "References" ), Description( "Who to walk at. Found automatically from whatever has RBPlayerMovement on it." )]
	public GameObject Target { get; set; }

	[Property, Group( "References" ), Description( "The citizen. Found automatically in children." )]
	public SkinnedModelRenderer Renderer { get; set; }

	[Property, Group( "References" ), Description( "Optional, found automatically. Drives the citizen's locomotion animation." )]
	public CitizenAnimationHelper Animator { get; set; }

	[Property, Group( "References" ), Description( "Optional, found automatically. The upright collision hull, switched off when the ragdoll takes over." )]
	public Collider Hull { get; set; }

	[Property, Group( "Movement" )] public float MoveSpeed { get; set; } = 450f;
	[Property, Group( "Movement" )] public float Acceleration { get; set; } = 18f;
	[Property, Group( "Movement" )] public float TurnSpeed { get; set; } = 12f;

	[Property, Group( "Movement" ), Description( "How close it gets before it plants its feet and swings." )]
	public float StopDistance { get; set; } = 80f;

	[Property, Group( "Crowd" ), Description( "Nearest a circling man stands to you. Every circler not taking a swing holds a spot somewhere between this and BandMax, each at his own distance — spread through depth rather than all trying to stand on one circle, which is what used to pile them into a blob." )]
	public float BandMin { get; set; } = 220f;

	[Property, Group( "Crowd" ), Description( "Farthest a waiting man stands, and the range he'll accept a turn to attack from. Keep it within what he can cover before EngageTimeout runs out." )]
	public float BandMax { get; set; } = 500f;

	[Property, Group( "Crowd" ), Description( "Waiting men move at this fraction of MoveSpeed. They stalk rather than sprint, so when you dash off the whole room doesn't follow you in a line — and whoever is already close gets the next swing." )]
	public float StalkSpeedScale { get; set; } = 0.5f;

	[Property, Group( "Crowd" ), Description( "Past BandMax times this, a circler stops stalking and runs to catch up — which is also how a man just called up from the reserves comes in. Without it, a gap in the ring would take an age to fill." )]
	public float CatchUpFactor { get; set; } = 1.3f;

	[Property, Group( "Crowd" ), Description( "Close enough to his spot to stop shuffling. Generous on purpose: the spots are far out and move with you, so a tight tolerance has the whole crowd twitching on the spot." )]
	public float SpotTolerance { get; set; } = 60f;

	[Property, Group( "Crowd" ), Description( "Give the turn back if they cannot reach him in this long — otherwise one man stuck behind a table holds a turn nobody else can use." )]
	public float EngageTimeout { get; set; } = 3f;

	[Property, Group( "Crowd" ), Description( "Elbow room kept between shoulders. Measured from the edge of each body rather than its middle, so it keeps working whatever the model is scaled to — measured from the middle, it used to be smaller than a man and never fired until they were standing in each other." )]
	public float ShoulderGap { get; set; } = 40f;

	[Property, Group( "Crowd" ), Description( "How hard they push off each other. This is what stops the crowd collapsing into one grinding pile." )]
	public float SeparationStrength { get; set; } = 0.9f;

	[Property, Group( "Crowd" ), Description( "Per-man random spread on speed and windup. Nothing reads as artificial faster than nine men marching and swinging on the same frame." )]
	public float Variance { get; set; } = 0.18f;

	[Property, Group( "Reserve" ), Description( "Reserves drift at this fraction of MoveSpeed. Slow on purpose: they're a threat at the edge of the fight, not a second crowd walking at you." )]
	public float ReserveSpeedScale { get; set; } = 0.3f;

	[Property, Group( "Reserve" ), Description( "Nearest a reserve settles to you. Walk up on his post and he gives ground to a new one — he only comes at you once the director calls him up." )]
	public float ReserveMinDistance { get; set; } = 800f;

	[Property, Group( "Reserve" ), Description( "Farthest post a reserve picks from you, so the reserves stay within reach of the fight instead of wandering off to the far end of the map." )]
	public float ReserveMaxDistance { get; set; } = 1800f;

	[Property, Group( "Reserve" ), Description( "How far from where he stands a reserve looks for his next post. Small keeps him drifting instead of marching across the map." )]
	public float PostSearchRadius { get; set; } = 600f;

	[Property, Group( "Reserve" ), Description( "Least distance between two reserves' posts. This is what spreads them over the map instead of into a clump." )]
	public float PostSpacing { get; set; } = 300f;

	[Property, Group( "Reserve" ), Description( "Shortest rest on a post before he drifts to another." )]
	public float PostHoldMin { get; set; } = 2f;

	[Property, Group( "Reserve" ), Description( "Longest rest on a post before he drifts to another." )]
	public float PostHoldMax { get; set; } = 5f;

	[Property, Group( "Navigation" ), Description( "Closer than this to where he's going, he walks straight at it instead of following the navmesh. Pathing across open floor only makes a man zigzag between polygon corners." )]
	public float DirectDistance { get; set; } = 200f;

	[Property, Group( "Navigation" ), Description( "How often a path is re-planned while its destination moves — the player, or a spot in the ring that follows him." )]
	public float PathRefreshTime { get; set; } = 0.5f;

	[Property, Group( "Navigation" ), Description( "How close to a corner of his path counts as having reached it." )]
	public float CornerRadius { get; set; } = 48f;

	// ---------------------------------------------------------------- attacks

	[Property, Group( "Attack" ), Description( "Shared by every move: how wide the swing's hit test is." )]
	public float AttackRadius { get; set; } = 18f;

	[Property, Group( "Attack" ), Description( "How far up the swing comes from. Roughly chest height." )]
	public float AttackHeight { get; set; } = 50f;

	[Property, Group( "Attack" ), Description( "Dead time after swinging, hit or miss. This is the window you're meant to punish." )]
	public float RecoverTime { get; set; } = 0.6f;

	[Property, Group( "Attack" ), Description( "Least time between one swing and the next." )]
	public float AttackCooldown { get; set; } = 0.4f;

	[Property, Group( "Attack" ), Description( "How much the pick wanders off what the distance says. 0 is always the obvious move, 0.5 is a coin toss — this is what stops him being readable." )]
	public float AttackVariance { get; set; } = 0.25f;

	[Property, Group( "Attack" ), Description( "The Type Name on the generic anim event that marks the frame his blow connects — the same event the player's clips use. A move only lands from that frame on, so what you see hit you is what hits you." )]
	public string HitEvent { get; set; } = "hit";

	[Property, Group( "Attack" ), Description( "How long after the hit frame a blow can still connect. A short window rather than one instant, so a lunge still closing the last few units lands instead of whiffing by a hair." )]
	public float HitWindow { get; set; } = 0.12f;

	[Property, Group( "Attack" ), Description( "How long past the windup and lunge he keeps waiting for the hit event before landing anyway. A safety net, not a timing — if its warning shows up, the event is missing, misnamed, or placed later than this." )]
	public float HitEventGrace { get; set; } = 0.3f;

	// ---- the plain punch: his bread and butter, and the one everything else is measured against

	[Property, Group( "Punch" ), Description( "Which slot in the animgraph's attack Selector this move is." )]
	public int PunchSlot { get; set; } = 1;

	[Property, Group( "Punch" )] public float PunchDamage { get; set; } = 14f;
	[Property, Group( "Punch" )] public float PunchRange { get; set; } = 100f;

	[Property, Group( "Punch" ), Description( "The telegraph — how long you get to see it coming." )]
	public float PunchWindup { get; set; } = 0.45f;

	[Property, Group( "Punch" )] public float PunchKnockback { get; set; } = 260f;
	[Property, Group( "Punch" )] public float PunchLift { get; set; } = 0.1f;

	[Property, Group( "Punch" ), Description( "Most the lunge carries him on this move." )]
	public float PunchLunge { get; set; } = 180f;

	[Property, Group( "Punch" ), Description( "How close to the hit frame the guard has to come up to parry this one. Bigger is easier; zero cannot be parried at all." )]
	public float PunchParryWindow { get; set; } = 0.22f;

	[Property, Group( "Punch" ), Description( "Closest he'll throw it from." )]
	public float PunchMinDistance { get; set; }

	[Property, Group( "Punch" ), Description( "Farthest he'll throw it from, before the lunge closes the rest." )]
	public float PunchMaxDistance { get; set; } = 170f;

	[Property, Group( "Punch" ), Description( "How often it's picked against the others, when more than one fits the distance." )]
	public float PunchWeight { get; set; } = 1f;

	// ---- the jab: quick, cheap, and what he throws when you crowd him

	[Property, Group( "Jab" ), Description( "Which slot in the animgraph's attack Selector this move is." )]
	public int JabSlot { get; set; }

	[Property, Group( "Jab" )] public float JabDamage { get; set; } = 8f;
	[Property, Group( "Jab" )] public float JabRange { get; set; } = 90f;

	[Property, Group( "Jab" ), Description( "Half the punch's telegraph: fast enough to punish standing on top of him." )]
	public float JabWindup { get; set; } = 0.28f;

	[Property, Group( "Jab" )] public float JabKnockback { get; set; } = 120f;
	[Property, Group( "Jab" )] public float JabLift { get; set; } = 0.05f;

	[Property, Group( "Jab" ), Description( "Most the lunge carries him on this move. Short — the jab is for when he's already close." )]
	public float JabLunge { get; set; } = 120f;

	[Property, Group( "Jab" ), Description( "How close to the hit frame the guard has to come up to parry this one. Tighter than the punch, because it comes out faster." )]
	public float JabParryWindow { get; set; } = 0.14f;

	[Property, Group( "Jab" )] public float JabMinDistance { get; set; }
	[Property, Group( "Jab" )] public float JabMaxDistance { get; set; } = 120f;
	[Property, Group( "Jab" )] public float JabWeight { get; set; } = 1.2f;

	// ---- the kick: his special. Rare, unparriable, and it moves you whether you block it or not

	[Property, Group( "Kick" ), Description( "Which slot in the animgraph's attack Selector this move is." )]
	public int KickSlot { get; set; } = 2;

	[Property, Group( "Kick" )] public float KickDamage { get; set; } = 20f;
	[Property, Group( "Kick" )] public float KickRange { get; set; } = 120f;

	[Property, Group( "Kick" ), Description( "A long telegraph: this is the one you're meant to see coming and get out of the way of." )]
	public float KickWindup { get; set; } = 0.7f;

	[Property, Group( "Kick" )] public float KickKnockback { get; set; } = 520f;
	[Property, Group( "Kick" )] public float KickLift { get; set; } = 0.25f;

	[Property, Group( "Kick" ), Description( "Most the lunge carries him on this move. Long: this is the one that catches you backing off." )]
	public float KickLunge { get; set; } = 230f;

	[Property, Group( "Kick" ), Description( "Zero means it cannot be parried at all — the kick is meant to be dodged, not caught. Put a number in to make it parriable like the others." )]
	public float KickParryWindow { get; set; }

	[Property, Group( "Kick" ), Description( "How hard a blocked kick shoves you back. Blocking it saves you the damage, never the ground." )]
	public float KickBlockPush { get; set; } = 420f;

	[Property, Group( "Kick" ), Description( "Least time between one of his kicks and the next, so it stays a special rather than his answer to everything." )]
	public float KickCooldown { get; set; } = 6f;

	[Property, Group( "Kick" ), Description( "Closest he'll kick from. Below this he's too close to wind one up and jabs instead." )]
	public float KickMinDistance { get; set; } = 90f;

	[Property, Group( "Kick" )] public float KickMaxDistance { get; set; } = 260f;

	[Property, Group( "Kick" ), Description( "Kept low on purpose: the cooldown decides how rare it is, and this decides how often he takes it when it is off cooldown." )]
	public float KickWeight { get; set; } = 0.7f;

	// ---- the lunge every move shares

	[Property, Group( "Lunge" ), Description( "Fraction of MoveSpeed he edges in at through the windup. He still telegraphs, but he's closing on you while he does it instead of standing there winding up at the air." )]
	public float WindupCreep { get; set; } = 0.15f;

	[Property, Group( "Lunge" ), Description( "How long the lunge lasts, and so how long a blow can land for. Short and fast reads as a strike; long reads as a slide." )]
	public float LungeTime { get; set; } = 0.18f;

	[Property, Group( "Lunge" ), Description( "How far short of your centre he aims to end up, so he hits you rather than barrelling through you." )]
	public float LungeStopDistance { get; set; } = 40f;

	// ---- what a landed blow feels like

	[Property, Group( "Hitstop" ), Description( "How long the whole game freezes when a blow lands on you. A few hundredths of a second is what makes a hit feel like it connected." )]
	public float HitStopTime { get; set; } = 0.08f;

	[Property, Group( "Hitstop" ), Description( "Freeze for a blow you blocked. Shorter than a clean hit, so the two feel different." )]
	public float BlockedHitStopTime { get; set; } = 0.04f;

	[Property, Group( "Hitstop" ), Description( "Camera shake when a blow lands on you, 0–1. A blocked one shakes at half this." )]
	public float HitShake { get; set; } = 0.5f;

	[Property, Group( "Hitstop" ), Description( "Game speed during the freeze. Not quite zero, so it reads as a jolt rather than a hitch." )]
	public float HitStopScale { get; set; } = 0.05f;

	[Property, Group( "Reactions" ), Description( "How long he stands open after you parry him, in game time — which the parry's slow motion stretches out, so it lasts a good deal longer than this on the clock." )]
	public float ParriedTime { get; set; } = 1.2f;

	[Property, Group( "Reactions" ), Description( "Least time the AI stops steering after being hit. Without this the chase velocity overwrites the knockback on the very next tick and punches feel like they do nothing — this is what lets a hit actually move them." )]
	public float StunTime { get; set; } = 0.35f;

	[Property, Group( "Reactions" ), Description( "Past StunTime, stay hands-off until he has slowed below this. A kick launches a man across the room, and handing control back mid-flight would have him brake to a stop in the air." )]
	public float StunRecoverSpeed { get; set; } = 200f;

	[Property, Group( "Reactions" ), Description( "Longest a hit keeps him hands-off however fast he's still sliding, so nobody gets stuck stunned on a slope." )]
	public float MaxStunTime { get; set; } = 1.5f;

	[Property, Group( "Reactions" )] public bool RagdollOnDeath { get; set; } = true;


	// ---------------------------------------------------------------- sound
	// Every slot is optional: an empty one is simply silence, so they can be filled in one at a time
	// while you listen to each. Swings play where he stands, hits play where they landed.

	[Property, Group( "Sound" ), Description( "The effort as he throws the punch, hit or miss." )]
	public SoundEvent PunchSwingSound { get; set; }

	[Property, Group( "Sound" ), Description( "Where the punch connects with you." )]
	public SoundEvent PunchHitSound { get; set; }

	[Property, Group( "Sound" ), Description( "The effort as he throws the jab." )]
	public SoundEvent JabSwingSound { get; set; }

	[Property, Group( "Sound" ), Description( "Where the jab connects." )]
	public SoundEvent JabHitSound { get; set; }

	[Property, Group( "Sound" ), Description( "The effort as he throws the kick — the tell you hear before you see it." )]
	public SoundEvent KickSwingSound { get; set; }

	[Property, Group( "Sound" ), Description( "Where the kick connects." )]
	public SoundEvent KickHitSound { get; set; }

	[Property, Group( "Sound" ), Description( "A blow of his coming off your guard. The kick's shove uses this too." )]
	public SoundEvent BlockedSound { get; set; }

	[Property, Group( "Sound" ), Description( "The moment you parry him — his half of it. The player's own parry sound lives on PlayerBlock." )]
	public SoundEvent ParriedSound { get; set; }

	[Property, Group( "Sound" ), Description( "A swing that touched nothing. Leave empty if the swing sound already sells the whiff." )]
	public SoundEvent MissSound { get; set; }

	[Property, Group( "Sound" ), Description( "Taking a hit: the grunt, not the impact — the impact belongs to whoever threw it." )]
	public SoundEvent HurtSound { get; set; }

	[Property, Group( "Sound" ), Description( "Going down: the moment he's staggered and can be picked up." )]
	public SoundEvent StaggerSound { get; set; }

	[Property, Group( "Sound" ), Description( "Getting back up off the floor." )]
	public SoundEvent RecoverSound { get; set; }

	[Property, Group( "Sound" ), Description( "Out for the night." )]
	public SoundEvent DeathSound { get; set; }

	[Property, Group( "Debug" )] public bool LogState { get; set; } = true;

	[Property, Group( "Debug" ), Description( "Practice dummy. He stays where he is, never turns and never swings, but everything done TO him still plays out as in a fight: knockback, the flinch, stagger, being grabbed, dying. Can be flipped mid-play." )]
	public bool ForceIdle { get; set; }

	[Property] public EnemyState State { get; private set; } = EnemyState.Idle;

	/// <summary>Which move he's in the middle of throwing.</summary>
	[Property] public EnemyAttack CurrentAttack { get; private set; } = EnemyAttack.Punch;


	/// <summary>Upright and still in the fight, so worth a place in the crowd.</summary>
	public bool WantsSlot => State != EnemyState.Dead && State != EnemyState.Staggered;

	/// <summary>Waiting, off cooldown, inside the band and not a practice dummy — someone the director may hand the next swing to.</summary>
	public bool WantsTurn => !ForceIdle && State == EnemyState.Circling && timeSinceAttack > AttackCooldown && DistanceToTarget <= BandMax;

	/// <summary>Flat distance to whoever he's after. Infinite with nobody to chase.</summary>
	public float DistanceToTarget => Target.IsValid() ? ( Target.WorldPosition - WorldPosition ).WithZ( 0 ).Length : float.MaxValue;

	/// <summary>Half the width of his hull in world units — what everyone else's ShoulderGap is measured out from.</summary>
	public float BodyRadius { get; private set; }

	/// <summary>On his feet, not a practice dummy or thrower and not busy with a throw, so the director may put him in the ring around the player.</summary>
	public bool CanCircle => WantsSlot && !ForceIdle && !PracticeThrower && !IsThrowing;

	/// <summary>The spot a reserve is holding or walking to. Null for anyone who isn't one.</summary>
	public Vector3? Post { get; private set; }

	TimeSince timeInState;
	TimeSince timeSinceAttack;
	TimeSince timeSinceKick = 999f;
	EnemyDirector director;
	float speedScale = 1f;
	float windupScale = 1f;
	float bandDistance;
	Vector3? pendingLaunch;
	Vector3 lungeVelocity;

	// a swing waiting on its hit frame, and whether that frame has come — the punch only connects inside
	// the HitWindow after it
	bool swingPending;
	bool strikeArmed;
	TimeSince timeSinceArmed;
	ModelPhysics ragdoll;

	// reserve bookkeeping: whether he's settled on his post yet, and how long he rests there
	bool resting;
	TimeUntil timeUntilNextPost;
	TimeSince timeSincePostPicked;
	const int PostSearchTries = 8;

	// the navmesh route he's following, when his destination is far enough away to need one
	readonly List<Vector3> path = new();
	int pathIndex;
	Vector3 pathGoal;
	TimeSince timeSincePath;

	// re-plan once the destination has moved this far, without waiting for PathRefreshTime
	const float RepathDistance = 96f;

	protected override void OnStart()
	{
		Renderer ??= Components.Get<SkinnedModelRenderer>( FindMode.Enabled | FindMode.InSelf | FindMode.InDescendants );
		Animator ??= Components.Get<CitizenAnimationHelper>( FindMode.Enabled | FindMode.InSelf | FindMode.InDescendants );
		Hull ??= Components.Get<Collider>( FindMode.EnabledInSelf );
		Stagger ??= Components.Get<Stagger>( FindMode.EnabledInSelf );

		BodyRadius = MeasureBodyRadius();

		director = Scene.GetSystem<EnemyDirector>();
		director.Register( this );

		// each man is his own slightly different fighter
		speedScale = Game.Random.Float( 1f - Variance, 1f + Variance );
		windupScale = Game.Random.Float( 1f - Variance, 1f + Variance );

		Target ??= Scene.GetAllComponents<RBPlayerMovement>().FirstOrDefault()?.GameObject;

		if ( !Target.IsValid() )
			Log.Warning( $"{GameObject.Name}: Enemy found nothing with RBPlayerMovement to chase — it'll just stand there." );

		if ( Hp.IsValid() )
		{
			Hp.OnDamaged += OnHurt;
			Hp.OnDied += OnDeath;
		}

		if ( Stagger.IsValid() )
		{
			Stagger.OnStaggered += OnStaggered;
			Stagger.OnRecovered += OnRecovered;
		}

		if ( Renderer.IsValid() )
			Renderer.OnGenericEvent += OnRendererEvent;

		// the animgraph's punch lives under holdtype "melee_punch" (index 5). Left at "none" the
		// b_attack trigger fires into a state that doesn't exist and the swing is invisible — which
		// is exactly what it did before this line
		if ( Animator.IsValid() )
			Animator.HoldType = CitizenAnimationHelper.HoldTypes.Punch;

		// shoved, not toppled: a kick should send them skidding across the floor upright, and the
		// falling over is the ragdoll's job when they die. Locked here rather than left to the
		// inspector so a hand-built test enemy can't get this subtly wrong
		if ( Body.IsValid() )
			Body.Locking = new PhysicsLock { Pitch = true, Roll = true };

		// everyone starts in reserve; the director calls up the best placed the first time it looks
		Enter( EnemyState.Lurking );
	}

	protected override void OnDestroy()
	{
		// anything in his hand falls out of it rather than hanging in the air with its physics off
		if ( IsThrowing )
			EndThrow();

		director?.Unregister( this );

		if ( Renderer.IsValid() )
			Renderer.OnGenericEvent -= OnRendererEvent;

		if ( !Hp.IsValid() )
			return;

		Hp.OnDamaged -= OnHurt;
		Hp.OnDied -= OnDeath;

		if ( !Stagger.IsValid() )
			return;

		Stagger.OnStaggered -= OnStaggered;
		Stagger.OnRecovered -= OnRecovered;
	}

	protected override void OnFixedUpdate()
	{
		if ( State == EnemyState.Dead )
		{
			ApplyPendingLaunch();
			return;
		}

		// the dummy only gives up his own moves. Stunned and Staggered fall through untouched, so a hit
		// still sends him flying and he only plants his feet again once it's done with him
		if ( ForceIdle && ( State is EnemyState.Circling or EnemyState.Lurking or EnemyState.Engaging or EnemyState.Windup or EnemyState.Lunge or EnemyState.Recover || IsThrowing ) )
		{
			// parked in reserve, which hands back any attack turn he was holding along with his place in the ring
			Enter( EnemyState.Lurking );
			Brake();

			Body.AngularVelocity = Vector3.Zero;
			UpdateAnimation();
			return;
		}

		if ( !Target.IsValid() )
		{
			Brake();
			UpdateAnimation();
			return;
		}

		var toTarget = ( Target.WorldPosition - WorldPosition ).WithZ( 0 );
		float distance = toTarget.Length;

		switch ( State )
		{
			// no steering at all here: the hit's impulse is in the rigidbody, and the only way it reads on
			// screen is if we keep our hands off the velocity long enough for it to travel — which for a
			// kick means until he's actually stopped flying, not just for a fixed beat
			case EnemyState.Stunned:
			{
				bool settled = Body.Velocity.WithZ( 0 ).Length <= StunRecoverSpeed;

				if ( timeInState > StunTime && ( settled || timeInState > MaxStunTime ) )
					EnterWaiting();
				break;
			}

			// hands off entirely: they're being carried, thrown, or lying there, and every one of
			// those is somebody else's physics to own. Stagger decides when this ends, not us
			case EnemyState.Staggered:
				break;

			// one of the few in the ring: holding his own spot in the band until the director hands him a turn.
			// He stalks while he's in the band, and runs when he's well outside it — left behind, or just called
			// up from the reserves
			case EnemyState.Circling:
			{
				// the director gave his place to someone better placed
				if ( !director.IsCircler( this, Target.WorldPosition ) )
				{
					Enter( EnemyState.Lurking );
					break;
				}

				// with no reserve near anything, a circler may be the one sent for a prop — see EnemyThrowing
				if ( TryStartThrow() )
					break;

				bool running = distance > BandMax * CatchUpFactor;

				// running in, he looks where he's going; in the band, he watches you
				FaceTarget( running ? Body.Velocity.WithZ( 0 ) : toTarget );

				Navigate( director.GetSlotPosition( this, Target.WorldPosition, bandDistance ), SpotTolerance, running ? 1f : StalkSpeedScale );

				if ( WantsTurn && director.RequestAttack( this ) )
					Enter( EnemyState.Engaging );
				break;
			}

			// a reserve: out of the fight on a post of his own, drifting between posts and watching until the
			// director calls him up
			case EnemyState.Lurking:
				if ( director.IsCircler( this, Target.WorldPosition ) )
				{
					Enter( EnemyState.Circling );
					break;
				}

				// sent off for something to throw — see EnemyThrowing
				if ( TryStartThrow() )
					break;

				FaceTarget( toTarget );

				// a practice thrower waits where he was put, with something to throw beside him
				if ( PracticeThrower )
				{
					SupplyPracticeProp();
					Brake();
					break;
				}

				Lurk();
				break;

			// he has a turn: close the distance and commit to it
			case EnemyState.Engaging:
				FaceTarget( toTarget );
				Navigate( Target.WorldPosition, StopDistance * 0.9f, 1f );

				if ( distance <= StopDistance )
					BeginSwing();
				else if ( timeInState > EngageTimeout )
					EnterWaiting();
				break;

			// the telegraph: still tracking you and edging in, so it's a threat coming at you rather than a man
			// winding up on the spot
			case EnemyState.Windup:
				FaceTarget( toTarget );

				if ( distance > LungeStopDistance + 20f )
					MoveTowards( Target.WorldPosition, LungeStopDistance + 20f, WindupCreep );
				else
					Brake();

				if ( timeInState >= Windup )
					BeginLunge( distance );
				break;

			// committed: no more turning, so stepping aside or dashing makes him miss — backing off doesn't.
			// This is only the movement; the punch itself lands on the clip's hit frame, in TickStrike
			case EnemyState.Lunge:
				Body.Velocity = lungeVelocity.WithZ( Body.Velocity.z );

				if ( timeInState >= LungeTime )
					Enter( EnemyState.Recover );
				break;

			case EnemyState.Recover:
				Brake();

				if ( timeInState >= RecoverTime )
					EnterWaiting();
				break;

			// knocked off balance by a parry: rooted and open, not turning, not swinging, until it wears off
			case EnemyState.Parried:
				Brake();

				if ( timeInState >= ParriedTime )
					EnterWaiting();
				break;

			case EnemyState.Fetching:
			case EnemyState.Grabbing:
			case EnemyState.Carrying:
			case EnemyState.ThrowWindup:
			case EnemyState.Throwing:
				UpdateThrow( toTarget, distance );
				break;
		}

		if ( swingPending )
			TickStrike();

		Body.AngularVelocity = Vector3.Zero;
		UpdateAnimation();
	}

	/// <summary>
	/// Lands the punch, the same way PlayerCombat lands the player's: nothing before the clip's hit frame,
	/// then a short window where it connects with whatever's in front of him. Runs through the windup, the
	/// lunge and the recover alike, since where the hit frame falls is up to the clip.
	/// </summary>
	void TickStrike()
	{
		if ( !strikeArmed && timeSinceAttack >= Windup + LungeTime + HitEventGrace )
		{
			// landed late rather than lost: a punch that silently never connects reads as the enemy being
			// broken, where this says exactly what to go and fix
			Log.Warning( $"[Enemy] {GameObject.Name}'s punch never got a '{HitEvent}' event — landed on the timer instead. Check the Type Name on the punch clip's generic event, and that the graph actually plays the clip." );
			ArmStrike();
		}

		if ( !strikeArmed )
			return;

		if ( Strike() )
		{
			EndSwing();

			// stopped by the man he hit, rather than carrying on through him
			if ( State is EnemyState.Windup or EnemyState.Lunge )
			{
				Body.Velocity = Body.Velocity.WithX( 0 ).WithY( 0 );
				Enter( EnemyState.Recover );
			}

			return;
		}

		if ( timeSinceArmed <= HitWindow )
			return;

		EndSwing();

		PlaySound( MissSound, WorldPosition );

		if ( LogState )
			Log.Info( $"[Enemy] {GameObject.Name} missed" );

		// the fist has been and gone; a clip that hits before the lunge would otherwise lunge after it
		if ( State == EnemyState.Windup )
			Enter( EnemyState.Recover );
	}

	void ArmStrike()
	{
		strikeArmed = true;
		timeSinceArmed = 0;
	}

	void EndSwing()
	{
		swingPending = false;
		strikeArmed = false;
	}

	void OnRendererEvent( SceneModel.GenericEvent e )
	{
		if ( !string.Equals( e.Type, HitEvent, StringComparison.OrdinalIgnoreCase ) )
			return;

		// only a swing still waiting on its frame counts. Anything else — the clip playing on after the
		// timer already landed it, say — is the animation's business, not a second punch
		if ( swingPending && !strikeArmed )
		{
			LearnHitTime();
			ArmStrike();
		}
	}

	void Enter( EnemyState state )
	{
		if ( State == state )
			return;

		// leaving the throw by any route — thrown, hit, staggered, killed, given up — lets go of it properly
		if ( IsThrowState( State ) && !IsThrowState( state ) )
			EndThrow();

		// a turn is only held while actually going through with an attack, so being hit, staggered
		// or killed hands it straight back to whoever is waiting for one
		bool keepsTurn = state is EnemyState.Engaging or EnemyState.Windup or EnemyState.Lunge or EnemyState.Recover;

		if ( !keepsTurn )
			director?.ReleaseAttack( this );

		// back on his feet and steering himself, so whatever launched or threw him is over. Left armed,
		// a man kicked a moment ago would hit the next friend he simply walked into
		if ( state is EnemyState.Circling or EnemyState.Lurking or EnemyState.Engaging )
		{
			var thrown = Components.Get<ThrownObject>();

			if ( thrown.IsValid() )
				thrown.Disarm();
		}

		// a fresh distance every time he goes back to waiting, so the crowd keeps shifting through depth
		// instead of settling into one shape for the whole fight
		if ( state == EnemyState.Circling )
			bandDistance = RollBandDistance();

		// any change gives up his post — a man going back into reserve picks a fresh one for where the fight
		// is now, not where it was when he left
		Post = null;
		resting = false;

		// hit, staggered, killed or sent back to waiting mid-swing: the punch goes with it
		if ( state is not ( EnemyState.Windup or EnemyState.Lunge or EnemyState.Recover ) )
			EndSwing();

		State = state;
		timeInState = 0;

		if ( LogState )
			Log.Info( $"[Enemy] {GameObject.Name} → {state}" );
	}

	void MoveTowards( Vector3 destination, float arriveDistance, float pace )
	{
		var to = ( destination - WorldPosition ).WithZ( 0 );

		// stop short rather than shouldering into things — walking into the player and pushing him
		// around the room reads as a bug, not as pressure
		var wish = to.Length > arriveDistance ? to.Normal * MoveSpeed * speedScale * pace : Vector3.Zero;

		wish += Separation() * MoveSpeed * SeparationStrength;

		// same exponential approach the player uses, so both move with the same weight
		float t = 1f - MathF.Exp( -Acceleration * Time.Delta );
		var velocity = Body.Velocity;

		Body.Velocity = velocity.WithZ( 0 ).LerpTo( wish, t ).WithZ( velocity.z );
	}

	/// <summary>
	/// Elbow room. The band spreads them out in principle, but men heading for neighbouring spots
	/// still grind against each other on the way — this is what keeps the crowd from collapsing back
	/// into one shoving pile.
	/// </summary>
	Vector3 Separation()
	{
		if ( director is null )
			return Vector3.Zero;

		var push = Vector3.Zero;

		foreach ( var other in director.Enemies )
		{
			if ( other == this || !other.IsValid() || !other.WantsSlot )
				continue;

			// both bodies plus the gap between them, so it starts pushing before shoulders touch
			float reach = BodyRadius + other.BodyRadius + ShoulderGap;

			var away = ( WorldPosition - other.WorldPosition ).WithZ( 0 );
			float distance = away.Length;

			if ( distance >= reach || distance <= 0.01f )
				continue;

			push += away.Normal * ( 1f - distance / reach );
		}

		return push;
	}

	/// <summary>Back to waiting after a swing, a hit or a stagger — in the ring if the director still has a place for him there, in reserve if not.</summary>
	void EnterWaiting()
	{
		bool circling = Target.IsValid() && director is not null && director.IsCircler( this, Target.WorldPosition );
		Enter( circling ? EnemyState.Circling : EnemyState.Lurking );
	}

	/// <summary>
	/// A reserve's whole job: hold a post, rest on it a few seconds, drift to another nearby. He picks a new one
	/// early if you walk up on the one he has — a reserve gives ground until he's called up.
	/// </summary>
	void Lurk()
	{
		var target = Target.WorldPosition;

		bool crowded = Post is Vector3 held && FlatDistance( held, target ) < ReserveMinDistance * 0.75f;
		bool restOver = resting && timeUntilNextPost;

		// a crowded re-pick is held to once a second: with nowhere better to go he'd otherwise search every tick
		if ( Post is null || restOver || ( crowded && timeSincePostPicked > 1f ) )
		{
			timeSincePostPicked = 0;
			resting = false;

			if ( TryPickPost( target, out var picked ) )
			{
				Post = picked;
			}
			else
			{
				// nowhere fits right now: hold where he stands and look again after a short rest
				Post = WorldPosition;
				resting = true;
				timeUntilNextPost = PostHoldMin;
			}
		}

		var post = Post.Value;

		if ( !resting && FlatDistance( post, WorldPosition ) <= SpotTolerance )
		{
			resting = true;
			timeUntilNextPost = Game.Random.Float( PostHoldMin, PostHoldMax );
		}

		Navigate( post, SpotTolerance, ReserveSpeedScale );
	}

	/// <summary>
	/// A spot for a reserve: near where he is, no nearer the player than ReserveMinDistance nor farther than
	/// ReserveMaxDistance, and clear of every other reserve's post. Searched around a point stepped away from the
	/// player if he's too close and toward him if he's too far, so either way he heads back into that band.
	/// </summary>
	bool TryPickPost( Vector3 target, out Vector3 post )
	{
		var away = ( WorldPosition - target ).WithZ( 0 );
		away = away.IsNearlyZero() ? Vector3.Forward : away.Normal;

		float fromTarget = FlatDistance( WorldPosition, target );

		var center = fromTarget < ReserveMinDistance ? WorldPosition + away * PostSearchRadius
			: fromTarget > ReserveMaxDistance ? WorldPosition - away * PostSearchRadius
			: WorldPosition;

		for ( int i = 0; i < PostSearchTries; i++ )
		{
			if ( RandomPointNear( center, PostSearchRadius ) is not Vector3 point )
				continue;

			float distance = FlatDistance( point, target );

			if ( distance < ReserveMinDistance || distance > ReserveMaxDistance || director.IsPostTaken( this, point, PostSpacing ) )
				continue;

			post = point;
			return true;
		}

		// nothing fit. Outside the band he still takes the step back toward it; inside it, he stays put
		post = center;
		return center != WorldPosition;
	}

	/// <summary>A random spot within <paramref name="radius"/> of <paramref name="center"/> — on the navmesh when there is one.</summary>
	Vector3? RandomPointNear( Vector3 center, float radius )
	{
		if ( NavMeshReady( out var nav ) )
			return nav.GetRandomPoint( center, radius );

		// no navmesh: anywhere at his own floor height. Walked in a straight line, so expect the odd snag
		var offset = Rotation.FromYaw( Game.Random.Float( 0f, 360f ) ).Forward * Game.Random.Float( 0f, radius );
		return center.WithZ( WorldPosition.z ) + offset;
	}

	/// <summary>
	/// Heads for <paramref name="destination"/> like MoveTowards, but around the level rather than through it:
	/// once it's more than DirectDistance away he follows a navmesh path, re-planned as the destination moves.
	/// Still steered through the rigidbody, so a punch knocks him off his route like anywhere else.
	/// </summary>
	void Navigate( Vector3 destination, float arriveDistance, float pace )
	{
		var waypoint = NextWaypoint( destination, out bool lastLeg );

		// corners are passed through at speed; only the end of the route is somewhere to stop
		MoveTowards( waypoint, lastLeg ? arriveDistance : 0f, pace );
	}

	Vector3 NextWaypoint( Vector3 destination, out bool lastLeg )
	{
		lastLeg = true;

		// close by, or nothing to path on: straight at it. Pathing across open floor only makes a man zigzag
		// between polygon corners
		if ( !NavMeshReady( out var nav ) || FlatDistance( destination, WorldPosition ) <= DirectDistance )
		{
			path.Clear();
			return destination;
		}

		if ( path.Count == 0 || timeSincePath > PathRefreshTime || ( destination - pathGoal ).Length > RepathDistance )
			Plan( nav, destination );

		while ( pathIndex < path.Count - 1 && FlatDistance( path[pathIndex], WorldPosition ) <= CornerRadius )
			pathIndex++;

		lastLeg = pathIndex >= path.Count - 1;
		return path[Math.Min( pathIndex, path.Count - 1 )];
	}

	void Plan( NavMesh nav, Vector3 destination )
	{
		path.Clear();
		pathIndex = 0;
		pathGoal = destination;

		// a little random slack, so a whole crowd doesn't re-plan on the same tick
		timeSincePath = -Game.Random.Float( 0f, PathRefreshTime * 0.5f );

		var planned = nav.CalculatePath( new CalculatePathRequest { Start = WorldPosition, Target = destination } );

		if ( planned.IsValid )
		{
			foreach ( var point in planned.Points )
				path.Add( point.Position );
		}

		// no route (off the mesh, or cut off from it): walk straight at it and try again on the next re-plan
		if ( path.Count == 0 )
			path.Add( destination );

		// the first point is where he already stands
		if ( path.Count > 1 )
			pathIndex = 1;
	}

	bool NavMeshReady( out NavMesh nav )
	{
		nav = Scene.NavMesh;
		return nav is not null && nav.IsEnabled && !nav.IsGenerating;
	}

	static float FlatDistance( Vector3 a, Vector3 b ) => ( a - b ).WithZ( 0 ).Length;

	float RollBandDistance()
	{
		float near = MathF.Min( BandMin, BandMax );
		float far = MathF.Max( BandMin, BandMax );

		return Game.Random.Float( near, far );
	}

	/// <summary>
	/// Half the widest side of the hull in world units, read off the collider itself so the crowd's
	/// spacing follows the model whatever it's scaled to. Falls back to a citizen's rough half-width.
	/// </summary>
	float MeasureBodyRadius()
	{
		float scale = Hull.IsValid() ? Hull.GameObject.WorldScale.x : GameObject.WorldScale.x;

		return Hull switch
		{
			BoxCollider box => MathF.Max( box.Scale.x, box.Scale.y ) * 0.5f * scale,
			CapsuleCollider capsule => capsule.Radius * scale,
			SphereCollider sphere => sphere.Radius * scale,
			_ => 24f * scale
		};
	}

	void Brake()
	{
		float t = 1f - MathF.Exp( -Acceleration * Time.Delta );
		var velocity = Body.Velocity;

		Body.Velocity = velocity.WithZ( 0 ).LerpTo( Vector3.Zero, t ).WithZ( velocity.z );
	}

	void FaceInstantly( Vector3 direction )
	{
		direction = direction.WithZ( 0 );

		if ( direction.IsNearlyZero() )
			return;

		WorldRotation = Rotation.LookAt( direction.Normal, Vector3.Up );

		if ( Body.IsValid() )
			Body.AngularVelocity = Vector3.Zero;
	}

	void FaceTarget( Vector3 toTarget )
	{
		if ( toTarget.IsNearlyZero() )
			return;

		var wanted = Rotation.LookAt( toTarget.Normal, Vector3.Up );
		WorldRotation = Rotation.Slerp( WorldRotation, wanted, TurnSpeed * Time.Delta );
	}

	void BeginSwing()
	{
		Enter( EnemyState.Windup );
		timeSinceAttack = 0;
		swingPending = true;
		strikeArmed = false;
		SwingNumber++;

		CurrentAttack = ChooseAttack( DistanceToTarget );

		if ( CurrentAttack == EnemyAttack.Kick )
			timeSinceKick = 0;

		// the animation is the telegraph — it starts now and the damage lands on its hit frame,
		// so what you dodge is what you can see. The slot first, so it's in place on the frame the
		// trigger is read
		Renderer?.Set( "attack_type", SlotOf( CurrentAttack ) );
		Renderer?.Set( "b_attack", true );

		PlaySound( CurrentAttack switch
		{
			EnemyAttack.Jab => JabSwingSound,
			EnemyAttack.Kick => KickSwingSound,
			_ => PunchSwingSound,
		}, WorldPosition );

		if ( LogState )
			Log.Info( $"[Enemy] {GameObject.Name} throws a {CurrentAttack.ToString().ToLower()}" );
	}

	/// <summary>
	/// Which move to throw from here. Every one that fits the distance goes in the hat, weighted by how
	/// well it fits and how often he likes it, and AttackVariance shakes the hat — so standing at kicking
	/// distance usually gets you kicked, but not every time.
	/// </summary>
	EnemyAttack ChooseAttack( float distance )
	{
		EnemyAttack best = EnemyAttack.Punch;
		float bestScore = float.MinValue;

		foreach ( var attack in new[] { EnemyAttack.Jab, EnemyAttack.Punch, EnemyAttack.Kick } )
		{
			var stats = StatsFor( attack );

			if ( stats.Weight <= 0f )
				continue;

			// the special stays special: off the table entirely until its cooldown is up
			if ( attack == EnemyAttack.Kick && timeSinceKick < KickCooldown )
				continue;

			// outside its band it's still possible, just unlikely — the lunge covers a fair bit of ground,
			// and a man who never throws anything from an inch outside its range reads as a robot
			bool fits = distance >= stats.MinDistance && distance <= stats.MaxDistance;
			float score = stats.Weight * ( fits ? 1f : 0.2f );

			score *= 1f + Game.Random.Float( -AttackVariance, AttackVariance );

			if ( score <= bestScore )
				continue;

			bestScore = score;
			best = attack;
		}

		return best;
	}

	/// <summary>
	/// Locks in the direction he's facing and throws him along it, far enough to reach where you are now
	/// — not where you'll be, which is what leaves sidestepping as the answer.
	/// </summary>
	void BeginLunge( float distance )
	{
		float reach = ( distance - LungeStopDistance ).Clamp( 0f, StatsFor( CurrentAttack ).Lunge );
		var direction = WorldRotation.Forward.WithZ( 0 ).Normal;

		lungeVelocity = direction * reach / MathF.Max( LungeTime, 0.01f );
		Enter( EnemyState.Lunge );
	}

	/// <summary>
	/// Resolved the same way the player's attacks are, so both sides obey the same rules. Checked every
	/// tick of the lunge, so it only counts something that can be hurt — a wall or a loose crate in the
	/// way doesn't use up the punch.
	/// </summary>
	bool Strike()
	{
		var origin = WorldPosition + Vector3.Up * AttackHeight;
		var forward = WorldRotation.Forward;

		var trace = Scene.Trace
			.Sphere( AttackRadius, origin, origin + forward * StatsFor( CurrentAttack ).Range )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

		if ( !trace.Hit || !trace.GameObject.IsValid() )
			return false;

		var hit = trace.GameObject;
		var health = hit.Components.Get<Health>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );

		if ( !health.IsValid() )
			return false;

		// asked before anything else happens: a guard up and facing this way stops the whole blow, so a
		// blocked swing costs him his turn and cannot even shove you
		var guard = hit.Components.Get<PlayerBlock>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );

		var blow = StatsFor( CurrentAttack );

		if ( guard.IsValid() && guard.TryBlock( GameObject, WorldPosition, blow.ParryWindow, out bool parried ) )
		{
			if ( parried )
			{
				// stopped dead where the guard met him, the lunge and the swing both gone
				Body.Velocity = Body.Velocity.WithX( 0 ).WithY( 0 );
				PlaySound( ParriedSound, WorldPosition );
				Enter( EnemyState.Parried );
				return true;
			}

			// a blocked kick still puts you on the back foot: the guard saves you the damage, never the ground
			if ( CurrentAttack == EnemyAttack.Kick && KickBlockPush > 0f )
			{
				var away = ( forward.WithZ( 0 ).Normal + Vector3.Up * KickLift ).Normal;
				var shovedPlayer = hit.Components.Get<RBPlayerMovement>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );

				if ( shovedPlayer.IsValid() )
				{
					shovedPlayer.Knockback( away * KickBlockPush );
				}
				else
				{
					var shoved = hit.Components.Get<Rigidbody>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );
					shoved?.ApplyImpulse( away * KickBlockPush * ( shoved.PhysicsBody?.Mass ?? 1f ) );
				}
			}

			if ( LogState )
				Log.Info( $"[Enemy] {GameObject.Name}'s {CurrentAttack.ToString().ToLower()} was blocked" );

			PlaySound( BlockedSound, trace.HitPosition );
			Scene.GetSystem<HitStop>().Freeze( BlockedHitStopTime, HitStopScale );
			CameraMovement.Find( hit )?.Shake( HitShake * 0.5f );
			return true;
		}

		var body = hit.Components.Get<Rigidbody>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );
		var player = hit.Components.Get<RBPlayerMovement>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );
		var direction = ( forward + Vector3.Up * blow.Lift ).Normal;

		// the player's movement brakes on the ground every tick, which would eat a plain impulse in a few frames —
		// so the player is handed the velocity and left to fly. Anyone else just takes the impulse
		if ( player.IsValid() )
			player.Knockback( direction * blow.Knockback );
		else if ( body.IsValid() )
			body.ApplyImpulseAt( trace.HitPosition, direction * blow.Knockback * ( body.PhysicsBody?.Mass ?? 1f ) );

		health.TakeDamage( blow.Damage, GameObject, trace.HitPosition, direction );

		PlaySound( CurrentAttack switch
		{
			EnemyAttack.Jab => JabHitSound,
			EnemyAttack.Kick => KickHitSound,
			_ => PunchHitSound,
		}, trace.HitPosition );
		Scene.GetSystem<HitStop>().Freeze( HitStopTime, HitStopScale );

		// only the player's own camera: a friend clipped by a stray swing has no view to shake
		if ( player.IsValid() )
			CameraMovement.Find( hit )?.Shake( HitShake );

		if ( LogState )
			Log.Info( $"[Enemy] {GameObject.Name} hit {hit.Name} with the {CurrentAttack.ToString().ToLower()} for {blow.Damage:0.#}" );

		return true;
	}

	/// <summary>This swing's telegraph, with his own variance on it — the number the lunge and the hit frame hang off.</summary>
	float Windup => StatsFor( CurrentAttack ).Windup * windupScale;

	int SlotOf( EnemyAttack attack ) => attack switch
	{
		EnemyAttack.Jab => JabSlot,
		EnemyAttack.Kick => KickSlot,
		_ => PunchSlot,
	};

	readonly record struct AttackStats( float Damage, float Range, float Windup, float Knockback, float Lift, float Lunge, float ParryWindow, float MinDistance, float MaxDistance, float Weight );

	AttackStats StatsFor( EnemyAttack attack ) => attack switch
	{
		EnemyAttack.Jab => new AttackStats( JabDamage, JabRange, JabWindup, JabKnockback, JabLift, JabLunge, JabParryWindow, JabMinDistance, JabMaxDistance, JabWeight ),
		EnemyAttack.Kick => new AttackStats( KickDamage, KickRange, KickWindup, KickKnockback, KickLift, KickLunge, KickParryWindow, KickMinDistance, KickMaxDistance, KickWeight ),
		_ => new AttackStats( PunchDamage, PunchRange, PunchWindup, PunchKnockback, PunchLift, PunchLunge, PunchParryWindow, PunchMinDistance, PunchMaxDistance, PunchWeight ),
	};

	/// <summary>An empty slot is just silence, so sounds can be filled in one at a time while trying them out.</summary>
	static void PlaySound( SoundEvent sound, Vector3 position )
	{
		if ( sound is not null )
			Sfx.Play( sound, position );
	}

	void OnStaggered()
	{
		// a body can still be knocked about — a bowled man landing on it, a bottle — and a stagger must never
		// pull it out of Dead, or it gets back up when the stagger wears off
		if ( State == EnemyState.Dead )
			return;

		PlaySound( StaggerSound, WorldPosition );
		Enter( EnemyState.Staggered );

		// fists come down. There's no stagger animation on the citizen, so dropping out of the
		// punch holdtype is the cheapest honest tell that he's stopped fighting
		if ( Animator.IsValid() )
			Animator.HoldType = CitizenAnimationHelper.HoldTypes.None;
	}

	void OnRecovered()
	{
		if ( State == EnemyState.Dead )
			return;

		PlaySound( RecoverSound, WorldPosition );

		if ( Animator.IsValid() )
			Animator.HoldType = CitizenAnimationHelper.HoldTypes.Punch;

		EnterWaiting();
	}

	void OnHurt( float amount, GameObject attacker )
	{
		if ( State == EnemyState.Dead )
			return;

		// kicked: turned square to whoever did it on the spot, down or not. The knockback clip is a man
		// flying backwards away from his attacker, and it only reads that way facing him
		if ( Hp.LastHitKind == HitKind.Launch && attacker.IsValid() )
			FaceInstantly( attacker.WorldPosition - WorldPosition );

		if ( State == EnemyState.Staggered )
			return;

		PlaySound( HurtSound, WorldPosition );

		// interrupts a windup, so landing a punch first genuinely beats them to the swing
		Enter( EnemyState.Stunned );
	}

	void OnDeath()
	{
		PlaySound( DeathSound, WorldPosition );
		Enter( EnemyState.Dead );

		// the killing blow's impulse is already sitting in the rigidbody's velocity. Hand it to the
		// ragdoll before switching over, or a kick that kills someone drops them straight down
		// instead of launching them, which is the exact opposite of what a kick should do
		var velocity = Body.IsValid() ? Body.Velocity : Vector3.Zero;

		if ( RagdollOnDeath && Renderer.IsValid() )
		{
			// created on whatever object actually holds the citizen, which is usually a child — the
			// ragdoll's bodies are built around the renderer, so it has to live alongside it
			ragdoll = Renderer.GameObject.Components.GetOrCreate<ModelPhysics>();
			ragdoll.Renderer = Renderer;
			ragdoll.Model = Renderer.Model;
			ragdoll.Enabled = true;

			// so the collision matrix can tell a body on the floor from a man standing up — a "corpse" /
			// "player" rule set to Ignore lets you walk through a pile instead of wading into it
			Renderer.GameObject.Tags.Add( "corpse" );

			pendingLaunch = velocity;
			ApplyPendingLaunch();
		}

		if ( Hull.IsValid() )
			Hull.Enabled = false;

		if ( Body.IsValid() )
			Body.Enabled = false;
	}

	/// <summary>
	/// Hands the corpse the velocity it died with. The ragdoll's bodies don't exist the instant
	/// ModelPhysics is switched on, so this is retried each tick until they do rather than assumed
	/// to work first time — miss the window and a kick that kills just drops them on the spot.
	/// </summary>
	void ApplyPendingLaunch()
	{
		if ( !pendingLaunch.HasValue )
			return;

		if ( ragdoll?.Bodies is not { Count: > 0 } bodies )
			return;

		foreach ( var body in bodies )
		{
			if ( body.Component.IsValid() )
				body.Component.Velocity = pendingLaunch.Value;
		}

		pendingLaunch = null;
	}

	void UpdateAnimation()
	{
		// ahead of the guard: on a custom rig there's no citizen helper, and the throw's parameters
		// still have to reach its animgraph
		AnimateThrow();

		if ( !Animator.IsValid() )
			return;

		Animator.IsGrounded = true;
		Animator.WithVelocity( Body.IsValid() ? Body.Velocity : Vector3.Zero );

		if ( Target.IsValid() )
			Animator.WithLook( Target.WorldPosition - WorldPosition );
	}
}
