// M1 stand-in for the procedural squidkid (src/game/character.js, ported in M2): a team-coloured kid capsule with a
// head, a gun stub and an ink tank, morphing into a flat squid blob for squid / swim / climb forms. It honours the same
// contract (ICharacter) so actors, weapons and the camera code never know the difference.
using System;
using UnityEngine;

namespace Inkwave
{
    public class CapsuleCharacter : ICharacter
    {
        static Mesh _body, _head, _gun, _squid;
        static Material _baseMat;

        readonly GameObject go;
        readonly Transform kid, squid, gun, upper;
        readonly Material bodyMat, inkMat;
        readonly bool isLocal;
        float squash, form;   // form: 0 kid … 1 squid
        float recoil, hitT;
        Vector2 hitDir;
        bool visible = true;
        Color color;

        public Transform root => go.transform;
        public Action<string, Ev> onEvent { get; set; }

        public CapsuleCharacter(Color color, string name, bool isLocal)
        {
            this.isLocal = isLocal;
            build();
            go = ThreeSpace.create("kid:" + name);
            bodyMat = new Material(_baseMat);
            inkMat = new Material(_baseMat);
            kid = ThreeSpace.create("kid", go.transform).transform;
            upper = ThreeSpace.create("upper", kid).transform;
            upper.localPosition = new Vector3(0, 0.75f, 0);
            part(kid, "body", _body, bodyMat, Vector3.zero);
            part(upper, "head", _head, inkMat, new Vector3(0, 0.52f, 0));
            gun = part(upper, "gun", _gun, bodyMat, new Vector3(-0.22f, 0.1f, 0.32f)).transform;
            part(upper, "tank", _head, inkMat, new Vector3(0, 0.12f, -0.26f)).transform.localScale = new Vector3(0.6f, 0.9f, 0.5f);
            squid = ThreeSpace.create("squid", go.transform).transform;
            part(squid, "mantle", _squid, inkMat, Vector3.zero);
            setColor(color);
        }

        static void build()
        {
            if (_baseMat != null) return;
            _baseMat = new Material(Shader.Find("Inkwave/Debug/VertexColorLit"));
            _body = Geo.box(0.62f, 0.95f, 0.46f).translate(0, 0.475f, 0).ensureColor(Color.white).toMesh("kidBody");
            _head = Geo.box(0.42f, 0.4f, 0.42f).ensureColor(Color.white).toMesh("kidHead");
            _gun = Geo.box(0.12f, 0.16f, 0.5f).ensureColor(new Color(0.25f, 0.25f, 0.3f)).toMesh("kidGun");
            _squid = Geo.box(0.5f, 0.28f, 0.75f).translate(0, 0.14f, 0).ensureColor(Color.white).toMesh("squidBody");
        }

        static GameObject part(Transform parent, string name, Mesh mesh, Material mat, Vector3 pos)
        {
            var g = ThreeSpace.create(name, parent);
            g.transform.localPosition = pos;
            g.AddComponent<MeshFilter>().sharedMesh = mesh;
            g.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return g;
        }

        public void setColor(Color c)
        {
            color = c;
            inkMat.SetColor("_BaseColor", c);
            bodyMat.SetColor("_BaseColor", Color.Lerp(new Color(0.94f, 0.92f, 0.88f), c, 0.25f));
        }

        public void setWeapon(string id) { }
        public bool hideWeapon { get; set; }
        public void setSub(string kind) { }

        public void update(float dt, AnimState s)
        {
            float want = s.form == "kid" ? 0 : 1;
            form = Mathf.MoveTowards(form, want, dt / 0.12f);
            kid.gameObject.SetActive(visible && form < 0.5f);
            squid.gameObject.SetActive(visible && form >= 0.5f && !(s.form == "swim" && !isLocal));
            squash = Mathf.MoveTowards(squash, 0, dt * 4);
            float sq = 1 - squash * 0.25f;
            kid.localScale = new Vector3(1 + squash * 0.15f, sq, 1 + squash * 0.15f);
            squid.localPosition = s.form == "swim" ? new Vector3(0, -0.18f, 0) : Vector3.zero;
            if (s.form == "climb")
            {
                // belly on the wall, head up (wallNormal in three space, facing is already into the wall)
                squid.localRotation = Quaternion.AngleAxis(-90, Vector3.right);
                squid.localPosition = new Vector3(0, 0.35f, -0.1f);
            }
            else squid.localRotation = Quaternion.identity;
            // aim pitch on the upper body, recoil + flinch
            recoil = Mathf.MoveTowards(recoil, 0, dt * 6);
            hitT = Mathf.MoveTowards(hitT, 0, dt * 5);
            upper.localRotation = Quaternion.AngleAxis(-s.aimPitch * Mathf.Rad2Deg * 0.6f + hitT * 12 * hitDir.y, Vector3.right)
                                * Quaternion.AngleAxis(hitT * 12 * hitDir.x, Vector3.forward);
            gun.localPosition = new Vector3(-0.22f, 0.1f, 0.32f - recoil * 0.06f);
            gun.localRotation = Quaternion.AngleAxis(-s.aimPitch * Mathf.Rad2Deg * 0.4f, Vector3.right);
            // spawn protection flicker
            bool flick = s.invuln && Mathf.Repeat(s.time * 10, 1) < 0.35f;
            kid.gameObject.SetActive(kid.gameObject.activeSelf && !flick);
        }

        public void trigger(string name, float arg = 1, Vector2? dir = null)
        {
            switch (name)
            {
                case "shoot": recoil = 1; break;
                case "land": squash = Mathf.Clamp01(arg / 14); break;
                case "jump": squash = -0.3f; break;
                case "hit": hitT = 1; hitDir = dir ?? new Vector2(0, 1); break;
                case "spawn": squash = 0.6f; break;
            }
        }

        public void setHurt(float amount, Color enemyColor) { }

        public void setVisible(bool on)
        {
            visible = on;
            kid.gameObject.SetActive(on);
            squid.gameObject.SetActive(false);
        }

        public Vector3 getMuzzle()
        {
            // the gun's tip, back in three space (the hierarchy lives under the mirrored root)
            return ThreeSpace.toThree(gun.TransformPoint(new Vector3(0, 0, 0.25f)));
        }

        public Vector3 getMuzzleHand(int hand = 0) => getMuzzle();
        public float aimReady() => 1;
        public bool getAimMuzzle(float pitch, out Vector3 o) { o = default; return false; }
        public bool dual => false;

        public void dispose()
        {
            Ctx.destroy(go);
            Ctx.destroy(bodyMat);
            Ctx.destroy(inkMat);
        }
    }
}
