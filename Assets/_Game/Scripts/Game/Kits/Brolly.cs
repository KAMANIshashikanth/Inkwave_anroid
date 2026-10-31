// Port of src/game/kits/brolly.js — Canopy Brolly (main weapon, kind 'brolly'); see Kits.cs for the hooks. Model + part
// animation in BrollyModel.cs (kits/brolly-model.js); tuning in Config.WEAPONS["brolly"].
//
// Fire: each trigger pull fires a narrow cone of ink pellets (core projectiles via fireCustom, damage 0 there). The
//   kit scores pellet hits itself in tick(): it replays the core's integrator + hit test one frame ahead, so a blast's
//   pellets that land together sum into ONE hit per victim (one damage number / hit sound) with per-pellet distance
//   falloff. Holding the trigger with no canopy (broken / launched) repeats the blast at the fire rate.
// Canopy: keep holding after the shot and it unfolds into a shield in front of you (you move slower). It blocks
//   enemy shots, beams and ink (tick pre-scans every projectile's next step, so a shot never slips through to the
//   body behind; blockShot / blockRay cover other callers), soaks bomb blasts (bomb:explode), shows tears + flicker
//   as it wears, breaks at 0 hp and regrows after regrowTime. Held open for launchHold s it LAUNCHES: detaches and
//   slides along the ground as a wall (inks under itself, blocks enemy shots and enemy players, lets allies and
//   their shots through), fading after launchLife s or when shot apart. No canopy until it regrows.
//   Bomb blasts rip into canopies in their radius, and a canopy between a blast and a player on its team takes the
//   blast for them: every bomb emits bomb:explode just before its damage loop, so the kit gives the players it covers
//   a one-frame guard (invuln) for that loop (there is no per-player damage-filter hook for kits).
// Runner state lives in runner.kit (a BrollyState); the HUD reticle reads hp / hpMax / state / open / launchK /
//   regrowK straight off it. Not ported: the online ghosts (netState / netApply / ghost), the Boss Battle pellet
//   scoring (M7), the procedural sounds (M5: the names are played through G.audio).
using System.Collections.Generic;
using Inkwave.World;
using UnityEngine;
using static Inkwave.Ctx;

namespace Inkwave
{
    /** hp + hit feedback shared by a held canopy (the runner's kit) and a launched one */
    public class CanopyHp { public float hp, hpMax, lastHit = -9, flash, shake, sndT = -9; }

    /** runner.kit while the Brolly is held (kit.brolly) */
    public class BrollyState : CanopyHp
    {
        public string state = "ready";   // ready · broken · launched
        public float regrowT, regrowK, grow = 1, open, pressT, openT, launchK, lastUpd;
        public bool opening, want, shot, prevFire, lockRelease;
        public Brolly.Shield sh;
    }

    public class Brolly : MainKit
    {
        const float DEG = Mathf.PI / 180;
        static readonly Vector3 UP = new Vector3(0, 1, 0), DOWN = new Vector3(0, -1, 0);
        static readonly Hit _hit = new Hit();
        static WeaponDef W => Config.WEAPONS["brolly"];

        // held canopy (logical shield): a disc facing the aim, centred ahead of the chest (matches the model in the aim pose)
        const float HELD_UP = 0.9f, HELD_FWD = 0.62f, HELD_R = 0.8f, HELD_PMIN = -0.5f, HELD_PMAX = 0.45f;
        // launched canopy: crown this high over the ground; the blocking plane sits half the dome's depth behind the crown
        const float LAUNCH_H = 0.76f, LAUNCH_R = 0.8f;
        // share of a projectile's damage the canopy takes, by type (flick / swipe / sprinkler drops are sprays; bucket blobs volleys)
        static float canopyMul(string type) => type == "drop" ? 0.25f : type == "slosh" ? 0.5f : 1;

        // ------------------------------------------------------------------------------------------ runner state
        public static BrollyState kitOf(WeaponRunner r)
        {
            if (r.kit is BrollyState k) return k;
            var w = W;
            k = new BrollyState { hp = w.canopyHp, hpMax = w.canopyHp, lastUpd = G.time };
            k.sh = new Shield { held = true, kit = k, owner = r.a, team = r.a.team, R = HELD_R };
            r.kit = k;
            return k;
        }

        // pellet damage by distance travelled
        public static float pelletDamage(float d)
        {
            var w = W;
            if (d <= w.falloffStart) return w.pelletDamage;
            return lerp(w.pelletDamage, w.pelletDamageFar, clamp((d - w.falloffStart) / (w.falloffEnd - w.falloffStart), 0, 1));
        }

        // ------------------------------------------------------------------------------------------ firing
        class Pellet { public Projectile p; public int id, blast; public Actor owner; }
        static readonly List<Pellet> PELLETS = new List<Pellet>();   // live pellets this kit scores
        static int SEQ, BLAST;
        static readonly Look LOOK = new Look(0.068f, 0.55f, 1.1f, 0.06f, 24, 0.2f, 1);
        // pellet pattern (unit cone): centre, an inner ring of 4, an outer ring of 6 — turned randomly per blast, jittered
        static readonly Vector2[] PATTERN = buildPattern();
        static Vector2[] buildPattern()
        {
            var l = new List<Vector2> { new Vector2(0, 0) };
            for (int i = 0; i < 4; i++) l.Add(new Vector2(0.42f, (i / 4f) * Mathf.PI * 2 + 0.4f));
            for (int i = 0; i < 6; i++) l.Add(new Vector2(0.9f, (i / 6f) * Mathf.PI * 2));
            return l.ToArray();
        }

