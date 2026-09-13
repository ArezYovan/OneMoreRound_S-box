using Sandbox;
using System;

public sealed class Sliding : Component
{
	// REFERENCES
	private RBPlayerMovement playerMovement;
	private Rigidbody rb;
	private GameObject orientation;
	private GameObject Head;
	[Property] public GameObject PlayerObj;

	[Header("Sliding")]
	[Property] public float maxSlideSpeed { get; set; } = 1500f;
	[Property] public float minSlideSpeed { get; set; } = 300f;
	// The slide auto-ends below this speed so you never crawl out the tail of a slide.
	[Property] public float slideEndSpeed { get; set; } = 150f;
	[Property] public float slideBoost { get; set; } = 10f;
	[Property] public float slideFriction { get; set; } = 0f;
	// How sharply you can steer/carve the slide. Higher = turns faster. Speed is preserved.
	[Property] public float slideSteerStrength { get; set; } = 4f;

	[Header("Slope Sliding")]
	// Extra push down the slope while descending — this is what makes downhill slides fun.
	[Property] public float slopeSlideAccel { get; set; } = 15f;
	// Higher speed ceiling for downhill slides so they can outrun a flat slide.
	[Property] public float maxSlopeSlideSpeed { get; set; } = 3000f;

	[Header("Timer")]
	[Property] public float maxSlideTime { get; set; } = 2f;
	[Property] public float slideTimer { get; set; }

	[Header("Feel")]
	// How long a slide press is remembered, so a press just before landing still slides.
	[Property] public float slideBufferTime { get; set; } = 2f;
	// How fast the body/camera ease into and out of the crouch (higher = snappier).
	[Property] public float crouchLerpSpeed { get; set; } = 25f;
	// How far the head dips while sliding.
	[Property] public float headDropAmount { get; set; } = 20f;
	// Clearance needed above the player to stand back up after a slide. If something is
	// overhead you stay crouched until it's clear.
	[Property] public float standUpHeadroom { get; set; } = 64f;
	// Camera FOV push while sliding.
	[Property] public float SlideFov { get; set; } = 120f;

	[Property] public CameraMovement camMovement { get; set; }
	[Property] public bool sliding { get; set; }

	// Player Scale Values
	private float slideZScale { get; set; }
	private float startZScale { get; set; }

	// Private variables
	private float horizontalInput;
	private float verticalInput;
	private float slideEntrySpeed;
	private float slideBufferTimer;
	private float currentHeadDrop;

	protected override void OnAwake()
	{
		playerMovement = GameObject.GetComponent<RBPlayerMovement>();
		rb = GameObject.GetComponent<Rigidbody>();
		orientation = playerMovement.Orientation;
		Head = playerMovement.Head;

		startZScale = PlayerObj.LocalScale.z;
		slideZScale = PlayerObj.LocalScale.z / 2f;
	}

	protected override void OnStart()
	{
		camMovement ??= CameraMovement.Find( GameObject );
	}

	protected override void OnUpdate()
	{
		if ( IsProxy )
			return;

		horizontalInput = Input.AnalogMove.x;
		verticalInput = Input.AnalogMove.y;

		// Buffer the slide press so a press shortly before landing still slides on touchdown.
		if ( Input.Pressed( "Duck" ) )
			slideBufferTimer = slideBufferTime;
		if ( slideBufferTimer > 0f )
			slideBufferTimer -= Time.Delta;

		// Consume the buffered press once we're grounded and actually moving.
		// Grounded state comes from RBPlayerMovement — single source of truth, so the
		// movement systems can never disagree about being on the ground.
		bool moving = horizontalInput != 0 || verticalInput != 0 || rb.Velocity.WithZ( 0 ).Length > 50f;
		// Require Duck to still be HELD when the buffered press lands. The buffer only
		// tolerates pressing a hair early; if you tapped-and-released in the air it must NOT
		// fire a slide on touchdown — that would apply StartSlide's speed boost as a phantom
		// one-frame launch before the stop check below cancels it.
		if ( slideBufferTimer > 0f && !sliding && !playerMovement.IsGrindingNow && playerMovement.isGrounded && moving && Input.Down( "Duck" ) )
		{
			StartSlide();
			slideBufferTimer = 0f;
		}

		// End the slide the instant Duck isn't held. This deliberately checks Down (is it
		// held NOW?) instead of the Released edge: the slide can START from a buffered press
		// on landing, so the Released edge often fires BEFORE `sliding` is ever true and gets
		// lost — which left the slide stuck "on" with the key not held (worst on downhill
		// slopes, whose SlopeSlide path has no timer/speed auto-end, and while airborne, where
		// SlidingMovement doesn't run). Down is re-checked every frame, so it can't get stuck.
		if ( sliding && !Input.Down( "Duck" ) )
			StopSlide();

		// A rail grind owns the player — never slide while grinding.
		if ( sliding && playerMovement.IsGrindingNow )
			StopSlide();

		// FOV push, re-requested every render frame while sliding (eases off on its own).
		if ( sliding )
			camMovement?.RequestFov( SlideFov );

		// Smoothly ease the crouch in/out every frame (see UpdateCrouchLerp).
		UpdateCrouchLerp();
	}

