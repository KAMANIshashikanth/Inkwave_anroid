// environment.js's shared shader code: GLSL_SKY_COMMON (sky gradient + aerial haze, used by the sea and every far
// scenery material so everything melts into the same horizon) and GLSL_NOISE's hash / value noise. Globals under their
// JS names (World/StageEnvironment.cs envGlobals), three-space values; uTime = _StageTime (World/StageAnimator.cs).
#ifndef INKWAVE_ENV_COMMON
#define INKWAVE_ENV_COMMON

#define MIR float3(1.0, 1.0, -1.0)
#define WATER_Y -1.6
float _StageTime;
float4 uSunDir, uZenith, uSkyMid, uHorizon, uGround, uHorizonGlow, uGlowColor, uGlowParams, uHaze;
float uNight;

float3 threeCameraPos() { return _WorldSpaceCameraPos * MIR; }

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
    // dusk: the horizon opposite the sun lies in the earth's shadow — cooler and dimmer than the sunward side
    float anti = (1.0 - az) * (1.0 - az);
    c *= lerp(1.0, float3(0.7, 0.74, 0.95), uNight * anti * exp(-hp * 2.5));
    c = lerp(c, uGround.rgb, smoothstep(0.0, -0.22, h));
    return c;
}
float3 hazeColor(float3 d)
{
    float3 h = skyGradient(normalize(float3(d.x, saturate(d.y) * 0.3, d.z)));
    h = lerp(h, uSkyMid.rgb * 1.08 + uHorizon.rgb * 0.12, 0.22);   // aerial perspective: distant land turns bluer
    // looking down through the haze you see scattered ambient, not the glowing horizon band
    return lerp(h, uGround.rgb * 1.15 + uSkyMid.rgb * 0.25, smoothstep(-0.02, -0.45, d.y) * 0.75);
}
float3 applyHaze(float3 col, float3 wp)
{
    float3 dv = wp - threeCameraPos();
    float dist = length(dv);
    float3 dir = dv / max(dist, 1e-3);
    float hf = exp(-max(wp.y, 0.0) / uHaze.z);
    float f = 1.0 - exp(-dist * uHaze.x * lerp(0.45, 1.0, hf));
    return lerp(col, hazeColor(dir), min(f, uHaze.y));
}

// ---- GLSL_NOISE (the parts the scenery uses)
float hash12(float2 p) { float3 p3 = frac(p.xyx * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return frac((p3.x + p3.y) * p3.z); }
float vnoise2(float2 p)
{
    float2 i = floor(p), f = frac(p); float2 u = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(hash12(i), hash12(i + float2(1.0, 0.0)), u.x), lerp(hash12(i + float2(0.0, 1.0)), hash12(i + float2(1.0, 1.0)), u.x), u.y);
}
float fbm2(float2 p)
{
    float s = 0.0, a = 0.5;
    for (int i = 0; i < 5; i++) { s += a * vnoise2(p); p = float2(1.6 * p.x - 1.2 * p.y, 1.2 * p.x + 1.6 * p.y) + 3.7; a *= 0.5; }
    return s / 0.96875;
}
#endif
