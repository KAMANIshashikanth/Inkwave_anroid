// The live, team-coloured part of a baked stage's dressing — decor.js (spawn pads, their barriers, team flags) and
// props.js's cloth (bunting / banners):
//   * cloth is drawn instanced every frame (Inkwave/Env/Cloth; DrawMeshInstanced, per-instance colour) so it keeps its
//     wave, which phases off each instance's position;
//   * setTeamColors = decor.js setTeamColors + props.js _applyColors (a cloth piece with a team takes the team colour,
//     lightened toward white by its tint; the rest keep their own colour);
//   * update = decor.js update: the pads' time / pulse decay, the barrier brightening as an enemy nears it;
//   * pulse(team) = decor.js pulse (the spawn-pad flare; the JS has no caller yet).
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using static Inkwave.Ctx;

namespace Inkwave.World
{
    [ExecuteAlways]   // (batch captures render in edit mode: the cloth draws are issued per camera)
    public class StageDecor : MonoBehaviour
    {
        public static StageDecor instance;

        class ClothSet
        {
            public Mesh mesh; public Material mat; public bool flag;
            public Matrix4x4[] m; public Vector4[] col;
            public StageLoader.ClothRecJ[] recs;   // bunting / banners: team + tint per instance (null for the decor flags)
            public int[] teams;                    // decor flags: team per instance
            public bool castShadow;
        }
        class Pad { public Material face, barrier; public int team; public Vector3 pos; public float pulse, pulseT = 1; }

        readonly List<ClothSet> cloth = new List<ClothSet>();
        readonly List<Pad> pads = new List<Pad>();
        StageLoader.TeamsJ T;
        const int BATCH = 500;
        Matrix4x4[] mb = new Matrix4x4[BATCH];
        Vector4[] cb = new Vector4[BATCH];
        MaterialPropertyBlock mpb;
        static readonly int IColor = Shader.PropertyToID("_IColor"), Pulse = Shader.PropertyToID("_Pulse"), PulseT = Shader.PropertyToID("_PulseT"),
            Alpha = Shader.PropertyToID("_Alpha"), ColorId = Shader.PropertyToID("_Color");

        void Awake() { instance = this; mpb = new MaterialPropertyBlock(); }
        void OnEnable() => RenderPipelineManager.beginCameraRendering += drawFor;
        void OnDisable() => RenderPipelineManager.beginCameraRendering -= drawFor;
        void OnDestroy() { if (instance == this) instance = null; foreach (var c in cloth) if (c.mat != null) Destroy(c.mat); }

        public void init(StageLoader.TeamsJ teams) { T = teams; }

        /** a cloth instanced mesh (props:flags / props:banners / flags:cloth): instance matrices in three space */
        public void addCloth(StageLoader.ObjJ o, Mesh mesh, Material mat)
        {
            var root = ThreeSpace.root.localToWorldMatrix;
            var c = new ClothSet { mesh = mesh, mat = mat, flag = o.name == "flags:cloth", m = new Matrix4x4[o.instN], col = new Vector4[o.instN], castShadow = o.castShadow };
            for (int i = 0; i < o.instN; i++)
            {
                int k = i * 16;
                var a = o.inst;
                var M = new Matrix4x4(new Vector4(a[k], a[k + 1], a[k + 2], a[k + 3]), new Vector4(a[k + 4], a[k + 5], a[k + 6], a[k + 7]),
                                      new Vector4(a[k + 8], a[k + 9], a[k + 10], a[k + 11]), new Vector4(a[k + 12], a[k + 13], a[k + 14], a[k + 15]));
                c.m[i] = root * M;
                c.col[i] = o.instanceColors != null && o.instanceColors.Length >= (i + 1) * 3 ? new Vector4(o.instanceColors[i * 3], o.instanceColors[i * 3 + 1], o.instanceColors[i * 3 + 2], 1) : Vector4.one;
            }
            if (T != null)
            {
                if (c.flag) c.teams = T.flags;
                else c.recs = o.name == "props:banners" ? T.banners : T.bunting;
            }
            cloth.Add(c);
        }

