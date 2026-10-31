// Port of src/game/cameraRig.js — third-person follow, death spectate, cinematic paths, orbit, judge overview and the
// map diorama.
//
// Follow: the pivot rides critically-damped springs toward the character's *visual* position with velocity
// feed-forward (smooth starts/stops/landings, no steady lag), a little strafe look-ahead, a soft vertical for jumps
// and a landing dip. The boom length comes from a soft cylinder probe (physics.cameraProbe): obstacles sweeping
// toward the line of sight start pulling the lens in several frames early, the boom springs in fast / eases out slow,
// never sits more than a hand's width past the hard line of sight and never goes under a floor. Orientation is
// exactly the aim (yaw/pitch) plus spring-driven visual recoil and smooth trauma shake — the aim ray is taken from
// the rendered camera, so the crosshair is always the truth. Mode changes blend the pose (no cuts, no pops).
//
// The rig works on a three-space camera (ThreeCam); apply() hands the pose to the Unity camera.
using System;
using Inkwave.World;
using UnityEngine;
using static Inkwave.Ctx;

namespace Inkwave
{
    public static partial class G
    {
        public static CameraRig rig;
    }

    // three.PerspectiveCamera stand-in: pose in three space (looks down its local -Z), vertical fov in degrees
    public class ThreeCam
    {
        public Vector3 position;
        public Quaternion quaternion = Quaternion.identity;
        public float fov = 60, aspect = 16f / 9, near = 0.15f, far = 6500;
        public Vector3 up = Vector3.up;
        public void lookAt(Vector3 target) => quaternion = ThreeSpace.lookAtRotation(position, target, up, true);
        public void rotateX(float a) => quaternion *= Quaternion.AngleAxis(a * Mathf.Rad2Deg, Vector3.right);
        public void rotateY(float a) => quaternion *= Quaternion.AngleAxis(a * Mathf.Rad2Deg, Vector3.up);
        public void rotateZ(float a) => quaternion *= Quaternion.AngleAxis(a * Mathf.Rad2Deg, Vector3.forward);
        public Vector3 getWorldDirection() => quaternion * Vector3.back;   // (0,0,-1)
        public void copyFrom(ThreeCam c) { position = c.position; quaternion = c.quaternion; fov = c.fov; aspect = c.aspect; near = c.near; far = c.far; }
        public void apply(Camera cam)
        {
            ThreeSpace.placeCamera(cam, position, quaternion);
            cam.fieldOfView = fov; cam.nearClipPlane = near; cam.farClipPlane = far;
        }
    }

    // Critically-damped spring (exact integration — stable and frame-rate independent).
    public class Spring
    {
        public float x, v;
        public Spring(float x = 0) { this.x = x; }
        public void reset(float x) { this.x = x; v = 0; }
        public float step(float target, float omega, float dt)
        {
            float d = x - target, e = Mathf.Exp(-omega * dt), k = (v + omega * d) * dt;
            x = target + (d + k) * e;
            v = (v - omega * k) * e;
            return x;
        }
    }

    public class CameraRig
    {
        // map diorama framing: a steep telephoto look down the stage from behind your base (your base at the bottom)
        const float DIO_PITCH = 1.1f, DIO_FOV = 30;

        class Damped { public float x, v; }
        // Damped spring for zeta < 1, sub-stepped so each explicit step stays tiny: stable at any dt.
        static float stepDamped(Damped o, float target, float omega, float zeta, float dt)
        {
            int n = Mathf.Max(1, Mathf.CeilToInt((omega * dt) / 0.12f)); float h = dt / n;
            for (int i = 0; i < n; i++) { o.v += (-omega * omega * (o.x - target) - 2 * zeta * omega * o.v) * h; o.x += o.v * h; }
            return o.x;
        }
        // smooth pseudo-noise in [-1, 1]: two low incommensurate sines, no high partials (they alias into flicker)
        static float nz(float t, float p) => Mathf.Sin(t + p) * 0.62f + Mathf.Sin(t * 1.87f + p * 1.7f) * 0.38f;
        static float easeInOut(float k) => k < 0.5f ? 4 * k * k * k : 1 - Mathf.Pow(-2 * k + 2, 3) / 2;

