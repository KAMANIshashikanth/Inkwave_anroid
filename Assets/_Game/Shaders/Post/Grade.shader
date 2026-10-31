// Port of src/core/renderer.js's GradeShader + three's OutputPass tone mapping (NeutralToneMapping = Khronos PBR
// Neutral at renderer.toneMappingExposure): HDR linear in (after bloom) → exposure, saturation + vibrance (muted colours
// only), log-space contrast around mid grey, lift, cool-shadow / warm-light split tone for the neutrals, vignette,
// low-health desaturation, flash → tone mapped linear out (the final blit encodes sRGB). Driven by World/StageGrade.cs.
Shader "Hidden/Inkwave/Grade"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZTest Always ZWrite Off Cull Off

        Pass
        {
            Name "Grade"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float _Sat, _Vib, _Contrast, _Lift, _Exposure, _Vignette, _Hurt, _Flash, _Aspect, _ToneExposure;
            float4 _ShadowTint, _HighTint, _HurtColor;

            float3 neutralToneMapping(float3 color)
            {
                const float StartCompression = 0.8 - 0.04;
                const float Desaturation = 0.15;
                color *= _ToneExposure;
                float x = min(color.r, min(color.g, color.b));
                float offset = x < 0.08 ? x - 6.25 * x * x : 0.04;
                color -= offset;
                float peak = max(color.r, max(color.g, color.b));
                if (peak < StartCompression) return color;
                float d = 1.0 - StartCompression;
                float newPeak = 1.0 - d * d / (peak + d - StartCompression);
                color *= newPeak / peak;
                float g = 1.0 - 1.0 / (Desaturation * (peak - newPeak) + 1.0);
                return lerp(color, newPeak.xxx, g);
            }

            half4 frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                float4 c = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);
                c.rgb *= _Exposure;
                float l = dot(c.rgb, float3(0.2126, 0.7152, 0.0722));
                // vibrance: muted colours gain saturation, already-saturated ones (team ink) barely move
                float mx = max(c.r, max(c.g, c.b)), mn = min(c.r, min(c.g, c.b));
                float chroma = (mx - mn) / max(mx, 1e-4);
                c.rgb = max(lerp(l.xxx, c.rgb, _Sat + _Vib * (1.0 - smoothstep(0.1, 0.7, chroma))), 0.0);
                // contrast in log space around mid grey (keeps HDR highlights ordered), then a cool-shadow / warm-light split tone
                c.rgb = 0.18 * pow(max(c.rgb, 1e-6) / 0.18, _Contrast) + _Lift;
                // split tone is for the world's neutrals: strongly saturated colours (team ink) keep their exact hue
                float lt = smoothstep(0.015, 0.55, l);
                c.rgb *= lerp(1.0, lerp(_ShadowTint.rgb, _HighTint.rgb, lt), 1.0 - 0.85 * smoothstep(0.35, 0.8, chroma));
                float2 q = (uv - 0.5) * float2(_Aspect, 1.0);
                float r = length(q);
                float v = smoothstep(0.55, 1.25, r);
                c.rgb *= 1.0 - _Vignette * v;
                // low health: the HUD draws the coloured edge; here only drain saturation + darken the rim slightly
                float lum = dot(c.rgb, float3(0.2126, 0.7152, 0.0722));
                c.rgb = lerp(c.rgb, lum.xxx, _Hurt * 0.45);
                c.rgb *= 1.0 - _Hurt * 0.25 * smoothstep(0.4, 1.2, r);
                c.rgb += _Flash;
                return half4(neutralToneMapping(c.rgb), 1.0);
            }
            ENDHLSL
        }
    }
}
