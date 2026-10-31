using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Inkwave.EditorTools
{
    /// <summary>
    /// The M3 check for the kit weapon models: a kid holding each kit in its key states, rendered to PNGs.
    /// Unity -batchmode -projectPath . -executeMethod Inkwave.EditorTools.KitCapture.Kits -captureOut DIR -quit
    /// </summary>
    public static class KitCapture
    {
        [MenuItem("Inkwave/Captures/Kit weapons")]
        public static void Kits()
        {
            string dir = ArgValue("-captureOut") ?? "Builds/Captures";
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cam = new GameObject("Main Camera") { tag = "MainCamera" }.AddComponent<Camera>();
            cam.gameObject.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            cam.fieldOfView = 30; cam.nearClipPlane = 0.05f; cam.farClipPlane = 200;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.62f, 0.8f, 0.95f);
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional; sun.shadows = LightShadows.Soft; sun.intensity = 1.3f;
            sun.transform.rotation = Quaternion.LookRotation(ThreeSpace.toUnity(-new Vector3(-0.41f, 0.83f, 0.58f).normalized));
            RenderSettings.sun = sun;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.62f, 0.72f, 0.85f);
            RenderSettings.ambientEquatorColor = new Color(0.6f, 0.6f, 0.6f);
            RenderSettings.ambientGroundColor = new Color(0.4f, 0.38f, 0.35f);
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.transform.localScale = new Vector3(4, 1, 4);
            var fm = new Material(Shader.Find("Inkwave/Debug/VertexColorLit")); fm.SetColor("_BaseColor", new Color(0.55f, 0.53f, 0.5f));
            floor.GetComponent<Renderer>().sharedMaterial = fm;
            var orange = Ctx.hex("#ff8a14"); var blue = Ctx.hex("#2f5bff");
            G.teamColors = new[] { orange, blue };
            Inkwave.Kits.init();   // (GameMain does this in the game: the kit models register their part animation)

            // ---- Canopy Brolly: furled, open (aiming), torn + low, and a launched canopy sliding beside it
            var ch = new Character(orange, "brolly", new CharacterStyle.Style { hair = 1, skin = 2, outfit = 3, eyes = 1 }, "brolly", false);
            ch.root.localPosition = Vector3.zero;
            ch.trigger("spawn");
            var k = new BrollyState { hp = 450, hpMax = 450, grow = 1 };
            BrollyModel.previewKit = k;
            var s = new AnimState();
            void run(int frames, bool firing)
            {
                for (int f = 0; f < frames; f++) { s.time += 1 / 60f; s.grounded = true; s.firing = firing; s.aimPitch = 0.05f; ch.update(1 / 60f, s); }
            }
            run(150, false);
            ThreeSpace.cameraLookAt(cam, new Vector3(-2.2f, 1.4f, 2.0f), new Vector3(0, 0.95f, 0.2f), Vector3.up);
            GreyBoxTools.Capture(cam, System.IO.Path.Combine(dir, "_warmup.png"));   // (the first render of a batch session can miss skinned meshes)
            GreyBoxTools.Capture(cam, System.IO.Path.Combine(dir, "brolly_furled.png"));
            k.open = 1; run(60, true);
            ThreeSpace.cameraLookAt(cam, new Vector3(-1.2f, 1.5f, 3.2f), new Vector3(0, 1.0f, 0.5f), Vector3.up);
            GreyBoxTools.Capture(cam, System.IO.Path.Combine(dir, "brolly_open_front.png"));
            ThreeSpace.cameraLookAt(cam, new Vector3(-3.0f, 1.3f, 0.4f), new Vector3(0, 1.0f, 0.4f), Vector3.up);
            GreyBoxTools.Capture(cam, System.IO.Path.Combine(dir, "brolly_open_side.png"));
            k.hp = 100; run(30, true);
            ThreeSpace.cameraLookAt(cam, new Vector3(-1.2f, 1.5f, 3.2f), new Vector3(0, 1.0f, 0.5f), Vector3.up);
            GreyBoxTools.Capture(cam, System.IO.Path.Combine(dir, "brolly_torn.png"));
            k.open = 0.5f; k.opening = true; k.hp = 450; run(1, true);
            GreyBoxTools.Capture(cam, System.IO.Path.Combine(dir, "brolly_half.png"));
            var c = new Brolly.Launched { team = 1, pos = new Vector3(1.6f, 0, 0.6f), dir = new Vector3(0, 0, 1), life = 6, hp = 450, hpMax = 450, h0 = 0.76f, t = 1 };
            c.mesh = BrollyModel.buildWall(c); c.mesh.update(c, 0.76f);
            ThreeSpace.cameraLookAt(cam, new Vector3(0.6f, 1.6f, 4.2f), new Vector3(0.8f, 0.8f, 0.4f), Vector3.up);
            GreyBoxTools.Capture(cam, System.IO.Path.Combine(dir, "brolly_wall.png"));
            c.mesh.dispose();
            BrollyModel.previewKit = null;
            ch.dispose();

            // ---- Tideline Bow: braced, at full draw (on the ground: canted flat), just loosed
            var bw = new Character(blue, "bow", new CharacterStyle.Style { hair = 5, skin = 1, outfit = 7, eyes = 2, brows = 1 }, "bow", false);
            bw.trigger("spawn");
            var bs = new AnimState();
            void runB(int frames, bool firing, float charge)
            {
                for (int f = 0; f < frames; f++) { bs.time += 1 / 60f; bs.grounded = true; bs.firing = firing; bs.charge = charge; bs.aimPitch = 0.05f; bw.update(1 / 60f, bs); }
            }
            runB(120, false, 0);
            ThreeSpace.cameraLookAt(cam, new Vector3(-2.0f, 1.5f, 2.2f), new Vector3(0, 1.0f, 0.2f), Vector3.up);
            GreyBoxTools.Capture(cam, System.IO.Path.Combine(dir, "bow_rest.png"));
            for (int f = 0; f < 70; f++) runB(1, true, Mathf.Min(1, f / 60f));
            GreyBoxTools.Capture(cam, System.IO.Path.Combine(dir, "bow_drawn.png"));
            ThreeSpace.cameraLookAt(cam, new Vector3(1.6f, 1.6f, 2.4f), new Vector3(0, 1.1f, 0.3f), Vector3.up);
            GreyBoxTools.Capture(cam, System.IO.Path.Combine(dir, "bow_drawn_front.png"));
            bw.trigger("charge_release"); runB(4, true, 0);
            ThreeSpace.cameraLookAt(cam, new Vector3(-2.0f, 1.5f, 2.2f), new Vector3(0, 1.0f, 0.2f), Vector3.up);
            GreyBoxTools.Capture(cam, System.IO.Path.Combine(dir, "bow_loosed.png"));
            bw.dispose();

            // ---- Brine Cutlass: carried, mid quick-cut, charging (edge filling), full (aura)
            var bl = new Character(orange, "blade", new CharacterStyle.Style { hair = 7, skin = 3, outfit = 1, eyes = 5, brows = 2 }, "blade", false);
            bl.trigger("spawn");
            var ls = new AnimState();
            void runL(int frames, bool firing, float charge)
            {
                for (int f = 0; f < frames; f++) { ls.time += 1 / 60f; ls.grounded = true; ls.firing = firing; ls.charge = charge; ls.aimPitch = 0.05f; bl.update(1 / 60f, ls); }
            }
            runL(120, false, 0);
            ThreeSpace.cameraLookAt(cam, new Vector3(-1.8f, 1.4f, 2.4f), new Vector3(0, 0.95f, 0.3f), Vector3.up);
            GreyBoxTools.Capture(cam, System.IO.Path.Combine(dir, "blade_rest.png"));
            bl.trigger("blade_slash", 1); runL(5, true, 0);
            GreyBoxTools.Capture(cam, System.IO.Path.Combine(dir, "blade_slash.png"));
            runL(40, true, 0.5f);
            GreyBoxTools.Capture(cam, System.IO.Path.Combine(dir, "blade_charging.png"));
            runL(40, true, 1);
            GreyBoxTools.Capture(cam, System.IO.Path.Combine(dir, "blade_full.png"));
            bl.dispose();

            // ---- Sponge Mitts: gloves up, mid punch
            var mt = new Character(blue, "mitts", new CharacterStyle.Style { hair = 2, skin = 4, outfit = 5, eyes = 3, brows = 0 }, "mitts", false);
            mt.trigger("spawn");
            var ms = new AnimState();
            void runM(int frames, bool firing)
            {
                for (int f = 0; f < frames; f++) { ms.time += 1 / 60f; ms.grounded = true; ms.firing = firing; ms.aimPitch = 0.05f; mt.update(1 / 60f, ms); }
            }
            runM(120, true);
            ThreeSpace.cameraLookAt(cam, new Vector3(-1.4f, 1.4f, 2.2f), new Vector3(0, 1.0f, 0.3f), Vector3.up);
            GreyBoxTools.Capture(cam, System.IO.Path.Combine(dir, "mitts_guard.png"));
            mt.trigger("shoot", 1); runM(3, true);
            GreyBoxTools.Capture(cam, System.IO.Path.Combine(dir, "mitts_punch.png"));
            mt.dispose();
        }

        static string ArgValue(string name)
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
            return null;
        }
    }
}