        public readonly ThreeCam camera = new ThreeCam();
        public readonly ThreeCam gameCam = new ThreeCam();   // the gameplay view (aim, audio, hit tests) under the diorama
        public string mode = "orbit";
        public float yaw, pitch = -0.1f;
        public Vector3 pivot;
        public float pivotY, dist = 4.5f, curDist = 4.5f, fovKick, trauma, shakeScale = 1;
        public Actor target;
        public class SpectateInfo { public Actor actor; public Vector3 pos, from; }
        public SpectateInfo spectate;
        class PathInfo { public Vector3 from, to, lookFrom, lookTo; public float t, dur; public Action onDone; }
        PathInfo path;
        class OrbitInfo { public Vector3 center; public float radius, height, speed, phase; }
        OrbitInfo orbitP;
        public Vector3 lookAtP;
        public float baseFov = 70, zoom, time, kick;
        readonly Spring sx = new Spring(), sy = new Spring(), sz = new Spring(), boom = new Spring(4.5f), hgt = new Spring(1.85f);
        readonly Damped dipS = new Damped();
        readonly Spring side = new Spring(0), lensLift = new Spring(0), kickS = new Spring(0);
        float wantDist = 4.5f, shoulder;
        float? _lastTY, _takeoffY;
        float _vyF, _landSeen = 99;
        bool _sjWasFlight; float _sjLandT = 99;
        float _traumaIn;
        readonly float shakeSeed = random() * 100;
        class Blend { public float t, dur, fov; public Vector3 pos; public Quaternion quat; public bool active; }
        Blend blend;
        string _prevMode;
        Actor _prevTarget;
        float spectateT;
        public bool mapOpen; public float mapK;
        public Vector2 dioLook;   // -1…1: the map cursor, tilts the diorama a touch toward where you point
        public bool dioFlip;
        class Dio { public LayoutDef layout; public float aspect, d = 150, zShift, yaw, pitch; public Vector3 pos, target; public Quaternion quat = Quaternion.identity; }
        readonly Dio _dio = new Dio();
        Spring[] _ov; bool _ovFresh;
        readonly CameraProbeResult _probe = new CameraProbeResult();
        readonly Hit _hit = new Hit();
        Vector3 _dT;

        public CameraRig() { _prevMode = mode; }

        public void setMap(bool open) => mapOpen = open;

        // Visual recoil: a pitch impulse on a critically-damped spring — only the heavy single shots send it.
        public void recoil(float amount) => kickS.v = Mathf.Min(kickS.v + amount * 55, 3);

        // Trauma shake. The noise runs on continuous time and new trauma is fed in over ~2-3 frames.
        public void addShake(float amount, Vector3? pos = null)
        {
            float a = amount;
            if (pos.HasValue) { float d = Vector3.Distance(pos.Value, camera.position); a *= clamp(1 - (d - 4) / 22, 0, 1); }
            if (a <= 0) return;
            _traumaIn = Mathf.Min(1 - trauma, _traumaIn + a * 0.75f);
        }

        public void follow(Actor actor, bool snap = false)
        {
            mode = "follow";
            target = actor;
            if (snap)
            {
                var p = actor.visualPos();
                float h = actor.form == "squid" ? 1.3f : 1.85f;
                sx.reset(p.x); sy.reset(p.y + h); sz.reset(p.z);
                pivot = new Vector3(p.x, p.y + h, p.z); pivotY = p.y + h;
                hgt.reset(h);
                boom.reset(dist); curDist = dist; wantDist = dist;
                dipS.x = 0; dipS.v = 0; side.reset(0); lensLift.reset(0);
                kickS.reset(0);
                _lastTY = null; _takeoffY = p.y;
                _landSeen = actor.landT;
            }
        }

        // Cinematic: move along a smooth arc from->to while looking from lookFrom->lookTo.
        public void cinematic(Vector3 from, Vector3 to, Vector3 lookFrom, Vector3 lookTo, float dur, Action onDone = null)
        {
            mode = "path";
            path = new PathInfo { from = from, to = to, lookFrom = lookFrom, lookTo = lookTo, t = 0, dur = dur, onDone = onDone };
        }

        public void orbit(Vector3 center, float radius, float height, float speed = 0.05f, float phase = 0)
        {
            mode = "orbit";
            orbitP = new OrbitInfo { center = center, radius = radius, height = height, speed = speed, phase = phase };
        }

        public void overview() => mode = "overview";

