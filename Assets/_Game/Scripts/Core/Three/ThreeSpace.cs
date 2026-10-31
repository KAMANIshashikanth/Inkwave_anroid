// The bridge between INKWAVE's three.js coordinates and Unity's.
//
// three.js is right-handed (Y up, +Z towards the viewer); Unity is left-handed (Y up, +Z into the screen). Rotation
// matrices and quaternions are the same numbers in both — only the handedness of the space they are drawn in differs.
// So every world object lives under ONE root mirrored on Z (localScale 1,1,-1): a three.js position / quaternion is
// written to a child's localPosition / localRotation unchanged and Unity draws exactly the picture three.js draws.
// Unity flips the triangle culling of anything under a negative-determinant transform, so meshes keep three.js'
// counter-clockwise front faces too (Core/Three/Geo.cs emits them in three's order).
//
// The camera is the one thing that does not sit under the root (a camera ignores scale): its three-space pose is
// converted here. Euler angles are the other trap: three's default order is 'XYZ' (q = qx·qy·qz) while
// Quaternion.Euler is ZXY — use eulerXYZ().
using UnityEngine;

namespace Inkwave
{
    public static class ThreeSpace
    {
        static Transform _root;

        // The mirrored world root. Everything gameplay creates is parented here.
        public static Transform root
        {
            get
            {
                if (_root == null)
                {
                    var go = GameObject.Find("ThreeWorld") ?? new GameObject("ThreeWorld");
                    go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                    go.transform.localScale = new Vector3(1, 1, -1);
                    _root = go.transform;
                }
                return _root;
            }
        }

        // three-space point/direction ⇄ Unity world (the mirror is its own inverse)
        public static Vector3 toUnity(Vector3 p) => new Vector3(p.x, p.y, -p.z);
        public static Vector3 toThree(Vector3 p) => new Vector3(p.x, p.y, -p.z);
        // a three-space rotation expressed as a Unity world rotation (conjugation by the Z mirror)
        public static Quaternion toUnity(Quaternion q) => new Quaternion(-q.x, -q.y, q.z, q.w);
        public static Quaternion toThree(Quaternion q) => new Quaternion(-q.x, -q.y, q.z, q.w);

        // new GameObject parented under the world root (optionally under another three-space parent)
        public static GameObject create(string name, Transform parent = null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent != null ? parent : root, false);
            return go;
        }

        // three.js Euler (order 'XYZ', radians) → quaternion
        public static Quaternion eulerXYZ(float x, float y, float z)
        {
            return Quaternion.AngleAxis(x * Mathf.Rad2Deg, Vector3.right)
                 * Quaternion.AngleAxis(y * Mathf.Rad2Deg, Vector3.up)
                 * Quaternion.AngleAxis(z * Mathf.Rad2Deg, Vector3.forward);
        }

        // three.js Euler with order 'YXZ' (cameras / aim: yaw then pitch then roll)
        public static Quaternion eulerYXZ(float x, float y, float z)
        {
            return Quaternion.AngleAxis(y * Mathf.Rad2Deg, Vector3.up)
                 * Quaternion.AngleAxis(x * Mathf.Rad2Deg, Vector3.right)
                 * Quaternion.AngleAxis(z * Mathf.Rad2Deg, Vector3.forward);
        }

        // three's Matrix4.lookAt rotation. Cameras (and lights) point -Z at the target; other objects point +Z.
        public static Quaternion lookAtRotation(Vector3 eye, Vector3 target, Vector3 up, bool isCamera)
        {
            Vector3 z = isCamera ? eye - target : target - eye;
            if (z.sqrMagnitude < 1e-12f) z = Vector3.forward;
            z.Normalize();
            Vector3 x = Vector3.Cross(up, z);
            if (x.sqrMagnitude < 1e-12f)
            {
                // up and z are parallel: nudge z like three does
                if (Mathf.Abs(up.z) == 1) z.x += 0.0001f; else z.z += 0.0001f;
                z.Normalize();
                x = Vector3.Cross(up, z);
            }
            x.Normalize();
            Vector3 y = Vector3.Cross(z, x);
            var m = Matrix4x4.identity;
            m.SetColumn(0, x);
            m.SetColumn(1, y);
            m.SetColumn(2, z);
            return m.rotation;
        }

        // Place a Unity camera from a three.js camera pose (three cameras look down their local -Z).
        public static void placeCamera(Camera cam, Vector3 threePos, Quaternion threeQuat)
        {
            Vector3 fwd = threeQuat * Vector3.back;   // (0,0,-1)
            Vector3 up = threeQuat * Vector3.up;
            cam.transform.SetPositionAndRotation(toUnity(threePos), Quaternion.LookRotation(toUnity(fwd), toUnity(up)));
        }

        public static void cameraLookAt(Camera cam, Vector3 threePos, Vector3 threeTarget, Vector3 up)
        {
            placeCamera(cam, threePos, lookAtRotation(threePos, threeTarget, up, true));
        }

        // three-space ray through a screen point (Unity screen pixels, origin bottom-left)
        public static Ray screenRay(Camera cam, Vector2 screenPx)
        {
            Ray r = cam.ScreenPointToRay(screenPx);
            return new Ray(toThree(r.origin), toThree(r.direction));
        }
    }
}
