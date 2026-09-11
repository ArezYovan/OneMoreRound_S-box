using Sandbox;

public sealed class HandAnimationHelper : Component
{
	[Property] public SkinnedModelRenderer Renderer { get; set; }

	protected override void OnUpdate()
	{
		bool attacking = Input.Down( "attack1" );

		Renderer.Set( "IsPunching", attacking );
	}
}