        public void spectateFrom(Actor killer, Vector3 pos, Vector3 from)
        {
            spectate = new SpectateInfo { actor = killer, pos = pos, from = from };
            mode = "spectate";
            lookAtP = pos + Vector3.up * 1.1f;
        }

        // Pose blend from the current rendered camera into whatever the new mode computes (ease in-out).
        void startBlend(float dur)
        {
            blend ??= new Blend();
            blend.t = 0; blend.dur = dur;
            blend.pos = camera.position; blend.quat = camera.quaternion; blend.fov = camera.fov;
            blend.active = true;
        }

        void modeChanged(string prev, string next)
        {
            spectateT = 0;
            if (next == "overview") _ovFresh = true;
            if ((G.match?.attract ?? false) || prev == null) return;   // attract mode cuts between shots on purpose
            float dur = 0;
            if (next == "spectate") dur = 0.55f;                            // splatted → death cam
            else if (next == "follow" && prev == "spectate") dur = 0.7f;   // respawn
            else if (next == "follow" && prev == "path") dur = 0.6f;       // intro fly-in → gameplay
            else if (next == "overview") dur = 1.2f;                        // time's up → judge
            else if (next == "follow" && prev != "follow") dur = 0.45f;
            if (dur > 0) startBlend(dur);
        }

        public void update(float dt, float aspect)
        {
            time += dt;
            camera.aspect = aspect;
            if (!float.IsFinite(pivot.x + pivot.y + pivot.z + pivotY + curDist + sx.x + sy.x + sz.x + boom.x + dipS.x + kickS.x + trauma))
            {
                pivot = new Vector3(0, 2, 0); pivotY = 2; curDist = dist;
                sx.reset(0); sy.reset(2); sz.reset(0); boom.reset(dist); dipS.x = 0; dipS.v = 0;
                kickS.reset(0); trauma = 0; _traumaIn = 0;
            }
            if (mode != _prevMode || (mode == "follow" && target != _prevTarget))
            {
                if (mode != _prevMode) modeChanged(_prevMode, mode);
                _prevMode = mode; _prevTarget = target;
            }
            var cam = camera;
            var s = G.settings;
            baseFov = s?.fov ?? 82;
            if (mode == "follow" && target != null) followStep(dt);
            else if (mode == "spectate" && spectate != null) spectateStep(dt);
            else if (mode == "path" && path != null) pathStep(dt);
            else if (mode == "orbit" && orbitP != null) orbitStep(dt);
            else if (mode == "overview") overviewStep(dt);
            // settings.fov is the HORIZONTAL field of view at a 16:9 reference (Hor+: wider screens see more, never a
            // stretched fisheye); kicks/zoom are in vertical degrees
            float vBase = 2 * Mathf.Atan(Mathf.Tan((baseFov * Mathf.PI) / 360) / (16f / 9)) * (180 / Mathf.PI);
            float fov = vBase + (fovKick - zoom) * 0.8f;
            // pose blend (mode transitions)
            var b = blend;
            if (b != null && b.active)
            {
                b.t += dt;
                float k = easeInOut(clamp(b.t / b.dur, 0, 1));
                cam.position = Vector3.LerpUnclamped(b.pos, cam.position, k);
                cam.quaternion = Quaternion.Slerp(b.quat, cam.quaternion, k);
                fov = lerp(b.fov, fov, k);
                if (k >= 1) b.active = false;
            }
            // trauma shake: smooth low-frequency noise (≤ ~4.5 Hz), rotation-led with a touch of translation
            if (_traumaIn > 0) { float m = Mathf.Min(_traumaIn, dt * 14); trauma = Mathf.Min(1, trauma + m); _traumaIn -= m; }
            else trauma = Mathf.Max(0, trauma - dt * 2.1f);
            float sh = trauma * trauma * (s?.cameraShake ?? 1) * shakeScale;
            if (sh > 0.0005f)
            {
                float t = time * 13, p = shakeSeed;
                cam.rotateX(nz(t, p) * 0.014f * sh);
                cam.rotateY(nz(t * 1.13f, p + 3.1f) * 0.01f * sh);
                cam.rotateZ(nz(t * 0.87f, p + 7.7f) * 0.008f * sh);
                Vector3 right = cam.quaternion * Vector3.right;
                cam.position += right * (nz(t * 1.07f, p + 11) * 0.025f * sh);
                cam.position.y += nz(t * 0.93f, p + 19) * 0.025f * sh;
            }
            // gameplay view, captured before the diorama touches the rendered camera
            var gc = gameCam;
            gc.copyFrom(cam);
            gc.fov = fov;
            // map diorama (eased both ways; reverses smoothly if the key is released mid-swoop)
            bool inPlay = mode == "follow" || mode == "spectate";
            float want = mapOpen && inPlay ? 1 : 0;
            mapK = want > 0 ? Mathf.Min(1, mapK + dt / 0.42f) : Mathf.Max(0, mapK - dt / 0.34f);
            if (mapK > 1e-4f)
            {
                diorama(dt);
                float e = easeInOut(mapK);
                var D = _dio;
                // crane: rise a little ahead of the travel so nothing clips on the way up, and keep the subject framed
                float up = e + 0.35f * e * (1 - e);
                Vector3 gF = gc.quaternion * Vector3.back;
                Vector3 gpT = gc.position + gF * 12;
                Vector3 bT = Vector3.LerpUnclamped(gpT, D.target, e);
                cam.position = new Vector3(lerp(gc.position.x, D.pos.x, e), lerp(gc.position.y, D.pos.y, up), lerp(gc.position.z, D.pos.z, e));
                cam.quaternion = ThreeSpace.lookAtRotation(cam.position, bT, Vector3.up, true);
                if (e < 0.15f) cam.quaternion = Quaternion.Slerp(gc.quaternion, cam.quaternion, e / 0.15f);
                if (e > 0.999f) cam.quaternion = D.quat; else if (e > 0.85f) cam.quaternion = Quaternion.Slerp(cam.quaternion, D.quat, (e - 0.85f) / 0.15f);
                fov = lerp(fov, DIO_FOV, e);
            }
            cam.fov = fov;
        }

