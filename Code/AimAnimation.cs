using System;

/// <summary>
/// What the Look At node wants in its target parameter. The four combinations exist because the node
/// doesn't say which it reads, and guessing wrong looks identical to the node being broken. Cycle this
/// until the body follows your aim, then leave it alone.
/// </summary>
public enum AimTargetSpace
{
	WorldPosition,
	ModelPosition,
	WorldDirection,
	ModelDirection
}

/// <summary>
/// Points the body where the player is looking. The animgraph's Look At node does the bending — all this
/// does is tell it what to aim at, once a frame.
///
/// The target is built from <see cref="CameraMovement.EyeAngles"/> and deliberately NOT from the camera's
/// rotation. The camera carries the head bob, the strafe tilt and the landing spring layered on top of the
/// aim; feed it in here and the whole body sways along with all three, which reads as the character
/// wobbling for no reason anyone can point at.
/// </summary>
public sealed class AimAnimation : Component
{
	[Property, Group( "References" ), Description( "Optional, found automatically. The model whose animgraph has the Look At node." )]
	public SkinnedModelRenderer Renderer { get; set; }

	[Property, Group( "References" ), Description( "Optional, found automatically. Where the aim comes from. Its EyeAngles are the clean aim, with none of the camera's bob or tilt on them." )]
	public CameraMovement Look { get; set; }

	[Property, Group( "Animation" ), Description( "The parameter the Look At node reads its target from." )]
	public string Parameter { get; set; } = "aim_head";

	[Property, Group( "Animation" ), Description( "Attachment the look is measured from — the same one the Look At node is set to. Measuring from anywhere else puts a constant offset on every look. Falls back to the renderer's own position if the model hasn't got it." )]
	public string EyeAttachment { get; set; } = "eyes";

	[Property, Group( "Animation" ), Description( "What the node expects in that parameter. Try ModelPosition first, then the others. If none of the four move the body, the problem is the chain or the node, not this." )]
	public AimTargetSpace Mode { get; set; } = AimTargetSpace.ModelPosition;

	[Property, Group( "Feel" ), Description( "How far down the aim the target point sits. Far enough to read as a pure direction — too close and the angle swings as the body moves underneath it. Ignored by the Direction modes." )]
	public float TargetDistance { get; set; } = 500f;

	[Property, Group( "Debug" ), Description( "With the player selected, draws the line the body is being told to look down. If the line points where you're looking but the body doesn't follow, the problem is in the graph rather than here." )]
	public bool DrawAim { get; set; }

	Vector3 lastOrigin;
	Vector3 lastTarget;

	protected override void OnAwake()
	{
		Renderer ??= Components.Get<SkinnedModelRenderer>( FindMode.EverythingInSelfAndDescendants );
		Look ??= CameraMovement.Find( GameObject );
	}

	protected override void OnUpdate()
	{
		if ( IsProxy || !Renderer.IsValid() || string.IsNullOrEmpty( Parameter ) )
			return;

		// The camera resolves itself late — it waits for a networked player to exist — so keep asking rather
		// than giving up on whatever wasn't there yet in OnAwake.
		Look ??= CameraMovement.Find( GameObject );

		if ( Look is null )
			return;

		var forward = Look.EyeAngles.ToRotation().Forward;
		var origin = EyeOrigin();
		var target = origin + forward * MathF.Max( TargetDistance, 1f );

		var frame = Renderer.WorldTransform;

		Renderer.Set( Parameter, Mode switch
		{
			AimTargetSpace.WorldPosition => target,
			AimTargetSpace.ModelPosition => frame.PointToLocal( target ),
			AimTargetSpace.WorldDirection => forward,
			_ => frame.NormalToLocal( forward ),
		} );

		lastOrigin = origin;
		lastTarget = target;
	}

	// Measured from the same attachment the Look At node uses, so the angle asked for here is the angle the
	// node computes from.
	Vector3 EyeOrigin()
	{
		var attachment = string.IsNullOrEmpty( EyeAttachment ) ? null : Renderer.GetAttachment( EyeAttachment );
		return attachment?.Position ?? Renderer.WorldPosition;
	}

	protected override void DrawGizmos()
	{
		if ( !DrawAim || lastTarget.IsNearlyZero() )
			return;

		Gizmo.Transform = global::Transform.Zero;
		Gizmo.Draw.Color = Color.Cyan;
		Gizmo.Draw.Line( lastOrigin, lastTarget );
		Gizmo.Draw.SolidSphere( lastTarget, 4f );
	}
}
