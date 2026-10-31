using System.IO;
using System.Linq;
using Inkwave.World;
using UnityEngine;

namespace Inkwave.EditorTools
{
    /// <summary>
    /// The lightmap layout checksum of a stage, as main.js _loadLightmap computes it (must equal assets/lightmaps/&lt;id&gt;.json
    /// "hash"), plus the faces it hashes in Builds/layout-<id>.txt for diffing against the web (Tools/bake/layout-faces.mjs).
    /// Unity -batchmode -quit -projectPath . -executeMethod Inkwave.EditorTools.LayoutCheck.Tidewater
    /// </summary>
    public static class LayoutCheck
    {
        public static void Tidewater() => check("tidewater", Inkwave.World.Stages.Tidewater.build(), 8, 2048);
        public static void TidewaterDressed() { var L = Inkwave.World.Stages.Tidewater.build(); var cols = StageColliders.load("tidewater"); var level = new Level(L, cols); level.layoutLightmap(8, 2048); Debug.Log($"[Inkwave] layout tidewater + {cols.Count} colliders: hash {level.layoutHash}, {level.blocks.Count} blocks, {level.faces.Count} faces"); }

        static void check(string id, LayoutDef layout, float ppm, int size)
        {
            var level = new Level(layout);
            level.layoutLightmap(ppm, size);
            Debug.Log($"[Inkwave] layout {id}: hash {level.layoutHash}, {level.faces.Count} faces");
            File.WriteAllLines($"Builds/blocks-{id}.txt", level.blocks.Select(b => string.Join(" ", new[] { b.center.x, b.center.y, b.center.z, b.half.x, b.half.y, b.half.z }.Select(v => System.Math.Floor((double)v * 100.0 + 0.5).ToString())) + (b.solid ? " S" : " -") + (b.hidden ? "H" : "")));
            foreach (int i in new[] { 40, 49, 58, 67 })
            {
                var b = level.blocks[i];
                Debug.Log($"[Inkwave] block {i}: center {b.center.x:0.###},{b.center.y:0.###},{b.center.z:0.###} half {b.half.x:0.###},{b.half.y:0.###},{b.half.z:0.###} aligned {b.aligned} axes {string.Join(" | ", b.axes.Select(a => $"{a.x:0.###},{a.y:0.###},{a.z:0.###}"))}");
            }
            Directory.CreateDirectory("Builds");
            File.WriteAllLines($"Builds/layout-{id}.txt", level.faces.Select(f =>
                string.Join(" ", new[] { f.origin.x, f.origin.y, f.origin.z, f.su, f.sv }.Select(v => System.Math.Floor((double)v * 100.0 + 0.5).ToString())) + " b" + f.block));
        }
    }
}
