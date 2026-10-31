// Port of src/game/kits/bow.js — Tideline Bow (main weapon, kind 'bow'): a Tri-Stringer-style bow, hold to draw, release
// to loose THREE arrows at once. Tuning lives in Config.WEAPONS["bow"]; the model / part animation in BowModel.cs
// (kits/bow-model.js); the draw pose in Character.Pose.cs (HOLD.bow + poseBow); sounds (bow-sfx.js) arrive with M5.
//
//   draw      charge 0..1 builds linearly over chargeTime; ring 1 at `ring1` (0.45 s), ring 2 = full draw (1.0 s).
//             Slow walk while drawing; the draw holds at full like a charger's. The tank caps how far you can draw.
//   loose     three arrows: a flat (horizontal) fan when you're on the ground, an upright (vertical) one in the air.
//             A tap (< ring 1) = short, weak arrows that just splat. Ring 1+ = the arrows LODGE where they land (or at
//             a victim's feet on a direct hit), glow and tick, then burst after a short fuse. Full draw = full reach,
//             top damage and a bigger burst. Centre + side direct hits, or a direct hit + its burst, splat.
//   arrows    this module's own projectiles (not G.projectiles.list): fast with a light arc, the same flight model
//             Projectiles uses (straight phase → gravity) so its ballistic solver lands them on the crosshair; past
//             their reach they nose down into the ground. Drawn as meshes + additive streaks and glows.
// Not ported: the online ghosts, the Boss Battle hits (M7).
using System.Collections.Generic;
using Inkwave.World;
using UnityEngine;
using UnityEngine.Rendering;
using static Inkwave.Ctx;

namespace Inkwave
{
    /** runner.kit while the Bow is held */
    public class BowState { public int ring, botJump; public float stall, lastC, botYaw, botWant, botPaint, seen; public bool botJumped; }

    public class Bow : MainKit
    {
        static WeaponDef W => Config.WEAPONS["bow"];
        const float DEG = Mathf.PI / 180;
        static readonly Vector3 UP = new Vector3(0, 1, 0), DOWN = new Vector3(0, -1, 0), ZAX = new Vector3(0, 0, 1);
        const int MAX = 192;             // arrows alive at once (flying + lodged)
        const float ARROW_R = 0.11f;     // added to the victim's capsule radius (a thin shaft, a fair hitbox)
        const float LIFE = 3;            // a flying arrow gives up after this long (s)
        static readonly Hit _hit = new Hit(), _hit2 = new Hit();

        static bool near(Vector3 p, float d) => (G.camPos - p).sqrMagnitude < d * d;
        static void credit(Actor owner, float area) { if (!(area > 0)) return; if (owner.specialActive != null) owner.addTurfNoSpecial(area); else owner.addTurf(area); }
        static BowState kitOf(WeaponRunner r) => r.kit as BowState ?? (BowState)(r.kit = new BowState());

        // ------------------------------------------------------------------------------------------ shot tiers
        // charge → { tier 0 tap | 1 ring | 2 full, range (straight flight before the dive), speed, damage (centre / side),
        // fan (deg), lodge + burst }
        public struct Shot { public int tier; public float range, speed, dmg, side, fan, fuse, br, bd, be, bp; public bool lodge; }
        public static Shot bowShot(float c)
        {
            var w = W;
            if (c >= 0.999f)
                return new Shot { tier = 2, range = w.flightFull, speed = w.speedFull, dmg = w.damageFull, side = w.sideFull, fan = w.fanFull, lodge = true,
                    fuse = w.fuseFull, br = w.burstRadiusTier[1], bd = w.burstDamage[1], be = w.burstEdge[1], bp = w.burstPaint[1] };
            if (c >= w.ring1)
            {
                float u = (c - w.ring1) / (1 - w.ring1), d = lerp(w.damageRing[0], w.damageRing[1], u);
                return new Shot { tier = 1, range = lerp(w.flightRing[0], w.flightRing[1], u), speed = lerp(w.speedRing[0], w.speedRing[1], u), dmg = d, side = d * w.sideMul,
                    fan = w.fanRing, lodge = true, fuse = w.fuseRing, br = w.burstRadiusTier[0], bd = w.burstDamage[0], be = w.burstEdge[0], bp = w.burstPaint[0] };
            }
            {
                float u = clamp(c / w.ring1, 0, 1), d = lerp(w.damageTap[0], w.damageTap[1], u);
                return new Shot { tier = 0, range = lerp(w.flightTap[0], w.flightTap[1], u), speed = w.speedTap, dmg = d, side = d * w.sideMul, fan = w.fanTap, lodge = false };
            }
        }

