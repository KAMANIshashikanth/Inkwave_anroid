// Port of main.js _loadLightmap — the baked ambient occlusion (tools/bake-ao.mjs), copied from
// reference/inkwave/assets/lightmaps into Resources/Lightmaps/<layoutId>.{png,json}. Applied only when the bake matches
// this exact layout: level.layoutLightmap(meta.ppm, meta.size) must reproduce meta.hash, else the faces lose their light
// rects and the level renders without AO (one warning).
using System;
using UnityEngine;

namespace Inkwave.World
{
    public static class Lightmaps
    {
        [Serializable] class Meta { public string id, hash; public int size; public float ppm; }

        /** call before Level.buildGeometry (it writes the light rects into the mesh); null = render without AO */
        public static Texture2D load(string layoutId, Level level)
        {
            var ta = Resources.Load<TextAsset>("Lightmaps/" + layoutId);
            if (ta == null) { Debug.LogWarning($"[inkwave] no lightmap for {layoutId}"); clear(level); return null; }
            var meta = JsonUtility.FromJson<Meta>(ta.text);
            level.layoutLightmap(meta.ppm, meta.size);
            if (level.layoutHash != meta.hash)
            {
                Debug.LogWarning($"[inkwave] lightmap for {layoutId} is stale (layout {level.layoutHash}, bake {meta.hash}) — re-run tools/bake-ao.mjs");
                clear(level);
                return null;
            }
            var tex = Resources.Load<Texture2D>("Lightmaps/" + layoutId);
            if (tex == null) { Debug.LogWarning($"[inkwave] lightmap image for {layoutId} missing"); clear(level); return null; }
            return tex;
        }

        static void clear(Level level) { level.lightSize = 0; foreach (var f in level.faces) f.light = null; }
    }
}
