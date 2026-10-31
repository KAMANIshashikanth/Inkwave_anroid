using System.IO;
using UnityEditor;
using UnityEngine;

namespace Inkwave.EditorTools
{
    /// <summary>
    /// Imports the texlib atlases baked by Tools/bake/pages/texlib.js (Resources/Texlib/texlib_{albedo,normal,orm}_{256,512}.png:
    /// the layers on a cols × rows grid, see texlib.json) as Texture2DArrays — one slice per layer, left to right, top to
    /// bottom: albedo sRGB, normal / orm linear (the normal is three's raw tangent-space encoding, not a Unity normal map),
    /// full mips, Repeat, aniso 4, platform-default compression.
    /// Batch: Unity -batchmode -quit -projectPath . -executeMethod Inkwave.EditorTools.TexlibImporter.Run
    /// </summary>
    public class TexlibImporter : AssetPostprocessor
    {
        const string DIR = "Assets/_Game/Resources/Texlib/";

        [System.Serializable] class Grid { public int cols, rows; }

        void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(DIR) || !assetPath.EndsWith(".png")) return;
            var g = new Grid { cols = 7, rows = 7 };
            var json = DIR + "texlib.json";
            if (File.Exists(json)) JsonUtility.FromJsonOverwrite(File.ReadAllText(json), g);
            var ti = (TextureImporter)assetImporter;
            var s = new TextureImporterSettings();
            ti.ReadTextureSettings(s);
            s.textureType = TextureImporterType.Default;
            s.textureShape = TextureImporterShape.Texture2DArray;
            s.flipbookColumns = g.cols; s.flipbookRows = g.rows;
            s.sRGBTexture = Path.GetFileName(assetPath).StartsWith("texlib_albedo");
            s.alphaSource = TextureImporterAlphaSource.FromInput;
            s.mipmapEnabled = true;
            s.wrapMode = TextureWrapMode.Repeat;
            s.filterMode = FilterMode.Trilinear;
            s.aniso = 4;
            s.readable = false;
            ti.SetTextureSettings(s);
            ti.maxTextureSize = 4096;
            ti.textureCompression = TextureImporterCompression.CompressedHQ;
        }

        [MenuItem("Inkwave/Reimport Texlib")]
        public static void Run()
        {
            foreach (var f in Directory.GetFiles(DIR, "texlib_*.png"))
                AssetDatabase.ImportAsset(f.Replace('\\', '/'), ImportAssetOptions.ForceUpdate);
            AssetDatabase.SaveAssets();
            foreach (var f in Directory.GetFiles(DIR, "texlib_*.png"))
            {
                var a = AssetDatabase.LoadAssetAtPath<Texture2DArray>(f.Replace('\\', '/'));
                Debug.Log(a != null ? $"[Inkwave] texlib {Path.GetFileName(f)}: {a.width}x{a.height} x {a.depth} slices, {a.format}, mips {a.mipmapCount}"
                    : $"[Inkwave] texlib {Path.GetFileName(f)}: NOT a Texture2DArray");
            }
        }
    }
}