	protected override void OnFixedUpdate()
	{
		if ( IsProxy )
			return;

		if ( sliding && playerMovement.isGrounded )
			SlidingMovement();
	}

	private void StartSlide()
	{
		// Guard against re-triggering an active slide (would re-boost speed and re-drop the head).
		if ( sliding ) return;

		sliding = true;

		Vector3 flatVel = rb.Velocity.WithZ( 0 );
		slideEntrySpeed = flatVel.Length;

		// One-time entry burst: boost current momentum (or facing direction if nearly
		// stationary), clamped between minSlideSpeed and maxSlideSpeed.
		Vector3 slideDir = slideEntrySpeed > 50f ? flatVel.Normal : orientation.WorldRotation.Forward;
		float boostSpeed = MathX.Clamp( slideEntrySpeed * slideBoost, minSlideSpeed, maxSlideSpeed );
		// Never slow you below your entry speed — lets a fast grapple/landing carry into the
		// slide (it then bleeds off naturally via friction).
		boostSpeed = MathF.Max( boostSpeed, slideEntrySpeed );
		rb.Velocity = (slideDir * boostSpeed).WithZ( rb.Velocity.z );

		// The crouch (scale + head dip) is eased in by UpdateCrouchLerp, not snapped here.
		slideTimer = maxSlideTime;
	}

	// Start a slide carrying an EXACT velocity, bypassing the normal entry boost and clamp.
	// The velocity is used as-is (its vertical kept), so GroundPound can hand in flat momentum
	// (z = 0) on flat ground, or a down-slope velocity on a slope so you flow down it.
	public void BeginSlideWith( Vector3 velocity )
	{
		if ( sliding || playerMovement is null || !playerMovement.isGrounded ) return;

		sliding = true;
		slideEntrySpeed = velocity.WithZ( 0f ).Length;
		rb.Velocity = velocity;
		slideTimer = maxSlideTime;
	}

	private void SlidingMovement()
	{
		Vector3 inputDirection = orientation.WorldRotation.Forward * horizontalInput + orientation.WorldRotation.Right * -verticalInput;

		if ( playerMovement.OnSlopeNow )
			SlopeSlide( inputDirection );
		else
			FlatSlide( inputDirection );
	}

	private void FlatSlide( Vector3 inputDirection )
	{
		// Carve toward your input without changing speed.
		Steer( inputDirection );

		// Friction bleeds the slide down over time. No hard cap here, so a fast entry (grapple
		// landing) keeps its speed and bleeds off naturally instead of snapping to the cap.
		Vector3 flatVel = rb.Velocity.WithZ( 0 );
		flatVel = Vector3.Lerp( flatVel, Vector3.Zero, Time.Delta * slideFriction );

		rb.Velocity = flatVel.WithZ( rb.Velocity.z );

		// End crisply once we're too slow to be "sliding" — no crawling out the tail.
		slideTimer -= Time.Delta;
		if ( slideTimer <= 0 || flatVel.Length < slideEndSpeed )
			StopSlide();
	}

