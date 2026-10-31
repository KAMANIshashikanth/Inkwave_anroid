// Runtime side of src/game/character-weapons.js: the held weapon and sub-weapon prop models. The geometry is baked by
// Tools/bake/weapons.mjs (INKWAVE's own builders run under Node) into Resources/KidWeapons; this file loads it, builds
// the per-character instances (character.js _weaponInstance / _buildBomb / the twins mirror) and ports animateWeapon.
// Weapon space: grip centre at the origin, +Z = barrel forward, +Y = up, character's right = -X.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using static Inkwave.Ctx;

namespace Inkwave
{
    public static class WeaponModel
    {
        // grip holes (character-geo HAND.hole; the right fist mirrors the left)
        public static readonly Vector3 GRIP_HOLE_L = new Vector3(-0.0255f, -0.0525f, 0);
        public static readonly Vector3 GRIP_HOLE_R = new Vector3(0.0255f, -0.0525f, 0);
        public static readonly Vector3 FIST_OFFSET = GRIP_HOLE_R;
        const string DIR = "KidWeapons/";

        [Serializable] class FrameRec { public float[] pos, quat; }
        [Serializable] class LampRec { public string color, emissive; public float intensity, roughness; }
        [Serializable] class PartRec { public string name, mat; public float[] pivot; public LampRec lamp; }
        [Serializable] class WRec { public string kind; public bool dual, mirrorDual, glow, drum, drumCaps, spin; public float drawLen, drumR; public float[] muzzle, drumAt, spinAt; public FrameRec handR, handL, inHand, inHandL; public PartRec[] parts; }
        [Serializable] class SRec { public string kind; public FrameRec handL, inHandL; public bool spin, spinInk, glow; public float[] spinAt; }
        [Serializable] class Man { public WRec[] weapons; public SRec[] subs; }

        public struct Frame { public Vector3 pos; public Quaternion quat; }
        public class Lamp { public Color color, emissive; public float intensity, roughness; }
        public class PartDef { public string name, mat; public Vector3 pivot; public Lamp lamp; public Mesh geo; }
        public class Def
        {
            public string kind; public bool dual, mirrorDual, hasInHandL; public float drawLen, drumR;
            public Vector3 muzzle, drumAt, spinAt; public Frame handR, handL, inHand, inHandL;
            public Mesh body, ink, bodyStatic, inkStatic, bodyFar, inkFar, glow, drum, drumCaps, spin;
            public PartDef[] parts;
        }
        public class SubDef { public string kind; public Frame handL, inHandL; public Vector3 spinAt; public Mesh body, ink, spin, spinInk, glow; }

        public class Part { public Transform g; public MeshRenderer mesh; public Vector3 rest; public string mat; }
        public class Instance
        {
            public Def def; public Transform pivot, off, muzzle, drum, spin, pivotL;
            public MeshRenderer body, ink, bodyFar, inkFar, glow, drumInk, inkL;
            public readonly Dictionary<string, Part> parts = new Dictionary<string, Part>();
            public readonly List<Part> partList = new List<Part>();
            public readonly List<Material> lamps = new List<Material>();
            public Material coil;
            public bool near = true; public float pump, trig, bowDraw;
            public Instance left;
            // animateWeapon state
            public float[] ps; public float heat, drumW, drumA, spinW, spinA;
            public object ks;   // a kit model's own per-instance animation state (BrollyModel …)
            public void setColor(Color c)
            {
                var m = CharacterMats.ink(c);
                ink.sharedMaterial = m; inkFar.sharedMaterial = m;
                if (inkL != null) inkL.sharedMaterial = m;
                if (drumInk != null) drumInk.sharedMaterial = m;
                foreach (var p in partList) if (p.mat == "ink") p.mesh.sharedMaterial = m;
                if (coil != null) coil.SetColor(CharacterMats.ID_Emissive, c);
            }
        }
        public class SubInstance
        {
            public string kind; public Transform group; public MeshRenderer ink, spinInk;
            public void setColor(Color c) { var m = CharacterMats.ink(c); ink.sharedMaterial = m; if (spinInk != null) spinInk.sharedMaterial = m; }
        }
        // animateWeapon's per-frame input (see character.js _animWeapon)
        public class State
        {
            public float t, dt; public Color color; public bool near = true; public int hand;
            public WeaponRunner runner; public float sinceShoot = 99, sinceFlick = 99, sinceRelease = 99;
            public float charge; public bool full; public float chargeFlash, lowInk; public bool firing; public float rolling; public bool grounded; public float groundSpeed;
            public Quaternion? worldQuat;
        }

