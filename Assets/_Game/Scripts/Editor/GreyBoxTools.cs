using System.IO;
using Inkwave.Dev;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Inkwave.EditorTools
{
    /// <summary>
    /// M0 grey box: saves the play-test scene and renders the coordinate spike to a PNG, e.g.
    /// Unity -batchmode -projectPath . -executeMethod Inkwave.EditorTools.GreyBoxTools.BuildAndCapture -captureOut D:\shot.png -quit
    /// </summary>
    public static class GreyBoxTools
    {
        public const string ScenePath = "Assets/_Game/Scenes/GreyBox.unity";

        [MenuItem("Inkwave/Grey Box/Build Scene + Capture")]
        public static void BuildAndCapture()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cam = new GameObject("Main Camera") { tag = "MainCamera" }.AddComponent<Camera>();
            cam.gameObject.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            GreyBox.build(cam);
            Capture(cam, ArgValue("-captureOut") ?? "Builds/Captures/greybox.png");

            // the saved scene rebuilds itself at Start (the editor-built meshes are not assets)
            var root = ThreeSpace.root;
            for (int i = root.childCount - 1; i >= 0; i--) Object.DestroyImmediate(root.GetChild(i).gameObject);
            new GameObject("GreyBox").AddComponent<GreyBox>();
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log("[Inkwave] Grey box scene saved at " + ScenePath);
        }

        public static void Capture(Camera cam, string path, int w = 1280, int h = 720)
        {
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            cam.aspect = (float)w / h;
            cam.targetTexture = rt;
            cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            cam.targetTexture = null;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tex);
            Debug.Log("[Inkwave] Captured " + Path.GetFullPath(path));
        }

        static string ArgValue(string name)
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
            return null;
        }
    }
}