        // ---- map diorama pose ---------------------------------------------------------------------------------------
        void diorama(float dt)
        {
            var D = _dio; var L = G.level;
            if (L == null) return;
            var B = L.bounds; float aspect = camera.aspect;
            if (D.layout != L.layout || Mathf.Abs(D.aspect - aspect) > 1e-3f) { D.layout = L.layout; D.aspect = aspect; fitDiorama(B, aspect); }
            D.yaw = damp(D.yaw, dioLook.x * 0.09f, 4, dt);
            D.pitch = damp(D.pitch, -dioLook.y * 0.05f, 4, dt);
            int f = dioFlip ? 1 : 0;
            dioPose(D.d, f == 1 ? -D.zShift : D.zShift, B, D.yaw + f * Mathf.PI, DIO_PITCH + D.pitch, out D.pos, out D.quat);
            D.target = _dT;
        }

        void dioPose(float d, float zShift, StageBounds B, float yw, float ptc, out Vector3 outPos, out Quaternion outQuat)
        {
            _dT = new Vector3((B.minX + B.maxX) / 2, 0, (B.minZ + B.maxZ) / 2 + zShift);
            float cp = Mathf.Cos(ptc);
            outPos = new Vector3(_dT.x - Mathf.Sin(yw) * cp * d, Mathf.Sin(ptc) * d, _dT.z - Mathf.Cos(yw) * cp * d);
            outQuat = ThreeSpace.lookAtRotation(outPos, _dT, Vector3.up, true);
        }

        // three's Vector3.project(camera): NDC of a three-space point
        static Vector3 project(Vector3 p, Vector3 camPos, Quaternion camQ, float fovDeg, float aspect)
        {
            Vector3 l = Quaternion.Inverse(camQ) * (p - camPos);   // camera looks down -z
            float f = 1 / Mathf.Tan(fovDeg * Mathf.Deg2Rad / 2);
            return new Vector3(l.x * f / aspect / -l.z, l.y * f / -l.z, 0);
        }

