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

	[Property, Group( "Animation" ), Description( "Bool in the hands' animgraph that plays the jab. Tick Auto Reset on it in the graph, so each jab fires it once instead of holding it on." )]
	public string JabParameter { get; set; } = "IsPunching";

	[Property, Group( "Animation" ), Description( "Empty until the hook has a clip. With none it simply lands on its windup timer." )]
	public string HookParameter { get; set; } = "";

	[Property, Group( "Animation" ), Description( "Empty until the kick has a clip. With none it simply lands on its windup timer." )]
	public string KickParameter { get; set; } = "";

	[Property, Group( "Animation" ), Description( "The Type Name on the generic anim event that marks the frame the fist connects. Every attack clip uses the same one — combat already knows which attack it's waiting on." )]
	public string HitEvent { get; set; } = "hit";

	/// <summary>Called on the frame an attack clip's hit event plays.</summary>
	public Action OnHitFrame { get; set; }

	/// <summary>
	/// Plays this attack's clip. False if it has none — then there's no hit event coming, and whoever
	/// threw the attack has to time the hit some other way.
	/// </summary>
	public bool TryPlay( MeleeAttack attack )
	{
		var parameter = ParameterFor( attack );

		if ( !Renderer.IsValid() || string.IsNullOrEmpty( parameter ) )
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

	string ParameterFor( MeleeAttack attack ) => attack switch
	{
		MeleeAttack.Hook => HookParameter,
		MeleeAttack.Kick => KickParameter,
		_ => JabParameter,
	};
}
