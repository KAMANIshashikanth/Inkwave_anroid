// Tide Torpedo lock ring (port of RING_VS / RING_FS in kits/torpedo.js): a dashed team-coloured band, a thin inner
// ring, four ticks and a bright rim on a flat 2.3 m quad. _Time2 spins the dashes, _Alpha opens / pulses the ring.
// _Color linear. Alpha blended, no depth write, pulled toward the camera (polygon offset).
Shader "Inkwave/Fx/LockRing"
{
    Properties
    {
        _Color ("Colour (linear)", Vector) = (1, 1, 1, 1)
        _Time2 ("Time", Float) = 0
        _Alpha ("Alpha", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }
        Pass
        {
            Name "LockRing"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            Offset -2, -4

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Time2, _Alpha;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 p : TEXCOORD0; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.p = i.positionOS.xy;
                return o;
            }
            float4 frag(Varyings i) : SV_Target
            {
                float r = length(i.p) / 1.15;
                float a = atan2(i.p.y, i.p.x);
                float dash = step(0.45, frac(a / 6.2831853 * 16.0 - _Time2 * 0.55));
                float band = smoothstep(0.76, 0.79, r) * (1.0 - smoothstep(0.93, 0.96, r));
                float rim = smoothstep(0.93, 0.95, r) * (1.0 - smoothstep(0.97, 0.99, r));
                float inner = smoothstep(0.60, 0.62, r) * (1.0 - smoothstep(0.645, 0.665, r));
                float tick = step(0.92, frac(a / 6.2831853 * 4.0 + 0.125 - _Time2 * 0.12))
                    * smoothstep(0.46, 0.5, r) * (1.0 - smoothstep(0.72, 0.75, r));
                float al = (band * (1.0 - dash) + inner * 0.5 + tick * 0.95 + rim * (1.0 - dash) * 0.6) * _Alpha;
                clip(al - 0.01);
                float3 c = lerp(_Color.rgb * 1.3, float3(1, 1, 1), 0.12 + 0.3 * band * (1.0 - dash));
                return float4(c, al);
            }
            ENDHLSL
        }
    }
}
