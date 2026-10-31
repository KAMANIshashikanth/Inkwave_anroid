// Stage dressing material (World/StageMaterials.cs): three's MeshStandardMaterial / MeshPhysicalMaterial (PBR: base
// colour × vertex colour × map, roughness, metalness, clear coat, emissive, alpha test, side) and MeshBasicMaterial
// (PROP_UNLIT) through URP. Colours arrive linear. Transparent materials set the blend / zwrite properties.
// PROP_GULL: environment.js HZ_VERT_BEGIN_GULL — wings flap / glide in object space (aPhase in TEXCOORD1.x, _StageTime).
Shader "Inkwave/Prop"
{
    Properties
    {
        _BaseColor ("Colour (linear)", Vector) = (1, 1, 1, 1)
        _BaseMap ("Map", 2D) = "white" {}
        _UseVC ("Vertex colours", Float) = 0
        _Roughness ("Roughness", Float) = 1
        _Metallic ("Metalness", Float) = 0
        _Cutoff ("Alpha test", Float) = 0
        _Emission ("Emissive (linear)", Vector) = (0, 0, 0, 0)
        _ClearCoat ("Clear coat", Float) = 0
        _ClearCoatRough ("Clear coat roughness", Float) = 0
        _InstColor ("Instance colour", Color) = (1, 1, 1, 1)
        _Cull ("Cull", Float) = 2
        _SrcBlend ("Src blend", Float) = 1
        _DstBlend ("Dst blend", Float) = 0
        _ZWrite ("ZWrite", Float) = 1
        _OffF ("Offset factor", Float) = 0
        _OffU ("Offset units", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseColor, _BaseMap_ST, _Emission, _InstColor;
            float _UseVC, _Roughness, _Metallic, _Cutoff, _ClearCoat, _ClearCoatRough;
            float _Cull, _SrcBlend, _DstBlend, _ZWrite, _OffF, _OffU;
        CBUFFER_END
        float _StageTime;   // environment time (World/StageAnimator.cs)
        float3 propVertex(float3 p, float phase)
        {
        #if PROP_GULL
            float side = abs(p.x);
            float glide = 0.35 + 0.65 * smoothstep(-0.3, 0.6, sin(_StageTime * 0.55 + phase * 17.0));
            float flap = sin(_StageTime * 8.5 + phase * 6.2831);
            p.y += flap * glide * side * 0.62 * smoothstep(0.08, 0.35, side);
            p.y -= 0.12 * side * side;
        #endif
            return p;
        }
        float4 propAlbedo(float4 vc, float2 uv)
        {
            float4 c = _BaseColor * float4(_InstColor.rgb, 1);
            if (_UseVC > 0.5) c *= vc;
            return c * SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv);
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull [_Cull]
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            Offset [_OffF], [_OffU]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fog
            #pragma multi_compile_local _ PROP_UNLIT
            #pragma multi_compile_local_vertex _ PROP_GULL
            #pragma multi_compile_local_fragment _ PROP_COAT
            #if PROP_COAT
                #define _CLEARCOAT 1
            #endif
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float4 color : COLOR; float2 uv : TEXCOORD0; float2 uv1 : TEXCOORD1; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float4 color : TEXCOORD2;
                float2 uv : TEXCOORD3;
                float fogCoord : TEXCOORD4;
            };

            Varyings vert(Attributes i)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(propVertex(i.positionOS.xyz, i.uv1.x));
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(i.normalOS);
                o.color = i.color;
                o.uv = i.uv;
                o.fogCoord = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            half4 frag(Varyings i, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target
            {
                float4 alb = propAlbedo(i.color, i.uv);
                if (_Cutoff > 0.0) clip(alb.a - _Cutoff);
            #if PROP_UNLIT
                float3 col = alb.rgb;
            #else
                float3 n = normalize(i.normalWS) * IS_FRONT_VFACE(face, 1.0, -1.0);
                InputData inputData = (InputData)0;
                inputData.positionWS = i.positionWS;
                inputData.positionCS = i.positionCS;
                inputData.normalWS = n;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(i.positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                inputData.fogCoord = i.fogCoord;
                inputData.bakedGI = SampleSH(n);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);
                SurfaceData surf = (SurfaceData)0;
                surf.albedo = alb.rgb;
                surf.metallic = _Metallic;
                surf.smoothness = 1.0 - _Roughness;
                surf.normalTS = float3(0, 0, 1);
                surf.occlusion = 1;
                surf.alpha = alb.a;
                surf.emission = _Emission.rgb;
                surf.clearCoatMask = _ClearCoat;
                surf.clearCoatSmoothness = 1.0 - _ClearCoatRough;
                float3 col = UniversalFragmentPBR(inputData, surf).rgb;
            #endif
                col = MixFog(col, i.fogCoord);
                return half4(col, alb.a);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection;
            float3 _LightPosition;
            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; float4 color : COLOR; float2 uv : TEXCOORD0; };
            struct V { float4 positionCS : SV_POSITION; float4 color : TEXCOORD0; float2 uv : TEXCOORD1; };
            V vert(A i)
            {
                V o;
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
                o.positionCS = cs; o.color = i.color; o.uv = i.uv;
                return o;
            }
            half4 frag(V i) : SV_Target
            {
                if (_Cutoff > 0.0) clip(propAlbedo(i.color, i.uv).a - _Cutoff);
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On ColorMask R
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            struct A { float4 positionOS : POSITION; float4 color : COLOR; float2 uv : TEXCOORD0; };
            struct V { float4 positionCS : SV_POSITION; float4 color : TEXCOORD0; float2 uv : TEXCOORD1; };
            V vert(A i) { V o; o.positionCS = TransformObjectToHClip(i.positionOS.xyz); o.color = i.color; o.uv = i.uv; return o; }
            half4 frag(V i) : SV_Target
            {
                if (_Cutoff > 0.0) clip(propAlbedo(i.color, i.uv).a - _Cutoff);
                return 0;
            }
            ENDHLSL
        }
    }
}
