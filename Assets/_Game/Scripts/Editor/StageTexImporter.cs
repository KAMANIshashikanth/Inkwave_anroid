using UnityEditor;

namespace Inkwave.EditorTools
{
    /// <summary>
    /// Import settings for the stage textures baked by Tools/bake/pages/stage.js (Resources/Stages/&lt;id&gt;/tex/):
    /// "t&lt;n&gt;.lin.png" holds data (three NoColorSpace: masks, noise) → linear, everything else is sRGB colour.
    /// Full mips, aniso 4; wrap modes are set at load (World/StageMaterials.cs).
    /// </summary>
    public class StageTexImporter : AssetPostprocessor
    {
        void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith("Assets/_Game/Resources/Stages/") || !assetPath.Contains("/tex/")) return;
            var ti = (TextureImporter)assetImporter;
            ti.textureType = TextureImporterType.Default;
            ti.sRGBTexture = !assetPath.EndsWith(".lin.png");
            ti.alphaSource = TextureImporterAlphaSource.FromInput;
            ti.mipmapEnabled = true;
            ti.anisoLevel = 4;
            ti.maxTextureSize = 2048;
            ti.textureCompression = TextureImporterCompression.CompressedHQ;
        }
    }
}