        // distance + look offset that fit the whole stage (bounds box, floor to rooftops) into the free screen area
        void fitDiorama(StageBounds B, float aspect)
        {
            var D = _dio;
            var pts = new System.Collections.Generic.List<Vector3>();
            foreach (float x in new[] { B.minX, B.maxX }) foreach (float z in new[] { B.minZ, B.maxZ }) foreach (float y in new[] { -1.2f, 6f }) pts.Add(new Vector3(x, y, z));
            const float yLo = -0.72f, yHi = 0.74f, xLim = 0.92f;
            float zShift = 0, d = 150;
            for (int pass = 0; pass < 4; pass++)
            {
                float lo = 20, hi = 1500;
                for (int it = 0; it < 28; it++)
                {
                    float mid = (lo + hi) / 2;
                    dioPose(mid, zShift, B, 0, DIO_PITCH, out var cpos, out var cq);
                    bool ok = true;
                    foreach (var p in pts) { var v = project(p, cpos, cq, DIO_FOV, aspect); if (v.x < -xLim || v.x > xLim || v.y < yLo || v.y > yHi) { ok = false; break; } }
                    if (ok) hi = mid; else lo = mid;
                }
                d = hi;
                dioPose(d, zShift, B, 0, DIO_PITCH, out var cpos2, out var cq2);
                float ymin = 9, ymax = -9;
                foreach (var p in pts) { var v = project(p, cpos2, cq2, DIO_FOV, aspect); ymin = Mathf.Min(ymin, v.y); ymax = Mathf.Max(ymax, v.y); }
                float off = (yLo + yHi) / 2 - (ymin + ymax) / 2;          // + → the stage should sit higher on screen
                zShift -= off * d * Mathf.Tan((DIO_FOV * Mathf.PI) / 360) / Mathf.Sin(DIO_PITCH);
            }
            D.d = d; D.zShift = zShift;
        }

        public Vector3 forward()
        {
            float cp = Mathf.Cos(pitch);
            return new Vector3(Mathf.Sin(yaw) * cp, Mathf.Sin(pitch), Mathf.Cos(yaw) * cp);
        }

