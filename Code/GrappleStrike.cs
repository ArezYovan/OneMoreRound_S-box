using Sandbox;
using System;

// A grapple-execute ability (press F). The hook shoots out along your aim:
//  • Hits nothing  → snaps back, ability goes on cooldown.
//  • Hits a target → freezes them in place, yanks YOU to them fast, then on arrival
//       damages them (killing if low enough) and launches you back and up off them.
// Targets both AI enemies (Enemy + Health, pinned directly — they're local objects) and
// OTHER PLAYERS (PlayerHealth, pinned via an owner-side RPC so it works networked).
// Runs on a cooldown shown by the HUD ability box.
public sealed class GrappleStrike : Component
{
	[Header( "Input" )]
	// Bound to F by default (the "Flashlight" action in Input.config). Not "Use" — E is grab and
	// throw in this game, and one key doing both would hook whatever you reached for.
	[Property] public string InputAction { get; set; } = "Flashlight";

	[Header( "Reach" )]
	[Property] public float MaxDistance { get; set; } = 1600f;
	// Aim-assist cone (degrees from your look direction). An enemy anywhere inside this
	// cone counts as "aimed at" — you don't need a pinpoint crosshair hit, just close.
	// Bigger = more forgiving / stickier targeting.
	[Property] public float AimConeAngle { get; set; } = 14f;
	// Require a clear path to the enemy (can't hook through walls).
	[Property] public bool RequireLineOfSight { get; set; } = true;
	// How fast the hook tip flies out (u/s).
	[Property] public float ShootSpeed { get; set; } = 9000f;
	// How fast it snaps back on a miss (u/s).
	[Property] public float RetractSpeed { get; set; } = 14000f;
	// Optional origin for the hook line (a hand/muzzle). Falls back to the camera.
	[Property] public GameObject HookOrigin { get; set; }

	[Header( "Reel" )]
	// How fast you're pulled to a hooked enemy (u/s).
	[Property] public float ReelSpeed { get; set; } = 3000f;
	// How close to the enemy counts as "arrived" and triggers the hit.
	[Property] public float ArriveDistance { get; set; } = 110f;

	[Header( "Damage & Knockback" )]
	[Property] public float StrikeDamage { get; set; } = 50f;
	// How hard you're thrown back and up off the enemy on every strike (kill or not).
	[Property] public float KnockbackBack { get; set; } = 700f;
	[Property] public float KnockbackUp { get; set; } = 550f;

	[Header( "Player Strikes" )]
	// How long a hooked PLAYER stays pinned while you fly in. Generous on purpose — it's
	// released the moment you arrive; this is just the safety net if the reel is cut short.
	[Property] public float PlayerFreezeTime { get; set; } = 2f;
	// Where on the player the hook aims, above their root (roughly chest height).
	[Property] public float PlayerAimHeight { get; set; } = 25f;

	[Header( "Cooldown" )]
	[Property] public float Cooldown { get; set; } = 3f;

	[Header( "Visual" )]
	[Property] public Color LineColor { get; set; } = new Color( 0.5f, 0.9f, 1f, 1f );
	[Property] public float LineWidth { get; set; } = 1.5f;
	// FOV pushed while the strike is active (re-requested per frame, eases back on its own).
	[Property] public float StrikeFov { get; set; } = 115f;
	[Property] public CameraMovement cameraMovement { get; set; }

	[Header( "FX (optional)" )]
	[Property] public SoundEvent FireSound { get; set; }
	[Property] public SoundEvent HitSound { get; set; }
	[Property] public SoundEvent KillSound { get; set; }
	[Property] public SoundEvent MissSound { get; set; }

	// --- Public state for HUD / movement ---
	public bool IsReady => state == StrikeState.Idle && cooldownTimer <= 0f;
	// 1 right after use → 0 when ready again (drives the HUD cooldown drain).
	public float CooldownFraction => Cooldown > 0f ? MathX.Clamp( cooldownTimer / Cooldown, 0f, 1f ) : 0f;
	// True while the ability owns the player's velocity (reeling / dashing this frame).
	public bool IsControllingPlayer => state == StrikeState.Reeling;

	private enum StrikeState { Idle, Shooting, Reeling, Retracting }
	private StrikeState state = StrikeState.Idle;

	private float cooldownTimer;

	private Rigidbody rb;
	private RBPlayerMovement playerMovement;

	// Hook flight.
	private Vector3 origin;
	private Vector3 tipPos;
	private Vector3 missPoint;       // where the tip flies to on a miss
	private bool hookHitsEnemy;

