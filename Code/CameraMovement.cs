using Sandbox;
using System;
using System.Linq;

public sealed class CameraMovement : Component
{
	// The FOV the camera rests at when nothing (slide/wallrun/grapple) is pushing it.
	[Property] public float BaseFov { get; set; } = 100f;
	[Property] public float FOVspeed { get; set; } = 8f;
	[Property] public float tiltSpeed { get; set; } = 8f;

	[Header( "Landing Impact" )]
	// Falls slower than this don't register an impact at all.
	[Property] public float MinLandingSpeed { get; set; } = 300f;
	// At/above this fall speed you get the full-strength impact.
	[Property] public float MaxLandingSpeed { get; set; } = 1500f;
	// Downward velocity kick fed into the spring on a max-strength landing (the "punch").
	[Property] public float LandingKick { get; set; } = 300f;
	// How much the view pitches down as it dips (degrees per unit of dip).
	[Property] public float LandingPitchFactor { get; set; } = 0.5f;
	// Spring stiffness — higher = snappier, faster recovery.
	[Property] public float LandingStiffness { get; set; } = 200f;
	// Spring damping — lower = more bounce/overshoot, higher = a dead thud.
	[Property] public float LandingDamping { get; set; } = 10f;

	[Header( "Head Bob" )]
	// Vertical bob height (units) while running. 0 disables.
	[Property] public float BobAmplitude { get; set; } = 0f;
	// Sideways sway as a fraction of the vertical bob.
	[Property] public float BobHorizontal { get; set; } = 0.5f;
	// Step rate at BobReferenceSpeed (cycles/sec). Speeds up as you move faster.
	[Property] public float BobFrequency { get; set; } = 1.6f;
	// Ground speed that maps to the base BobFrequency.
	[Property] public float BobReferenceSpeed { get; set; } = 300f;
	// How fast the bob fades in/out when you start/stop moving.
	[Property] public float BobFade { get; set; } = 8f;

	[Header( "Strafe Tilt" )]
	// How far the camera rolls when strafing (degrees). 0 to disable.
	[Property] public float StrafeTiltAmount { get; set; } = 2f;
	// How quickly the tilt follows the strafe input. Higher = snappier.
	[Property] public float StrafeTiltSpeed { get; set; } = 8f;

	[Header( "Weapon Recoil" )]
	// How quickly a queued recoil kick is fed into the aim. Higher = sharper snap per shot.
	[Property] public float RecoilSnappiness { get; set; } = 20f;

	[Header( "Hit Shake" )]
	// Master volume for every shake. 0 turns them all off, 2 doubles them — the one knob to reach for
	// before touching each attack's own amount.
	[Property] public float ShakeStrength { get; set; } = 1f;
	// Degrees the view can twist at a full-strength shake. Kept small: this is a jolt, not an earthquake.
	[Property] public float ShakeMaxAngle { get; set; } = 2.5f;
	// How fast a shake dies away, in full shakes per second.
	[Property] public float ShakeDecay { get; set; } = 4f;
	// How fast the shake jitters. Higher is a sharper buzz, lower a heavier wobble.
	[Property] public float ShakeFrequency { get; set; } = 30f;

	[Header( "Winded Sway" )]
	// Degrees the view drifts at its fullest — a slow, heavy sway, like catching your breath. It starts once
	// stamina drops below the Stamina component's Low Fraction, grows as the bar empties, and is all there while
	// you're winded. Purely visual: it's layered on the camera like the shake, so aim and punches never feel it.
	// 0 turns it off.
	[Property] public float WindedSway { get; set; } = 1.2f;
	// Breaths per second of the sway. Low reads as heavy and tired; high as dizzy.
	[Property] public float WindedSwaySpeed { get; set; } = 0.55f;
	// How fast it follows your stamina — in as you tire, back out as your wind returns.
	[Property] public float WindedSwayFade { get; set; } = 3f;

	// Recoil queued up by weapons, consumed smoothly over the next few frames.
	private Angles pendingRecoil;

	private Stamina staminaComponent;
	private float swayWeight;
	private float lastSwayTime;

	// 0–1 "trauma": added by hits, drained by ShakeDecay, and felt as its square so small bumps stay subtle
	private float shakeTrauma;
	private float lastShakeTime;
	private float currentTilt;

