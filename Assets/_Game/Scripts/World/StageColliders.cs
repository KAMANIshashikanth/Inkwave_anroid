// The set dressing's solid props as collision boxes (main.js _buildWorldNow: PropKit.add hands them back and they become
// part of the level — physics, nav, paint, the lightmap layout). Baked per stage and mode by Tools/bake/pages/colliders.js
// into Resources/Stages/<id>/colliders.<mode>.json; `hash` is the web level's layout checksum with them.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Inkwave.World
{
    public static class StageColliders
    {
        [Serializable] class Rec { public float[] min, max, center, size; public double rotY; public bool obox, roof, rail, perch; }
        [Serializable] class Man { public string id, mode, hash; public Rec[] colliders; }
        static Vector3 v3(float[] a) => a != null && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : Vector3.zero;

        /** the stage's prop colliders for a mode (empty when not baked: the bare layout, no AO) */
        public static List<Level.ExtraCollider> load(string id, string mode = "turf")
        {
            var outL = new List<Level.ExtraCollider>();
            var ta = Resources.Load<TextAsset>($"Stages/{id}/colliders.{mode}");
            if (ta == null) { Debug.LogWarning($"[inkwave] no prop colliders for {id} ({mode}) — run Tools/bake/browser.mjs colliders"); return outL; }
            foreach (var r in JsonUtility.FromJson<Man>(ta.text).colliders)
                outL.Add(new Level.ExtraCollider { min = v3(r.min), max = v3(r.max), center = v3(r.center), size = v3(r.size), rotY = r.rotY, obox = r.obox, roof = r.roof, rail = r.rail, perch = r.perch });
            return outL;
        }
    }
}
