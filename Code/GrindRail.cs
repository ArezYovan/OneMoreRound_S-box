using Sandbox;
using System;
using System.Collections.Generic;

// A grind rail: an ordered path of node GameObjects the player can lock onto and ride.
// Place 2+ GameObjects in the world, drop them into Nodes (in order), and the rail is the
// polyline through them. RailGrinding (on the player) handles attach/movement; this
// component only owns the PATH and the math to query it.
//
// v1 is LINEAR (straight segments between nodes). The query surface — FindClosest,
// GetPoint, GetTangent, Length — is deliberately the only thing RailGrinding touches, so
// swapping in Catmull-Rom curves later is a change to THIS file alone; the player code
// won't move an inch.
public sealed class GrindRail : Component
{
	// The ordered points the rail runs through (world-positioned GameObjects, e.g. empties
	// you place as children). Order matters — the rail goes Node[0] → Node[last].
	[Property] public List<GameObject> Nodes { get; set; } = new();

	// If a LineRenderer is on this object, draw the rail through the nodes so it's visible
	// in-game. Purely cosmetic — attachment is by proximity, not by this line.
	[Property] public bool DriveLineRenderer { get; set; } = true;

	private LineRenderer line;

	public int NodeCount => Nodes?.Count ?? 0;
	public bool IsUsable => NodeCount >= 2;

	protected override void OnStart()
	{
		if ( !DriveLineRenderer ) return;

		line = Components.Get<LineRenderer>();
		if ( line is null ) return;

		line.Points ??= new();
		line.Points.Clear();
		foreach ( var n in Nodes )
			if ( n is not null )
				line.Points.Add( n );
	}

	// Total length of the polyline (sum of segment lengths). Computed live so moving a node
	// in the editor is reflected immediately; rails have few nodes, so this is cheap.
	public float Length
	{
		get
		{
			float total = 0f;
			for ( int i = 0; i < NodeCount - 1; i++ )
			{
				if ( Nodes[i] is null || Nodes[i + 1] is null ) continue;
				total += Vector3.DistanceBetween( Nodes[i].WorldPosition, Nodes[i + 1].WorldPosition );
			}
			return total;
		}
	}

	// Closest point on the rail to `pos`. Returns false if the rail is unusable.
	//   distanceAlong : arc length from Node[0] to the closest point
	//   point         : the closest world point on the rail
	//   tangent       : unit travel direction (toward higher distanceAlong) there
	//   perpDistance  : how far `pos` sits from the rail
	public bool FindClosest( Vector3 pos, out float distanceAlong, out Vector3 point, out Vector3 tangent, out float perpDistance )
	{
		distanceAlong = 0f;
		point = pos;
		tangent = Vector3.Forward;
		perpDistance = float.MaxValue;

		if ( !IsUsable ) return false;

		float cumulative = 0f;
		bool found = false;

		for ( int i = 0; i < NodeCount - 1; i++ )
		{
			if ( Nodes[i] is null || Nodes[i + 1] is null ) continue;

			Vector3 a = Nodes[i].WorldPosition;
			Vector3 b = Nodes[i + 1].WorldPosition;
			Vector3 ab = b - a;
			float segLen = ab.Length;
			if ( segLen < 0.001f ) continue;

			Vector3 dir = ab / segLen;
			// Project pos onto the segment, clamped to its ends.
			float t = MathX.Clamp( Vector3.Dot( pos - a, dir ), 0f, segLen );
			Vector3 proj = a + dir * t;
			float pd = Vector3.DistanceBetween( pos, proj );

			if ( pd < perpDistance )
			{
				perpDistance = pd;
				point = proj;
				tangent = dir;
				distanceAlong = cumulative + t;
				found = true;
			}

			cumulative += segLen;
		}

		return found;
	}

	// World point at the given arc length (clamped to the rail).
	public Vector3 GetPoint( float distanceAlong )
	{
		if ( !IsUsable ) return WorldPosition;
		distanceAlong = MathX.Clamp( distanceAlong, 0f, Length );

		float cumulative = 0f;
		for ( int i = 0; i < NodeCount - 1; i++ )
		{
			if ( Nodes[i] is null || Nodes[i + 1] is null ) continue;

			Vector3 a = Nodes[i].WorldPosition;
			Vector3 b = Nodes[i + 1].WorldPosition;
			float segLen = Vector3.DistanceBetween( a, b );
			if ( segLen < 0.001f ) continue;

			if ( distanceAlong <= cumulative + segLen )
			{
				float t = (distanceAlong - cumulative) / segLen;
				return Vector3.Lerp( a, b, t );
			}

			cumulative += segLen;
		}

		return LastNode();
	}

	// Unit travel direction at the given arc length (toward higher distanceAlong).
	public Vector3 GetTangent( float distanceAlong )
	{
		if ( !IsUsable ) return Vector3.Forward;
		distanceAlong = MathX.Clamp( distanceAlong, 0f, Length );

		float cumulative = 0f;
		Vector3 lastDir = Vector3.Forward;
		for ( int i = 0; i < NodeCount - 1; i++ )
		{
			if ( Nodes[i] is null || Nodes[i + 1] is null ) continue;

			Vector3 ab = Nodes[i + 1].WorldPosition - Nodes[i].WorldPosition;
			float segLen = ab.Length;
			if ( segLen < 0.001f ) continue;

			lastDir = ab / segLen;
			if ( distanceAlong <= cumulative + segLen )
				return lastDir;

			cumulative += segLen;
		}

		return lastDir;
	}

	private Vector3 LastNode()
	{
		for ( int i = NodeCount - 1; i >= 0; i-- )
			if ( Nodes[i] is not null )
				return Nodes[i].WorldPosition;
		return WorldPosition;
	}
}
