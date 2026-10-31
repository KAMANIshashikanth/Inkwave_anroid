using Inkwave.World;
using Inkwave.World.Stages;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Inkwave.EditorTools
{
    /// <summary>
    /// Renders a stage with some test ink (both teams, floors + walls) to PNGs — the M1 check that the level port, the
    /// paint atlas and the ink shader agree:
    /// Unity -batchmode -projectPath . -executeMethod Inkwave.EditorTools.StageCapture.Tidewater -captureOut DIR -quit
    /// </summary>
    public static class StageCapture
    {
        [MenuItem("Inkwave/Captures/Tidewater")]
        public static void Tidewater()
        {
            string dir = ArgValue("-captureOut") ?? "Builds/Captures";
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cam = new GameObject("Main Camera") { tag = "MainCamera" }.AddComponent<Camera>();
            cam.gameObject.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            cam.fieldOfView = 55; cam.nearClipPlane = 0.1f; cam.farClipPlane = 500;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.62f, 0.8f, 0.95f);
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional; sun.shadows = LightShadows.Soft; sun.intensity = 1.3f;
            sun.transform.rotation = Quaternion.LookRotation(ThreeSpace.toUnity(-new Vector3(-0.41f, 0.83f, -0.38f).normalized));
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.62f, 0.72f, 0.85f);
            RenderSettings.ambientEquatorColor = new Color(0.6f, 0.6f, 0.6f);
            RenderSettings.ambientGroundColor = new Color(0.4f, 0.38f, 0.35f);

            var t0 = System.Diagnostics.Stopwatch.StartNew();
            var layout = Inkwave.World.Stages.Tidewater.build();
            var level = new Level(layout, StageColliders.load(layout.id));
            long tLevel = t0.ElapsedMilliseconds;
            var paint = new PaintSystem(level, 2048);
            long tPaint = t0.ElapsedMilliseconds;
            // as the game on the desktop high tier: baked AO + the 512 texture library (-captureTexlib off = fallback)
            bool texlib = ArgValue("-captureTexlib") != "off" && Texlib.bind(Config.QUALITY["high"], false);
            var view = new LevelView(level, paint, true, layout.id, texlib);
            Debug.Log($"[Inkwave] capture: layout {level.layoutHash}, AO {(view.lightmap != null ? "on" : "off")}, texlib {(texlib ? Texlib.size.ToString() : "off")}");
            LevelView.setTeamColors(Ctx.hex("#ff8a14"), Ctx.hex("#2f5bff"));
            Debug.Log($"[Inkwave] Tidewater: {level.blocks.Count} blocks, {level.faces.Count} faces, {paint.paintFaces.Count} paintable, " +
                      $"{level.renderTris} tris, ppm {paint.ppm:0.0}, turf cells {paint.turfTotal} ({paint.turfArea:0} m²); level {tLevel} ms, paint {tPaint - tLevel} ms");

            // test ink: alpha's loggia + square, bravo's mirror, a wall splat on the clock tower, a seam in the middle
            var rnd = new System.Random(7);
            for (int k = 0; k < 40; k++)
            {
                float x = (float)(rnd.NextDouble() * 24 - 12), z = (float)(rnd.NextDouble() * 22 - 34);
                paint.splat(new Vector3(x, 0, z), 0.6f + (float)rnd.NextDouble() * 1.6f, 0);
                paint.splat(new Vector3(-x, 0, -z), 0.6f + (float)rnd.NextDouble() * 1.6f, 1);
            }
            for (int k = 0; k < 10; k++) paint.splat(new Vector3(-1 + k * 0.25f, 0, -6), 2.2f, k % 2);
            paint.splat(new Vector3(0, 5, -1.9f), 1.4f, 0);
            paint.splat(new Vector3(1.9f, 6.5f, 0), 1.4f, 1);
            paint.splat(new Vector3(0, 2.4f, -41.8f), 2.5f, 0, new SplatOpts { kind = "bomb" });
            for (int f = 0; f < 120; f++) paint.flush(1 / 60f);
            view.update(paint);
            var cov = paint.coverage();
            Debug.Log($"[Inkwave] coverage alpha {cov.x * 100:0.0}% bravo {cov.y * 100:0.0}%");

            // the stage-select hero shot, the match intro and a low gameplay view over the alpha loggia
            var L = level.layout;
            ThreeSpace.cameraLookAt(cam, L.artFrom, L.artLook, Vector3.up);
            cam.fieldOfView = L.artFov * 0.8f;
            GreyBoxTools.Capture(cam, System.IO.Path.Combine(dir, "tidewater_art.png"));
            cam.fieldOfView = 60;
            ThreeSpace.cameraLookAt(cam, L.intro.from, L.intro.lookFrom, Vector3.up);
            GreyBoxTools.Capture(cam, System.IO.Path.Combine(dir, "tidewater_intro.png"));
            ThreeSpace.cameraLookAt(cam, new Vector3(4, 5.2f, -47), new Vector3(0, 0, -22), Vector3.up);
            GreyBoxTools.Capture(cam, System.IO.Path.Combine(dir, "tidewater_spawn.png"));
            ThreeSpace.cameraLookAt(cam, new Vector3(3, 1.6f, -9), new Vector3(0, 0.2f, -3), Vector3.up);
            GreyBoxTools.Capture(cam, System.IO.Path.Combine(dir, "tidewater_ink_close.png"));
            // surface close-ups: the terrace steps (stair frame) and the square's paving
            ThreeSpace.cameraLookAt(cam, new Vector3(-3, 2.0f, -16.5f), new Vector3(0, 0.6f, -10), Vector3.up);
            GreyBoxTools.Capture(cam, System.IO.Path.Combine(dir, "tidewater_steps.png"));
            ThreeSpace.cameraLookAt(cam, new Vector3(-12, 1.8f, -20), new Vector3(-6, 0, -15), Vector3.up);
            GreyBoxTools.Capture(cam, System.IO.Path.Combine(dir, "tidewater_paving.png"));
            paint.dispose();
        }

        static string ArgValue(string name)
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
            return null;
        }
    }
}
