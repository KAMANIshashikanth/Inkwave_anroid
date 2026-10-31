// Port of src/game/kits/mitts.js — Sponge Mitts (main weapon, kind 'mitts'): a melee boxing weapon, a squishy ink-soaked
// sponge glove in each fist.
//
//  · Punches (tap / hold fire): the gloves alternate right / left (≈ 6 a second). Each throws an ink fist — a glossy
//    glove-shaped blob — that flies fistRange and bursts: a direct hit does punchDamage (three splat), the burst splashes
//    anyone else near it for less and paints a splat. Near full run speed while punching.
//  · Charged leap (hold fire, press jump — the press never becomes a jump): a coiled crouch while jump stays held, the
//    gloves swelling with ink; let go to leap along the aim (aim higher for a higher arc, ≈ 4.5–13 m on flat ground).
//    Costs leapInkMin … leapInkMax ink with the charge. Where it comes down: a glove smash on anyone right there
//    (gloveDamage within gloveRadius), a splash ring (landDamageMax at the centre, falling off to landDamageMin), a big
//    paint splat, and landInvuln s untouchable. The local player sees the arc + landing ring while charging; in flight
//    everyone sees the landing zone marked on the ground in the leaper's ink (the telegraph, G.fx.mark).
//  · Wall cling: a leap that meets a wall (one that rises past the head) sticks there — the kid hangs off it by the left
//    glove, still punching with the right. Holding on drains clingDrain ink / s; jump lets go (fire + jump leaps off the
//    wall instead), an empty tank drops you, squid form lets go.
//
// Engine hooks: Kits.MAIN["mitts"] (update / busy / firingPose / moveSpeed / spreadDeg / damageTaken / jump / tick / clear
// / bot) — the actor takes a jump press through jump() and holds the kid in place while runner.kit.hang. Per-runner state
// lives in runner.kit (a MittsState; the character's pose reads its KitState part). The fists are this module's own
// projectiles. Model: MittsModel.cs. Not ported: the online ghosts, Boss Battle hits (M7), the sounds (M5, by name).
using System.Collections.Generic;
using Inkwave.World;
using UnityEngine;
using UnityEngine.Rendering;
using static Inkwave.Ctx;

namespace Inkwave
{
    /** runner.kit while the Mitts are held (the KitState fields are what the character's pose reads) */
    public class MittsState : KitState
    {
        public float seen, cd, side = 1, chargeT, chargeAir, leapT, leapH, trail, predT, clingT, dryWarn, botWant = -1, botYaw, botPitch, botHold, squishT = -9;
        public bool full, slid;
        public string botKind;
        public Vector3 leapV, trailP;
        public AudioLoop loop, drain;
        public readonly Mitts.Land land = new Mitts.Land();
    }

    public class Mitts : MainKit
    {
        const float DEG = Mathf.PI / 180;
        static readonly Vector3 UP = new Vector3(0, 1, 0), DOWN = new Vector3(0, -1, 0);
        static readonly Hit _hit = new Hit(), _hitP = new Hit(), _hitC = new Hit();
        static WeaponDef W => Config.WEAPONS["mitts"];
        static bool near(Vector3 p, float r = 30) => (G.camPos - p).sqrMagnitude < r * r;
        static void credit(Actor a, float area, bool sp = false) { if (!(area > 0)) return; if (sp || a.specialActive != null) a.addTurfNoSpecial(area); else a.addTurf(area); }

        // ============================================================================================ per-runner state
        static readonly Dictionary<WeaponRunner, MittsState> STATES = new Dictionary<WeaponRunner, MittsState>();   // so tick can sweep a state the runner dropped
        static MittsState K(WeaponRunner r)
        {
            if (r.kit is MittsState k) return k;
            k = new MittsState { mitts = true };
            r.kit = k;
            STATES[r] = k;
            return k;
        }
        static void stopLoops(MittsState k) { k.loop?.stop(0.08f); k.loop = null; k.drain?.stop(0.12f); k.drain = null; }

        // ============================================================================================ fists (projectiles)
        // A glove-shaped ink blob flying knuckles-first (baked: mitts/fist), team gloss with a rim glow so it reads fast.
        const int MAX_FISTS = 96;
        class Fist { public Vector3 pos, prev, start, vel, dir; public Actor owner; public int team, hand; public float age, life, seed, trail, spin; public bool sp; }
        static readonly List<Fist> fists = new List<Fist>();
        static readonly Stack<Fist> fistPool = new Stack<Fist>();
        static Mesh fistMesh;

        // the glove's face (where a fist leaves), falling back to a point in front of the chest on the punching side
        static Vector3 gloveMuzzle(Actor a, int hand)
        {
            Vector3 o = a.character.getMuzzleHand(hand);
            Vector3 chest = a.pos; chest.y += 1.05f;
            if (!float.IsFinite(o.x) || (o - chest).sqrMagnitude > 1.5f * 1.5f || !G.physics.los(chest, o))
            {
                float sx = hand != 0 ? 1 : -1, cy = Mathf.Cos(a.yaw), sy = Mathf.Sin(a.yaw);
                o = chest + a.aimDir * 0.35f;
                o.x += cy * 0.16f * sx; o.z -= sy * 0.16f * sx;
                if (!G.physics.los(chest, o)) o = chest;
            }
            return o;
        }
        static void spawnFist(Actor a, WeaponDef w, int hand, Vector3 m, Vector3 dir)
        {
            var f = fistPool.Count > 0 ? fistPool.Pop() : new Fist();
            f.owner = a; f.team = a.team; f.age = 0; f.life = w.fistRange / w.fistSpeed; f.hand = hand; f.seed = random();
            f.sp = a.specialActive != null; f.trail = -0.8f; f.spin = (random() - 0.5f) * 0.6f;
            f.pos = m; f.prev = m; f.start = m; f.dir = dir; f.vel = dir * w.fistSpeed;
            fists.Add(f);
            if (a.isLocal || near(m)) G.audio?.play("mitts_punch", new AudioOpts { pos = a.isLocal ? (Vector3?)null : m, volume = a.isLocal ? 0.6f : 0.45f, pitch = hand != 0 ? 1.06f : 0.97f });
        }
        static void fireFist(Actor a, WeaponDef w, int hand)
        {
            Vector3 m = gloveMuzzle(a, hand);
            Vector3 dir = Projectiles.spreadDir(Projectiles.aimFrom(a, m), w.punchSpread);
            spawnFist(a, w, hand, m, dir);
            emit("weapon:fire", new Ev { actor = a, id = w.id, muzzle = m, dir = dir, hand = hand });
            var r = a.weaponRunner;
            if (r.rumbleT <= 0) { r.rumbleT = 0.08f; Actor.rumble(a, 0.05f, 0.12f, 45); }
        }