        // ------------------------------------------------------------------------------------------ arrows
        class Arrow
        {
            public Vector3 pos, prev, vel, dir, fdir, hitP, nrm;
            public Actor owner; public int team, st, tier, ticks;   // st 0 flying · 1 lodged
            public float age, dist, t, range, speed, dmg, fuse, br, bd, be, bp, trail, seed;
            public bool lodge, noHit, center;
        }
        static readonly List<Arrow> arrows = new List<Arrow>();   // live records
        static readonly Stack<Arrow> pool = new Stack<Arrow>();
        public static int grouped;   // bots' fan nudges toward a second foe (tests read it)
        static void kill(int i) { var p = arrows[i]; arrows[i] = arrows[arrows.Count - 1]; arrows.RemoveAt(arrows.Count - 1); p.owner = null; pool.Push(p); }

        // Loose a volley from actor a at charge c (weapons-side: ink, sounds, pose are the runner's).
        public static Shot looseVolley(Actor a, float c)
        {
            var S = bowShot(c); var P = G.projectiles; var w = W;
            Vector3 m = P.muzzle(a);
            Vector3 dir = Projectiles.aimFrom(a, m);
            dir = Projectiles.ballistic(m, dir, a.aimPoint, S.speed, w.straight, w.grav, 0, S.range);
            // fan axis: on the ground the arrows spread about the vertical (a flat fan); in the air about the aim's
            // horizontal right axis (an upright fan)
            bool air = !a.grounded;
            // bots: nudge the fan's centre toward a second foe beside the target, so the fan spans both
            var k = a.weaponRunner != null ? kitOf(a.weaponRunner) : null;
            if (k != null && k.botYaw != 0 && !air) { dir = Quaternion.AngleAxis(k.botYaw * Mathf.Rad2Deg, UP) * dir; k.botYaw = 0; grouped++; }
            if (k != null) k.botYaw = 0;
            volley(a, S, m, dir, air);
            if (a.isLocal) emit("recoil", new Ev { amount = 0.004f + 0.004f * S.tier });
            emit("weapon:fire", new Ev { actor = a, id = w.id, muzzle = m, dir = dir, charge = c });
            Actor.rumble(a, 0.1f + 0.15f * S.tier, 0.18f + 0.12f * S.tier, 70 + 30 * S.tier);
            return S;
        }

