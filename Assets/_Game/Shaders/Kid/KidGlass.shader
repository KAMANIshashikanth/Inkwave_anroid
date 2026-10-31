// Transparent squidkid parts (port of character-mats.js getGlassMaterial / the squid ghost, first pass): the ink tank's
// glass capsule (a faint fresnel shell with a sharp sky reflection) and the local swimmer's see-through ghost.
Shader "Inkwave/Kid/Glass"
{
    Properties
    {
        _BaseColor ("Tint", Color) = (0.85, 0.92, 1, 0.14)
        _Team ("Team", Color) = (1, 0.54, 0.08, 1)
        _Ghost ("Ghost (team tinted)", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent+2" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Back

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor, _Team;
                float _Ghost;
            CBUFFER_END

            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Vy { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float3 normalWS : TEXCOORD1; float fog : TEXCOORD2; };

            Vy vert(A i)
            {
                Vy o;
                VertexPositionInputs p = GetVertexPositionInputs(i.positionOS.xyz);
                o.positionCS = p.positionCS; o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(i.normalOS);
                o.fog = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            half4 frag(Vy i) : SV_Target
            {
                float3 n = normalize(i.normalWS);
                float3 V = GetWorldSpaceNormalizeViewDir(i.positionWS);
                float f = pow(1.0 - saturate(dot(n, V)), 3.0);
                Light L = GetMainLight();
                float3 H = normalize(L.direction + V);
                float spec = pow(saturate(dot(n, H)), 180.0) * 2.5;
                float3 refl = GlossyEnvironmentReflection(reflect(-V, n), 0.05, 1.0);
                float3 tint = _Ghost > 0.5 ? _Team.rgb : _BaseColor.rgb;
                float a = _Ghost > 0.5 ? 0.18 + 0.5 * f : _BaseColor.a + 0.6 * f;
                float3 col = tint * (0.25 + 0.5 * f) + refl * f + L.color * spec;
                col = MixFog(col, i.fog);
                return half4(col, saturate(a + spec));
            }
            ENDHLSL
        }
    }
}
