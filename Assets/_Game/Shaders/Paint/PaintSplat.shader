// Port of the PAINT_VS / PAINT_FS and drying shaders in src/world/paint.js. Draws splat quads straight into the paint
// atlas in face space (texture-space painting).
//
// Atlas encoding (RGBA8, premultiplied by coverage — the level shader divides by A):
//   R = team share (0 = team 0, 1 = team 1), "over" composited: the newest splat wins at a team border
//   G = wetness: every splat lands at 1, the dry pass subtracts it to 0 over ≈ 6 s
//   B = per-splat tone
//   A = coverage as a smooth ≈ 3-texel profile, MAX blended (union of splats; redrawing a spreading splat is idempotent)
Shader "Hidden/Inkwave/PaintSplat"
{
    SubShader
    {
        ZTest Always ZWrite Off Cull Off

        // 0: splat quads
        Pass
        {
            // RGB: newest splat wins ("over"); A: max → union coverage that stays idempotent while a splat spreads
            Blend SrcAlpha OneMinusSrcAlpha, One One
            BlendOp Add, Max

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"

            struct A
            {
                float3 aPos : POSITION;
                float3 aLocal : TEXCOORD0;
                float4 aSplat : TEXCOORD1;
                float3 aStretch : TEXCOORD2;
                float4 aGrow : TEXCOORD3;
            };
            struct V
            {
                float4 pos : SV_POSITION;
                float3 vLocal : TEXCOORD0;    // metres from the splat centre in face space; z = centre's distance from the face plane
                float4 vSplat : TEXCOORD1;    // final radius, team, seed, flags (isWall + 2 × kind)
                float3 vStretch : TEXCOORD2;  // travel direction in face space, smear amount (0 = none)
                float4 vGrow : TEXCOORD3;     // x: age / spread time (runs on past 1) · y: drip progress 0..1 · z: 1 = drips only
            };

            V vert(A i)
            {
                V o;
                o.pos = float4(i.aPos.xy, 0.5, 1.0);
            #if UNITY_UV_STARTS_AT_TOP
                o.pos.y = -o.pos.y;   // atlas rows are addressed bottom-up (GL convention) on every platform
            #endif
                o.vLocal = i.aLocal; o.vSplat = i.aSplat; o.vStretch = i.aStretch; o.vGrow = i.aGrow;
                return o;
            }

            float hsh(float n) { return frac(sin(n) * 43758.5453123); }
            float wob(float a, float s)
            {
                return 1.0 + 0.12 * sin(3.0 * a + s * 6.2831) + 0.08 * sin(5.0 * a + s * 17.0) + 0.05 * sin(7.0 * a + s * 41.0)
                    + 0.03 * sin(11.0 * a + s * 73.0) + 0.018 * sin(17.0 * a + s * 29.0)
                    + 0.17 * pow(max(cos(a - s * 37.7), 0.0), 28.0) + 0.12 * pow(max(cos(a - s * 53.3 - 2.1), 0.0), 36.0);
            }
            float smin(float a, float b, float k) { float h = saturate(0.5 + 0.5 * (b - a) / k); return lerp(b, a, h) - k * h * (1.0 - h); }
            // thin tapered ray from a (radius ra) to b (radius rb)
            float sdRay(float2 p, float2 a, float2 b, float ra, float rb)
            {
                float2 pa = p - a, ba = b - a;
                float h = saturate(dot(pa, ba) / max(dot(ba, ba), 1e-8));
                return length(pa - ba * h) - lerp(ra, rb, h);
            }
            // per kind: rays, satellite droplets, spatter dots, drips
            float4 kindShape(float k)
            {
                if (k < 0.5) return float4(5.0, 7.0, 8.0, 3.0);     // shot
                if (k < 1.5) return float4(3.0, 4.0, 5.0, 2.0);     // charger line
                if (k < 2.5) return float4(7.0, 9.0, 10.0, 4.0);    // blast
                if (k < 3.5) return float4(10.0, 12.0, 14.0, 5.0);  // bomb / slam / splat-out
                if (k < 4.5) return float4(3.0, 4.0, 4.0, 2.0);     // trail drip
                if (k < 5.5) return float4(2.0, 2.0, 0.0, 1.0);     // droplet paint
                return float4(0.0, 0.0, 0.0, 0.0);                  // roller band, speck
            }

            #define BAND_L 0.55
            #define BAND_W 0.62
            #define BAND_R 0.1

            float4 frag(V i) : SV_Target
            {
                float R = i.vSplat.x, team = i.vSplat.y, seed = i.vSplat.z;
                float isWall = fmod(i.vSplat.w, 2.0);
                float kind = floor(i.vSplat.w * 0.5 + 0.01);
                float dn = i.vLocal.z;
                float r2 = R * R - dn * dn;
                if (r2 <= 0.0) discard;
                float r = sqrt(r2);                                    // footprint radius on this face
                float fall = saturate(r / max(R, 1e-3));               // 1 on the face the blob hit, smaller on faces it grazes
                float2 p0 = i.vLocal.xy;
                float tx = max(max(abs(ddx(p0.x)), abs(ddy(p0.x))), max(abs(ddx(p0.y)), abs(ddy(p0.y))));   // metres per texel
                float2 dir = i.vStretch.xy; float sa = i.vStretch.z;
                float2 p = p0;
                if (sa > 0.0)
                {                                                     // shots: smeared forward along the travel direction
                    float a = dot(p, dir); float2 perp = p - a * dir;
                    float s = a > 0.0 ? 1.0 + sa : 1.0 + 0.25 * sa;
                    p = perp + dir * (a / s);
                }
                float tn = i.vGrow.x;
                float4 ks = kindShape(kind);
                float sd = 1e3;
                if (i.vGrow.z < 0.5)
                {
                    // ---- body: floods out from ~40 % with a strong ease-out; its final edge is the CPU gameplay edge
                    float tb = 1.0 - pow(1.0 - saturate(tn), 4.0);
                    float grow = lerp(0.4, 1.0, tb);
                    if (kind > 5.5 && kind < 6.5)
                    {
                        // roller band: a straight-edged segment across the drum, edges gently wavy
                        float2 bx = float2(-dir.y, dir.x);
                        float2 q = float2(dot(p0, dir), dot(p0, bx));
                        float wv = r * (0.03 * sin(q.x / r * 9.0 + seed * 30.0) + 0.018 * sin(q.x / r * 23.0 + seed * 11.0));
                        float2 dq = abs(q) - float2(r * BAND_L * grow, r * BAND_W + wv);
                        sd = length(max(dq, 0.0)) + min(max(dq.x, dq.y), 0.0) - r * BAND_R;
                    }
                    else if (kind > 6.5)
                    {
                        sd = length(p) - r * grow * (1.0 + 0.12 * sin(3.0 * atan2(p.y, p.x) + seed * 20.0));
                    }
                    else
                    {
                        sd = length(p) - r * grow * wob(atan2(p.y, p.x), seed);
                    }
                    float dirAng = sa > 0.0 ? atan2(dir.y, dir.x) : 0.0;
                    float spread = lerp(6.2831, 2.5, saturate(sa * 1.2));
                    bool big = kind > 1.5 && kind < 3.5;
                    // ---- rays: short tapered streaks shot out ahead of the body (the splat's "star"), mostly stubby with
                    // the odd long one, each ending in a bead where the ink collected as it flew
                    float tsp = 1.0 - pow(1.0 - saturate(tn * 1.4), 3.0);
                    [loop] for (int k = 0; k < 10; k++)
                    {
                        float fk = (float)k;
                        if (fk >= ks.x) break;
                        float h1 = hsh(seed * 7.31 + fk * 1.93), h2 = hsh(seed * 3.17 + fk * 5.71), h3 = hsh(seed * 11.3 + fk * 2.39);
                        float a = sa > 0.0 ? dirAng + (h1 - 0.5) * spread : (fk + 0.35 + 0.6 * h1) / ks.x * 6.2831 + seed * 6.2831;
                        float2 u = float2(cos(a), sin(a));
                        float edge = r * grow * wob(a, seed);
                        float len = r * (0.07 + (big ? 0.5 : 0.4) * h2 * h2 * h2) * tsp;
                        float wB = r * (0.055 + 0.06 * h3);
                        float tipR = max(r * (0.012 + 0.012 * h3), tx * 0.45);
                        float2 tip = u * (edge + len) + float2(-u.y, u.x) * len * 0.18 * (h1 - 0.5);
                        float ray = min(sdRay(p, u * edge * 0.72, tip, wB, tipR), length(p - tip) - tipR * (1.6 + 1.4 * h2));
                        sd = smin(sd, ray, r * 0.06);
                    }
                    // ---- satellite droplets flung off the crown: they land a beat after the body (the farthest last),
                    // streaked along their flight line; on walls gravity drags the spray down a little
                    [loop] for (int k2 = 0; k2 < 12; k2++)
                    {
                        float fk = (float)k2;
                        if (fk >= ks.y) break;
                        float h1 = hsh(seed * 13.1 + fk * 7.7), h2 = hsh(seed * 5.3 + fk * 3.1), h3 = hsh(seed * 9.9 + fk * 1.7);
                        float tl = 0.28 + 0.95 * h2;
                        float land = smoothstep(tl, tl + 0.2, tn);
                        if (land <= 0.0) continue;
                        float a2 = sa > 0.0 ? dirAng + (h1 - 0.5) * spread : h1 * 6.2831;
                        float2 u = float2(cos(a2), sin(a2));
                        if (isWall > 0.5) u = normalize(lerp(u, float2(0.0, -1.0), 0.32));
                        float dist = r * (1.1 + (big ? 1.05 : 0.8) * h2 * h2);
                        float rad = r * (0.028 + 0.085 * h3) * fall * (1.0 - 0.4 * h2) * (big ? 0.8 : 1.0) * land;
                        float2 q = p - u * dist;
                        float el = 1.0 + (0.5 + 1.6 * sa) * h2;
                        q -= u * dot(q, u) * (1.0 - 1.0 / el);
                        sd = smin(sd, length(q) - rad, rad * 0.8);
                    }
                    // ---- fine spatter: tiny dots sprayed farther out, landing last
                    [loop] for (int k3 = 0; k3 < 14; k3++)
                    {
                        float fk = (float)k3;
                        if (fk >= ks.z) break;
                        float h1 = hsh(seed * 17.9 + fk * 4.13), h2 = hsh(seed * 2.71 + fk * 8.09), h3 = hsh(seed * 6.47 + fk * 3.37);
                        if (tn < 0.45 + 0.95 * h2) continue;
                        float a3 = sa > 0.0 ? dirAng + (h1 - 0.5) * spread * 1.15 : h1 * 6.2831;
                        float2 u = float2(cos(a3), sin(a3));
                        if (isWall > 0.5) u = normalize(lerp(u, float2(0.0, -1.0), 0.25));
                        float rad = max(r * (0.011 + 0.02 * h3) * fall, tx * 0.9);
                        sd = min(sd, length(p - u * r * (1.3 + 1.2 * h2)) - rad);
                    }
                }
                // ---- drips on walls: the lower edge sags into streams that keep running; each thins as its bulbous head
                // carries the ink down, meandering a little
                if (isWall > 0.5 && fall > 0.3 && ks.w > 0.0)
                {
                    float dT = i.vGrow.y;
                    float nD = min(6.0, ks.w + floor(R * 1.2));
                    [loop] for (int k4 = 0; k4 < 6; k4++)
                    {
                        float fk = (float)k4;
                        if (fk >= nD) break;
                        float h1 = hsh(seed * 3.7 + fk * 11.3), h2 = hsh(seed * 8.1 + fk * 2.9), h3 = hsh(seed * 4.3 + fk * 5.9);
                        if (k4 > 1 && h3 < 0.3) continue;
                        float x = (h1 * 2.0 - 1.0) * r * 0.72;
                        float c = sqrt(max(1.0 - (x / r) * (x / r), 0.0));
                        float yTop = -c * r * 0.7;
                        float len = c * r * 0.25 + r * (0.3 + 2.3 * h2 * h2) * fall * dT;
                        float w = r * (0.042 + 0.04 * h3) * (0.75 + 0.35 * fall);
                        float2 q = p0 - float2(x, yTop);
                        float ty = saturate(-q.y / max(len, 1e-4));
                        q.x += sin(q.y / r * 9.0 + seed * 20.0 + fk * 2.3) * w * 0.35 * ty;
                        float wt = w * lerp(1.0, 0.6, smoothstep(0.05, 0.85, ty));
                        float stream = max(abs(q.x) - wt, max(q.y, -len - q.y));
                        float2 tq = (q - float2(0.0, -len + w * 0.3)) * float2(1.0, 0.8);
                        float bulb = length(tq) - w * (1.2 + 0.35 * h2) * (0.6 + 0.4 * dT);
                        sd = smin(sd, smin(stream, bulb, w * 0.9), w * 1.2);
                    }
                }
                float fw = max(fwidth(sd), 1e-5);
                float a = 1.0 - smoothstep(-1.5 * fw, 1.5 * fw, sd);
                if (a <= 0.002) discard;
                return float4(team, 1.0, hsh(seed * 1.73), a);   // premultiplied by the blend: team share, wet, tone
            }
            ENDHLSL
        }

        // 1: drying — subtract a few 1/255 steps of wetness (G) from the used part of the atlas
        Pass
        {
            BlendOp RevSub, Add
            Blend One One, Zero One

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float _Dry;
            float4 vert(float3 p : POSITION) : SV_POSITION
            {
                float4 o = float4(p.xy, 0.5, 1.0);
            #if UNITY_UV_STARTS_AT_TOP
                o.y = -o.y;
            #endif
                return o;
            }
            float4 frag() : SV_Target { return float4(0.0, _Dry, 0.0, 0.0); }
            ENDHLSL
        }
    }
}