        // the three arrows of a volley from m along dir (the fan: about the vertical on the ground, about the aim's right
        // axis in the air)
        static void volley(Actor a, Shot S, Vector3 m, Vector3 dir, bool air)
        {
            Vector3 ax;
            if (air) { ax = Vector3.Cross(dir, UP); if (ax.sqrMagnitude < 1e-4f) ax = new Vector3(1, 0, 0); ax.Normalize(); } else ax = UP;
            Color col = G.teamColors[a.team];
            for (int i = -1; i <= 1; i++)
            {
                if (arrows.Count >= MAX) kill(0);
                var p = pool.Count > 0 ? pool.Pop() : new Arrow();
                Vector3 d = i != 0 ? Quaternion.AngleAxis(i * S.fan, ax) * dir : dir;
                p.pos = m; p.prev = m; p.vel = d * S.speed; p.dir = d; p.fdir = d;
                p.owner = a; p.team = a.team; p.st = 0; p.age = 0; p.dist = 0; p.t = 0; p.range = S.range; p.speed = S.speed; p.tier = S.tier;
                p.dmg = i != 0 ? S.side : S.dmg; p.lodge = S.lodge; p.fuse = S.fuse; p.br = S.br; p.bd = S.bd; p.be = S.be; p.bp = S.bp;
                p.trail = -1.8f; p.seed = random(); p.noHit = false; p.ticks = 0; p.center = i == 0;
                arrows.Add(p);
            }
            if (a.isLocal || a.nearCamera())
            {
                G.audio?.play("bow_loose", new AudioOpts { pos = a.isLocal ? (Vector3?)null : m, volume = a.isLocal ? 0.75f : 0.55f, pitch = 1.12f - 0.12f * S.tier });
                G.fx?.muzzle(m, dir, col, S.tier == 2 ? "charger" : "shooter");
            }
        }

        // stick arrow p into a surface at `point` (normal n); `d` = the direction it sinks in
        static void lodge(Arrow p, Vector3 point, Vector3 n, Vector3 d)
        {
            p.st = 1; p.t = 0; p.hitP = point; p.nrm = n; p.dir = d.normalized;
            p.pos = p.hitP + p.dir * 0.12f;      // tip buried; the shaft sticks out behind it
            credit(p.owner, G.paint.splat(p.hitP + p.nrm * 0.08f, W.paintStick, p.team, new SplatOpts { seed = p.seed, stretch = p.dir, stretchAmt = 0.5f }));
            if (p.owner.isLocal || near(p.hitP, 26))
            {
                G.audio?.play("bow_thunk", new AudioOpts { pos = p.hitP, volume = 0.5f + 0.15f * p.tier, pitch = p.center ? 1 : 1.07f });
                G.fx?.burst(p.hitP, p.nrm, G.teamColors[p.team], new FxOpts { count = 5, speed = 2.4f, size = 0.06f, paint = false });
            }
        }

        // the lodged arrow bursts: paint, splash damage (falloff past burstInner, needs line of sight), devices, fx
        static void burst(Arrow p)
        {
            Vector3 c = p.hitP + p.nrm * 0.18f;
            Color col = G.teamColors[p.team]; var owner = p.owner;
            float area = G.paint.splat(p.hitP + p.nrm * 0.1f, p.bp, p.team, new SplatOpts { seed = p.seed });
            for (int i = 0; i < 2; i++)
            {
                float an = p.seed * 20 + i * 2.7f, r = p.bp * 0.55f;
                area += G.paint.splat(new Vector3(c.x + Mathf.Cos(an) * r, c.y + 0.1f, c.z + Mathf.Sin(an) * r), p.bp * 0.45f, p.team, new SplatOpts { seed = p.seed + i });
            }
            credit(owner, area);
            float inner = W.burstInner;
            foreach (var e in G.actors)
            {
                if (e.team == p.team || !e.alive) continue;
                // nearest point of the victim's body axis (feet + 0.25 … head - 0.25)
                float h = e.form == "squid" ? PLAYER.squidHeight : PLAYER.height;
                float by = e.pos.y + e.smoothY;
                Vector3 v = new Vector3(e.pos.x, clamp(c.y, by + 0.25f, by + Mathf.Max(0.3f, h - 0.25f)), e.pos.z);
                float d = Mathf.Max(0, Vector3.Distance(v, c) - PLAYER.radius * 0.6f);
                if (d > p.br) continue;
                if (!G.physics.los(c, v)) continue;
                float dmg = d <= inner ? p.bd : lerp(p.bd, p.be, (d - inner) / Mathf.Max(0.01f, p.br - inner));
                G.projectiles.applyHit(owner, e, dmg, W.id);
            }
            G.subs.damageArea(c, p.br, p.be, p.team);
            // (Boss Battle splash — M7)
            bool loud = owner.isLocal || near(c, 34);
            if (loud)
            {
                G.fx?.explosion(c, col, p.br * (p.tier == 2 ? 0.7f : 0.55f));
                G.audio?.play("bow_burst", new AudioOpts { pos = c, volume = 0.45f + 0.15f * p.tier, pitch = (p.tier == 2 ? 0.95f : 1.1f) * (p.center ? 1 : 1.06f) });
                if (near(c, 9)) emit("shake", new Ev { pos = c, amount = 0.12f + 0.08f * p.tier });
            }
            emit("weapon:impact", new Ev { pos = c, normal = p.nrm, team = p.team, kind = "blast", radius = p.br });
        }

