// Port of decor.js BARRIER_FRAG: the spawn barrier cylinder — additive hex lattice in the team colour, a scan line
// climbing it, fading upward, brighter at grazing angles; a bright foot ring. _Alpha rises as enemies approach
// (World/StageDecor.cs); time = _StageTime.
Shader "Inkwave/Env/SpawnBarrier"
{
    Properties
    {
        _Color ("Team colour (linear)", Vector) = (1, 1, 1, 1)
        _Alpha ("Alpha", Float) = 0.12
        _Pulse ("Pulse", Float) = 0
    }
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
                float4 _Color;
                float _Alpha, _Pulse;
            CBUFFER_END
            float _StageTime;
            float glmod(float x, float y) { return x - y * floor(x / y); }
            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; };
            struct V { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float3 w : TEXCOORD1; float3 n : TEXCOORD2; };
            V vert(A i)
            {
                V o; o.w = TransformObjectToWorld(i.positionOS.xyz); o.n = TransformObjectToWorldNormal(i.normalOS);
                o.positionCS = TransformWorldToHClip(o.w); o.uv = i.uv; return o;
            }
            half4 frag(V i) : SV_Target
            {
                float h = i.uv.y;
                // hex lattice on the cylinder
                float2 p = float2(i.uv.x * 64.0, h * 9.0);
                p.x += step(1.0, glmod(floor(p.y), 2.0)) * 0.5;
                float2 f = frac(p) - 0.5;
                float hexEdge = smoothstep(0.43, 0.49, max(abs(f.x) * 1.15 + abs(f.y) * 0.6, abs(f.y) * 1.2));
                float scan = smoothstep(0.08, 0.0, abs(frac(h * 1.4 - _StageTime * 0.45) - 0.5) - 0.44);
                float fade = pow(max(1.0 - h, 0.0), 1.6);
                float base = smoothstep(0.0, 0.06, h);
                float3 Vv = normalize(_WorldSpaceCameraPos - i.w);
                float rim = pow(max(1.0 - abs(dot(normalize(i.n), Vv)), 0.0), 2.0);
                float a = (_Alpha + _Pulse * 0.5) * fade * base * (0.25 + 0.75 * hexEdge + scan * 0.6) * (0.55 + 0.45 * rim);
                a += smoothstep(0.035, 0.0, h) * (0.5 + _Alpha);
                return half4(_Color.rgb * (1.5 + _Pulse), a);
            }
            ENDHLSL
        }
    }
}
