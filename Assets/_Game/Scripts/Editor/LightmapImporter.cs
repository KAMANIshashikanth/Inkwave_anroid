using UnityEditor;

namespace Inkwave.EditorTools
{
    /// <summary>
    /// Import settings for the baked AO lightmaps (Resources/Lightmaps/*.png, copied from reference/inkwave/assets/lightmaps):
    /// one linear channel (R8, uncompressed: no block artefacts in the soft gradients), full mips, clamped, aniso 4 —
    /// as main.js loads them (NoColorSpace, LinearMipmapLinear, anisotropy 4).
    /// </summary>
    public class LightmapImporter : AssetPostprocessor
    {
        void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith("Assets/_Game/Resources/Lightmaps/") || !assetPath.EndsWith(".png")) return;
            var ti = (TextureImporter)assetImporter;
            ti.textureType = TextureImporterType.SingleChannel;
            var s = new TextureImporterSettings();
            ti.ReadTextureSettings(s);
            s.singleChannelComponent = TextureImporterSingleChannelComponent.Red;
            s.sRGBTexture = false;
            s.mipmapEnabled = true;
            s.wrapMode = UnityEngine.TextureWrapMode.Clamp;
            s.filterMode = UnityEngine.FilterMode.Trilinear;
            s.aniso = 4;
            ti.SetTextureSettings(s);
            ti.maxTextureSize = 4096;
            ti.textureCompression = TextureImporterCompression.Uncompressed;
        }
    }
}
