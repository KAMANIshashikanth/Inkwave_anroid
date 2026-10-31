// Port of decor.js PAD_FRAG: a team's spawn pad face — glossy dark disc with a sky band, a slowly swirling pool of the
// team's ink with the white squid emblem, glowing rims / dashed rotating track / ticks / outward chevrons, idle
// breathing and the spawn pulse wave, a metal lip. Driven by World/StageDecor.cs (_Color linear team colour, _Pulse,
// _PulseT); time = _StageTime. Linear HDR out (tone mapped by the grade pass, as three's OUT chunk does).
Shader "Inkwave/Env/SpawnPad"
{
    Properties
    {
        _Color ("Team colour (linear)", Vector) = (1, 1, 1, 1)
        _Pulse ("Pulse", Float) = 0
        _PulseT ("Pulse time", Float) = 1
        _Emblem ("Emblem", 2D) = "black" {}
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        Pass
        {
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_Emblem); SAMPLER(sampler_Emblem);
            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Pulse, _PulseT;
            CBUFFER_END
            float _StageTime;
            float dHash(float2 p) { float3 p3 = frac(p.xyx * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return frac((p3.x + p3.y) * p3.z); }
            float dNoise(float2 p)
            {
                float2 i = floor(p), f = frac(p); float2 u = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(dHash(i), dHash(i + float2(1.0, 0.0)), u.x), lerp(dHash(i + float2(0.0, 1.0)), dHash(i + float2(1.0, 1.0)), u.x), u.y);
            }
            float glmod(float x, float y) { return x - y * floor(x / y); }
            struct A { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct V { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float3 w : TEXCOORD1; };
            V vert(A i) { V o; o.w = TransformObjectToWorld(i.positionOS.xyz); o.positionCS = TransformWorldToHClip(o.w); o.uv = i.uv; return o; }
            half4 frag(V i) : SV_Target
            {
                float uTime = _StageTime;
                float3 uColor = _Color.rgb;
                float2 q = i.uv * 2.0 - 1.0; float r = length(q); float a = atan2(q.y, q.x + 1e-5);
                clip(1.0 - r);
                float3 Vv = normalize(_WorldSpaceCameraPos - i.w);
                // glossy dark base with a soft sky reflection band
                float3 base = lerp(float3(0.035, 0.04, 0.055), float3(0.075, 0.085, 0.11), smoothstep(1.0, 0.0, r));
                float fres = pow(max(1.0 - saturate(Vv.y), 0.0), 3.0);
                base += float3(0.16, 0.2, 0.26) * fres;
                // pooled ink in the centre: slow liquid swirl, wet highlight
                float n = dNoise(q * 3.2 + float2(uTime * 0.25, -uTime * 0.18)) * 0.6 + dNoise(q * 7.0 - uTime * 0.3) * 0.4;
                float pool = smoothstep(0.58, 0.5, r + (n - 0.5) * 0.06);
                float3 ink = uColor * (0.42 + 0.3 * n) + 0.9 * pow(n, 7.0) * 1.4;
                base = lerp(base, ink, pool);
                // emblem (white squid) in the pool
                float2 eu = q / 0.46 * 0.5 + 0.5;
                float em = (eu.x > 0.0 && eu.x < 1.0 && eu.y > 0.0 && eu.y < 1.0) ? SAMPLE_TEXTURE2D_LOD(_Emblem, sampler_Emblem, eu, 0).a : 0.0;
                base = lerp(base, (0.9 + 0.2 * _Pulse).xxx, em * pool);
                // rings: bright outer rim, inner rim, dashed rotating track, radial ticks, chevrons pointing out
                float glow = 0.0;
                glow += smoothstep(0.022, 0.0, abs(r - 0.955)) * 1.3;
                glow += smoothstep(0.014, 0.0, abs(r - 0.6)) * 0.95;
                float dash = step(0.5, frac(a / 6.2831853 * 36.0 + uTime * 0.25));
                glow += smoothstep(0.018, 0.0, abs(r - 0.79)) * dash * 0.8;
                float tick = step(0.8, frac(a / 6.2831853 * 72.0)) * smoothstep(0.035, 0.0, abs(r - 0.88));
                glow += tick * 0.45;
                float sa = glmod(a - uTime * 0.35, 6.2831853 / 6.0) - 3.14159265 / 6.0;
                float2 pc = float2(r - 0.7, sa * 0.7);
                float chev = smoothstep(0.02, 0.0, abs(pc.x - abs(pc.y) * 0.9)) * step(abs(pc.y), 0.07) * step(-0.06, pc.x);
                glow += chev * 0.9;
                // breathing idle + spawn pulse wave
                glow *= 0.85 + 0.15 * sin(uTime * 2.2);
                float wave = _Pulse * smoothstep(0.09, 0.0, abs(r - _PulseT * 1.35)) * (1.0 - smoothstep(0.7, 1.35, _PulseT * 1.35));
                float3 col = base + uColor * glow * 2.6 + (uColor * 2.2 + 1.2) * wave * 2.2;
                col += uColor * pool * _Pulse * 0.8;
                // metal lip just inside the rim
                col = lerp(col, float3(0.5, 0.52, 0.56) * (0.6 + 0.6 * fres), smoothstep(0.975, 0.99, r));
                return half4(col, 1.0);
            }
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On ColorMask R
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            float4 vert(float4 p : POSITION) : SV_POSITION { return TransformObjectToHClip(p.xyz); }
            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
