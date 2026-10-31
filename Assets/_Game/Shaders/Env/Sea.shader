// Port of environment.js SEA_VERT / SEA_FRAG (open sea; the marina water of Halyard is not ported yet) for a baked
// stage. Vertex: the long swell (three sines, calmer near the deck via the baked deck distance field). Fragment: wave
// texture normals at four scales, turquoise shallows by the pier, lit crests, sky-gradient reflection with Fresnel,
// sun glints, the real sun shadow + the analytic deck-slab shadow, lapping foam along the deck edge and rings round
// pilings / boats (foam field), aerial haze. All in three space (the env uniforms are three-space values; vWorld =
// Unity world with z negated). Globals (uZenith, uSeaDeep, … uWaveTex, uFoamTex, uDeckField) come from
// World/StageEnvironment.cs, uTime = _StageTime (World/StageAnimator.cs). Linear HDR out, no fog (three: fog off).
Shader "Inkwave/Env/Sea"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #define MIR float3(1.0, 1.0, -1.0)
        float _StageTime;
        float4 uSunDir, uZenith, uSkyMid, uHorizon, uGround, uHorizonGlow, uGlowColor, uGlowParams, uHaze;
        float uNight;
        float4 uSeaDeep, uSeaShallow, uSeaCrest, uFoamColor, uSunLight, uSeaAmbient, uFoamRect, uDeckFieldRect, uDeckFieldK, uDeckBox;
        float uSunSpec, uWaveStrength;
        TEXTURE2D(uWaveTex); SAMPLER(sampler_uWaveTex);
        TEXTURE2D(uFoamTex); SAMPLER(sampler_uFoamTex);
        TEXTURE2D(uDeckField); SAMPLER(sampler_uDeckField);

        // ---- GLSL_DECK (DECK_FIELD)
        float sdRect(float2 p, float4 r)
        {
            float2 c = (r.xy + r.zw) * 0.5, h = (r.zw - r.xy) * 0.5;
            float2 q = abs(p - c) - h;
            return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0);
        }
        float deckField(float2 p) { return SAMPLE_TEXTURE2D_LOD(uDeckField, sampler_uDeckField, saturate((p - uDeckFieldRect.xy) * uDeckFieldRect.zw), 0).r; }
        float sdDeck(float2 p) { return max(deckField(p), sdRect(p, uDeckBox)); }
        // ---- GLSL_SWELL
        float swell(float2 p, float t, out float2 g)
        {
            float h = 0.0; g = 0.0;
            float2 k; float ph;
            k = float2(0.110, 0.047); ph = dot(p, k) + t * 0.95;        h += 0.45 * sin(ph); g += 0.45 * k * cos(ph);
            k = float2(-0.052, 0.097); ph = dot(p, k) + t * 1.13 + 1.7; h += 0.35 * sin(ph); g += 0.35 * k * cos(ph);
            k = float2(0.173, -0.141); ph = dot(p, k) + t * 1.61 + 4.1; h += 0.20 * sin(ph); g += 0.20 * k * cos(ph);
            return h;
        }
        float swellAmp(float dDeck) { return lerp(0.035, 0.16, smoothstep(3.0, 70.0, dDeck)); }

        struct A { float4 positionOS : POSITION; };
        struct V { float4 positionCS : SV_POSITION; float3 world : TEXCOORD0; float2 swellGrad : TEXCOORD1; float deckD : TEXCOORD2; float3 positionWS : TEXCOORD3; };
        V seaVert(A i)
        {
            V o;
            float3 w = TransformObjectToWorld(i.positionOS.xyz) * MIR;   // three space
            float dd = sdDeck(w.xz);
            float amp = swellAmp(dd);
            float2 g;
            float h = swell(w.xz, _StageTime, g);
            w.y += h * amp;
            o.world = w; o.swellGrad = g * amp; o.deckD = dd;
            o.positionWS = w * MIR;
            o.positionCS = TransformWorldToHClip(o.positionWS);
            return o;
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex seaVert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // ---- GLSL_SKY_COMMON
            float3 skyGradient(float3 d)
            {
                float h = d.y;
                float hp = max(h, 0.0);
                float t = sqrt(hp);
                float3 c = lerp(uHorizon.rgb, uSkyMid.rgb, smoothstep(0.0, 0.52, t));
                c = lerp(c, uZenith.rgb, smoothstep(0.4, 1.0, t));
                float2 sh = normalize(uSunDir.xz + 1e-5);
                float2 dh = normalize(d.xz + 1e-5);
                float az = dot(sh, dh) * 0.5 + 0.5;
                float band = exp(-hp * uGlowParams.z);
                c += uHorizonGlow.rgb * band * (0.2 + 0.8 * az * az * az);
                float sd = max(dot(normalize(float3(d.x, hp, d.z)), uSunDir.xyz), 0.0);
                c += uGlowColor.rgb * (pow(sd, uGlowParams.x) * uGlowParams.y + pow(sd, 6.0) * uGlowParams.w);
                float anti = (1.0 - az) * (1.0 - az);
                c *= lerp(1.0, float3(0.7, 0.74, 0.95), uNight * anti * exp(-hp * 2.5));
                c = lerp(c, uGround.rgb, smoothstep(0.0, -0.22, h));
                return c;
            }
            float3 hazeColor(float3 d)
            {
                float3 h = skyGradient(normalize(float3(d.x, saturate(d.y) * 0.3, d.z)));
                h = lerp(h, uSkyMid.rgb * 1.08 + uHorizon.rgb * 0.12, 0.22);
                return lerp(h, uGround.rgb * 1.15 + uSkyMid.rgb * 0.25, smoothstep(-0.02, -0.45, d.y) * 0.75);
            }
            float3 applyHaze(float3 col, float3 wp, float3 camPos)
            {
                float3 dv = wp - camPos;
                float dist = length(dv);
                float3 dir = dv / max(dist, 1e-3);
                float hf = exp(-max(wp.y, 0.0) / uHaze.z);
                float f = 1.0 - exp(-dist * uHaze.x * lerp(0.45, 1.0, hf));
                return lerp(col, hazeColor(dir), min(f, uHaze.y));
            }

            half4 frag(V i) : SV_Target
            {
                float uTime = _StageTime;
                float3 P = i.world;
                float3 camPos = _WorldSpaceCameraPos * MIR;
                float3 toCam = camPos - P;
                float dist = length(toCam);
                float3 Vd = toCam / dist;
                float2 p = P.xz;
                float detail = 1.0 - smoothstep(35.0, 380.0, dist);

                float4 w1 = SAMPLE_TEXTURE2D(uWaveTex, sampler_uWaveTex, p * 0.041 + uTime * float2(0.012, 0.007));
                float4 w2 = SAMPLE_TEXTURE2D(uWaveTex, sampler_uWaveTex, p * 0.097 + float2(0.37, 0.61) + uTime * float2(-0.019, 0.013));
                float4 w3 = SAMPLE_TEXTURE2D(uWaveTex, sampler_uWaveTex, p * 0.0083 + uTime * float2(0.0034, -0.0022));
                float nearF = 1.0 - smoothstep(6.0, 40.0, dist);
                float4 w4 = SAMPLE_TEXTURE2D(uWaveTex, sampler_uWaveTex, p * 0.29 + float2(0.71, 0.13) + uTime * float2(0.031, -0.026));
                float2 g = (w3.xy - 0.5) * 0.9 + ((w1.xy - 0.5) * 0.85 + (w2.xy - 0.5) * 0.55) * lerp(0.3, 1.0, detail) + (w4.xy - 0.5) * 0.45 * nearF;
                g = g * uWaveStrength * 0.42 + i.swellGrad;
                float3 N = normalize(float3(-g.x, 1.0, -g.y));
                float crest = (w1.z * 0.9 + w2.z * 0.5 + w3.z * 0.9) / 2.3;

                float dEdge = sdDeck(p);
                // the sea never shows through inside the arena footprint (e.g. dry-dock trenches below sea level)
                clip(dEdge + 0.05);
                float2 fuv = (p - uFoamRect.xy) * uFoamRect.zw;
                float inField = step(0.0, fuv.x) * step(fuv.x, 1.0) * step(0.0, fuv.y) * step(fuv.y, 1.0);
                float dObj = lerp(8.0, SAMPLE_TEXTURE2D(uFoamTex, sampler_uFoamTex, saturate(fuv)).r * 8.0, inField);

                // shadows: real shadow map + the analytic deck-slab shadow outside the shadow camera
                float sm = MainLightRealtimeShadow(TransformWorldToShadowCoord(i.positionWS));
                float2 q = p + uSunDir.xz * ((-0.6 - P.y) / max(uSunDir.y, 0.06));
                float ash = smoothstep(-0.2, 0.45, sdDeck(q));
                float shadow = min(sm, ash);
                float under = smoothstep(0.3, -2.4, dEdge);

                // body colour: turquoise shallows near the pier, deep blue further out; crests catch light
                float shallow = smoothstep(8.0, 0.5, dEdge) * (0.7 + 0.6 * (w3.z - 0.5));
                float3 body = lerp(uSeaDeep.rgb, uSeaShallow.rgb, saturate(shallow) * 0.6);
                body += uSeaCrest.rgb * smoothstep(0.55, 0.95, crest) * (0.3 + 0.7 * shadow) * 0.4 * lerp(0.25, 1.0, detail);
                float NdL = max(dot(N, uSunDir.xyz), 0.0);
                float3 lit = body * (uSeaAmbient.rgb + uSunLight.rgb * (0.4 + 0.6 * NdL) * shadow * 0.55);

                // reflection of the sky
                float3 R = reflect(-Vd, N);
                R.y = abs(R.y);
                float3 refl = skyGradient(normalize(R + float3(0.0, 0.015, 0.0)));
                float NdV = saturate(dot(N, Vd));
                float fres = 0.035 + 0.965 * pow(1.0 - NdV, 5.0);
                fres = min(fres * 1.1, 1.0);
                float3 col = lerp(lit, refl, fres * lerp(1.0, 0.45, under));
                col *= lerp(1.0, 0.2, under);

                // sun glint: broad sheen + tight sparkles from the fine normals
                float rs = saturate(dot(R, uSunDir.xyz));
                float spec = pow(rs, 1600.0) * 9.0 + pow(rs, 200.0) * 0.38 + pow(rs, 24.0) * 0.04;
                col += uSunLight.rgb * spec * uSunSpec * shadow * (1.0 - under);

                // foam: lapping band along the deck edge, rings around pilings / boats / buoys
                float fn = w2.z * 0.6 + w1.z * 0.4;
                float edgeF = smoothstep(0.75, 0.0, dEdge + (fn - 0.5) * 0.9) * smoothstep(-1.0, -0.2, dEdge);
                float lapPhase = frac(dEdge * 0.7 - uTime * 0.3);
                float lap = smoothstep(0.1, 0.0, abs(lapPhase - 0.5) - 0.4) * smoothstep(3.4, 0.5, dEdge) * step(0.0, dEdge);
                lap *= smoothstep(0.35, 0.7, fn + 0.15);
                float objF = smoothstep(0.55, 0.0, dObj + (fn - 0.5) * 0.45);
                objF += 0.45 * smoothstep(0.1, 0.0, abs(frac(dObj * 0.9 - uTime * 0.35) - 0.5) - 0.42) * smoothstep(2.2, 0.4, dObj) * smoothstep(0.35, 0.7, fn + 0.1);
                float foam = saturate(edgeF + lap * 0.5 + objF) * lerp(1.0, 0.35, under);
                float3 foamCol = uFoamColor.rgb * (uSeaAmbient.rgb * 1.3 + uSunLight.rgb * (0.35 + 0.65 * shadow) * 0.6);
                col = lerp(col, foamCol, foam * detail);

                col = applyHaze(col, P, camPos);
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
            #pragma target 3.5
            #pragma vertex seaVert
            #pragma fragment frag
            half4 frag(V i) : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