	// Hooked-target bookkeeping. Exactly ONE of targetEnemy/targetPlayer is set while hooked.
	private Health targetEnemy;
	private PlayerHealth targetPlayer;
	private RBPlayerMovement targetMovement;
	private GameObject targetObject;
	private Rigidbody targetRb;
	private Enemy targetAI;
	private Vector3 frozenPos;
	private Vector3 reelDir;

	// Line visual.
	private LineRenderer line;
	private GameObject lineStart;
	private GameObject lineEnd;

	protected override void OnAwake()
	{
		rb = GameObject.GetComponent<Rigidbody>();
		playerMovement = GameObject.GetComponent<RBPlayerMovement>();
	}

	protected override void OnStart()
	{
		// Build a dedicated line for the hook so we don't clash with the GrappleGun's rope.
		var lineGo = new GameObject( true, "StrikeLine" );
		lineGo.SetParent( GameObject );
		lineStart = new GameObject( true, "StrikeLineStart" );
		lineStart.SetParent( lineGo );
		lineEnd = new GameObject( true, "StrikeLineEnd" );
		lineEnd.SetParent( lineGo );

		line = lineGo.Components.Create<LineRenderer>();
		line.Points = new() { lineStart, lineEnd };
		line.Width = LineWidth;
		line.Color = LineColor;
		line.Enabled = false;

		cameraMovement ??= CameraMovement.Find( GameObject );
	}

	protected override void OnUpdate()
	{
		if ( IsProxy )
			return;

		if ( cooldownTimer > 0f )
			cooldownTimer -= Time.Delta;

		// No striking while dead or frozen (respawn wait, round countdown, pinned).
		bool blocked = playerMovement is not null && playerMovement.ActionsBlocked;
		if ( IsReady && Input.Pressed( InputAction ) && !blocked )
			Activate();

		// Keep the line drawn from the origin to the current tip while the hook is out.
		bool showLine = state == StrikeState.Shooting || state == StrikeState.Reeling || state == StrikeState.Retracting;
		if ( line is not null )
		{
			line.Enabled = showLine;
			if ( showLine )
			{
				lineStart.WorldPosition = CurrentOrigin();
				lineEnd.WorldPosition = tipPos;
			}
		}

		// Push the FOV while the ability is active — re-requested each frame, so it eases
		// back to the base FOV on its own once the strike finishes (same as the others).
		if ( showLine )
			cameraMovement?.RequestFov( StrikeFov );
	}

	protected override void OnFixedUpdate()
	{
		if ( IsProxy )
			return;

		// Dying or getting frozen mid-strike cancels it cleanly — EndStrike also releases
		// anyone we had pinned, so a dead striker can't leave the enemy stuck.
		if ( state != StrikeState.Idle && playerMovement is not null && playerMovement.ActionsBlocked )
		{
			EndStrike();
			return;
		}

		switch ( state )
		{
			case StrikeState.Shooting: TickShooting(); break;
			case StrikeState.Reeling: TickReeling(); break;
			case StrikeState.Retracting: TickRetracting(); break;
		}
	}

	// Move `from` toward `to` by at most `maxDelta` units (s&box has no Vector3.MoveTowards).
	private static Vector3 MoveTowards( Vector3 from, Vector3 to, float maxDelta )
	{
		Vector3 delta = to - from;
		float dist = delta.Length;
		if ( dist <= maxDelta || dist < 0.001f )
			return to;
		return from + delta.Normal * maxDelta;
	}

	private Vector3 CurrentOrigin()
	{
		if ( HookOrigin is not null )
			return HookOrigin.WorldPosition;
		var cam = Scene.Camera;
		return cam is not null ? cam.WorldPosition : WorldPosition;
	}

	private void Activate()
	{
		var cam = Scene.Camera;
		if ( cam is null ) return;

		// Cooldown starts the moment you use it — hit or miss.
		cooldownTimer = Cooldown;

		origin = CurrentOrigin();
		tipPos = origin;
		Vector3 dir = cam.WorldRotation.Forward;

		// Soft target: grab the best enemy OR player near where you're looking (within the
		// aim cone), not just one a pinpoint ray happens to cross.
		FindTarget( cam.WorldPosition, dir );

		if ( targetObject.IsValid() )
		{
			hookHitsEnemy = true;
		}
		else
		{
			hookHitsEnemy = false;
			// No enemy in the cone — fly straight out (stopping at a wall) and snap back.
			var tr = Scene.Trace.Ray( cam.WorldPosition, cam.WorldPosition + dir * MaxDistance )
				.IgnoreGameObjectHierarchy( GameObject )
				.Run();
			missPoint = tr.Hit ? tr.HitPosition : tr.EndPosition;
		}

		state = StrikeState.Shooting;

		if ( FireSound is not null )
			Sound.Play( FireSound, origin );
	}