	// Per-frame camera-effect requests. Systems (slide/wallrun/grapple) re-request every
	// frame they're active; when nobody asks, FOV eases back to BaseFov and tilt to 0.
	// One consumer, many requesters — no more systems lerp-fighting each other.
	private float requestedFov;
	// Per-request FOV lerp speed. <0 means "use the global FOVspeed" — lets a single ability
	// (e.g. Dash) punch its FOV faster than the smooth default the others ride.
	private float requestedFovSpeed = -1f;
	private float requestedTilt;
	private float currentEffectTilt;

	// Head bob state: phase advances with ground speed, weight fades in/out so the bob
	// never pops when starting, stopping, jumping, or sliding.
	private float bobTimer;
	private float bobWeight;
	private Sliding slidingComponent;
	private LockOn lockOnComponent;

	// A fixed viewpoint that has taken the camera over — the bar between rounds. While one is set the mouse is
	// ignored and the camera eases across to it, leaning a little toward the cursor. The transform is kept as
	// well as the object, so letting go can ease back out of a spot that has already gone.
	private GameObject viewSpot;
	private Transform viewSpotTransform;
	private Vector2 viewLean;
	private float viewBlendSpeed = 6f;
	private float viewBlend;
	private Angles viewLeanAngles;

	private CameraComponent Camera;
	private Angles eyeAngles;
	// eyeAngles used to be rebuilt from Head's transform every frame; it now carries itself, so it needs
	// seeding once from whatever Head was pointing at when we first found it.
	private bool eyeAnglesInitialised;

	// Handed from OnUpdate to OnPreRender, where the camera is actually placed.
	private Angles viewAngles;
	private Vector3 viewBob;
	private bool viewReady;

	/// <summary>
	/// Where the player is actually aiming. Deliberately NOT what the camera is pointing at —
	/// the landing dip, strafe tilt and head bob are layered on the camera afterwards and none
	/// of them move this. Combat and grabbing read it so a punch goes where you looked, not
	/// where the camera happened to be bouncing.
	/// </summary>
	public Angles EyeAngles => eyeAngles;
	private bool wasGrounded;

	// Landing spring state: a vertical camera offset that dips and bounces back to 0.
	private float landingOffset;
	private float landingVel;
	private float lastAirSpeedDown;

	[Property] public RBPlayerMovement Player { get; set; }

	/// <summary>
	/// The model's head bone. Its position places the camera; its rotation only reaches it as what it does beyond the aim (see RotationInfluence). Null keeps the camera
	/// on the plain Head object like before. Point it at a bone object (tick "create bone objects" on the
	/// SkinnedModelRenderer) and the head motion authored in Blender comes through as sway.
	///
	/// This must NOT be the same object as the player's Head. Head is where the mouse aim accumulates, and the
	/// animation re-poses a bone every frame - pointing Head at one throws the aim away each frame.
	/// </summary>
	[Property] public GameObject EyeBone { get; set; }

	/// <summary>
	/// How much of the bone's motion reaches the camera. 0 is the old fixed eye position, 1 is fully
	/// animation-driven. Blender head motion is nearly always too strong at 1 - tune it while playing.
	/// </summary>
	[Property, Range( 0f, 1f )] public float BoneInfluence { get; set; } = 1f;

	/// <summary>
	/// How much of the bone's ROTATION reaches the camera, so a head snap in the hook or a head tilt in the slide
	/// turns the view. Taken as what the head does beyond the aim (see HeadRotation), never its raw rotation, so rig axes can't
	/// tilt the camera. Purely visual - aim, lock-on and attacks all read EyeAngles and never see it.
	/// </summary>
	[Property, Range( 0f, 1f )] public float RotationInfluence { get; set; } = 0.5f;

	/// <summary>Hard cap on how far the head can turn the camera, in degrees, whatever the animation does.</summary>
	[Property] public float MaxHeadRotation { get; set; } = 30f;

	/// <summary>
	/// The attachment the Look At node aims - the same one AimAnimation uses. The look-at already turns it down the
	/// aim, so only the difference between the two reaches the camera; without it the look-at's own turn would be
	/// counted a second time on top of the mouse. Falls back to the bone's rest pose if the model hasn't got it.
	/// </summary>
	[Property] public string EyeAttachment { get; set; } = "eyes";

	private GameObject Orientation { get; set; }
	private GameObject Head { get; set; }

