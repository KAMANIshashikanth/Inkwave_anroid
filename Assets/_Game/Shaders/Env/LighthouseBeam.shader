// Port of environment.js's lighthouse beam (sunset only): an open cone along +x, additive, brightest face-on and near
// the lamp, fading out along its 150 m, scaled by uNight (World/StageEnvironment.cs globals).
Shader "Inkwave/Env/LighthouseBeam"
{
    Properties { _Col ("Colour (linear)", Vector) = (1, 0.76, 0.39, 1) }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Blend SrcAlpha One
        ZWrite Off
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                float4 _Col;
            CBUFFER_END
            float uNight;
            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct V { float4 positionCS : SV_POSITION; float a : TEXCOORD0; float3 n : TEXCOORD1; float3 v : TEXCOORD2; };
            V vert(A i)
            {
                V o;
                o.a = saturate(i.positionOS.x / 150.0);
                float3 wp = TransformObjectToWorld(i.positionOS.xyz);
                o.n = TransformObjectToWorldNormal(i.normalOS);
                o.v = _WorldSpaceCameraPos - wp;
                o.positionCS = TransformWorldToHClip(wp);
                return o;
            }
            half4 frag(V i) : SV_Target
            {
                float e = pow(saturate(abs(dot(normalize(i.n), normalize(i.v)))), 2.0);
                float a = e * pow(max(1.0 - i.a, 0.0), 1.6) * smoothstep(0.0, 0.03, i.a) * 0.5 * uNight;
                return half4(_Col.rgb * a, 1.0);
            }
            ENDHLSL
        }
    }
}
