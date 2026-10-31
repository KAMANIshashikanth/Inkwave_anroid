// Runtime side of src/game/kits/bow-model.js — the Tideline Bow's part animation (animateBow). The geometry is baked by
// Tools/bake/weapons.mjs (bow-model.js run under Node).
//
// Weapon space: the ORIGIN is the nock on the string, held in the right fist (gripR); +Z = the arrows' flight, +Y = up,
// character's right = -X. At rest (braced) the string runs straight through the nock and the riser's grip sits `brace` in
// front of it, where the left fist holds it (gripL). The bow is authored upright; the character (poseBow) cants the whole
// weapon onto its side on the ground so the fan reads flat.
// Drawing: the character pulls the right fist back along the arrow by drawLen × draw (w.bowDraw) and this file slides the
// whole bow forward in weapon space by the same amount (the `off` group), so in the world the bow and the left hand stay
// put while the string hand draws. What rides the fist instead — the string's nock and the three nocked arrows — is
// moved back by the same amount. The limbs flex with the string. On the release the string snaps home and rings while
// the fist follows through, the arrows vanish (they're in the air) and a new trio nocks in. The two glow rings on the
// riser light at ring 1 and at full draw (coil material, aSeg 0.45 / 1.0); the arrowheads glow brighter at each ring.
using UnityEngine;
using static Inkwave.Ctx;

namespace Inkwave
{
    public static class BowModel
    {
        const float FLEX = 0.3f;   // limb bend at full draw (rad, about each limb's root)
        static readonly Vector3 TIP_U = new Vector3(0, 0.382f, 0), TIP_L = new Vector3(0, -0.407f, 0);          // string ends on the limb tips (at rest)
        static readonly Vector3 ROOT_U = new Vector3(0, 0.165f, 0.118f), ROOT_L = new Vector3(0, -0.19f, 0.118f);   // limb roots in the riser pockets

        class S { public Vector3 rest, ax; public float str, strV, lastRel = 99, glow = 0.4f; public bool loose; }

        static Vector3 tipAt(Vector3 tip, Vector3 root, float th)
        {
            float dy = tip.y - root.y, dz = tip.z - root.z, c = Mathf.Cos(th), s = Mathf.Sin(th);
            return new Vector3(tip.x, root.y + dy * c - dz * s, root.z + dy * s + dz * c);
        }
        static void aimString(WeaponModel.Part g, Vector3 nock, Vector3 tip, Vector3 dirRest, float L)
        {
            Vector3 v = tip - nock; float len = v.magnitude; if (len == 0) len = 1e-4f;
            g.g.localPosition = nock; g.g.localRotation = Quaternion.FromToRotation(dirRest, v / len); g.g.localScale = new Vector3(1, len / L, 1);
        }
        static Quaternion rx(float a) => Quaternion.AngleAxis(a * Mathf.Rad2Deg, Vector3.right);

        /** WeaponModel.KIT_ANIM["bow"] */
        public static void animate(WeaponModel.Instance w, WeaponModel.State st)
        {
            var d = w.def; var P = w.parts; float t = st.t, dt = Mathf.Min(0.1f, Mathf.Max(0, st.dt));
            var s = w.ks as S;
            if (s == null) { w.ks = s = new S { rest = w.off.localPosition, ax = w.off.localRotation * Vector3.forward }; w.pump = 0; w.trig = 0; }
            var R = st.runner;
            float hand = Mathf.Max(0, w.bowDraw);
            bool drawing = R != null ? R.charging : st.charge > 0.01f;
            float c = R != null ? R.charge : st.charge;
            // the bow rides forward by the fist's draw (so it stays put in the world)
            w.off.localPosition = s.rest + s.ax * (d.drawLen * hand);
            // string: rides the fist while drawing; loosed, it snaps home and rings, then waits for the fist to come back
            float rel = st.sinceRelease;
            if (rel < s.lastRel - 1e-4f) { s.loose = true; s.strV = 0; }
            s.lastRel = rel;
            if (drawing) s.loose = false;
            if (s.loose)
            {
                const float om = Mathf.PI * 2 * 15, z = 0.3f;   // exact underdamped spring toward 0
                float wd = om * Mathf.Sqrt(1 - z * z), e = Mathf.Exp(-z * om * dt), co = Mathf.Cos(wd * dt), sn = Mathf.Sin(wd * dt);
                float x0 = s.str, v0 = s.strV, Bq = (v0 + z * om * x0) / wd;
                s.str = e * (x0 * co + Bq * sn); s.strV = e * ((-z * om * x0 + wd * Bq) * co + (-z * om * Bq - wd * x0) * sn);
                s.str = Mathf.Max(-0.16f, s.str);
                if (rel > 0.3f && hand < 0.03f && Mathf.Abs(s.str) < 0.02f) s.loose = false;
            }
            else { s.str = hand; s.strV = 0; }
            // limbs flex with the string
            float phi = FLEX * Mathf.Min(1.05f, Mathf.Max(-0.2f, s.str));
            P["limbU"].g.localRotation = rx(-phi); P["limbUInk"].g.localRotation = rx(-phi);
            P["limbL"].g.localRotation = rx(phi); P["limbLInk"].g.localRotation = rx(phi);
            // string halves from the nock to the flexed tips
            Vector3 nock = new Vector3(0, 0, -d.drawLen * s.str);
            aimString(P["stringU"], nock, tipAt(TIP_U, ROOT_U, -phi), Vector3.up, Mathf.Abs(TIP_U.y));
            aimString(P["stringL"], nock, tipAt(TIP_L, ROOT_L, phi), Vector3.down, Mathf.Abs(TIP_L.y));
            // nocked arrows: gone on the release (they're flying), a fresh trio nocks in a beat later
            float show = drawing ? 1 : rel < 0.3f ? 0 : Mathf.Min(1, (rel - 0.3f) / 0.12f);
            foreach (var n in new[] { "arrows", "heads", "fletch" })
            {
                var g = P[n].g;
                g.localPosition = nock; g.localScale = Vector3.one * Mathf.Max(0.001f, show);
                bool on = show > 0.01f; if (g.gameObject.activeSelf != on) g.gameObject.SetActive(on);
            }
            // glow: heads brighten at each ring; the riser's rings light in turn (coil material)
            int ring = c >= 0.999f ? 2 : c >= Config.WEAPONS["bow"].ring1 ? 1 : 0;
            float want = drawing ? 0.35f + 1.4f * c + (ring >= 1 ? 1.4f : 0) + (ring == 2 ? 2.2f + 0.9f * Mathf.Sin(t * 30) : 0) : 0.35f;
            s.glow += (want - s.glow) * (1 - Mathf.Exp(-dt * 18));
            var hm = P["heads"].mesh.sharedMaterial;
            Color col = st.color.linear;
            CharacterMats.setBase(hm, (col * 0.55f).gamma);
            CharacterMats.setLamp(hm, col, s.glow + 5 * st.chargeFlash);
            if (w.coil != null) CharacterMats.setCoil(w.coil, drawing ? c : 0, drawing && ring == 2 ? 1 : 0, st.chargeFlash, t);
        }
    }
}