	// EyeBone's renderer and rest pose in model space, cached per assigned bone (see ResolveEyeRig).
	private GameObject eyeRestBone;
	private SkinnedModelRenderer eyeRenderer;
	private Rotation eyeRestRotation;
	private bool eyeRestReady;

	/// <summary>
	/// The camera that belongs to <paramref name="player"/>, for abilities to push FOV and tilt through.
	/// Looks inside the player's own hierarchy first — with several players each carries a camera in
	/// their prefab, and the first one in the scene could be somebody else's. Only if there's none there
	/// does it take the scene's local camera, which is where a level's camera usually lives: on its own
	/// object, where searching the player alone never found it and every FOV kick silently did nothing.
	/// </summary>
	public static CameraMovement Find( GameObject player )
	{
		if ( !player.IsValid() || player.Scene is null )
			return null;

		return player.Root.Components.Get<CameraMovement>( FindMode.EverythingInSelfAndDescendants )
			?? player.Scene.GetAllComponents<CameraMovement>().FirstOrDefault( c => !c.IsProxy );
	}

	protected override void OnAwake()
	{
		Camera = Components.Get<CameraComponent>();
		ResolvePlayer();
	}

	// The editor-assigned Player reference can silently break when objects move between
	// prefabs (a stale GUID deserializes as null) — so resolve it at runtime instead of
	// trusting it. Prefer the movement component on our own player root (camera lives
	// inside the player prefab); as a fallback for a scene-placed camera, find the LOCAL
	// player in the scene — never a remote proxy.
	private void ResolvePlayer()
	{
		Player ??= GameObject.Root.Components.Get<RBPlayerMovement>( FindMode.EverythingInSelfAndDescendants );
		Player ??= Scene.GetAllComponents<RBPlayerMovement>().FirstOrDefault( p => !p.IsProxy );

		if ( Player is null )
			return;

		Orientation = Player.Orientation;
		Head = Player.Head;

		if ( !eyeAnglesInitialised && Head.IsValid() )
		{
			var start = Head.WorldRotation.Angles();
			eyeAngles = new Angles( start.pitch, start.yaw, 0f );
			eyeAnglesInitialised = true;
		}
		slidingComponent = Player.GameObject.GetComponent<Sliding>();
		lockOnComponent = Player.GameObject.GetComponent<LockOn>();
		staminaComponent = Player.GameObject.Components.Get<Stamina>( FindMode.EverythingInSelfAndDescendants );
	}

