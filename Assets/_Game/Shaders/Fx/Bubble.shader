// Bubble Guard force field / Bubble Blower bubbles (port of BUBBLE_VS / BUBBLE_FS in specials.js): a team-tinted
// iridescent rim, a slow swirl and a specular glint. _Color linear; _Time2 drives the swirl, _Alpha fades / blinks it,
// _Hit flashes it white on a hit. Alpha blended, no depth write.
Shader "Inkwave/Fx/Bubble"
{
    Properties
    {
        _Color ("Colour (linear)", Vector) = (1, 1, 1, 1)
        _Time2 ("Time", Float) = 0
        _Alpha ("Alpha", Float) = 1
        _Hit ("Hit", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }
        Pass
        {
            Name "Bubble"
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
                float _Time2, _Alpha, _Hit;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 n : TEXCOORD0; float3 v : TEXCOORD1; float3 p : TEXCOORD2; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                float3 ws = TransformObjectToWorld(i.positionOS.xyz);
                float3 vs = TransformWorldToView(ws);
                o.positionCS = TransformWViewToHClip(vs);
                o.n = TransformWorldToViewDir(TransformObjectToWorldNormal(i.normalOS));   // view space, as three's normalMatrix
                o.v = -vs;
                o.p = i.positionOS.xyz;
                return o;
            }
            float4 frag(Varyings i) : SV_Target
            {
                float3 n = normalize(i.n);
                float f = 1.0 - abs(dot(n, normalize(i.v)));
                float rim = pow(f, 2.2);
                float3 irid = 0.5 + 0.5 * cos(6.2831 * (f * 1.3 + _Time2 * 0.15 + float3(0.0, 0.33, 0.67)));
                float swirl = 0.5 + 0.5 * sin(i.p.y * 5.0 + i.p.x * 3.0 + _Time2 * 2.0);
                float3 c = lerp(_Color.rgb, irid, 0.35 * rim) + _Color.rgb * swirl * 0.15 + _Hit;
                float hl = pow(max(0.0, dot(n, normalize(float3(-0.4, 0.7, 0.6)))), 40.0);
                float a = (0.1 + 0.75 * rim + 0.2 * swirl * rim + _Hit * 0.5) * _Alpha + hl * 0.8;
                return float4(c + hl, saturate(a));
            }
            ENDHLSL
        }
    }
}
