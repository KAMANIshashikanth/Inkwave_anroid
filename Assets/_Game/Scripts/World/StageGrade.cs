// renderer.js's post stack after bloom: the theme's colour grade (GradeShader) and three's OutputPass tone mapping
// (Khronos PBR Neutral × toneMappingExposure), as one full-screen pass (Shaders/Post/Grade.shader) enqueued on every
// game camera from RenderPipelineManager.beginCameraRendering — no renderer asset changes. It runs after URP's own post
// (which then only does bloom, on the tiers that have it, with tonemapping off and HDR grading so nothing clamps).
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace Inkwave.World
{
    public static class StageGrade
    {
        class Pass : ScriptableRenderPass
        {
            public Material mat;
            public Pass() { renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing; requiresIntermediateTexture = true; }

            public override void RecordRenderGraph(RenderGraph rg, ContextContainer frame)
            {
                var res = frame.Get<UniversalResourceData>();
                if (res.isActiveTargetBackBuffer || mat == null) return;
                var src = res.activeColorTexture;
                var desc = rg.GetTextureDesc(src);
                desc.name = "_InkwaveGrade"; desc.clearBuffer = false;
                var dst = rg.CreateTexture(desc);
                rg.AddBlitPass(new RenderGraphUtils.BlitMaterialParameters(src, dst, mat, 0), "Inkwave Grade");
                res.cameraColor = dst;
            }
        }

        static Pass pass;
        static Material mat;
        public static bool active => pass != null;

        /** grade + tone mapping for the theme (stage bake: grade, exposure); call again on a theme change */
        public static void apply(StageLoader.StageJ s)
        {
            if (mat == null)
            {
                var sh = Shader.Find("Hidden/Inkwave/Grade");
                if (sh == null) { Debug.LogWarning("[inkwave] grade shader missing"); return; }
                mat = new Material(sh) { name = "inkwave-grade" };
            }
            var g = s.grade ?? new StageLoader.GradeJ();
            mat.SetFloat("_Sat", g.uSat); mat.SetFloat("_Vib", g.uVib); mat.SetFloat("_Contrast", g.uContrast);
            mat.SetFloat("_Lift", g.uLift); mat.SetFloat("_Exposure", g.uExposure); mat.SetFloat("_Vignette", g.uVignette);
            mat.SetVector("_ShadowTint", v(g.uShadowTint, new Vector4(0.975f, 0.99f, 1.035f, 0)));
            mat.SetVector("_HighTint", v(g.uHighTint, new Vector4(1.025f, 1.0f, 0.972f, 0)));
            mat.SetFloat("_ToneExposure", s.exposure);
            mat.SetFloat("_Hurt", 0); mat.SetFloat("_Flash", 0);
            if (pass == null)
            {
                pass = new Pass();
                RenderPipelineManager.beginCameraRendering += enqueue;
            }
            pass.mat = mat;
        }

        static Vector4 v(float[] a, Vector4 d) => a != null && a.Length >= 3 ? new Vector4(a[0], a[1], a[2], 0) : d;

        static void enqueue(ScriptableRenderContext ctx, Camera cam)
        {
            if (pass == null || (cam.cameraType != CameraType.Game && cam.cameraType != CameraType.SceneView)) return;
            var cd = cam.GetUniversalAdditionalCameraData();
            if (cd == null || cd.scriptableRenderer == null) return;
            mat.SetFloat("_Aspect", cam.aspect);
            cd.scriptableRenderer.EnqueuePass(pass);
        }

        public static void dispose()
        {
            if (pass != null) RenderPipelineManager.beginCameraRendering -= enqueue;
            pass = null;
            if (mat != null) Object.Destroy(mat);
            mat = null;
        }
    }
}