	// Finds the best target to strike — AI enemy or another player — in range, inside the
	// aim cone, with a clear path. "Best" = the most centered on your aim (smallest angle),
	// so it feels like it locks onto whoever you're closest to looking at.
	// Sets targetEnemy XOR targetPlayer, and targetObject to whichever won.
	private void FindTarget( Vector3 eyePos, Vector3 look )
	{
		float cosCone = MathF.Cos( AimConeAngle * MathF.PI / 180f );
		float bestDot = cosCone; // must be at least this aligned to qualify

		targetEnemy = null;
		targetPlayer = null;

		// Only the AI counts as an enemy: Health on its own also sits on the player and on the
		// furniture, and the strike was never meant to hook a barstool.
		foreach ( var ai in Scene.GetAllComponents<Enemy>() )
		{
			var enemy = ai.Components.Get<Health>();
			if ( enemy is null || enemy.IsDead ) continue;
			if ( !AimScore( eyePos, look, enemy.WorldPosition, enemy.GameObject, ref bestDot ) ) continue;

			targetEnemy = enemy;
			targetPlayer = null;
		}

		foreach ( var player in Scene.GetAllComponents<PlayerHealth>() )
		{
			if ( player is null || player.IsDead ) continue;
			if ( player.GameObject.Root == GameObject.Root ) continue; // never hook yourself
			if ( !AimScore( eyePos, look, PlayerAimPoint( player ), player.GameObject, ref bestDot ) ) continue;

			targetPlayer = player;
			targetEnemy = null;
		}

		targetObject = targetEnemy?.GameObject ?? targetPlayer?.GameObject;
	}

	// Shared cone/range/line-of-sight test. Returns true (and tightens bestDot) if this
	// point is the best-aimed valid target so far.
	private bool AimScore( Vector3 eyePos, Vector3 look, Vector3 point, GameObject target, ref float bestDot )
	{
		Vector3 to = point - eyePos;
		float dist = to.Length;
		if ( dist > MaxDistance || dist < 1f ) return false;

		// How centered on your aim this target is (1 = dead center).
		float dot = Vector3.Dot( look, to / dist );
		if ( dot < bestDot ) return false;

		if ( RequireLineOfSight && IsBlocked( eyePos, point, target ) ) return false;

		bestDot = dot;
		return true;
	}

	// True if something solid sits between the eye and the target (so we can't hook it).
	private bool IsBlocked( Vector3 from, Vector3 to, GameObject target )
	{
		var tr = Scene.Trace.Ray( from, to )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

		if ( !tr.Hit ) return false;

		// Hitting any part of the target itself still counts as a clear path.
		return tr.GameObject is null || tr.GameObject.Root != target.Root;
	}

	// Chest height on a player (their root sits at the feet).
	private Vector3 PlayerAimPoint( PlayerHealth player )
	{
		return player.WorldPosition + Vector3.Up * PlayerAimHeight;
	}

	// The live position the hook should fly at / pin to for the current target.
	private Vector3 CurrentTargetPoint()
	{
		if ( targetPlayer is not null )
			return PlayerAimPoint( targetPlayer );
		return targetObject.WorldPosition;
	}

	// Hook tip flies out toward the target (tracking them if there is one).
	private void TickShooting()
	{
		Vector3 target = (hookHitsEnemy && targetObject.IsValid())
			? CurrentTargetPoint()
			: missPoint;

		tipPos = MoveTowards( tipPos, target, ShootSpeed * Time.Delta );

		if ( Vector3.DistanceBetween( tipPos, target ) > 4f )
			return;

		// Reached the end of the throw.
		if ( hookHitsEnemy && targetObject.IsValid() )
			BeginReel();
		else
			state = StrikeState.Retracting; // miss → snap back
	}

