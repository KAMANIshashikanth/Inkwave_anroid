// The Brine Cutlass's charge aura (port of kits/blade-model.js makeAuraMaterial): the blade body inflated a hair, additive;
// a shimmer band sweeps hilt → tip while charging, the steel glows at full. _P = charge, full, flash, time; _Color linear.
// Weapon space: the blade runs along +Z from z0 0.092 to the tip at 0.735.
Shader "Inkwave/Fx/BladeAura"
{
    Properties
    {
        _Color ("Colour (linear)", Vector) = (1, 1, 1, 1)
        _P ("Charge, full, flash, time", Vector) = (0, 0, 0, 0)
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }
        Pass
        {
            Name "BladeAura"
            Tags { "LightMode" = "UniversalForward" }
            Blend One One
            ZWrite Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color, _P;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings { float4 positionCS : SV_POSITION; float z : TEXCOORD0; float3 n : TEXCOORD1; float3 v : TEXCOORD2; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                float3 ws = TransformObjectToWorld(i.positionOS.xyz + i.normalOS * 0.0016);
                o.positionCS = TransformWorldToHClip(ws);
                o.z = i.positionOS.z;
                o.n = TransformObjectToWorldNormal(i.normalOS);
                o.v = GetWorldSpaceViewDir(ws);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float uCharge = _P.x, uFull = _P.y, uFlash = _P.z, uTime = _P.w;
                float u = saturate((i.z - 0.092) / 0.643);
                float band = exp(-pow((frac(uTime * 1.6) * 1.4 - 0.2 - u) * 9.0, 2.0));
                float rim = pow(1.0 - abs(dot(normalize(i.n), normalize(i.v))), 2.0);
                float a = uCharge * (0.12 + 0.55 * band) * step(u, uCharge + 0.04) + uFull * (0.25 + 0.35 * rim + 0.4 * band) + uFlash * 0.9;
                float3 col = lerp(_Color.rgb, float3(1, 1, 1), 0.35 + 0.4 * band + 0.5 * uFlash) * a * 1.6;
                return half4(col, 1);
            }
            ENDHLSL
        }
    }
}