        static readonly float[] TICK_AT = { 0.42f, 0.24f, 0.1f };
        static void stepArrow(Arrow p, int i, float dt)
        {
            var w = W;
            if (p.st == 1)
            {
                p.t += dt;
                // countdown ticks: three, closing in on the burst
                float left = p.fuse - p.t;
                if (p.ticks < 3 && left < TICK_AT[p.ticks])
                {
                    p.ticks++;
                    if (p.center && (p.owner.isLocal || near(p.hitP, 24))) G.audio?.play("bow_tick", new AudioOpts { pos = p.hitP, volume = 0.35f + 0.1f * p.ticks, pitch = 1 + 0.12f * p.ticks });
                    if (near(p.hitP, 30)) G.fx?.beepPulse(p.hitP + p.nrm * 0.1f, G.teamColors[p.team], p.br, p.t / p.fuse);
                }
                if (p.t >= p.fuse) { burst(p); kill(i); }
                return;
            }
            p.age += dt;
            p.prev = p.pos;
            if (p.age > w.straight) p.vel.y -= w.grav * dt;
            if (p.dist > p.range) { p.vel.y -= w.diveGrav * dt; p.vel *= 1 - w.diveDrag * dt; }
            p.pos += p.vel * dt;
            float sp = p.vel.magnitude;
            p.dist += sp * dt;
            if (sp > 0.01f) p.dir = p.vel * (1 / sp);
            // actors (a victim takes the direct hit; a lodging arrow then drops and sticks into the floor at their feet)
            if (!p.noHit)
            {
                foreach (var e in G.actors)
                {
                    if (e.team == p.team || !e.alive) continue;
                    float h = e.form == "squid" ? PLAYER.squidHeight : PLAYER.height, hr = PLAYER.radius;
                    if (Mathf.Abs(e.pos.x - p.pos.x) > 3 + hr || Mathf.Abs(e.pos.z - p.pos.z) > 3 + hr) continue;
                    var (dist, t) = Physics.segmentCapsuleDist(p.prev, p.pos, new Vector3(e.pos.x, e.pos.y + e.smoothY, e.pos.z), hr, h);
                    if (dist < hr * 0.95f + ARROW_R)
                    {
                        Vector3 v = Vector3.LerpUnclamped(p.prev, p.pos, t);
                        G.projectiles.applyHit(p.owner, e, p.dmg, w.id);
                        G.fx?.burst(v, -p.dir, G.teamColors[p.team], new FxOpts { count = 6, speed = 3, size = 0.07f });
                        emit("weapon:impact", new Ev { pos = v, normal = -p.dir, team = p.team, kind = "shot", radius = 0.3f, victim = e });
                        if (!p.lodge) { kill(i); return; }
                        p.noHit = true; p.fdir = p.dir;
                        p.pos = v; p.prev = v; p.vel = new Vector3(p.dir.x * 1.5f, -9, p.dir.z * 1.5f); p.dist = Mathf.Max(p.dist, p.range);
                        return;
                    }
                }
            }
            // (Boss Battle: HULLBREAKER's hit spheres / its crablets take the direct hit — M7)
            // enemy devices / canopies (and special objects) catch arrows; lodging ones stick in them
            if (!p.noHit && (G.subs.blockShot(p.prev, p.pos, p.team, p.dmg) || (G.specials?.shotHit(p.prev, p.pos, p.team, p.dmg, p.owner) ?? false)))
            {
                if (p.lodge) lodge(p, p.pos, -p.dir, p.dir); else kill(i);
                return;
            }
            // world
            var hit = G.physics.segment(p.prev, p.pos, _hit, true);
            if (hit.hit)
            {
                if (p.lodge)
                {
                    // a dropped arrow (after a direct hit) sticks in at a slant along its old flight line
                    Vector3 d = p.noHit ? new Vector3(p.fdir.x * 0.55f, -1, p.fdir.z * 0.55f).normalized : p.dir;
                    lodge(p, hit.point, hit.normal, d);
                }
                else
                {
                    credit(p.owner, G.paint.splat(hit.point + hit.normal * 0.12f, w.paintTap * (0.85f + 0.3f * p.seed), p.team, new SplatOpts { seed = p.seed, stretch = p.dir, stretchAmt = 0.7f }));
                    emit("weapon:impact", new Ev { pos = hit.point, normal = hit.normal, team = p.team, kind = "shot", radius = w.paintTap });
                    if (p.owner.isLocal || near(hit.point, 22))
                    {
                        G.fx?.burst(hit.point, hit.normal, G.teamColors[p.team], new FxOpts { count = 5, speed = 3, size = 0.07f, paint = false });
                        if (p.center) G.audio?.play("splat_small", new AudioOpts { pos = hit.point, volume = 0.35f });
                    }
                    kill(i);
                }
                return;
            }
            // trail drips (lodging arrows only: a dotted ink line under the flight)
            if (p.lodge && !p.noHit)
            {
                p.trail += sp * dt;
                if (p.trail > w.trailEvery)
                {
                    p.trail = 0;
                    var g = G.physics.raycast(p.pos, DOWN, 4, _hit2, true);
                    if (g.hit) credit(p.owner, G.paint.splat(g.point + g.normal * 0.1f, w.trailRadius * (0.8f + random() * 0.4f), p.team, new SplatOpts { seed = random() }));
                }
            }
            if (p.age > LIFE || p.pos.y < PLAYER.waterY - 1.2f)
            {
                if (p.pos.y < PLAYER.waterY - 1.2f && near(p.pos, 30)) G.fx?.waterPlop(new Vector3(p.pos.x, PLAYER.waterY, p.pos.z), 0.5f);
                kill(i);
            }
        }

