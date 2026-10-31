// Glossy ink teardrops (port of weapons.js makeBlobMaterial): a unit sphere (poles on ±Z) deformed per instance in the
// vertex shader — round, pressure-flattened nose; the back half stretched into a tapering tail (length from speed);
// low-order liquid wobble — then oriented along the velocity by the instance matrix (uniform scale = head radius).
// _IShape = (tail length in radii, wobble amplitude, wobble phase, nose flatten); _IColor = linear team colour. Fresnel rim
// glow keeps them readable against pale floors; anything within ~1.6 m of the lens dithers out (allies' shots flying
// past your camera never blot the view). Drawn with Graphics.DrawMeshInstanced (per-instance property-block arrays).
Shader "Inkwave/Fx/InkBlob"
{
    Properties { }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        // instance matrices carry the mirrored world root: draw both faces rather than trust the winding
        Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        UNITY_INSTANCING_BUFFER_START(Blob)
            UNITY_DEFINE_INSTANCED_PROP(float4, _IColor)
            UNITY_DEFINE_INSTANCED_PROP(float4, _IShape)
        UNITY_INSTANCING_BUFFER_END(Blob)

        void iwBlobDeform(float3 p, float3 n, float4 s, out float3 P, out float3 N)
        {
            float back = step(p.z, 0.0);
            float u = saturate(-p.z);
            float tau = lerp(1.0, 1.0 - 0.42 * pow(u, 1.3), back);       // tail taper (soft, rounded tip)
            float fz = lerp(1.0 - 0.22 * s.w, max(s.x, 1.0), back);
            P = float3(p.xy * tau, p.z * fz);
            N = normalize(float3(n.xy / max(tau, 0.15), n.z / fz));
            float wob = s.y * (0.6 * sin(s.z + 2.3 * p.x + 1.7 * p.y + 0.9 * p.z) + 0.4 * sin(1.63 * s.z - 2.9 * p.y + 2.1 * p.z));
            P += n * wob * (1.0 - 0.5 * back * u);
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog
            #define _CLEARCOAT 1
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0; float3 normalWS : TEXCOORD1;
                float4 color : TEXCOORD2;   // rgb linear colour, a = fog
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes i)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_TRANSFER_INSTANCE_ID(i, o);
                float3 P, N;
                iwBlobDeform(i.positionOS.xyz, i.normalOS, UNITY_ACCESS_INSTANCED_PROP(Blob, _IShape), P, N);
                VertexPositionInputs p = GetVertexPositionInputs(P);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(N);
                o.color = float4(UNITY_ACCESS_INSTANCED_PROP(Blob, _IColor).rgb, ComputeFogFactor(p.positionCS.z));
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float3 V = GetWorldSpaceNormalizeViewDir(i.positionWS);
                // dither out right at the lens
                float iwNear = smoothstep(0.55, 1.6, distance(_WorldSpaceCameraPos, i.positionWS));
                float iwIgn = frac(52.9829189 * frac(dot(floor(i.positionCS.xy), float2(0.06711056, 0.00583715))));
                if (iwIgn > iwNear) discard;
                float3 n = normalize(i.normalWS);
                float3 col = i.color.rgb;
                float rim = pow(1.0 - saturate(dot(n, V)), 3.0);

                InputData inputData = (InputData)0;
                inputData.positionWS = i.positionWS;
                inputData.positionCS = i.positionCS;
                inputData.normalWS = n;
                inputData.viewDirectionWS = V;
                inputData.shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                inputData.fogCoord = i.color.a;
                inputData.bakedGI = SampleSH(n);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);
                SurfaceData surf = (SurfaceData)0;
                surf.albedo = col;
                surf.smoothness = 1.0 - 0.12;
                surf.normalTS = float3(0, 0, 1);
                surf.occlusion = 1;
                surf.alpha = 1;
                surf.emission = col * (0.16 + 0.55 * rim);
                surf.clearCoatMask = 1;
                surf.clearCoatSmoothness = 1.0 - 0.04;
                half4 c = UniversalFragmentPBR(inputData, surf);
                c.rgb = MixFog(c.rgb, i.color.a);
                return half4(c.rgb, 1);
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
            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Vy { float4 positionCS : SV_POSITION; };
            Vy vert(A i)
            {
                Vy o;
                UNITY_SETUP_INSTANCE_ID(i);
                float3 P, N;
                iwBlobDeform(i.positionOS.xyz, i.normalOS, UNITY_ACCESS_INSTANCED_PROP(Blob, _IShape), P, N);
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
                o.positionCS = cs;
                return o;
            }
            half4 frag(Vy i) : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On ColorMask R

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Vy { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };
            Vy vert(A i)
            {
                Vy o;
                UNITY_SETUP_INSTANCE_ID(i);
                float3 P, N;
                iwBlobDeform(i.positionOS.xyz, i.normalOS, UNITY_ACCESS_INSTANCED_PROP(Blob, _IShape), P, N);
                o.positionWS = TransformObjectToWorld(P);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                return o;
            }
            half4 frag(Vy i) : SV_Target
            {
                float iwNear = smoothstep(0.55, 1.6, distance(_WorldSpaceCameraPos, i.positionWS));
                float iwIgn = frac(52.9829189 * frac(dot(floor(i.positionCS.xy), float2(0.06711056, 0.00583715))));
                if (iwIgn > iwNear) discard;
                return 0;
            }
            ENDHLSL
        }
    }
}
