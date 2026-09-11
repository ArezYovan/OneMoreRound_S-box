using Sandbox;
using System;

public sealed class WallRunning : Component
{
	[Header("WallRunning")]
	// Target speed along the wall — the wall run accelerates toward this, like maxSpeed.
	[Property] public float wallRunSpeed { get; set; } = 1000f;
	// How fast you reach wallRunSpeed (lerp rate). Higher = snappier.
	[Property] public float wallRunAccel { get; set; } = 4f;
	// How fast excess entry speed (from a fast grapple/jump into the wall) eases back down
	// to wallRunSpeed. Mirrors the ground soft cap so you can't carry infinite speed.
	[Property] public float wallSpeedDecay { get; set; } = 3f;
	// Reduced gravity while on the wall, for a gentle controlled slide. 0 = full float.
	[Property] public float wallRunGravityScale { get; set; } = 0.1f;
	// Pushes you into the wall so you stay attached unless you steer away.
	[Property] public float wallStickForce { get; set; } = 100f;
	[Property] public float MaxWallRunTime { get; set; } = 1000f;
	[Property] public float WallRunTimer { get; set; }

	[Header("Wall Jump")]
	[Property] float WallJumpUpForce { get; set; }
	[Property] float WallJumpSideForce { get; set; }

	[Header("Detection")]
	[Property] float WallCheckDistance { get; set; }
	// Only near-vertical surfaces count as walls. A hit normal with |z| above this is a
	// ramp/slope/ceiling, not something you can wall run on.
	[Property] float MaxWallNormalZ { get; set; } = 0.3f;
	private SceneTraceResult leftWallHit;
	private SceneTraceResult rightWallHit;
	private bool wallLeft;
	private bool wallRight;

	[Header("Camera")]
	[Property] public float WallRunFov { get; set; } = 120f;
	// Camera lean while on the wall (rolls away from the wall side).
	[Property] public float WallRunTilt { get; set; } = 10f;

	[Header("Exiting")]
	[Property, Range(0f, 1f)] float ExitWallTime { get; set; }
	private float ExitWallTimer { get; set; }
	private bool exitingWall;

	// Input. s&box convention: AnalogMove.x = forward/back (W/S), AnalogMove.y = strafe
	// (positive = left). Named for what they are so they can't get crossed again.
	private float forwardInput;
	private float strafeInput;

	// References
	private RBPlayerMovement playerMovement;
	private Rigidbody rb;
	private GrappleGun grapple;
	private GameObject orientation;
	[Property] public CameraMovement camMovement;
	private bool isGrounded;
	private float wallRunEntrySpeed;
	[Property] public bool IsWallRunning { get; set; }

	protected override void OnAwake()
	{
		rb = GameObject.GetComponent<Rigidbody>();
		playerMovement = GameObject.GetComponent<RBPlayerMovement>();
		grapple = GameObject.GetComponent<GrappleGun>();
		orientation = playerMovement.Orientation;
		camMovement ??= GameObject.Root.Components.Get<CameraMovement>( FindMode.EverythingInSelfAndDescendants );
	}

	protected override void OnUpdate()
	{
		if ( IsProxy )
			return;

		isGrounded = playerMovement.isGrounded;
		CheckForWall();
		StateHandler();
	}

	protected override void OnFixedUpdate()
	{
		if ( IsProxy )
			return;

		if ( IsWallRunning )
			WallRunningMovement();
	}

	private void StateHandler()
	{
		forwardInput = Input.AnalogMove.x;
		strafeInput = Input.AnalogMove.y;

		// While the grapple is reeling, it owns velocity — don't fight it with a wall run.
		bool grappleBusy = grapple is not null && grapple.grappling;

		// Wall run requires: a valid wall, holding FORWARD (W), airborne, not in the
		// post-run exit grace, and not mid-grapple.
		bool wantsWallRun = (wallLeft || wallRight) && forwardInput > 0f
			&& !isGrounded && !exitingWall && !grappleBusy && !playerMovement.IsGrindingNow && !playerMovement.IsGroundPounding;

		if ( wantsWallRun )
		{
			if ( !IsWallRunning ) StartWallRun();

			// Re-requested every frame we're on the wall; eases off when we stop asking.
			camMovement.RequestFov( WallRunFov );
			camMovement.RequestTilt( wallRight ? -WallRunTilt : WallRunTilt );

			if ( WallRunTimer > 0 ) WallRunTimer -= Time.Delta;
			if ( WallRunTimer <= 0 ) StopWallRun();

			if ( Input.Pressed( "Jump" ) ) WallJump();
		}
		else if ( IsWallRunning )
		{
			// Lost the wall / let go of forward / touched down — end the run cleanly so
			// gravity is always restored.
			StopWallRun();
		}

		if ( exitingWall )
		{
			if ( ExitWallTimer > 0 ) ExitWallTimer -= Time.Delta;
			if ( ExitWallTimer <= 0 ) exitingWall = false;
		}
	}