        // ------------------------------------------------------------------------------------------ drawing
        // ink = shaft + ink-bulb head (team gloss), fletch = vanes + nock (vertex colour, plastic), glow = additive head /
        // lodged throb, streak = additive tail fading back from the head (Shaders/Fx/Additive)
        static Mesh _ink, _fletch, _glow, _streak;
        static Material _glowMat, _streakMat;
        // one property block per glow / streak draw this frame (the draws are queued, so each keeps its own colour)
        static readonly List<MaterialPropertyBlock> _mpbs = new List<MaterialPropertyBlock>();
        static int _nm;
        static MaterialPropertyBlock mpb(Color linear) { if (_nm >= _mpbs.Count) _mpbs.Add(new MaterialPropertyBlock()); var m = _mpbs[_nm++]; m.SetVector(ID_Color, new Vector4(linear.r, linear.g, linear.b, 1)); return m; }
        static readonly int ID_Color = Shader.PropertyToID("_Color");
        static void ensureRender()
        {
            if (_ink != null) return;
            _ink = WeaponModel.loadMesh("bow/arrowInk"); _fletch = WeaponModel.loadMesh("bow/arrowFletch");
            _glow = Geo.sphere(1, 10, 8).toMesh("bowGlow");
            // CylinderGeometry(1, 1, 1, 6, 1, open).rotateX(π/2).translate(0, 0, -0.5): a unit tube along -Z from the origin
            _streak = Geo.cylinder(1, 1, 1, 6, 1, true).rotateX(Mathf.PI / 2).translate(0, 0, -0.5f).toMesh("bowStreak");
            var sh = Shader.Find("Inkwave/Fx/Additive");
            _glowMat = new Material(sh) { name = "iw-bow-glow" }; _glowMat.SetFloat("_Mode", 0);
            _streakMat = new Material(sh) { name = "iw-bow-streak" }; _streakMat.SetFloat("_Mode", 1);
        }
        static void draw()
        {
            if (arrows.Count == 0) return;
            ensureRender();
            var rootM = ThreeSpace.root.localToWorldMatrix;
            var rpI = new RenderParams(CharacterMats.ink(G.teamColors[0])) { shadowCastingMode = ShadowCastingMode.On, receiveShadows = true };
            var rpF = new RenderParams(CharacterMats.plastic) { shadowCastingMode = ShadowCastingMode.On, receiveShadows = true };
            var rpG = new RenderParams(_glowMat) { shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false };
            var rpS = new RenderParams(_streakMat) { shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false };
            float t = G.time; _nm = 0;
            foreach (var p in arrows)
            {
                Color col = G.teamColors[p.team], lc = col.linear;
                Quaternion q = Quaternion.FromToRotation(ZAX, p.dir);
                var M = rootM * Matrix4x4.TRS(p.pos, q, Vector3.one);
                rpI.material = CharacterMats.ink(col);
                Graphics.RenderMesh(rpI, _ink, 0, M);
                Graphics.RenderMesh(rpF, _fletch, 0, M);
                if (p.st == 1)
                {
                    // lodged: the head swells and throbs faster toward the burst, the danger ring fills on the surface
                    float k = clamp(p.t / p.fuse, 0, 1), beat = 0.5f + 0.5f * Mathf.Sin(t * (14 + 30 * k));
                    Vector3 at = p.hitP + p.nrm * 0.05f;
                    glowAt(rpG, rootM * Matrix4x4.TRS(at, q, Vector3.one * (0.07f + 0.07f * k + 0.035f * beat)), lc * (1.2f + 2.2f * k + 1.6f * beat));
                    if (near(p.hitP, 36)) G.fx?.dangerRing(p.hitP + p.nrm * 0.02f, p.nrm, col, p.br, k);
                }
                else
                {
                    // flying: a glowing head and a fading streak behind (longer for faster, charged arrows)
                    glowAt(rpG, rootM * Matrix4x4.TRS(p.pos - p.dir * 0.05f, q, Vector3.one * (p.tier == 2 ? 0.085f : p.tier != 0 ? 0.07f : 0.05f)), lc * (p.tier == 2 ? 2.4f : p.tier != 0 ? 1.6f : 0.9f));
                    float len = Mathf.Min(p.dist, p.vel.magnitude * 0.06f + 0.4f), rad = p.tier == 2 ? 0.045f : p.tier != 0 ? 0.036f : 0.026f;
                    rpS.matProps = mpb(lc * (p.tier == 2 ? 1.5f : 1.0f));
                    Graphics.RenderMesh(rpS, _streak, 0, rootM * Matrix4x4.TRS(p.pos - p.dir * 0.25f, q, new Vector3(rad, rad, len)));
                }
            }
        }
        static void glowAt(RenderParams rp, Matrix4x4 m, Color linear)
        {
            rp.matProps = mpb(linear);
            Graphics.RenderMesh(rp, _glow, 0, m);
        }

