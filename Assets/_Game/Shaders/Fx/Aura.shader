// Zipcaster shroud (port of AURA_VS / AURA_FS in specials.js): a sphere puffed along its normals into a shifting dark
// smoke with drifting patches and a thin violet-tinted team glow at the rim. _Color linear. Alpha blended, no depth write.
Shader "Inkwave/Fx/Aura"
{
    Properties
    {
        _Color ("Colour (linear)", Vector) = (1, 1, 1, 1)
        _Time2 ("Time", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }
        Pass
        {
            Name "Aura"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Time2;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 n : TEXCOORD0; float3 v : TEXCOORD1; float3 p : TEXCOORD2; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                float3 p = i.positionOS.xyz;
                p += i.normalOS * (0.1 * sin(_Time2 * 3.0 + p.y * 7.0 + atan2(p.z, p.x) * 3.0) + 0.05 * sin(_Time2 * 5.3 + p.y * 13.0));
                float3 vs = TransformWorldToView(TransformObjectToWorld(p));
                o.positionCS = TransformWViewToHClip(vs);
                o.n = TransformWorldToViewDir(TransformObjectToWorldNormal(i.normalOS));
                o.v = -vs;
                o.p = i.positionOS.xyz;
                return o;
            }
            float4 frag(Varyings i) : SV_Target
            {
                float f = 1.0 - abs(dot(normalize(i.n), normalize(i.v)));
                // drifting patches of dark smoke rising through the shroud, a thin violet-tinted glow at the edge
                float n = sin(i.p.x * 6.0 + _Time2 * 1.3) * sin(i.p.y * 5.0 - _Time2 * 2.4) * sin(i.p.z * 6.0 + _Time2 * 1.1);
                float n2 = sin((i.p.x + i.p.z) * 9.0 - _Time2 * 3.0 + i.p.y * 4.0);
                float smoke = saturate(0.45 + 0.7 * n + 0.25 * n2);
                float3 dark = float3(0.03, 0.0, 0.06);
                float3 glow = lerp(_Color.rgb, float3(0.62, 0.22, 1.0), 0.5) * 1.9;
                float rim = pow(f, 3.5);
                float3 c = lerp(dark, glow, rim * 0.9);
                float a = smoke * smoke * 0.6 * (0.3 + 0.7 * f) + rim * 0.45;
                return float4(c, clamp(a, 0.0, 0.9));
            }
            ENDHLSL
        }
    }
}
