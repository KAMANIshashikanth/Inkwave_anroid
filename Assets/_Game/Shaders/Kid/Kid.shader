// The squidkid's opaque materials (port of src/game/character-mats.js, first pass): one program, the part picked by
// _Kind — 0 skin · 1 cloth · 2 hair · 3 eye · 4 squid · 5 dark (vertex colour) · 6 weapon plastic (aMat classes) ·
// 7 team ink · 8 lamp · 9 charge coil · 10 tank ink fill · 11 glow · 12 matte prop · 13 canopy fabric · 14 blade edge · 15 sponge · 16 ink fist. Every vertex contract of the baked meshes is honoured
// (colour sources, part ids, material classes, AO, eye space); the finer GLSL detail (cloth graphics, lid/mouth rig,
// parallax iris, gummy hair) is ported part by part on top of this.
Shader "Inkwave/Kid/Body"
{
    Properties
    {
        _Kind ("Kind", Float) = 0
        _BaseColor ("Base", Color) = (1, 1, 1, 1)
        _Team ("Team", Color) = (1, 0.54, 0.08, 1)
        _Skin ("Skin", Color) = (1, 0.85, 0.76, 1)
        _Shirt ("Shirt", Color) = (0.95, 0.95, 0.93, 1)
        _Shorts ("Shorts", Color) = (0.16, 0.2, 0.31, 1)
        _Shoe ("Shoe", Color) = (0.16, 0.17, 0.21, 1)
        _Sole ("Sole", Color) = (0.95, 0.95, 0.93, 1)
        _Sock ("Sock", Color) = (0.97, 0.97, 0.96, 1)
        _Strap ("Strap", Color) = (0.2, 0.21, 0.25, 1)
        _Iris ("Iris top", Color) = (1, 0.7, 0.11, 1)
        _Iris2 ("Iris bottom", Color) = (1, 0.42, 0, 1)
        _Emissive ("Emissive", Color) = (0, 0, 0, 1)
        _EmissiveI ("Emissive intensity", Float) = 0
        _Rough ("Roughness", Float) = 0.5
        _Metal ("Metallic", Float) = 0
        _Cull ("Cull", Float) = 2
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        Cull [_Cull]

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fog

            #define _CLEARCOAT 1
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "KidCommon.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION; float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0; float4 col : TEXCOORD1; float ex : TEXCOORD2; float4 v3 : TEXCOORD3; float4 face : TEXCOORD4; float3 bind : TEXCOORD5;
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0; float3 normalWS : TEXCOORD1;
                float4 uvEx : TEXCOORD2;     // uv, ex, fog
                float4 col : TEXCOORD3; float4 v3 : TEXCOORD4; float4 face : TEXCOORD5; float3 bind : TEXCOORD6;
            };

            Varyings vert(Attributes i)
            {
                Varyings o;
                float3 pos = i.positionOS.xyz;
                int kind = (int)round(_Kind);
                // squid arms: a travelling sideways wave down each arm (colour g = weight along the arm, b = phase)
                if (kind == 4)
                {
                    float part = round(i.ex);
                    if (part >= 0.5 && part <= 3.5)
                    {
                        float w = i.col.g;
                        float3 radial = normalize(float3(pos.x, 0.0, pos.z) + float3(1e-4, 0, 0));
                        float3 side = cross(float3(0, 1, 0), radial);
                        pos += side * (_Wig.x * pow(saturate(w), 1.5) * sin(_Clock.x * _Wig.y - w * 5.0 + i.col.b * 6.2831853));
                    }
                }
                VertexPositionInputs p = GetVertexPositionInputs(pos);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(i.normalOS);
                o.uvEx = float4(i.uv, i.ex, ComputeFogFactor(p.positionCS.z));
                o.col = i.col; o.v3 = i.v3; o.face = i.face;
                o.bind = kind <= 3 ? i.bind : i.positionOS.xyz;
                return o;
            }

            // ---- skin: the Inkling eye mask in the head's (az, el) (character-face.js visorSD)
            float iwSmax(float a, float b, float k) { float h = saturate(0.5 + 0.5 * (a - b) / k); return lerp(b, a, h) + k * h * (1.0 - h); }
            float iwVisorSD(float az, float el)
            {
                float ax = abs(az);
                float lobe = (1.0 - length(float2((ax - 0.355) / 0.3, (el - 0.15) / 0.29))) * 0.26;
                float bridge = ax < 0.42 ? 0.1 + 0.1 * smoothstep(0.0, 0.36, ax) - abs(el - 0.17) : -1.0;
                float s = (ax - 0.42) / 0.9;
                float wc = 0.16 + 0.2 * pow(saturate(s), 1.35);
                float wh = 0.205 * pow(saturate(1.0 - s), 0.8);
                float wing = (s > -0.1 && s <= 1.0) ? wh - abs(el - wc) : -1.0;
                return iwSmax(iwSmax(lobe, bridge, 0.06), wing, 0.07);
            }

            // sponge pores (kind 15): a 3D Worley cell distance
            float3 spHash(float3 p)
            {
                p = float3(dot(p, float3(127.1, 311.7, 74.7)), dot(p, float3(269.5, 183.3, 246.1)), dot(p, float3(113.5, 271.9, 124.6)));
                return frac(sin(p) * 43758.5453);
            }
            float spPore(float3 p)
            {
                float3 i0 = floor(p), f = frac(p); float d = 8.0;
                for (int z = -1; z <= 1; z++) for (int y = -1; y <= 1; y++) for (int x = -1; x <= 1; x++)
                {
                    float3 g = float3(x, y, z);
                    float3 r = g + 0.1 + 0.8 * spHash(i0 + g) - f;
                    d = min(d, dot(r, r));
                }
                return 1.0 - smoothstep(0.02, 0.17, d);
            }

            float3 contrast(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)) > 0.45 ? c * 0.35 : lerp(c, float3(1, 1, 1), 0.75); }

            half4 frag(Varyings i) : SV_Target
            {
                iwLodDither(i.positionCS);
                int kind = (int)round(_Kind);
                float3 n = normalize(i.normalWS);
                float2 uv = i.uvEx.xy; float ex = i.uvEx.z;
                float3 V = GetWorldSpaceNormalizeViewDir(i.positionWS);
                float3 albedo = 1; float rough = 0.5; float metal = 0; float coat = 0; float coatRough = 0.1; float ao = 1;
                float3 emit = 0; bool hurt = false;
                float3 team = _Team.rgb, skin = _Skin.rgb;

                if (kind == 0)
                {
                    // ---- SKIN: sub-materials (aEx) 0 skin · 1 nail · 2 ear cup · 8 ear shell · 9 lid margin · 10 cavity · 11 teeth · 12 tongue
                    float sub = round(ex);
                    albedo = skin; rough = 0.55; hurt = true; ao = saturate(i.face.w);
                    if (sub == 1) { albedo = lerp(skin, float3(1, 0.92, 0.9), 0.4); rough = 0.3; coat = 0.6; }
                    else if (sub == 2) albedo = skin * float3(0.86, 0.68, 0.66);
                    else if (sub == 9) albedo = skin * float3(0.9, 0.62, 0.6);
                    else if (sub == 10) { albedo = float3(0.22, 0.045, 0.05); rough = 0.4; }
                    else if (sub == 11) { albedo = float3(0.9, 0.89, 0.85); rough = 0.3; }
                    else if (sub == 12) { albedo = float3(0.75, 0.24, 0.27); rough = 0.35; }
                    float3 hd = i.v3.xyz;
                    if (sub == 0 && dot(hd, hd) > 0.25)
                    {
                        hd = normalize(hd);
                        float sd = iwVisorSD(atan2(hd.x, hd.z), asin(clamp(hd.y, -1, 1)));
                        float w = max(fwidth(sd), 1e-4);
                        float m = smoothstep(-w, w, sd);
                        // glossy black lacquer mask
                        albedo = lerp(albedo, float3(0.018, 0.02, 0.028), m);
                        rough = lerp(rough, 0.18, m); coat = max(coat, m);
                    }
                    // blush (cheeks under the mask)
                    if (_Mouth2.w > 0.01 && sub == 0 && dot(i.v3.xyz, i.v3.xyz) > 0.25)
                    {
                        float3 h2 = normalize(i.v3.xyz);
                        float az = abs(atan2(h2.x, h2.z)), el = asin(clamp(h2.y, -1, 1));
                        float b = exp(-pow((az - 0.62) / 0.18, 2) - pow((el + 0.05) / 0.12, 2));
                        albedo = lerp(albedo, albedo * float3(1.05, 0.62, 0.62), saturate(b * _Mouth2.w));
                    }
                }
                else if (kind == 1)
                {
                    // ---- CLOTH: colour source (aEx), part id + material class (aCloth.xy), AO (aOcc)
                    float slot = round(ex), cls = round(i.v3.y);
                    float3 base = 1;
                    if (slot == 1) base = team;
                    else if (slot == 2) base = _Shirt.rgb;
                    else if (slot == 3) base = _Shorts.rgb;
                    else if (slot == 4) base = _Shoe.rgb;
                    else if (slot == 5) base = _Sock.rgb;
                    else if (slot == 6) base = _Sole.rgb;
                    else if (slot == 7) base = _Strap.rgb;
                    else if (slot == 8) base = contrast(_Shoe.rgb);
                    else if (slot == 9) base = lerp(_Sole.rgb * 0.3 + 0.035, float3(0.09, 0.09, 0.1), 0.35);
                    else if (slot == 10) base = float3(0.78, 0.8, 0.84);
                    else if (slot == 11) base = _Strap.rgb * 0.55 + 0.02;
                    else if (slot == 12) base = lerp(_Sock.rgb, 0.96, 0.6);
                    else if (slot == 13) base = team * 0.62;
                    else if (slot == 14) base = lerp(_Shorts.rgb, team, 0.5);
                    albedo = base; hurt = true;
                    ao = 1.0 - clamp(i.v3.w, 0.0, 0.95);
                    // material class: 0 jersey · 1 twill · 2 rib · 3 leather · 4 foam · 5 rubber · 6 plastic · 7 metal · 8 webbing · 9 mesh · 10 lace · 11 padding · 12 print
                    rough = cls == 3 ? 0.45 : cls == 4 ? 0.7 : cls == 5 ? 0.62 : cls == 6 ? 0.35 : cls == 7 ? 0.3 : cls == 12 ? 0.6 : cls == 1 ? 0.78 : 0.84;
                    if (cls == 7) metal = 0.85;
                    if (cls == 3 || cls == 6) coat = 0.35;
                    // weave micro-shading so the fabric does not read as plastic
                    if (cls <= 2 || cls == 8 || cls == 9) albedo *= 0.94 + 0.06 * iwNoise(i.bind * 900.0);
                }
                else if (kind == 2)
                {
                    // ---- HAIR: aTint (+ lighter / − darker team ink; ≤ −1.5 gear) · colour = strand data | cap | gear rgb
                    float tint = ex; float3 c = i.col.rgb;
                    float lum = dot(team, float3(0.299, 0.587, 0.114));
                    float3 light = lerp(team, 1.0, 0.55);
                    float3 core = pow(max(team, 1e-4), 1.35) * lerp(0.8, 0.9, saturate(lum * 1.2));
                    ao = saturate(i.v3.z > 0.001 ? i.v3.z : 1.0); hurt = true;
                    if (tint <= -1.5)
                    {
                        // gear accessory: colour code in r (>= 0 literal rgb; −1 team, −2 shirt, −3 strap, −4 shorts, each × g)
                        float3 gc = c;
                        if (gc.r < -0.5) { float code = round(-gc.r); gc = (code == 1 ? team : code == 2 ? _Shirt.rgb : code == 3 ? _Strap.rgb : _Shorts.rgb) * gc.g; }
                        albedo = gc; rough = 0.6;
                        float cls = round(-tint - 2.0);
                        if (cls == 0) { rough = 0.35; coat = 0.6; }        // plastic
                        else if (cls == 1) { rough = 0.3; metal = 0.8; }   // metal
                    }
                    else if (c.b >= 1.5)
                    {
                        // scalp cap: deep ink at the hairline, brighter toward the crown
                        float edge = smoothstep(0.0, 0.25, c.g);
                        albedo = lerp(lerp(core, team, 0.5), team, 0.45 + 0.55 * edge);
                        rough = 0.3; coat = 1; coatRough = 0.05;
                    }
                    else
                    {
                        // tentacle strands: gummy ink, darker down the core, lighter on the + tint locks
                        float tAlong = c.r;
                        albedo = tint >= 0 ? lerp(team, light, saturate(tint)) : team * (1.0 + max(tint, -0.9));
                        albedo = lerp(albedo, lerp(core, albedo, 0.6), 0.35 * (1.0 - saturate(tAlong)));
                        // printed suckers on the underside (colour g = sucker flag, b = section sin)
                        float under = saturate(-(c.b * 2.0 - 1.0));
                        albedo = lerp(albedo, lerp(albedo, light, 0.5), c.g * under * 0.6);
                        rough = 0.3; coat = 1; coatRough = 0.05;
                        emit += team * 0.06 * (1.0 - lum);   // jelly self-light
                    }
                }
                else if (kind == 3)
                {
                    // ---- EYES: socketed balls (aEx = ±2, aEyeS = eye-space position) / flat painted patches (aEx = ±1, squid)
                    float side = ex > 0 ? 1.0 : -1.0;
                    rough = 0.14; coat = 1; coatRough = 0.03;
                    float3 sclera = float3(0.95, 0.94, 0.92);
                    if (abs(ex) > 1.5)
                    {
                        float3 s = i.v3.xyz;
                        // gaze: the ball turns in eye space (yaw about +Y, pitch about −X); the iris is the ball's +Z cap
                        float yaw = -0.06 + side * clamp(0.6 * (_Look.x * 1.25 + (side > 0 ? _Gaze.x : _Gaze.z)), -0.36, 0.36);
                        float pitch = clamp(0.6 * (_Look.y * 1.25 + (side > 0 ? _Gaze.y : _Gaze.w)), -0.3, 0.3);
                        float cy = cos(yaw), sy = sin(yaw), cp = cos(pitch), sp = sin(pitch);
                        // inverse rotation (Ry·Rx)ᵀ applied to the surface point
                        float3 a = float3(cy * s.x - sy * s.z, s.y, sy * s.x + cy * s.z);
                        float3 b = float3(a.x, cp * a.y - sp * a.z, sp * a.y + cp * a.z);
                        float3 d = normalize(b);
                        float th = acos(clamp(d.z, -1, 1));
                        const float IRIS = 0.37;
                        float r = th / IRIS;
                        float2 iq = d.xy / max(length(d.xy), 1e-4) * r;
                        float wI = max(fwidth(r), 1e-3);
                        float irisM = 1.0 - smoothstep(1.0 - wI, 1.0 + wI, r);
                        float3 ic = lerp(_Iris2.rgb, _Iris.rgb, saturate(0.5 + 0.5 * iq.y));
                        ic *= 0.85 + 0.15 * sin(atan2(iq.y, iq.x) * 18.0) * smoothstep(0.3, 0.9, r);   // radial fibres
                        ic = lerp(ic * 0.45, ic, smoothstep(1.0, 0.82, r));                          // limbal ring
                        float pr = lerp(0.36, 0.58, _Pupil);
                        float pupil = (1.0 - smoothstep(pr - wI, pr + wI, r)) * irisM;
                        albedo = lerp(sclera, ic, irisM);
                        albedo = lerp(albedo, float3(0.01, 0.009, 0.022), pupil);
                        emit += ic * irisM * (1.0 - pupil) * 0.07;
                        // lids: the upper lid comes down with blinks / squints, the lower one rises with squints / smiles
                        float closeU = side > 0 ? _Lid.x : _Lid.y, closeL = side > 0 ? _Lid.z : _Lid.w;
                        float meet = -0.12;
                        float up = lerp(0.62, meet, saturate(closeU)), lo = lerp(-0.6, meet, saturate(closeL) * 0.6);
                        float wl = max(fwidth(s.y), 1e-3);
                        float lidM = max(smoothstep(up - wl, up + wl, s.y), smoothstep(lo + wl, lo - wl, s.y));
                        float lash = (1.0 - smoothstep(0.0, 0.07, abs(s.y - up))) * step(0.02, closeU);
                        albedo = lerp(albedo, float3(0.03, 0.02, 0.03), lash * (1.0 - lidM));
                        albedo = lerp(albedo, skin * 0.92, lidM);
                        rough = lerp(rough, 0.55, lidM); coat *= 1.0 - lidM;
                        // contact shadow under the upper lid
                        ao = 1.0 - 0.4 * smoothstep(up - 0.3, up, s.y);
                    }
                    else
                    {
                        // painted patch (polar uv): iris disc + pupil, looking with the squid's gaze
                        float2 q = uv - float2(_Look.x * 0.3 * side, _Look.y * 0.3);
                        float r = length(q) / 0.66;
                        float wI = max(fwidth(r), 1e-3);
                        float iris = 1.0 - smoothstep(1.0 - wI, 1.0 + wI, r);
                        float3 ic = lerp(_Iris2.rgb, _Iris.rgb, saturate(0.5 + 0.5 * q.y / 0.66));
                        float pupil = 1.0 - smoothstep(0.45 - wI, 0.45 + wI, r);
                        albedo = lerp(sclera, ic, iris);
                        albedo = lerp(albedo, float3(0.012, 0.01, 0.028), pupil);
                        emit += albedo * 0.07;
                    }
                }
                else if (kind == 4)
                {
                    // ---- SQUID: colour = (tint, wiggle weight, phase) · aEx = part (0 mantle · 1/3 arm · 2 sucker) · aSq.z = AO
                    float part = round(ex);
                    float tint = i.col.r;
                    float3 light = lerp(team, 1.0, 0.45);
                    albedo = tint >= 0 ? lerp(team, light, saturate(tint) * 0.6) : team * (1.0 + max(tint, -0.8));
                    if (part == 2) albedo = lerp(albedo, light, 0.45);
                    ao = i.v3.z > 0.001 ? saturate(i.v3.z) : 1.0;
                    rough = 0.28; coat = 1; coatRough = 0.05;
                    emit += team * 0.05;
                }
                else if (kind == 5)
                {
                    // ---- dark lacquer (squid visor band)
                    albedo = i.col.rgb; rough = 0.25; coat = 1; coatRough = 0.03;
                }
                else if (kind == 6)
                {
                    // ---- weapon plastic: vertex colour, aMat = 0 satin · 1 gloss · 2 rubber · 3 metal · 4 lens · 5 LED · 6 print
                    float m = round(ex);
                    albedo = i.col.rgb;
                    rough = m == 1 ? 0.18 : m == 2 ? 0.85 : m == 3 ? 0.28 : m == 4 ? 0.05 : m == 5 ? 0.3 : m == 6 ? 0.55 : 0.48;
                    if (m == 3) metal = 0.9;
                    if (m == 1 || m == 4) { coat = 1; coatRough = 0.05; }
                    if (m == 5) emit += albedo * 1.5;
                }
                else if (kind == 7)
                {
                    // ---- team ink gloss (weapon tanks, bomb bulbs)
                    albedo = _BaseColor.rgb; rough = 0.12; coat = 1; coatRough = 0.04;
                    emit += albedo * 0.06;
                }
                else if (kind == 8)
                {
                    // ---- lamp (LEDs, scope lenses, hot ports)
                    albedo = _BaseColor.rgb; rough = _Rough;
                    emit += _Emissive.rgb * _EmissiveI;
                }
                else if (kind == 9)
                {
                    // ---- charge coil: the rings light one after another as the charge passes their aSeg threshold
                    float seg = i.v3.x, ch = _Coil.x, full = _Coil.y, flash = _Coil.z, tm = _Coil.w;
                    float lit = smoothstep(seg - 0.1, seg + 0.01, ch);
                    float edge = exp(-pow((ch - seg + 0.05) * 11.0, 2.0)) * (1.0 - full);
                    float I = 0.05 + lit * (0.9 + 1.7 * ch) + edge * (1.3 + 0.9 * sin(tm * 57.0)) + full * (1.1 + 1.0 * (0.5 + 0.5 * sin(tm * 34.0 - seg * 14.0))) + flash * 6.0;
                    albedo = float3(0.106, 0.11, 0.133); rough = 0.3; metal = 0.25;
                    emit += _Emissive.rgb * I + flash * 2.0;
                }
                else if (kind == 10)
                {
                    // ---- tank ink fill
                    albedo = team; rough = 0.1; coat = 1; coatRough = 0.03;
                    emit += _Emissive.rgb * _EmissiveI;
                }
                else if (kind == 12)
                {
                    // ---- matte prop (bomb caps, storm clouds): plain lit colour + emissive
                    albedo = _BaseColor.rgb; rough = _Rough; metal = _Metal;
                    emit += _Emissive.rgb * _EmissiveI;
                }
                else if (kind == 15)
                {
                    // ---- Sponge Mitts pad (kits/mitts-model.js makeSpongeMaterial): team-ink sponge — Worley pores (two octaves, object
                    // space so they ride the squash) darken + roughen it, faded out once smaller than a pixel; a wet coat on top.
                    // _Coil = glow (the leap charge soaks it up), soak. (The pores' screen-space bump is not ported.)
                    float3 q = i.bind * 92.0;
                    float fd = 1.0 - smoothstep(0.35, 0.9, length(fwidth(q)));
                    float h = saturate((spPore(q) + 0.55 * spPore(q * 2.1 + 3.7)) * fd);
                    albedo = _BaseColor.rgb * (1.0 - 0.42 * h * _Coil.y); rough = min(1.0, 0.34 + 0.45 * h); coat = 0.7; coatRough = 0.18;
                    emit += _BaseColor.rgb * _Coil.x * (0.55 + 0.45 * (1.0 - h));
                }
                else if (kind == 16)
                {
                    // ---- Mitts ink fist (mitts.js fist material): glossy team ink with a rim glow so it reads fast
                    float fr = 1.0 - abs(dot(n, V));
                    albedo = _BaseColor.rgb; rough = 0.12; coat = 1; coatRough = 0.05;
                    emit += albedo * (0.18 + 0.9 * fr * fr * fr);
                }
                else if (kind == 14)
                {
                    // ---- Cutlass ink edge (kits/blade-model.js makeEdgeMaterial): team ink at rest (a faint glow), the charge (_Coil =
                    // charge, full, flash, time) fills it with light from the hilt (aSeg 0) to the tip (1) with a hot front at the fill
                    // line; full charge ripples toward the tip; the release flashes white-hot
                    float seg = i.v3.x, ch = _Coil.x, full = _Coil.y, flash = _Coil.z, tm = _Coil.w;
                    float lit = smoothstep(seg - 0.06, seg + 0.01, ch);
                    float front = exp(-pow((ch - seg) * 16.0, 2.0)) * (1.0 - full) * step(0.02, ch);
                    float rip = full * (1.2 + 1.3 * pow(0.5 + 0.5 * sin(tm * 26.0 - seg * 22.0), 3.0));
                    float I = 0.22 + lit * (0.9 + 2.2 * ch) + front * (2.5 + 1.0 * sin(tm * 61.0)) + rip + flash * 6.0;
                    albedo = _BaseColor.rgb; rough = 0.16; metal = 0.05;
                    emit += _Emissive.rgb * I + (flash * 2.5 + front * 0.6 + full * 0.25);
                }
                else if (kind == 13)
                {
                    // ---- canopy fabric (Canopy Brolly gores): vertex colour × _BaseColor (team ink / white), clearcoat = _Metal,
                    // emissive = hit flash + low-hp flicker; see-through via the _LodFade dither (the owner's open canopy, fades)
                    albedo = i.col.rgb * _BaseColor.rgb; rough = _Rough; coat = _Metal; coatRough = 0.12;
                    emit += _Emissive.rgb * _EmissiveI;
                }
                else
                {
                    // ---- glow (charger lamp / special-ready glow)
                    albedo = _BaseColor.rgb; rough = 0.4;
                    emit += _Emissive.rgb * _EmissiveI;
                }

                if (hurt) albedo = iwHurt(albedo, i.bind);

                InputData inputData = (InputData)0;
                inputData.positionWS = i.positionWS;
                inputData.positionCS = i.positionCS;
                inputData.normalWS = n;
                inputData.viewDirectionWS = V;
                inputData.shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                inputData.fogCoord = i.uvEx.w;
                inputData.bakedGI = SampleSH(n) * ao;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);
                SurfaceData surf = (SurfaceData)0;
                surf.albedo = albedo;
                surf.metallic = metal;
                surf.smoothness = 1.0 - rough;
                surf.normalTS = float3(0, 0, 1);
                surf.occlusion = ao;
                surf.alpha = 1;
                surf.emission = emit;
                surf.clearCoatMask = coat;
                surf.clearCoatSmoothness = 1.0 - coatRough;
                half4 col = UniversalFragmentPBR(inputData, surf);
                if (kind <= 4) col.rgb += iwRimFill(n, V, albedo);
                // invuln flash + special-ready glow
                col.rgb += _Flash.rgb + (kind <= 2 ? _Glow.rgb * 0.5 : 0);
                col.rgb = min(col.rgb, 8.0);
                col.rgb = MixFog(col.rgb, i.uvEx.w);
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
            #include "KidCommon.hlsl"
            float3 _LightDirection;
            float3 _LightPosition;
            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Vy { float4 positionCS : SV_POSITION; };
            Vy vert(A i)
            {
                Vy o;
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
                o.positionCS = cs;
                return o;
            }
            half4 frag(Vy i) : SV_Target { return 0; }
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
            #include "KidCommon.hlsl"
            struct A { float4 positionOS : POSITION; };
            struct Vy { float4 positionCS : SV_POSITION; };
            Vy vert(A i) { Vy o; o.positionCS = TransformObjectToHClip(i.positionOS.xyz); return o; }
            half4 frag(Vy i) : SV_Target { iwLodDither(i.positionCS); return 0; }
            ENDHLSL
        }
    }
}
