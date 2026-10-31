// Port of src/game/player.js — local player controller: input → actor intent + camera yaw/pitch + aim point.
//
// Look: mouse is raw 1:1 (cursor lock — no smoothing, no acceleration). Gamepad uses a radial dead zone, a two-stage
// response curve (fine control near centre, fast at the edge) and a short edge boost for quick turn-arounds. Touch
// drags carry their own gain. Aim assist (gamepad + touch by default; settings.aimAssistMouse opts mouse in, gentler):
// friction slows the look near an enemy under the crosshair, tracking assist carries a fraction of the target's
// angular motion while you are actively aiming or moving — never an auto-snap. Bullet magnetism pulls shots onto the
// body line of an enemy the crosshair is actually touching (at the height you aimed), so hits register as they look.
//
// M1: the TAB-map super jump to allies / base is ported; jump beacons and the Vortex Strike map cursor come with M3.
using System.Linq;
using UnityEngine;
using static Inkwave.Ctx;

namespace Inkwave
{
    public class PlayerController
    {
        const float DEG = Mathf.PI / 180;

        // fine near the centre, fast at the edge (continuous, slope-matched at the knee)
        static float lookCurve(float m) => m < 0.75f ? 0.62f * Mathf.Pow(m / 0.75f, 1.6f) : 0.62f + ((m - 0.75f) / 0.25f) * 0.38f;

        public readonly Actor a;
        readonly CameraRig rig;
        readonly InputState input;
        public bool mapHeld, enabled = true, inRange;
        public Actor onTarget;
        Vector2 padLook;
        float edgeT;
        readonly InputState.StickOut _stick = new InputState.StickOut();
        readonly Hit _hit = new Hit();

        class Assist { public Actor target; public float yaw, pitch, prevYaw, prevPitch, closeness, strength; public bool has, prevValid; }
        readonly Assist assist = new Assist();

        public PlayerController(Actor actor, CameraRig rig, InputState input) { a = actor; this.rig = rig; this.input = input; }