        static bool fireBlast(WeaponRunner r, WeaponDef w)
        {
            var a = r.a; var PJ = G.projectiles;
            if (a.ink < w.inkPerShot) { r.empty(); r.cooldown = Mathf.Max(r.cooldown, 0.2f); return false; }
            a.ink -= w.inkPerShot; a.lastFire = 0;
            r.firingT = 0.35f; a.fireFacing = 0.5f;
            Vector3 m = PJ.muzzle(a);
            Vector3 dir = Projectiles.aimFrom(a, m);
            dir = Projectiles.ballistic(m, dir, a.aimPoint, w.projSpeed, w.straightTime, w.pelletGrav, w.pelletDrag, w.range);
            Vector3 rt = Vector3.Cross(dir, UP); if (rt.sqrMagnitude < 1e-6f) rt = new Vector3(1, 0, 0); rt.Normalize();
            Vector3 u = Vector3.Cross(rt, dir).normalized;
            float cone = Mathf.Tan((a.grounded ? w.spreadDeg : w.spreadAir) * DEG), turn = random() * Mathf.PI * 2;
            int n = Mathf.Min((int)w.pellets, PATTERN.Length);
            BLAST++;
            for (int i = 0; i < n; i++)
            {
                float pr = PATTERN[i].x, pa = PATTERN[i].y;
                float ang = pa + turn, jx = (random() - 0.5f) * 0.18f, jy = (random() - 0.5f) * 0.18f;
                float ox = (pr * Mathf.Cos(ang) + jx) * cone, oy = (pr * Mathf.Sin(ang) + jy) * cone * 0.72f;
                Vector3 v = (dir + rt * ox + u * oy).normalized;
                var lk = LOOK; lk.vis = LOOK.vis * (i == 0 ? 1.25f : 0.85f + random() * 0.3f);
                var p = PJ.fireCustom(a, m, v, new FireOpts
                {
                    // one speed for the whole blast (± a hair): the pellets arrive together, so a blast lands as one hit
                    type = "shot", speed = w.projSpeed * (0.985f + random() * 0.03f), range = w.range, life = w.pelletLife, damage = 0, radius = w.pelletPaint * (0.85f + random() * 0.3f),
                    size = w.pelletSize, grav = w.pelletGrav, drag = w.pelletDrag, straight = w.straightTime, weaponId = "brolly", trailEvery = 0, look = lk,
                });
                p.life = w.pelletLife;
                p._bid = ++SEQ;
                PELLETS.Add(new Pellet { p = p, id = p._bid, owner = a, blast = BLAST });
            }
            if (a.isLocal || a.nearCamera())
            {
                G.audio?.play("brolly_blast", new AudioOpts { pos = a.isLocal ? (Vector3?)null : m, volume = a.isLocal ? 0.7f : 0.5f });
                G.fx?.muzzle(m, dir, a.color, "blaster");
            }
            if (a.isLocal) emit("recoil", new Ev { amount = 0.011f });
            emit("weapon:fire", new Ev { actor = a, id = w.id, muzzle = m, dir = dir });
            a.character.trigger("shoot");
            Actor.rumble(a, 0.22f, 0.3f, 80);
            r.cooldown = w.fireInterval;
            STATS.blasts++;
            return true;
        }

        // ------------------------------------------------------------------------------------------ per-frame trigger
        public override void update(WeaponRunner r, float dt, WeaponInput inp, WeaponDef w)
        {
            var a = r.a; var k = kitOf(r);
            k.lastUpd = G.time;
            var bb = a.bot?.kitMem as BotMem;
            // bots: the canopy only opens when their fight logic asks (bb.holdUntil) — a trigger held to paint, refill or climb
            // repeats blasts instead. A bot holding the canopy keeps it up while its aim settles.
            bool botHold = bb != null && bb.holdUntil > G.time && a.form == "kid" && a.bot.mode == "fight";
            bool fire = inp.fire || botHold;
            bool press = fire && !k.prevFire;
            k.prevFire = fire;
            if (press) { k.pressT = 0; k.shot = false; }
            if (fire) k.pressT += dt; else k.lockRelease = false;
            bool canopy = k.state == "ready" && k.grow > 0.6f && !k.lockRelease;
            bool shieldMode = canopy && (bb == null || botHold);
            bool want = false;
            if (fire && !k.lockRelease)
            {
                if (shieldMode)
                {
                    // tap = one blast; hold = the blast, then the canopy unfolds
                    if (!k.shot && r.cooldown <= 0 && k.open < 0.05f && k.pressT <= w.openDelay + 0.06f) { fireBlast(r, w); k.shot = true; }
                    want = k.pressT >= w.openDelay;
                }
                else if (r.cooldown <= 0) fireBlast(r, w);
            }
            if (!fire && r.cooldown < 0) r.cooldown = 0;
            // unfold / fold
            float was = k.open;
            k.open = want ? Mathf.Min(1, k.open + dt / w.openTime) : Mathf.Max(0, k.open - dt / w.closeTime);
            k.opening = k.open > was || (k.open >= 1 && k.opening);
            if (was <= 0 && k.open > 0) { sound(a, "brolly_open", 0.6f); STATS.opens++; }
            if (k.want && !want && was > 0.4f) sound(a, "brolly_close", 0.5f);
            k.want = want;
            if (k.open > 0) { a.fireFacing = 0.5f; a.lastFire = 0; r.firingT = Mathf.Max(r.firingT, 0.2f); }
            // held fully open → launch
            if (k.open >= 1 && want)
            {
                k.openT += dt;
                if (k.openT >= w.launchHold) launch(r, k, w);
            }
            else k.openT = 0;
            k.launchK = k.state == "ready" ? clamp(k.openT / w.launchHold, 0, 1) : 0;
        }

