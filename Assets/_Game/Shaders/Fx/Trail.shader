// Tracer Bolt trail ribbon (port of RIB_VS / RIB_FS in kits/tracer.js): a camera-facing strip through the bolt's path,
// built on the CPU each frame. uv.x = the side (-1 … 1 across), uv.y = when that point was laid (the kit's clock); each
// point fades out _Life seconds after it was laid (_Time2 = the clock now). _Color linear. Double-sided, alpha blended.
Shader "Inkwave/Fx/Trail"
{
    Properties
    {
        _Color ("Colour (linear)", Vector) = (1, 1, 1, 1)
        _Time2 ("Clock", Float) = 0
        _Life ("Life", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }
        Pass
        {
            Name "Trail"
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
                float _Time2;
                float _Life;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 info : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float side : TEXCOORD0; float age : TEXCOORD1; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.side = i.info.x;
                o.age = saturate((_Time2 - i.info.y) / _Life);
                return o;
            }
            float4 frag(Varyings i) : SV_Target
            {
                float s = abs(i.side);
                float edge = 1.0 - smoothstep(0.1, 1.0, s);
                edge *= edge;
                float core = 1.0 - smoothstep(0.0, 0.22, s);
                float fade = pow(1.0 - i.age, 1.5);
                float a = (edge * 0.85 + core * 0.15) * fade;
                clip(a - 0.01);
                float3 c = lerp(_Color.rgb * 1.45, float3(1, 1, 1), core * 0.7 * (1.0 - i.age * 0.7));
                return float4(c, a);
            }
            ENDHLSL
        }
    }
}
