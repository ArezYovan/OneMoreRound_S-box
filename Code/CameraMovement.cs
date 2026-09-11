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

	// Recoil queued up by weapons, consumed smoothly over the next few frames.
	private Angles pendingRecoil;
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
	private GrappleGun grappleComponent;

	private CameraComponent Camera;
	private Angles eyeAngles;

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
	private GameObject Orientation { get; set; }
	private GameObject Head { get; set; }

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
		slidingComponent = Player.GameObject.GetComponent<Sliding>();
		grappleComponent = Player.GameObject.GetComponent<GrappleGun>();
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
			return;
		}

		UpdateLanding();

		// Setting the eye angles based on mouse movement (this is the player's true aim).
		eyeAngles = Head.LocalTransform.Rotation.Angles();
		eyeAngles.pitch += Input.MouseDelta.y * 0.1f;
		eyeAngles.yaw -= Input.MouseDelta.x * 0.1f;

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

		Vector3 bobOffset = UpdateBob( camAngles.ToRotation() );

		Camera.WorldPosition = Head.WorldPosition + Vector3.Up * landingOffset + bobOffset;
		Camera.WorldRotation = camAngles.ToRotation();

		Orientation.WorldRotation = Rotation.From( 0f, Head.WorldRotation.Angles().yaw, 0f );
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
		bool grapplingNow = grappleComponent is not null && grappleComponent.grappling;
		bool shouldBob = Player.isGrounded && horizSpeed > 50f && !slidingNow && !grapplingNow;

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
