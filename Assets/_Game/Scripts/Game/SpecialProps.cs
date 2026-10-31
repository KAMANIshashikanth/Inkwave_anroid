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
        // one record for every kind (JsonUtility: a field a kind lacks reads as null / empty / 0)
        [Serializable] class Rec
        {
            public string kind, body, ink, glow, cone, coneInk;
            public float[] coneAt, mouth, tail, nose, eyesAt, mouthAt;
            public float radius, length, height, width, depth;
        }

        public class Def
        {
            public string kind;
            public Mesh body, ink, glow, cone, coneInk;
            public Vector3 coneAt, tail, nose, eyesAt, mouthAt;
            public Vector3? mouth;
            public float radius, length, height, width, depth;
        }

        static Dictionary<string, Rec> _recs;
        static readonly Dictionary<string, Def> _cache = new Dictionary<string, Def>();
        // JsonUtility turns a missing array into an empty one, never null
        static bool has(float[] a) => a != null && a.Length >= 3;
        static Vector3 v3(float[] a) => has(a) ? new Vector3(a[0], a[1], a[2]) : Vector3.zero;

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
                eyesAt = v3(r.eyesAt), mouthAt = v3(r.mouthAt),
                radius = r.radius, length = r.length, height = r.height, width = r.width, depth = r.depth,
            };
            return _cache[kind] = d;
        }
    }
}
