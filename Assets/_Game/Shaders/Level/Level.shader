// Port of the level surface material (src/world/levelMaterial.js) with its wet-ink layer (src/world/inkShading.js).
//
// M1 scope: block colour + contact darkening, the full ink layer (B-spline atlas reconstruction close up, meniscus /
// team-seam height field → normal, fresh/dry gloss, ripples, lip / body colour, halo), lit through URP's PBR with a
// clear wet coat, baked AO. The texlib surface patterns, murals, swim wakes and lamps arrive with M4/M5.
//
// The mesh sits under the Z-mirrored world root, so Unity world space is three space mirrored: any cross product of
// world vectors flips sign — the face bitangent is -cross(N, T) here (three: cross(N, T)).
Shader "Inkwave/Level"
{
    Properties
    {
        _Paint ("Paint atlas", 2D) = "black" {}
        _AtlasSize ("Atlas size", Float) = 4096
        _InkGlow ("Ink glow", Float) = 0.06
        _BaseRough ("Base roughness", Float) = 0.82
        _Light ("Baked AO", 2D) = "white" {}
        _AO ("AO amount", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fog
            #pragma multi_compile_local_fragment _ INK_RIPPLES

            #define _CLEARCOAT 1
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            #define MIRROR_SIGN -1.0
            #define INK_RIPPLE_N 24

            TEXTURE2D(_Paint); SAMPLER(sampler_Paint);
            TEXTURE2D(_Light); SAMPLER(sampler_Light);   // baked AO (World/Lightmaps.cs): r = occlusion, through the face's light rect
            CBUFFER_START(UnityPerMaterial)
                float _AtlasSize;
                float _InkGlow;
                float _BaseRough;
                float _AO;
            CBUFFER_END
            float4 _TeamA, _TeamB;      // linear team colours (global per match)
            float _InkClock;
            float4 _Rip[INK_RIPPLE_N];  // xyz (Unity world) + birth time (paint clock)
            float4 _RipP[INK_RIPPLE_N]; // amplitude (m), wavelength (m), speed (m/s), life (s)

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 color : COLOR;
                float4 uv0 : TEXCOORD0;   // paintUv.xy, lightUv.xy
                float4 uv1 : TEXCOORD1;   // faceUv.xy, wall, groundedBottom
                float4 uv2 : TEXCOORD2;   // pattern, hasAtlas, su, sv
                float4 uv3 : TEXCOORD3;   // faceTan.xyz, mural
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 tanWS : TEXCOORD2;
                float4 color : TEXCOORD3;
                float4 uv0 : TEXCOORD4;
                float4 uv1 : TEXCOORD5;
                float4 uv2 : TEXCOORD6;
                float fogCoord : TEXCOORD7;
            };

            Varyings vert(Attributes i)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(i.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(i.normalOS);
                o.tanWS = TransformObjectToWorldDir(i.uv3.xyz);
                o.color = i.color;
                o.uv0 = i.uv0; o.uv1 = i.uv1; o.uv2 = i.uv2;
                o.fogCoord = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            float hash12(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
            float vnoise(float2 p)
            {
                float2 i = floor(p), f = frac(p); f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(hash12(i), hash12(i + float2(1, 0)), f.x), lerp(hash12(i + float2(0, 1)), hash12(i + float2(1, 1)), f.x), f.y);
            }

            // Paint lookups use gradients clamped to ≤ 8 texels: trilinear + anisotropic filtering (no shimmer at
            // distance) while never reaching mip levels coarse enough to bleed across the 8-texel face padding.
            static float2 gPdx, gPdy;
            float4 paintAt(float2 uv) { return SAMPLE_TEXTURE2D_GRAD(_Paint, sampler_Paint, uv, gPdx, gPdy); }
            // Close-up reconstruction of the paint atlas with a cubic B-spline (C2: smooth, round ink outlines and a smooth
            // height field at any magnification) plus its analytic gradient — 12 bilinear taps inside one 4×4 footprint.
            void inkBspl(float t, out float4 w, out float4 dw)
            {
                float t2 = t * t, t3 = t2 * t, it = 1.0 - t;
                w = float4(it * it * it, 3.0 * t3 - 6.0 * t2 + 4.0, -3.0 * t3 + 3.0 * t2 + 3.0 * t + 1.0, t3) * (1.0 / 6.0);
                dw = float4(-it * it, 3.0 * t2 - 4.0 * t, -3.0 * t2 + 2.0 * t + 1.0, t2) * 0.5;
            }
            float4 inkTap(float2 st) { return SAMPLE_TEXTURE2D_LOD(_Paint, sampler_Paint, (st + 0.5) / _AtlasSize, 0.0); }
            float4 paintCubic(float2 uv, out float4 gX, out float4 gY)
            {
                float2 st = uv * _AtlasSize - 0.5;
                float2 i = floor(st), f = st - i;
                float4 wx, dwx, wy, dwy;
                inkBspl(f.x, wx, dwx); inkBspl(f.y, wy, dwy);
                float2 gx = float2(wx.x + wx.y, wx.z + wx.w), gy = float2(wy.x + wy.y, wy.z + wy.w);
                float2 ox = float2(-1.0 + wx.y / gx.x, 1.0 + wx.w / gx.y), oy = float2(-1.0 + wy.y / gy.x, 1.0 + wy.w / gy.y);
                float2 dgx = float2(dwx.x + dwx.y, dwx.z + dwx.w), dgy = float2(dwy.x + dwy.y, dwy.z + dwy.w);
                float2 dox = float2(-1.0 + dwx.y / dgx.x, 1.0 + dwx.w / dgx.y), doy = float2(-1.0 + dwy.y / dgy.x, 1.0 + dwy.w / dgy.y);
                float4 v = gx.x * (gy.x * inkTap(i + float2(ox.x, oy.x)) + gy.y * inkTap(i + float2(ox.x, oy.y)))
                         + gx.y * (gy.x * inkTap(i + float2(ox.y, oy.x)) + gy.y * inkTap(i + float2(ox.y, oy.y)));
                gX = dgx.x * (gy.x * inkTap(i + float2(dox.x, oy.x)) + gy.y * inkTap(i + float2(dox.x, oy.y)))
                   + dgx.y * (gy.x * inkTap(i + float2(dox.y, oy.x)) + gy.y * inkTap(i + float2(dox.y, oy.y)));
                gY = dgy.x * (gx.x * inkTap(i + float2(ox.x, doy.x)) + gx.y * inkTap(i + float2(ox.y, doy.x)))
                   + dgy.y * (gx.x * inkTap(i + float2(ox.x, doy.y)) + gx.y * inkTap(i + float2(ox.y, doy.y)));
                return v;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 vWNorm = normalize(i.normalWS);
                float2 fu = i.uv1.xy;                 // face metres
                float2 fsz = i.uv2.zw;
                bool wallF = abs(vWNorm.y) < 0.5;
                float3 T = normalize(i.tanWS - vWNorm * dot(i.tanWS, vWNorm));
                float3 Bt = MIRROR_SIGN * cross(vWNorm, T);

                // ---- base surface (M1: block colour with a soft mottle; texlib patterns come with M4)
                float3 base = i.color.rgb;
                float mott = vnoise(fu * 1.7) * 0.6 + vnoise(fu * 6.3) * 0.4;
                base *= 0.94 + 0.1 * mott;
                float rough = _BaseRough;
                // contact darkening at the foot of walls that stand on something
                if (i.uv1.z > 0.5 && i.uv1.w > 0.5) base *= lerp(0.55, 1.0, smoothstep(0.0, 0.85, fu.y));

                // ---- wet ink
                float gInk = 0.0, gFresh = 0.0, gInkS = 0.0, gWake = 0.0;
                float3 gInkCol = 0;
                float2 slope = 0;
                if (i.uv2.y > 0.5)
                {
                    float2 vPaintUv = i.uv0.xy;
                    float2 pdx = ddx(vPaintUv), pdy = ddy(vPaintUv);
                    float uTexel = 1.0 / _AtlasSize;
                    float texFoot = max(length(pdx), length(pdy)) / uTexel;
                    float gk = min(1.0, 8.0 / max(texFoot, 1e-4));
                    gPdx = pdx * gk; gPdy = pdy * gk;
                    float4 pnt = paintAt(vPaintUv);
                    float near = 1.0 - smoothstep(0.9, 2.4, texFoot);
                    float4 gX = 0, gY = 0;                       // per-texel gradients of all four channels
                    float4 l2 = SAMPLE_TEXTURE2D_LOD(_Paint, sampler_Paint, vPaintUv, 2.0);
                    float seam2 = l2.r / max(l2.a, 1e-3);
                    if (near > 0.0 && l2.a > 0.002 && (l2.a < 0.998 || pnt.a < 0.998 || (seam2 > 0.006 && seam2 < 0.994)))
                    {
                        float4 cX, cY;
                        float4 cub = paintCubic(vPaintUv, cX, cY);
                        pnt = lerp(pnt, cub, near);
                        gX = cX * near; gY = cY * near;
                    }
                    float amt = pnt.a;
                    float fw = fwidth(amt);
                    float w = clamp(fw * 0.8, 0.008, 0.25);
                    gInk = smoothstep(0.5 - w, 0.5 + w, amt);
                    float ia = 1.0 / max(amt, 0.02);
                    float tRaw = saturate(pnt.r * ia);
                    // crisp, pixel-anti-aliased colour border between the teams (no muddy mixed-colour fringe)
                    float wT = clamp(fwidth(tRaw) * 0.75, 0.012, 0.2);
                    float tm = smoothstep(0.5 - wT, 0.5 + wT, tRaw);
                    float wet = saturate(pnt.g * ia);
                    float fresh = smoothstep(0.3, 0.97, wet);       // visibly wet for ≈ 4 s after landing
                    gFresh = fresh * gInk;
                    float tone = saturate(pnt.b * ia);
                    Light sunL = GetMainLight();
                    float2 sunF = float2(dot(sunL.direction, T), dot(sunL.direction, Bt));   // sun direction in the face plane
                    // thickness profile: 0 at the edge → 1 on the flat top (≈2–3 texels in). Floors pool (tall rounded lip,
                    // taller while fresh); walls hold a thinner clinging film.
                    float s = saturate((amt - 0.5) * 1.7);
                    float hs = 1.0 - (1.0 - s) * (1.0 - s);
                    gInkS = hs;
                    float thick = (wallF ? 0.6 : 1.0) * (1.0 + 0.5 * fresh);
                    float2 gradA = float2(gX.a, gY.a);
                    float2 gInkD = gradA * 2.0 * (1.0 - s) * 1.7 * 1.9 * gInk * thick;
                    // where the two teams' inks meet: the newer (wetter) ink rides over the older one on a rounded lip; two
                    // inks of the same age meet in a soft crease between their rounded edges
                    float2 gT = (float2(gX.r, gY.r) - tRaw * gradA) * ia;   // d(team share) per texel
                    float gTl = length(gT);
                    float seam = 0.0, seamShadow = 0.0;
                    if (gTl > 0.03 && amt > 0.6)
                    {
                        float2 sn = gT / gTl;
                        float x = (tRaw - 0.5) / gTl;                  // signed distance to the midline, texels
                        float e1 = exp(-x * x / 1.7), e2 = exp(-x * x / 0.8);
                        seam = e1 * smoothstep(0.03, 0.14, gTl) * near;
                        float2 gW = (float2(gX.g, gY.g) - wet * gradA) * ia;
                        float newer = clamp(dot(gW, sn) / gTl * 1.6, -1.0, 1.0);
                        float dh = newer * 2.4 * e1 / 2.3 + 0.9 * 2.0 * x / 0.8 * e2 * (1.0 - 0.6 * abs(newer));
                        gInkD += sn * dh * seam * gInk * thick;
                        seamShadow = seam * (0.45 * max(0.0, -x * newer) * e1 + 0.55 * e2 * (1.0 - 0.7 * abs(newer)));
                    }
                    float3 team = lerp(_TeamA.rgb, _TeamB.rgb, tm);
                    float3 inkCol = team * (0.93 + 0.13 * tone);
                    // translucent thin lip reads lighter and a touch more saturated; the thick body a little deeper
                    float lip = (1.0 - hs) * near;
                    inkCol = lerp(inkCol, inkCol * 1.16 + team * 0.05, lip * 0.35);
                    inkCol *= 1.0 - 0.05 * hs;
                    inkCol *= 1.0 - 0.3 * seamShadow * gInk;
                    inkCol *= 1.0 + 0.07 * fresh * fresh;
                    gInkCol = inkCol;
                    float halo = smoothstep(0.06, 0.5, amt) * (1.0 - gInk) * near;
                    if (!wallF)
                    {
                        // ink sits ON the ground: the lip throws a soft contact shadow, heavier on the side away from the sun
                        float2 outN = -gradA / max(length(gradA), 1e-4);
                        float away = clamp(0.55 - 0.9 * dot(outN, normalize(sunF + 1e-4)) * length(sunF), 0.25, 1.3);
                        halo *= smoothstep(0.4, 0.85, vWNorm.y) * away;
                        base *= lerp(1.0, team * 0.45 + 0.3, halo * 0.5);
                    }
                    else
                    {
                        // walls: the edge soaks into the surface — a damp, slightly tinted halo instead of a shadow
                        base *= lerp(1.0, team * 0.35 + 0.5, halo * 0.55);
                    }
                    base = lerp(base, gInkCol, gInk);
                    rough = lerp(rough, lerp(0.26, 0.14, gFresh), gInk);
                    slope = gInkD;

                    if (gInk > 0.01)
                    {
                        // gel micro-surface: soft swells so reflections break into wet highlights (analytic noise slope)
                        float2 gp = fu * (wallF ? float2(0.9, 0.42) : float2(0.75, 0.75));
                        float fr2 = gFresh * gFresh;
                        float tt = _Time.y;
                        gp += float2(tt * 0.011, -tt * 0.007) + fr2 * 0.022 * float2(sin(tt * 2.3), cos(tt * 1.9));
                        const float e = 0.06;
                        float2 q = gp * 2.2;
                        float hx = vnoise(q + float2(e, 0.0)) - vnoise(q - float2(e, 0.0));
                        float hy = vnoise(q + float2(0.0, e)) - vnoise(q - float2(0.0, e));
                        float gf = 1.0 - smoothstep(0.03, 0.12, length(fwidth(i.positionWS)));
                        slope += float2(hx, hy) / (2.0 * e) * 0.035 * gf * gInk;
                    }
                #if INK_RIPPLES
                    if (gInk > 0.01)
                    {
                        // ripples: a wave packet runs out from every impact / footstep / dive, thinning as it spreads; the
                        // crests catch the light (glossier). Height field → analytic gradient in the face plane.
                        float3 rS = 0; float rG = 0.0;
                        [loop] for (int k = 0; k < INK_RIPPLE_N; k++)
                        {
                            float4 Pr = _Rip[k], Qr = _RipP[k];
                            float age = _InkClock - Pr.w;
                            if (age <= 0.0 || age >= Qr.w) continue;
                            float3 D = i.positionWS - Pr.xyz;
                            float off = dot(D, vWNorm);
                            if (abs(off) > 0.4) continue;                      // another surface
                            D -= vWNorm * off;
                            float front = Qr.z * age;
                            float d = length(D);
                            float x = d - front;
                            float sg = Qr.y * 0.8;
                            if (x > sg * 3.0 || x < -Qr.y * 4.0) continue;
                            float env = exp(-x * x / (sg * sg));
                            float kk = 6.2831853 / Qr.y;
                            float fade = 1.0 - age / Qr.w;
                            fade *= fade * smoothstep(0.0, 0.03, age);
                            float amp = Qr.x * fade / (1.0 + d * 1.6);
                            float dh = amp * env * (-2.0 * x / (sg * sg) * cos(kk * x) - kk * sin(kk * x));
                            rS += D * (dh / max(d, 1e-4));
                            rG += env * fade * min(Qr.x * 90.0, 1.0);
                        }
                        float rl = length(rS);
                        if (rl > 0.75) rS *= 0.75 / rl;
                        float rFar = 1.0 - smoothstep(0.05, 0.18, length(fwidth(i.positionWS)));
                        slope += float2(dot(rS, T), dot(rS, Bt)) * gInk * rFar;
                        gWake = max(gWake, saturate(rG * 0.5) * gInk * rFar);
                    }
                #endif
                }

                float3 wn = normalize(vWNorm - slope.x * T - slope.y * Bt);

                // ---- lighting: URP PBR with a clear wet coat over the ink
                InputData inputData = (InputData)0;
                inputData.positionWS = i.positionWS;
                inputData.positionCS = i.positionCS;
                inputData.normalWS = wn;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(i.positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                inputData.fogCoord = i.fogCoord;
                inputData.bakedGI = SampleSH(wn);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                // glossy wet coat on ink (sharp while fresh, satin once dry)
                float cr = lerp(lerp(0.06, 0.028, gFresh), 0.022, gWake);
                SurfaceData surf = (SurfaceData)0;
                surf.albedo = base;
                surf.metallic = 0;
                surf.specular = 0;
                surf.smoothness = 1.0 - rough;
                surf.normalTS = float3(0, 0, 1);
                surf.occlusion = 1;
                surf.alpha = 1;
                // a little self-light keeps ink loud in shadow and at dusk (stronger in the thick body)
                surf.emission = gInkCol * gInk * _InkGlow * (0.75 + 0.35 * gInkS);
                surf.clearCoatMask = gInk;
                surf.clearCoatSmoothness = 1.0 - sqrt(cr);
                // baked ambient occlusion: full on sky / indirect light, a touch (0.4) on the sun's diffuse so contact shadows
                // read in daylight. URP's occlusion only reaches indirect light, so the direct share rides on the albedo and is
                // divided back out of the occlusion (indirect diffuse = albedo × bao, direct diffuse = albedo × mix(1, bao, 0.4))
                if (_AO > 0.0 && i.uv0.z >= 0.0)
                {
                    float bao = lerp(1.0, SAMPLE_TEXTURE2D(_Light, sampler_Light, i.uv0.zw).r, _AO);
                    float m = lerp(1.0, bao, 0.4);
                    surf.albedo *= m;
                    surf.occlusion = bao / max(m, 1e-3);
                }
                half4 col = UniversalFragmentPBR(inputData, surf);
                col.rgb = min(col.rgb, 5.0);
                col.rgb = MixFog(col.rgb, i.fogCoord);
                return half4(col.rgb, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            CBUFFER_START(UnityPerMaterial)
                float _AtlasSize;
                float _InkGlow;
                float _BaseRough;
                float _AO;
            CBUFFER_END
            float3 _LightDirection;
            float3 _LightPosition;
            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            float4 vert(A i) : SV_POSITION
            {
                float3 ws = TransformObjectToWorld(i.positionOS.xyz);
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

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                float _AtlasSize;
                float _InkGlow;
                float _BaseRough;
                float _AO;
            CBUFFER_END
            float4 vert(float4 p : POSITION) : SV_POSITION { return TransformObjectToHClip(p.xyz); }
            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