        static void sound(Actor a, string name, float vol, float pitch = 1)
        {
            if (G.audio == null || !(a.isLocal || a.nearCamera())) return;
            G.audio.play(name, new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = a.isLocal ? vol : vol * 0.7f, pitch = pitch });
        }

        // ------------------------------------------------------------------------------------------ shields
        /** a logical shield: a disc (centre C, facing N, radius R), swept between its pose last frame (Cp, Np) and now */
        public class Shield
        {
            public bool held, prev; public BrollyState kit; public Launched rf; public Actor owner; public int team;
            public Vector3 C, N, Cp, Np; public float R; public View info;
        }
        /** a canopy launched into the world (a sliding wall) */
        public class Launched : CanopyHp
        {
            public bool alive = true, shoving; public Actor owner; public int team;
            public Vector3 pos, dir; public float t, life, vy, paintAcc, h0, fxT, fade;
            public Shield sh; public BrollyModel.Wall mesh;
        }
        public class Stats { public int blasts, opens, blocked, launches, breaks, guarded; public float blockedDmg; }

        static readonly List<Launched> LAUNCHED = new List<Launched>();   // world canopies
        public static readonly Stats STATS = new Stats();                 // (tests / tuning)
        static readonly List<Shield> SH = new List<Shield>();             // active shields this frame (held + launched)
        static float SH_T = -1;                                            // G.time of the last refresh (blockShot / blockRay reuse it within a frame)

        static void heldPose(Actor a, Shield s)
        {
            float yaw = a.aimYaw, p = clamp(a.aimPitch, HELD_PMIN, HELD_PMAX), cp = Mathf.Cos(p);
            s.N = new Vector3(Mathf.Sin(yaw) * cp, Mathf.Sin(p), Mathf.Cos(yaw) * cp);
            s.C = new Vector3(a.pos.x, a.pos.y + a.smoothY + HELD_UP, a.pos.z) + s.N * HELD_FWD;
            s.team = a.team; s.owner = a;
        }
        public static List<Shield> refreshShields()
        {
            SH.Clear(); SH_T = G.time;
            foreach (var a in G.actors)
            {
                if (a.weapon?.kind != "brolly") continue;
                if (!(a.weaponRunner?.kit is BrollyState k)) continue;
                if (!a.alive || a.form != "kid" || k.state != "ready" || k.open < 0.55f || k.grow < 0.9f) { k.sh.prev = false; continue; }
                heldPose(a, k.sh); SH.Add(k.sh);
            }
            foreach (var c in LAUNCHED) if (c.alive) SH.Add(c.sh);
            foreach (var s in SH) if (!s.prev) { s.Cp = s.C; s.Np = s.N; s.prev = true; }
            return SH;
        }
        // end of the frame: this pose becomes the swept test's start pose
        static void snapShields() { foreach (var s in SH) { s.Cp = s.C; s.Np = s.N; } }
        // segment prev→pos through shield s, either direction. Swept: prev is measured against the shield's pose at the
        // start of the frame and pos against its pose now, so a canopy moving / turning onto a shot can't skip past it.
        static bool segShield(Vector3 prev, Vector3 pos, Shield s, out Vector3 o)
        {
            float a = Vector3.Dot(prev - s.Cp, s.Np), b = Vector3.Dot(pos - s.C, s.N);
            o = default;
            if ((a > 0) == (b > 0) || a == b) return false;
            o = Vector3.LerpUnclamped(prev, pos, a / (a - b));
            return (o - s.C).sqrMagnitude < s.R * s.R;
        }
        // hp / flash / sound state: the runner's kit for a held canopy, the canopy record for a launched one
        static CanopyHp hpOf(Shield s) => s.held ? (CanopyHp)s.kit : s.rf;
        static void hitShield(Shield s, Vector3 at, float dmg, Color color, Vector3? from)
        {
            var o = hpOf(s);
            if (o.hp <= 0 || !(dmg > 0)) return;
            o.hp -= dmg; o.lastHit = G.time; STATS.blocked++; STATS.blockedDmg += dmg;
            o.flash = Mathf.Min(1, o.flash + 0.3f + dmg / 140);
            o.shake = Mathf.Min(1, o.shake + 0.25f + dmg / 120);
            if ((G.camPos - at).sqrMagnitude < 26 * 26)
            {
                // ink splashing off the canopy (toward the side it came from)
                Vector3 n = s.N; if (from.HasValue && Vector3.Dot(from.Value - s.C, s.N) < 0) n = -n;
                G.fx?.burst(at, n, color, new FxOpts { count = dmg > 40 ? 9 : 4, speed = dmg > 40 ? 3.4f : 2.4f, size = dmg > 40 ? 0.085f : 0.065f });
                if (G.time - o.sndT > 0.07f)
                {
                    o.sndT = G.time;
                    bool local = s.owner != null && s.owner.isLocal && s.held;
                    G.audio?.play("brolly_hit", new AudioOpts { pos = local ? (Vector3?)null : at, volume = clamp(0.35f + dmg / 90, 0.35f, 0.9f) * (local ? 0.8f : 0.65f), pitch = dmg > 60 ? 0.8f : 1 });
                }
            }
            if (s.held && s.owner != null && s.owner.isLocal) Actor.rumble(s.owner, 0.05f + Mathf.Min(0.3f, dmg / 300), 0.12f, 50);
            if (o.hp <= 0) breakShield(s);
        }
        static void breakShield(Shield s)
        {
            var o = hpOf(s); var w = W;
            o.hp = 0; STATS.breaks++;
            Color col = G.teamColors[s.team];
            if ((G.camPos - s.C).sqrMagnitude < 40 * 40)
            {
                G.fx?.burst(s.C, s.N, col, new FxOpts { count = 22, speed = 5.2f, size = 0.11f, spread = 1.6f });
                G.fx?.burst(s.C, -s.N, hex("#f4efe4"), new FxOpts { count = 10, speed = 3.5f, size = 0.08f, spread = 1.4f });
                G.fx?.ring(s.C, s.N, col, new FxOpts { radius = BrollyModel.RIM_R * 1.2f, life = 0.3f, snap = false });
                G.audio?.play("brolly_break", new AudioOpts { pos = s.held && s.owner != null && s.owner.isLocal ? (Vector3?)null : s.C, volume = 0.85f });
            }
            if (s.held)
            {
                var k = s.kit;
                k.state = "broken"; k.regrowT = w.regrowTime; k.regrowK = 0; k.open = 0; k.grow = 0; k.openT = 0; k.lockRelease = true;
                if (s.owner != null && s.owner.isLocal) { Actor.rumble(s.owner, 0.45f, 0.4f, 160); emit("shake", new Ev { amount = 0.25f }); }
            }
            else s.rf.alive = false;
            emit("brolly:break", new Ev { actor = s.owner, pos = s.C, team = s.team, launched = !s.held });
        }

