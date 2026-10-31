// Runtime side of src/game/kits/blade-model.js — the Brine Cutlass's part animation (animateBlade). The geometry is baked
// by Tools/bake/weapons.mjs (blade-model.js run under Node): the weapon itself, plus the ink edge (blade/edge, aSeg 0 at the
// guard → 1 at the tip) and the aura shell (blade/aura, the blade body).
// Weapon space: grip centre at the origin, +Z = along the blade (hilt → tip), +Y = the spine side, −Y = the cutting edge.
// The edge: team ink at rest, filling with light from the hilt to the tip as a swing charges, shimmering at full charge and
// flashing white on the release (Kid shader kind 14). A thin additive aura shimmers over the steel while charging
// (Shaders/Fx/BladeAura). Charge comes from the runner's kit state (Blade.cs) when there is a runner, else st.charge.
using UnityEngine;
using UnityEngine.Rendering;

namespace Inkwave
{
    public static class BladeModel
    {
        class S { public MeshRenderer edge, aura; public Material em, am; public float charge, flash, full; public bool near = true; }
        static Mesh _edge, _aura;
        static readonly int ID_Color = Shader.PropertyToID("_Color"), ID_P = Shader.PropertyToID("_P");
        static float dampE(float a, float b, float l, float dt) => a + (b - a) * (1 - Mathf.Exp(-l * dt));

        static MeshRenderer meshObj(string name, Transform parent, Mesh m, Material mat)
        {
            var g = ThreeSpace.create(name, parent);
            g.AddComponent<MeshFilter>().sharedMesh = m;
            var r = g.AddComponent<MeshRenderer>(); r.sharedMaterial = mat; r.shadowCastingMode = ShadowCastingMode.Off;
            return r;
        }

        /** WeaponModel.KIT_ANIM["blade"] */
        public static void animate(WeaponModel.Instance w, WeaponModel.State st)
        {
            float dt = Mathf.Min(0.1f, Mathf.Max(0, st.dt));
            var B = w.ks as S;
            if (B == null)
            {
                _edge ??= WeaponModel.loadMesh("blade/edge"); _aura ??= WeaponModel.loadMesh("blade/aura");
                B = new S { em = CharacterMats.bladeEdge(), am = new Material(Shader.Find("Inkwave/Fx/BladeAura")) { name = "iw-blade-aura" } };
                B.edge = meshObj("edge", w.off, _edge, B.em);
                B.aura = meshObj("aura", w.off, _aura, B.am); B.aura.enabled = false;
                w.ks = B;
            }
            var K = st.runner?.kit as BladeState;
            // LOD like the built-ins (the edge stays: a charging blade is a tell at any range)
            bool near = st.near;
            if (near != B.near)
            {
                B.near = near;
                w.body.enabled = near; w.ink.enabled = near;
                w.bodyFar.enabled = !near; w.inkFar.enabled = !near;
            }
            float ch = K != null ? K.charge : st.charge;
            if (K != null && K.flashT < 0.02f) B.flash = 1;
            if (K == null && st.chargeFlash > 0.9f) B.flash = 1;
            B.flash = Mathf.Max(0, B.flash - dt * 3.2f);
            B.charge = dampE(B.charge, ch, ch > B.charge ? 30 : 9, dt);
            float full = ch >= 0.999f ? 1 : 0;
            B.full = dampE(B.full, full, full > 0 ? 18 : 10, dt);
            Color lc = st.color.linear;
            CharacterMats.setBase(B.em, st.color); CharacterMats.setLamp(B.em, lc, 1);
            CharacterMats.setCoil(B.em, B.charge, B.full, B.flash, st.t);
            bool on = B.charge > 0.01f || B.full > 0.01f || B.flash > 0.01f;
            B.aura.enabled = on && near;
            if (on)
            {
                B.am.SetVector(ID_Color, new Vector4(lc.r, lc.g, lc.b, 1));
                B.am.SetVector(ID_P, new Vector4(B.charge, B.full, B.flash, st.t));
            }
        }
    }
}
