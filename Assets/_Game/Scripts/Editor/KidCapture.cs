using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Inkwave.EditorTools
{
    /// <summary>
    /// The M2 check for the procedural squidkid: a line-up of kids (styles, weapons, idle / aiming / running / squid)
    /// animated for a couple of seconds, rendered to PNGs.
    /// Unity -batchmode -projectPath . -executeMethod Inkwave.EditorTools.KidCapture.Lineup -captureOut DIR -quit
    /// </summary>
    public static class KidCapture
    {
        class Slot { public Character ch; public AnimState s; public Vector3 pos; public float yaw; public bool run, fire; }

        [MenuItem("Inkwave/Captures/Squidkid line-up")]
        public static void Lineup()
        {
            string dir = ArgValue("-captureOut") ?? "Builds/Captures";
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cam = new GameObject("Main Camera") { tag = "MainCamera" }.AddComponent<Camera>();
            cam.gameObject.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            cam.fieldOfView = 40; cam.nearClipPlane = 0.05f; cam.farClipPlane = 200;
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
            // floor
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.transform.localScale = new Vector3(4, 1, 4);
            var fm = new Material(Shader.Find("Inkwave/Debug/VertexColorLit")); fm.SetColor("_BaseColor", new Color(0.55f, 0.53f, 0.5f));
            floor.GetComponent<Renderer>().sharedMaterial = fm;

            var orange = Ctx.hex("#ff8a14"); var blue = Ctx.hex("#2f5bff");
            var looks = new[]
            {
                (w: "shooter", st: new CharacterStyle.Style { hair = 0, skin = 0, outfit = 0, eyes = 0, hat = 0, brows = 0 }, col: orange, run: false, fire: false, form: "kid"),
                (w: "blaster", st: new CharacterStyle.Style { hair = 1, skin = 5, outfit = 9, eyes = 1, hat = 0, brows = 1 }, col: orange, run: false, fire: true, form: "kid"),
                (w: "charger", st: new CharacterStyle.Style { hair = 6, skin = 6, outfit = 6, eyes = 2, hat = 3, brows = 3 }, col: blue, run: false, fire: false, form: "kid"),
                (w: "roller", st: new CharacterStyle.Style { hair = 4, skin = 1, outfit = 8, eyes = 7, hat = 0, brows = 1 }, col: blue, run: true, fire: false, form: "kid"),
                (w: "dualies", st: new CharacterStyle.Style { hair = 3, skin = 2, outfit = 2, eyes = 3, hat = 2, brows = 0 }, col: orange, run: false, fire: false, form: "kid"),
                (w: "shooter", st: new CharacterStyle.Style { hair = 2, skin = 4, outfit = 4, eyes = 4, hat = 0, brows = 2 }, col: blue, run: false, fire: false, form: "squid"),
            };
            var slots = new List<Slot>();
            for (int i = 0; i < looks.Length; i++)
            {
                var L = looks[i];
                var ch = new Character(L.col, L.w, L.st, "kid" + i, false);
                var sl = new Slot { ch = ch, s = new AnimState { form = L.form }, pos = new Vector3((i - (looks.Length - 1) * 0.5f) * 1.1f, 0, 0), yaw = 0, run = L.run, fire = L.fire };
                ch.root.localPosition = sl.pos;
                ch.trigger("spawn");
                slots.Add(sl);
            }
            // two seconds of animation at 60 Hz (the spawn drop plays out, blinks, breathing, the runner jogs in place)
            const float dt = 1 / 60f;
            for (int f = 0; f < 150; f++)
            {
                foreach (var sl in slots)
                {
                    var s = sl.s; s.time += dt;
                    s.grounded = true; s.turnRate = 0;
                    if (sl.run) { s.speed = 4.5f; s.localMove = new Vector2(0, 1); }   // treadmill: speed with a still root
                    s.firing = sl.fire && f > 60; s.aimPitch = 0.1f;
                    if (sl.fire && f > 60 && f % 30 == 0) sl.ch.trigger("shoot");
                    sl.ch.root.localRotation = Quaternion.AngleAxis(sl.yaw * Mathf.Rad2Deg, Vector3.up);
                    sl.ch.update(dt, s);
                }
            }
            // three space: the kids face +Z; the camera stands in front of them
            ThreeSpace.cameraLookAt(cam, new Vector3(0, 1.2f, 6.2f), new Vector3(0, 0.75f, 0), Vector3.up);
            GreyBoxTools.Capture(cam, System.IO.Path.Combine(dir, "kids_lineup.png"));
            cam.fieldOfView = 22;
            ThreeSpace.cameraLookAt(cam, slots[0].pos + new Vector3(0.25f, 1.25f, 1.5f), slots[0].pos + new Vector3(0, 1.05f, 0), Vector3.up);
            GreyBoxTools.Capture(cam, System.IO.Path.Combine(dir, "kid_face.png"));
            ThreeSpace.cameraLookAt(cam, slots[1].pos + new Vector3(-1.3f, 1.2f, 1.6f), slots[1].pos + new Vector3(0, 0.8f, 0), Vector3.up);
            GreyBoxTools.Capture(cam, System.IO.Path.Combine(dir, "kid_aim.png"));
            ThreeSpace.cameraLookAt(cam, slots[2].pos + new Vector3(0.6f, 1.4f, -1.9f), slots[2].pos + new Vector3(0, 0.8f, 0), Vector3.up);
            GreyBoxTools.Capture(cam, System.IO.Path.Combine(dir, "kid_back.png"));
            ThreeSpace.cameraLookAt(cam, slots[5].pos + new Vector3(0.4f, 0.7f, 1.3f), slots[5].pos + new Vector3(0, 0.2f, 0), Vector3.up);
            GreyBoxTools.Capture(cam, System.IO.Path.Combine(dir, "kid_squid.png"));
            // the wide shot again, last (the first render of a batch session can miss the skinned meshes)
            cam.fieldOfView = 40;
            ThreeSpace.cameraLookAt(cam, new Vector3(0, 1.2f, 6.2f), new Vector3(0, 0.75f, 0), Vector3.up);
            GreyBoxTools.Capture(cam, System.IO.Path.Combine(dir, "kids_lineup2.png"));
            foreach (var sl in slots) sl.ch.dispose();
        }

        static string ArgValue(string name)
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
            return null;
        }
    }
}
