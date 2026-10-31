// Runtime side of src/game/kits/brolly-model.js — the Canopy Brolly's part animation (animateBrolly) and the launched
// canopy's mesh. The geometry is baked by Tools/bake/weapons.mjs (brolly-model.js run under Node).
// Weapon space: grip centre at the origin, +Z = forward, +Y = up, character's right = -X.
//
// The canopy is eight gores (alternating team ink / cream), each a part hinged at the crown. upstream authors them
// FURLED and re-poses each with one affine matrix per frame: T(crown)·R·Rx(fold)·diag(sx, s, s)·goreLocal. That is a
// rotation × scale, so the bake hands each gore part its gore-local geometry and a plain Transform poses it — open
// (dome, ~1.4 m across), furled, regrowing from the crown, or gone (broken / launched). Damage tears ride their gore
// (children, gore-local too); the stretcher struts are re-aimed from the runner collar to each rib every frame.
// Canopy space (the launched canopy): crown at the origin, axis +Z, dome curving back toward -Z.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using static Inkwave.Ctx;

namespace Inkwave
{
    public static class BrollyModel
    {
        // ---- dimensions (brolly-model.js)
        public const float AY = 0.066f;                 // shaft axis height
        public const float CROWN_Z = 0.56f;             // canopy hinge (crown) on the shaft
        public const float RIM_R = 0.7f;                // open canopy rim radius
        static readonly float TH = 52 * Mathf.PI / 180; // dome half-angle
        static readonly float RS = RIM_R / Mathf.Sin(TH);
        public static readonly float DEPTH = RS * (1 - Mathf.Cos(TH));   // crown → rim plane (~0.34)
        const int NG = 8;                               // gores
        static readonly float GW = Mathf.PI / NG;       // gore half-width (azimuth)
        const float THICK = 0.006f;
        // furled pose, in the gore's own frame: fold back about the hinge (+X), squash across (sx) and along (s)
        const float FOLD_A = -1.05f, FOLD_SX = 0.19f, FOLD_S = 0.37f;
        const float RUNNER_Z0 = 0.305f, RUNNER_Z1 = 0.47f;   // runner collar: furled → open
        static bool TEAM_GORE(int k) => (k & 1) == 1;        // odd gores wear the team ink
        static float foldA(int k) => FOLD_A * ((k & 1) != 0 ? 1 : 0.965f);   // alternate gores fold a hair apart (they overlap, furled)
        static float foldSX(int k) => FOLD_SX * ((k & 1) != 0 ? 1 : 1.08f);
        static Quaternion goreRot(int k) => Quaternion.AngleAxis(-k * 360f / NG, Vector3.forward);
        // gore-local point on the dome: crown at the origin, axis +Z, gore centred on +Y, azimuth ps toward +X
        static Vector3 gp(float th, float ps, float off)
        {
            float r = RS + off;
            return new Vector3(r * Mathf.Sin(th) * Mathf.Sin(ps), r * Mathf.Sin(th) * Mathf.Cos(ps), -RS + r * Mathf.Cos(th));
        }
        static float easeOutBack(float t) { const float c1 = 1.9f, c3 = c1 + 1; return 1 + c3 * Mathf.Pow(t - 1, 3) + c1 * Mathf.Pow(t - 1, 2); }
        static readonly Color LAMP_OK = hex("#3dff7a").linear, LAMP_MID = hex("#ffc21a").linear, LAMP_LOW = hex("#ff3b30").linear, LAMP_RE = hex("#9fd8ff").linear;

        // ---- baked extras (weapons.json "brolly": the tears' stage + gore; strut + open canopy meshes)
        [Serializable] class TearRec { public int stage, gore; public string name; }
        [Serializable] class BRec { public TearRec[] tears; }
        [Serializable] class Man { public BRec brolly; }
        static TearRec[] _tears;
        static Mesh _strut, _cInk, _cCream, _cHub;
        static readonly Dictionary<string, Mesh> _tearMesh = new Dictionary<string, Mesh>();
        static void load()
        {
            if (_tears != null) return;
            var ta = Resources.Load<TextAsset>("KidWeapons/weapons");
            var b = JsonUtility.FromJson<Man>(ta.text).brolly;
            _tears = b != null && b.tears != null ? b.tears : new TearRec[0];
            foreach (var t in _tears) _tearMesh[t.name] = WeaponModel.loadMesh("brolly/" + t.name);
            _strut = WeaponModel.loadMesh("brolly/strut");
            _cInk = WeaponModel.loadMesh("brolly/canopyInk"); _cCream = WeaponModel.loadMesh("brolly/canopyCream"); _cHub = WeaponModel.loadMesh("brolly/canopyHub");
        }

