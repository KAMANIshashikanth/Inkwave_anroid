// Port of src/game/kits/blade.js — Brine Cutlass (kind 'blade'), a sword that swings like a brush, without the rolling.
//   Tap fire   → a quick horizontal cut (alternating right → left / left → right). The blade itself is a hitbox for the
//                sweep (an arc in front, `reach` m) that deals `tapMelee`; the cut flings a wide crescent of droplets
//                forward (one droplet per victim per cut: `tapDamageNear` → `tapDamageFar`).
//   Hold fire  → after the cut, the blade rises and charges (`chargeTime`, glowing hilt → tip, a rising hum); full
//                charge holds as long as the trigger does (and through a quick dive, `chargeStore` s). Release at full
//                → an overhead cut with a short lunge: the blade splats in one hit (`heavyMelee`) and a long straight ink
//                wave flies `waveRange` m, piercing enemies (`waveDamage` → `waveDamageFar`) and painting a stripe.
//                Released early (≥ 30 %) → just a quick cut.
// Runner state lives in runner.kit (a BladeState); runner.charge mirrors the charge (HUD meter, pose, blade glow) but
// runner.charging stays false: that flag makes the core treat the kid as busy (no diving), and the blade may dive with
// a charge held. World objects (waves, arc trails) are module-owned (tick / clear). Model: BladeModel.cs; trails and the
// wave's look: BladeFX.cs. Not ported: the online ghosts, the Boss Battle hits (M7), the sounds (M5: played by name
// through G.audio).
using System.Collections.Generic;
using Inkwave.World;
using UnityEngine;
using static Inkwave.Ctx;

namespace Inkwave
{
    /** runner.kit while the Cutlass is held */
    public class BladeState
    {
        public bool held, charging, full, lowWarned;
        public float holdT, charge, store, cd, queued, side = -1, lunge, lx, lz, lv, lastT, flashT = 99, since = 99;
        public int taps, heavies;
        public Blade.Swing swing;
    }

    public class Blade : MainKit
    {
        const float DEG = Mathf.PI / 180;
        static readonly Vector3 DOWN = new Vector3(0, -1, 0), UP = new Vector3(0, 1, 0);
        static readonly Hit _hit = new Hit(), _hit2 = new Hit();
        static WeaponDef W => Config.WEAPONS["blade"];

        public class Swing { public bool heavy, fired, dev, stroke, sp; public float t, side, yaw, pitch; public readonly HashSet<Actor> hits = new HashSet<Actor>(); }

        // ------------------------------------------------------------------------------------------ runner state
        static BladeState st(WeaponRunner R) => R.kit as BladeState ?? (BladeState)(R.kit = new BladeState { lastT = G.time });
        // charge hums are keyed by runner (a loadout swap resets through the NEW kind's kit — tick() sweeps any hum whose
        // runner stopped charging a blade)
        static readonly Dictionary<WeaponRunner, AudioLoop> HUMS = new Dictionary<WeaponRunner, AudioLoop>();
        static readonly List<WeaponRunner> _hk = new List<WeaponRunner>();
        static void stopHum(WeaponRunner R) { if (HUMS.TryGetValue(R, out var h)) { h?.stop(0.06f); HUMS.Remove(R); } }
        static void cancelCharge(WeaponRunner R, BladeState K)
        {
            K.charging = false; K.charge = 0; K.full = false; K.store = 0;
            R.charge = 0;
            stopHum(R);
        }
        static bool heard(Actor a) => a.isLocal || a.nearCamera();
        static Vector3? sndPos(Actor a) => a.isLocal ? (Vector3?)null : a.pos;
        // core bot logic holding the trigger for its own ends (inking a wall to climb, a puddle to refill in): quick cuts
        static bool coreDriven(Actor a) => a.bot != null && !a.isLocal && (a.bot.climbAiming || a.bot.mode == "refill");
        // a bot brain that wants to keep holding a charge even though the core cleared its trigger this frame (aim drifted)
        static bool botHolds(Actor a) => a.bot != null && a.bot.kitMem is BotState B && B.blHold > G.time - 0.3f;

        // ------------------------------------------------------------------------------------------ the cuts
        // Heading of a cut: toward the crosshair's world point (the camera sits over the shoulder, so its ray and the kid's
        // facing differ by a few degrees at mid range), else the aim yaw.
        static float cutYaw(Actor a)
        {
            Vector3 p = a.aimPoint; float dx = p.x - a.pos.x, dz = p.z - a.pos.z;
            if (dx * dx + dz * dz < 4) return a.aimYaw;
            float y = Mathf.Atan2(dx, dz);
            return Mathf.Abs(angleDiff(a.aimYaw, y)) < 0.35f ? y : a.aimYaw;
        }
        static bool tap(WeaponRunner R, BladeState K, WeaponDef w)
        {
            var a = R.a;
            if (a.ink < w.tapInk) { R.empty(); K.cd = 0.18f; return false; }
            a.ink -= w.tapInk; a.lastFire = 0;
            K.side = -K.side;
            K.cd = w.tapInterval; K.since = 0; K.taps++;
            K.swing = new Swing { heavy = false, side = K.side, yaw = cutYaw(a), pitch = a.aimPitch, sp = a.specialActive != null };
            R.firingT = 0.4f; a.fireFacing = 0.45f;
            a.character.trigger("blade_slash", K.side);
            BladeFX.slash(a, K.side, false, 0.1f, K.swing.yaw);
            if (heard(a)) G.audio?.play("blade_swish", new AudioOpts { pos = sndPos(a), volume = a.isLocal ? 0.55f : 0.42f, pitch = K.side > 0 ? 1 : 1.09f });
            if (a.isLocal) emit("recoil", new Ev { amount = 0.004f });
            Actor.rumble(a, 0.05f, 0.12f, 45);
            return true;
        }
        static void heavy(WeaponRunner R, BladeState K, WeaponDef w)
        {
            var a = R.a;
            a.ink = Mathf.Max(0, a.ink - w.heavyInk); a.lastFire = 0;
            K.cd = w.heavyInterval; K.since = 0; K.flashT = 0; K.heavies++;
            K.swing = new Swing { heavy = true, side = 1, yaw = cutYaw(a), pitch = a.aimPitch, sp = a.specialActive != null };
            R.firingT = 0.5f; a.fireFacing = 0.6f;
            // short forward lunge with the cut (grounded; never off into the sea)
            float fx = Mathf.Sin(K.swing.yaw), fz = Mathf.Cos(K.swing.yaw);
            K.lunge = 0;
            if (a.grounded && lungeSafe(a, fx, fz, w.lungeDist)) { K.lunge = w.lungeTime; K.lx = fx; K.lz = fz; K.lv = w.lungeDist / (w.lungeTime * 0.75f); }
            a.character.trigger("blade_heavy");
            BladeFX.slash(a, 1, true, 0.12f, K.swing.yaw);
            if (heard(a)) G.audio?.play("blade_heavy", new AudioOpts { pos = sndPos(a), volume = a.isLocal ? 0.7f : 0.55f });
            if (a.isLocal) { emit("recoil", new Ev { amount = 0.012f }); emit("shake", new Ev { amount = 0.1f }); }
            Actor.rumble(a, 0.25f, 0.3f, 120);
        }
        static readonly float[] LUNGE_K = { 0.5f, 1, 1.4f };
        static bool lungeSafe(Actor a, float fx, float fz, float d)
        {
            var L = G.level; if (L == null) return false;
            foreach (float k in LUNGE_K)
            {
                float gy = L.groundHeight(a.pos.x + fx * d * k, a.pos.z + fz * d * k, a.pos.y + 0.6f);
                if (!(gy > PLAYER.waterY + 0.25f)) return false;                       // sea / void ahead
                if (a.bot != null && !a.isLocal && gy < a.pos.y - 1.6f) return false;   // bots don't lunge off ledges
            }
            return true;
        }

