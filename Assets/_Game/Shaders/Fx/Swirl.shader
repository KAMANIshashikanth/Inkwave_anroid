// Swirling funnel (port of SWIRL_VS / SWIRL_FS in specials.js: the Vortex Strike tornado, Twister Zooka shots): spiral
// streaks scrolling up an open cylinder (uv.y 0 at the bottom, 1 at the top), narrowed from _Bottom to _Top, wobbling,
// soft top / bottom, rim-brightened. _Color linear. Alpha blended, no depth write, both sides.
Shader "Inkwave/Fx/Swirl"
{
    Properties
    {
        _Color ("Colour (linear)", Vector) = (1, 1, 1, 1)
        _Time2 ("Time", Float) = 0
        _Alpha ("Alpha", Float) = 1
        _Speed ("Speed", Float) = 1.6
        _Bottom ("Bottom radius", Float) = 0.35
        _Top ("Top radius", Float) = 1
        _Wobble ("Wobble", Float) = 0.2
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }
        Pass
        {
            Name "Swirl"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Time2, _Alpha, _Speed, _Bottom, _Top, _Wobble;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float rim : TEXCOORD1; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                o.uv = i.uv;
                float3 p = i.positionOS.xyz;
                float k = lerp(_Bottom, _Top, pow(i.uv.y, 0.8));
                p.xz *= k;
                p.x += sin(_Time2 * 3.1 + i.uv.y * 5.0) * _Wobble * i.uv.y;
                p.z += cos(_Time2 * 2.7 + i.uv.y * 4.2) * _Wobble * i.uv.y;
                float3 vs = TransformWorldToView(TransformObjectToWorld(p));
                float3 n = normalize(TransformWorldToViewDir(TransformObjectToWorldNormal(i.normalOS)));
                o.rim = 1.0 - abs(dot(n, normalize(-vs)));
                o.positionCS = TransformWViewToHClip(vs);
                return o;
            }
            float4 frag(Varyings i) : SV_Target
            {
                float s = frac(i.uv.x * 4.0 + i.uv.y * 2.5 - _Time2 * _Speed);
                float band = smoothstep(0.0, 0.18, s) * smoothstep(0.62, 0.3, s);
                float s2 = frac(i.uv.x * 7.0 - i.uv.y * 1.5 - _Time2 * _Speed * 1.6);
                float band2 = smoothstep(0.0, 0.1, s2) * smoothstep(0.4, 0.2, s2);
                float fade = smoothstep(0.0, 0.12, i.uv.y) * smoothstep(1.0, 0.8, i.uv.y);
                float a = (0.28 + 0.5 * band + 0.25 * band2) * fade * (0.55 + 0.45 * i.rim) * _Alpha;
                float3 c = lerp(_Color.rgb * 0.7, _Color.rgb * 1.5 + 0.12, band * 0.8 + band2 * 0.4);
                return float4(c, saturate(a));
            }
            ENDHLSL
        }
    }
}