        // A fist bursts: splash damage to everyone near but the one it hit, paint where it burst (on the surface it hit, else
        // the ground under it), FX + pop.
        static void burstFist(Fist f, Vector3 at, Actor direct, Vector3? normal)
        {
            var w = W; var o = f.owner;
            if (o == null) return;
            float area = 0;
            if (normal.HasValue) area = G.paint.splat(at + normal.Value * 0.12f, w.fistPaint * (0.85f + random() * 0.3f), f.team, new SplatOpts { seed = f.seed, stretch = f.dir, stretchAmt = 0.45f });
            else
            {
                var g = G.physics.raycast(new Vector3(at.x, at.y + 0.2f, at.z), DOWN, 3.4f, _hit, true);
                if (g.hit) area = G.paint.splat(g.point + g.normal * 0.1f, w.fistPaint * 0.85f, f.team, new SplatOpts { seed = f.seed });
            }
            credit(o, area, f.sp);
            foreach (var e in G.actors)
            {
                if (e.team == f.team || !e.alive || e == direct) continue;
                Vector3 v = e.pos; v.y += e.smoothY + (e.form == "squid" ? 0.3f : 0.7f);
                float d = Vector3.Distance(v, at);
                if (d > w.splashRadius || !G.physics.los(at, v)) continue;
                G.projectiles.applyHit(o, e, lerp(w.splashMax, w.splashMin, clamp(d / w.splashRadius, 0, 1)), "mitts");
            }
            // (specials.js areaHit: bubbles and tanks in the burst — with that module; Boss Battle splash — M7)
            Color col = o.color;
            if (o.isLocal || near(at, 26))
            {
                Vector3 n = normal ?? -f.dir;
                G.fx?.burst(at, n, col, new FxOpts { count = 12, speed = 4.4f, size = 0.085f, mist = true });
                // the full ink-sheet pop only where it can't wall off your own view: someone else's fists, or a fist that connected
                if ((direct != null || !o.isLocal) && near(at, 16)) G.fx?.explosion(at, col, direct != null ? 0.6f : 0.5f);
                else G.fx?.ring(at, n, col, new FxOpts { radius = w.splashRadius * 0.8f, life = 0.22f, snap = normal.HasValue });
                G.audio?.play("mitts_pop", new AudioOpts { pos = at, volume = direct != null ? 0.75f : 0.55f, pitch = 0.95f + random() * 0.12f });
            }
            emit("weapon:impact", new Ev { pos = at, normal = normal ?? UP, team = f.team, kind = "shot", radius = w.splashRadius, victim = direct });
        }

        static void updateFists(float dt)
        {
            var w = W;
            for (int i = fists.Count - 1; i >= 0; i--) stepFist(fists[i], i, w, dt);
            drawFists();
        }
        static void stepFist(Fist f, int i, WeaponDef w, float dt)
        {
            bool dead = f.owner == null;
            f.age += dt;
            f.prev = f.pos;
            f.pos += f.vel * dt;
            float hr0 = w.fistSize;
            // bodies
            if (!dead) foreach (var e in G.actors)
            {
                if (e.team == f.team || !e.alive) continue;
                float h = e.form == "squid" ? PLAYER.squidHeight : PLAYER.height, hr = PLAYER.radius;
                if (Mathf.Abs(e.pos.x - f.pos.x) > 3 + hr || Mathf.Abs(e.pos.z - f.pos.z) > 3 + hr) continue;
                var (dist, t) = Physics.segmentCapsuleDist(f.prev, f.pos, new Vector3(e.pos.x, e.pos.y + e.smoothY, e.pos.z), hr, h);
                if (dist < hr * 0.95f + hr0)
                {
                    Vector3 v = Vector3.LerpUnclamped(f.prev, f.pos, t);
                    G.projectiles.applyHit(f.owner, e, w.punchDamage, "mitts");
                    G.fx?.burst(v, -f.dir, f.owner.color, new FxOpts { count = 7, speed = 3.2f, size = 0.08f });
                    burstFist(f, v, e, null);
                    dead = true; break;
                }
            }
            // (Boss Battle: HULLBREAKER's hit spheres / its crablets — M7)
            // enemy canopies / curtains / devices / special objects catch it (and it bursts there)
            if (!dead && G.subs.blockShot(f.prev, f.pos, f.team, w.punchDamage)) { burstFist(f, f.pos, null, null); dead = true; }
            // the level
            if (!dead)
            {
                var h = G.physics.segment(f.prev, f.pos, _hit, true);
                if (h.hit) { burstFist(f, h.point, null, h.normal); dead = true; }
            }
            // end of its reach: bursts in the air
            if (!dead && f.age >= f.life) { burstFist(f, f.pos, null, null); dead = true; }
            if (!dead && f.pos.y < PLAYER.waterY - 1.5f) dead = true;
            // a thin drip trail under it
            if (!dead)
            {
                f.trail += w.fistSpeed * dt;
                if (f.trail > 1.5f)
                {
                    f.trail = 0;
                    var g = G.physics.raycast(f.pos, DOWN, 3, _hit, true);
                    if (g.hit) credit(f.owner, G.paint.splat(g.point + g.normal * 0.08f, 0.28f + random() * 0.1f, f.team, new SplatOpts { seed = random() }), f.sp);
                }
            }
            if (dead) { f.owner = null; fists[i] = fists[fists.Count - 1]; fists.RemoveAt(fists.Count - 1); fistPool.Push(f); }
        }
        static void drawFists()
        {
            if (fists.Count == 0) return;
            fistMesh ??= WeaponModel.loadMesh("mitts/fist");
            var rootM = ThreeSpace.root.localToWorldMatrix;
            var rp = new RenderParams(CharacterMats.plastic) { shadowCastingMode = ShadowCastingMode.On, receiveShadows = true };
            int n = 0;
            foreach (var f in fists)
            {
                if (n++ >= MAX_FISTS) break;
                // knuckles along the flight, kept level; pops out of the glove, wobbles like a jelly
                Vector3 z = f.dir, x = Vector3.Cross(UP, z); if (x.sqrMagnitude < 1e-6f) x = new Vector3(1, 0, 0); x.Normalize();
                Vector3 y = Vector3.Cross(z, x);
                float grow = Mathf.Min(1, 0.45f + f.age / 0.05f * 0.55f), wob = Mathf.Sin(f.age * 46 + f.seed * 6) * 0.1f, k = 1.25f * grow;
                rp.material = CharacterMats.fist(f.owner != null ? f.owner.color : Color.white);
                Graphics.RenderMesh(rp, fistMesh, 0, rootM * Matrix4x4.TRS(f.pos, Quaternion.LookRotation(z, y), new Vector3(k * (1 + wob * 0.6f), k * (1 + wob * 0.6f), k * (1 - wob))));
            }
        }

        // ============================================================================================ leap
        /** where a leap comes down (predictLeap): kind 'ground' | 'wall' (clingable) | 'bump' | 'sea' | 'none' */
        public class Land { public string kind = "none"; public Vector3 pos, normal = Vector3.up, hitPt; public int block = -1, n; public float t; }

        // Launch velocity for a charge: speed² eases linearly with the charge (distance ∝ v² on flat ground), heading = the
        // aim yaw, elevation = leapAngle + aim pitch × leapPitchK (clamped), the vertical speed capped. Off a wall: never into it.
        static Vector3 leapVelocity(Actor a, MittsState k, float c)
        {
            var w = W;
            float yaw = k.botWant >= 0 ? k.botYaw : a.aimYaw, pitch = k.botWant >= 0 ? k.botPitch : a.aimPitch;
            float ang = clamp(w.leapAngle * DEG + pitch * w.leapPitchK, w.leapAngleMin * DEG, w.leapAngleMax * DEG);
            float v = Mathf.Sqrt(lerp(w.leapSpeedMin * w.leapSpeedMin, w.leapSpeedMax * w.leapSpeedMax, clamp(c, 0, 1)));
            float vy = Mathf.Min(w.leapVyMax, v * Mathf.Sin(ang)), vh = v * Mathf.Cos(ang);
            float dx = Mathf.Sin(yaw), dz = Mathf.Cos(yaw);
            if (k.cling)
            {
                Vector3 n = k.clingN; float dn = dx * n.x + dz * n.z;
                if (dn < 0.35f) { dx += n.x * (0.35f - dn); dz += n.z * (0.35f - dn); float l = Mathf.Sqrt(dx * dx + dz * dz); if (l == 0) l = 1; dx /= l; dz /= l; }
            }
            return new Vector3(dx * vh, vy, dz * vh);
        }

