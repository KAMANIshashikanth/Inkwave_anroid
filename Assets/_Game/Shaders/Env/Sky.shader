// Skybox for a baked stage (World/StageEnvironment.cs): the sky dome + cloud layer environment.js draws, baked in the
// browser as an HDR cube (Tools/bake/pages/stage.js) and rearranged into Unity's mirrored world on load. Linear HDR
// out; tonemapping happens in post like every other surface (three tonemaps its sky too).
Shader "Inkwave/Env/Sky"
{
    Properties { _Sky ("Sky cube", Cube) = "black" {} }
    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" "RenderPipeline" = "UniversalPipeline" }
        Cull Off ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURECUBE(_Sky); SAMPLER(sampler_Sky);
            struct A { float4 positionOS : POSITION; };
            struct V { float4 positionCS : SV_POSITION; float3 dir : TEXCOORD0; };
            V vert(A i) { V o; o.positionCS = TransformObjectToHClip(i.positionOS.xyz); o.dir = i.positionOS.xyz; return o; }
            half4 frag(V i) : SV_Target { return half4(SAMPLE_TEXTURECUBE_LOD(_Sky, sampler_Sky, normalize(i.dir), 0).rgb, 1); }
            ENDHLSL
        }
    }
}