        // damage a projectile deals to a canopy
        static float projDamage(Projectile p)
        {
            if (p._bid != 0 && p.weaponId == "brolly") return pelletDamage(Vector3.Distance(p.start, p.pos));
            float d = p.damage > 0 ? p.damage : 10;
            if (p.type == "drop") d = lerp(d, p.dmgFar, clamp(Vector3.Distance(p.start, p.pos) / 7, 0, 1));
            return d * canopyMul(p.type);
        }
        // where a core projectile will be after this frame's step (the exact integrator Projectiles.step runs next)
        static Vector3 predict(Projectile p, float dt)
        {
            float age = p.age + dt; bool g = age > p.straight;
            float vx = p.vel.x, vy = p.vel.y, vz = p.vel.z;
            if (g) vy -= p.grav * dt;
            if (p.drag != 0) { float f = 1 - p.drag * dt * (g ? 1 : 0); vx *= f; vy *= f; vz *= f; }
            return new Vector3(p.pos.x + vx * dt, p.pos.y + vy * dt, p.pos.z + vz * dt);
        }

        // ------------------------------------------------------------------------------------------ launched canopy
        static void launch(WeaponRunner r, BrollyState k, WeaponDef w)
        {
            var a = r.a;
            float yaw = a.aimYaw; Vector3 dir = new Vector3(Mathf.Sin(yaw), 0, Mathf.Cos(yaw));
            // start where the held canopy was, settle onto the ground just ahead
            var g = G.physics.raycast(new Vector3(a.pos.x + dir.x * 0.9f, a.pos.y + 1.2f, a.pos.z + dir.z * 0.9f), DOWN, 3.5f, _hit, true);
            Vector3 pos = g.hit ? g.point : new Vector3(a.pos.x + dir.x * 0.9f, a.pos.y, a.pos.z + dir.z * 0.9f);
            float hp = Mathf.Max(k.hp, w.canopyHp * 0.5f), h0 = a.pos.y + a.smoothY + HELD_UP - pos.y;
            addLaunched(a, pos, dir, hp, h0);
            STATS.launches++;
            k.state = "launched"; k.regrowT = w.regrowTime; k.regrowK = 0; k.open = 0; k.grow = 0; k.openT = 0; k.launchK = 0; k.lockRelease = true;
            sound(a, "brolly_launch", 0.8f);
            if (a.isLocal) emit("recoil", new Ev { amount = 0.008f });
            Actor.rumble(a, 0.3f, 0.35f, 120);
            emit("brolly:launch", new Ev { actor = a, pos = pos, dir = dir });
        }
        static Launched addLaunched(Actor a, Vector3 pos, Vector3 dir, float hp, float h0)
        {
            var w = W;
            var c = new Launched { owner = a, team = a.team, pos = pos, dir = dir, life = w.launchLife, hp = hp, hpMax = w.canopyHp, h0 = h0 };
            c.sh = new Shield { held = false, rf = c, owner = a, team = a.team, N = dir, Np = dir, R = LAUNCH_R };
            c.mesh = BrollyModel.buildWall(c);
            LAUNCHED.Add(c);
            return c;
        }
        static void disposeLaunched(Launched c) { c.mesh?.dispose(); c.mesh = null; }
        static readonly float[] PROBE_H = { 0.3f, 1.0f };
        static bool updateLaunched(Launched c, float dt)
        {
            c.t += dt;
            var w = W;
            if (c.alive && c.t >= c.life) c.alive = false;
            if (!c.alive)
            {
                // fade out (or collapse, when shot apart)
                c.fade += dt / (c.hp <= 0 ? 0.18f : 0.45f);
                if (c.fade >= 1) { disposeLaunched(c); return false; }
            }
            else
            {
                // slide: stop at walls; ease off while shoving an enemy
                float sp = w.launchSpeed * (c.shoving ? 0.6f : 1) * Mathf.Min(1, 0.35f + c.t * 3);
                c.shoving = false;
                float step = sp * dt;
                bool blocked = false;
                foreach (float hgt in PROBE_H)
                {
                    Vector3 o = c.pos; o.y += hgt;
                    if (G.physics.raycast(o, c.dir, 0.12f + step, _hit, true).hit) { blocked = true; break; }
                }
                if (!blocked)
                {
                    c.pos += c.dir * step;
                    // ground: step up small lips, follow slopes down, fall off edges (and into the sea)
                    var g = G.physics.raycast(new Vector3(c.pos.x, c.pos.y + 0.6f, c.pos.z), DOWN, 1.5f, _hit, true);
                    if (g.hit && g.point.y > c.pos.y - 0.12f - Mathf.Max(0, -c.vy) * dt - 0.02f && c.vy <= 0.01f) { c.pos.y = g.point.y; c.vy = 0; }
                    else { c.vy -= 24 * dt; c.pos.y += c.vy * dt; if (g.hit && c.pos.y < g.point.y) { c.pos.y = g.point.y; c.vy = 0; } }
                    if (c.pos.y < PLAYER.waterY - 0.4f) { c.alive = false; c.hp = 0; }
                    // ink the ground under it: a stripe the canopy's width
                    c.paintAcc += step;
                    if (c.paintAcc >= 0.32f && c.vy == 0)
                    {
                        c.paintAcc = 0;
                        float area = G.paint.splat(new Vector3(c.pos.x, c.pos.y + 0.3f, c.pos.z), w.launchPaint, c.team, new SplatOpts { seed = random(), kind = "roll", stretch = c.dir });
                        var ow = c.owner; if (ow != null) { if (ow.specialActive != null) ow.addTurfNoSpecial(area); else ow.addTurf(area); }
                    }
                }
            }
            // blocking shield (crown ahead, plane at mid-depth)
            float hNow = LAUNCH_H + (c.h0 - LAUNCH_H) * Mathf.Max(0, 1 - c.t / 0.22f);
            c.sh.C = new Vector3(c.pos.x, c.pos.y + hNow, c.pos.z) - c.dir * (BrollyModel.DEPTH * 0.5f);
            c.sh.N = c.dir;
            // mesh
            if (c.mesh != null)
            {
                c.flash = Mathf.Max(0, c.flash - dt * 6); c.shake *= Mathf.Exp(-dt * 12);
                c.mesh.update(c, hNow);
                // a spray of ink kicked up at the leading edge
                c.fxT -= dt;
                if (c.alive && c.fxT <= 0 && (G.camPos - c.pos).sqrMagnitude < 28 * 28)
                {
                    c.fxT = 0.22f;
                    G.fx?.burst(c.pos + c.dir * 0.05f, new Vector3(c.dir.x, 0.8f, c.dir.z).normalized, G.teamColors[c.team], new FxOpts { count = 3, speed = 1.8f, size = 0.05f, sheet = false });
                }
            }
            return true;
        }
        // enemy players can't pass a launched canopy: push them back out to the side they're on (allies walk through)
        static void blockActors(Launched c)
        {
            if (!c.alive) return;
            Vector3 N = c.dir; float tx = -N.z, tz = N.x, rr = PLAYER.radius + 0.18f;
            foreach (var e in G.actors)
            {
                if (!e.alive || e.team == c.team) continue;
                float dx = e.pos.x - c.pos.x, dz = e.pos.z - c.pos.z, dy = e.pos.y - c.pos.y;
                if (dy < -1.2f || dy > 1.6f) continue;
                float along = dx * tx + dz * tz;
                if (Mathf.Abs(along) > LAUNCH_R + PLAYER.radius * 0.6f) continue;
                float across = dx * N.x + dz * N.z + BrollyModel.DEPTH * 0.5f;   // from the blocking plane
                if (Mathf.Abs(across) >= rr) continue;
                float side = across >= 0 ? 1 : -1, push = side * rr - across;
                e.pos.x += N.x * push; e.pos.z += N.z * push;
                float vn = e.vel.x * N.x + e.vel.z * N.z;
                if (vn * side < 0) { e.vel.x -= N.x * vn; e.vel.z -= N.z * vn; }
                if (side > 0) c.shoving = true;   // shoving someone along slows it (next frame)
            }
        }