	protected override void OnUpdate()
	{
		// Networked players spawn AFTER the scene loads, so a scene-placed camera's OnAwake
		// can run before any player exists — keep trying until ours does.
		if ( Player is null || Head is null )
		{
			ResolvePlayer();
			if ( Player is null || Head is null )
				return;
		}

		// Only the local player's camera should run and render. For everyone else this is a
		// remote player's camera — turn it off so it can't steal the view or audio listener
		// (Scene.Camera then resolves to OUR own camera on each client). Gate on the root
		// player's network state since this component lives on a child of the player.
		bool isProxy = Player.IsProxy;
		if ( isProxy )
		{
			if ( Camera is not null && Camera.Enabled )
				Camera.Enabled = false;
			viewReady = false;
			return;
		}

		UpdateLanding();

		// eyeAngles is the aim and it is authoritative - it carries over from last frame, and only the mouse,
		// lock-on and recoil below move it. It used to be rebuilt from Head's transform here, which worked only
		// because nothing else wrote to Head; the moment Head pointed at a skeleton bone the animation re-posed
		// it every frame and the mouse input vanished along with it.

		// Locked on, the target owns the aim and the mouse only gets a leash round him — LockOn keeps the two apart
		// so they can't fight, which is exactly where lock-on jitter comes from. Aimed from the head rather than the
		// camera, so the landing dip and head bob layered on below can never feed back into where it looks.
		var mouseLook = new Angles( Input.MouseDelta.y * 0.1f, -Input.MouseDelta.x * 0.1f, 0f );

		if ( viewSpot.IsValid() )
		{
			// a fixed viewpoint owns the view; the player's aim simply waits where they left it
		}
		else if ( lockOnComponent is not null && lockOnComponent.IsLocked )
		{
			eyeAngles = lockOnComponent.Steer( eyeAngles, Head.WorldPosition, mouseLook );
		}
		else
		{
			eyeAngles.pitch += mouseLook.pitch;
			eyeAngles.yaw += mouseLook.yaw;
		}

		// Feed queued weapon recoil into the aim a fraction per frame, so rapid fire reads
		// as a continuous climb instead of per-shot jitter. This moves the REAL aim — the
		// player rides/compensates it, which is the loose feel we want.
		float consume = 1f - MathF.Exp( -RecoilSnappiness * Time.Delta );
		eyeAngles.pitch -= pendingRecoil.pitch * consume;
		eyeAngles.yaw += pendingRecoil.yaw * consume;
		pendingRecoil *= 1f - consume;

		eyeAngles.pitch = eyeAngles.pitch.Clamp( -89f, 89f ); //Clamp to avoid flipping
		Head.WorldRotation = eyeAngles.ToRotation(); // aim is unaffected by the landing dip

		// Strafe tilt: roll toward the movement direction. Purely visual, aim unaffected.
		// AnalogMove.y is +left/−right; negate so leaning right when strafing right.
		float tiltTarget = -Input.AnalogMove.y * StrafeTiltAmount;
		currentTilt = MathX.Lerp( currentTilt, tiltTarget, Time.Delta * StrafeTiltSpeed );

		// Consume this frame's camera-effect requests, then clear them — anything still
		// active will re-request next frame, anything that stopped eases back to rest.
		currentEffectTilt = MathX.Lerp( currentEffectTilt, requestedTilt, Time.Delta * tiltSpeed );
		requestedTilt = 0f;

		float fovTarget = requestedFov > 0f ? requestedFov : BaseFov;
		// Use the winning request's speed if one was given, otherwise the global FOVspeed.
		// (Easing back to BaseFov, with no request, always uses the global speed.)
		float fovSpeed = requestedFovSpeed > 0f ? requestedFovSpeed : FOVspeed;
		Camera.FieldOfView = MathX.Lerp( Camera.FieldOfView, fovTarget, Time.Delta * fovSpeed );
		requestedFov = 0f;
		requestedFovSpeed = -1f;

		// The camera gets the landing dip, tilts, and head bob layered on top: purely
		// cosmetic, none of it moves the player's actual aim direction.
		Angles camAngles = eyeAngles;
		camAngles.pitch = (eyeAngles.pitch + landingOffset * LandingPitchFactor).Clamp( -89f, 89f );
		camAngles.roll = eyeAngles.roll + currentTilt + currentEffectTilt;

		viewAngles = camAngles;
		viewBob = UpdateBob( camAngles.ToRotation() );

		// A fixed viewpoint takes the camera off the player entirely, eased both ways, so the cut to the bar and
		// the cut back to the fight are moves rather than jumps.
		if ( viewSpot.IsValid() )
			viewSpotTransform = viewSpot.WorldTransform;

		viewBlend = MathX.Lerp( viewBlend, viewSpot.IsValid() ? 1f : 0f, 1f - MathF.Exp( -viewBlendSpeed * Time.Delta ) );
		viewReady = true;

		Orientation.WorldRotation = Rotation.From( 0f, Head.WorldRotation.Angles().yaw, 0f );
	}

	/// <summary>
	/// The camera's final placement. It happens here rather than at the end of OnUpdate because this is the only
	/// point the eye bone can be trusted: the skeleton has been posed for this frame by now. Sampling a bone in
	/// OnUpdate reads the PREVIOUS frame's pose, which reads as a camera juddering a frame behind the animation.
	/// </summary>
	protected override void OnPreRender()
	{
		if ( !viewReady || Camera is null || !Camera.Enabled )
			return;

		var viewPosition = EyePosition() + Vector3.Up * landingOffset + viewBob;
		var viewRotation = HeadRotation() * viewAngles.ToRotation();

		if ( viewBlend > 0.001f )
		{
			viewPosition = Vector3.Lerp( viewPosition, viewSpotTransform.Position, viewBlend );
			viewRotation = Rotation.Slerp( viewRotation, viewSpotTransform.Rotation * ViewLean(), viewBlend );
		}

		Camera.WorldPosition = viewPosition;
		Camera.WorldRotation = viewRotation * WindedSwayRotation() * ShakeRotation();
	}