        // ------------------------------------------------------------------------------------------ the runner
        public override void update(WeaponRunner runner, float dt, WeaponInput inp, WeaponDef w)
        {
            var a = runner.a; var k = kitOf(runner);
            k.seen = G.time;
            Vector3? pos = a.isLocal ? (Vector3?)null : a.pos;
            if (inp.fire && runner.cooldown <= 0)
            {
                if (!runner.charging)
                {
                    if (a.ink < w.inkFull * w.inkMin) { runner.empty(); return; }
                    runner.charging = true; runner.charge = 0; runner.chargeT = 0; k.ring = 0; k.stall = 0; k.lastC = 0;
                    if (a.isLocal || a.nearCamera()) runner.chargeLoop = G.audio?.loop("bow_draw", new AudioOpts { pos = pos, volume = a.isLocal ? 0.5f : 0.32f, pitch = 0.6f });
                }
                // the draw builds linearly (ring 1 at w.ring1 of the way); the tank caps it
                float maxC = clamp(a.ink / w.inkFull, 0, 1);
                runner.chargeT = Mathf.Min(1, runner.chargeT + dt / w.chargeTime);
                runner.charge = Mathf.Min(maxC, runner.chargeT);
                a.fireFacing = 0.4f;
                runner.chargeLoop?.set(pitch: 0.6f + 0.9f * runner.charge, pos: pos);
                if (k.ring < 1 && runner.charge >= w.ring1)
                {
                    k.ring = 1;
                    if (a.isLocal) G.audio?.play("bow_ring1", new AudioOpts { volume = 0.65f });
                    Actor.rumble(a, 0.03f, 0.18f, 45);
                    emit("bow:ring", new Ev { actor = a, level = 1 });
                }
                if (k.ring < 2 && runner.charge >= 0.999f)
                {
                    k.ring = 2;
                    if (a.isLocal) G.audio?.play("bow_full", new AudioOpts { volume = 0.7f });
                    Actor.rumble(a, 0.05f, 0.3f, 60);
                    emit("bow:ring", new Ev { actor = a, level = 2 });
                }
                // bots only: a draw that can't grow (tank-capped / held at full) while not duelling lets go after a beat, so
                // a bot painting a puddle to refill never stands there holding the string
                k.stall = runner.charge > k.lastC + 1e-4f ? 0 : k.stall + dt; k.lastC = runner.charge;
                if (a.bot != null && k.stall > 0.45f && a.bot.mode != "fight") release(runner, w, k);
            }
            else if (runner.charging) release(runner, w, k);
        }
        static void release(WeaponRunner runner, WeaponDef w, BowState k)
        {
            var a = runner.a;
            runner.charging = false;
            runner.chargeLoop?.stop(0.06f); runner.chargeLoop = null;
            float c = runner.charge;
            a.ink = Mathf.Max(0, a.ink - w.inkFull * Mathf.Max(w.inkMin, c));
            a.lastFire = 0;
            looseVolley(a, c);
            a.character.trigger("charge_release");
            runner.charge = 0; runner.chargeT = 0; k.ring = 0; k.stall = 0;
            runner.firingT = 0.35f;
            runner.cooldown = w.cooldown;
        }

