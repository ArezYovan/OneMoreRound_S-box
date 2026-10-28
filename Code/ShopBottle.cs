using System;

/// <summary>
/// One bottle standing on the counter. Built at runtime by <see cref="BarShop"/> rather than placed, so adding a
/// drink is one asset and nothing else — there's no prefab to keep in step with it.
///
/// It rides its slot: on the bar's rig that's an animated bone, read straight off the rig every frame, so the slide
/// in, the tip and the slide out all come from the animation and the bottle doesn't need to know what it's standing
/// on. The bone's GameObject isn't trusted for this - it isn't moved by the animation. On top of that it does its own
/// procedural bits, measured from the slot: it lifts and turns while the cursor is on it, and hops away when it's
/// paid for. Its numbers live here as constants rather than properties because nothing in the scene owns one to tune.
/// </summary>
public sealed class ShopBottle : Component
{
	// hover: how far it lifts, how fast it turns up there, and how quickly it eases between the two
	const float HoverLift = 7f;
	const float HoverSpin = 70f;
	const float EaseSpeed = 12f;

	// bought: up, around and away before it removes itself
	const float TakenTime = 0.45f;
	const float TakenRise = 40f;
	const float TakenSpin = 540f;

	// how much of its colour is left when you can't afford it
	const float DimAmount = 0.35f;

	// where the card points, above the bottle
	const float CardHeight = 16f;

	/// <summary>What's in it.</summary>
	public Drink Drink { get; private set; }

	/// <summary>Set by the bar each frame: true on the one the cursor is over.</summary>
	public bool Hovered { get; set; }

	/// <summary>Paid for and on its way out. It stops being pointable the moment it's bought.</summary>
	public bool Taken { get; private set; }

	/// <summary>Where the info card points.</summary>
	public Vector3 CardAnchor => WorldPosition + Vector3.Up * CardHeight;

	ModelRenderer renderer;
	Collider hitBox;
	GameObject slot;
	SkinnedModelRenderer rig;
	string bone;
	Rotation upright;
	float halfWidth;
	float lift;
	float spin;
	float size = 1f;
	TimeSince timeSinceTaken;
	bool affordable = true;

	/// <summary>
	/// Told what it is and given its parts. It stands on <paramref name="slotBone"/> of <paramref name="slotRig"/>
	/// when there is one, otherwise on <paramref name="slotObject"/> itself. <paramref name="uprightInSlot"/> turns the
	/// slot's axes into a standing bottle - a bone's axes point wherever the rig put them, so the bar works it out
	/// from the bone's rest pose.
	/// </summary>
	public void Setup( Drink drink, ModelRenderer bottleRenderer, Collider bottleHitBox, GameObject slotObject,
		SkinnedModelRenderer slotRig, string slotBone, Rotation uprightInSlot )
	{
		Drink = drink;
		renderer = bottleRenderer;
		hitBox = bottleHitBox;
		slot = slotObject;
		rig = slotRig;
		bone = slotBone;
		upright = uprightInSlot;

		var bounds = renderer.IsValid() && renderer.Model is not null ? renderer.Model.Bounds : default;
		halfWidth = MathF.Max( bounds.Size.x, bounds.Size.y ) * 0.5f;

		ApplyTint();
		Place();
	}

	/// <summary>Whether the player can afford it. The bar says so every frame; the bottle only dims itself when the answer changes.</summary>
	public void SetAffordable( bool value )
	{
		if ( affordable == value )
			return;

		affordable = value;
		ApplyTint();
	}

	/// <summary>Bought: hop away and remove itself.</summary>
	public void Take()
	{
		if ( Taken )
			return;

		Taken = true;
		Hovered = false;
		timeSinceTaken = 0;

		// nothing to point at any more, so the cursor falls through to whatever is behind it
		if ( hitBox.IsValid() )
			hitBox.Enabled = false;
	}

	protected override void OnUpdate()
	{
		if ( Taken )
		{
			UpdateTaken();
			return;
		}

		lift = MathX.Lerp( lift, Hovered ? HoverLift : 0f, Ease( EaseSpeed ) );

		// turns while you're looking at it, and unwinds when you look away
		spin = Hovered
			? spin + HoverSpin * Time.Delta
			: MathX.Lerp( spin, 0f, Ease( EaseSpeed * 0.5f ) );
	}

	void UpdateTaken()
	{
		float gone = ( timeSinceTaken / TakenTime ).Clamp( 0f, 1f );

		lift += TakenRise / TakenTime * Time.Delta;
		spin += TakenSpin / TakenTime * Time.Delta;
		size = 1f - gone;

		if ( gone >= 1f )
			GameObject.Destroy();
	}

	// Placed here rather than in OnUpdate because only now has the rig been posed for this frame - reading the bone
	// in OnUpdate gets last frame's pose, and the bottle would trail a frame behind the animation.
	protected override void OnPreRender()
	{
		Place();
	}

	/// <summary>Stands the bottle on its slot as the animation has it this frame, lifted and turned about its own axis.</summary>
	void Place()
	{
		var at = SlotTransform();
		var standing = at.Rotation * upright;

		WorldRotation = standing * Rotation.FromYaw( spin );
		WorldPosition = at.Position + standing.Up * lift + Vector3.Up * EdgeLift( standing );
		WorldScale = Vector3.One * size;
	}

	/// <summary>Where the slot is right now: its bone, posed by the animation, or the plain object when there's no bone.</summary>
	Transform SlotTransform()
	{
		if ( rig.IsValid() && !string.IsNullOrEmpty( bone ) && rig.TryGetBoneTransform( bone, out var posed ) )
			return posed;

		return slot.IsValid() ? slot.WorldTransform : WorldTransform;
	}

	/// <summary>
	/// The animation tips the bottle about the middle of its base, which sinks the low edge into the counter. A real
	/// bottle rocks on that edge instead, lifting its middle by half its width times the sine of the tilt, so this
	/// raises it by that much - measured from each bottle's own width, so a fat one lifts more than a thin one.
	/// </summary>
	float EdgeLift( Rotation standing )
	{
		if ( halfWidth <= 0f )
			return 0f;

		float cos = Vector3.Dot( standing.Up, Vector3.Up ).Clamp( -1f, 1f );
		return halfWidth * MathF.Sqrt( 1f - cos * cos );
	}

	void ApplyTint()
	{
		if ( !renderer.IsValid() || Drink is null )
			return;

		var tint = Drink.Tint;

		renderer.Tint = affordable
			? tint
			: new Color( tint.r * DimAmount, tint.g * DimAmount, tint.b * DimAmount, tint.a );
	}

	// Fraction of the remaining gap to close this frame, exponential so the feel holds at any framerate.
	static float Ease( float rate ) => 1f - MathF.Exp( -rate * Time.Delta );
}