	/// <summary>
	/// The slow drift while you're low on wind: pitch rising and falling with each breath, and a lazier roll and yaw
	/// out of step with it, so it wanders rather than rocking on a beat. Follows the stamina's Tiredness — creeping in
	/// below its Low Fraction, full while winded — eased, and never at the bar, where the fixed view isn't you. On
	/// real time like the shake, so a hitstop doesn't freeze it mid-breath.
	/// </summary>
	private Rotation WindedSwayRotation()
	{
		float now = RealTime.Now;
		float delta = MathF.Min( now - lastSwayTime, 0.1f );
		lastSwayTime = now;

		float tired = staminaComponent.IsValid() && viewBlend < 0.5f ? staminaComponent.Tiredness : 0f;
		swayWeight = swayWeight.LerpTo( tired, 1f - MathF.Exp( -WindedSwayFade * delta ) );

		if ( swayWeight <= 0.001f || WindedSway <= 0f )
			return Rotation.Identity;

		float amount = WindedSway * swayWeight;
		float t = now * WindedSwaySpeed * MathF.PI * 2f;

		float pitch = MathF.Sin( t ) * 0.8f;
		float yaw = MathF.Sin( t * 0.5f + 1.3f ) * 0.5f;
		float roll = MathF.Sin( t * 0.37f + 2.1f ) * 1f;

		return Rotation.From( pitch * amount, yaw * amount, roll * amount );
	}

	/// <summary>
	/// The shake's twist for this frame, layered on after everything else so it never touches where you aim.
	/// Run on real time: the hitstop that usually comes with it slows the scene's clock, and a shake frozen
	/// along with everything else would be no shake at all.
	/// </summary>
	private Rotation ShakeRotation()
	{
		float now = RealTime.Now;
		float delta = MathF.Min( now - lastShakeTime, 0.1f );
		lastShakeTime = now;

		if ( shakeTrauma <= 0f )
			return Rotation.Identity;

		shakeTrauma = MathF.Max( 0f, shakeTrauma - ShakeDecay * delta );

		float amount = shakeTrauma * shakeTrauma * ShakeMaxAngle * ShakeStrength;
		float t = now * ShakeFrequency;

		// a few out-of-step sines per axis: cheap, smooth, and never lines up into an obvious rhythm
		float pitch = MathF.Sin( t * 1.00f ) * 0.6f + MathF.Sin( t * 2.31f + 1.7f ) * 0.4f;
		float yaw = MathF.Sin( t * 1.13f + 4.2f ) * 0.6f + MathF.Sin( t * 2.77f + 0.3f ) * 0.4f;
		float roll = MathF.Sin( t * 0.87f + 2.9f ) * 0.6f + MathF.Sin( t * 1.93f + 5.1f ) * 0.4f;

		return Rotation.From( pitch * amount, yaw * amount, roll * amount * 0.5f );
	}

	/// <summary>
	/// Where the eye sits this frame: the animated bone when one is assigned, otherwise the plain Head object.
	/// Only the POSITION is taken here; rotation goes through HeadRotation. A bone's raw rotation is a rig axis convention - rigify bones don't point
	/// down the model's forward - so feeding it to the camera is exactly where a permanent tilt comes from.
	/// </summary>
	private Vector3 EyePosition()
	{
		if ( !EyeBone.IsValid() || BoneInfluence <= 0f )
			return Head.WorldPosition;

		return Vector3.Lerp( Head.WorldPosition, PosedEyeBone().Position, BoneInfluence );
	}

	/// <summary>
	/// The eye bone as the animation has it this frame, read off the renderer. The bone's GameObject isn't a safe
	/// source - it isn't always moved by the animation, and then it just sits at its rest pose.
	/// </summary>
	private Transform PosedEyeBone()
	{
		ResolveEyeRig();

		if ( eyeRenderer.IsValid() && eyeRenderer.TryGetBoneTransform( EyeBone.Name, out var posed ) )
			return posed;

		return EyeBone.WorldTransform;
	}

	/// <summary>Finds the renderer the eye bone belongs to and its rest pose, again only if a different bone gets assigned.</summary>
	private void ResolveEyeRig()
	{
		if ( eyeRestBone == EyeBone )
			return;

		eyeRestBone = EyeBone;
		eyeRenderer = EyeBone.Components.GetInAncestors<SkinnedModelRenderer>();
		var model = eyeRenderer.IsValid() ? eyeRenderer.Model : null;
		eyeRestReady = model is not null && model.Bones.HasBone( EyeBone.Name );
		if ( eyeRestReady )
			eyeRestRotation = model.GetBoneTransform( EyeBone.Name ).Rotation;
	}