        // ------------------------------------------------------------------------------------------ bots
        // fight: a full draw at range, ring 1 up close; the flat fan leans toward a second foe beside the target; a target
        // above / below (ledge, stairs), or now and then at mid range, gets a jump shot with the upright fan
        static float groupYaw(Actor a, Actor t, float fanRad)
        {
            Actor best = null; float bd = 3.5f;
            foreach (var e in G.actors)
            {
                if (e == t || e.team == a.team || !e.alive) continue;
                float d = Mathf.Sqrt((e.pos.x - t.pos.x) * (e.pos.x - t.pos.x) + (e.pos.z - t.pos.z) * (e.pos.z - t.pos.z));
                if (d < bd) { bd = d; best = e; }
            }
            if (best == null) return 0;
            float y1 = Mathf.Atan2(t.pos.x - a.pos.x, t.pos.z - a.pos.z), y2 = Mathf.Atan2(best.pos.x - a.pos.x, best.pos.z - a.pos.z);
            return clamp(angleDiff(y1, y2) * 0.5f, -fanRad, fanRad);
        }
        static bool botFight(BotBrain brain, BotCtx ctx)
        {
            var a = ctx.a; var w = ctx.w; float dist = ctx.dist, range = ctx.range; var it = ctx.it; var target = ctx.target;
            var wr = a.weaponRunner; var k = kitOf(wr);
            if (!wr.charging)
            {
                if (dist > range * 1.1f || wr.cooldown > 0) return false;
                k.botWant = dist > 11 ? 1 : w.ring1 + 0.08f;
                float dy = target.pos.y - a.pos.y;
                k.botJump = a.grounded && dist > 4 && dist < 17 && (Mathf.Abs(dy) > 1.3f || random() < 0.18f) ? 1 : 0;
                k.botJumped = false;
                return true;
            }
            ctx.move *= 0.4f;
            float want = Mathf.Min(k.botWant, clamp(a.ink / w.inkFull, 0, 1) - 0.001f);
            if (wr.charge < want) return true;
            if (k.botJump != 0)
            {
                if (!k.botJumped) { if (a.grounded) { it.jump = true; k.botJumped = true; } return true; }
                if (a.grounded && a.vel.y <= 0.5f && k.botJumped) { k.botJump = 0; return false; }   // landed already: just loose
                if (a.vel.y > 1.5f) return true;                                                     // rising: loose near the top
                return false;
            }
            if (a.grounded) k.botYaw = groupYaw(a, target, bowShot(wr.charge).fan * DEG);
            return false;
        }
        static bool botPaint(BotBrain brain, BotCtx ctx)
        {
            var a = ctx.a; var w = ctx.w;
            var wr = a.weaponRunner; var k = kitOf(wr);
            if (wr.charging)
            {
                bool done = wr.charge >= Mathf.Min(k.botPaint > 0 ? k.botPaint : w.ring1 + 0.05f, clamp(a.ink / w.inkFull, 0, 1) - 0.001f);
                if (done) brain.paintPause = 0.3f + random() * 0.35f;
                return !done;
            }
            if (!(ctx.needPaint && ctx.inkFrac > 0.25f && brain.paintPause <= 0 && wr.cooldown <= 0)) return false;
            // not getting anywhere on its path: hold off the next draw (drawing resets the unstick timer) so the brain's
            // hop / skip / replan recovery gets its turn
            if (brain.noProg > 0.25f) return false;
            k.botPaint = random() < 0.3f ? 1 : w.ring1 + 0.05f;
            return true;
        }