        // ------------------------------------------------------------------------------------------ per-frame (world)
        class Pend { public Actor owner, victim; public float dmg, at; public int n; }
        static readonly Dictionary<int, Pend> PEND = new Dictionary<int, Pend>();
        static readonly HashSet<Projectile> LIVE = new HashSet<Projectile>();
        static readonly List<int> _done = new List<int>();
        const float MERGE = 1 / 30f;

        public override void tick(float dt)
        {
            var w = W;
            // runner-side timers (run even while a special owns the body / the runner is idle)
            foreach (var a in G.actors)
            {
                if (a.weapon?.kind != "brolly") continue;
                if (!(a.weaponRunner?.kit is BrollyState k)) continue;
                if (G.time - k.lastUpd > 0.1f || !a.alive || a.form != "kid") { k.want = false; k.open = Mathf.Max(0, k.open - dt / w.closeTime); k.openT = 0; }
                if (k.state != "ready")
                {
                    k.regrowT -= dt; k.regrowK = clamp(1 - k.regrowT / w.regrowTime, 0, 1);
                    if (k.regrowT <= 0)
                    {
                        k.state = "ready"; k.hp = k.hpMax; k.grow = 0.001f; k.regrowK = 1;
                        if (a.alive)
                        {
                            sound(a, "brolly_regrow", 0.6f);
                            if (a.nearCamera() || a.isLocal) G.fx?.burst(a.character.getMuzzle(), UP, a.color, new FxOpts { count = 6, speed = 1.6f, size = 0.05f, sheet = false });
                        }
                    }
                }
                else
                {
                    if (k.grow < 1) k.grow = Mathf.Min(1, k.grow + dt / 0.32f);
                    if (k.open <= 0 && G.time - k.lastHit > w.canopyRegenDelay) k.hp = Mathf.Min(k.hpMax, k.hp + w.canopyRegen * dt);
                }
                k.flash = Mathf.Max(0, k.flash - dt * 6); k.shake *= Mathf.Exp(-dt * 12);
            }
            // launched canopies move first (their shields must be where they are this frame)
            for (int i = LAUNCHED.Count - 1; i >= 0; i--) if (!updateLaunched(LAUNCHED[i], dt)) LAUNCHED.RemoveAt(i);
            refreshShields();
            // 1) every projectile's next step vs every enemy canopy: blocked shots end on the canopy (before the body behind it)
            var PJ = G.projectiles; var list = PJ.list;
            if (SH.Count > 0)
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (i >= list.Count) continue;
                    var p = list[i];
                    if (p.owner == null || (p.delay > 0 && p.delay - dt > 0)) continue;
                    Vector3 nx = predict(p, dt);
                    foreach (var s in SH)
                    {
                        if (s.team == p.team) continue;
                        if (!segShield(p.pos, nx, s, out var x)) continue;
                        hitShield(s, x, projDamage(p), p.owner.color, p.pos);
                        if (p._bid > 0) p._bid = -p._bid;
                        PJ.removeShot(p);
                        break;
                    }
                }
            }
            // 2) this kit's pellets vs enemy bodies: one summed hit per victim per frame. A pellet that is no longer in the
            // live list died last frame (wall, canopy, curtain …); one that scores here dies in the core pass right after.
            if (PELLETS.Count > 0) { LIVE.Clear(); foreach (var p in list) if (p._bid > 0) LIVE.Add(p); }
            for (int i = PELLETS.Count - 1; i >= 0; i--)
            {
                var e = PELLETS[i]; var p = e.p;
                if (p._bid != e.id || !LIVE.Contains(p)) { PELLETS[i] = PELLETS[PELLETS.Count - 1]; PELLETS.RemoveAt(PELLETS.Count - 1); continue; }
                Vector3 nx = predict(p, dt);
                // (Boss Battle: pellets into HULLBREAKER / a crablet add up per blast the same way — M7)
                foreach (var v in G.actors)
                {
                    if (v.team == p.team || !v.alive) continue;
                    float h = v.form == "squid" ? PLAYER.squidHeight : PLAYER.height, hr = PLAYER.radius;
                    if (Mathf.Abs(v.pos.x - nx.x) > 3 + hr || Mathf.Abs(v.pos.z - nx.z) > 3 + hr) continue;
                    var (dist, t) = Physics.segmentCapsuleDist(p.pos, nx, new Vector3(v.pos.x, v.pos.y + v.smoothY, v.pos.z), hr, h);
                    if (dist < hr * 0.95f + p.size)
                    {
                        Vector3 x = Vector3.LerpUnclamped(p.pos, nx, t);
                        // pellets of one blast reaching the same body within MERGE s land as one hit
                        int key = e.blast * 64 + (G.actors.IndexOf(v) & 63);
                        if (!PEND.TryGetValue(key, out var rec)) PEND[key] = rec = new Pend { owner = e.owner, victim = v, at = G.time + MERGE };
                        rec.dmg += pelletDamage(Vector3.Distance(p.start, x)); rec.n++;
                        p._bid = -p._bid;   // scored: never counted again (the core pass ends it on the same body this frame)
                        break;
                    }
                }
            }
            if (PEND.Count > 0)
            {
                _done.Clear();
                foreach (var kv in PEND) if (G.time >= kv.Value.at - 1e-6f) _done.Add(kv.Key);
                foreach (int key in _done)
                {
                    var rec = PEND[key]; PEND.Remove(key);
                    PJ.applyHit(rec.owner, rec.victim, rec.dmg, "brolly");
                }
            }
            snapShields();
            // 3) launched canopies hold enemy players back
            foreach (var c in LAUNCHED) blockActors(c);
        }

        public override void clear()
        {
            foreach (var c in LAUNCHED) disposeLaunched(c);
            LAUNCHED.Clear(); PELLETS.Clear(); SH.Clear(); PEND.Clear();
        }

        // ------------------------------------------------------------------------------------------ blocking hooks
        public override bool blockShot(Vector3 prev, Vector3 pos, int team, float dmg)
        {
            if (SH_T != G.time) refreshShields();
            if (SH.Count == 0) return false;
            foreach (var s in SH)
            {
                if (s.team == team) continue;
                if (!segShield(prev, pos, s, out var x)) continue;
                hitShield(s, x, dmg, G.teamColors[team], prev);
                return true;
            }
            return false;
        }
        public override float blockRay(Vector3 from, Vector3 dir, float len, int team, float dmg)
        {
            if (SH_T != G.time) refreshShields();
            if (SH.Count == 0) return len;
            float best = len; Shield hit = null;
            foreach (var s in SH)
            {
                if (s.team == team) continue;
                float dn = Vector3.Dot(dir, s.N);
                if (Mathf.Abs(dn) < 1e-4f) continue;
                float t = Vector3.Dot(s.C - from, s.N) / dn;
                if (t <= 0 || t >= best) continue;
                if ((from + dir * t - s.C).sqrMagnitude > s.R * s.R) continue;
                best = t; hit = s;
            }
            if (hit != null) hitShield(hit, from + dir * best, dmg, G.teamColors[team], from);
            return best;
        }
        // bomb blasts: canopies between the blast and players on their team take it for them (a guard for the damage loop
        // that follows this event), and every canopy in the radius is ripped into
        static void onBomb(Ev e)
        {
            if (e == null || G.actors == null) return;
            float R = e.radius > 0 ? e.radius : 3;
            refreshShields();
            if (SH.Count == 0) return;
            foreach (var v in G.actors)
            {
                if (!v.alive || v.team == e.team) continue;
                Vector3 vp = new Vector3(v.pos.x, v.pos.y + v.smoothY + 0.7f, v.pos.z);
                if (Vector3.Distance(vp, e.pos) > R + 0.6f) continue;
                foreach (var s in SH) if (s.team == v.team && segShield(e.pos, vp, s, out _)) { v.invuln = Mathf.Max(v.invuln, 1e-4f); STATS.guarded++; break; }
            }
            foreach (var s in SH.ToArray())
            {
                if (s.team == e.team) continue;
                float d = Mathf.Max(0, Vector3.Distance(e.pos, s.C) - s.R * 0.5f);
                if (d > R) continue;
                float k = 1 - clamp((d - 0.8f) / Mathf.Max(0.1f, R - 0.8f), 0, 1);
                hitShield(s, s.C, lerp(35, 180, k * k), e.team >= 0 ? G.teamColors[e.team] : Color.white, e.pos);
            }
        }

        // ------------------------------------------------------------------------------------------ bots
        /** brain._brolly */
        class BotMem { public float holdUntil, nextShield, advanceUntil; public bool launch; }
        static BotMem mem(BotBrain b) => b.kitMem as BotMem ?? (BotMem)(b.kitMem = new BotMem());
        // an enemy charger / long-range weapon lining up on this bot
        static Actor lineThreat(Actor a)
        {
            foreach (var e in G.actors)
            {
                if (e.team == a.team || !e.alive) continue;
                string kind = e.weapon?.kind; var KB = Kits.main(kind)?.bot;
                if (!(BotBrain.CHARGES.Contains(kind) || BotBrain.LONG.Contains(kind) || (KB != null && (KB.@long || KB.charges)))) continue;
                var r = e.weaponRunner; if (r == null || !(r.charging || r.firingT > 0 || r.burstT > 0)) continue;
                float dx = a.pos.x - e.pos.x, dy = a.pos.y + 0.8f - (e.pos.y + 1.1f), dz = a.pos.z - e.pos.z, d = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
                if (d > Config.weaponRange(e.weapon) + 3 || d < 1) continue;
                if ((e.aimDir.x * dx + e.aimDir.y * dy + e.aimDir.z * dz) / d < Mathf.Cos(0.14f)) continue;
                return e;
            }
            return null;
        }
        static bool botFight(BotBrain brain, BotCtx ctx)
        {
            var a = ctx.a; var w = ctx.w; float dist = ctx.dist, range = ctx.range, dt = ctx.dt;
            var k = kitOf(a.weaponRunner);
            var bb = mem(brain);
            // open the canopy when taking fire, or when a long-range weapon is lining up from roughly ahead
            if (k.state == "ready" && k.grow > 0.9f && bb.holdUntil <= G.time && G.time >= bb.nextShield && dist < 18)
            {
                var thr = lineThreat(a);
                bool ahead = thr != null && Mathf.Abs(angleDiff(a.aimYaw, Mathf.Atan2(thr.pos.x - a.pos.x, thr.pos.z - a.pos.z))) < 0.9f;
                bool hurt = a.lastDamage < 0.45f && a.lastAttacker != null && a.lastAttacker.alive && Mathf.Abs(angleDiff(a.aimYaw, Mathf.Atan2(a.lastAttacker.pos.x - a.pos.x, a.lastAttacker.pos.z - a.pos.z))) < 1.0f;
                float disc = brain.diff.fireDiscipline;
                if ((ahead && random() < 0.35f * dt * 60 * disc) || (hurt && random() < 0.2f * dt * 60 * disc))
                {
                    int allies = 0;
                    foreach (var o in G.actors) if (o != a && o.alive && o.team == a.team && (o.pos - a.pos).sqrMagnitude < 49) allies++;
                    bool launchIt = dist > 4 && dist < 14 && (allies > 0 || ahead) && random() < (allies > 0 ? 0.45f : 0.2f);
                    float hold = launchIt ? w.openDelay + w.openTime + w.launchHold + 0.12f : 0.5f + random() * 0.3f;
                    bb.holdUntil = G.time + hold; bb.launch = launchIt;
                    bb.nextShield = G.time + hold + 0.9f + random() * 1.4f;
                    if (launchIt) bb.advanceUntil = G.time + hold + 2.2f;
                }
            }
            if (bb.holdUntil > G.time && k.state == "ready")
            {
                if (!bb.launch) ctx.move *= 0.4f;   // plant behind the canopy (it slows you anyway)
                return true;
            }
            if (bb.holdUntil > G.time) bb.holdUntil = 0;   // broke / launched mid-hold
            // advance behind the canopy we just launched (it slides toward the target)
            if (bb.advanceUntil > G.time && k.state == "launched")
            {
                Launched own = null;
                foreach (var c in LAUNCHED) if (c.owner == a && c.alive) { own = c; break; }
                if (own != null)
                {
                    float bx = own.pos.x - own.dir.x * 1.3f - a.pos.x, bz = own.pos.z - own.dir.z * 1.3f - a.pos.z, bl = Mathf.Sqrt(bx * bx + bz * bz);
                    ctx.move = bl > 0.4f ? new Vector3(bx / bl, 0, bz / bl) : new Vector3(own.dir.x * 0.5f, 0, own.dir.z * 0.5f);
                }
            }
            return dist < range * 1.1f;
        }
        static bool botPaint(BotBrain brain, BotCtx ctx)
        {
            mem(brain);
            return ctx.needPaint && ctx.inkFrac > 0.2f;
        }

        // ------------------------------------------------------------------------------------------ query hook
        // Bots route round enemy launched canopies and flank held ones: one stable descriptor per shield, getters read
        // its live state. Read-only: nothing here changes how a canopy behaves.
        public class View : ShieldInfo
        {
            readonly Shield s;
            public View(Shield sh)
            {
                s = sh; var w = W;
                kind = "brolly"; held = s.held; owner = s.owner; hpMax = w.canopyHp; blocksActors = !s.held;
                // launched: half-width it blocks players over, and the blocking plane's offset along N from pos
                halfW = LAUNCH_R; planeOff = -BrollyModel.DEPTH * 0.5f; speed = s.held ? 0 : w.launchSpeed;
            }
            public override Vector3 C => s.C;
            public override Vector3 N => s.N;
            public override float R => s.R;
            public override Vector3 pos => s.held ? default : s.rf.pos;
            public override int team => s.team;
            public override float hp => Mathf.Max(0, hpOf(s).hp);
            public override bool live
            {
                get
                {
                    if (!s.held) return s.rf.alive;
                    var a = s.owner; var k = s.kit;
                    return a != null && a.alive && a.form == "kid" && a.weaponRunner?.kit == k && k.state == "ready" && k.open >= 0.55f && k.grow >= 0.9f;
                }
            }
            public override float left => s.held ? 0 : Mathf.Max(0, s.rf.life - s.rf.t);
        }
        public override void shields(List<ShieldInfo> o)
        {
            if (SH_T != G.time) refreshShields();
            foreach (var s in SH) o.Add(s.info ??= new View(s));
        }

        // ------------------------------------------------------------------------------------------ registration
        public Brolly()
        {
            bot = new KitBot { paintPitch = -0.36f, fight = botFight, paint = botPaint };
            on("bomb:explode", onBomb);
        }
        public override bool busy(WeaponRunner r) => false;
        public override bool firingPose(WeaponRunner r) => r.kit is BrollyState k && k.open > 0;
        public override float moveSpeed(WeaponRunner r, WeaponDef w) => r.kit is BrollyState k && k.open > 0.2f ? w.moveSpeedShield : 0;
        public override float? spreadDeg(WeaponRunner r, WeaponDef w) => r.a.grounded ? w.spreadDeg : w.spreadAir;
    }
}
