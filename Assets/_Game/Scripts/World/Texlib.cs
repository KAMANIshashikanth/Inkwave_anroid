// Runtime half of src/world/texlib.js plus the slot table of levelMaterial.js createLevelMaterial: the baked texture
// library (Tools/bake/pages/texlib.js → Resources/Texlib, one Texture2DArray per map: albedo sRGB, normal + orm linear)
// and its per-PATTERN slot table (Tools/bake/texlib-slots.mjs → texlib_slots.json), bound as global shader textures for
// Level.shader. main.js sizes the library from the quality tier — 512 on a 4096 paint atlas, else 256, × memK (0.5 on
// touch devices); 256 is the smallest baked set, so touch devices always get 256.
// Missing bake → one warning and Level.shader keeps its procedural fallback (main.js: "texture library failed").
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Inkwave.World
{
    public static class Texlib
    {
        [Serializable] class Lib { public string[] names; }
        [Serializable] class Slots { public int slots, gel; public string[] names; public float[] data; }

        static Dictionary<string, int> index;
        static Texture2DArray albedo, normal, orm;
        static Texture2D slotTex;

        public static bool ready => albedo != null && slotTex != null;
        public static int size { get; private set; }

        /** layer index of a texlib surface by name (lib.layers), -1 if absent */
        public static int layer(string name)
        {
            if (index == null)
            {
                index = new Dictionary<string, int>();
                var ta = Resources.Load<TextAsset>("Texlib/texlib");
                var names = ta != null ? JsonUtility.FromJson<Lib>(ta.text).names : null;
                if (names != null) for (int i = 0; i < names.Length; i++) index[names[i]] = i;
            }
            return index.TryGetValue(name, out var k) ? k : -1;
        }

        /** load the library for this tier and bind it globally (_TexlibAlbedo/_TexlibNormal/_TexlibOrm, _TLTex, _TLSlots,
         *  _TexSize, _Gel); false = no library (the level shader falls back to its procedural surface) */
        public static bool bind(QualityDef q, bool touch)
        {
            int want = q.paintAtlas >= 4096 && !touch ? 512 : 256;
            if (ready && size == want) return true;
            if (!load(want) && !load(want == 512 ? 256 : 512))
            {
                Debug.LogWarning("[inkwave] texture library missing (Resources/Texlib) — procedural fallback");
                return false;
            }
            if (slotTex == null)
            {
                var ta = Resources.Load<TextAsset>("Texlib/texlib_slots");
                var s = ta != null ? JsonUtility.FromJson<Slots>(ta.text) : null;
                if (s == null || s.slots <= 0 || s.data.Length != s.slots * 12)
                {
                    Debug.LogWarning("[inkwave] texlib slot table missing or malformed (Resources/Texlib/texlib_slots.json) — procedural fallback");
                    albedo = normal = orm = null;
                    return false;
                }
                // exact texel fetch, no interpolation, no mips (levelMaterial.js tlTex)
                slotTex = new Texture2D(s.slots, 3, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "texlib_slots" };
                slotTex.SetPixelData(s.data, 0);
                slotTex.Apply(false, true);
                Shader.SetGlobalTexture("_TLTex", slotTex);
                Shader.SetGlobalFloat("_TLSlots", s.slots);
                Shader.SetGlobalFloat("_Gel", s.gel);
            }
            Shader.SetGlobalTexture("_TexlibAlbedo", albedo);
            Shader.SetGlobalTexture("_TexlibNormal", normal);
            Shader.SetGlobalTexture("_TexlibOrm", orm);
            Shader.SetGlobalFloat("_TexSize", size);
            return true;
        }

        static bool load(int sz)
        {
            var a = Resources.Load<Texture2DArray>($"Texlib/texlib_albedo_{sz}");
            var n = Resources.Load<Texture2DArray>($"Texlib/texlib_normal_{sz}");
            var o = Resources.Load<Texture2DArray>($"Texlib/texlib_orm_{sz}");
            if (a == null || n == null || o == null) return false;
            albedo = a; normal = n; orm = o; size = sz;
            return true;
        }
    }
}