        // ------------------------------------------------------------------------------------------ registration
        public Bow()
        {
            bot = new KitBot { paintPitch = -0.18f, fight = botFight, paint = botPaint, charges = true, @long = true };
            BotBrain.CHARGES.Add("bow"); BotBrain.LONG.Add("bow");
        }
        public override void reset(WeaponRunner runner) { runner.kit = null; }
        public override float moveSpeed(WeaponRunner runner, WeaponDef w)
        {
            if (runner.charging) return lerp(PLAYER.runSpeed * 0.75f, w.moveSpeedDrawing, Mathf.Min(1, runner.charge * 2.5f));
            if (runner.firingT > 0) return w.moveSpeedFiring;
            return 0;
        }
        public override float? spreadDeg(WeaponRunner r, WeaponDef w) => 0;
        public override void tick(float dt)
        {
            for (int i = arrows.Count - 1; i >= 0; i--) if (i < arrows.Count) stepArrow(arrows[i], i, dt);
            // a draw left hanging (a special took over the trigger mid-draw, so the runner stopped updating): let it go quietly
            foreach (var a in G.actors)
            {
                var r = a.weaponRunner;
                if (r != null && r.charging && r.kit is BowState k && a.weapon?.kind == "bow" && G.time - k.seen > 0.15f)
                {
                    r.charging = false; r.charge = 0; r.chargeT = 0; k.ring = 0; r.chargeLoop?.stop(0.06f); r.chargeLoop = null;
                }
            }
            draw();
        }
        public override void clear() { while (arrows.Count > 0) kill(arrows.Count - 1); }
    }
}