        bool _strikePull;
        public void update(float dt)
        {
            var inp = input; var s = G.settings;
            var it = a.intent;
            if (!enabled)
            {
                it.move = Vector3.zero; it.fire = it.jump = it.squid = it.sub = it.special = false;
                assist.has = false;
                // splatted in a live round: the map still opens (super jump planning arrives with the full HUD, M6)
                var m = G.match;
                bool planning = m != null && !a.alive && m.state == "playing" && !m.paused;
                mapHeld = planning && (inp.down("Tab") || inp.down("KeyM") || inp.padButton(8) || inp.touch.map);
                return;
            }
            bool usingPad = inp.pad != null && inp.lastDevice == "pad";
            var touch = inp.touch;
            // Touch takes the full-strength assist (the pad's tier), not the mouse's gentler opt-in: a thumb on glass is far
            // less precise than a mouse.
            bool assisted = usingPad || touch.active;
            // ---- aim assist target (computed from last frame's camera; cheap)
            var asT = assistTarget(assisted ? s.aimAssist : (s.aimAssistMouse ? 0.5f : 0));
            // ---- look
            float inv = s.invertY ? -1 : 1;
            float tInv = s.touchInvertY ? -1 : 1;   // separate setting: the natural touch mapping is usually the mouse's opposite
            float friction = asT != null ? lerp(1, 0.58f, asT.closeness * asT.strength) : 1;
            bool lookActive = false;
            float mdx = inp.mouse.dx, mdy = inp.mouse.dy;
            // while the map diorama is up the mouse / right stick steer the map cursor, not your camera
            bool mapUp = (G.rig?.mapK ?? 0) > 0.05f || inp.down("Tab") || inp.down("KeyM") || inp.padButton(8) || touch.map;
            float ldx = mapUp ? 0 : mdx, ldy = mapUp ? 0 : mdy;
            if (ldx != 0 || ldy != 0)
            {
                // A touch drag is bounded by the screen, so it carries its own gain and always gets the aim-assist friction.
                float sens = touch.active
                    ? TOUCH.lookSens * s.touchSensitivity * friction
                    : 0.0021f * s.sensitivity * (s.aimAssistMouse ? friction : 1);
                rig.yaw -= ldx * sens;
                rig.pitch -= ldy * sens * (touch.active ? tInv : inv);
                lookActive = true;
            }
            if (inp.pad != null && !mapUp)
            {
                inp.padStick(2, 3, _stick, 0.11f, 0.96f);
                float ps = s.padSensitivity;
                // edge boost: holding the stick at the rim speeds yaw up (quick 180s) after a short delay
                if (_stick.mag > 0.93f) edgeT = Mathf.Min(0.5f, edgeT + dt); else edgeT = Mathf.Max(0, edgeT - dt * 3);
                float boost = 1 + 0.55f * clamp((edgeT - 0.16f) / 0.3f, 0, 1);
                float c = _stick.mag > 0 ? lookCurve(_stick.mag) / _stick.mag : 0;
                // tiny low-pass on the stick removes sensor noise without adding felt latency (~16 ms)
                float k = 1 - Mathf.Exp(-60 * dt);
                padLook.x += (_stick.x * c - padLook.x) * k; padLook.y += (_stick.y * c - padLook.y) * k;
                if (_stick.mag > 0) lookActive = true;
                rig.yaw -= padLook.x * 3.6f * ps * boost * friction * dt;
                rig.pitch -= padLook.y * 2.4f * ps * friction * dt * inv;
            }
            // ---- move (camera relative)
            float mx = 0, mz = 0;
            if (inp.down("KeyW") || inp.down("ArrowUp")) mz += 1;
            if (inp.down("KeyS") || inp.down("ArrowDown")) mz -= 1;
            if (inp.down("KeyA") || inp.down("ArrowLeft")) mx -= 1;
            if (inp.down("KeyD") || inp.down("ArrowRight")) mx += 1;
            if (inp.pad != null) { inp.padStick(0, 1, _stick, 0.14f, 0.95f); mx += _stick.x; mz -= _stick.y; }
            if (touch.active) { mx += touch.mx; mz += touch.mz; }   // left stick; already clamped to the rim
            float ml = Mathf.Sqrt(mx * mx + mz * mz);
            if (ml > 1) { mx /= ml; mz /= ml; }
            // tracking assist: carry a share of the target's angular motion while the player is engaging (look or move input)
            if (asT != null && asT.prevValid && (lookActive || ml > 0.2f || it.fire))
            {
                float share = 0.42f * asT.strength * asT.closeness;
                rig.yaw += angleDiff(asT.prevYaw, asT.yaw) * share;
                rig.pitch += (asT.pitch - asT.prevPitch) * share * 0.7f;
            }
            rig.pitch = clamp(rig.pitch, -1.05f, 1.15f);
            a.aimYaw = rig.yaw;
            a.aimPitch = rig.pitch;
            float sy = Mathf.Sin(rig.yaw), cy = Mathf.Cos(rig.yaw);
            // forward = (sy, 0, cy); right = (-cy, 0, sy)
            it.move = new Vector3(sy * mz - cy * mx, 0, cy * mz + sy * mx);

            // Vortex Strike targeting: the move stick slides the target (a ground ring until the full map arrives), fire
            // launches on a fresh press (a trigger still held from shooting when the special started doesn't count)
            var spx = a.specialActive;
            if (spx != null && spx.id == "strike" && spx.data is Strike.State sst && sst.aiming)
            {
                G.specials.aimMove(a, it.move * (18 * dt));
                bool pull = inp.mouse.left || inp.padValue(7) > 0.3f;
                if (spx.t < 0.05f) _strikePull = pull;
                if (pull && !_strikePull) G.specials.aimConfirm(a);
                _strikePull = pull;
                it.move = Vector3.zero; it.fire = it.jump = it.squid = it.sub = it.special = false;
                mapHeld = false;
                computeAim();
                return;
            }

            it.jump = inp.down("Space") || inp.padButton(0) || inp.touchHeld("jump");
            it.squid = inp.down("ShiftLeft") || inp.down("ShiftRight") || inp.padValue(6) > 0.3f || inp.touchHeld("squid");
            it.fire = inp.mouse.left || inp.padValue(7) > 0.3f;
            it.sub = inp.mouse.right || inp.down("KeyE") || inp.padButton(5);
            it.special = inp.down("KeyF") || inp.down("KeyQ") || inp.padButton(3) || inp.padButton(11) || inp.touchHeld("special");
            mapHeld = inp.down("Tab") || inp.down("KeyM") || inp.padButton(8) || touch.map;
            // the map is a targeting UI (picking a teammate super jumps) — never fire or throw through it
            if (mapHeld) { it.fire = false; it.sub = false; }
            // super jump: while the map is open, 1-3 (or d-pad left/up/right) jumps to that teammate, 4 / d-pad down to spawn
            if (mapHeld && a.canSuperJump()) mapKeys();

            // ---- aim point from the camera centre ray
            computeAim();
        }

        readonly System.Collections.Generic.List<SubItem> _beacons = new System.Collections.Generic.List<SubItem>();
        void mapKeys()
        {
            var inp = input;
            var allies = G.actors.Where(o => o.team == a.team && o != a).ToList();
            void pick(string kind, Actor target)
            {
                if (kind == "ally") { if (target != null && target.alive && target.superJumpState == null) a.superJump(target); }
                else if (kind == "base") a.superJump(G.level.spawnPads[a.team]);
            }
            if (inp.wasPressed("Digit1") || inp.padPressed.Contains(14)) pick("ally", allies.ElementAtOrDefault(0));
            if (inp.wasPressed("Digit2") || inp.padPressed.Contains(12)) pick("ally", allies.ElementAtOrDefault(1));
            if (inp.wasPressed("Digit3") || inp.padPressed.Contains(15)) pick("ally", allies.ElementAtOrDefault(2));
            if (inp.wasPressed("Digit4") || inp.padPressed.Contains(13)) pick("base", null);
            // 5–9 + 0: the team's jump beacons, oldest first (as the map lists them)
            if (G.subs != null)
            {
                var bs = G.subs.beaconsFor(a.team, _beacons); bs.Sort((x, y) => x.born.CompareTo(y.born));
                for (int k = 0; k < Mathf.Min(6, bs.Count); k++) if (inp.wasPressed("Digit" + ((k + 5) % 10))) G.subs.jumpToBeacon(a, bs[k]);
            }
        }