        static MeshRenderer meshObj(string name, Transform parent, Mesh m, Material mat, bool shadow)
        {
            var g = ThreeSpace.create(name, parent);
            g.AddComponent<MeshFilter>().sharedMesh = m;
            var r = g.AddComponent<MeshRenderer>(); r.sharedMaterial = mat;
            r.shadowCastingMode = shadow ? ShadowCastingMode.On : ShadowCastingMode.Off;
            return r;
        }
        static void show(GameObject o, bool on) { if (o.activeSelf != on) o.SetActive(on); }

        // ------------------------------------------------------------------------------------------ held instance
        class Gore { public int k; public WeaponModel.Part g; public Material mat; public Quaternion R; public float a0, sx0; }
        class Strut { public GameObject m; public Gore G; public Vector3 ribA, dir; }
        class S
        {
            public readonly List<Gore> gores = new List<Gore>();
            public readonly List<GameObject>[] tears = { new List<GameObject>(), new List<GameObject>() };
            public readonly List<Strut> struts = new List<Strut>();
            public Color col; public Material inkMat, creamMat; public bool local;
        }

        static S init(WeaponModel.Instance w, WeaponModel.State st)
        {
            load();
            var s = new S { col = st.color, inkMat = CharacterMats.fabric(st.color, true), creamMat = CharacterMats.fabric(Color.white, false) };
            // your own canopy turns see-through as it opens (it would otherwise fill the over-the-shoulder view)
            s.local = st.runner != null && st.runner.a != null && st.runner.a.isLocal;
            for (int k = 0; k < NG; k++)
            {
                if (!w.parts.TryGetValue("g" + k, out var g)) continue;
                g.mesh.shadowCastingMode = ShadowCastingMode.On;
                var mat = TEAM_GORE(k) ? s.inkMat : s.creamMat;
                g.mesh.sharedMaterial = mat;
                s.gores.Add(new Gore { k = k, g = g, mat = mat, R = goreRot(k), a0 = foldA(k), sx0 = foldSX(k) });
            }
            // damage tears: children of their gore (same gore-local frame), shown by hp stage
            foreach (var t in _tears)
            {
                if (!w.parts.TryGetValue("g" + t.gore, out var gg) || t.stage < 0 || t.stage > 1) continue;
                var r = meshObj(t.name, gg.g, _tearMesh[t.name], CharacterMats.plastic, false);
                r.gameObject.SetActive(false); s.tears[t.stage].Add(r.gameObject);
            }
            if (w.parts.TryGetValue("runner", out var run)) run.mesh.shadowCastingMode = ShadowCastingMode.On;
            // stretchers: a strut from the runner collar to each rib (re-aimed every frame; they fold flat along the shaft)
            foreach (var G in s.gores)
            {
                var r = meshObj("strut", w.off, _strut, CharacterMats.plastic, false);
                Vector3 ribA = gp(TH * 0.42f, GW, -THICK - 0.004f);                                    // gore-local
                Vector3 dir = (G.R * new Vector3(Mathf.Sin(GW), Mathf.Cos(GW), 0)).normalized;
                s.struts.Add(new Strut { m = r.gameObject, G = G, ribA = ribA, dir = dir });
            }
            return s;
        }

        /** the canopy state shown on a kid with no runner (editor captures, menus); null = furled */
        public static BrollyState previewKit;

