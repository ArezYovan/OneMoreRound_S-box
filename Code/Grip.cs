/// <summary>
/// Where a hand closes on this prop. Add an empty child to the prop, put this on it, and move and turn that
/// child with the ordinary gizmos until it sits where a palm should: its position is the middle of the palm,
/// and its blue (up) axis points out of the top of the fist, the way a bottle's neck would.
///
/// Optional. Without one a prop is held by its middle, standing the way it was modelled — right for most
/// things modelled upright. This is for the few that aren't, and for anything you'd rather hold somewhere
/// other than the middle: a bottle by the neck, a chair by a leg.
///
/// The axes are drawn the same colours as <see cref="PlayerGrab"/>'s palm marker, because that is the whole
/// rule: this frame gets laid exactly over that one.
/// </summary>
public sealed class Grip : Component
{
	protected override void DrawGizmos()
	{
		// only while you're placing it — a marker on every prop in the bar would be noise
		if ( !Gizmo.IsSelected )
			return;

		Gizmo.Draw.Color = Color.Orange;
		Gizmo.Draw.LineSphere( Vector3.Zero, 3f, 8 );

		Gizmo.Draw.Color = Color.Red;
		Gizmo.Draw.Line( Vector3.Zero, Vector3.Forward * 10f );

		Gizmo.Draw.Color = Color.Green;
		Gizmo.Draw.Line( Vector3.Zero, Vector3.Left * 10f );

		Gizmo.Draw.Color = Color.Blue;
		Gizmo.Draw.Line( Vector3.Zero, Vector3.Up * 10f );
	}
}