        void followStep(float dt)
        {
            var a = target; var cam = camera;
            bool squid = a.form == "squid";
            string af = a.anim.form;
            bool swim = af == "swim" || af == "climb";
            var sj = a.superJumpState;
            bool flying = sj != null && sj.phase == "flight";
            bool special = a.specialActive != null;
            var p = a.visualPos();
            // ---- pivot height above the feet (form changes glide)
            float hT = swim ? 1.15f : squid ? 1.3f : 1.85f;
            float h = hgt.step(hT, 11, dt);
            // ---- horizontal: critically-damped with 72 % velocity feed-forward (fluid, no persistent lag)
            float omegaH = flying ? 20 : 30, lead = 0.72f * 2 / omegaH;
            Vector3 fwd = forward();
            Vector3 right = new Vector3(-Mathf.Cos(yaw), 0, Mathf.Sin(yaw));   // camera right on the ground plane
            // strafe look-ahead: a hair more view where you're heading sideways
            float lat = a.vel.x * right.x + a.vel.z * right.z;
            float sideT = clamp(lat * 0.045f, -0.32f, 0.32f) * (flying ? 0 : 1);
            float so = side.step(sideT, 3.2f, dt);
            float tx = p.x + a.vel.x * lead + right.x * so, tz = p.z + a.vel.z * lead + right.z * so;
            // snap if the target teleported (respawn, NaN recovery)
            float ty = p.y + h;
            if (Mathf.Abs(sx.x - p.x) + Mathf.Abs(sz.x - p.z) > 6 || Mathf.Abs(sy.x - ty) > 6)
            {
                sx.reset(p.x); sz.reset(p.z); sy.reset(ty); _lastTY = null; _vyF = 0;
            }
            sx.step(tx, omegaH, dt); sz.step(tz, omegaH, dt);
            // ---- vertical: firm on the ground (ramps, steps), soft through ordinary jumps, firm again for falls/super jumps
            bool fresh = _lastTY == null || dt <= 0;
            float vyRaw = fresh ? 0 : clamp((ty - _lastTY.Value) / dt, -30, 30);
            _lastTY = ty;
            _vyF = fresh ? 0 : damp(_vyF, vyRaw, 30, dt);
            float vyT = _vyF;
            float omegaV = 13, leadV = 0.5f;
            if (a.grounded || a.climbing) _takeoffY = p.y;
            if (!a.grounded && !a.climbing)
            {
                if (flying || special) { omegaV = 16; leadV = 0.85f; }
                else if (p.y < (_takeoffY ?? p.y) - 0.25f) { omegaV = 15; leadV = 0.8f; }   // dropping below where we left the ground
                else { omegaV = 7.5f; leadV = 0.25f; }                                      // a hop: keep the horizon calm
            }
            sy.step(ty + vyT * leadV * 2 / omegaV, omegaV, dt);
            // ---- landing dip (lightly under-damped: one soft settle with weight, no bob)
            if (a.landT < _landSeen - 1e-6f && a.landSpeed > 4) dipS.v -= clamp((a.landSpeed - 4) * 0.045f, 0, 0.45f);
            _landSeen = a.landT;
            float dip = clamp(stepDamped(dipS, 0, 13, 0.82f, dt), -0.3f, 0.12f);
            pivot = new Vector3(sx.x, sy.x + dip, sz.x);
            pivotY = pivot.y;
            // ---- super jump: swing toward the landing spot and look down on it during the descent; settle after landing
            float hs = Mathf.Sqrt(a.vel.x * a.vel.x + a.vel.z * a.vel.z);
            if (flying)
            {
                float dx = sj.to.x - sj.from.x, dz = sj.to.z - sj.from.z;
                if (dx * dx + dz * dz > 1) yaw = dampAngle(yaw, Mathf.Atan2(dx, dz), 3.5f, dt);
                float k = clamp(sj.t / sj.dur, 0, 1);
                pitch = damp(pitch, lerp(-0.3f, -0.75f, clamp((k - 0.35f) / 0.5f, 0, 1)), 4, dt);
                _sjWasFlight = true;
            }
            else if (_sjWasFlight) { _sjWasFlight = false; _sjLandT = 0; }
            if (_sjLandT < 0.45f) { _sjLandT += dt; if (pitch < -0.2f) pitch = damp(pitch, -0.16f, 7, dt); }
            // ---- FOV: swim speed, super jump, a touch on takeoff; charger zoom
            float fk = 0;
            if (swim) fk = clamp((hs - 6) * 1.1f, 0, 7);
            else if (flying) fk = 6;
            else if (!a.grounded && a.vel.y > 2) fk = 1.2f;
            fovKick = damp(fovKick, fk, 5, dt);
            float charging = a.weaponRunner.charging ? a.weaponRunner.charge : 0;
            zoom = damp(zoom, charging > 0.99f ? 14 : charging * 6, 8, dt);
            // ---- boom length: soft probe, fast in / slow out
            float want = (squid ? 4.1f : dist) - charging * 0.6f;
            if (swim) want += clamp((hs - 6) / 6, 0, 1) * 0.35f;
            if (flying) want += 1.2f;
            wantDist = damp(wantDist, want, 6, dt);
            G.physics.cameraProbe(pivot, -fwd, wantDist, 0.62f, _probe);
            float tgt = _probe.soft;
            // fast in / slow out. The lens may trail a fast-closing obstacle for a few frames (bounded past the line of sight)
            float deep = clamp((boom.x - _probe.hard) / 1.0f, 0, 1);
            boom.step(tgt, tgt < boom.x ? 22 + 26 * deep : 3.6f, dt);
            if (boom.x > _probe.hard + 1.6f) { boom.x = _probe.hard + 1.6f; boom.v = Mathf.Min(boom.v, 0); }
            if (_probe.floor && boom.x > _probe.hard) { boom.x = _probe.hard; boom.v = Mathf.Min(boom.v, 0); }
            if (boom.x < 0.45f) { boom.x = 0.45f; boom.v = Mathf.Max(0, boom.v); }
            curDist = boom.x;
            cam.position = pivot - fwd * curDist;
            cam.position.y += 0.15f;
            // keep the lens off the floor under it (only real floors well below the pivot — not wall tops at head height)
            float gy = G.level != null ? G.level.groundHeight(cam.position.x, cam.position.z, cam.position.y + 0.2f) : float.NegativeInfinity;
            float liftT = !float.IsNegativeInfinity(gy) && gy < pivot.y - 0.6f ? Mathf.Max(0, gy + 0.24f - cam.position.y) : 0;
            lensLift.step(liftT, liftT > lensLift.x ? 34 : 7, dt);
            cam.position.y += lensLift.x;
            // ---- over-the-shoulder when the boom is forced short (wall right behind you): ease the lens to the right
            float closeK = clamp((2.8f - curDist) / 1.8f, 0, 1);
            float shT = 0.55f * closeK * closeK * (3 - 2 * closeK);
            if (shT > 0.01f)
            {
                var hr = G.physics.raycast(cam.position, right, shT + 0.25f, _hit, true);
                if (hr.hit) shT = Mathf.Max(0, hr.dist - 0.25f);
            }
            shoulder = damp(shoulder, shT, 8, dt);
            if (shoulder > 1e-3f) cam.position += right * shoulder;
            // ---- visual recoil (pitch kick up), exact critically-damped spring
            kick = clamp(kickS.step(0, 22, dt), -0.01f, 0.035f);
            // ---- orientation = the aim, exactly
            Vector3 look = pivot + fwd * 10;
            look.y += 0.15f;
            if (shoulder > 1e-3f) look += right * shoulder;   // parallel shift: aim direction unchanged
            cam.up = Vector3.up;
            cam.lookAt(look);
            if (Mathf.Abs(kick) > 1e-6f) cam.rotateX(kick);
        }

