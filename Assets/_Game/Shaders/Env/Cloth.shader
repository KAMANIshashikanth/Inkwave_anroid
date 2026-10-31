// Cloth of a baked stage, drawn instanced (World/StageDecor.cs, DrawMeshInstanced; per-instance _IColor, linear):
//   default     props.js clothMaterial — bunting / banners: flex + wave attributes (TEXCOORD1.x, TEXCOORD2.xyz) ripple the
//               panel along z, the atlas art laid over the instance colour by its alpha
//   CLOTH_FLAG  decor.js flagMaterial — the big team flags: a travelling wave from the hoist, sagging fly end, white
//               emblem + hoist sleeve, darker fly-end hem
// Both phase off the instance position (three space) and _StageTime. Double-sided (the instance matrices carry the
// mirrored world root).
Shader "Inkwave/Env/Cloth"
{
    Properties
    {
        _BaseMap ("Map", 2D) = "white" {}
        _Roughness ("Roughness", Float) = 0.82
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #pragma multi_compile_local _ CLOTH_FLAG
        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            float _Roughness;
        CBUFFER_END
        UNITY_INSTANCING_BUFFER_START(Cloth)
            UNITY_DEFINE_INSTANCED_PROP(float4, _IColor)
        UNITY_INSTANCING_BUFFER_END(Cloth)
        float _StageTime;
        #define FLAG_W 1.70

        struct Attributes
        {
            float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0;
            float4 a1 : TEXCOORD1; float4 a2 : TEXCOORD2;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };
        // object-space displacement + normal; ipos = the instance's position in three space
        void clothDeform(float3 p, float3 n, float4 a1, float4 a2, out float3 P, out float3 N)
        {
            float uTime = _StageTime;
            float3 ipos = float3(UNITY_MATRIX_M._m03, UNITY_MATRIX_M._m13, -UNITY_MATRIX_M._m23);
            P = p; N = n;
        #if CLOTH_FLAG
            float fph = ipos.x * 0.71 + ipos.z * 0.43;
            float fk = saturate(p.x / FLAG_W);
            float fa = p.x * 3.1 - uTime * 5.2 + fph;
            float fb = p.x * 5.7 + p.y * 3.9 - uTime * 7.9 + fph * 1.7;
            float fwav = (sin(fa) * 0.16 + sin(fb) * 0.045) * fk;
            float dwdx = (cos(fa) * 3.1 * 0.16 + cos(fb) * 5.7 * 0.045) * fk + (sin(fa) * 0.16 + sin(fb) * 0.045) / FLAG_W;
            float dwdy = cos(fb) * 3.9 * 0.045 * fk;
            N = normalize(float3(-dwdx, -dwdy, 1.0));
            P.z += fwav;
            P.y -= fk * fk * 0.06 + fk * 0.03 * (0.5 + 0.5 * sin(uTime * 2.3 + fph));
            P.x -= fk * abs(fwav) * 0.12;
        #else
            float flex = a1.x; float3 wave = a2.xyz;
            float cph = ipos.x * 0.83 + ipos.z * 0.61 + ipos.y * 0.37;
            float ca = uTime * wave.x + cph + p.x * wave.y;
            float cb = uTime * wave.x * 2.3 + cph * 1.7 + p.y * 3.0;
            float cw = sin(ca) + 0.35 * sin(cb);
            N = normalize(n + float3(-flex * wave.z * wave.y * cos(ca), -flex * wave.z * 1.05 * cos(cb), 0.0));
            P.z += flex * wave.z * cw;
        #endif
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0; float3 normalWS : TEXCOORD1;
                float2 uv : TEXCOORD2; float4 color : TEXCOORD3;   // a = fog
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes i)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_TRANSFER_INSTANCE_ID(i, o);
                float3 P, N;
                clothDeform(i.positionOS.xyz, i.normalOS, i.a1, i.a2, P, N);
                VertexPositionInputs p = GetVertexPositionInputs(P);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(N);
                o.uv = i.uv;
                o.color = float4(UNITY_ACCESS_INSTANCED_PROP(Cloth, _IColor).rgb, ComputeFogFactor(p.positionCS.z));
                return o;
            }

            half4 frag(Varyings i, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float4 tx = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv);
                float3 diffuse = i.color.rgb;
            #if CLOTH_FLAG
                // hoist sleeve + fly-end hem stripes and the white emblem
                float sleeve = smoothstep(0.075, 0.065, i.uv.x);
                float hem = smoothstep(0.955, 0.965, i.uv.x);
                diffuse = lerp(diffuse, float3(0.93, 0.93, 0.93), max(tx.a, sleeve));
                diffuse *= 1.0 - hem * 0.35;
            #else
                diffuse = lerp(diffuse, tx.rgb, tx.a);
            #endif
                float3 n = normalize(i.normalWS) * IS_FRONT_VFACE(face, 1.0, -1.0);
                InputData inputData = (InputData)0;
                inputData.positionWS = i.positionWS;
                inputData.positionCS = i.positionCS;
                inputData.normalWS = n;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(i.positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                inputData.fogCoord = i.color.a;
                inputData.bakedGI = SampleSH(n);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);
                SurfaceData surf = (SurfaceData)0;
                surf.albedo = diffuse;
                surf.smoothness = 1.0 - _Roughness;
                surf.normalTS = float3(0, 0, 1);
                surf.occlusion = 1;
                surf.alpha = 1;
                float3 col = UniversalFragmentPBR(inputData, surf).rgb;
                return half4(MixFog(col, i.color.a), 1);
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
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection;
            float3 _LightPosition;
            float4 vert(Attributes i) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float3 P, N;
                clothDeform(i.positionOS.xyz, i.normalOS, i.a1, i.a2, P, N);
                float3 ws = TransformObjectToWorld(P);
                float3 nws = TransformObjectToWorldNormal(N);
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
                return cs;
            }
            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