        // Best enemy near the crosshair for aim assist (angular cone scaled so it covers ~a body width at any range).
        Assist assistTarget(float strength)
        {
            var asT = assist;
            var cam = G.rig?.gameCam;
            if (!(strength > 0) || cam == null) { asT.has = false; asT.target = null; return null; }
            Vector3 fwd = cam.getWorldDirection();
            var w = a.weapon;
            float maxR = Mathf.Min(32, Config.weaponRange(w) * 1.15f + 2);
            Actor best = null; float bestScore = float.PositiveInfinity, bYaw = 0, bPitch = 0, bClose = 0;
            foreach (var e in G.actors)
            {
                if (e.team == a.team || !e.alive || e.anim.form == "swim" || e.invuln > 0) continue;
                Vector3 c = new Vector3(e.pos.x, e.pos.y + e.smoothY + (e.form == "squid" ? 0.3f : 0.95f), e.pos.z);
                Vector3 v = c - cam.position;
                float d = v.magnitude;
                if (d > maxR + 4 || d < 0.5f) continue;
                if (Vector3.Distance(a.pos, e.pos) > maxR) continue;
                v /= d;
                float ang = Mathf.Acos(clamp(Vector3.Dot(v, fwd), -1, 1));
                float cone = clamp(Mathf.Atan2(1.0f, d), 2.5f * DEG, 10 * DEG);
                if (ang > cone) continue;
                if (!G.physics.los(cam.position, c)) continue;
                float score = ang / cone + d * 0.01f;
                if (score < bestScore) { bestScore = score; best = e; bYaw = Mathf.Atan2(v.x, v.z); bPitch = Mathf.Asin(clamp(v.y, -1, 1)); bClose = 1 - ang / cone; }
            }
            if (best == null) { asT.has = false; asT.target = null; return null; }
            asT.prevValid = asT.has && asT.target == best;
            asT.prevYaw = asT.yaw; asT.prevPitch = asT.pitch;
            asT.target = best; asT.yaw = bYaw; asT.pitch = bPitch; asT.closeness = clamp(bClose * 1.3f, 0, 1); asT.strength = strength; asT.has = true;
            return asT;
        }

        public void computeAim()
        {
            // the gameplay view — while the map diorama is up the rendered camera is overhead, aim stays with the player
            var cam = G.rig.gameCam;
            Vector3 fwd = cam.getWorldDirection();
            // start the ray level with the player so geometry between camera and player is ignored
            Vector3 chest = a.pos + Vector3.up * 1.3f;
            float along = Mathf.Max(0, Vector3.Dot(chest - cam.position, fwd));
            Vector3 start = cam.position + fwd * along;
            var hit = G.physics.raycast(start, fwd, 70, _hit, true);
            float dist = hit.hit ? hit.dist : 70;
            a.aimPoint = start + fwd * dist;
            // enemy under the crosshair? (visual body, generous by 0.2 m)
            onTarget = null;
            float best = dist;
            float reach = Mathf.Min(dist, 34);
            Vector3 end = start + fwd * reach;
            foreach (var e in G.actors)
            {
                if (e.team == a.team || !e.alive) continue;
                if (e.anim.form == "swim") continue;
                var (d0, t) = Physics.segmentCapsuleDist(start, end, new Vector3(e.pos.x, e.pos.y + e.smoothY, e.pos.z), PLAYER.radius, e.form == "squid" ? PLAYER.squidHeight : PLAYER.height);
                if (d0 < PLAYER.radius + 0.2f)
                {
                    float d = t * reach;
                    if (d < best) { best = d; onTarget = e; }
                }
            }
            if (onTarget != null)
            {
                // bullet magnetism: converge on the enemy's body axis at the height the crosshair crosses it
                var e = onTarget;
                float h = e.form == "squid" ? PLAYER.squidHeight : PLAYER.height;
                float py = start.y + fwd.y * best;
                float baseY = e.pos.y + e.smoothY;
                a.aimPoint = new Vector3(e.pos.x, clamp(py, baseY + 0.2f, baseY + h - 0.12f), e.pos.z);
            }
            // is the crosshair point inside the weapon's effective range? (HUD reticle state)
            inRange = Vector3.Distance(a.aimPoint, a.pos) <= Config.weaponRange(a.weapon) + 0.5f;
        }
    }
}