        void spectateStep(float dt)
        {
            var s = spectate; var cam = camera;
            spectateT += dt;
            // first ~0.8 s: hold on the spot where you were splatted; then turn to watch whoever did it
            var killer = s.actor != null && s.actor.alive ? s.actor : null;
            Vector3 focus = spectateT < 0.8f || killer == null ? s.pos : killer.pos;
            Vector3 f = focus + Vector3.up * 1.1f;
            lookAtP = Vector3.LerpUnclamped(lookAtP, f, 1 - Mathf.Exp(-(spectateT < 0.8f ? 7 : 4.5f) * dt));
            // sit on the victim's side of the killer, slowly drifting round
            Vector3 w = s.from - lookAtP; w.y = 0;
            if (w.sqrMagnitude < 0.01f) w = new Vector3(0, 0, 1);
            w.Normalize();
            float ang = Mathf.Atan2(w.x, w.z) + Mathf.Min(spectateT, 6) * 0.07f;
            float r = spectateT < 0.8f ? 4.2f : 5.5f;
            Vector3 v = lookAtP + new Vector3(Mathf.Sin(ang) * r, 2.4f, Mathf.Cos(ang) * r);
            var h = G.physics?.segment(lookAtP, v, _hit, true);
            if (h != null && h.hit) v = Vector3.LerpUnclamped(h.point, lookAtP, 0.12f);
            cam.position = Vector3.LerpUnclamped(cam.position, v, 1 - Mathf.Exp(-3 * dt));
            cam.up = Vector3.up;
            cam.lookAt(lookAtP);
            fovKick = damp(fovKick, -6, 3, dt);
            zoom = damp(zoom, 0, 6, dt);
        }

        void pathStep(float dt)
        {
            var p = path; var cam = camera;
            p.t += dt;
            float k = clamp(p.t / p.dur, 0, 1);
            float e = easeInOut(k);
            cam.position = Vector3.LerpUnclamped(p.from, p.to, e);
            cam.position.y += Mathf.Sin(e * Mathf.PI) * 1.5f;
            cam.up = Vector3.up;
            cam.lookAt(Vector3.LerpUnclamped(p.lookFrom, p.lookTo, e));
            fovKick = 0; zoom = 0;
            if (k >= 1 && p.onDone != null) { var cb = p.onDone; p.onDone = null; cb(); }
        }

        void orbitStep(float dt)
        {
            var o = orbitP; var cam = camera;
            float a = o.phase + time * o.speed;
            var v = new Vector3(o.center.x + Mathf.Sin(a) * o.radius, o.center.y + o.height + Mathf.Sin(time * 0.13f) * 1.2f, o.center.z + Mathf.Cos(a) * o.radius);
            cam.position = Vector3.LerpUnclamped(cam.position, v, 1 - Mathf.Exp(-2 * dt));
            cam.up = Vector3.up;
            cam.lookAt(o.center);
            fovKick = 0; zoom = 0;
        }

        void overviewStep(float dt)
        {
            var cam = camera;
            // crane up to the judge view on critically-damped springs (starts from rest — no lurch)
            if (_ov == null || _ovFresh) { _ov = new[] { new Spring(cam.position.x), new Spring(cam.position.y), new Spring(cam.position.z) }; _ovFresh = false; }
            var v = new Vector3(0, 62, -18);
            cam.position = new Vector3(_ov[0].step(v.x, 1.9f, dt), _ov[1].step(v.y, 1.9f, dt), _ov[2].step(v.z, 1.9f, dt));
            cam.up = Vector3.up;
            cam.lookAt(new Vector3(0, 0, 3));
            fovKick = damp(fovKick, -12, 2, dt);
            zoom = 0;
        }
    }
}
