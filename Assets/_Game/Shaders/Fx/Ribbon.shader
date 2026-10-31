// Camera-facing ribbon (port of weapons.js RIBBON_VERT / RIBBON_FRAG): charger beams (_Mode 0) and laser sights (_Mode 1).
// The strip (x ∈ {-1, 1} across, z 0..1 along +Z) turns about its own axis to face the camera, so the streak keeps its
// full width even when seen nearly end-on (your own shot from behind the shoulder). Object transform: position = muzzle,
// +Z = direction, scale.z = length. Colours are linear; URP's post stack does the tone mapping three did in-shader.
Shader "Inkwave/Fx/Ribbon"
{
    Properties
    {
        _Color ("Colour (linear)", Vector) = (1, 1, 1, 1)
        _T ("Time", Float) = 0
        _Life ("Life", Float) = 0.34
        _Len ("Length", Float) = 1
        _Charge ("Charge", Float) = 0
        _Seed ("Seed", Float) = 0
        _Mode ("Mode", Float) = 0
        _Width ("Width", Float) = 0.05
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }
        Pass
        {
            Name "Ribbon"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _T, _Life, _Len, _Charge, _Seed, _Mode, _Width;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                float3 P = TransformObjectToWorld(float3(0, 0, i.positionOS.z));
                float3 A = normalize(mul((float3x3)GetObjectToWorldMatrix(), float3(0, 0, 1)));
                float3 toCam = _WorldSpaceCameraPos - P;
                float3 S = cross(A, toCam);
                float sl = length(S);
                S = sl > 1e-5 ? S / sl : float3(1, 0, 0);
                float w = max(_Width, length(toCam) * 0.0016);   // never thinner than ~1 px (no shimmer at range)
                P += S * i.positionOS.x * w;
                o.uv = float2(i.positionOS.x, i.positionOS.z * _Len);
                o.positionCS = TransformWorldToHClip(P);
                return o;
            }

            float iwH(float x) { return frac(sin(x * 78.233 + _Seed * 13.71) * 43758.5453); }
            float iwN(float x) { float i = floor(x), f = frac(x); f = f * f * (3.0 - 2.0 * f); return lerp(iwH(i), iwH(i + 1.0), f); }

            half4 frag(Varyings i) : SV_Target
            {
                float v = i.uv.x, m = i.uv.y;
                float core = exp(-v * v * 16.0), glow = exp(-v * v * 3.0) * (1.0 - v * v);
                float3 uColor = _Color.rgb;
                float3 col; float a;
                if (_Mode < 0.5)
                {
                    // fired shot: tracer front races out, white-hot core snaps off, the ink sheath breaks into dashes as it goes
                    float t = _T;
                    float front = t * 1100.0;
                    float drawn = smoothstep(front + 0.6, front - 0.6, m);
                    float head = exp(-max(0.0, front - m) * 0.8) * (1.0 - smoothstep(0.02, 0.05, t));
                    float k0 = -0.35 + 1.5 * (t / _Life);
                    float br = iwN(m * 1.9 - t * 7.0) * 0.72 + iwN(m * 6.3 + 3.1) * 0.28;
                    float keep = smoothstep(k0, k0 + 0.3, br);
                    float ca = core * min(1.0, exp(-t * 11.0) * 1.3 + head);
                    float ga = glow * 0.85 * exp(-t * 4.5) * keep;
                    float3 hot = lerp(uColor, float3(1, 1, 1), 0.6) * (2.2 + 3.2 * _Charge + 3.0 * head);
                    col = uColor * (1.05 + 0.5 * _Charge) * ga + hot * ca;
                    a = saturate(ga + ca);
                    col /= max(a, 1e-3);
                    a *= drawn * smoothstep(0.0, 0.3, m) * smoothstep(_Len + 0.05, _Len - 0.12, m);
                }
                else
                {
                    // laser sight: pulses crawl toward the target faster as the charge builds; white-hot shimmer at full charge
                    float ch = _Charge, full = step(0.995, ch);
                    float dash = 0.55 + 0.45 * smoothstep(0.3, 0.7, frac(m * 1.4 - _T * (1.5 + 7.0 * ch)));
                    float ca = core * (0.35 + 0.65 * ch) * dash;
                    float ga = glow * (0.1 + 0.28 * ch) * dash;
                    float3 hot = lerp(uColor, float3(1, 1, 1), 0.3 + 0.45 * full) * (1.0 + 2.4 * ch * ch + full * (0.7 + 0.5 * sin(_T * 42.0)));
                    col = uColor * ga + hot * ca;
                    a = saturate(ga + ca);
                    col /= max(a, 1e-3);
                    a *= smoothstep(0.04, 0.5, m) * smoothstep(_Len + 0.02, _Len - 0.3, m);
                }
                return half4(col, a);
            }
            ENDHLSL
        }
    }
}