        // the swing in progress: ink leaves the blade, the blade itself hits, the lunge carries the kid forward
        static void runSwing(WeaponRunner R, BladeState K, float dt, WeaponDef w)
        {
            var S = K.swing; var a = R.a;
            S.t += dt;
            if (!S.fired && S.t >= (S.heavy ? 0.05f : 0.028f)) { S.fired = true; if (S.heavy) spawnWave(a, w, S); else fireDrops(a, w, S); }
            if (S.heavy && !S.stroke && S.t >= 0.075f) { S.stroke = true; groundStroke(a, w, S); }
            float t0 = 0.01f, t1 = S.heavy ? 0.16f : 0.12f;
            if (S.t >= t0 && S.t - dt <= t1) melee(a, w, S);
            if (K.lunge > 0)
            {
                K.lunge -= dt;
                if (K.lunge > 0 && a.alive) { float v = K.lv * (0.55f + 0.45f * Mathf.Min(1, K.lunge / (w.lungeTime * 0.5f))); a.vel.x = K.lx * v; a.vel.z = K.lz * v; }
            }
            if (S.t > 0.4f) K.swing = null;
        }

        // Quick cut: a crescent of droplets flung across the front along the sweep (the centre ones fastest, so the sheet
        // bows forward), leaving from the blade's path in sweep order. Aiming steeply down throws them at your feet.
        static void fireDrops(Actor a, WeaponDef w, Swing S)
        {
            var P = G.projectiles; if (P == null) return;
            float yaw = S.yaw, side = S.side, pitch = a.aimPitch;
            bool steep = pitch < -0.55f;
            float up = steep ? pitch * 0.85f : clamp(pitch, -0.4f, 0.45f) * 0.75f + 0.06f;
            var vol = new HashSet<Actor>();
            int N = (int)w.tapDrops; float A = w.tapSpreadDeg * DEG;
            Color col = G.teamColors[a.team];
            for (int i = 0; i < N; i++)
            {
                float t = N > 1 ? (float)i / (N - 1) : 0.5f;
                float off = side * (t - 0.5f) * A;                         // side +1 sweeps the kid's right → left
                float ang = yaw + off, ox = Mathf.Sin(ang), oz = Mathf.Cos(ang);
                float mid = 1 - Mathf.Abs(t - 0.5f) * 2;
                float sp = w.tapSpeed * (0.8f + 0.24f * mid) * (0.95f + random() * 0.1f) * (steep ? 0.45f : 1);
                var p = P._new();
                p.type = "drop"; p.owner = a; p.team = a.team; p.age = 0; p.life = 1.2f; p.straight = steep ? 0 : w.tapStraight;
                p.radius = w.tapDropRadius * (0.85f + random() * 0.3f); p.damage = w.tapDamageNear; p.dmgFar = w.tapDamageFar;
                p.size = 0.13f; p.trail = -0.2f; p.trailEvery = 1.25f; p.trailRadius = 0.3f; p.grav = 24; p.drag = 0.5f; p.seed = random(); p.volley = vol; p.weaponId = "blade";
                p.delay = t * 0.018f; p.sp = S.sp; p.spSet = true;
                p.vis = 0.044f + 0.03f * mid + random() * 0.01f; p.tail0 = 0.8f; p.tailK = 1.7f; p.wob = 0.07f; p.wobF = 22; p.nose = 0.15f; p.sats = 1;
                // leaves from the blade's path: a diagonal cut (high at the start, low at the end) — never from inside a wall
                // the kid is hugging (then it leaves from the body and splats on the wall)
                float r = steep ? 0.55f : 0.8f;
                p.pos = new Vector3(a.pos.x + ox * r, a.pos.y + (steep ? 0.7f : 0.98f) + (0.5f - t) * 0.12f, a.pos.z + oz * r);
                var body = new Vector3(a.pos.x, p.pos.y, a.pos.z);
                if (!G.physics.los(body, p.pos)) p.pos = body + new Vector3(ox, 0, oz) * 0.12f;
                p.prev = p.pos; p.start = p.pos;
                float cu = Mathf.Cos(up + (random() - 0.5f) * 0.05f);
                // outward, with a little of the sweep's own motion
                float tx = -oz * side, tz = ox * side;
                float hx = ox + tx * 0.12f, hz = oz + tz * 0.12f, hl = Mathf.Sqrt(hx * hx + hz * hz);
                p.vel = new Vector3((hx / hl) * cu * sp, Mathf.Sin(up) * sp, (hz / hl) * cu * sp);
                P._push(p);
            }
            // a fine spray off the edge (FX only)
            if (G.fx != null && heard(a))
            {
                for (int i = 0; i < 6; i++)
                {
                    float off = side * (random() - 0.5f) * A * 1.1f, ang = yaw + off;
                    G.fx.drop(new Vector3(a.pos.x + Mathf.Sin(ang) * 0.9f, a.pos.y + 0.98f, a.pos.z + Mathf.Cos(ang) * 0.9f),
                        new Vector3(Mathf.Sin(ang) * (4 + random() * 4), 1 + random() * 2, Mathf.Cos(ang) * (4 + random() * 4)), col, new FxOpts { size = 0.035f + random() * 0.03f, life = 0.6f });
                }
            }
            emit("weapon:fire", new Ev { actor = a, id = w.id, kind = "quick", muzzle = new Vector3(a.pos.x + Mathf.Sin(yaw) * 0.8f, a.pos.y + 0.98f, a.pos.z + Mathf.Cos(yaw) * 0.8f), dir = new Vector3(Mathf.Sin(yaw), Mathf.Sin(up), Mathf.Cos(yaw)).normalized });
        }

