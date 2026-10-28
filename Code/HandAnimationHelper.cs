using System;

/// <summary>
/// The first-person hands. They play what <see cref="PlayerCombat"/> tells them to and report back the
/// frame a punch connects — they never read input themselves. They used to, and swung whenever the
/// button was down, whether a punch had really been thrown or not.
///
/// That connect frame is a generic anim event placed on the clip in ModelDoc, so where the hit lands is
/// an animation decision: move the event and the punch lands on the new frame, with no code change.
/// </summary>
public sealed class HandAnimationHelper : Component
{
	[Property, Description( "The hands model. Its animgraph plays the attacks, and its clips' generic anim events say when they connect." )]
	public SkinnedModelRenderer Renderer { get; set; }

	[Property, Group( "Animation" ), Description( "Bool in the animgraph that plays the jab. Tick Auto Reset on it in the graph, so each jab fires it once instead of holding it on. Leave empty to land on the windup timer instead." )]
	public string JabParameter { get; set; } = "IsJabbing";

	[Property, Group( "Animation" ), Description( "Bool in the animgraph that plays the hook. Auto Reset, same as the jab. Leave empty to land on the windup timer instead." )]
	public string HookParameter { get; set; } = "IsHooking";

	[Property, Group( "Animation" ), Description( "Bool in the animgraph that plays the kick. Auto Reset, same as the jab. Leave empty to land on the windup timer instead." )]
	public string KickParameter { get; set; } = "IsKicking";

	[Property, Group( "One Trigger" ), Description( "Drive the graph with one trigger and a number instead of a bool per attack: every attack sets AttackType to its slot, then fires AttackTrigger. The graph plays the slot through a Select or Blend node, so it never needs a transition from one attack to another. Off, the three bools above are used as before." )]
	public bool UseAttackType { get; set; }

	[Property, Group( "One Trigger" ), Description( "Bool fired at the start of every attack. Auto Reset in the graph." )]
	public string AttackTrigger { get; set; } = "b_attack";

	[Property, Group( "One Trigger" ), Description( "Which attack it is, as the slot numbers below." )]
	public string AttackType { get; set; } = "attack_type";

	[Property, Group( "One Trigger" ), Description( "Tick if attack_type is a float in the graph — a Blend node needs one. Leave off for an int, which a Select node takes." )]
	public bool AttackTypeIsFloat { get; set; }

	[Property, Group( "One Trigger" )] public int JabLeftSlot { get; set; } = 0;
	[Property, Group( "One Trigger" )] public int JabRightSlot { get; set; } = 1;
	[Property, Group( "One Trigger" )] public int HookSlot { get; set; } = 2;
	[Property, Group( "One Trigger" )] public int KickSlot { get; set; } = 3;

	[Property, Group( "One Trigger" ), Description( "A jab thrown within this long of the last one comes off the other hand. Any longer and the next jab starts from the left again." )]
	public float JabAlternateWindow { get; set; } = 0.6f;

	[Property, Group( "Animation" ), Description( "The Type Name on the generic anim event that marks the frame the fist connects. Every attack clip uses the same one — combat already knows which attack it's waiting on." )]
	public string HitEvent { get; set; } = "hit";

	/// <summary>Called on the frame an attack clip's hit event plays.</summary>
	public Action OnHitFrame { get; set; }

	// which hand the last jab came off, and when — the next quick one takes the other
	bool lastJabRight = true;
	TimeSince timeSinceJab = 999f;

	/// <summary>
	/// Plays this attack's clip. False if it has none — then there's no hit event coming, and whoever
	/// threw the attack has to time the hit some other way.
	/// </summary>
	public bool TryPlay( MeleeAttack attack )
	{
		if ( !Renderer.IsValid() )
			return false;

		if ( UseAttackType )
		{
			if ( string.IsNullOrEmpty( AttackTrigger ) || string.IsNullOrEmpty( AttackType ) )
				return false;

			int slot = SlotFor( attack );

			// the number first, so it's already in place on the frame the trigger is read
			if ( AttackTypeIsFloat )
				Renderer.Set( AttackType, (float)slot );
			else
				Renderer.Set( AttackType, slot );

			Renderer.Set( AttackTrigger, true );
			return true;
		}

		var parameter = ParameterFor( attack );

		if ( string.IsNullOrEmpty( parameter ) )
			return false;

		Renderer.Set( parameter, true );
		return true;
	}

	protected override void OnEnabled()
	{
		if ( Renderer.IsValid() )
			Renderer.OnGenericEvent += OnGenericEvent;
	}

	protected override void OnDisabled()
	{
		if ( Renderer.IsValid() )
			Renderer.OnGenericEvent -= OnGenericEvent;
	}

	void OnGenericEvent( SceneModel.GenericEvent e )
	{
		if ( string.Equals( e.Type, HitEvent, StringComparison.OrdinalIgnoreCase ) )
		{
			OnHitFrame?.Invoke();
			return;
		}

		// said out loud rather than dropped, because a name typed slightly wrong in ModelDoc otherwise
		// looks exactly like the event not being there at all
		Log.Info( $"[Hands] generic event '{e.Type}' ignored — HitEvent is set to '{HitEvent}'" );
	}

	int SlotFor( MeleeAttack attack )
	{
		if ( attack == MeleeAttack.Hook )
			return HookSlot;

		if ( attack == MeleeAttack.Kick )
			return KickSlot;

		// quick jabs trade hands; one after a pause always leads with the left
		bool right = timeSinceJab <= JabAlternateWindow && !lastJabRight;

		lastJabRight = right;
		timeSinceJab = 0;

		return right ? JabRightSlot : JabLeftSlot;
	}

	string ParameterFor( MeleeAttack attack ) => attack switch
	{
		MeleeAttack.Hook => HookParameter,
		MeleeAttack.Kick => KickParameter,
		_ => JabParameter,
	};
}
