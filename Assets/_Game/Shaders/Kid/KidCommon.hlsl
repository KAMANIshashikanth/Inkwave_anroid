// Shared parts of the squidkid shaders (port of src/game/character-mats.js): the per-character property block,
// noise, hurt splotches, the character rim/fill light and the LOD screen-door dither.
// Vertex channels (Tools/bake/iwk.mjs): POSITION · NORMAL · TEXCOORD0 uv · TEXCOORD1 colour · TEXCOORD2 aEx|aTint|aMat ·
// TEXCOORD3 (aHead|aCloth|aHair|aEyeS|aSq|aSeg).xyz + aOcc · TEXCOORD4 aFace · TEXCOORD5 rest position (skinned only).
#ifndef INKWAVE_KID_COMMON
#define INKWAVE_KID_COMMON

CBUFFER_START(UnityPerMaterial)
    float _Kind, _RimK, _Pattern, _HurtSeed, _Pupil, _EmissiveI, _Rough, _Opacity, _Metal;
    float4 _BaseColor, _Team, _Shirt, _Shorts, _Shoe, _Sole, _Sock, _Strap, _Skin, _Iris, _Iris2;
    float4 _Hurt, _Flash, _Glow, _Look, _Gaze, _Lid, _Mouth, _Mouth2, _Wig, _Rim, _RimL, _Fill, _LodFade, _Emissive, _Coil, _Clock;
CBUFFER_END

float iwHash(float3 p) { p = frac(p * 0.3183099 + float3(0.1, 0.2, 0.3)); p *= 17.0; return frac(p.x * p.y * p.z * (p.x + p.y + p.z)); }
float iwNoise(float3 x)
{
    float3 i = floor(x); float3 f = frac(x); f = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(lerp(iwHash(i), iwHash(i + float3(1, 0, 0)), f.x), lerp(iwHash(i + float3(0, 1, 0)), iwHash(i + float3(1, 1, 0)), f.x), f.y),
                lerp(lerp(iwHash(i + float3(0, 0, 1)), iwHash(i + float3(1, 0, 1)), f.x), lerp(iwHash(i + float3(0, 1, 1)), iwHash(i + float3(1, 1, 1)), f.x), f.y), f.z);
}
float iwFbm(float3 p) { return 0.56 * iwNoise(p) + 0.29 * iwNoise(p * 2.07 + 7.1) + 0.15 * iwNoise(p * 4.31 + 3.7); }

// hurt: enemy-ink splotches spreading over the body (rest-pose noise so they stick to the skin)
float3 iwHurt(float3 col, float3 bindPos)
{
    if (_Hurt.w <= 0.002) return col;
    float3 hp = bindPos * float3(10.0, 6.5, 10.0) + _HurtSeed;
    hp.y += 0.35 * iwNoise(bindPos * 23.0);
    float n = iwFbm(hp);
    float th = lerp(1.0, 0.47, saturate(_Hurt.w));
    float w = max(fwidth(n) * 0.9, 0.004);
    float m = smoothstep(th - w, th + w, n);
    float rim = smoothstep(th - 0.05, th - w, n) * (1.0 - m);
    col = lerp(col, col * 0.8, rim * 0.6);
    return lerp(col, _Hurt.rgb, m);
}

// LOD cross-fade: interleaved-gradient noise; _LodFade = (f, side): +1 outgoing keeps IGN ≥ f, −1 incoming keeps IGN < f
void iwLodDither(float4 positionCS)
{
    if (_LodFade.x > 0.0)
    {
        float n = frac(52.9829189 * frac(dot(floor(positionCS.xy), float2(0.06711056, 0.00583715))));
        if ((n < _LodFade.x) == (_LodFade.y > 0.0)) discard;
    }
}

// character light: a sky/sun-tinted fresnel rim strongest on the silhouette edge facing the sun + a soft camera-side fill
float3 iwRimFill(float3 n, float3 V, float3 albedo)
{
    float f = pow(1.0 - saturate(dot(n, V)), _Rim.w);
    float s = saturate(dot(n, _RimL.xyz) * 0.5 + 0.5);
    float3 o = _Rim.rgb * (_RimK * f * (0.3 + 0.7 * s * s));
    float3 fillDir = mul((float3x3)UNITY_MATRIX_I_V, normalize(float3(0.25, 0.45, 1.0)));
    float fl = saturate(dot(n, normalize(fillDir)) * 0.6 + 0.4);
    return o + _Fill.rgb * albedo * fl * fl;
}

#endif