        // the blade meets the ground in front at the bottom of the overhead cut: a stroke of ink (cuts through enemy ink)
        static readonly float[] STROKE_D = { 0.75f, 1.35f };
        static void groundStroke(Actor a, WeaponDef w, Swing S)
        {
            float fx = Mathf.Sin(S.yaw), fz = Mathf.Cos(S.yaw);
            var n = new Vector3(fx, 0, fz);
            float area = 0;
            foreach (float d in STROKE_D)
            {
                var g = G.physics.raycast(new Vector3(a.pos.x + fx * d, a.pos.y + 0.8f, a.pos.z + fz * d), DOWN, 2.2f, _hit2, true);
                if (!g.hit) continue;
                area += G.paint.splat(g.point + g.normal * 0.08f, w.strokeRadius, a.team, new SplatOpts { seed = random(), stretch = n, stretchAmt = 0.9f });
                if (d > 1 && heard(a)) G.fx?.burst(g.point, g.normal, G.teamColors[a.team], new FxOpts { count = 10, speed = 3.5f, size = 0.08f });
            }
            credit(a, area, S.sp);
        }
        static void credit(Actor a, float area, bool sp) { if (area > 0) { if (sp) a.addTurfNoSpecial(area); else a.addTurf(area); } }

        // The blade as a hitbox: every enemy inside the arc in front (reach + their body radius, the swing's vertical window,
        // nothing through walls) takes the cut's melee damage once per swing. Shields / curtains in between take it instead.
        static void melee(Actor a, WeaponDef w, Swing S)
        {
            float reach = w.reach, half = (S.heavy ? w.heavyArcDeg : w.meleeArcDeg) * DEG * 0.5f;
            float fx = Mathf.Sin(S.yaw), fz = Mathf.Cos(S.yaw);
            float dmg = S.heavy ? w.heavyMelee : w.tapMelee;
            var o = new Vector3(a.pos.x, a.pos.y + 1.0f, a.pos.z);
            foreach (var e in G.actors)
            {
                if (e == a || e.team == a.team || !e.alive || S.hits.Contains(e)) continue;
                float hr = PLAYER.radius, h = e.form == "squid" ? PLAYER.squidHeight : PLAYER.height;
                float dx = e.pos.x - a.pos.x, dz = e.pos.z - a.pos.z, d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d > reach + hr) continue;
                float ey = e.pos.y + e.smoothY - a.pos.y;
                if (ey > (S.heavy ? 1.9f : 1.55f) || ey + h < (S.heavy ? -0.9f : -0.35f)) continue;
                float fwd = dx * fx + dz * fz, lat = Mathf.Abs(dx * fz - dz * fx);
                float ang = Mathf.Atan2(lat, fwd);
                if (ang > half && !(d < hr + 0.4f && fwd > -0.15f)) continue;
                var v = new Vector3(e.pos.x, e.pos.y + e.smoothY + Mathf.Min(h * 0.6f, 0.85f), e.pos.z);
                if (!G.physics.los(o, v)) continue;
                S.hits.Add(e);
                if (G.subs.blockShot(o, v, a.team, dmg)) { if (heard(a)) G.audio?.play("blade_hit", new AudioOpts { pos = v, volume = 0.4f, pitch = 1.4f }); continue; }
                G.projectiles.applyHit(a, e, dmg, "blade");
                // contact: where the edge meets the body
                Vector3 n = v - o; float l = n.magnitude; if (l == 0) l = 1; n /= l;
                Vector3 at = o + n * Mathf.Max(0.2f, l - hr);
                if (heard(a) || e.isLocal)
                {
                    G.fx?.burst(at, -n, G.teamColors[a.team], new FxOpts { count = S.heavy ? 18 : 10, speed = S.heavy ? 6 : 4, size = 0.09f, mist = true });
                    G.audio?.play("blade_hit", new AudioOpts { pos = a.isLocal ? (Vector3?)null : at, volume = a.isLocal ? 0.6f : 0.5f, pitch = S.heavy ? 0.78f : 1 });
                }
                if (a.isLocal) emit("shake", new Ev { amount = S.heavy ? 0.22f : 0.06f });
                Actor.rumble(a, S.heavy ? 0.4f : 0.15f, S.heavy ? 0.45f : 0.2f, S.heavy ? 140 : 60);
                emit("blade:hit", new Ev { actor = a, victim = e, damage = dmg, final = S.heavy });
            }
            // (Boss Battle: the edge meets HULLBREAKER along the swing's centre line — M7)
            // devices in reach (sprinklers, beacons, curtains): the blade cuts them too — once per swing
            if (!S.dev)
            {
                S.dev = true;
                G.subs.damageArea(new Vector3(a.pos.x + fx * reach * 0.6f, a.pos.y + 0.8f, a.pos.z + fz * reach * 0.6f), reach * 0.75f, dmg * 0.6f, a.team);
            }
        }

