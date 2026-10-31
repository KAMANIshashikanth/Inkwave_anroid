// Drip Curtain sheet (port of CURTAIN_VS / CURTAIN_FS in subs.js): a sheet of falling ink — scrolling vertical streaks,
// thicker at the top where it pours from, ragged bottom edge. _Color linear; _Time2 = the curtain's age (s); _Life 0..1
// fades it as its hp drains. Double-sided, alpha blended, no depth write.
Shader "Inkwave/Fx/Curtain"
{
    Properties
    {
        _Color ("Colour (linear)", Vector) = (1, 1, 1, 1)
        _Time2 ("Age", Float) = 0
        _Life ("Life", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }
        Pass
        {
            Name "Curtain"
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
                float _Time2;
                float _Life;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            float hash1(float n) { return frac(sin(n * 91.3458) * 47453.5453); }

            Varyings vert(Attributes i)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.uv = i.uv;
                return o;
            }
            float4 frag(Varyings i) : SV_Target
            {
                float2 uv = i.uv;
                float col = floor(uv.x * 46.0);
                float speed = 1.4 + hash1(col) * 1.2;
                float y = uv.y + _Time2 * speed * 0.55 + hash1(col + 3.1);
                float streak = smoothstep(0.35, 0.0, abs(frac(y * 3.0) - 0.5) - 0.15);
                float sheet = 0.42 + 0.35 * streak;
                float edge = smoothstep(0.0, 0.08, uv.x) * smoothstep(1.0, 0.92, uv.x);
                float bottom = smoothstep(0.0, 0.12 + 0.06 * hash1(col + 7.0), uv.y);
                float a = sheet * edge * bottom * (0.35 + 0.65 * _Life);
                float3 c = lerp(_Color.rgb * 0.7, _Color.rgb * 1.35 + 0.08, streak * 0.8 + uv.y * 0.2);
                return float4(c, a);
            }
            ENDHLSL
        }
    }
}
