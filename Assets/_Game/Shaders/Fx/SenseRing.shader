// Waddle Bomb sensing circle (port of RING_VS / RING_FS in kits/waddle.js): a team-coloured rim, a dashed inner ring, a
// radar sweep and a pulse on a flat quad (uv 0..1 → −1..1); _Grow 0..1 opens it, _Lock flashes it on a lock, _Sweep
// fades the sweep + pulse. _Color linear. Alpha blended, no depth write, pulled toward the camera (polygon offset).
Shader "Inkwave/Fx/SenseRing"
{
    Properties
    {
        _Color ("Colour (linear)", Vector) = (1, 1, 1, 1)
        _Time2 ("Time", Float) = 0
        _Alpha ("Alpha", Float) = 1
        _Grow ("Grow", Float) = 0
        _Lock ("Lock", Float) = 0
        _Sweep ("Sweep", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }
        Pass
        {
            Name "SenseRing"
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
                float _Time2, _Alpha, _Grow, _Lock, _Sweep;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.uv = i.uv * 2.0 - 1.0;
                return o;
            }
            float4 frag(Varyings i) : SV_Target
            {
                float r = length(i.uv) / max(_Grow, 0.001);
                clip(1.03 - r);
                float ang = atan2(i.uv.y, i.uv.x);
                float rim = smoothstep(0.03, 0.008, abs(r - 0.965));
                float dark = smoothstep(0.024, 0.0, abs(r - 1.0));
                float dash = step(0.5, frac(ang / 6.2831853 * 36.0 - _Time2 * 0.6)) * smoothstep(0.018, 0.005, abs(r - 0.9));
                float sw = frac(ang / 6.2831853 + _Time2 * 0.7);
                float wedge = pow(sw, 8.0) * step(r, 0.965) * (0.25 + 0.75 * r) * 0.6 * _Sweep;
                float ph = frac(_Time2 * 1.3);
                float wave = smoothstep(0.04, 0.0, abs(r - ph * 0.96)) * (1.0 - ph) * 0.55 * _Sweep;
                float fill = (0.07 + 0.25 * _Lock) * step(r, 0.965);
                float a = max(max(rim, dash * 0.75), max(max(wedge, wave), fill));
                float3 col = lerp(_Color.rgb, float3(1, 1, 1), rim * 0.3 + wedge * 0.25 + _Lock * 0.45);
                col = lerp(col, float3(0.03, 0.03, 0.03), dark * 0.7); a = max(a, dark * 0.45);
                return float4(col, a * _Alpha);
            }
            ENDHLSL
        }
    }
}