        // Where a leap from `from` with velocity v0 comes down: the actor's own integrator (gravity with the fall / apex
        // multipliers; the kit holds the horizontal speed) swept against the level — the chest for walls, the feet for ground,
        // the head for ceilings. pts (optional) gets the path for the arc preview (o.n points).
        const float PRED_DT = 1 / 60f; const int PRED_PTS = 64;
        static Land predictLeap(Vector3 from, Vector3 v0, Land o, Vector3[] pts)
        {
            float x = from.x, y = from.y, z = from.z, vy = v0.y;
            float vx = v0.x, vz = v0.z, hs = Mathf.Sqrt(vx * vx + vz * vz), dx = hs > 1e-4f ? vx / hs : 0, dz = hs > 1e-4f ? vz / hs : 0;
            o.kind = "none"; o.n = 0; o.block = -1;
            void push() { if (pts != null && o.n < PRED_PTS) { pts[o.n] = new Vector3(x, y + 0.35f, z); o.n++; } }
            push();
            for (int i = 1; i <= 210; i++)
            {
                float g = PLAYER.gravity; if (vy < 0) g *= PLAYER.fallGravityMul; if (Mathf.Abs(vy) < PLAYER.apexBand) g *= PLAYER.apexGravityMul;
                vy = Mathf.Max(-PLAYER.maxFall, vy - g * PRED_DT);
                float nx = x + vx * PRED_DT, nz = z + vz * PRED_DT;
                float ny = y + vy * PRED_DT;
                // head against a ceiling: stop rising
                if (vy > 0)
                {
                    var hc = G.physics.segment(new Vector3(x, y + PLAYER.height, z), new Vector3(nx, ny + PLAYER.height, nz), _hitP, false);
                    if (hc.hit && hc.normal.y < -0.5f) { vy = 0; ny = y; }
                }
                // chest against a wall
                var h = G.physics.segment(new Vector3(x, y + 0.8f, z), new Vector3(nx + dx * PLAYER.radius, ny + 0.8f, nz + dz * PLAYER.radius), _hitP, false);
                if (h.hit && Mathf.Abs(h.normal.y) < 0.55f)
                {
                    o.t = i * PRED_DT; Vector3 nn = h.normal; nn.y = 0; o.normal = nn.normalized; o.block = h.block;
                    o.hitPt = new Vector3(h.point.x, ny + 0.8f, h.point.z);
                    o.pos = new Vector3(h.point.x + o.normal.x * PLAYER.radius, ny, h.point.z + o.normal.z * PLAYER.radius);
                    o.kind = clingableAt(o.pos, o.normal, h.block) ? "wall" : "bump";
                    x = o.pos.x; y = ny; z = o.pos.z; push();
                    return o;
                }
                // feet onto the ground (falling)
                if (vy <= 0.5f)
                {
                    h = G.physics.segment(new Vector3(x, y + 0.3f, z), new Vector3(nx, ny - 0.02f, nz), _hitP, false);
                    if (h.hit && h.normal.y > 0.55f)
                    {
                        o.kind = "ground"; o.pos = h.point; o.normal = h.normal; o.hitPt = h.point; o.block = h.block; o.t = i * PRED_DT;
                        x = h.point.x; y = h.point.y; z = h.point.z; push();
                        return o;
                    }
                }
                x = nx; y = ny; z = nz;
                if ((i & 1) == 0) push();
                if (y < PLAYER.fallDeathY && float.IsNegativeInfinity(G.level.groundHeight(x, z, y + 0.6f)))
                {
                    o.kind = "sea"; o.pos = new Vector3(x, PLAYER.waterY, z); o.normal = Vector3.up; o.hitPt = o.pos; o.t = i * PRED_DT;
                    return o;
                }
            }
            o.pos = new Vector3(x, y, z);
            return o;
        }

        // a wall a sponge can stick to: solid (not a railing / grate), rising past the head, with no floor right under the feet
        static bool clingableAt(Vector3 pos, Vector3 n, int blockId)
        {
            var b = blockId >= 0 ? G.level.blocks[blockId] : null;
            if (b != null && (b.rail || b.grate)) return false;
            var h = G.physics.raycast(new Vector3(pos.x, pos.y + 1.35f, pos.z), new Vector3(-n.x, 0, -n.z), PLAYER.radius + 0.45f, _hitC, false);
            if (!h.hit || Mathf.Abs(h.normal.y) > 0.5f) return false;
            return !G.physics.raycast(new Vector3(pos.x, pos.y + 0.2f, pos.z), DOWN, 0.5f, _hitC, false).hit;
        }

        static bool startCharge(WeaponRunner r, MittsState k)
        {
            var a = r.a; var w = W;
            if (k.charging || k.leaping) return true;
            if (a.ink < w.leapInkMin) { r.empty(); return false; }
            k.charging = true; k.charge = 0; k.chargeT = 0; k.chargeAir = 0; k.full = false;
            if (a.isLocal || near(a.pos)) k.loop = G.audio?.loop("mitts_charge", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = a.isLocal ? 0.5f : 0.35f, pitch = 1 });
            return true;
        }
        static void cancelCharge(WeaponRunner r, MittsState k)
        {
            k.charging = false; k.charge = 0; k.chargeT = 0; k.botWant = -1; r.charge = 0;
            k.loop?.stop(0.08f); k.loop = null;
        }

        static void releaseLeap(WeaponRunner r, MittsState k)
        {
            var a = r.a; var w = W;
            float c = k.charge;
            k.charging = false; r.charge = 0;
            k.loop?.stop(0.05f); k.loop = null;
            a.ink = Mathf.Max(0, a.ink - lerp(w.leapInkMin, w.leapInkMax, c));
            a.lastFire = 0;
            Vector3 v = leapVelocity(a, k, c);
            bool fromWall = k.cling;
            if (fromWall) { k.cling = false; k.hang = false; k.drain?.stop(0.1f); k.drain = null; }
            k.botWant = -1;
            a.vel = v; a.grounded = false; a.coyote = 0; a.jumpBuffer = 0;
            k.leaping = true; k.leapT = 0; k.slid = false; k.leapV = new Vector3(v.x, 0, v.z); k.leapH = Mathf.Sqrt(v.x * v.x + v.z * v.z);
            k.trail = 0; k.trailP = a.pos; k.predT = 0;
            predictLeap(a.pos, v, k.land, null);
            a.character.trigger("jump");
            leapFx(a, a.pos, c, fromWall ? k.clingN : (Vector3?)null);
            // push-off splat
            if (!fromWall) credit(a, G.paint.splat(new Vector3(a.pos.x, a.pos.y + 0.25f, a.pos.z), 0.9f, a.team, new SplatOpts { seed = random() }));
            emit("mitts:leap", new Ev { actor = a, pos = a.pos, to = k.land.pos, kind = k.land.kind, charge = c });
            Actor.rumble(a, 0.25f, 0.35f, 110);
        }