	/// <summary>
	/// How far the animation has turned the head away from where you're aiming, as a world-space rotation to put in
	/// front of the aim.
	///
	/// With the eye attachment: the Look At node has already pointed it down the aim, so whatever is left between the
	/// two is the authored motion - the hook's head snap, the slide's tilt. Measuring from the rest pose instead
	/// counted the look-at's turn as head motion too, so looking up 40 degrees tipped the view a further 30 on top,
	/// and the body looked like it was barely following. Only the facing is compared, never the roll, so the
	/// attachment's own axes can't lean the view.
	///
	/// Without it: the bone against its rest pose, compared in the model's own space so the rig's axes cancel out.
	/// </summary>
	private Rotation HeadRotation()
	{
		if ( !EyeBone.IsValid() || RotationInfluence <= 0f )
			return Rotation.Identity;

		ResolveEyeRig();

		if ( !eyeRenderer.IsValid() )
			return Rotation.Identity;

		Rotation delta;
		var eyes = string.IsNullOrEmpty( EyeAttachment ) ? null : eyeRenderer.GetAttachment( EyeAttachment );

		if ( eyes.HasValue )
		{
			delta = Rotation.FromToRotation( eyeAngles.ToRotation().Forward, eyes.Value.Rotation.Forward );
		}
		else if ( eyeRestReady )
		{
			var body = eyeRenderer.WorldRotation;
			var posed = body.Inverse * PosedEyeBone().Rotation;
			delta = body * ( posed * eyeRestRotation.Inverse ) * body.Inverse;
		}
		else
		{
			return Rotation.Identity;
		}

		float angle = delta.Angle();
		float limit = MathF.Min( angle, MaxHeadRotation ) * RotationInfluence;
		if ( angle < 0.01f || limit < 0.01f )
			return Rotation.Identity;

		return Rotation.Slerp( Rotation.Identity, delta, limit / angle );
	}

	// Footstep-style head bob while running on the ground. Vertical bobs twice per cycle
	// (one dip per "step"), with a gentler once-per-cycle sideways sway — the classic
	// figure-eight. Phase speed scales with how fast you're actually moving, and the whole
	// thing fades out in the air, during slides, and while grappling.
	private Vector3 UpdateBob( Rotation camRot )
	{
		if ( Player is null || BobAmplitude <= 0f )
			return Vector3.Zero;

		float horizSpeed = Player.Velocity.WithZ( 0 ).Length;
		bool slidingNow = slidingComponent is not null && slidingComponent.sliding;
		bool shouldBob = Player.isGrounded && horizSpeed > 50f && !slidingNow;

		bobWeight = MathX.Lerp( bobWeight, shouldBob ? 1f : 0f, Time.Delta * BobFade );

		// Advance the step phase with speed: faster running = quicker steps (capped so
		// post-grapple landings don't turn into a jackhammer).
		if ( bobWeight > 0.01f )
		{
			float speedScale = MathF.Min( horizSpeed / MathF.Max( BobReferenceSpeed, 1f ), 2.5f );
			bobTimer += Time.Delta * BobFrequency * speedScale * MathF.PI * 2f;
		}

		float vertical = MathF.Sin( bobTimer * 2f ) * BobAmplitude;
		float horizontal = MathF.Sin( bobTimer ) * BobAmplitude * BobHorizontal;

		return (Vector3.Up * vertical + camRot.Right * horizontal) * bobWeight;
	}

	private void UpdateLanding()
	{
		bool grounded = Player?.isGrounded ?? false;

		// Remember how fast we were falling; the landing frame itself reads ~0.
		if ( !grounded )
			lastAirSpeedDown = MathF.Max( 0f, -Player.Velocity.z );

		// On touchdown, kick the spring downward proportional to fall speed.
		if ( grounded && !wasGrounded && lastAirSpeedDown > MinLandingSpeed )
		{
			float t = MathX.Clamp( (lastAirSpeedDown - MinLandingSpeed) / MathF.Max( MaxLandingSpeed - MinLandingSpeed, 1f ), 0f, 1f );
			landingVel -= LandingKick * t;
		}
		wasGrounded = grounded;

		// Damped spring pulls the camera back to rest (with a little overshoot/bounce).
		landingVel += (-LandingStiffness * landingOffset - LandingDamping * landingVel) * Time.Delta;
		landingOffset += landingVel * Time.Delta;
	}