        // Kit weapons (Game/Kits/*) bring their own part animation: animate(w, st) replaces the built-in one for that kind
        public static readonly Dictionary<string, Action<Instance, State>> KIT_ANIM = new Dictionary<string, Action<Instance, State>>();
        /** a kit model's extra baked mesh (Resources/KidWeapons/<path>) */
        public static Mesh loadMesh(string path) => CharacterGeo.loadRigid(DIR, path);

        static Man _man;
        static readonly Dictionary<string, Def> _cache = new Dictionary<string, Def>();
        static readonly Dictionary<string, SubDef> _subCache = new Dictionary<string, SubDef>();
        // JsonUtility turns a missing array / object into an empty one, never null
        static Vector3 v3(float[] a) => a == null || a.Length < 3 ? Vector3.zero : new Vector3(a[0], a[1], a[2]);
        static Frame fr(FrameRec r) => r == null || r.pos == null || r.pos.Length < 3 || r.quat == null || r.quat.Length < 4 ? new Frame { quat = Quaternion.identity } : new Frame { pos = v3(r.pos), quat = new Quaternion(r.quat[0], r.quat[1], r.quat[2], r.quat[3]) };
        static Color hexC(string h) => h != null && ColorUtility.TryParseHtmlString(h, out var c) ? c : Color.black;

        static void load()
        {
            if (_man != null) return;
            var ta = Resources.Load<TextAsset>(DIR + "weapons");
            if (ta == null) throw new Exception("[weapons] Resources/KidWeapons/weapons.json missing — run Tools/bake/weapons.mjs");
            _man = JsonUtility.FromJson<Man>(ta.text);
        }

        public static Def getWeaponDef(string kind)
        {
            load();
            if (_cache.TryGetValue(kind, out var d)) return d;
            WRec r = null;
            foreach (var w in _man.weapons) if (w.kind == kind) r = w;
            if (r == null) return getWeaponDef("shooter");
            string p = r.kind + "/";
            d = new Def
            {
                kind = r.kind, dual = r.dual, mirrorDual = r.mirrorDual, drawLen = r.drawLen, drumR = r.drumR, hasInHandL = r.inHandL != null && r.inHandL.pos != null && r.inHandL.pos.Length == 3,
                muzzle = v3(r.muzzle), drumAt = v3(r.drumAt), spinAt = v3(r.spinAt),
                handR = fr(r.handR), handL = fr(r.handL), inHand = fr(r.inHand), inHandL = fr(r.inHandL),
                body = CharacterGeo.loadRigid(DIR, p + "body"), ink = CharacterGeo.loadRigid(DIR, p + "ink"),
                bodyStatic = CharacterGeo.loadRigid(DIR, p + "bodyStatic"), inkStatic = CharacterGeo.loadRigid(DIR, p + "inkStatic"),
                bodyFar = CharacterGeo.loadRigid(DIR, p + "bodyFar"), inkFar = CharacterGeo.loadRigid(DIR, p + "inkFar"),
                glow = r.glow ? CharacterGeo.loadRigid(DIR, p + "glow") : null,
                drum = r.drum ? CharacterGeo.loadRigid(DIR, p + "drum") : null, drumCaps = r.drumCaps ? CharacterGeo.loadRigid(DIR, p + "drumCaps") : null,
                spin = r.spin ? CharacterGeo.loadRigid(DIR, p + "spin") : null,
                parts = Array.ConvertAll(r.parts ?? new PartRec[0], pr => new PartDef
                {
                    name = pr.name, mat = pr.mat, pivot = v3(pr.pivot), geo = CharacterGeo.loadRigid(DIR, p + "part_" + pr.name),
                    lamp = pr.lamp != null && pr.lamp.color != null ? new Lamp { color = hexC(pr.lamp.color), emissive = hexC(pr.lamp.emissive), intensity = pr.lamp.intensity, roughness = pr.lamp.roughness } : null,
                }),
            };
            _cache[kind] = d;
            return d;
        }