        // the launch: whoosh + a ring of ink off the deck (or a burst off the wall it pushed away from: wallN)
        static void leapFx(Actor a, Vector3 pos, float c, Vector3? wallN)
        {
            if (!(a.isLocal || near(pos))) return;
            Color col = a.color;
            G.audio?.play("mitts_leap", new AudioOpts { pos = a.isLocal ? (Vector3?)null : pos, volume = a.isLocal ? 0.75f : 0.55f, pitch = 1.05f - 0.15f * c });
            if (!wallN.HasValue)
            {
                G.fx?.burst(new Vector3(pos.x, pos.y + 0.08f, pos.z), UP, col, new FxOpts { count = 14, speed = 5, size = 0.08f, spread = 0.7f });
                G.fx?.ring(pos, UP, col, new FxOpts { radius = 1.3f, life = 0.35f });
            }
            else G.fx?.burst(new Vector3(pos.x, pos.y + 1, pos.z), wallN.Value, col, new FxOpts { count = 12, speed = 4, size = 0.08f });
        }
        static void landFx(Actor a, Vector3 at, Vector3 n, Vector3 c)
        {
            var w = W; Color col = a.color;
            if (a.isLocal || near(c, 40))
            {
                G.fx?.explosion(c, col, 1.9f);
                if (n.y > 0.7f) G.fx?.superJumpLand(at, col);
                else G.fx?.ring(at, n, col, new FxOpts { radius = w.landRadius, life = 0.45f });
                G.audio?.play("mitts_land", new AudioOpts { pos = c, volume = a.isLocal ? 0.95f : 0.75f });
            }
            emit("shake", new Ev { pos = c, amount = 0.35f });
        }
        // in flight: the landing zone marked on the ground in the leaper's ink, for everyone (re-aimed now and then in case
        // something nudged the flight)
        static void telegraph(Actor a, MittsState k, WeaponDef w, float dt)
        {
            k.predT -= dt;
            if (k.predT <= 0) { k.predT = 0.1f; predictLeap(a.pos, a.vel, k.land, null); }
            var L = k.land;
            if (L.kind == "ground" || L.kind == "wall")
            {
                Vector3 at = L.kind == "wall" ? L.hitPt : L.pos, n = L.normal;
                G.fx?.mark(at + n * 0.03f, n, a.color, w.landRadius, 3, 0.95f, G.time % 1, 1.2f, 0.5f);
                G.fx?.mark(at + n * 0.035f, n, a.color, w.landRadius * 0.45f * (0.8f + 0.2f * Mathf.Sin(G.time * 20)), 4, 0.8f, G.time % 1, 1, 0.3f);
            }
        }
        // the leap's splash where it comes down (ground or wall): damage ring + big splat + FX
        static void landSplash(Actor a, Vector3 at, Vector3 n)
        {
            var w = W; Color col = a.color;
            if (w.landInvuln > 0) a.invuln = Mathf.Max(a.invuln, w.landInvuln);   // a moment untouchable on landing (it flickers)
            // the gloves themselves: whoever it comes down on takes a glove smash on top of the splash
            if (w.gloveDamage > 0) foreach (var e in G.actors)
            {
                if (e.team == a.team || !e.alive) continue;
                float reach = w.gloveRadius + PLAYER.radius;
                if (Mathf.Sqrt((e.pos.x - at.x) * (e.pos.x - at.x) + (e.pos.z - at.z) * (e.pos.z - at.z)) > reach || Mathf.Abs(e.pos.y + e.smoothY + 0.7f - at.y) > 1.6f) continue;
                G.projectiles.applyHit(a, e, w.gloveDamage, "mitts");
                if (a.isLocal || e.isLocal || near(e.pos, 30)) G.fx?.burst(new Vector3(e.pos.x, e.pos.y + 0.8f, e.pos.z), UP, col, new FxOpts { count = 10, speed = 3.6f, size = 0.09f });
            }
            Vector3 c = at + n * 0.35f;
            float area = G.paint.splat(c, w.landPaint, a.team, new SplatOpts { seed = random() });
            Vector3 xa = new Vector3(n.y > 0.7f ? 1 : -n.z, 0, n.y > 0.7f ? 0 : n.x).normalized, ya = Vector3.Cross(n, xa);
            for (int i = 0; i < 6; i++)
            {
                float ang = (i / 6f) * Mathf.PI * 2 + random() * 0.5f, rr = w.landPaint * (0.75f + random() * 0.35f);
                // round the contact, in the surface plane
                area += G.paint.splat(c + xa * (Mathf.Cos(ang) * rr) + ya * (Mathf.Sin(ang) * rr), 0.55f + random() * 0.35f, a.team, new SplatOpts { seed = random() });
            }
            credit(a, area);
            foreach (var e in G.actors)
            {
                if (e.team == a.team || !e.alive) continue;
                Vector3 v = e.pos; v.y += e.smoothY + (e.form == "squid" ? 0.3f : 0.7f);
                float d = Vector3.Distance(v, c);
                if (d > w.landRadius || !G.physics.los(c, v)) continue;
                G.projectiles.applyHit(a, e, lerp(w.landDamageMax, w.landDamageMin, clamp((d - 0.6f) / (w.landRadius - 0.6f), 0, 1)), "mitts");
            }
            // (specials.js areaHit / Boss Battle splash — with those modules)
            landFx(a, at, n, c);
            emit("mitts:land", new Ev { actor = a, pos = c, normal = n, radius = w.landRadius });
            Actor.rumble(a, 0.45f, 0.5f, 160);
        }