	/// <summary>
	/// Hand the camera to a fixed viewpoint and stop reading the mouse — the bar between rounds. It eases across
	/// to <paramref name="spot"/>, and leans up to <paramref name="lean"/> degrees toward the cursor so moving the
	/// mouse still does something without letting the player look away from the shot.
	/// </summary>
	public void SetViewOverride( GameObject spot, Vector2 lean, float blendSpeed, bool instant = false )
	{
		viewSpot = spot;
		viewLean = lean;
		viewBlendSpeed = blendSpeed > 0f ? blendSpeed : 6f;

		if ( spot.IsValid() )
			viewSpotTransform = spot.WorldTransform;

		// instant is for a cut behind a black screen: there is no point easing a move nobody can see
		if ( instant )
			viewBlend = 1f;
	}

	/// <summary>
	/// Give the player their head back. The camera eases out of the viewpoint, unless <paramref name="instant"/>
	/// says the screen is black anyway and there is nothing to see.
	/// </summary>
	public void ClearViewOverride( bool instant = false )
	{
		viewSpot = null;

		if ( instant )
			viewBlend = 0f;
	}

	// How far the view leans toward the cursor while a fixed viewpoint is up. Eased on its own clock so the pan
	// trails the mouse instead of sticking to it.
	private Rotation ViewLean()
	{
		var size = Screen.Size;
		var cursor = Mouse.Position;

		float x = size.x > 1f ? ( cursor.x / size.x - 0.5f ) * 2f : 0f;
		float y = size.y > 1f ? ( cursor.y / size.y - 0.5f ) * 2f : 0f;

		var wanted = new Angles( y.Clamp( -1f, 1f ) * viewLean.y, -x.Clamp( -1f, 1f ) * viewLean.x, 0f );

		viewLeanAngles = LerpAngles( viewLeanAngles, wanted, 1f - MathF.Exp( -4f * Time.Delta ) );
		return viewLeanAngles.ToRotation();
	}

	// Per component; the lean never gets anywhere near ±180, so there's no wrapping to worry about.
	private static Angles LerpAngles( Angles a, Angles b, float t ) => new Angles(
		MathX.Lerp( a.pitch, b.pitch, t ),
		MathX.Lerp( a.yaw, b.yaw, t ),
		MathX.Lerp( a.roll, b.roll, t ) );

	/// <summary>
	/// Point the aim somewhere outright - respawning, scripted moments. Roll is dropped; the camera owns that.
	/// </summary>
	public void SetEyeAngles( Angles angles )
	{
		eyeAngles = new Angles( angles.pitch, angles.yaw, 0f );
		eyeAnglesInitialised = true;
	}

	// Jolt the view: 0–1, where 1 is the biggest shake ShakeMaxAngle allows. Shakes add together (capped at 1)
	// and die away on their own, so a flurry builds up instead of resetting on every hit.
	public void Shake( float amount )
	{
		if ( amount <= 0f )
			return;

		shakeTrauma = MathF.Min( 1f, shakeTrauma + amount );
	}

	// Queue a recoil kick (degrees): positive pitch kicks the aim UP, yaw kicks sideways.
	public void AddRecoil( float pitch, float yaw )
	{
		pendingRecoil += new Angles( pitch, yaw, 0f );
	}

	// Ask for a FOV push this frame (highest request wins). Call every frame the effect
	// is active — when calls stop, the FOV eases back to BaseFov automatically.
	// Optionally pass a lerp speed for this request; the highest requested FOV wins, and its
	// speed is used. Omit speed (or pass <0) to ride the global FOVspeed like before.
	public void RequestFov( float fov, float speed = -1f )
	{
		if ( fov > requestedFov )
		{
			requestedFov = fov;
			requestedFovSpeed = speed;
		}
	}

	// Ask for a camera roll this frame (e.g. wall run lean). Same re-request-per-frame
	// contract as RequestFov; eases back to 0 when calls stop. Purely visual.
	public void RequestTilt( float roll )
	{
		requestedTilt = roll;
	}
}