        public static SubDef getSubDef(string kind)
        {
            load();
            if (_subCache.TryGetValue(kind, out var d)) return d;
            SRec r = null;
            foreach (var s in _man.subs) if (s.kind == kind) r = s;
            if (r == null) return kind == "bomb" ? null : getSubDef("bomb");
            string p = "sub_" + r.kind + "/";
            d = new SubDef
            {
                kind = r.kind, handL = fr(r.handL), inHandL = fr(r.inHandL), spinAt = v3(r.spinAt),
                body = CharacterGeo.loadRigid(DIR, p + "body"), ink = CharacterGeo.loadRigid(DIR, p + "ink"),
                spin = r.spin ? CharacterGeo.loadRigid(DIR, p + "spin") : null, spinInk = r.spinInk ? CharacterGeo.loadRigid(DIR, p + "spinInk") : null,
                glow = r.glow ? CharacterGeo.loadRigid(DIR, p + "glow") : null,
            };
            _subCache[kind] = d;
            return d;
        }

        static MeshRenderer mesh(string name, Transform parent, Mesh m, Material mat, bool shadow)
        {
            var g = ThreeSpace.create(name, parent);
            g.AddComponent<MeshFilter>().sharedMesh = m;
            var r = g.AddComponent<MeshRenderer>(); r.sharedMaterial = mat;
            r.shadowCastingMode = shadow ? ShadowCastingMode.On : ShadowCastingMode.Off;
            return r;
        }

        /** One held weapon: pivot at the fist's grip axis (twirls spin about the handle) → off = weapon space. */
        public static Instance instance(string kind, CharacterMats mats, bool left)
        {
            var d = getWeaponDef(kind);
            Vector3 hole = left ? GRIP_HOLE_L : FIST_OFFSET; var inHand = left ? d.inHandL : d.inHand;
            var w = new Instance { def = d };
            w.pivot = ThreeSpace.create("weapon:" + kind + (left ? ":L" : "")).transform; w.pivot.localPosition = hole;
            w.off = ThreeSpace.create("off", w.pivot).transform; w.off.localPosition = inHand.pos - hole; w.off.localRotation = inHand.quat;
            // two LODs: near = static shell + animated parts; far = the complete weapon merged at rest (decimated)
            var plastic = CharacterMats.plastic;
            w.body = mesh("body", w.off, d.bodyStatic ?? d.body, plastic, true);
            w.ink = mesh("ink", w.off, d.inkStatic ?? d.ink, plastic, true);
            w.bodyFar = mesh("bodyFar", w.off, d.bodyFar ?? d.body, plastic, true); w.bodyFar.enabled = false;
            w.inkFar = mesh("inkFar", w.off, d.inkFar ?? d.ink, plastic, true); w.inkFar.enabled = false;
            foreach (var pd in d.parts)
            {
                if (pd.geo == null) continue;
                var g = ThreeSpace.create("part:" + pd.name, w.off).transform; g.localPosition = pd.pivot;
                Material mat = pd.mat == "lamp" ? CharacterMats.lamp(pd.lamp) : plastic;
                var m = mesh(pd.name, g, pd.geo, mat, false);
                var part = new Part { g = g, mesh = m, rest = pd.pivot, mat = pd.mat };
                w.parts[pd.name] = part; w.partList.Add(part);
                if (pd.mat == "lamp") w.lamps.Add(mat);
            }
            if (d.glow != null) { w.coil = CharacterMats.coilMaterial(); w.glow = mesh("glow", w.off, d.glow, w.coil, false); }
            if (d.drum != null)
            {
                w.drum = ThreeSpace.create("drum", w.off).transform; w.drum.localPosition = d.drumAt;
                w.drumInk = mesh("drumInk", w.drum, d.drum, plastic, true);
                if (d.drumCaps != null) mesh("drumCaps", w.drum, d.drumCaps, plastic, false);
            }
            if (d.spin != null && !left)
            {
                // spinner / zooka: the barrel cluster turns about its own axis (weapon +Z through spinAt)
                w.spin = ThreeSpace.create("spin", w.off).transform; w.spin.localPosition = d.spinAt;
                mesh("spinMesh", w.spin, d.spin, plastic, true).transform.localPosition = -d.spinAt;
            }
            w.muzzle = ThreeSpace.create("muzzle", w.off).transform; w.muzzle.localPosition = d.muzzle;
            return w;
        }

