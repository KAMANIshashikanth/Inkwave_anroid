// Port of the level surface material (src/world/levelMaterial.js) with its wet-ink layer (src/world/inkShading.js).
//
// Ported: the texlib surface (USE_TEXLIB: per-PATTERN slot table with wall / top remaps, grid + hex anti-tiling,
// stairs sampled in the ramp's own frame, macro variation, cavity, Toksvig roughness, relief normal kept partly under
// ink, metalness), the pattern overlays (court markings, hazard edges, spawn chevrons, planter grass) and the modelled
// detail (grime, coping, ramp grooves, container rust, stair wear, wall streaks, boatyard drain), contact darkening, the
// full ink layer (B-spline atlas reconstruction close up, meniscus / team-seam height field → normal, fresh/dry gloss,
// texlib gel micro-surface, ripples, lip / body colour, halo), lit through URP's PBR with a clear wet coat, baked AO.
// Without the texlib (bake missing) a plain mottled surface stands in. Murals, swim wakes, dusk lamps and the
// see-through window arrive with their milestones (murals: Task 7, wakes: Task 10).
//
// The mesh sits under the Z-mirrored world root, so Unity world space is three space mirrored: any cross product of
// world vectors flips sign — the face bitangent is -cross(N, T) here (three: cross(N, T)). Patterns keyed to world
// position read the three-space position (vWPos = positionWS with z negated).
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
            #pragma multi_compile_local_fragment _ USE_TEXLIB
            #pragma require 2darray

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

            float h21(float2 p) { p = frac(p * float2(123.34, 456.21)); p += dot(p, p + 45.32); return frac(p.x * p.y); }
            float vnoise(float2 p)
            {
                float2 i = floor(p), f = frac(p);
                float2 u = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(h21(i), h21(i + float2(1, 0)), u.x), lerp(h21(i + float2(0, 1)), h21(i + float2(1, 1)), u.x), u.y);
            }
            float fbm(float2 p) { float a = 0.5, s = 0.0; for (int k = 0; k < 4; k++) { s += a * vnoise(p); p = p * 2.03 + 17.1; a *= 0.5; } return s; }
            // anti-aliased grid line: returns 1 on the line
            float gridLine(float x, float period, float width)
            {
                float fx = abs(frac(x / period + 0.5) - 0.5) * period;
                float w = fwidth(x) * 0.75;
                return 1.0 - smoothstep(width - w, width + w, fx);
            }

        #if USE_TEXLIB
            // ---- INKWAVE texlib runtime helpers (port of TEXLIB_GLSL, src/world/texlib.js). GLSL mat2(c0, c1) is
            // column-major; here a float2x2 holds the same matrix by rows, so GLSL M * v == mul(M, v).
            TEXTURE2D_ARRAY(_TexlibAlbedo); SAMPLER(sampler_TexlibAlbedo);
            TEXTURE2D_ARRAY(_TexlibNormal);
            TEXTURE2D_ARRAY(_TexlibOrm);
            TEXTURE2D(_TLTex);          // slot table (Texlib.cs): row 0 = layer, 1/scale, mode, sym · row 1 = tint, grate, onWall, onTop · row 2 = stair
            float _TLSlots, _TexSize, _Gel;
            float4 tlSlot(int k, int row) { return LOAD_TEXTURE2D(_TLTex, int2(k, row)); }

            #define TEXLIB_PLAIN 0
            #define TEXLIB_GRID 1
            #define TEXLIB_HEX 2
            #define TEXLIB_HEX_SCALE 0.5
            struct TexlibSample { float4 albedo; float3 normal; float4 orm; };

            uint texlib_pcg(uint v) { uint s = v * 747796405u + 2891336453u; uint w = ((s >> ((s >> 28u) + 4u)) ^ s) * 277803737u; return (w >> 22u) ^ w; }
            uint texlib_hash(int2 p) { return texlib_pcg(asuint(p.x) + texlib_pcg(asuint(p.y) + 0x9e3779b9u)); }
            float texlib_h01(uint h) { return float(h >> 8u) * (1.0 / 16777216.0); }

            void texlib_triGrid(float2 st, out float3 w, out int2 v1, out int2 v2, out int2 v3)
            {
                st *= 3.46410162;
                float2 sk = float2(st.x - 0.57735027 * st.y, 1.15470054 * st.y);
                int2 b = int2(floor(sk));
                float3 t = float3(frac(sk), 0.0); t.z = 1.0 - t.x - t.y;
                float s = step(0.0, -t.z), s2 = 2.0 * s - 1.0;
                w = float3(-t.z * s2, s - t.y * s2, s - t.x * s2);
                int si = int(s);
                v1 = b + int2(si, si); v2 = b + int2(si, 1 - si); v3 = b + int2(1 - si, si);
            }
            float2 texlib_hexCentre(int2 v) { return float2(v.x + 0.5 * v.y, 0.8660254 * v.y) / (3.46410162 * TEXLIB_HEX_SCALE); }
            float2x2 texlib_rot(uint h) { float a = texlib_h01(h) * 6.28318531; float c = cos(a), s = sin(a); return float2x2(c, -s, s, c); }

            void texlib_tap(float2 st, float layer, float2 gx, float2 gy, float2x2 M, out float4 a, out float3 n, out float nl, out float4 o)
            {
                float2 dx = mul(M, gx), dy = mul(M, gy);
                a = SAMPLE_TEXTURE2D_ARRAY_GRAD(_TexlibAlbedo, sampler_TexlibAlbedo, st, layer, dx, dy);
                float3 nr = SAMPLE_TEXTURE2D_ARRAY_GRAD(_TexlibNormal, sampler_TexlibAlbedo, st, layer, dx, dy).xyz * 2.0 - 1.0;
                o = SAMPLE_TEXTURE2D_ARRAY_GRAD(_TexlibOrm, sampler_TexlibAlbedo, st, layer, dx, dy);
                nl = length(nr);
                n = float3(mul(transpose(M), nr.xy), nr.z);
            }

            TexlibSample texlibSample(float2 uv, float layer, int mode, int sym)
            {
                float2 gx = ddx(uv), gy = ddy(uv);
                // keep the footprint anisotropy within what hardware AF resolves (≤ 12:1): past that, blur instead of alias
                float lx = dot(gx, gx), ly = dot(gy, gy);
                if (lx > ly * 144.0) gy = ly > 1e-20 ? gy * sqrt(lx / (ly * 144.0)) : float2(-gx.y, gx.x) / 12.0;
                else if (ly > lx * 144.0) gx = lx > 1e-20 ? gx * sqrt(ly / (lx * 144.0)) : float2(gy.y, -gy.x) / 12.0;
                TexlibSample r;
                float3 n; float nl;
                if (mode == TEXLIB_HEX)
                {
                    float3 w; int2 v1, v2, v3;
                    texlib_triGrid(uv * TEXLIB_HEX_SCALE, w, v1, v2, v3);
                    uint h1 = texlib_hash(v1), h2 = texlib_hash(v2), h3 = texlib_hash(v3);
                    float2x2 R1 = texlib_rot(h1), R2 = texlib_rot(h2), R3 = texlib_rot(h3);
                    float2 c1 = texlib_hexCentre(v1), c2 = texlib_hexCentre(v2), c3 = texlib_hexCentre(v3);
                    float2 o1 = float2(texlib_h01(texlib_pcg(h1)), texlib_h01(texlib_pcg(h1 + 1u)));
                    float2 o2 = float2(texlib_h01(texlib_pcg(h2)), texlib_h01(texlib_pcg(h2 + 1u)));
                    float2 o3 = float2(texlib_h01(texlib_pcg(h3)), texlib_h01(texlib_pcg(h3 + 1u)));
                    float4 a1, a2, a3, q1, q2, q3; float3 n1, n2, n3; float l1, l2, l3;
                    texlib_tap(mul(R1, uv - c1) + c1 + o1, layer, gx, gy, R1, a1, n1, l1, q1);
                    texlib_tap(mul(R2, uv - c2) + c2 + o2, layer, gx, gy, R2, a2, n2, l2, q2);
                    texlib_tap(mul(R3, uv - c3) + c3 + o3, layer, gx, gy, R3, a3, n3, l3, q3);
                    float3 W = w * w; W *= W; W *= w * w * w;                       // w^7
                    W *= lerp(1.0, float3(q1.a, q2.a, q3.a) + 0.05, 0.6);           // height-aware contrast blend
                    W /= max(W.x + W.y + W.z, 1e-6);
                    r.albedo = W.x * a1 + W.y * a2 + W.z * a3;
                    r.orm = W.x * q1 + W.y * q2 + W.z * q3;
                    n = W.x * n1 + W.y * n2 + W.z * n3;
                    nl = W.x * l1 + W.y * l2 + W.z * l3;
                }
                else if (mode == TEXLIB_GRID)
                {
                    uint h = texlib_hash(int2(floor(uv))) & uint(sym);
                    float2x2 M = float2x2(1, 0, 0, 1);
                    if ((h & 4u) != 0u) M = float2x2(0, 1, 1, 0);
                    if ((h & 1u) != 0u) M = mul(float2x2(-1, 0, 0, 1), M);
                    if ((h & 2u) != 0u) M = mul(float2x2(1, 0, 0, -1), M);
                    texlib_tap(mul(M, frac(uv) - 0.5) + 0.5, layer, gx, gy, M, r.albedo, n, nl, r.orm);
                }
                else
                {
                    texlib_tap(uv, layer, gx, gy, float2x2(1, 0, 0, 1), r.albedo, n, nl, r.orm);
                }
                // Toksvig: mips average bumps into shorter normals -> widen the lobe instead of sparkling
                float v = max((1.0 - nl) / max(nl, 1e-3) - 0.006, 0.0);
                float a = r.orm.g * r.orm.g;
                r.orm.g = sqrt(sqrt(min(a * a + 2.0 * v, 1.0)));
                r.normal = normalize(float3(n.xy, max(n.z, 1e-3)));
                return r;
            }
            float3 texlibPerturbNormal(float3 nTS, float3 T, float3 B, float3 N, float strength)
            {
                return normalize(T * (nTS.x * strength) + B * (nTS.y * strength) + N * nTS.z);
            }
            float texlib_vn(float2 p)
            {
                float2 i = floor(p), f = p - i; float2 u = f * f * (3.0 - 2.0 * f);
                int2 c = int2(i);
                float a = texlib_h01(texlib_hash(c)), b = texlib_h01(texlib_hash(c + int2(1, 0)));
                float d = texlib_h01(texlib_hash(c + int2(0, 1))), e = texlib_h01(texlib_hash(c + int2(1, 1)));
                return lerp(lerp(a, b, u.x), lerp(d, e, u.x), u.y);
            }
            float texlibMacro(float2 p)
            {
                float n = 0.62 * texlib_vn(p * 0.085 + 3.7) + 0.38 * texlib_vn(p * 0.29 + 11.3);
                return 0.94 + 0.12 * n;
            }
        #endif

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
                float pattern = i.uv2.x;
                bool wallF = abs(vWNorm.y) < 0.5;
                float3 T = normalize(i.tanWS - vWNorm * dot(i.tanWS, vWNorm));
                float3 Bt = MIRROR_SIGN * cross(vWNorm, T);
                const float3 MIR = float3(1.0, 1.0, MIRROR_SIGN);
                float3 vWPos = i.positionWS * MIR;    // three space
                float2 wp = vWPos.xz;

                // ---- base surface
                float3 base = i.color.rgb;
                float rough = _BaseRough;
                float3 gTexN = float3(0, 0, 1);
                float4 gTexORM = float4(1.0, 0.8, 0.0, 0.5);
                float gTexStr = 0.0;
                float4 gStair = 0;      // stair / ramp top: distance across from one side (m), width (m), edge kind, step coordinate
                float gTexKeep = 0.0;   // share of the texture relief kept under ink (stairs keep their steps)
                float gTexPaint = 1.0;
            #if USE_TEXLIB
                float fine = vnoise(fu * 9.0);
                {
                    int pid = int(pattern + 0.5);
                    bool vertical = abs(vWNorm.y) < 0.5;
                    // per-slot remaps: ramp / asphalt / yard sides → concrete, car-deck edge → plating, gelcoat tops → planks
                    if (vertical) pid = int(tlSlot(pid, 1).z + 0.5); else if (vWNorm.y > 0.5) pid = int(tlSlot(pid, 1).w + 0.5);
                    float4 tl = tlSlot(pid, 0), tt = tlSlot(pid, 1);
                    // stairs / ramps: sample in the ramp's own frame — u across, v downhill from the top landing — with v
                    // phase-locked so a whole number of steps fits the visible flight (the slab runs 0.6 m on under the
                    // floor at its low end, level.js)
                    float2 tuv = fu * tl.y;
                    float4 sp = tlSlot(pid, 2);
                    float2 tA = float2(1, 0), tD = float2(0, 1);
                    if (sp.x > 0.0 && vWNorm.y > 0.5)
                    {
                        float3 fT = i.tanWS * MIR, fN = vWNorm * MIR;                       // three space
                        bool alongU = abs(fT.y) > 0.02;                                      // x-running ramps: face u runs up/down the slope
                        float sg = alongU ? sign(fT.y) : sign(fN.z * fT.x - fN.x * fT.z);   // uphill = +axis?
                        float fa = alongU ? fu.x : fu.y, fcr = alongU ? fu.y : fu.x;
                        float La = alongU ? fsz.x : fsz.y, Wd = alongU ? fsz.y : fsz.x;
                        float dTop = sg > 0.0 ? La - fa : fa;
                        if (dTop > La - 0.3) { sg = -sg; dTop = La - dTop; }                // bevel past the crest: visible fragments never sit in the buried tail
                        float Lv = max(La - 0.6, sp.x);
                        float Ps = Lv / max(1.0, floor(Lv / sp.x + 0.5));
                        tuv = float2(fcr * tl.y, dTop / (Ps * sp.y));
                        tA = alongU ? float2(0, 1) : float2(1, 0);
                        tD = alongU ? float2(-sg, 0) : float2(0, -sg);
                        gStair = float4(fcr, Wd, sp.w, dTop / Ps);
                        gTexKeep = sp.z;
                    }
                    TexlibSample ts = texlibSample(tuv, tl.x, int(tl.z), int(tl.w));
                    ts.normal = float3(ts.normal.x * tA + ts.normal.y * tD, ts.normal.z);  // back into the face's (u, v) frame
                    float3 dc = i.color.rgb;
                    base = tt.x > 1.5 ? ts.albedo.rgb + dc * ts.albedo.a * 1.25
                         : (tt.x > 0.5 ? dc * ts.albedo.rgb * 1.25 : ts.albedo.rgb);
                    base *= texlibMacro(wp + vWPos.y * 0.7);
                    base *= lerp(1.0, ts.orm.r, 0.85);                            // cavity occlusion in grout / seams / grooves
                    gTexPaint = tt.x > 1.5 ? saturate(ts.albedo.a * 1.45) : 1.0;
                    rough = ts.orm.g;
                    gTexN = ts.normal; gTexORM = ts.orm; gTexStr = tt.y;
                }
                if (pattern < 0.5) {
                } else if (pattern < 1.5) {
                    // court markings over the pavers
                    float rr = length(wp);
                    float wr = fwidth(rr);
                    float mark = 1.0 - smoothstep(0.09 - wr, 0.09 + wr, abs(rr - 9.5));
                    float wz = fwidth(vWPos.z);
                    mark = max(mark, (1.0 - smoothstep(0.07 - wz, 0.07 + wz, abs(vWPos.z))) * step(9.5, abs(vWPos.x)));
                    mark *= 0.75 + 0.25 * step(0.35, vnoise(wp * 6.0));
                    if (vWNorm.y > 0.5) base = lerp(base, float3(0.97, 0.96, 0.92), mark * 0.9);
                } else if (pattern > 3.5 && pattern < 4.5) {
                    float edge = 1.0 - smoothstep(0.16, 0.2, min(fu.x, fsz.x - fu.x));
                    if (vWNorm.y > 0.5) { base = lerp(base, float3(0.93, 0.72, 0.12), edge * 0.95); rough = lerp(rough, 0.6, edge); }
                } else if (pattern > 7.5 && pattern < 8.5) {
                    float e = min(min(fu.x, fsz.x - fu.x), min(fu.y, fsz.y - fu.y));
                    float band = 1.0 - smoothstep(0.34, 0.36, e);
                    float chev = step(0.5, frac((fu.x + fu.y) * 1.6));
                    if (vWNorm.y > 0.5) base = lerp(base, lerp(float3(0.95, 0.76, 0.1), 0.12, chev), band * step(0.05, e));
                } else if (pattern > 8.5 && pattern < 9.5) {
                    float e = min(min(fu.x, fsz.x - fu.x), min(fu.y, fsz.y - fu.y));
                    float bed = smoothstep(0.24, 0.27, e) * step(0.5, vWNorm.y);
                    float3 grass = float3(0.32, 0.55, 0.22) * (0.8 + 0.35 * fbm(fu * 6.0));
                    base = lerp(base, grass, bed);
                    rough = lerp(rough, 0.95, bed);
                    gTexStr *= 1.0 - bed;
                }
                // ---- modelled detail (shader-only, no layout change)
                {
                    int pid = int(pattern + 0.5);
                    float ef = min(min(fu.x, fsz.x - fu.x), min(fu.y, fsz.y - fu.y));
                    bool isTop = vWNorm.y > 0.6;
                    bool isWall = abs(vWNorm.y) < 0.5;
                    // floors: soft grime patches, sun-bleached warm areas and smoother traffic-polished patches
                    if (isTop && (pid == 1 || pid == 16 || pid == 10 || pid == 2 || pid == 8 || pid == 17 || pid == 19 || pid == 21))
                    {
                        float m1 = vnoise(wp * 0.09 + 5.3), m2 = vnoise(wp * 0.31 + 1.7), m3 = vnoise(wp * 1.3);
                        float grime = smoothstep(0.52, 0.86, m1 * 0.65 + m2 * 0.35);
                        base *= 1.0 - 0.075 * grime * (0.7 + 0.3 * m3);
                        base *= lerp(1.0, float3(1.025, 1.005, 0.965), smoothstep(0.42, 0.12, m1));
                        rough = lerp(rough, rough * 0.72, smoothstep(0.62, 0.9, m2) * 0.7);
                    }
                    // stone coping on exposed tops of walls / platforms / parapets, with a drip-groove shadow under the cap
                    if (pid == 0 || pid == 2 || pid == 3 || pid == 13 || pid == 15 || pid == 16 || pid == 23)
                    {
                        float cop = 0.0, groove = 0.0;
                        float cw = lerp(0.2, 0.26, step(3.0, min(fsz.x, fsz.y)));
                        if (isTop)
                        {
                            cop = min(fsz.x, fsz.y) < 1.15 ? 1.0 : 1.0 - smoothstep(cw - 0.008, cw + 0.008, ef);
                            groove = (1.0 - smoothstep(0.004, 0.012, abs(ef - cw))) * step(1.15, min(fsz.x, fsz.y));
                        }
                        else if (isWall && i.uv1.z > 0.5)
                        {
                            float dt = fsz.y - fu.y;
                            cop = 1.0 - smoothstep(0.1, 0.108, dt);
                            groove = 1.0 - smoothstep(0.005, 0.016, abs(dt - 0.118));
                        }
                        float3 stone = float3(0.9, 0.885, 0.845) * (0.95 + 0.07 * vnoise(fu * 9.0) + 0.03 * vnoise(fu * 41.0));
                        base = lerp(base, stone * texlibMacro(wp + vWPos.y * 0.7), cop);
                        rough = lerp(rough, 0.72, cop);
                        gTexStr *= 1.0 - 0.8 * cop;
                        base *= 1.0 - 0.28 * groove;
                    }
                    // ramps: anti-slip grooves across the slope (T / Bt .y are the same in both spaces)
                    if (pid == 4 && isTop)
                    {
                        float2 up2 = float2(T.y, Bt.y);
                        float along = dot(fu, up2 / max(length(up2), 1e-4));
                        float g = gridLine(along, 0.32, 0.016);
                        base *= 1.0 - 0.2 * g;
                        rough = lerp(rough, 0.95, g);
                    }
                    // containers: rust streaks bleeding from the top rail and corners, dirt toward the bottom
                    if (pid == 5 && isWall)
                    {
                        float st = vnoise(float2(dot(wp, float2(0.707, 0.707)) * 5.5 + vWPos.x * 0.8, vWPos.y * 0.35));
                        float fromTop = smoothstep(fsz.y * 0.25, fsz.y, fu.y);
                        float rust = smoothstep(0.6, 0.92, st) * (0.35 + 0.65 * fromTop);
                        rust = max(rust, (1.0 - smoothstep(0.05, 0.3, ef)) * smoothstep(0.45, 0.8, vnoise(fu * 3.0)) * 0.8);
                        base = lerp(base, float3(0.42, 0.2, 0.1) * (0.8 + 0.4 * vnoise(fu * 11.0)), rust * 0.42);
                        rough = lerp(rough, 0.85, rust);
                        base *= lerp(0.84, 1.0, smoothstep(0.0, 0.7, fu.y));
                    }
                    // stairs / ramps: soft contact shadow along the stringers / kick plates / cheek walls at both sides; the
                    // stone stair is worn smoother and paler down its middle, the steel stair's plate polished along the line
                    if (gStair.z > 0.5)
                    {
                        float e = min(gStair.x, gStair.y - gStair.x);
                        float kind = gStair.z;
                        float sw = kind < 1.5 ? 0.16 : (kind < 2.5 ? 0.3 : (kind < 3.5 ? 0.14 : 0.12));
                        base *= 1.0 - (kind < 2.5 ? 0.26 : 0.2) * (1.0 - smoothstep(0.0, sw, e));
                        float mid = 1.0 - smoothstep(0.18, 0.42, abs(gStair.x / max(gStair.y, 0.01) - 0.5));
                        if (kind > 1.5 && kind < 2.5) { base *= 1.0 + 0.05 * mid; rough = lerp(rough, rough * 0.78, mid); }
                        if (kind < 1.5) rough = lerp(rough, rough * 0.85, mid * (1.0 - gTexPaint));
                    }
                    // painted render / timber cladding: rain-washed grime streaks hanging from the top edge of each wall face
                    if ((pid == 23 || pid == 22) && isWall)
                    {
                        float fromTop = fsz.y - fu.y;
                        float st = 0.6 * vnoise(float2(fu.x * 1.9, fromTop * 0.3 + 7.1)) + 0.4 * vnoise(float2(fu.x * 6.3, fromTop * 0.8 + 2.3));
                        float streak = smoothstep(0.5, 0.85, st) * exp(-fromTop * 0.75);
                        base *= 1.0 - 0.2 * streak;
                        rough = lerp(rough, 0.9, streak * 0.6);
                    }
                    // boatyard: galvanised slot drain round the tug's wash-down bay (Alpha-half coordinates; the 180° twin follows)
                    if (pid == 21 && isTop)
                    {
                        float2 q = wp.y > 0.0 ? -wp : wp;
                        float2 d = abs(q - float2(17.5, -18.4)) - float2(4.4, 7.0);
                        float sd = length(max(d, 0.0)) + min(max(d.x, d.y), 0.0);
                        float e = abs(sd), aw = fwidth(e) + 1e-4;
                        float chan = 1.0 - smoothstep(0.08 - aw, 0.08 + aw, e);
                        float lip = (1.0 - smoothstep(0.105 - aw, 0.105 + aw, e)) * (1.0 - chan);
                        float run = d.x > d.y ? q.y : q.x;
                        float fw = fwidth(run) + 1e-5;
                        float slot = smoothstep(0.3 - fw / 0.025, 0.3 + fw / 0.025, abs(frac(run / 0.025) - 0.5) * 2.0);
                        float bar = lerp(0.4, slot, saturate(0.025 / (fw * 4.0) - 0.5));
                        base = lerp(base, lerp(0.025, float3(0.34, 0.35, 0.36), bar), chan);
                        base = lerp(base, float3(0.4, 0.41, 0.42) * (0.9 + 0.2 * fine), lip * 0.85);
                        rough = lerp(rough, 0.48, max(chan * bar, lip));
                        gTexORM.b = lerp(gTexORM.b, 0.75, max(chan * bar, lip));
                        gTexStr *= 1.0 - max(chan, lip);
                        base *= lerp(1.0, float3(0.94, 0.9, 0.87), step(sd, -0.105) * 0.8);   // the bay floor: darker, run-off stained
                        // world-scale (non-repeating) oil drips and a worn service-lane wheel track beside the bay
                        float oilW = smoothstep(0.68, 0.88, 0.65 * vnoise(q * 0.9 + 13.1) + 0.35 * vnoise(q * 3.1 + 2.7));
                        base *= 1.0 - 0.26 * oilW * (1.0 - chan);
                        rough = lerp(rough, 0.62, oilW * 0.5);
                        float tx = q.x - (12.15 + 0.25 * sin(q.y * 0.37));
                        float track = (1.0 - smoothstep(0.1, 0.17, min(abs(tx - 0.85), abs(tx + 0.85)))) * step(q.y, -7.6) * (0.5 + 0.5 * vnoise(q * float2(1.3, 0.35)));
                        base *= 1.0 - 0.1 * track;
                        rough = lerp(rough, rough * 0.85, track);
                    }
                }
            #else
                // no texture library: block colour with a soft mottle
                float mott = vnoise(fu * 1.7) * 0.6 + vnoise(fu * 6.3) * 0.4;
                base *= 0.94 + 0.1 * mott;
            #endif
                // contact darkening at the foot of walls that stand on something
                if (i.uv1.z > 0.5 && i.uv1.w > 0.5) base *= lerp(0.55, 1.0, smoothstep(0.0, 0.85, fu.y));

                // ---- wet ink
                float gInk = 0.0, gFresh = 0.0, gInkS = 0.0, gWake = 0.0, gInkKeep = 0.0;
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
                    gInkKeep = wallF ? 0.32 * (1.0 - 0.5 * fresh) : 0.0;     // the surface relief shows through thin wall ink
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
                        // gel micro-surface: soft swells so reflections break into wet highlights. Walls stretch it
                        // vertically; fresh ink is smoother and sways very gently while it settles.
                        float2 gp = fu * (wallF ? float2(0.9, 0.42) : float2(0.75, 0.75));
                        float fr2 = gFresh * gFresh;
                        float tt = _Time.y;
                        gp += float2(tt * 0.011, -tt * 0.007) + fr2 * 0.022 * float2(sin(tt * 2.3), cos(tt * 1.9));
                    #if USE_TEXLIB
                        // texlib 'gel' layer, mip-filtered (fades to flat by itself at distance)
                        if (_Gel >= 0.0)
                        {
                            float3 gn = SAMPLE_TEXTURE2D_ARRAY_GRAD(_TexlibNormal, sampler_TexlibAlbedo, gp, _Gel, ddx(gp), ddy(gp)).xyz * 2.0 - 1.0;
                            slope += -gn.xy / max(gn.z, 0.3) * (0.95 - 0.5 * gFresh) * gInk * gInkS;
                        }
                    #else
                        const float e = 0.06;
                        float2 q = gp * 2.2;
                        float hx = vnoise(q + float2(e, 0.0)) - vnoise(q - float2(e, 0.0));
                        float hy = vnoise(q + float2(0.0, e)) - vnoise(q - float2(0.0, e));
                        float gf = 1.0 - smoothstep(0.03, 0.12, length(fwidth(i.positionWS)));
                        slope += float2(hx, hy) / (2.0 * e) * 0.035 * gf * gInk;
                    #endif
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

                float3 nBase = vWNorm;
            #if USE_TEXLIB
                // surface relief from the texture library; ink fills the grooves so the relief fades out under it
                // (on corrugated metal the ink still follows the ribs)
                float keep = max((pattern > 4.5 && pattern < 5.5) ? 0.55 : 0.0, gTexKeep);   // containers + stairs
                nBase = texlibPerturbNormal(gTexN, T, Bt, vWNorm, gTexStr * (1.0 - gInk * (1.0 - max(keep, gInkKeep))));
            #endif
                float3 wn = normalize(nBase - slope.x * T - slope.y * Bt);

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
                surf.metallic = gTexORM.b * (1.0 - gInk);
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