        /** WeaponModel.KIT_ANIM["brolly"]: drives the canopy from the runner's kit (BrollyState, see Brolly.cs). */
        public static void animate(WeaponModel.Instance w, WeaponModel.State st)
        {
            var P = w.parts; var k = st.runner != null ? st.runner.kit as BrollyState : previewKit;
            float dt = Mathf.Min(0.1f, Mathf.Max(0, st.dt)), t = st.t;
            var S = w.ks as S;
            if (S == null) w.ks = S = init(w, st);
            float open = k != null ? k.open : 0, grow = k != null ? k.grow : 1;
            // LOD: the merged far mesh is the furled brolly — anything else (open, regrowing, gone) keeps the live parts
            bool near = st.near || open > 0.004f || grow < 0.999f;
            if (near != w.near)
            {
                w.near = near;
                w.body.enabled = near; w.ink.enabled = near;
                w.bodyFar.enabled = !near; w.inkFar.enabled = !near;
                foreach (var g in w.partList) g.g.gameObject.SetActive(near);
                foreach (var u in S.struts) u.m.SetActive(near);
            }
            // team colour (setColor re-points 'ink' parts at the shared ink material: take them back)
            if (S.col != st.color) { S.col = st.color; CharacterMats.setBase(S.inkMat, st.color); }
            foreach (var G in S.gores) if (G.g.mesh.sharedMaterial != G.mat) G.g.mesh.sharedMaterial = G.mat;
            if (!near) return;
            // ---- gores: fold angle / squash from the (eased, overshooting) openness; hit recoil; regrowth scale from the crown
            float eo = open <= 0 ? 0 : open >= 1 ? 1 : (k != null && k.opening ? easeOutBack(open) : open * open * (3 - 2 * open));
            float flutter = open > 0.9f ? 0.012f * Mathf.Sin(t * 13.7f) : 0, kick = k != null ? k.shake : 0;
            float gs = Mathf.Max(0.001f, grow < 1 ? easeOutBack(Mathf.Max(0, grow)) : 1);
            var crown = new Vector3(0, AY, CROWN_Z);
            foreach (var G in S.gores)
            {
                float e = Mathf.Max(0, eo), ec = Mathf.Min(1, e);
                float a = G.a0 * (1 - e) - 0.16f * kick * (open > 0.5f ? 1 : 0) + flutter * ((G.k & 1) != 0 ? 1 : -1);
                float sx = (G.sx0 + (1 - G.sx0) * ec) * gs, sl = (FOLD_S + (1 - FOLD_S) * ec) * gs;
                var tr = G.g.g;
                tr.localPosition = crown;
                tr.localRotation = G.R * Quaternion.AngleAxis(a * Mathf.Rad2Deg, Vector3.right);
                tr.localScale = new Vector3(sx, sl, sl);
                show(tr.gameObject, grow > 0.002f);
            }
            // struts: runner collar → rib (both where they are this frame)
            {
                float rz = RUNNER_Z0 + (RUNNER_Z1 - RUNNER_Z0) * Mathf.Min(1, Mathf.Max(0, eo));
                bool on = grow > 0.05f;
                foreach (var u in S.struts)
                {
                    show(u.m, on);
                    if (!on) continue;
                    Vector3 sa = new Vector3(0, AY, rz) + u.dir * 0.019f;
                    var gt = u.G.g.g;
                    Vector3 sb = gt.localPosition + gt.localRotation * Vector3.Scale(gt.localScale, u.ribA);
                    Vector3 sy = sb - sa;
                    Vector3 sx = Vector3.Cross(sy, Vector3.forward); if (sx.sqrMagnitude < 1e-10f) sx = Vector3.right; sx.Normalize();
                    Vector3 sz = Vector3.Cross(sx, sy).normalized;
                    var mt = u.m.transform;
                    mt.localPosition = sa;
                    mt.localRotation = Quaternion.LookRotation(sz, sy);
                    mt.localScale = new Vector3(0.0026f, sy.magnitude, 0.0026f);
                }
            }
            // tears by hp stage (only while the canopy is there)
            float hpf = k != null ? k.hp / k.hpMax : 1; bool have = grow > 0.5f;
            foreach (var m in S.tears[0]) show(m, have && hpf < 0.66f);
            foreach (var m in S.tears[1]) show(m, have && hpf < 0.33f);
            // runner collar rides the opening; trigger follows shots
            if (P.TryGetValue("runner", out var run)) { var p = run.g.localPosition; p.z = run.rest.z + (RUNNER_Z1 - RUNNER_Z0) * Mathf.Min(1, Mathf.Max(0, eo)); run.g.localPosition = p; }
            if (P.TryGetValue("trigger", out var trig))
            {
                float want = st.sinceShoot < 0.12f || open > 0.1f ? 1 : 0;
                w.trig += (want - w.trig) * (1 - Mathf.Exp(-(want > 0 ? 40 : 20) * dt));
                trig.g.localRotation = Quaternion.AngleAxis(0.42f * w.trig * Mathf.Rad2Deg, Vector3.right);
            }
            // status lamp: hp green → amber → red; off (broken / launched) with a blue blink while it regrows
            if (P.TryGetValue("lamp", out var lamp))
            {
                var m = lamp.mesh.sharedMaterial;
                if (k == null || k.state == "ready")
                {
                    Color e = hpf > 0.5f ? Color.Lerp(LAMP_MID, LAMP_OK, (hpf - 0.5f) * 2) : Color.Lerp(LAMP_LOW, LAMP_MID, hpf * 2);
                    CharacterMats.setLamp(m, e, (hpf < 0.25f ? 0.6f + 2.2f * (Mathf.Sin(t * 22) > 0 ? 1 : 0.1f) : 1.4f) + 2.5f * (k != null ? k.flash : 0) + (grow < 1 ? 3 * (1 - grow) : 0));
                }
                else
                {
                    float reK = k.regrowK;
                    CharacterMats.setLamp(m, LAMP_RE, 0.15f + (Mathf.Sin(t * (6 + 14 * reK)) > 0.3f ? 1.6f * reK : 0));
                }
            }
            // fabric: white flash on hits, sputtering flicker when nearly broken
            float fl = k != null ? k.flash : 0;
            bool low = k != null && k.state == "ready" && hpf < 0.25f;
            float flick = low ? (Mathf.Sin(t * 37) * Mathf.Sin(t * 23.3f) > 0.2f ? 0.35f : 0) : 0;
            CharacterMats.setLamp(S.inkMat, Color.Lerp(S.col.linear * (0.5f * fl + flick), Color.white, fl * 0.5f), 1);
            CharacterMats.setLamp(S.creamMat, Color.white * (0.45f * fl + flick * 0.6f), 1);
            if (S.local)
            {
                float f = 0.56f * Mathf.Min(1, Mathf.Max(0, open));
                CharacterMats.setFade(S.inkMat, f); CharacterMats.setFade(S.creamMat, f);
            }
        }

