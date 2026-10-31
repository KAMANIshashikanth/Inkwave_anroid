// Port of src/game/special-props.js getSpecialProp(kind): the specials' world props (Kraken, Howl Box speaker, Vortex
// Strike missile, Ink Jet pack, Crab Rig), baked by Tools/bake/specials.mjs into Resources/SpecialProps (IWK1 meshes +
// props.json). Space: metres, +Y up, +Z forward, origin at the bottom centre unless a prop says otherwise (missile: its
// centre). Moving parts come with a pivot (`…At`): a group at the pivot, the mesh inside it at −pivot.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Inkwave
{
    public static class SpecialProps
    {
        const string DIR = "SpecialProps/";

        [Serializable] class Man { public int version; public Rec[] props; }
        [Serializable] class PartRec { public string body, ink; public float[] at; }
        [Serializable] class LegRec { public string body, ink; public float[] at; public float side, index; }
        // one record for every kind (JsonUtility: a field a kind lacks reads as null / empty / 0)
        [Serializable] class Rec
        {
            public string kind, body, ink, glow, cone, coneInk;
            public float[] coneAt, mouth, tail, nose, eyesAt, mouthAt;
            public float[] nozzles;   // (vector lists are flattened by the bake: x, y, z, x, y, z …)
            public string gunSpin, gunSpinInk, ballBody, ballInk;
            public float[] gunSpinAt, gunMuzzle, cannonMuzzle, cannonDir, seatAt;
            public float ballR, hatchY, hatchR;
            public PartRec gun, cannon;
            public LegRec[] legs;
            public float radius, length, height, width, depth;
        }

        public class Def
        {
            public string kind;
            public Mesh body, ink, glow, cone, coneInk;
            public Vector3 coneAt, tail, nose, eyesAt, mouthAt;
            public Vector3? mouth;
            public Vector3[] nozzles;
            // Crab Rig: legs (hip pivots), gun / cannon housings, the spinning barrels, muzzles, the seat, the rolling ball
            public class Leg { public Mesh body, ink; public Vector3 at; public float side; public int index; }
            public Leg[] legs;
            public Mesh gunBody, gunInk, cannonBody, cannonInk, gunSpin, gunSpinInk, ballBody, ballInk;
            public Vector3 gunSpinAt; public Vector3? gunMuzzle, cannonMuzzle, cannonDir, seatAt; public float ballR;
            public float radius, length, height, width, depth;
        }

        static Dictionary<string, Rec> _recs;
        static readonly Dictionary<string, Def> _cache = new Dictionary<string, Def>();
        // JsonUtility turns a missing array into an empty one, never null
        static bool has(float[] a) => a != null && a.Length >= 3;
        static Vector3 v3(float[] a) => has(a) ? new Vector3(a[0], a[1], a[2]) : Vector3.zero;
        static Vector3[] list(float[] a) { if (a == null) return null; var o = new Vector3[a.Length / 3]; for (int i = 0; i < o.Length; i++) o[i] = new Vector3(a[i * 3], a[i * 3 + 1], a[i * 3 + 2]); return o; }

        static void load()
        {
            if (_recs != null) return;
            _recs = new Dictionary<string, Rec>();
            var ta = Resources.Load<TextAsset>(DIR + "props");
            if (ta == null) { Debug.LogWarning("[special-props] Resources/SpecialProps/props.json missing — run Tools/bake/specials.mjs"); return; }
            foreach (var r in JsonUtility.FromJson<Man>(ta.text).props) _recs[r.kind] = r;
        }
        static Mesh mesh(string kind, string name) => string.IsNullOrEmpty(name) ? null : CharacterGeo.loadRigid(DIR, kind + "/" + name);

        /** the geometry set for a special's world prop (cached per kind; shared — never destroy); unknown → null */
        public static Def get(string kind)
        {
            if (_cache.TryGetValue(kind, out var d)) return d;
            load();
            if (!_recs.TryGetValue(kind, out var r)) return _cache[kind] = null;
            d = new Def
            {
                kind = kind, body = mesh(kind, r.body), ink = mesh(kind, r.ink), glow = mesh(kind, r.glow),
                cone = mesh(kind, r.cone), coneInk = mesh(kind, r.coneInk),
                coneAt = v3(r.coneAt), mouth = has(r.mouth) ? v3(r.mouth) : (Vector3?)null, tail = v3(r.tail), nose = v3(r.nose),
                eyesAt = v3(r.eyesAt), mouthAt = v3(r.mouthAt), nozzles = list(r.nozzles),
                radius = r.radius, length = r.length, height = r.height, width = r.width, depth = r.depth,
            };
            d.gunSpin = mesh(kind, r.gunSpin); d.gunSpinInk = mesh(kind, r.gunSpinInk); d.ballBody = mesh(kind, r.ballBody); d.ballInk = mesh(kind, r.ballInk);
            d.gunSpinAt = v3(r.gunSpinAt); d.ballR = r.ballR;
            d.gunMuzzle = has(r.gunMuzzle) ? v3(r.gunMuzzle) : (Vector3?)null; d.cannonMuzzle = has(r.cannonMuzzle) ? v3(r.cannonMuzzle) : (Vector3?)null;
            d.cannonDir = has(r.cannonDir) ? v3(r.cannonDir) : (Vector3?)null; d.seatAt = has(r.seatAt) ? v3(r.seatAt) : (Vector3?)null;
            if (r.gun != null) { d.gunBody = mesh(kind, r.gun.body); d.gunInk = mesh(kind, r.gun.ink); }
            if (r.cannon != null) { d.cannonBody = mesh(kind, r.cannon.body); d.cannonInk = mesh(kind, r.cannon.ink); }
            if (r.legs != null && r.legs.Length > 0)
            {
                d.legs = new Def.Leg[r.legs.Length];
                for (int i = 0; i < r.legs.Length; i++) { var L = r.legs[i]; d.legs[i] = new Def.Leg { body = mesh(kind, L.body), ink = mesh(kind, L.ink), at = v3(L.at), side = L.side, index = (int)L.index }; }
            }
            return _cache[kind] = d;
        }
    }
}
