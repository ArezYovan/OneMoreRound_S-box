using System;
using System.Collections.Generic;

public enum HitReactionSpace
{
	Bone,
	Model
}

/// <summary>
/// Makes the part of the body that got hit be the part that recoils: a jab to the right shoulder snaps
/// that shoulder back, one to the jaw rocks the head. None of it is animated by hand — the animgraph's
/// Hit React node bends the bones procedurally, and all this does is tell it where the blow landed,
/// which way it was going and how hard.
///
/// Works off <see cref="Health"/>, so anything that deals damage with a position gets a reaction for
/// free. The enemy's collider is one box, so a hit can't say which bone it touched — the bone is picked
/// here instead, as the one in <see cref="Bones"/> closest to the line the blow travelled along.
/// </summary>
public sealed class HitReaction : Component
{
	[Property, Group( "References" ), Description( "Optional, found automatically. The model whose animgraph has the Hit React node." )]
	public SkinnedModelRenderer Renderer { get; set; }

	[Property, Group( "References" ), Description( "Optional, found automatically. Every hit it reports becomes a reaction." )]
	public Health Health { get; set; }

	[Property, Group( "Bones" ), Description( "Bones a blow can land on. Keep to ones the Hit React node's weight list actually moves — on the citizen that's the spine and everything above it. Fingers are in the skeleton too, but a punch that picks a finger just looks like no reaction at all." )]
	public List<string> Bones { get; set; } = new() { "pelvis", "spine_0", "spine_1", "spine_2", "neck_0", "head", "clavicle_L", "clavicle_R", "arm_upper_L", "arm_upper_R" };

	[Property, Group( "Feel" ), Description( "Strength sent for the lightest hit. The citizen's Hit React node is set up for impact forces of 50 to 250, so small numbers like 1 barely register." )]
	public float MinStrength { get; set; } = 50f;

	[Property, Group( "Feel" ), Description( "Strength sent for a hit of DamageForMaxStrength or more." )]
	public float MaxStrength { get; set; } = 250f;

	[Property, Group( "Feel" ), Description( "Damage that earns MaxStrength. At 25 the hook's 22 lands near the top and the jab's 8 near the bottom, so the two read differently." )]
	public float DamageForMaxStrength { get; set; } = 25f;

	[Property, Group( "Feel" ), Description( "What hit_offset and hit_direction are measured against. Try Bone first; if blows recoil the wrong way, flip this before tuning anything else." )]
	public HitReactionSpace Space { get; set; } = HitReactionSpace.Bone;

	[Property, Group( "Debug" ), Description( "Draw the bone each hit picked, a line from where the blow landed to it, and which way it was pushed." )]
	public bool DebugDraw { get; set; } = true;

	[Property, Group( "Debug" )] public float DebugDuration { get; set; } = 1.5f;

	[Property, Group( "Debug" )] public bool LogHits { get; set; } = true;

	// one warning per missing name for the whole game, not one per enemy a wave spawns
	static readonly HashSet<string> warnedMissing = new();

	protected override void OnStart()
	{
		Renderer ??= Components.Get<SkinnedModelRenderer>( FindMode.Enabled | FindMode.InSelf | FindMode.InDescendants );
		Health ??= Components.Get<Health>( FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors );

		if ( Health.IsValid() )
			Health.OnDamaged += OnDamaged;

		var bones = Renderer.IsValid() ? Renderer.Model?.Bones : null;

		if ( bones is null )
			return;

		foreach ( var name in Bones )
		{
			if ( !bones.HasBone( name ) && warnedMissing.Add( name ) )
				Log.Warning( $"[HitReaction] '{name}' isn't a bone on {GameObject.Name}'s model — hits will never pick it" );
		}
	}

	protected override void OnDestroy()
	{
		if ( Health.IsValid() )
			Health.OnDamaged -= OnDamaged;
	}

	void OnDamaged( float amount, GameObject attacker )
	{
		// a killing blow hands the body to the ragdoll, which has its own ideas about where it's going
		if ( Health.IsDead || !Renderer.IsValid() )
			return;

		var position = Health.LastHitPosition;
		var direction = Health.LastHitDirection;

		// damage that came from nowhere in particular has nothing to point the flinch at
		if ( position == Vector3.Zero )
			return;

		// a thrown bottle doesn't say which way it was flying; from whoever threw it to where it landed
		// is close enough for a flinch
		if ( direction.IsNearlyZero() && attacker.IsValid() )
			direction = ( position - attacker.WorldPosition ).WithZ( 0 ).Normal;

		if ( direction.IsNearlyZero() || !TryPickBone( position, direction, out var bone, out var boneTransform ) )
			return;

		float strength = amount.Remap( 0f, DamageForMaxStrength, MinStrength, MaxStrength, true );
		var frame = Space == HitReactionSpace.Bone ? boneTransform : Renderer.WorldTransform;

		Renderer.Set( "hit_bone", bone.Index );
		Renderer.Set( "hit_offset", frame.PointToLocal( position ) );
		Renderer.Set( "hit_direction", frame.NormalToLocal( direction ) );
		Renderer.Set( "hit_strength", strength );
		Renderer.Set( "hit", true );

		DrawHit( bone, boneTransform.Position, position, direction, strength );

		if ( LogHits )
			Log.Info( $"[HitReaction] {GameObject.Name} hit on {bone.Name}, strength {strength:0}" );
	}

	/// <summary>
	/// The listed bone nearest the line the blow travelled along. Measured to the line rather than to the
	/// point it landed, because that point is on the face of a box collider out in front of the body, and
	/// a hook arriving from the side lands on a face nowhere near the bone it's heading for.
	/// </summary>
	bool TryPickBone( Vector3 position, Vector3 direction, out BoneCollection.Bone picked, out Transform pickedTransform )
	{
		picked = null;
		pickedTransform = default;

		var bones = Renderer.Model?.Bones;

		if ( bones is null )
			return false;

		float closest = float.MaxValue;

		foreach ( var name in Bones )
		{
			if ( !bones.HasBone( name ) )
				continue;

			var bone = bones.GetBone( name );

			if ( !Renderer.TryGetBoneTransform( bone, out var transform ) )
				continue;

			// how far along the blow's path the bone sits, then how far off to the side of that path
			float along = Vector3.Dot( transform.Position - position, direction );
			float offPath = ( transform.Position - ( position + direction * along ) ).Length;

			if ( offPath >= closest )
				continue;

			closest = offPath;
			picked = bone;
			pickedTransform = transform;
		}

		return picked is not null;
	}

	void DrawHit( BoneCollection.Bone bone, Vector3 bonePosition, Vector3 hitPosition, Vector3 direction, float strength )
	{
		if ( !DebugDraw )
			return;

		DebugOverlay.Sphere( new Sphere( hitPosition, 2f ), Color.Yellow, DebugDuration );
		DebugOverlay.Line( hitPosition, bonePosition, Color.Yellow.WithAlpha( 0.5f ), DebugDuration );
		DebugOverlay.Sphere( new Sphere( bonePosition, 4f ), Color.Orange, DebugDuration );
		DebugOverlay.Line( bonePosition, bonePosition + direction * 24f, Color.Red, DebugDuration );
		DebugOverlay.Text( bonePosition + Vector3.Up * 10f, $"{bone.Name} {strength:0}", 14f, TextFlag.Center, Color.Orange, DebugDuration, true );
	}
}
