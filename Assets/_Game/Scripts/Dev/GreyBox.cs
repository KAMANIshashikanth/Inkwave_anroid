// M0 grey box: proves the three-space bridge (mirrored root, three.js winding, camera conversion) and the input /
// touch layer before the real game modules land. A capsule walks round a few boxes; the test cube's faces are
// coloured by their three.js axis (+X red, +Y green, +Z blue; the opposite faces dark), so a screenshot shows at a
// glance whether handedness and culling came through: from the default camera (three-space +X +Z, looking at the
// origin) the blue +Z face must face the camera on the LEFT, the red +X face sit on the RIGHT, green on top.
using UnityEngine;
using static Inkwave.Ctx;

namespace Inkwave.Dev
{
    public class GreyBox : MonoBehaviour
    {
        public static readonly Vector3 CAM_POS = new Vector3(6, 5, 9), CAM_TARGET = new Vector3(0, 1, 0);

        Camera cam;
        Transform player;
        TouchControls touchUi;
        Vector3 playerPos = new Vector3(0, 0, 5);
        float camYaw = Mathf.Atan2(CAM_POS.x, CAM_POS.z), camPitch = -0.35f;

        void Start()
        {
            G.settings = Settings.load();
            G.input = new InputState();
            cam = build(Camera.main);
            player = ThreeSpace.root.Find("player");
            touchUi = new TouchControls(G.input);
            touchUi.mount();
            G.input.setTouchMode(TouchControls.shouldUseTouch(G.settings));
            G.mode = "match";
        }

        // Builds the grey box under the world root and places camera + sun. Safe in edit mode (the editor capture uses it).
        public static Camera build(Camera cam)
        {
            var root = ThreeSpace.root;
            for (int i = root.childCount - 1; i >= 0; i--) DestroyImmediate(root.GetChild(i).gameObject);
            var mat = new Material(Shader.Find("Inkwave/Debug/VertexColorLit"));

            add("ground", Geo.box(30, 1, 30).translate(0, -0.5f, 0).ensureColor(new Color(0.78f, 0.74f, 0.68f)), mat);
            var cube = Geo.box(2, 2, 2);
            // face order of BoxGeometry: px, nx, py, ny, pz, nz — 4 vertices each
            Color[] faces = { new Color(0.9f, 0.1f, 0.1f), new Color(0.35f, 0.05f, 0.05f), new Color(0.1f, 0.8f, 0.2f), new Color(0.3f, 0.3f, 0.3f), new Color(0.15f, 0.3f, 1f), new Color(0.05f, 0.08f, 0.35f) };
            cube.color = new System.Collections.Generic.List<Color>();
            for (int f = 0; f < 6; f++) for (int k = 0; k < 4; k++) cube.color.Add(faces[f]);
            add("axis-cube", cube.translate(0, 1, 0), mat);
            add("pillar", Geo.box(1, 3, 1).translate(-4, 1.5f, -4).ensureColor(new Color(0.85f, 0.82f, 0.75f)), mat);
            add("ramp", Geo.box(2, 0.3f, 5).rotateX(-0.35f).translate(5, 0.6f, -3).ensureColor(new Color(0.7f, 0.75f, 0.8f)), mat);
            var p = add("player", Geo.box(0.76f, 1.45f, 0.76f).translate(0, 0.725f, 0).ensureColor(new Color(1f, 0.54f, 0.08f)), mat);
            p.transform.localPosition = new Vector3(0, 0, 5);

            if (cam == null) cam = new GameObject("Main Camera") { tag = "MainCamera" }.AddComponent<Camera>();
            cam.fieldOfView = 60;
            cam.nearClipPlane = 0.1f; cam.farClipPlane = 400;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.55f, 0.75f, 0.95f);
            ThreeSpace.cameraLookAt(cam, CAM_POS, CAM_TARGET, Vector3.up);

            var sun = FindAnyObjectByType<Light>();
            if (sun == null) sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.shadows = LightShadows.Soft;
            sun.intensity = 1.2f;
            // three-space sun at (12, 20, 8) shining at the origin
            sun.transform.rotation = Quaternion.LookRotation(ThreeSpace.toUnity(-new Vector3(12, 20, 8).normalized));
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.45f, 0.48f, 0.55f);
            return cam;
        }

        static GameObject add(string name, Geo g, Material mat)
        {
            var go = ThreeSpace.create(name);
            go.AddComponent<MeshFilter>().sharedMesh = g.toMesh(name);
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        void Update()
        {
            var inp = G.input;
            inp.poll();
            inp.pollPad();
            inp.pollTouch();
            touchUi.update(true);
            float dt = Mathf.Min(Time.deltaTime, 0.05f);

            // look: touch drags (points) or locked-mouse counts
            float sens = inp.touch.active ? TOUCH.lookSens * G.settings.touchSensitivity : 0.0021f * G.settings.sensitivity;
            camYaw -= inp.mouse.dx * sens;
            camPitch = clamp(camPitch - inp.mouse.dy * sens, -1.2f, 0.6f);
            if (!inp.touch.active && inp.mouse.leftPressed == false && UnityEngine.InputSystem.Mouse.current?.leftButton.wasPressedThisFrame == true) inp.requestLock();
            if (inp.wasPressed("Escape")) inp.exitLock();

            // move: stick / WASD in camera-relative three space (+z forward = away from the camera)
            float mx = inp.touch.mx, mz = inp.touch.mz;
            if (inp.down("KeyW") || inp.down("ArrowUp")) mz += 1;
            if (inp.down("KeyS") || inp.down("ArrowDown")) mz -= 1;
            if (inp.down("KeyD") || inp.down("ArrowRight")) mx += 1;
            if (inp.down("KeyA") || inp.down("ArrowLeft")) mx -= 1;
            var ps = inp.padStick(0, 1, new InputState.StickOut());
            mx += ps.x; mz -= ps.y;
            var fwd = new Vector3(-Mathf.Sin(camYaw), 0, -Mathf.Cos(camYaw));   // the camera looks toward -(sin yaw, cos yaw)
            var right = new Vector3(-fwd.z, 0, fwd.x);
            var mv = fwd * mz + right * mx;
            if (mv.sqrMagnitude > 1) mv.Normalize();
            playerPos += mv * PLAYER.runSpeed * dt;
            player.localPosition = playerPos;
            if (mv.sqrMagnitude > 0.01f) player.localRotation = Quaternion.AngleAxis(Mathf.Atan2(mv.x, mv.z) * Mathf.Rad2Deg, Vector3.up);

            // third-person camera behind the capsule
            var dir = new Vector3(Mathf.Sin(camYaw) * Mathf.Cos(camPitch), -Mathf.Sin(camPitch), Mathf.Cos(camYaw) * Mathf.Cos(camPitch));
            var eye = playerPos + Vector3.up * 1.6f + dir * 5.5f;
            ThreeSpace.cameraLookAt(cam, eye, playerPos + Vector3.up * 1.2f, Vector3.up);

            inp.endFrame();
        }
    }
}
