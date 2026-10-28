// A standard lit material with one thing added: it can be washed over with a flat colour.
//
// HitFlash (0-1) is how far the final, lit colour is pushed toward HitFlashColor. At 1 the model is that colour
// outright, whatever its texture or lighting - which is what a tint can't do, since a tint only multiplies and
// leaves the dark parts dark. Both are set per model from code (HitFlash.cs), so every enemy flashes on its own.
//
// To use it: set a material's shader to this one. Its texture slots are the standard ones, so an existing
// Complex material keeps its colour, normal, roughness and AO textures.

HEADER
{
	Description = "Standard lit material that can be washed over with a flat colour from code - the hit flash";
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
	Depth();
}

COMMON
{
	#include "common/shared.hlsl"
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"
};

VS
{
	#include "common/vertex.hlsl"

	PixelInput MainVs( VertexInput i )
	{
		PixelInput o = ProcessVertex( i );
		return FinalizeVertex( o );
	}
}

PS
{
	// the standard texture slots - Color, Normal, Roughness, Metalness, AO and the rest - the same names Complex
	// uses, so an existing material keeps its textures when it's switched over
	#include "common/utils/Material.CommonInputs.hlsl"
	#include "common/pixel.hlsl"

	RenderState( CullMode, F_RENDER_BACKFACES ? NONE : DEFAULT );

	float3 g_vHitFlashColor < Attribute( "HitFlashColor" ); Default3( 1.0, 0.0, 0.0 ); >;
	float g_flHitFlash < Attribute( "HitFlash" ); Default( 0.0 ); >;

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		Material m = Material::From( i );
		float4 color = ShadingModelStandard::Shade( i, m );

		color.rgb = lerp( color.rgb, g_vHitFlashColor, saturate( g_flHitFlash ) );
		return color;
	}
}
