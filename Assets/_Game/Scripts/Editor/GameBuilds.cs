using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Inkwave.EditorTools
{
    /// <summary>
    /// The game scene + player builds, from the menu or batch mode, e.g.
    /// Unity -batchmode -quit -projectPath . -executeMethod Inkwave.EditorTools.GameBuilds.BuildWindowsDevelopment
    /// </summary>
    public static class GameBuilds
    {
        public const string ScenePath = "Assets/_Game/Scenes/Main.unity";
        const string ResDir = "Assets/_Game/Resources/ShaderRefs";

        // Shaders the game only reaches through Shader.Find must ride along in the build: one material per shader in a
        // Resources folder does that (instancing on, so the instanced variants are kept too).
        static readonly string[] Shaders = { "Inkwave/Level", "Hidden/Inkwave/PaintSplat", "Inkwave/Debug/VertexColorLit", "Universal Render Pipeline/Unlit", "Inkwave/Kid/Body", "Inkwave/Kid/Glass", "Inkwave/Fx/InkBlob", "Inkwave/Fx/Ribbon", "Inkwave/Fx/Additive", "Inkwave/Fx/BladeFx", "Inkwave/Fx/BladeAura", "Inkwave/Fx/Tint", "Inkwave/Fx/Curtain", "Inkwave/Fx/Trail", "Inkwave/Fx/SpinBlur", "Inkwave/Fx/SenseRing", "Inkwave/Fx/LockRing", "Inkwave/Fx/Bubble", "Inkwave/Fx/Swirl", "Inkwave/Fx/Beam" };

        [MenuItem("Inkwave/Build Main Scene")]
        public static void BuildScene()
        {
            ProjectSetup.Apply();
            Directory.CreateDirectory(ResDir);
            foreach (var s in Shaders)
            {
                var path = $"{ResDir}/{s.Replace('/', '_').Replace(' ', '_')}.mat";
                if (AssetDatabase.LoadAssetAtPath<Material>(path) == null) AssetDatabase.CreateAsset(new Material(Shader.Find(s)) { enableInstancing = true }, path);
            }
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cam = new GameObject("Main Camera") { tag = "MainCamera" }.AddComponent<Camera>();
            cam.gameObject.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            cam.gameObject.AddComponent<AudioListener>();
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            var gm = new GameObject("Game").AddComponent<GameMain>();
            gm.cam = cam; gm.sun = sun;
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log("[Inkwave] Main scene built at " + ScenePath);
        }

        [MenuItem("Inkwave/Build Windows (development)")]
        public static void BuildWindowsDevelopment()
        {
            BuildScene();
            Build(BuildTarget.StandaloneWindows64, "Builds/Windows/ColorSplash.exe", BuildOptions.Development);
        }

        [MenuItem("Inkwave/Build Android APK (development)")]
        public static void BuildAndroidDevelopment()
        {
            BuildScene();
            Build(BuildTarget.Android, "Builds/Android/ColorSplash.apk", BuildOptions.Development);
        }

        static void Build(BuildTarget target, string location, BuildOptions options)
        {
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = location,
                target = target,
                options = options,
            });
            var s = report.summary;
            Debug.Log($"[Inkwave] {target} build {s.result}: {s.outputPath} ({s.totalSize / (1024f * 1024f):0.0} MB, {s.totalErrors} errors, {s.totalTime})");
            if (s.result != BuildResult.Succeeded && Application.isBatchMode) throw new Exception($"{target} build failed: {s.result}");
        }
    }
}
