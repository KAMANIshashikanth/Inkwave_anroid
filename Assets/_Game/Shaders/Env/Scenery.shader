// Port of environment.js patchScenery (HZ_* chunks on MeshStandardMaterial) for a baked stage: every far-scenery /
// dock / boat material. Lit through URP's PBR like Inkwave/Prop, then:
//   HZ_CITY        skyline facades (punched windows, ribbon bands, curtain walls, piers, bands) from the bld attribute,
//                  glass reflecting the sky gradient, lit windows at dusk, a stop darker at dusk
//   HZ_TERRAIN     hills: meadow, woodland canopy, dry tops, layered rock on steep faces, sand + wet sand at the shore,
//                  baked fold AO in glow (always with HZ_SHORE here: HZ_TERRAIN_SHORE)
//   HZ_WATERLINE   wet darkening + weed just above the water, reflected-sun caustics on the sides
//   HZ_SHORE       the wash line at the water's edge
//   HZ_GULL        wing flap / glide (aPhase)
// plus the night lights (glow attribute: lamps by uNight, > 1.5 = blinking beacons) added after the aerial haze so
// they punch through it. No Unity fog (three: fog off; the haze replaces it). Attributes (the bake sorts their names):
// bld TEXCOORD1 + glow TEXCOORD2 (city), aPhase TEXCOORD1 + glow TEXCOORD2 (gull), glow TEXCOORD1 (the rest).
Shader "Inkwave/Env/Scenery"
{
    Properties
    {
        _BaseColor ("Colour (linear)", Vector) = (1, 1, 1, 1)
        _UseVC ("Vertex colours", Float) = 1
        _Roughness ("Roughness", Float) = 1
        _Metallic ("Metalness", Float) = 0
        _Cull ("Cull", Float) = 2
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "EnvCommon.hlsl"
        #pragma multi_compile_local _ HZ_CITY HZ_TERRAIN_SHORE HZ_SHORE HZ_WATERLINE HZ_GULL
        #if HZ_TERRAIN_SHORE
            #define HZ_TERRAIN 1
            #define HZ_SHORE_ON 1
        #endif
        #if HZ_SHORE
            #define HZ_SHORE_ON 1
        #endif
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseColor;
            float _UseVC, _Roughness, _Metallic, _Cull;
        CBUFFER_END

        struct Attributes
        {
            float4 positionOS : POSITION; float3 normalOS : NORMAL; float4 color : COLOR;
            float4 a1 : TEXCOORD1; float4 a2 : TEXCOORD2;
        };
        float3 hzVertex(float3 p, Attributes i)
        {
        #if HZ_GULL
            float side = abs(p.x);
            float glide = 0.35 + 0.65 * smoothstep(-0.3, 0.6, sin(_StageTime * 0.55 + i.a1.x * 17.0));
            float flap = sin(_StageTime * 8.5 + i.a1.x * 6.2831);
            p.y += flap * glide * side * 0.62 * smoothstep(0.08, 0.35, side);
            p.y -= 0.12 * side * side;
        #endif
            return p;
        }
        float hzGlow(Attributes i)
        {
        #if HZ_CITY || HZ_GULL
            return i.a2.x;
        #else
            return i.a1.x;
        #endif
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull [_Cull]
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float4 color : TEXCOORD2;
                float3 hzWorld : TEXCOORD3;    // three space
                float3 hzNormal : TEXCOORD4;
                float glow : TEXCOORD5;
                float3 bld : TEXCOORD6;
            };

            Varyings vert(Attributes i)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(hzVertex(i.positionOS.xyz, i));
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(i.normalOS);
                o.color = i.color;
                o.hzWorld = p.positionWS * MIR;
                o.hzNormal = o.normalWS * MIR;
                o.glow = hzGlow(i);
                o.bld = i.a1.xyz;
                return o;
            }

            float aaBand(float x, float a, float b, float w) { return smoothstep(a - w, a + w, x) * smoothstep(b + w, b - w, x); }
            float aaLine(float x, float hw, float w) { float d = min(x, 1.0 - x); return smoothstep(hw + w, hw - w, d); }

            half4 frag(Varyings i, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target
            {
                float uTime = _StageTime;
                float3 diffuse = _BaseColor.rgb * (_UseVC > 0.5 ? i.color.rgb : float3(1, 1, 1));
                float rough = _Roughness;
                float vGlow = i.glow;
                float3 vHzWorld = i.hzWorld;
                float hzWin = 0.0;
                float3 hzLitCol = 0.0;
            #if HZ_CITY
                {
                    // facade styles (bld.x): 0 solid, 1 punched windows w/ mullion, 2 ribbon bands, 3 curtain wall, 4 vertical piers, 5 bands only
                    float3 wn = normalize(i.hzNormal);
                    float style = floor(i.bld.x + 0.5);
                    float seed = i.bld.y;
                    float v = vHzWorld.y - i.bld.z;
                    if (style > 0.5 && abs(wn.y) < 0.5)
                    {
                        float2 tdir = normalize(float2(-wn.z, wn.x));
                        float u = dot(vHzWorld.xz, tdir) + seed * 37.0;
                        float bay = style < 1.5 ? 3.3 : style < 2.5 ? 1.7 : style < 3.5 ? 1.5 : style < 4.5 ? 2.8 : 2.0;
                        float flH = style < 2.5 ? 3.5 : 3.9;
                        float2 cell = float2(u / bay, v / flH);
                        float2 f = frac(cell), id = floor(cell);
                        float2 fw = fwidth(cell);
                        float far = smoothstep(0.22, 0.75, max(fw.x, fw.y));
                        float glass = 0.0, mull = 0.0, avg = 0.5;
                        if (style < 1.5) { glass = aaBand(f.x, 0.2, 0.8, fw.x) * aaBand(f.y, 0.27, 0.86, fw.y); mull = aaBand(f.x, 0.485, 0.515, fw.x) * glass; avg = 0.6 * 0.59; }
                        else if (style < 2.5) { glass = aaBand(f.y, 0.3, 0.9, fw.y); mull = aaLine(f.x, 0.04, fw.x) * glass; avg = 0.58; }
                        else if (style < 3.5) { glass = 1.0; mull = max(aaLine(f.x, 0.035, fw.x), aaLine(f.y, 0.05, fw.y) * 0.8); avg = 0.86; }
                        else if (style < 4.5) { glass = aaBand(f.x, 0.32, 0.92, fw.x) * aaBand(f.y, 0.06, 0.97, fw.y); mull = aaLine(f.y, 0.04, fw.y) * glass; avg = 0.52; }
                        else { glass = aaBand(f.y, 0.36, 0.9, fw.y); avg = 0.54; }
                        glass = lerp(glass * (1.0 - mull), avg, far);
                        // ground-floor storefront band, and no glass right at the roofline
                        glass = lerp(glass, 0.75 * smoothstep(0.5, 0.9, v), 1.0 - smoothstep(3.6, 4.2, v));
                        float r = hash12(id + seed * 113.0);
                        float3 gcol = lerp(float3(0.11, 0.16, 0.23), float3(0.2, 0.27, 0.33), r);
                        if (style > 2.5 && style < 3.5) gcol = lerp(float3(0.24, 0.37, 0.47), float3(0.34, 0.47, 0.55), r * 0.7) * lerp(0.9, 1.08, frac(seed * 7.1));
                        gcol = lerp(gcol, float3(0.22, 0.29, 0.36), far);
                        diffuse = lerp(diffuse, gcol, glass);
                        diffuse *= 1.0 - mull * 0.3 * (1.0 - far);
                        diffuse *= 1.0 - 0.07 * aaLine(f.y, 0.025, fw.y) * (1.0 - glass) * (1.0 - far);
                        hzWin = glass;
                        float lit = step(r, style > 2.5 && style < 3.5 ? 0.34 : 0.44);
                        lit = lerp(lit, 0.38, far);
                        float3 lc = lerp(float3(1.0, 0.72, 0.4), float3(0.8, 0.88, 1.0), step(0.78, hash12(id * 1.7 + seed)));
                        hzLitCol = lc * lit * glass;
                    }
                }
                rough = lerp(rough, 0.14, hzWin);
            #endif
            #if HZ_TERRAIN
                {
                    float3 wn = normalize(i.hzNormal);
                    float hgt = vHzWorld.y - WATER_Y;
                    float2 tp = vHzWorld.xz;
                    float n1 = fbm2(tp * 0.011);
                    float n2 = vnoise2(tp * 0.05) * 0.6 + vnoise2(tp * 0.17) * 0.4;
                    float nt = vnoise2(tp * 0.3) * 0.55 + vnoise2(tp * 0.83 + 7.0) * 0.45;
                    float3 tint = diffuse;
                    float slope = 1.0 - wn.y;
                    float3 grass = tint * lerp(0.9, 1.08, n1);
                    float wood = smoothstep(0.47, 0.62, n1 + (n2 - 0.5) * 0.3) * smoothstep(0.62, 0.3, slope) * smoothstep(1.5, 5.0, hgt);
                    float3 canopy = tint * float3(0.5, 0.66, 0.52) * lerp(0.68, 1.14, smoothstep(0.3, 0.78, nt));
                    grass = lerp(grass, canopy, wood * 0.92);
                    grass = lerp(grass, tint * float3(1.12, 1.06, 0.8), smoothstep(0.34, 0.12, n1) * (1.0 - wood) * 0.5);
                    float3 rock = float3(0.56, 0.55, 0.51) * (0.82 + 0.3 * n2);
                    rock *= 0.9 + 0.1 * sin(vHzWorld.y * 0.85 + n2 * 5.0);
                    float rockM = smoothstep(0.34, 0.54, slope + (n2 - 0.5) * 0.2) * 0.88;
                    float3 sand = float3(0.95, 0.88, 0.72) * (0.95 + 0.08 * n2);
                    float sandM = smoothstep(1.6, 0.6, hgt + (n1 - 0.5) * 1.6);
                    float3 c = lerp(grass, rock, rockM);
                    c = lerp(c, sand, sandM);
                    c = lerp(c, sand * float3(0.72, 0.7, 0.66), smoothstep(0.45, 0.1, hgt) * sandM);
                    diffuse = c * vGlow;
                }
            #endif
            #if HZ_WATERLINE
                {
                    float wl = vHzWorld.y - WATER_Y;
                    diffuse *= lerp(0.5, 1.0, smoothstep(0.0, 0.45, wl));
                    diffuse = lerp(diffuse, float3(0.16, 0.27, 0.18), smoothstep(0.3, 0.0, wl) * 0.55);
                }
            #endif
            #if HZ_SHORE_ON
                {
                    float wl = vHzWorld.y - WATER_Y;
                    float wob = 0.12 * sin(uTime * 1.2 + vHzWorld.x * 0.21 + vHzWorld.z * 0.17);
                    diffuse = lerp(diffuse, float3(0.95, 0.97, 0.98), smoothstep(0.45, 0.05, abs(wl - 0.12 - wob)) * 0.85);
                }
            #endif
                // emissive: night lights (after the haze) + waterline caustics (lit)
                float3 hzEmit = 0.0, emission = 0.0;
                {
                    float blink = step(0.55, frac(uTime * 0.7 + vHzWorld.x * 0.013 + vHzWorld.z * 0.007));
                #if !HZ_TERRAIN
                    float gAmt = vGlow > 1.5 ? blink * 1.5 : vGlow;
                    hzEmit += i.color.rgb * gAmt * (vGlow > 1.5 ? max(uNight, 0.35) : uNight) * 7.0;
                #endif
                #if HZ_CITY
                    hzEmit += uNight * hzLitCol * 1.5;
                #endif
                #if HZ_WATERLINE
                    float wl2 = vHzWorld.y - WATER_Y;
                    float2 cq = vHzWorld.xz * 1.7 + float2(vHzWorld.y * 2.3, 0.0);
                    float cz = sin(cq.x + uTime * 1.3 + sin(cq.y * 1.3 + uTime * 0.9) * 1.6) * sin(cq.y * 1.1 - uTime * 1.1 + sin(cq.x * 0.9 - uTime * 0.7) * 1.4);
                    float side = 1.0 - abs(normalize(i.hzNormal).y);
                    emission += uGlowColor.rgb * 0.07 * side * smoothstep(0.45, 0.95, cz) * smoothstep(1.1, 0.15, wl2) * step(0.0, wl2);
                #endif
                }

                float3 n = normalize(i.normalWS) * IS_FRONT_VFACE(face, 1.0, -1.0);
                InputData inputData = (InputData)0;
                inputData.positionWS = i.positionWS;
                inputData.positionCS = i.positionCS;
                inputData.normalWS = n;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(i.positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                inputData.bakedGI = SampleSH(n);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);
                SurfaceData surf = (SurfaceData)0;
                surf.albedo = diffuse;
                surf.metallic = _Metallic;
                surf.smoothness = 1.0 - rough;
                surf.normalTS = float3(0, 0, 1);
                surf.occlusion = 1;
                surf.alpha = 1;
                surf.emission = emission;
                float3 col = UniversalFragmentPBR(inputData, surf).rgb;
            #if HZ_CITY
                {
                    // glass reflects the sky gradient (grazing angles → mirror-bright), so towers pick up the sky's colours
                    float3 wn2 = normalize(i.hzNormal);
                    float3 Vv = normalize(vHzWorld - threeCameraPos());
                    float3 Rr = reflect(Vv, wn2);
                    float fres = 0.05 + 0.95 * pow(1.0 - saturate(dot(-Vv, wn2)), 5.0);
                    float3 refl = min(skyGradient(normalize(float3(Rr.x, max(Rr.y, 0.02), Rr.z))), 1.1);
                    col = lerp(col, refl * lerp(0.92, 0.55, uNight), hzWin * clamp(0.18 + fres * 0.75, 0.0, 0.85) * (1.0 - uNight * 0.75));
                    // dusk: the skyline across the bay settles a stop darker so its lit windows carry it
                    col *= 1.0 - 0.38 * uNight;
                }
            #endif
                col = applyHaze(col, vHzWorld) + hzEmit * exp(-length(vHzWorld - threeCameraPos()) * uHaze.x * 0.35);
                return half4(col, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0
            Cull [_Cull]
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection;
            float3 _LightPosition;
            float4 vert(Attributes i) : SV_POSITION
            {
                float3 ws = TransformObjectToWorld(hzVertex(i.positionOS.xyz, i));
                float3 nws = TransformObjectToWorldNormal(i.normalOS);
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 dir = normalize(_LightPosition - ws);
            #else
                float3 dir = _LightDirection;
            #endif
                float4 cs = TransformWorldToHClip(ApplyShadowBias(ws, nws, dir));
            #if UNITY_REVERSED_Z
                cs.z = min(cs.z, UNITY_NEAR_CLIP_VALUE);
            #else
                cs.z = max(cs.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                return cs;
            }
            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On ColorMask R
            Cull [_Cull]
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 vert(Attributes i) : SV_POSITION { return TransformObjectToHClip(hzVertex(i.positionOS.xyz, i)); }
            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