        // ------------------------------------------------------------------------------------------ launched canopy
        /** the open canopy sliding along the ground as a wall (brolly.js buildLaunchedMesh / the mesh half of updateLaunched) */
        public class Wall
        {
            public Transform root, inner; public Material ink, cream;
            public void update(Brolly.Launched c, float hNow)
            {
                float fade = c.alive ? 0 : c.fade;
                root.localPosition = c.pos;
                inner.localPosition = new Vector3(0, hNow + 0.012f * Mathf.Sin(c.t * 11), 0);
                inner.localRotation = ThreeSpace.eulerXYZ(-0.08f * c.shake, 0, 0.035f * Mathf.Sin(c.t * 7.3f));
                float s = c.hp <= 0 ? 1 + 0.4f * fade : 1 - 0.25f * fade;
                inner.localScale = new Vector3(s, s, c.hp <= 0 ? Mathf.Max(0.05f, 1 - fade) : s);
                Color col = G.teamColors[c.team].linear;
                float low = c.hp / c.hpMax < 0.25f && Mathf.Sin(c.t * 37) * Mathf.Sin(c.t * 23.3f) > 0.2f ? 0.35f : 0;
                CharacterMats.setLamp(ink, col * (0.5f * c.flash + low), 1);
                CharacterMats.setLamp(cream, Color.white * (0.45f * c.flash + low * 0.6f), 1);
                CharacterMats.setFade(ink, fade); CharacterMats.setFade(cream, fade);
            }
            public void dispose() { destroy(root.gameObject); destroy(ink); destroy(cream); }
        }
        public static Wall buildWall(Brolly.Launched c)
        {
            load();
            Color col = G.teamColors[c.team];
            var wl = new Wall { ink = CharacterMats.fabric(col, true), cream = CharacterMats.fabric(Color.white, false) };
            wl.root = ThreeSpace.create("brollyWall").transform;
            wl.inner = ThreeSpace.create("inner", wl.root).transform;
            meshObj("ink", wl.inner, _cInk, wl.ink, true);
            meshObj("cream", wl.inner, _cCream, wl.cream, true);
            meshObj("hub", wl.inner, _cHub, CharacterMats.plastic, true);
            wl.root.localRotation = ThreeSpace.eulerXYZ(0, Mathf.Atan2(c.dir.x, c.dir.z), 0);
            wl.root.localPosition = c.pos;
            return wl;
        }
    }
}