	private void BeginReel()
	{
		if ( targetPlayer is not null )
		{
			// A hooked PLAYER is a networked proxy here — we must never write its position
			// directly (the owner's sync would fight us). Instead ask their machine to pin
			// them via RPC; the frozen result replicates back through the transform sync.
			targetMovement = targetPlayer.GameObject.GetComponent<RBPlayerMovement>();
			targetMovement?.NetFreeze( PlayerFreezeTime );
		}
		else
		{
			// AI enemy: local object — pin its position and silence its AI for the pull.
			targetRb = targetObject.Components.Get<Rigidbody>( FindMode.EverythingInSelfAndAncestors );
			targetAI = targetObject.Components.Get<Enemy>( FindMode.EverythingInSelfAndAncestors );
			if ( targetAI is not null )
				targetAI.Enabled = false;
		}

		frozenPos = CurrentTargetPoint();
		reelDir = (frozenPos - rb.WorldPosition).Normal;

		state = StrikeState.Reeling;

		if ( HitSound is not null )
			Sound.Play( HitSound, frozenPos );
	}

	private void TickReeling()
	{
		// Target died or vanished mid-pull (killed by another source, disconnected) — bail.
		bool targetGone = !targetObject.IsValid()
			|| (targetEnemy is not null && targetEnemy.IsDead)
			|| (targetPlayer is not null && targetPlayer.IsDead)
			|| (targetEnemy is null && targetPlayer is null);
		if ( targetGone )
		{
			EndStrike();
			return;
		}

		if ( targetPlayer is not null )
		{
			// Their owner is holding them still (NetFreeze); just track the replicated
			// position so tiny drift can't make us fly past them.
			frozenPos = CurrentTargetPoint();
		}
		else
		{
			// AI enemy: hold it frozen in place locally.
			targetObject.WorldPosition = frozenPos;
			if ( targetRb is not null )
				targetRb.Velocity = Vector3.Zero;
		}

		// Pin the hook tip to the target.
		tipPos = frozenPos;

		// Yank the player straight at the frozen target.
		Vector3 toTarget = frozenPos - rb.WorldPosition;
		reelDir = toTarget.Normal;
		rb.Velocity = reelDir * ReelSpeed;

		if ( toTarget.Length <= ArriveDistance )
			Impact();
	}

	private void Impact()
	{
		bool kill;

		if ( targetPlayer is not null )
		{
			// PlayerHealth only owns the knockout; the hit points live on the Health beside it
			var playerHp = targetPlayer.Components.Get<Health>();
			kill = playerHp is not null && StrikeDamage >= playerHp.CurrentHealth;
			playerHp?.TakeDamage( StrikeDamage, GameObject, frozenPos, reelDir );

			// Release the pin — unless this killed them, in which case their own respawn
			// freeze has taken over and we must not cancel it. Clear targetMovement so
			// EndStrike's safety release can't undo the respawn freeze either.
			if ( !kill )
				targetMovement?.NetFreeze( 0f );
			targetMovement = null;
		}
		else
		{
			kill = StrikeDamage >= targetEnemy.CurrentHealth;

			// Re-enable AI BEFORE applying damage: on a kill the object is destroyed anyway,
			// on a non-kill the enemy should resume behaving.
			if ( targetAI is not null )
				targetAI.Enabled = true;

			targetEnemy.TakeDamage( StrikeDamage, GameObject, frozenPos, reelDir );
		}

		// Always bounce back and up off the target — same launch whether or not the
		// strike killed them (no more dashing through low-health targets).
		rb.Velocity = -reelDir * KnockbackBack + Vector3.Up * KnockbackUp;

		if ( kill && KillSound is not null )
			Sound.Play( KillSound, frozenPos );

		EndStrike();
	}

	// Miss: tip snaps back to the origin, then we're done.
	private void TickRetracting()
	{
		tipPos = MoveTowards( tipPos, CurrentOrigin(), RetractSpeed * Time.Delta );

		if ( Vector3.DistanceBetween( tipPos, CurrentOrigin() ) <= 8f )
		{
			if ( MissSound is not null )
				Sound.Play( MissSound, CurrentOrigin() );
			EndStrike();
		}
	}

	private void EndStrike()
	{
		// Safety: never leave a target frozen if we bailed out unexpectedly.
		if ( targetAI is not null && targetAI.IsValid() )
			targetAI.Enabled = true;

		// Release a still-LIVING pinned player (bailed mid-pull). A dead one is left
		// alone — their respawn freeze owns them now.
		if ( targetMovement.IsValid() && targetPlayer.IsValid() && !targetPlayer.IsDead )
			targetMovement.NetFreeze( 0f );

		targetEnemy = null;
		targetPlayer = null;
		targetMovement = null;
		targetObject = null;
		targetRb = null;
		targetAI = null;
		state = StrikeState.Idle;
	}
}
