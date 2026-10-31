// The Brine Cutlass's swing FX (port of kits/blade-fx.js ARC_FRAG / WAVE_FRAG): _Mode 0 = a slash arc (an annular sector
// that sweeps with the blade and fades from its tail: uv.x along the sweep, uv.y inner → outer; _P = head, fade, tail,
// heavy), _Mode 1 = the charged cut's ink wave (sheet A, uv.z = 0: the lens front, uv.xy ∈ [-1, 1]; sheet B, uv.z = 1:
// the crescent profile with its tail, uv.x 0 → 1 back along it; _P = time, fade). Colours are linear; alpha-blended,
// both sides, no depth write.
Shader "Inkwave/Fx/BladeFx"
{
    Properties
    {
        _Color ("Colour (linear)", Vector) = (1, 1, 1, 1)
        _P ("Params", Vector) = (0, 1, 0.35, 0)
        _Mode ("Mode", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }
        Pass
        {
            Name "BladeFx"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color, _P;
                float _Mode;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 uv : TEXCOORD0; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.uv = i.uv;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 col; float a;
                if (_Mode < 0.5)
                {
                    float uHead = _P.x, uFade = _P.y, uTail = _P.z, uHeavy = _P.w;
                    float u = i.uv.x, v = i.uv.y;
                    float behind = uHead - u;
                    if (behind < -0.015) discard;
                    float tail = exp(-max(behind, 0.0) / uTail);
                    float thick = lerp(0.2, 0.9, tail);
                    float body = smoothstep(1.0 - thick, 1.0 - thick + 0.3, v) * (1.0 - smoothstep(0.94, 1.0, v));
                    float rim = exp(-pow((v - 0.9) * 11.0, 2.0));
                    float head = exp(-pow(max(behind, 0.0) * 12.0, 2.0));
                    float ends = smoothstep(0.0, 0.06, u) * (1.0 - smoothstep(0.93, 1.0, u));
                    // streaks along the sweep (ink torn by the edge)
                    float streak = 0.75 + 0.25 * sin(v * 60.0 + u * 7.0);
                    a = (body * 0.78 * streak + rim * 0.95) * tail * uFade * ends;
                    col = lerp(_Color.rgb * (1.1 + 0.4 * uHeavy), float3(1, 1, 1), saturate(rim * 0.5 + head * 0.55));
                    col *= 1.0 + head * (1.4 + uHeavy);
                }
                else
                {
                    float uT = _P.x, uFade = _P.y;
                    float hot;
                    float y = i.uv.y, ends = 1.0 - pow(abs(y), 6.0);
                    if (i.uv.z < 0.5)
                    {
                        float x = i.uv.x;
                        float lens = 1.0 - x * x;
                        hot = exp(-x * x * 18.0);
                        float rimA = exp(-pow((abs(x) - 0.82) * 7.0, 2.0));
                        a = pow(max(lens, 0.0), 0.6) * ends * (0.72 + 0.1 * sin(y * 9.0 - uT * 30.0)) + rimA * 0.35 * ends;
                    }
                    else
                    {
                        float t = i.uv.x;
                        hot = exp(-t * 14.0);
                        float streak = 0.6 + 0.4 * sin(y * 23.0 + t * 5.0 - uT * 20.0);
                        a = ends * exp(-t * 2.6) * (0.35 + 0.65 * hot) * lerp(1.0, streak, smoothstep(0.05, 0.4, t)) * smoothstep(1.0, 0.75, t);
                    }
                    a *= uFade;
                    col = lerp(_Color.rgb * 1.25, float3(1, 1, 1), hot * 0.7) * (1.0 + 1.6 * hot);
                }
                return half4(col, saturate(a));
            }
            ENDHLSL
        }
    }
}