        /** twins: the same gun mirrored across the body's X axis into the left fist (reflection: p → (-x, y, z),
         *  q → (x, -y, -z, w)); a static copy that rides the left hand's pose (no parts, no second anchor). */
        public static void addMirror(Instance w, CharacterMats mats)
        {
            var d = w.def;
            var pivotL = ThreeSpace.create("weapon:" + d.kind + ":mirror").transform; pivotL.localPosition = new Vector3(-FIST_OFFSET.x, FIST_OFFSET.y, FIST_OFFSET.z);
            var offL = ThreeSpace.create("off", pivotL).transform;
            var op = d.inHand.pos - FIST_OFFSET; op.x = -op.x; offL.localPosition = op;
            offL.localRotation = new Quaternion(d.inHand.quat.x, -d.inHand.quat.y, -d.inHand.quat.z, d.inHand.quat.w);
            var mir = ThreeSpace.create("mirror", offL).transform; mir.localScale = new Vector3(-1, 1, 1);
            mesh("body", mir, d.body, CharacterMats.plastic, true);
            w.inkL = mesh("ink", mir, d.ink, CharacterMats.plastic, true);
            w.pivotL = pivotL;
        }

        /** The sub prop held in the left hand while a throw is aimed. */
        public static SubInstance subInstance(string kind, Transform handL, CharacterMats mats)
        {
            var d = getSubDef(kind);
            var g = ThreeSpace.create("sub:" + kind, handL).transform;
            var s = new SubInstance { kind = kind, group = g };
            if (d == null) { s.ink = mesh("ink", g, null, CharacterMats.plastic, false); return s; }
            g.localPosition = d.inHandL.pos; g.localRotation = d.inHandL.quat;
            mesh("body", g, d.body, CharacterMats.plastic, true);
            s.ink = mesh("ink", g, d.ink, CharacterMats.plastic, true);
            if (d.spin != null) mesh("spin", g, d.spin, CharacterMats.plastic, true);
            if (d.spinInk != null) s.spinInk = mesh("spinInk", g, d.spinInk, CharacterMats.plastic, false);
            if (d.glow != null) mesh("glow", g, d.glow, CharacterMats.glowShared, false);
            return s;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Part animation (animateWeapon): character.js calls it once per frame per held weapon instance.
        // Writes: part transforms, lamp/coil uniforms, LOD visibility, w.pump (blaster pump stroke — the body's left hand
        // rides it) + w.trig (trigger squeeze — the index finger follows).
        // ---------------------------------------------------------------------------------------------------------
        static float pulseE(float t, float atk, float dec) => t < 0 ? 0 : t < atk ? t / atk : Mathf.Exp(-(t - atk) * dec);
        static float mjE(float t) { t = Mathf.Min(1, Mathf.Max(0, t)); return t * t * t * (10 + t * (6 * t - 15)); }
        static float dampE(float a, float b, float l, float dt) => a + (b - a) * (1 - Mathf.Exp(-l * dt));
        static float sprE(float[] S, int i, float target, float hz, float zeta, float dt)
        {   // exact damped spring (stable for any damping / step)
            float w = Mathf.PI * 2 * hz, x0 = S[i] - target, v0 = S[i + 1];
            float x, v;
            if (zeta < 0.999f)
            {
                float wd = w * Mathf.Sqrt(1 - zeta * zeta), e = Mathf.Exp(-zeta * w * dt), c = Mathf.Cos(wd * dt), sn = Mathf.Sin(wd * dt), B = (v0 + zeta * w * x0) / wd;
                x = e * (x0 * c + B * sn); v = e * ((-zeta * w * x0 + wd * B) * c + (-zeta * w * B - wd * x0) * sn);
            }
            else { float e = Mathf.Exp(-w * dt), B = v0 + w * x0; x = e * (x0 + B * dt); v = e * (v0 - w * B * dt); }
            S[i] = x + target; S[i + 1] = v; return S[i];
        }
        static Quaternion rx(float a) => Quaternion.AngleAxis(a * Mathf.Rad2Deg, Vector3.right);
        static Quaternion rz(float a) => Quaternion.AngleAxis(a * Mathf.Rad2Deg, Vector3.forward);
        static void setZ(Part p, float z) { var v = p.g.localPosition; v.z = z; p.g.localPosition = v; }

        public static void animate(Instance w, State st)
        {
            if (KIT_ANIM.TryGetValue(w.def.kind, out var kitAnim)) { kitAnim(w, st); return; }   // kit weapons animate themselves
            var d = w.def; var P = w.parts; string kind = d.kind; float t = st.t, dt = Mathf.Min(0.1f, Mathf.Max(0, st.dt)); Color col = st.color.linear;   // three works in linear colour
            if (w.ps == null) { w.ps = new float[12]; w.ps[0] = 1.3f; }
            var ps = w.ps; var R = st.runner;
            // per-instance shot clock (dual wield keeps one per hand: the left pistol's comes in as st.sinceShoot)
            float ts = st.sinceShoot;
            // near/far LOD
            bool near = st.near;
            if (near != w.near)
            {
                w.near = near;
                w.body.enabled = near; w.ink.enabled = near;
                w.bodyFar.enabled = !near; w.inkFar.enabled = !near;
                foreach (var g in w.partList) g.g.gameObject.SetActive(near);
            }
            // roller drum: rolls with the ground, coasts down, gets flung round by the flick
            if (w.drum != null)
            {
                if (st.rolling > 0.3f && st.grounded) w.drumW = st.groundSpeed / (d.drumR > 0 ? d.drumR : 0.1f);
                else w.drumW *= Mathf.Exp(-dt * 2.2f);
                float ft = st.sinceFlick;
                if (ft >= 0.15f && ft - dt < 0.15f) w.drumW += 34;
                w.drumA += w.drumW * dt;
                w.drum.localRotation = rx(w.drumA);
            }
            float u = st.sinceShoot;
            if (kind == "blaster")
            {   // pump stroke — computed at every distance (the body's left hand rides it)
                float pk = 0;
                if (u < 0.6f) pk = u < 0.14f ? 0 : u < 0.29f ? mjE((u - 0.14f) / 0.15f) : u < 0.33f ? 1 : u < 0.46f ? 1 - mjE((u - 0.33f) / 0.13f) : -0.1f * Mathf.Sin(Mathf.PI * Mathf.Min(1, (u - 0.46f) / 0.12f));
                w.pump = pk;
            }
            if (w.coil != null)
            {   // charger coil / splatling meter (always drawn — a charging weapon is a tell at any range)
                float ch = st.charge, full = st.full ? 1 : 0;
                if (kind == "splatling" && R != null) { ch = R.charging ? R.charge : R.burstT > 0 ? 1 : 0; full = R.charging && R.charge >= 0.999f ? 1 : 0; }
                CharacterMats.setCoil(w.coil, ch, full, st.chargeFlash, t);
            }
            if (!near) return;
            float shotK = pulseE(ts, 0.006f, 30);
            if (P.TryGetValue("trigger", out var trigger))
            {
                float want = kind == "charger" ? (st.charge > 0.01f ? 1 : 0) : kind == "splatling" ? (R != null ? (R.charging || R.burstT > 0 ? 1 : 0) : (st.firing ? 1 : 0))
                    : kind == "blaster" || kind == "dualies" ? (ts < 0.07f ? 1 : 0) : (st.firing && u < 0.16f ? 1 : 0);
                w.trig = dampE(w.trig, want, want > w.trig ? 45 : 22, dt);
                trigger.g.localRotation = rx(0.42f * w.trig);
            }
            if (kind == "shooter")
            {
                setZ(P["bolt"], P["bolt"].rest.z - 0.0095f * shotK);
                float ck = pulseE(ts, 0.01f, 18);
                P["can"].g.localScale = new Vector3(1 + 0.07f * ck, 1 + 0.07f * ck, 1 - 0.035f * ck);
                ledShot(P["led"], st, shotK, t);
            }
            else if (kind == "dualies")
            {
                // each pistol's slide snaps back 13 mm on its own shot and rides home; LED blinks per shot / red when low
                float k = pulseE(ts, 0.005f, 26);
                setZ(P["slide"], P["slide"].rest.z - 0.013f * k);
                setZ(P["slideInk"], P["slideInk"].rest.z - 0.013f * k);
                ledShot(P["led"], st, k, t);
            }
            else if (kind == "blaster")
            {
                setZ(P["pump"], P["pump"].rest.z - 0.036f * w.pump);
                bool dep = u < 0.36f;
                float needle = sprE(ps, 0, dep ? -1.95f : 1.3f, dep ? 9 : 4.2f, dep ? 0.55f : 0.28f, dt);
                P["needle"].g.localRotation = rz(needle + 0.018f * Mathf.Sin(t * 41) * (u > 0.6f ? 1 : 0.3f));
                if (ts < dt * 1.5f) ps[3] -= 7;
                if (u >= 0.33f && u - dt < 0.33f) ps[3] += 4.5f;
                float bq = Mathf.Max(-0.3f, Mathf.Min(0.3f, sprE(ps, 2, 0, 7, 0.22f, dt)));
                P["bulb"].g.localScale = new Vector3(1 - 0.35f * bq, 1 - 0.35f * bq, 1 + 0.9f * bq);
            }
            else if (kind == "charger")
            {
                float ch = st.charge, full = st.full ? 1 : 0;
                float btg = -0.03f * Mathf.Min(1, Mathf.Max(0, ch));
                float bolt = sprE(ps, 4, btg, btg < ps[4] ? 5 : 16, 0.32f, dt);
                setZ(P["bolt"], P["bolt"].rest.z + Mathf.Min(0.004f, Mathf.Max(-0.034f, bolt)));
                CharacterMats.setLamp(P["lens"].mesh.sharedMaterial, Color.Lerp(col, Color.white, 0.25f * full), 0.12f + 3.2f * ch * ch + full * (1.2f + 0.8f * Mathf.Sin(t * 31)) + 5 * st.chargeFlash);
                CharacterMats.setLamp(P["eyepiece"].mesh.sharedMaterial, col, 0.04f + 0.9f * ch * ch + 0.6f * full);
                float rel = st.sinceRelease;
                if (rel < dt * 1.5f) w.heat = 1;
                w.heat *= Mathf.Exp(-dt * 3.2f);
                CharacterMats.setLamp(P["ports"].mesh.sharedMaterial, Color.Lerp(col, Color.white, 0.5f * w.heat), 6 * w.heat * w.heat);
            }
            else if (kind == "roller")
            {
                var m = P["led"].mesh.sharedMaterial;
                CharacterMats.setLampIntensity(m, st.rolling > 0.3f ? 0.5f + 2.6f * (Mathf.Sin(t * Mathf.PI * 8) > 0 ? 1 : 0.15f) : 0.45f);
            }
            else if (kind == "slosher")
            {
                // the ink surface stays level against the swing (a lagging, ringing liquid, clamped to the rim), dips as
                // each throw empties the bucket and wells back up; the thumb lever trips as the ink leaves
                var s = P["surface"];
                if (st.worldQuat.HasValue)
                {
                    var wq = st.worldQuat.Value;
                    Vector3 av = wq * Vector3.up;                                     // bucket axis in world
                    Quaternion aq = Quaternion.FromToRotation(av, Vector3.up);         // world tilt that would level the surface
                    Quaternion aq2 = Quaternion.Inverse(wq) * aq * wq;                // … expressed in bucket space
                    Vector3 av2 = new Vector3(aq2.x, aq2.y, aq2.z); float sn = av2.magnitude;
                    float ang = 2 * Mathf.Atan2(sn, aq2.w); if (ang > Mathf.PI) ang -= Mathf.PI * 2;
                    const float lim = 0.6f; float a = Mathf.Max(-lim, Mathf.Min(lim, ang));
                    if (sn > 1e-5f) av2 /= sn; else av2 = new Vector3(1, 0, 0);
                    sprE(ps, 6, av2.x * a, 2.2f, 0.16f, dt); sprE(ps, 8, av2.z * a, 2.2f, 0.16f, dt);
                }
                s.g.localRotation = ThreeSpace.eulerXYZ(ps[6], 0, ps[8]);
                float drain = u < 0.6f ? (u < 0.16f ? mjE(u / 0.16f) : 1 - mjE((u - 0.16f) / 0.44f)) : 0;
                var sp0 = s.g.localPosition; sp0.y = s.rest.y - 0.034f * drain + 0.002f * Mathf.Sin(t * 7.3f); s.g.localPosition = sp0;
                float rip = 1 + 0.02f * Mathf.Sin(t * 11 + 1.3f) * (0.3f + drain);
                s.g.localScale = new Vector3(rip * (1 - 0.1f * drain), 1, (2 - rip) * (1 - 0.1f * drain));
                P["lever"].g.localRotation = rx(-0.4f * (u < 0.3f ? 1 - mjE(Mathf.Max(0, u - 0.18f) / 0.12f) : 0));
            }
            else if (kind == "splatling")
            {
                // barrel cluster: spins up with the charge, screams while it streams, spins down with inertia
                float want = 0;
                if (R != null) want = R.charging ? 14 + 46 * R.charge : R.burstT > 0 ? 64 : 0;
                else want = st.firing ? 30 + 30 * st.charge : 0;
                w.spinW = dampE(w.spinW, want, want > w.spinW ? 5 : 1.6f, dt);
                w.spinA = (w.spinA + w.spinW * dt) % (Mathf.PI * 2);
                var b = P["barrels"];
                b.g.localRotation = rz(w.spinA);
                setZ(b, b.rest.z - 0.004f * shotK);   // each round nudges the cluster back
            }
        }

        static void ledShot(Part g, State st, float k, float t)
        {
            if (g == null) return;
            var m = g.mesh.sharedMaterial;
            if (st.lowInk > 0.5f) CharacterMats.setLamp(m, new Color(1, 0.16f, 0.1f), 0.4f + 2.2f * (0.5f + 0.5f * Mathf.Sin(t * Mathf.PI * 6.4f)));
            else CharacterMats.setLamp(m, new Color(0.24f, 1, 0.48f), 1.1f + 4 * k);
        }
    }
}