	private void SlopeSlide( Vector3 inputDirection )
	{
		Vector3 normal = playerMovement.SlopeNormal;
		Vector3 downhill = Vector3.VectorPlaneProject( Vector3.Down, normal ).Normal;

		// Carve across the slope face — project input onto the surface so you don't dig in.
		Steer( Vector3.VectorPlaneProject( inputDirection, normal ) );

		bool goingDown = Vector3.Dot( rb.Velocity, downhill ) > 0f;

		if ( goingDown )
		{
			// Accelerate down the slope — no friction, no timer drain, higher cap.
			// Gravity (left on by RBPlayerMovement while sliding) adds to this.
			rb.ApplyForce( downhill * slopeSlideAccel * 200 );

			Vector3 flatVel = rb.Velocity.WithZ( 0 );
			if ( flatVel.Length > maxSlopeSlideSpeed )
				rb.Velocity = (flatVel.Normal * maxSlopeSlideSpeed).WithZ( rb.Velocity.z );
		}
		else
		{
			// Pushing uphill / across: bleed speed and run the timer so the slide ends.
			Vector3 flatVel = rb.Velocity.WithZ( 0 );
			flatVel = Vector3.Lerp( flatVel, Vector3.Zero, Time.Delta * slideFriction );
			rb.Velocity = flatVel.WithZ( rb.Velocity.z );

			slideTimer -= Time.Delta;
			if ( slideTimer <= 0 || flatVel.Length < slideEndSpeed )
				StopSlide();
		}
	}

	// Rotates the horizontal velocity toward the input direction at a limited rate while
	// keeping the same speed — lets you carve the slide without adding or killing momentum.
	private void Steer( Vector3 inputDirection )
	{
		if ( inputDirection.Length < 0.01f )
			return;

		Vector3 flatVel = rb.Velocity.WithZ( 0 );
		float speed = flatVel.Length;
		if ( speed < 1f )
			return;

		Vector3 newDir = Vector3.Lerp( flatVel.Normal, inputDirection.Normal, Time.Delta * slideSteerStrength ).Normal;
		rb.Velocity = (newDir * speed).WithZ( rb.Velocity.z );
	}

	private void StopSlide()
	{
		sliding = false;
		// Crouch eases back out via UpdateCrouchLerp; FOV eases back once we stop requesting it.
	}

	// Eases the body scale and head height toward their slide/standing targets each frame
	// so entering and leaving a slide looks smooth instead of snapping instantly.
	private void UpdateCrouchLerp()
	{
		// Stay crouched if there's something overhead — standing up would clip into it.
		bool standing = !sliding && CanStandUp();

		float targetScale = standing ? startZScale : slideZScale;
		float newScale = MathX.Lerp( PlayerObj.LocalScale.z, targetScale, Time.Delta * crouchLerpSpeed );
		PlayerObj.LocalScale = PlayerObj.LocalScale.WithZ( newScale );

		// Track the applied head dip so we only ever add the per-frame delta (works
		// regardless of the body moving underneath it) and always return exactly to zero.
		float targetDrop = standing ? 0f : headDropAmount;
		float newDrop = MathX.Lerp( currentHeadDrop, targetDrop, Time.Delta * crouchLerpSpeed );
		Head.WorldPosition += Vector3.Down * (newDrop - currentHeadDrop);
		currentHeadDrop = newDrop;
	}

	// Room to stand? Already-standing passes trivially (the trace starts inside our own
	// cleared space and we ignore our hierarchy).
	private bool CanStandUp()
	{
		// Start the sweep a bit above the feet so the sphere begins clear of the floor we're
		// standing on. The root rests only ~capsule-radius above the ground, which is LESS
		// than this trace radius — starting at the feet made the trace begin inside the
		// floor, which read as "something overhead" and kept the player crouched whenever
		// they were grounded (standing up only mid-jump).
		const float traceRadius = 14f;
		Vector3 start = WorldPosition + Vector3.Up * (traceRadius + 2f);

		return !Scene.Trace
			.Ray( start, WorldPosition + Vector3.Up * standUpHeadroom )
			.Radius( traceRadius )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run()
			.Hit;
	}
}