        static void startCling(WeaponRunner r, MittsState k, Vector3 n)
        {
            var a = r.a;
            k.cling = true; k.hang = true; k.hasCling = true; k.leaping = false; k.clingT = 0; k.dryWarn = 0;
            k.botHold = 1.3f + random() * 1.7f;   // bots: how long to hang on before leaping / dropping
            n.y = 0; k.clingN = n.normalized;
            k.clingPt = a.pos - k.clingN * PLAYER.radius; k.clingPt.y = a.pos.y + 1.0f;
            a.vel = Vector3.zero; a.grounded = false;
            k.landT = 0;
            landSplash(a, k.clingPt, k.clingN);
            if (a.isLocal || near(a.pos))
            {
                G.audio?.play("mitts_cling", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = a.isLocal ? 0.8f : 0.6f });
                k.drain = G.audio?.loop("mitts_drain", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = a.isLocal ? 0.35f : 0.22f });
            }
            emit("mitts:cling", new Ev { actor = a, on = true, pos = k.clingPt, normal = k.clingN });
        }
        // let go of the wall: 'jump' hops off it, 'empty' / 'squid' / anything else just drops
        static void dropCling(WeaponRunner r, MittsState k, string why)
        {
            var a = r.a;
            if (!k.cling) return;
            k.cling = false; k.hang = false;
            k.drain?.stop(0.12f); k.drain = null;
            Vector3 n = k.clingN;
            if (why == "jump") { a.vel = new Vector3(n.x * 3.4f, 4.6f, n.z * 3.4f); a.character.trigger("jump"); }
            else if (why != "squid") a.vel = new Vector3(n.x * 1.2f, 0.5f, n.z * 1.2f);
            a.grounded = false;
            if (a.isLocal || near(a.pos)) G.audio?.play("mitts_unstick", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = a.isLocal ? 0.7f : 0.5f, pitch = why == "empty" ? 0.85f : 1 });
            emit("mitts:cling", new Ev { actor = a, on = false, why = why });
        }

        // ============================================================================================ local preview
        // While the local player charges: an arc along the leap path and a ring where it comes down (an X over the sea).
        static LineRenderer prevLine; static Transform prevRing, prevX; static Material prevMat, prevRingMat; static float prevShown;
        static readonly Vector3[] prevPts = new Vector3[PRED_PTS], prevU = new Vector3[PRED_PTS];
        static readonly Land prevOut = new Land();
        static void ensurePreview()
        {
            if (prevLine != null) return;
            var unlit = Shader.Find("Universal Render Pipeline/Unlit");
            prevMat = new Material(unlit) { color = new Color(1, 1, 1, 0.9f), name = "iw-mitts-arc" };
            prevRingMat = new Material(unlit) { color = Color.white, name = "iw-mitts-ring" }; prevRingMat.SetFloat("_Cull", 0);
            var lg = ThreeSpace.create("mittsArc");
            prevLine = lg.AddComponent<LineRenderer>(); prevLine.useWorldSpace = true; prevLine.widthMultiplier = 0.05f; prevLine.material = prevMat;
            prevLine.shadowCastingMode = ShadowCastingMode.Off; prevLine.enabled = false;
            // landing ring (the splash radius, unit size: scaled per frame) + a centre dot; a red X for the sea
            prevRing = ThreeSpace.create("mittsRing").transform;
            var rg = new GameObject("ring"); rg.transform.SetParent(prevRing, false);
            rg.AddComponent<MeshFilter>().sharedMesh = annulus(0.86f, 1.12f, 56); rg.AddComponent<MeshRenderer>().sharedMaterial = prevRingMat;
            var dg = new GameObject("dot"); dg.transform.SetParent(prevRing, false);
            dg.AddComponent<MeshFilter>().sharedMesh = annulus(0, 0.1f, 20); dg.AddComponent<MeshRenderer>().sharedMaterial = prevRingMat;
            prevX = ThreeSpace.create("mittsX").transform;
            var xm = new Material(unlit) { color = new Color(1, 0.35f, 0.29f), name = "iw-mitts-x" }; xm.SetFloat("_Cull", 0);
            foreach (float r in new[] { Mathf.PI / 4, -Mathf.PI / 4 })
            {
                var bar = new GameObject("bar"); bar.transform.SetParent(prevX, false);
                bar.transform.localRotation = Quaternion.AngleAxis(r * Mathf.Rad2Deg, Vector3.forward); bar.transform.localScale = new Vector3(1.4f, 0.2f, 1);
                bar.AddComponent<MeshFilter>().sharedMesh = Geo.box(1, 1, 0.01f).toMesh("xbar"); bar.AddComponent<MeshRenderer>().sharedMaterial = xm;
            }
            prevRing.gameObject.SetActive(false); prevX.gameObject.SetActive(false);
        }
        // a flat annulus in the XY plane (faces +Z): the preview ring is aimed along the surface normal
        static Mesh annulus(float r0, float r1, int segs)
        {
            var pos = new List<Vector3>(); var idx = new List<int>();
            for (int i = 0; i <= segs; i++)
            {
                float t = (float)i / segs * Mathf.PI * 2, c = Mathf.Cos(t), s = Mathf.Sin(t);
                pos.Add(new Vector3(c * r0, s * r0, 0)); pos.Add(new Vector3(c * r1, s * r1, 0));
                if (i < segs) { int a = i * 2; idx.AddRange(new[] { a, a + 1, a + 2, a + 1, a + 3, a + 2 }); }
            }
            var m = new Mesh { name = "annulus" }; m.SetVertices(pos); m.SetTriangles(idx, 0); m.RecalculateNormals();
            return m;
        }
        static void showPreview(Actor a, MittsState k)
        {
            ensurePreview();
            predictLeap(a.pos, leapVelocity(a, k, k.charge), prevOut, prevPts);
            for (int i = 0; i < prevOut.n; i++) prevU[i] = ThreeSpace.toUnity(prevPts[i]);
            prevLine.positionCount = prevOut.n; prevLine.SetPositions(prevU);
            prevLine.enabled = prevOut.n > 1;
            bool good = prevOut.kind == "ground" || prevOut.kind == "wall";
            float R = W.landRadius / 1.12f;
            prevRing.gameObject.SetActive(good); prevX.gameObject.SetActive(prevOut.kind == "sea");
            if (good)
            {
                Vector3 n = prevOut.normal;
                prevRing.localPosition = (prevOut.kind == "wall" ? prevOut.hitPt : prevOut.pos) + n * 0.04f;
                prevRing.localRotation = Quaternion.FromToRotation(Vector3.forward, n);
                prevRing.localScale = Vector3.one * (R * (0.96f + 0.04f * Mathf.Sin(G.time * 12)));
                prevRingMat.color = Color.Lerp(Color.white, a.color, k.charge >= 0.999f ? 0.55f : 0.2f);
            }
            if (prevOut.kind == "sea") { prevX.localPosition = new Vector3(prevOut.pos.x, PLAYER.waterY + 0.05f, prevOut.pos.z); prevX.localRotation = Quaternion.FromToRotation(Vector3.forward, UP); }
            prevShown = G.time;
        }
        static void hidePreview() { if (prevLine == null) return; prevLine.enabled = false; prevRing.gameObject.SetActive(false); prevX.gameObject.SetActive(false); }

        // ============================================================================================ the runner
        public override void update(WeaponRunner r, float dt, WeaponInput inp, WeaponDef w)
        {
            var a = r.a; var k = K(r);
            // the body was owned by something else (a body special, a super jump …): nothing carries over
            if (k.seen > 0 && G.time - k.seen > 0.25f) { if (k.charging) cancelCharge(r, k); k.leaping = false; if (k.cling) dropCling(r, k, "reset"); }
            k.seen = G.time;
            k.cd -= dt; k.landT += dt;
            if (a.specialActive != null && k.cling) dropCling(r, k, "special");
            if (a.form == "squid") { if (k.cling) dropCling(r, k, "squid"); if (k.charging) cancelCharge(r, k); }

            // ---- leap charge (started by fire + jump — see jump() — or by a bot)
            if (k.charging)
            {
                k.chargeT += dt;
                float maxC = clamp((a.ink - w.leapInkMin) / (w.leapInkMax - w.leapInkMin), 0, 1);
                k.charge = Mathf.Min(maxC, k.chargeT / w.leapChargeTime);
                r.charge = k.charge;
                a.lastFire = 0; a.fireFacing = 0.5f;
                k.loop?.set(pitch: 0.8f + 0.9f * k.charge, pos: a.isLocal ? (Vector3?)null : a.pos);
                if (k.charge >= 0.999f && !k.full)
                {
                    k.full = true;
                    if (a.isLocal || near(a.pos)) G.audio?.play("mitts_full", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = a.isLocal ? 0.6f : 0.4f });
                    Actor.rumble(a, 0.05f, 0.3f, 60);
                }
                if (near(a.pos, 26)) for (int h = 0; h < 2; h++) G.fx?.chargeGlow(gloveMuzzle(a, h), a.color, 0.3f + 0.7f * k.charge);
                // walked off a ledge mid-charge: the crouch has nothing to push off
                if (!a.grounded && !k.cling) { k.chargeAir += dt; if (k.chargeAir > 0.12f) cancelCharge(r, k); } else k.chargeAir = 0;
                if (k.charging)
                {
                    bool go = k.botWant >= 0 ? k.charge >= Mathf.Min(k.botWant, maxC) - 1e-3f || k.chargeT > w.leapChargeTime + 0.6f : !a.intent.jump;
                    if (go && k.botWant >= 0 && !botReleaseOk(a, k)) cancelCharge(r, k);   // a bot never leaps somewhere it didn't plan
                    else if (go) releaseLeap(r, k);
                    else if (a.isLocal && !a.isBot && a.bot == null) showPreview(a, k);
                }
            }

            // ---- leap in flight (after this frame's integrate: contacts are fresh)
            if (k.leaping)
            {
                k.leapT += dt;
                if (a.grounded)
                {
                    k.leaping = false; k.landT = 0;
                    landSplash(a, a.pos, a.groundN.y > 0.5f ? a.groundN : UP);
                }
                else if (a.form != "kid" || k.leapT > 4) k.leaping = false;
                else
                {
                    var c = a.contacts;
                    if (!k.slid && c.wall && k.leapT > 0.03f)
                    {
                        float into = -(k.leapV.x * c.wallNormal.x + k.leapV.z * c.wallNormal.z);
                        if (into > 1.5f && clingableAt(a.pos, c.wallNormal, c.wallBlock)) startCling(r, k, c.wallNormal);
                        else k.slid = true;   // a wall it can't stick to: physics takes it from here
                    }
                    if (k.leaping)
                    {
                        // hold the launch's horizontal speed (air drag / steering never bend the pounce) unless something else moved us
                        if (!k.slid && Mathf.Sqrt((a.vel.x - k.leapV.x) * (a.vel.x - k.leapV.x) + (a.vel.z - k.leapV.z) * (a.vel.z - k.leapV.z)) > 2.2f) k.slid = true;
                        if (!k.slid) { a.vel.x = k.leapV.x; a.vel.z = k.leapV.z; }
                        a.fireFacing = 0.3f; a.lastFire = 0;
                        // trail: a few drips painted under the path, droplets + glow off the body
                        k.trail += Vector3.Distance(a.pos, k.trailP); k.trailP = a.pos;
                        if (k.trail > 1.6f)
                        {
                            k.trail = 0;
                            var g = G.physics.raycast(new Vector3(a.pos.x, a.pos.y + 0.3f, a.pos.z), DOWN, 5, _hit, true);
                            if (g.hit) credit(a, G.paint.splat(g.point + g.normal * 0.08f, 0.42f + random() * 0.15f, a.team, new SplatOpts { seed = random() }));
                        }
                        if (near(a.pos, 34)) G.fx?.superJumpTrail(new Vector3(a.pos.x, a.pos.y + 0.8f, a.pos.z), a.vel, a.color);
                        telegraph(a, k, w, dt);
                    }
                }
            }

            // ---- wall cling
            if (k.cling)
            {
                k.clingT += dt;
                a.vel = Vector3.zero;
                a.lastFire = 0;                                   // holding on: no idle refill
                a.ink = Mathf.Max(0, a.ink - w.clingDrain * dt);
                k.drain?.set(volume: (a.isLocal ? 0.35f : 0.22f) * (0.6f + 0.4f * Mathf.Sin(G.time * 7)), pos: a.isLocal ? (Vector3?)null : a.pos);
                if (a.isLocal && a.ink < 20 && (k.dryWarn -= dt) <= 0) { k.dryWarn = 0.8f; emit("lowink", new Ev { actor = a }); }
                if (near(a.pos, 20) && random() < dt * 5) G.fx?.climbDrip(k.clingPt, k.clingN, a.color);
                // let go: out of ink, landed on something, or the wall's gone
                bool wallThere = G.physics.raycast(new Vector3(a.pos.x, a.pos.y + 1.0f, a.pos.z), new Vector3(-k.clingN.x, 0, -k.clingN.z), PLAYER.radius + 0.5f, _hitC, false).hit;
                if (a.ink <= 0) dropCling(r, k, "empty");
                else if (a.grounded || !wallThere || !a.alive) dropCling(r, k, "lost");
                else if (a.isBot && (k.botHold -= dt) < -0.6f) dropCling(r, k, "jump");   // (bot code leaps / lets go at 0; this is the backstop)
            }

            // ---- punches (not while charging or mid-leap; from the wall only the right glove is free)
            if (!inp.fire || k.charging || k.leaping) { if (k.cd < 0) k.cd = 0; }
            else
            {
                r.firingT = 0.35f; a.fireFacing = 0.5f;
                int guard = 0;
                while (k.cd <= 0 && guard++ < 2)
                {
                    if (a.ink < w.inkPerPunch) { r.empty(); k.cd += w.punchInterval; break; }
                    a.ink -= w.inkPerPunch; a.lastFire = 0;
                    k.side = k.cling ? 0 : 1 - k.side;
                    int hand = (int)k.side;
                    fireFist(a, w, hand);
                    r.sinceHand[hand] = 0;
                    a.character.trigger("shoot", hand == 1 ? 2 : 1);   // (2 = the left glove)
                    k.cd += w.punchInterval;
                }
            }
        }

        // fire + jump → charge a leap (grounded, or stuck to a wall); jump alone on a wall → let go. A press mid-charge /
        // mid-leap is swallowed (it must never turn into a jump that overrides the leap). Bots start leaps via bot code only.
        public override bool jump(WeaponRunner r, Intent intent)
        {
            var a = r.a; var k = r.kit as MittsState;
            bool bot = a.isBot || a.bot != null;   // (the local player on autopilot plays like a bot)
            if (k != null && (k.charging || k.leaping)) return true;
            if (k != null && k.cling)
            {
                if (intent.fire && !bot && a.ink >= W.leapInkMin) startCharge(r, k);
                else if (!bot || !intent.fire) dropCling(r, k, "jump");
                return true;
            }
            if (bot || !intent.fire || !a.grounded || a.form != "kid") return false;
            return startCharge(r, K(r));
        }

        // ============================================================================================ bots
        // Close in and punch (MELEE: the brain walks them straight at the target); pounce in from mid range; leap away when
        // hurt and cornered; now and then leap onto a wall near the fight and punch down from it.
        static readonly Land _bp = new Land();
        class BotMem { public float leapCd = 1.5f + random() * 2, wallCd = 4 + random() * 4, travelCd = 4 + random() * 4, escapeCd, weaveT, weaveP = -1; }
        static BotMem botState(BotBrain brain) => brain.kitMem as BotMem ?? (BotMem)(brain.kitMem = new BotMem());
        // pick a charge that lands dist m out with the given yaw / pitch (two secant passes over the real prediction)
        static float botPlan(Actor a, MittsState k, float yaw, float pitch, float dist)
        {
            k.botYaw = yaw; k.botPitch = pitch;
            float c = clamp((dist - 4.5f) / 8.5f, 0, 1);
            for (int it = 0; it < 3; it++)
            {
                k.botWant = c;                                   // (leapVelocity reads the bot's yaw / pitch while botWant ≥ 0)
                predictLeap(a.pos, leapVelocity(a, k, c), _bp, null);
                if (it == 2) break;
                float got = Mathf.Sqrt((_bp.pos.x - a.pos.x) * (_bp.pos.x - a.pos.x) + (_bp.pos.z - a.pos.z) * (_bp.pos.z - a.pos.z));
                if (!(got > 0.5f)) break;
                c = clamp(c + (dist - got) / 8.5f, 0, 1);
            }
            k.botWant = -1;
            return c;
        }
        // a landing a bot may aim for: ground that is walkable nav (not a roof / perch / prop top it can't path off), not far
        // above or below
        static bool landingOk(Actor a, Land P)
        {
            if (P.kind != "ground") return false;
            var b = P.block >= 0 ? G.level.blocks[P.block] : null;
            if (b != null && (b.roof || b.perch || b.rail)) return false;
            if (Mathf.Abs(P.pos.y - a.pos.y) > 4) return false;
            if (G.nav == null) return true;
            int id = G.nav.nearest(P.pos, 0.6f);
            if (id < 0) return false;
            var n = G.nav.nodes[id];
            return Mathf.Sqrt((n.x - P.pos.x) * (n.x - P.pos.x) + (n.z - P.pos.z) * (n.z - P.pos.z)) < 1.3f && Mathf.Abs(n.y - P.pos.y) < 0.6f;
        }
        static bool botLeap(WeaponRunner r, MittsState k, float c, float yaw, float pitch, string kind = "ground")
        {
            k.botYaw = yaw; k.botPitch = pitch;
            if (!startCharge(r, k)) { k.botWant = -1; return false; }
            k.botWant = c; k.botKind = kind;
            return true;
        }
        // at release: does the leap still come down where the bot planned (it may have been nudged while crouching)?
        static bool botReleaseOk(Actor a, MittsState k)
        {
            predictLeap(a.pos, leapVelocity(a, k, k.charge), _bp, null);
            return k.botKind == "wall" ? _bp.kind == "wall" : landingOk(a, _bp);
        }
        // a wall near the fight worth sticking to: probe round the bot at chest and head height
        static bool botFindWall(Actor a, Actor target, out float wyaw)
        {
            wyaw = 0; bool found = false; float bestS = float.PositiveInfinity;
            for (int i = 0; i < 10; i++)
            {
                float yaw = (i / 10f) * Mathf.PI * 2 + random() * 0.3f;
                Vector3 o = new Vector3(a.pos.x, a.pos.y + 1.2f, a.pos.z), d = new Vector3(Mathf.Sin(yaw), 0, Mathf.Cos(yaw));
                var h = G.physics.raycast(o, d, 9, _hitC, false);
                if (!h.hit || h.dist < 3.5f || Mathf.Abs(h.normal.y) > 0.3f) continue;
                float hd = h.dist; Vector3 hp = h.point;
                o.y += 1.6f;
                var h2 = G.physics.raycast(o, d, 9.5f, _hitP, false);
                if (!h2.hit || Mathf.Abs(h2.dist - hd) > 0.6f) continue;           // needs height above the cling point
                float s = Mathf.Sqrt((hp.x - target.pos.x) * (hp.x - target.pos.x) + (hp.z - target.pos.z) * (hp.z - target.pos.z));
                if (s < 7 && s < bestS) { bestS = s; wyaw = yaw; found = true; }
            }
            return found;
        }
        // own ink along (nx, nz) from the kid, as far as d m ahead: a lane to swim in on
        static readonly RegionStats _rs = new RegionStats();
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
        // The approach is the Mitts' whole fight: most of their splats happen before they reach anyone. bot.tactics runs
        // every fight frame after the brain's defaults (which walk a melee bot straight at the target) and takes over from
        // 5–14 m: own ink running toward the target → swim it in; no lane → punch while closing (the fists paint the way and
        // the sponge guard is up); both weave across the line to them instead of walking a straight, easy-to-track line.
        static void botTactics(BotBrain brain, BotCtx ctx)
        {
            var a = ctx.a; var w = ctx.w; float dist = ctx.dist; var it = ctx.it; var t = ctx.target;
            if (w.botApproach == 0 || ctx.retreat || t == null || !ctx.visible) return;
            var k = K(a.weaponRunner);
            if (k.charging || k.leaping || k.cling || dist <= w.range * 1.05f || dist > 14) return;
            var B = botState(brain);
            B.weaveT += ctx.dt;
            if (B.weaveP < 0) B.weaveP = random() * 6.28f;
            float nx = (t.pos.x - a.pos.x) / dist, nz = (t.pos.z - a.pos.z) / dist;
            // heading: straight at them when the way is open (the brain's melee walk), else the nav path's; plus the weave
            float ux = nx, uz = nz;
            float ml = Mathf.Sqrt(ctx.move.x * ctx.move.x + ctx.move.z * ctx.move.z);
            if (dist > 7 && ml > 0.1f) { ux = ctx.move.x / ml; uz = ctx.move.z / ml; }
            float sway = Mathf.Sin(B.weaveT * 4.2f + B.weaveP) * 0.6f;
            float mx = ux - uz * sway, mz = uz + ux * sway, l = Mathf.Sqrt(mx * mx + mz * mz); if (l == 0) l = 1;
            ctx.move = new Vector3(mx / l, 0, mz / l);
            if (a.groundTeam == 1 && ownLane(a, nx, nz, Mathf.Min(dist - w.range, 5))) { it.squid = true; it.fire = false; return; }
            if (a.ink > w.inkPerPunch * 6) { it.squid = false; it.fire = true; }
        }
        static bool botFight(BotBrain brain, BotCtx ctx)
        {
            var a = ctx.a; var w = ctx.w; float dist = ctx.dist, dt = ctx.dt; var t = ctx.target;
            var r = a.weaponRunner; var k = K(r); var B = botState(brain);
            B.leapCd -= dt; B.wallCd -= dt; B.escapeCd -= dt;
            if (k.charging || k.leaping) return false;
            bool inReach = dist < w.range * 1.05f;
            if (k.cling)
            {
                // punch down from the wall while they're in reach; after a while leap at them (or drop)
                if (k.botHold <= 0 || a.ink < 18)
                {
                    if (a.ink > w.leapInkMax + 8 && dist > 4 && dist < 12)
                    {
                        float yaw = Mathf.Atan2(t.pos.x - a.pos.x, t.pos.z - a.pos.z);
                        float c = botPlan(a, k, yaw, -0.1f, dist);
                        if (landingOk(a, _bp) && botLeap(r, k, c, yaw, -0.1f)) { B.leapCd = 3 + random() * 3; return false; }
                    }
                    dropCling(r, k, "jump");
                }
                return inReach;
            }
            float hp = a.hp / PLAYER.hp;
            bool clear = G.physics.los(new Vector3(a.pos.x, a.pos.y + 1.1f, a.pos.z), new Vector3(t.pos.x, t.pos.y + 1.0f, t.pos.z));
            // escape: hurt and in their face → leap away (never into the sea / onto a roof)
            if (a.grounded && hp < 0.35f && dist < 5 && a.ink > w.leapInkMax && B.escapeCd <= 0 && random() < dt * 3)
            {
                B.escapeCd = 3;
                float away = Mathf.Atan2(a.pos.x - t.pos.x, a.pos.z - t.pos.z) + (random() - 0.5f) * 1.2f;
                float c = botPlan(a, k, away, 0.05f, 11);
                if (landingOk(a, _bp) && botLeap(r, k, c, away, 0.05f)) { B.leapCd = 4; return false; }
            }
            // pounce from mid range
            if (a.grounded && B.leapCd <= 0 && dist > 5.5f && dist < 12.5f && a.ink >= w.leapInkMax + 8 && clear && Mathf.Abs(t.pos.y - a.pos.y) < 2.5f && random() < dt * 3)
            {
                B.leapCd = 2 + random() * 2;
                const float lead = 0.8f;
                float tx = t.pos.x + t.vel.x * lead, tz = t.pos.z + t.vel.z * lead;
                float yaw = Mathf.Atan2(tx - a.pos.x, tz - a.pos.z), d = Mathf.Sqrt((tx - a.pos.x) * (tx - a.pos.x) + (tz - a.pos.z) * (tz - a.pos.z));
                float c = botPlan(a, k, yaw, 0, d);
                if (landingOk(a, _bp) && Mathf.Sqrt((_bp.pos.x - tx) * (_bp.pos.x - tx) + (_bp.pos.z - tz) * (_bp.pos.z - tz)) < 2.6f && botLeap(r, k, c, yaw, 0)) return false;
            }
            // now and then: onto a wall by the fight
            if (a.grounded && B.wallCd <= 0 && dist > 3 && dist < 11 && a.ink >= 70 && random() < dt * 0.7f)
            {
                B.wallCd = 7 + random() * 7;
                if (botFindWall(a, t, out float wyaw))
                {
                    foreach (float c in new[] { 0.35f, 0.55f, 0.75f, 1 })
                    {
                        k.botWant = c; k.botYaw = wyaw; k.botPitch = 0.3f;
                        predictLeap(a.pos, leapVelocity(a, k, c), _bp, null);
                        k.botWant = -1;
                        if (_bp.kind == "wall" && _bp.pos.y > a.pos.y + 0.8f)
                        {
                            if (botLeap(r, k, c, wyaw, 0.3f, "wall")) return false;
                            break;
                        }
                    }
                }
            }
            return inReach;
        }
        static bool botPaint(BotBrain brain, BotCtx ctx)
        {
            var a = ctx.a; float dt = ctx.dt;
            var r = a.weaponRunner; var k = K(r); var B = botState(brain);
            B.travelCd -= dt;
            if (k.charging || k.leaping) return false;
            if (k.cling) { if (k.botHold <= 0 || ctx.inkFrac < 0.15f) dropCling(r, k, "jump"); return ctx.inkFrac > 0.1f; }   // hang a moment, ink the wall + the ground below
            // cover ground: a long pounce along the path (paints the landing), with plenty of ink and a long way to go
            if (B.travelCd <= 0 && a.grounded && ctx.inkFrac > 0.7f && ctx.wantMove && brain.pathRemaining() > 14 && random() < dt * 0.8f)
            {
                B.travelCd = 6 + random() * 6;
                float yaw = Mathf.Atan2(ctx.move.x, ctx.move.z);
                float c = botPlan(a, k, yaw, 0, 11);
                if (landingOk(a, _bp) && botLeap(r, k, c, yaw, 0)) return false;
            }
            return ctx.inkFrac > 0.08f && (ctx.needPaint || random() < 0.02f) && ctx.wantMove;
        }

        // ============================================================================================ registration
        public Mitts()
        {
            bot = new KitBot { paintPitch = -0.32f, tactics = botTactics, fight = botFight, paint = botPaint, melee = true };
            BotBrain.MELEE.Add("mitts");
        }
        public override bool busy(WeaponRunner r) => r.kit is MittsState k && k.leaping;
        // leap armour: an incoming leap is otherwise picked off before it lands — mid-air (and on the landing beat) the kid
        // takes leapArmor of any damage. Sponge guard: punching with the gloves up (and a beat after), a hit from the front
        // (within guardArc° of the facing) takes guardArmor — the sponge soaks part of it (a squish off the gloves).
        public override float damageTaken(WeaponRunner r, float amount, Actor attacker, string source)
        {
            var k = r.kit as MittsState; var w = W;
            if (k != null && (k.leaping || k.landT < w.leapArmorGrace)) return amount * w.leapArmor;
            var a = r.a;
            if (k != null && !k.cling && a.form == "kid" && attacker != null && attacker != a && r.firingT > 0.35f - w.guardAfter)
            {
                float dx = attacker.pos.x - a.pos.x, dz = attacker.pos.z - a.pos.z, l = Mathf.Sqrt(dx * dx + dz * dz);
                if (l > 0.01f && (Mathf.Sin(a.aimYaw) * dx + Mathf.Cos(a.aimYaw) * dz) / l > Mathf.Cos(w.guardArc * DEG))
                {
                    if (a.isLocal || near(a.pos, 22))
                    {
                        float t = G.time;
                        if (!(k.squishT > t - 0.12f))
                        {   // (a spray of hits squishes once)
                            k.squishT = t;
                            Vector3 m = gloveMuzzle(a, (int)k.side);
                            G.fx?.burst(m, new Vector3(dx / l, 0.35f, dz / l), a.color, new FxOpts { count = 6, speed = 2.4f, size = 0.07f });
                            G.audio?.play("mitts_pop", new AudioOpts { pos = a.isLocal ? (Vector3?)null : m, volume = a.isLocal ? 0.35f : 0.25f, pitch = 0.7f });
                        }
                    }
                    return amount * w.guardArmor;
                }
            }
            return amount;
        }
        public override bool firingPose(WeaponRunner r) => r.kit is MittsState k && (k.charging || k.leaping || k.cling || k.landT < 0.35f);
        public override float moveSpeed(WeaponRunner r, WeaponDef w)
        {
            if (!(r.kit is MittsState k)) return 0;
            if (k.leaping) return Mathf.Max(k.leapH, PLAYER.airMinSpeed);
            if (k.charging) return r.a.isBot || r.a.bot != null ? 0.05f : w.moveSpeedCharging;   // bots crouch still (their plan assumes the spot)
            if (k.landT < 0.25f) return w.moveSpeedCharging * 2;
            return r.firingT > 0 ? w.moveSpeedFiring : 0;
        }
        public override float? spreadDeg(WeaponRunner r, WeaponDef w) => w.punchSpread;
        static readonly List<WeaponRunner> _gone = new List<WeaponRunner>();
        public override void tick(float dt)
        {
            // states their runner dropped (death, respawn, loadout swap): stop their sounds
            _gone.Clear();
            foreach (var kv in STATES) { var r = kv.Key; if (r.kit != kv.Value || !r.a.alive || r.a.weapon?.kind != "mitts") { stopLoops(kv.Value); kv.Value.hang = false; _gone.Add(r); } }
            foreach (var r in _gone) STATES.Remove(r);
            if (prevShown > 0 && G.time - prevShown > 0.05f) { hidePreview(); prevShown = 0; }
            updateFists(dt);
        }
        public override void clear()
        {
            foreach (var f in fists) { f.owner = null; fistPool.Push(f); }
            fists.Clear();
            foreach (var k in STATES.Values) stopLoops(k);
            STATES.Clear();
            hidePreview(); prevShown = 0;
        }
    }
}