	private void CheckForWall()
	{
		// Named for the side of the PLAYER they check: rightWallHit looks toward
		// orientation.Right, leftWallHit looks the opposite way.
		rightWallHit = Scene.Trace
			.Ray( orientation.WorldPosition, orientation.WorldPosition + orientation.WorldRotation.Right * WallCheckDistance )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

		leftWallHit = Scene.Trace
			.Ray( orientation.WorldPosition, orientation.WorldPosition - orientation.WorldRotation.Right * WallCheckDistance )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

		wallRight = rightWallHit.Hit && MathF.Abs( rightWallHit.Normal.z ) < MaxWallNormalZ;
		wallLeft = leftWallHit.Hit && MathF.Abs( leftWallHit.Normal.z ) < MaxWallNormalZ;
	}

	private void StartWallRun()
	{
		IsWallRunning = true;
		WallRunTimer = MaxWallRunTime;

		// Capture entry speed so a fast grapple-into-wall keeps its momentum along the wall.
		wallRunEntrySpeed = rb.Velocity.WithZ( 0f ).Length;
		rb.Velocity = rb.Velocity.WithZ( 0f );
	}

	private void WallRunningMovement()
	{
		// Gentle reduced gravity instead of a hard float — you slide down slowly.
		rb.Gravity = true;
		rb.GravityScale = wallRunGravityScale;

		Vector3 wallNormal = wallRight ? rightWallHit.Normal : leftWallHit.Normal;
		Vector3 wallForward = Vector3.Cross( Vector3.Up, wallNormal );

		// Pick the along-wall direction closest to where the player is facing.
		if ( (orientation.WorldRotation.Forward - wallForward).Length > (orientation.WorldRotation.Forward - -wallForward).Length )
			wallForward = -wallForward;

		// Excess entry speed (fast grapple/jump into the wall) eases back down to
		// wallRunSpeed over time — you keep the momentum briefly, then it soft-caps so
		// you can't accumulate infinite speed by hugging walls.
		if ( wallRunEntrySpeed > wallRunSpeed )
			wallRunEntrySpeed = MathX.Lerp( wallRunEntrySpeed, wallRunSpeed, Time.Delta * wallSpeedDecay );

		// Target velocity ALONG the wall, at the (decaying) carried speed.
		float wallSpeed = MathF.Max( wallRunEntrySpeed, wallRunSpeed );
		Vector3 currentHoriz = rb.Velocity.WithZ( 0 );
		Vector3 newHoriz = Vector3.Lerp( currentHoriz, wallForward * wallSpeed, Time.Delta * wallRunAccel );
		rb.Velocity = newHoriz.WithZ( rb.Velocity.z );

		// Stick to the wall unless you're strafing AWAY from it (A off a right-side wall,
		// D off a left-side wall). strafeInput is positive-left.
		bool steeringAway = (wallRight && strafeInput > 0f) || (wallLeft && strafeInput < 0f);
		if ( !steeringAway )
			rb.ApplyForce( -wallNormal * wallStickForce );
	}

	private void StopWallRun()
	{
		IsWallRunning = false;
		exitingWall = true;
		ExitWallTimer = ExitWallTime;
		rb.GravityScale = 1f;
		rb.Gravity = true;
	}

	private void WallJump()
	{
		Vector3 wallNormal = wallRight ? rightWallHit.Normal : leftWallHit.Normal;

		StopWallRun();

		// This press is ours — clear the buffered ground jump so a quick landing right
		// after a low wall jump doesn't fire a second free jump.
		playerMovement.ClearJumpBuffer();

		// Zero vertical first so the launch height is consistent, then apply as an
		// impulse (frame-independent) like the main jump does.
		rb.Velocity = rb.Velocity.WithZ( 0 );
		Vector3 impulse = Vector3.Up * WallJumpUpForce + wallNormal * WallJumpSideForce;
		rb.ApplyImpulse( impulse * 1000f );
	}
}