        // ------------------------------------------------------------------------------------------ the ink wave
        class WaveRec
        {
            public Actor owner; public int team; public Vector3 pos, prev, dir; public float dist, t, paintAcc = 0.3f, dying = -1; public bool sp;
            public Color col; public BladeFX.Wave fx; public WeaponDef w; public readonly HashSet<Actor> hits = new HashSet<Actor>();
        }
        static readonly List<WaveRec> WAVES = new List<WaveRec>();
        static void spawnWave(Actor a, WeaponDef w, Swing S)
        {
            float pitch = Mathf.Abs(S.pitch) < 0.3f ? S.pitch * 0.4f : clamp(S.pitch, -0.45f, 0.35f);
            float cp = Mathf.Cos(pitch);
            var dir = new Vector3(Mathf.Sin(S.yaw) * cp, Mathf.Sin(pitch), Mathf.Cos(S.yaw) * cp);
            var pos = new Vector3(a.pos.x + Mathf.Sin(S.yaw) * 0.55f, a.pos.y + w.waveHeight * 0.52f, a.pos.z + Mathf.Cos(S.yaw) * 0.55f);
            // starting inside a wall (hugging cover): the wave is born where the wall is and bursts there
            var body = new Vector3(a.pos.x, pos.y, a.pos.z);
            if (!G.physics.los(body, pos)) pos = body;
            var col = G.teamColors[a.team];
            WAVES.Add(new WaveRec { owner = a, team = a.team, pos = pos, prev = pos, dir = dir, sp = S.sp, col = col, fx = BladeFX.waveMesh(col), w = w });
            emit("weapon:fire", new Ev { actor = a, id = w.id, kind = "charged", muzzle = pos, dir = dir });
        }
        static void endWave(WaveRec Wv, Vector3? at, Vector3 normal)
        {
            Wv.dying = 0.14f;
            var col = Wv.col;
            if (at.HasValue)
            {
                Vector3 p = at.Value;
                credit(Wv.owner, G.paint.splat(p + normal * 0.1f, Wv.w.waveEndRadius, Wv.team, new SplatOpts { seed = random() }), Wv.sp);
                emit("weapon:impact", new Ev { pos = p, normal = normal, team = Wv.team, kind = "drop", radius = Wv.w.waveEndRadius });
                if ((G.camPos - p).sqrMagnitude < 30 * 30)
                {
                    G.fx?.burst(p, normal, col, new FxOpts { count = 16, speed = 5, size = 0.1f, mist = true });
                    G.audio?.play("splat_big", new AudioOpts { pos = p, volume = 0.5f });
                }
            }
            else
            {
                // spent: the wave slumps into a spatter of drops that paint where they land
                var g = G.physics.raycast(Wv.pos, DOWN, 3, _hit2, true);
                if (g.hit) credit(Wv.owner, G.paint.splat(g.point + g.normal * 0.1f, Wv.w.waveEndRadius * 0.85f, Wv.team, new SplatOpts { seed = random(), stretch = new Vector3(Wv.dir.x, 0, Wv.dir.z).normalized, stretchAmt = 0.6f }), Wv.sp);
                if (G.fx != null && (G.camPos - Wv.pos).sqrMagnitude < 30 * 30)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        var p = Wv.pos; p.y += (random() - 0.5f) * 1.2f;
                        var v = Wv.dir * (3 + random() * 3); v.y = random() * 1.5f;
                        G.fx.drop(p, v, col, new FxOpts { size = 0.05f + random() * 0.04f, life = 0.8f, paint = false });
                    }
                }
            }
        }
        static void stepWave(WaveRec Wv, int i, float dt)
        {
            var w = Wv.w;
            Wv.t += dt;
            if (Wv.dying >= 0)
            {
                Wv.dying -= dt;
                if (Wv.fx != null) Wv.fx.fade = Mathf.Max(0, Wv.dying / 0.14f);
                if (Wv.dying <= 0) { BladeFX.freeWave(Wv.fx); WAVES.RemoveAt(i); }
                else placeWave(Wv);
                return;
            }
            float step = w.waveSpeed * dt;
            Wv.prev = Wv.pos;
            Wv.pos += Wv.dir * step;
            Wv.dist += step;
            // enemies inside the crescent's sweep (a tall, narrow slab moving along dir); each is hit once
            float hx = Wv.dir.x, hz = Wv.dir.z, hl = Mathf.Sqrt(hx * hx + hz * hz); if (hl == 0) hl = 1;
            float ux = hx / hl, uz = hz / hl;
            float dmg = lerp(w.waveDamage, w.waveDamageFar, clamp(Wv.dist / w.waveRange, 0, 1));
            foreach (var e in G.actors)
            {
                if (e.team == Wv.team || !e.alive || Wv.hits.Contains(e)) continue;
                float hr = PLAYER.radius, h = e.form == "squid" ? PLAYER.squidHeight : PLAYER.height;
                float ex = e.pos.x - Wv.prev.x, ez = e.pos.z - Wv.prev.z;
                float along = ex * ux + ez * uz;
                if (along < -hr || along > step * hl + hr) continue;
                if (Mathf.Abs(ex * uz - ez * ux) > w.waveWidth * 0.5f + hr) continue;
                float cy = Wv.prev.y + (Wv.pos.y - Wv.prev.y) * clamp(along / Mathf.Max(1e-4f, step * hl), 0, 1);
                float y0 = e.pos.y + e.smoothY;
                if (y0 + h < cy - w.waveHeight * 0.55f || y0 > cy + w.waveHeight * 0.45f) continue;
                Wv.hits.Add(e);
                G.projectiles.applyHit(Wv.owner, e, dmg, "blade");
                if ((G.camPos - e.pos).sqrMagnitude < 30 * 30)
                    G.fx?.burst(new Vector3(e.pos.x, y0 + Mathf.Min(h * 0.6f, 0.9f), e.pos.z), -Wv.dir, Wv.col, new FxOpts { count = 12, speed = 4.5f, size = 0.09f });
            }
            // (Boss Battle: the crescent cuts HULLBREAKER / a crablet once on its way through — M7)
            // shields, curtains, devices and bubbles catch it; walls break it
            bool stop = false;
            if (G.subs.blockShot(Wv.prev, Wv.pos, Wv.team, dmg) || (G.specials?.shotHit(Wv.prev, Wv.pos, Wv.team, dmg, Wv.owner) ?? false)) { endWave(Wv, Wv.pos, -Wv.dir); stop = true; }
            if (!stop)
            {
                var hit = G.physics.segment(Wv.prev, Wv.pos, _hit, true);
                if (hit.hit) { Wv.pos = hit.point; endWave(Wv, hit.point, hit.normal); stop = true; }
            }
            // a straight stripe of ink along its path
            Wv.paintAcc += step;
            if (!stop && Wv.paintAcc >= w.wavePaintEvery)
            {
                Wv.paintAcc = 0;
                var g = G.physics.raycast(Wv.pos, DOWN, w.waveHeight * 0.52f + 1.4f, _hit2, true);
                if (g.hit) credit(Wv.owner, G.paint.splat(g.point + g.normal * 0.08f, w.wavePaintRadius * (0.9f + random() * 0.2f), Wv.team, new SplatOpts { seed = random(), stretch = new Vector3(ux, 0, uz), stretchAmt = 0.85f }), Wv.sp);
            }
            if (!stop && Wv.dist >= w.waveRange) { endWave(Wv, null, Vector3.zero); stop = true; }
            // spray off the crescent (FX)
            if (!stop && G.fx != null && random() < 0.6f && (G.camPos - Wv.pos).sqrMagnitude < 28 * 28)
            {
                var p = Wv.pos; p.y += (random() - 0.5f) * w.waveHeight * 0.8f;
                G.fx.drop(p, new Vector3(Wv.dir.x * w.waveSpeed * 0.25f + (random() - 0.5f) * 2, 0.5f + random(), Wv.dir.z * w.waveSpeed * 0.25f + (random() - 0.5f) * 2), Wv.col, new FxOpts { size = 0.035f + random() * 0.03f, life = 0.5f });
            }
            placeWave(Wv);
        }
        static void placeWave(WaveRec Wv)
        {
            if (Wv.fx == null) return;
            var w = Wv.w;
            Vector3 z = Wv.dir.normalized;
            Vector3 x = Vector3.Cross(UP, z); if (x.sqrMagnitude < 1e-6f) x = new Vector3(1, 0, 0); x.Normalize();
            Vector3 y = Vector3.Cross(z, x).normalized;
            float grow = Mathf.Min(1, 0.55f + Wv.t * 6);
            Wv.fx.m = Matrix4x4.TRS(Wv.pos, Quaternion.LookRotation(z, y), new Vector3(w.waveWidth * 1.45f * grow, w.waveHeight * 0.5f * grow, 1.0f + Mathf.Min(1, Wv.dist / 4) * 0.9f));
            Wv.fx.t = Wv.t;
        }

        // ------------------------------------------------------------------------------------------ bots
        // A Cutlass bot fights like an aggressive human swordsman. bot.tactics (botTactics) runs every fight frame, aimed or
        // not, after the brain's defaults (and in its retreat, ctx.retreat), and owns the trigger (tap / hold / release), the
        // swim and the footwork. Bot enemies see every kid in line of sight, so a head-on walk into a shooter is a lost duel:
        // the sword's kills are the enemy busy with someone else, the corner and the ink.
        //   · targets (targetBias): an enemy busy with a teammate before a ranged one that has us spotted from range
        //   · closing in: swim wherever own ink runs toward the target (a steady, unseen swim if it hasn't spotted us);
        //     a busy / unaware target: run in and pre-charge from ~5–8 m so the charge fills on arrival — then cut the moment
        //     it's in blade + lunge reach (the lunge closes the last metre); a ranged target that has us spotted: wait under
        //     in our ink for it to look away, or charge from beyond the droplets' reach (arcing out of its line, behind cover
        //     if there's any) and poke with the wave, then rush the stripe it paints; inside ~6.5 m: quick cuts as we close
        //   · a full charge with a lane ahead: dive and carry it in a swim (hard bots mostly), surface in reach and cut
        //   · up close: a quick-cut combo (two splat), circling them
        //   · no dodge-hop on every hit; hurt: slip out of a shooter's line in our ink; at blade range, finish the fight
        //     (stayIn) instead of turning away; falling back: swim where the way back is ours, run where it isn't
        // SKILL (per difficulty) scales the flank / cover / lurk / dive use, the strafe and the timing.
        static float SKILL(string id) => id == "easy" ? 0.35f : id == "hard" ? 1 : 0.7f;
        static float DIVE(string id) => id == "easy" ? 0 : id == "hard" ? 0.9f : 0.6f;   // chance a full charge is carried in a swim
        static readonly RegionStats _rs = new RegionStats();
        /** brain._bl (+ brain._blHold) */
        class BotState
        {
            public bool press, diveOK, lane, walk; public float hold, full, dive, dove, rush, waveT = 3, side = random() < 0.5f ? -1 : 1;
            public float sideT, flankT, coverT, laneT, walkT, patience = 1, lastD, closeV, square, lurk, blHold = -1;
            public Vector2? flank, cover; public Actor target;
        }
        static BotState botState(BotBrain brain) => brain.kitMem as BotState ?? (BotState)(brain.kitMem = new BotState());
        // own ink along the line (nx, nz) from the kid, as far as d m ahead: a swim lane
        static readonly float[] LANE_K = { 1.2f, 2.6f, 4.2f };
        static bool ownLane(Actor a, float nx, float nz, float d)
        {
            foreach (float k in LANE_K)
            {
                if (k > 1.2f && k > d - 0.6f) break;
                var s = G.paint.regionStats(a.pos.x + nx * k, a.pos.y, a.pos.z + nz * k, 0.8f, a.team, _rs);
                if (s.n == 0 || s.own < 0.5f) return false;
            }
            return true;
        }
        // can the kid walk len m along (ux, uz): no wall at knee height, no water, no drop off a ledge
        static bool walkable(BotBrain brain, Actor a, float ux, float uz, float len)
        {
            float x1 = a.pos.x + ux * len, z1 = a.pos.z + uz * len;
            if (!brain.fatLos(a.pos.x, a.pos.y, a.pos.z, x1, a.pos.y, z1)) return false;
            if (!BotBrain.dryLine(a.pos.x, a.pos.y, a.pos.z, x1, z1)) return false;
            return G.level.groundHeight(x1, z1, a.pos.y + 0.6f) > a.pos.y - 1.4f;
        }
        // heading along the bot's nav path (its look-ahead waypoint); null without one
        static Vector2? pathDir(BotBrain brain, Actor a)
        {
            var p = brain.path; if (p == null || brain.pi >= p.Count) return null;
            int i = brain._laPath == p && brain._laTi >= brain.pi && brain._laTi < p.Count ? brain._laTi : brain.pi;
            var n = G.nav.nodes[p[i]]; float dx = n.x - a.pos.x, dz = n.z - a.pos.z, l = Mathf.Sqrt(dx * dx + dz * dz);
            return l > 0.05f ? new Vector2(dx / l, dz / l) : (Vector2?)null;
        }
        // a spot 1.6–3.2 m away, level with us and reachable on foot, that the target can't see: charge behind it
        static Vector2? coverSpot(BotBrain brain, Actor a, Actor t, float dist)
        {
            var eye = new Vector3(t.pos.x, t.pos.y + 1.3f, t.pos.z);
            Vector2? best = null; float bs = float.NegativeInfinity;
            float bas = random() * Mathf.PI * 2;
            for (int i = 0; i < 10; i++)
            {
                float ang = bas + (i / 10f) * Mathf.PI * 2, r = (i % 2) != 0 ? 1.6f : 3.2f, ux = Mathf.Sin(ang), uz = Mathf.Cos(ang);
                float x = a.pos.x + ux * r, z = a.pos.z + uz * r, gy = G.level.groundHeight(x, z, a.pos.y + 0.6f);
                if (!(Mathf.Abs(gy - a.pos.y) < 0.5f)) continue;
                float dT = Mathf.Sqrt((t.pos.x - x) * (t.pos.x - x) + (t.pos.z - z) * (t.pos.z - z));
                if (dT > dist + 1.5f || dT < 3) continue;                                       // not a retreat, not into their face
                if (G.physics.los(eye, new Vector3(x, gy + 1.0f, z))) continue;                  // they'd still see us there
                if (!walkable(brain, a, ux, uz, r)) continue;
                float sc = -(dT - dist) * 0.6f - r * 0.3f;
                if (sc > bs) { bs = sc; best = new Vector2(x, z); }
            }
            return best;
        }

        static void botTactics(BotBrain brain, BotCtx ctx)
        {
            var a = ctx.a; var w = ctx.w; float dist = ctx.dist, dt = ctx.dt; var it = ctx.it; var t = ctx.target;
            var K = a.weaponRunner.kit as BladeState;
            var B = botState(brain); float sk = SKILL(brain.diff.id);
            if (B.target != t) { B.target = t; B.rush = 0; B.cover = null; B.coverT = 0; B.flank = null; B.flankT = 0; B.patience = lerp(1.3f, 0.5f, sk) + random() * 0.5f; B.lastD = dist; B.closeV = 0; }
            bool charging = K != null && K.charging, full = K != null && K.full;
            B.sideT -= dt; B.rush -= dt; B.coverT -= dt; B.flankT -= dt; B.laneT -= dt; B.walkT -= dt;
            if (charging && B.hold == 0) B.diveOK = random() < DIVE(brain.diff.id);   // decided once per charge
            B.hold = charging ? B.hold + dt : 0;
            B.full = full ? B.full + dt : 0;
            if (!charging) { B.dive = 0; B.dove = 0; B.square = 0; }
            B.closeV += ((B.lastD - dist) / Mathf.Max(dt, 1e-3f) - B.closeV) * (1 - Mathf.Exp(-5 * dt)); B.lastD = dist;   // closing speed
            // --- geometry
            float dx = t.pos.x - a.pos.x, dz = t.pos.z - a.pos.z, dy = t.pos.y + t.smoothY - a.pos.y;
            float nx = dx / Mathf.Max(dist, 0.01f), nz = dz / Mathf.Max(dist, 0.01f);
            float pd = Mathf.Sqrt((dx + t.vel.x * 0.1f) * (dx + t.vel.x * 0.1f) + (dz + t.vel.z * 0.1f) * (dz + t.vel.z * 0.1f));   // where they'll be as the blade lands
            float hitR = PLAYER.radius;
            bool level = dy < 1.5f && dy > -1.1f;                                             // inside the blade's vertical window
            float aimOff = Mathf.Abs(angleDiff(a.aimYaw, Mathf.Atan2(dx, dz)));
            // does the target (a bot) have us spotted? A swimmer it hasn't spotted stays unseen if it keeps under ~7 m/s
            bool spotted = t.bot == null || (t.bot.target == a && t.bot.seeTimer > 0);
            // their sights: looking our way (a bot that has us spotted: a wider cone), and do they outrange us?
            var tw = t.weapon; bool ranged = tw != null && !BotBrain.MELEE.Contains(tw.kind) && Config.weaponRange(tw) > w.range + 1;
            float facing = -(Mathf.Sin(t.aimYaw) * nx + Mathf.Cos(t.aimYaw) * nz);
            bool sighted = ctx.visible && facing > (t.bot != null && spotted ? 0.6f : 0.85f);
            bool exposed = sighted && ranged;
            bool lane()
            {
                if (B.laneT <= 0) { B.laneT = 0.15f; B.lane = a.groundTeam == 1 && ownLane(a, nx, nz, dist); }
                return B.lane;
            }
            bool kidOK = a.form != "squid" && a.kidT >= PLAYER.emergeDelay + 0.02f;   // fully out of the ink (can cut)
            if (ctx.retreat)
            {
                // falling back to heal (the brain's retreat path): cornered at blade range → cut; else swim where the way back
                // is our ink (fast, and out of sight), run where it isn't (a squid on dry ground crawls); a charge is kept
                float reach = w.reach + hitR + (a.grounded ? w.lungeDist * 0.6f : 0);
                if (full && kidOK && level && pd <= reach && aimOff < 0.4f) { it.fire = false; B.blHold = -1; it.squid = false; return; }
                if (!charging && dist < 2.3f && level && ctx.visible && aimOff < 0.75f && a.ink >= w.tapInk) { it.squid = false; it.fire = B.press = !B.press; B.blHold = -1; return; }
                float ml = Mathf.Sqrt(ctx.move.x * ctx.move.x + ctx.move.z * ctx.move.z);
                it.squid = a.groundTeam == 1 && (ml < 0.1f || ownLane(a, ctx.move.x / ml, ctx.move.z / ml, 2.4f));
                it.fire = charging; B.blHold = charging ? G.time : -1;
                return;
            }
            // footwork toward them: the nav path (straight when the way is open and short), arcing out of a ranged enemy's
            // sightline on the side we're already on. null: no path and not close — leave the brain's move alone
            Vector2? approach()
            {
                float ux = nx, uz = nz;
                var pdir = pathDir(brain, a);
                if (pdir == null && dist > 7) return null;
                if (pdir != null)
                {
                    if (dist < 6 && ctx.visible) { if (B.walkT <= 0) { B.walkT = 0.12f; B.walk = walkable(brain, a, nx, nz, Mathf.Min(dist - 0.8f, 3)); } }
                    else B.walk = false;
                    if (!B.walk) { ux = pdir.Value.x; uz = pdir.Value.y; }
                }
                if (exposed && dist > 3.2f && sk >= 0.5f)
                {
                    if (B.flankT <= 0)
                    {
                        B.flankT = 0.2f; B.flank = null;
                        // turn the heading off the line to them by up to ~30–50° (skill), toward the side away from their aim:
                        // a spiral in (a turning tracker keeps us sighted, so the arc keeps going) — tighter if a wall / the sea
                        float fx = Mathf.Sin(t.aimYaw), fz = Mathf.Cos(t.aimYaw);
                        float f = fx * -nz + fz * nx;                                            // their aim across the line to us
                        float s = Mathf.Abs(f) < 0.05f ? B.side : f > 0 ? -1 : 1;
                        float th0 = lerp(0.35f, 0.9f, sk);
                        foreach (float k in new[] { 1, 0.6f, 0.3f })
                        {
                            float th = -th0 * k * s, c = Mathf.Cos(th), sn = Mathf.Sin(th);       // s = +1: toward (-nz, nx)
                            float mx = ux * c + uz * sn, mz = -ux * sn + uz * c;
                            if (walkable(brain, a, mx, mz, 2.2f)) { B.flank = new Vector2(mx, mz); break; }
                        }
                    }
                    if (B.flank != null) { ux = B.flank.Value.x; uz = B.flank.Value.y; }
                }
                return new Vector2(ux, uz);
            }
            bool fire = false, hold = false, squid = false; Vector2? mv = null;

            if (brain.blockT > 0.15f)
            {
                // an enemy canopy in the way: the brain flanks round it (shieldFight); keep a charge for the opening, no cuts into it
                it.fire = charging; B.blHold = charging ? G.time : -1; it.squid = false;
                if (it.jump && !(a.hp < 45 && !charging)) it.jump = false;
                return;
            }
            if (charging)
            {
                // ================= holding a charge
                hold = true;
                // (released during the pop-out the charge would be lost: kidOK — the kid must be fully out of the ink)
                bool sees = ctx.visible || dist < 2.2f;
                bool near = full && pd < 3.4f;
                float cutR = w.reach + hitR - 0.05f + (near && a.grounded && lungeSafe(a, nx, nz, w.lungeDist) ? w.lungeDist * 0.7f : 0);
                float eta = (pd - cutR) / w.moveSpeedCharging;                               // walking in with it held
                if (full && kidOK && level && sees && pd <= cutR && aimOff < 0.32f)
                {
                    hold = false; mv = new Vector2(nx, nz);                                    // in blade + lunge reach: cut
                }
                else if (full && kidOK && B.dove == 0 && B.diveOK && pd > cutR + 1.4f && dist < 10 && level && lane() && walkable(brain, a, nx, nz, Mathf.Min(dist - 1, 4)))
                {
                    B.dove = 1; B.dive = 0;                                                    // dive with the charge stored
                }
                else if (full && kidOK && sees && dist > cutR + 0.4f && dist < w.waveRange * 0.9f
                    && ((exposed && dist > 4.2f) || !level || (B.full > B.patience && eta > 0.5f && B.closeV < 1.5f) || (a.hp < 40 && dist > 4)))
                {
                    // poke with the wave (then rush the stripe it paints): square up first — the wave is narrow, the arc skews the aim
                    B.square += dt;
                    if (aimOff < Mathf.Atan2(0.45f, dist) || B.square > 0.45f) { hold = false; B.rush = 1.5f; B.square = 0; }
                    mv = new Vector2(nx * 0.3f, nz * 0.3f);
                }
                else if (B.hold > 3.2f || (!full && dist < w.reach + hitR && K.charge < 0.65f))
                {
                    hold = false;                                                              // never got there / they rushed us
                }
                if (hold && B.dove == 1)
                {
                    // swimming in with the charge: surface short of reach (the pop-out takes a moment), or before the store runs out
                    B.dive += dt;
                    squid = B.dive < 0.8f && pd > cutR + 0.9f && lane();
                    if (!squid) B.dove = 2;
                    mv = new Vector2(nx, nz);
                }
                else if (hold)
                {
                    // walking in with the charge: charge behind cover while they have us in their sights
                    if (!full && exposed && sk >= 0.5f && dist > 4.5f)
                    {
                        if (B.coverT <= 0) { B.coverT = 0.6f; B.cover = random() < sk ? coverSpot(brain, a, t, dist) : null; }
                        if (B.cover != null) { float cx = B.cover.Value.x - a.pos.x, cz = B.cover.Value.y - a.pos.z, cl = Mathf.Sqrt(cx * cx + cz * cz); mv = cl > 0.4f ? new Vector2(cx / cl, cz / cl) : Vector2.zero; }
                    }
                    if (mv == null) mv = approach();
                }
            }
            else if (dist <= 3.1f && level)
            {
                // ================= up close: a quick-cut combo, circling them (the blade's arc is ±75°: no need for a fine aim;
                // the circle is slow enough, ~2 rad/s, that our own aim keeps them in front)
                fire = ctx.visible && brain.react <= 0 && aimOff < 0.75f && a.ink >= w.tapInk && (B.press = !B.press);
                if (B.sideT <= 0)
                {
                    B.sideT = lerp(1.0f, 0.45f, sk) + random() * 0.5f;
                    if (random() < 0.55f) B.side = -B.side;
                    if (!walkable(brain, a, -nz * B.side, nx * B.side, 1.2f)) B.side = -B.side;
                }
                float rad = clamp((dist - 1.45f) * 1.3f, -0.6f, 0.9f), amp = lerp(0.3f, 0.55f, sk);
                mv = new Vector2(-nz * B.side * amp + nx * rad, nx * B.side * amp + nz * rad);
            }
            else if (a.hp < 50 && t.hp > 40 && exposed && dist > 5 && sk >= 0.5f)
            {
                // ================= hurt, and a ranged enemy has us spotted out of reach: fall back to heal in our ink (the
                // brain's retreat: own ink away from it, out of its line; ctx.retreat runs it) instead of walking into its fire
                brain.mode = "retreat"; brain.retreatT = 1.6f + random() * 1.2f; brain.repath = 0; brain.pickRetreat();
                squid = a.groundTeam == 1;
            }
            else
            {
                // ================= closing in without a charge: swim the lane if there is one (straight down it after a wave);
                // a target busy with someone else: run in and pre-charge from ~5–8 m (it fills as we arrive: a one-shot
                // surprise); a ranged one that has us spotted: charge for the wave poke from beyond the droplets' reach (or
                // wait in our ink for it to look away); inside the droplets' reach: quick cuts as we close
                bool busyHeavy = K != null && K.swing != null && K.swing.heavy;
                mv = B.rush > 0 && (busyHeavy || lane()) ? new Vector2(nx, nz) : approach();
                bool canCharge = B.rush <= 0 && !busyHeavy && brain.react <= 0 && a.ink >= w.heavyInk + 2;
                float surprise = w.reach + hitR + w.lungeDist * 0.7f + (w.chargeDelay + w.chargeTime) * w.moveSpeedCharging;   // ≈ 6 m
                if (lane() && dist > 3.4f)
                {
                    squid = true;
                    if (!spotted && dist < 10 && mv != null) mv = mv.Value * 0.55f;            // unspotted: a steady swim stays unseen
                }
                else if (exposed && sk >= 0.5f && a.groundTeam == 1 && dist > 5 && B.rush <= 0 && B.lurk < lerp(0.8f, 1.6f, sk))
                {
                    // a ranged enemy has us in its sights and there's no lane to it: stay under in our ink until it looks away
                    // (it loses a still swimmer), rather than charging in the open
                    squid = true; B.lurk += dt; mv = Vector2.zero; brain.noProg = 0;
                }
                else if (canCharge && (exposed ? dist > w.range && dist < 12 : dist > surprise - 0.8f && dist < surprise + 2)) hold = true;
                else fire = ctx.canFire && dist < w.range && (B.press = !B.press);
            }
            if (!squid || !exposed) B.lurk = Mathf.Max(0, B.lurk - dt * 0.4f);
            // no dodge-hop on every hit: only when hurt and not about to cut
            if (it.jump && !(a.hp < 45 && !charging && dist > 3.2f)) it.jump = false;
            it.fire = fire || hold;
            B.blHold = hold ? G.time : -1;
            it.squid = squid;
            if (mv != null)
            {
                float l = mv.Value.magnitude, k = l > 1 ? 1 / l : 1;                        // (shorter than 1: a slower step)
                ctx.move = l > 0.01f ? new Vector3(mv.Value.x * k, 0, mv.Value.y * k) : Vector3.zero;
                // steering our own way (arcs, circling, cover): the waypoint-progress watchdog mustn't read it as stuck
                if (Mathf.Sqrt(a.vel.x * a.vel.x + a.vel.z * a.vel.z) > 1.5f) brain.noProg = 0;
            }
        }
        // low on health in a fight at blade range (or charged and nearly there): finish it rather than turn and run
        static bool botStayIn(BotBrain brain)
        {
            var a = brain.actor; var t = brain.target; if (t == null) return false;
            float d = Mathf.Sqrt((t.pos.x - a.pos.x) * (t.pos.x - a.pos.x) + (t.pos.z - a.pos.z) * (t.pos.z - a.pos.z));
            var K = a.weaponRunner.kit as BladeState;
            return Mathf.Abs(t.pos.y - a.pos.y) < 1.4f && (d < 4.5f || (K != null && K.full && d < 5.5f));
        }
        // target choice (the brain's perceive adds this to the distance score; lower is preferred): an enemy busy with one of
        // our team is the kill a sword gets; a ranged one that has us spotted from beyond the droplets' reach is the duel it
        // loses — another enemy within ~4 m of it is taken first
        static float botTargetBias(BotBrain brain, Actor e, float d)
        {
            var eb = e.bot; var a = brain.actor;
            float b = e.hp <= 50 && d < 12 ? -2 : 0;                                                  // a hurt one: finish it
            if (eb == null) return b;
            if (eb.target != null && eb.target != a && eb.target.team == a.team && eb.seeTimer > 0 && d < 14) b -= 4;
            else if (eb.target == a && eb.seeTimer > 0 && d > a.weapon.range && !BotBrain.MELEE.Contains(e.weapon.kind)) b += 4;
            return b;
        }
        static bool botPaint(BotBrain brain, BotCtx ctx)
        {
            var a = ctx.a;
            var K = a.weaponRunner.kit as BladeState;
            var B = botState(brain);
            B.waveT -= ctx.dt;
            if (K != null && K.charging)
            {
                if (K.full) { B.blHold = -1; B.waveT = 3.5f + random() * 3; return false; }   // loose a paint wave down the lane
                B.blHold = G.time; return true;
            }
            B.blHold = -1;
            // now and then, with a long way to go and ink to spare: charge a wave to paint a long stripe ahead
            if (B.waveT <= 0 && ctx.wantMove && ctx.inkFrac > 0.45f && ctx.needPaint && brain.pathRemaining() > 9) { B.blHold = G.time; return true; }
            // quick cuts while moving, but keep a reserve so a fight doesn't start on an empty tank
            if (ctx.needPaint && ctx.inkFrac > 0.25f && ctx.wantMove) { B.press = !B.press; return B.press; }
            return false;
        }

        // ------------------------------------------------------------------------------------------ the kit
        public Blade()
        {
            bot = new KitBot { paintPitch = -0.2f, tactics = botTactics, stayIn = botStayIn, targetBias = botTargetBias, paint = botPaint, melee = true };
            BotBrain.MELEE.Add("blade");
        }
        public override void update(WeaponRunner R, float dt, WeaponInput inp, WeaponDef w)
        {
            var a = R.a; var K = st(R);
            if (G.time - K.lastT > 0.3f) { cancelCharge(R, K); K.held = false; K.swing = null; K.lunge = 0; K.queued = 0; }   // resumed after a special took the trigger
            K.lastT = G.time;
            K.cd -= dt; K.since += dt; K.flashT += dt; K.queued = Mathf.Max(0, K.queued - dt);
            if (K.swing != null) runSwing(R, K, dt, w);
            bool raw = a.intent.fire;
            bool canAct = a.form != "squid" && a.kidT >= PLAYER.emergeDelay && a.alive;
            if (!canAct)
            {
                // squid / emerging: a charge keeps while the trigger stays held (briefly), and is lost once it's let go
                if (K.charging) { K.store += dt; a.lastFire = 0; if (!raw || K.store > w.chargeStore) cancelCharge(R, K); }
                K.held = K.held && raw;
                R.charge = K.charge;
                return;
            }
            K.store = 0;
            bool auto = coreDriven(a);
            bool fire = inp.fire;
            if (!fire && K.charging && botHolds(a)) fire = true;
            // press: a quick cut right away (mashing faster than the swing buffers the next one)
            if (inp.firePressed || (auto && fire && K.cd <= 0))
            {
                if (K.cd <= 0) tap(R, K, w); else K.queued = 0.16f;
                K.held = true; K.holdT = 0;
            }
            else if (K.queued > 0 && K.cd <= 0 && !K.charging) { K.queued = 0; tap(R, K, w); }
            if (fire && K.held)
            {
                K.holdT += dt;
                // held past the cut: the blade rises and charges
                if (!auto && !K.charging && K.holdT >= w.chargeDelay && !(K.swing != null && K.swing.heavy))
                {
                    K.charging = true; K.charge = 0; K.full = false;
                    if (heard(a)) { stopHum(R); HUMS[R] = G.audio?.loop("blade_charge", new AudioOpts { pos = sndPos(a), volume = a.isLocal ? 0.5f : 0.32f, pitch = 1 }); }
                }
                if (K.charging)
                {
                    float cap = a.ink >= w.heavyInk ? 1 : 0.94f;              // not enough ink for the heavy cut: it never completes
                    K.charge = Mathf.Min(cap, K.charge + dt / w.chargeTime);
                    if (K.charge >= 1 && !K.full)
                    {
                        K.full = true;
                        if (heard(a)) G.audio?.play("blade_ready", new AudioOpts { pos = sndPos(a), volume = a.isLocal ? 0.6f : 0.4f });
                        Actor.rumble(a, 0.06f, 0.3f, 60);
                    }
                    if (cap < 1 && K.charge >= cap && !K.lowWarned && a.isLocal) { K.lowWarned = true; R.empty(); }
                    if (HUMS.TryGetValue(R, out var hum)) hum?.set(pitch: 1 + 0.6f * K.charge + (K.full ? 0.02f * Mathf.Sin(G.time * 40) : 0), pos: sndPos(a));
                    a.fireFacing = Mathf.Max(a.fireFacing, 0.35f); R.firingT = Mathf.Max(R.firingT, 0.3f); a.lastFire = 0;   // no idle refill mid-charge
                }
            }
            else if (K.held && !fire)
            {
                // release
                K.held = false; K.lowWarned = false;
                if (K.charging)
                {
                    float c = K.charge;
                    cancelCharge(R, K);
                    if (c >= 1) heavy(R, K, w);
                    else if (c >= 0.3f) { if (K.cd <= 0.04f) { K.cd = 0; tap(R, K, w); } else K.queued = 0.16f; }
                }
            }
            R.charge = K.charge;
        }
        public override void reset(WeaponRunner R) { stopHum(R); }
        // (a charge doesn't make the runner busy: you can dive with it held — see chargeStore — and swim on)
        public override bool busy(WeaponRunner R) => R.kit is BladeState K && K.swing != null;
        public override bool firingPose(WeaponRunner R) => R.kit is BladeState K && (K.charging || (K.swing != null && K.swing.t < 0.3f));
        public override float moveSpeed(WeaponRunner R, WeaponDef w)
        {
            if (!(R.kit is BladeState K)) return 0;
            if (K.swing != null && K.swing.heavy && K.swing.t < 0.3f) return w.moveSpeedFiring * 0.55f;   // planted after the lunge
            if (K.charging) return w.moveSpeedCharging;
            if (R.firingT > 0) return w.moveSpeedFiring;
            return 0;
        }
        public override void tick(float dt)
        {
            for (int i = WAVES.Count - 1; i >= 0; i--) if (i < WAVES.Count) stepWave(WAVES[i], i, dt);
            BladeFX.tick(dt);
            _hk.Clear();
            foreach (var R in HUMS.Keys) { var K = R.kit as BladeState; if (K == null || !K.charging || R.a.weapon?.kind != "blade" || !R.a.alive) _hk.Add(R); }
            foreach (var R in _hk) stopHum(R);
        }
        public override void clear()
        {
            foreach (var Wv in WAVES) BladeFX.freeWave(Wv.fx);
            WAVES.Clear();
            BladeFX.clear();
            _hk.Clear(); _hk.AddRange(HUMS.Keys);
            foreach (var R in _hk) stopHum(R);
        }
    }
}
