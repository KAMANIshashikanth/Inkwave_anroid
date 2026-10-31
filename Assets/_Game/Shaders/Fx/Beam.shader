// Howl Box sound beam (port of SWIRL_VS + BEAM_FS in specials.js, with bottom = top = 1 and no wobble): rings racing
// along an open cylinder (uv.y 0 at the mouth, 1 at the far end), faint until _Charge = 1. _Color linear. Additive, no
// depth write, both sides.
Shader "Inkwave/Fx/Beam"
{
    Properties
    {
        _Color ("Colour (linear)", Vector) = (1, 1, 1, 1)
        _Time2 ("Time", Float) = 0
        _Alpha ("Alpha", Float) = 1
        _Charge ("Charge", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }
        Pass
        {
            Name "Beam"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha One
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Time2, _Alpha, _Charge;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float rim : TEXCOORD1; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                o.uv = i.uv;
                float3 vs = TransformWorldToView(TransformObjectToWorld(i.positionOS.xyz));
                float3 n = normalize(TransformWorldToViewDir(TransformObjectToWorldNormal(i.normalOS)));
                o.rim = 1.0 - abs(dot(n, normalize(-vs)));
                o.positionCS = TransformWViewToHClip(vs);
                return o;
            }
            float4 frag(Varyings i) : SV_Target
            {
                float r = frac(i.uv.y * 26.0 - _Time2 * 5.0);
                float ring = smoothstep(0.0, 0.08, r) * smoothstep(0.35, 0.12, r);
                float edge = smoothstep(0.0, 0.03, i.uv.y) * smoothstep(1.0, 0.85, i.uv.y);
                float a = lerp(0.12 + 0.5 * i.rim, (0.25 + 0.6 * ring) * (0.45 + 0.55 * i.rim), _Charge) * edge * _Alpha;
                float3 c = lerp(_Color.rgb, _Color.rgb * 1.6 + 0.25, ring * _Charge);
                return float4(c, saturate(a));
            }
            ENDHLSL
        }
    }
}