        /** a spawn pad's face / barrier material, in the bake's (team) order */
        public void addPadMaterial(Material m, bool barrier)
        {
            int n = barrier ? pads.FindIndex(p => p.barrier == null) : pads.FindIndex(p => p.face == null);
            if (n < 0) { pads.Add(new Pad()); n = pads.Count - 1; }
            var pad = pads[n];
            if (barrier) pad.barrier = m; else pad.face = m;
            if (T?.pads != null && n < T.pads.Length) { pad.team = T.pads[n].team; var p = T.pads[n].pos; if (p != null && p.Length >= 3) pad.pos = new Vector3(p[0], p[1], p[2]); }
            else pad.team = n;
        }

        /** team colours (sRGB, as G.teamColors) */
        public void setTeamColors(Color[] colors)
        {
            if (colors == null || colors.Length < 2) return;
            Vector4 lin(Color c) { var l = c.linear; return new Vector4(l.r, l.g, l.b, 1); }
            foreach (var p in pads)
            {
                var c = lin(colors[Mathf.Clamp(p.team, 0, 1)]);
                if (p.face != null) p.face.SetVector(ColorId, c);
                if (p.barrier != null) p.barrier.SetVector(ColorId, c);
            }
            foreach (var c in cloth)
            {
                for (int i = 0; i < c.col.Length; i++)
                {
                    if (c.teams != null && i < c.teams.Length) c.col[i] = lin(colors[Mathf.Clamp(c.teams[i], 0, 1)]);
                    else if (c.recs != null && i < c.recs.Length && c.recs[i].team >= 0)
                        c.col[i] = Vector4.Lerp(lin(colors[Mathf.Clamp(c.recs[i].team, 0, 1)]), Vector4.one, c.recs[i].tint);
                }
            }
        }

        public void pulse(int team, float strength = 1)
        {
            foreach (var p in pads)
                if (p.team == team) { p.pulse = Mathf.Min(1.5f, Mathf.Max(p.pulse, strength)); p.pulseT = 0; }
        }

        void Update()
        {
            float dt = Time.deltaTime;
            float barrierR = T != null ? T.spawnBarrier : 4.2f;
            foreach (var p in pads)
            {
                p.pulseT = Mathf.Min(3, p.pulseT + dt);
                p.pulse = Mathf.Max(0, p.pulse - dt * 0.9f);
                // barrier brightens when an enemy approaches it
                float near = 0;
                if (G.actors != null)
                    foreach (var a in G.actors)
                    {
                        if (a == null || a.team == p.team || !a.alive) continue;
                        float d = Mathf.Sqrt((a.pos.x - p.pos.x) * (a.pos.x - p.pos.x) + (a.pos.z - p.pos.z) * (a.pos.z - p.pos.z));
                        near = Mathf.Max(near, 1 - Mathf.Min(1, Mathf.Max(0, (d - barrierR) / 4)));
                    }
                if (p.face != null) { p.face.SetFloat(Pulse, p.pulse); p.face.SetFloat(PulseT, p.pulseT); }
                if (p.barrier != null) { p.barrier.SetFloat(Pulse, p.pulse); p.barrier.SetFloat(Alpha, 0.12f + near * 0.55f); }
            }
        }

        // the cloth, instanced, for each camera about to render
        void drawFor(ScriptableRenderContext ctx, Camera cam)
        {
            if (cam.cameraType != CameraType.Game && cam.cameraType != CameraType.SceneView) return;
            if (mpb == null) mpb = new MaterialPropertyBlock();
            foreach (var c in cloth)
            {
                for (int s = 0; s < c.m.Length; s += BATCH)
                {
                    int n = Mathf.Min(BATCH, c.m.Length - s);
                    System.Array.Copy(c.m, s, mb, 0, n);
                    System.Array.Copy(c.col, s, cb, 0, n);
                    var p = new MaterialPropertyBlock();   // (one block per draw: the arrays are captured when the draw is queued)
                    p.SetVectorArray(IColor, cb);
                    Graphics.DrawMeshInstanced(c.mesh, 0, c.mat, mb, n, p, c.castShadow ? ShadowCastingMode.On : ShadowCastingMode.Off, true, gameObject.layer, cam);
                }
            }
        }
    }
}
