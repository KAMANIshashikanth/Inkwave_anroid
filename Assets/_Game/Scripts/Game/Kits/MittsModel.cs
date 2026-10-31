// Runtime side of src/game/kits/mitts-model.js — the Sponge Mitts' part animation (animateMitts): a pair of squishy
// ink-soaked sponge boxing gloves (one per fist, dual wield). Each glove squashes on its own punch, both swell and glow
// while a leap charges, the left one flattens against the wall while clinging. The geometry is baked by
// Tools/bake/weapons.mjs; the sponge pad is its own part ('pad', pivot at the pad ↔ cuff seam) drawn with the sponge
// material (Kid shader kind 15).
using UnityEngine;

namespace Inkwave
{
    public static class MittsModel
    {
        class S { public float[] ms = { 1, 0, 1, 0, 0, 0, 0, 0 }; public Material sponge; public float lastPunch = 99; }
        static float pulse(float t, float atk, float dec) => t < 0 ? 0 : t < atk ? t / atk : Mathf.Exp(-(t - atk) * dec);
        static float spring(float[] S, int i, float target, float hz, float zeta, float dt)
        {   // exact damped spring (see WeaponModel sprE)
            float w = Mathf.PI * 2 * hz, x0 = S[i] - target, v0 = S[i + 1];
            float wd = w * Mathf.Sqrt(1 - zeta * zeta), e = Mathf.Exp(-zeta * w * dt), c = Mathf.Cos(wd * dt), sn = Mathf.Sin(wd * dt), B = (v0 + zeta * w * x0) / wd;
            S[i] = e * (x0 * c + B * sn) + target; S[i + 1] = e * ((-zeta * w * x0 + wd * B) * c + (-zeta * w * B - wd * x0) * sn);
            return S[i];
        }

        /** WeaponModel.KIT_ANIM["mitts"] (runs for both gloves: st.hand 0 = right, 1 = left) */
        public static void animate(WeaponModel.Instance w, WeaponModel.State st)
        {
            float dt = Mathf.Min(0.1f, Mathf.Max(0, st.dt));
            var s = w.ks as S;
            if (s == null) w.ks = s = new S { sponge = CharacterMats.sponge() };
            var Sm = s.ms;
            // near / far LOD (kit weapons animate themselves, LOD included)
            bool near = st.near;
            if (near != w.near)
            {
                w.near = near;
                w.body.enabled = near; w.ink.enabled = near;
                w.bodyFar.enabled = !near; w.inkFar.enabled = !near;
                foreach (var g in w.partList) g.g.gameObject.SetActive(near);
            }
            if (!w.parts.TryGetValue("pad", out var pad)) return;
            if (pad.mesh.sharedMaterial != s.sponge) pad.mesh.sharedMaterial = s.sponge;   // (setColor re-assigns 'ink' parts; take it back)
            CharacterMats.setBase(s.sponge, st.color);
            var R = st.runner; var K = R?.kit as MittsState;
            int hand = st.hand;
            float ts = R != null ? R.sinceHand[hand] : st.sinceShoot;
            // punch: the glove lands at full reach (~45 ms in): squash along the face, bulge round it, jiggle back
            if (ts < s.lastPunch) Sm[1] -= 7.5f;                                     // (new punch this frame: kick the spring)
            s.lastPunch = ts;
            float charge = K != null && K.charging ? K.charge : 0;
            float cling = K != null && K.cling && hand == 1 ? 1 : 0;
            float leap = K != null && K.leaping ? 1 : 0;
            float land = K != null ? pulse(K.landT - 0.01f, 0.02f, 7) : 0;
            float wob = charge > 0 ? 0.022f * charge * Mathf.Sin(st.t * (22 + 14 * charge) + hand * 1.7f) : 0;
            float zT = 1 + 0.08f * charge - 0.3f * cling + 0.05f * leap - 0.28f * land + wob;
            float z = spring(Sm, 0, zT, charge > 0 ? 6 : 9, 0.3f, dt);
            float xy = 1 + (1 - z) * 0.55f + 0.1f * charge + 0.06f * cling;
            pad.g.localScale = new Vector3(xy, xy * (1 - 0.35f * cling * 0.5f), Mathf.Max(0.55f, z));
            // glow: builds with the charge, flashes when a fist leaves, pulses at full charge
            float full = charge >= 0.999f ? 0.35f + 0.25f * Mathf.Sin(st.t * 18) : 0;
            float gl = 0.06f + 1.1f * charge * charge + full + 1.4f * pulse(ts - 0.005f, 0.01f, 20) + 0.8f * land;
            Sm[4] += (gl - Sm[4]) * (1 - Mathf.Exp(-dt * 30));
            CharacterMats.setCoil(s.sponge, Sm[4], 1 - 0.4f * charge, 0, 0);
        }
    }
}
