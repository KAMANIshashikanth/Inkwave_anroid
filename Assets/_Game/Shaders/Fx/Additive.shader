// Additive glow for small effect meshes (port of the MeshBasicMaterial / ShaderMaterial pair in kits/bow.js): _Mode 0 =
// a flat additive glow (arrow heads, lodged throb); _Mode 1 = a streak that fades back from its head along -Z (object
// z 0 → -1: a = (1 − f)²). Colours are linear and may run past 1 (toneMapped: false upstream); no depth write.
Shader "Inkwave/Fx/Additive"
{
    Properties
    {
        _Color ("Colour (linear)", Vector) = (1, 1, 1, 1)
        _Mode ("Mode", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }
        Pass
        {
            Name "Additive"
            Tags { "LightMode" = "UniversalForward" }
            Blend One One
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Mode;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float f : TEXCOORD0; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.f = saturate(-i.positionOS.z);
                return o;
            }
            half4 frag(Varyings i) : SV_Target
            {
                float a = 1.0;
                if (_Mode > 0.5) { a = 1.0 - i.f; a *= a; }
                return half4(_Color.rgb * a, a);
            }
            ENDHLSL
        }
    }
}
