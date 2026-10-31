// Whirl Boomerang spin blur (port of BLUR_VS / BLUR_FS in kits/boomerang.js): a smeared disc (uv 0..1 across the
// circle) with two swept-back blades, a cream-to-ink band and a bright tip rim; it fades in with the spin rate.
// _Color linear; _Alpha its opacity; _Ang the smear's turn. Double-sided, alpha blended, no depth write.
Shader "Inkwave/Fx/SpinBlur"
{
    Properties
    {
        _Color ("Colour (linear)", Vector) = (1, 1, 1, 1)
        _Alpha ("Alpha", Float) = 0
        _Ang ("Angle", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }
        Pass
        {
            Name "SpinBlur"
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
                float _Alpha;
                float _Ang;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.uv = i.uv;
                return o;
            }
            float4 frag(Varyings i) : SV_Target
            {
                float2 p = i.uv * 2.0 - 1.0; float r = length(p);
                clip(1.0 - r);
                float a = atan2(p.y, p.x) - _Ang;
                float arms = pow(0.5 + 0.5 * cos(2.0 * a + r * 2.6), 2.0);          // two smeared blades, swept back
                float band = smoothstep(1.0, 0.8, r) * smoothstep(0.1, 0.34, r);
                float ink = smoothstep(0.38, 0.56, r) * smoothstep(0.86, 0.66, r);
                float3 cream = float3(0.93, 0.9, 0.84);
                float3 c = lerp(cream, _Color.rgb * 1.15, 0.25 + 0.6 * ink);
                float al = _Alpha * band * (0.22 + 0.78 * arms);
                al += _Alpha * 0.5 * smoothstep(0.86, 0.95, r) * smoothstep(1.0, 0.95, r);   // the tips' bright rim
                return float4(c, al);
            }
            ENDHLSL
        }
    }
}
