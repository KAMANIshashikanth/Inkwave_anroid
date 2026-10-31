// Port of src/game/kits/torpedo.js - Tide Torpedo (kit sub 'torpedo'), see Kits.cs.
// Thrown in the bomb arc, inks the thrower's feet, locks a foe in mid-air (dashed ring on their feet), unfolds its fins
// and swims at them. Locked bursts are wide and spray droplets; unlocked bursts are a plain bomb blast. Enemy fire shoots
// it down, lethal blasts set a locked one off, and one is allowed out per player. Net ghosts are offline-only omissions.
using System.Collections.Generic;
using Inkwave.World;
using UnityEngine;
using UnityEngine.Rendering;
using static Inkwave.Ctx;

namespace Inkwave
{
    public class Torpedo : SubKit
    {
        static readonly Vector3 UP = new Vector3(0, 1, 0), DOWN = new Vector3(0, -1, 0), ZAX = new Vector3(0, 0, 1);
        static readonly Hit _hit = new Hit(), _hit2 = new Hit();
        const float GRAV = 24;
        const float SCALE = 1.9f;
        const float HIT_R = 0.3f;
        const float YC = 0.034f;
        const float FIN_R = 0.0331f, FIN_Z0 = -0.016f, FIN_SPAN = 1.8f;
        static readonly Vector3 PROP_AT = new Vector3(0, YC, -0.0895f);
        const int MAX_DROPS = 96;
        static readonly int ID_Color = Shader.PropertyToID("_Color"), ID_Time = Shader.PropertyToID("_Time2"), ID_Alpha = Shader.PropertyToID("_Alpha");

        public Torpedo() { bot = new SubKitBot { fight = botFight, paint = botPaint }; }

        static bool nearCam(Vector3 p, float r = 34) => (G.camPos - p).sqrMagnitude < r * r;
        static float backOut(float t, float s = 2.2f) { t = clamp(t, 0, 1) - 1; return 1 + t * t * ((s + 1) * t + s); }
        static Vector3 chest(Actor e) => new Vector3(e.pos.x, e.pos.y + e.smoothY + (e.form == "squid" ? 0.3f : 0.8f), e.pos.z);
        static Vector3 hitBase(Actor e) => new Vector3(e.pos.x, e.pos.y + e.smoothY, e.pos.z);
        static float hitH(Actor e) => e.form == "squid" ? PLAYER.squidHeight : PLAYER.height;
        static Actor liveFoe(Actor e) => e != null && e.alive && e.superJumpState == null ? e : null;

        // ================================================================================================= model
        class WorldGeo { public Mesh hullBody, hullInk, finLocal, prop, glow; }
        static WorldGeo _wg;
        static WorldGeo worldGeo() => _wg ??= new WorldGeo
        {
            hullBody = WeaponModel.loadMesh("sub_torpedo/hullBody"), hullInk = WeaponModel.loadMesh("sub_torpedo/hullInk"),
            finLocal = WeaponModel.loadMesh("sub_torpedo/finLocal"), prop = WeaponModel.loadMesh("sub_torpedo/prop"),
            glow = WeaponModel.loadMesh("sub_torpedo/glow"),
        };
        class Rig
        {
            public Transform outer, model, prop; public Transform[] fins; public Material glowMat; public Color glowEm;
        }
        static float finAz(int k) => Mathf.PI / 4 + k * Mathf.PI / 2 - 0.47f;
        static Rig makeMesh(int team)
        {
            var g = worldGeo(); Color col = G.teamColors[team];
            var M = new Rig { outer = ThreeSpace.create("torpedo").transform };
            M.model = ThreeSpace.create("model", M.outer).transform;
            M.model.localScale = Vector3.one * SCALE;
            M.model.localPosition = new Vector3(0, -YC * SCALE, 0);
            part("body", M.model, g.hullBody, CharacterMats.plastic, true);
            part("ink", M.model, g.hullInk, CharacterMats.ink(col), true);
            M.glowEm = col;
            M.glowMat = CharacterMats.lampMaterial(new Color(0x11 / 255f, 0x11 / 255f, 0x11 / 255f), 0.3f);
            CharacterMats.setLamp(M.glowMat, col.linear, 1.2f);
            part("glow", M.model, g.glow, M.glowMat, false);
            float a0 = finAz(0);
            var piv0 = new Vector3(Mathf.Cos(a0) * FIN_R, YC + Mathf.Sin(a0) * FIN_R, FIN_Z0);
            M.fins = new Transform[4];
            for (int k = 0; k < 4; k++)
            {
                var arm = ThreeSpace.create("finArm", M.model).transform;
                arm.localPosition = new Vector3(0, YC, 0);
                arm.localRotation = Quaternion.AngleAxis((finAz(k) - a0) * Mathf.Rad2Deg, ZAX);
                var pv = ThreeSpace.create("fin", arm).transform;
                pv.localPosition = new Vector3(piv0.x, piv0.y - YC, piv0.z);
                part("finMesh", pv, g.finLocal, CharacterMats.ink(col), true);
                M.fins[k] = pv;
            }
            M.prop = ThreeSpace.create("prop", M.model).transform;
            M.prop.localPosition = PROP_AT;
            part("propMesh", M.prop, g.prop, CharacterMats.plastic, true);
            return M;
        }
        static MeshRenderer part(string name, Transform parent, Mesh m, Material mat, bool shadow)
        {
            var g = ThreeSpace.create(name, parent);
            g.AddComponent<MeshFilter>().sharedMesh = m;
            var r = g.AddComponent<MeshRenderer>(); r.sharedMaterial = mat;
            r.shadowCastingMode = shadow ? ShadowCastingMode.On : ShadowCastingMode.Off;
            return r;
        }
        static void poseRig(Item t)
        {
            var M = t.m;
            for (int i = 0; i < M.fins.Length; i++) M.fins[i].localRotation = Quaternion.AngleAxis(-FIN_SPAN * t.fin * Mathf.Rad2Deg, ZAX);
            float pk = lerp(0.38f, 1, t.propOpen);
            M.prop.localScale = new Vector3(pk, pk, 1);
            M.prop.localRotation = Quaternion.AngleAxis(t.propA * Mathf.Rad2Deg, ZAX);
            CharacterMats.setLamp(M.glowMat, M.glowEm.linear, t.glowI);
        }

        // ---- dashed lock ring on the target's feet
        static Mesh _ringGeo;
        static Mesh ringGeo()
        {
            if (_ringGeo != null) return _ringGeo;
            var m = new Mesh { name = "torpedo-lock-ring" };
            m.vertices = new[] { new Vector3(-1.15f, -1.15f, 0), new Vector3(1.15f, -1.15f, 0), new Vector3(-1.15f, 1.15f, 0), new Vector3(1.15f, 1.15f, 0) };
            m.triangles = new[] { 0, 2, 1, 1, 2, 3 };
            m.RecalculateBounds();
            return _ringGeo = m;
        }
        class Ring { public Transform t; public Material mat; }
        static Ring makeRing(int team)
        {
            var R = new Ring { mat = new Material(Shader.Find("Inkwave/Fx/LockRing")) { name = "iw-torpedo-ring", renderQueue = 3003 } };
            Color c = G.teamColors[team].linear;
            R.mat.SetVector(ID_Color, new Vector4(c.r, c.g, c.b, 1)); R.mat.SetFloat(ID_Time, 0); R.mat.SetFloat(ID_Alpha, 0);
            var go = ThreeSpace.create("torpedoLockRing");
            go.AddComponent<MeshFilter>().sharedMesh = ringGeo();
            var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = R.mat;
            r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
            R.t = go.transform;
            return R;
        }
        static void dropRing(Ring R) { if (R == null) return; destroy(R.t.gameObject); destroy(R.mat); }

        // ================================================================================================= state
        class Item
        {
            public Actor owner, target, hitFoe; public int team; public SubDef sub; public Rig m;
            public bool sp, shot; public Vector3 pos, prev, vel, dir, hover, hitN = UP;
            public string state; public float t, age, hp, fin, propOpen, propA, roll, ringT = 0, glowI = 0.5f, speed, flash;
            public Ring ring; public AudioLoop whirr; public Threat thr;
        }
        static readonly List<Item> items = new List<Item>();
        class Drop
        {
            public Actor owner; public int team; public bool sp; public Vector3 pos, prev, vel;
            public float age, size, dmg, paint; public int cap; public Dictionary<Actor, int> hits;
            public Transform t;
        }
        static readonly List<Drop> drops = new List<Drop>();
        static readonly Stack<Drop> dropPool = new Stack<Drop>();
        static Mesh _dropMesh;
        static Mesh dropMesh() => _dropMesh ??= Geo.sphere(1, 12, 9).toMesh("torpedo-drop");
        static bool locked(Item t) => t.state == "unfold" || t.state == "launch";
        static bool live(Item t) => t.state != "dead";
        static void credit(Item t, float area) { if (!(area > 0)) return; if (t.sp) t.owner.addTurfNoSpecial(area); else t.owner.addTurf(area); }
        static void creditDrop(Drop d, float area) { if (!(area > 0)) return; if (d.sp) d.owner.addTurfNoSpecial(area); else d.owner.addTurf(area); }

        public override bool blocked(Actor a, SubDef sub)
        {
            foreach (var t in items) if (t.owner == a && live(t)) return true;
            return false;
        }

        public override void use(SubSystem subs, Actor a, SubDef sub)
        {
            Vector3 pos = a.pos; pos.y += 1.35f;
            Vector3 vel = G.projectiles.throwVelocity(a, sub.throwSpeed);
            var t = spawn(a, sub, pos, vel);
            var g = G.physics.raycast(new Vector3(a.pos.x, a.pos.y + 0.4f, a.pos.z), DOWN, 2.5f, _hit);
            if (g.hit)
            {
                var b = g.block >= 0 && g.block < G.level.blocks.Count ? G.level.blocks[g.block] : null;
                if (!(b != null && (b.roof || b.rail || b.perch)))
                    credit(t, G.paint.splat(g.point + g.normal * 0.1f, sub.feetPaint, a.team, new SplatOpts { seed = random() }));
            }
            if (a.isLocal || a.nearCamera())
            {
                G.audio?.play("bomb_throw", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = 0.6f, pitch = 0.92f });
                G.audio?.play("torpedo_throw", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = 0.7f });
            }
            emit("sub:use", new Ev { actor = a, kind = "torpedo" });
        }

        static Item spawn(Actor a, SubDef sub, Vector3 pos, Vector3 vel)
        {
            var t = new Item
            {
                sub = sub, owner = a, team = a.team, pos = pos, prev = pos, vel = vel, dir = vel.normalized,
                state = "fly", hp = sub.hp, m = makeMesh(a.team), sp = a.specialActive != null,
                roll = random() * 6, glowI = 0.5f,
            };
            t.m.outer.localPosition = pos;
            orient(t);
            items.Add(t);
            if (nearCam(pos, 40)) t.whirr = G.audio?.loop("torpedo_whirr", new AudioOpts { pos = pos, volume = 0.35f, pitch = 0.8f });
            return t;
        }
        static void orient(Item t)
        {
            t.m.outer.localRotation = Quaternion.FromToRotation(ZAX, t.dir) * Quaternion.AngleAxis(t.roll * Mathf.Rad2Deg, ZAX);
        }

        public override void tick(float dt)
        {
            for (int i = items.Count - 1; i >= 0; i--)
            {
                var t = items[i];
                if (live(t))
                {
                    t.age += dt; t.t += dt;
                    int n = Mathf.Max(1, Mathf.CeilToInt(dt / (1f / 60f) - 1e-6f)); float h = dt / n;
                    for (int s = 0; s < n && live(t); s++)
                    {
                        if (t.state == "fly") fly(t, h);
                        else if (t.state == "unfold") unfold(t, h);
                        else if (t.state == "launch") launch(t, h);
                    }
                    if (live(t) && locked(t)) lethalContact(t);
                    if (live(t)) visuals(t, dt);
                }
                if (!live(t)) { dispose(t); items.RemoveAt(i); }
            }
            tickDrops(dt);
        }

        // ---- in flight: gravity arc; locks a foe in range, bursts small on touching anything first
        static void fly(Item t, float h)
        {
            var s = t.sub;
            t.prev = t.pos;
            t.vel.y -= GRAV * h;
            t.pos += t.vel * h;
            if (t.vel.sqrMagnitude > 1e-4f) t.dir = t.vel.normalized;
            var e = touchFoe(t);
            if (e != null) { burst(t, false); return; }
            if (touchDevice(t)) { burst(t, false); return; }
            var w = G.physics.segment(t.prev, t.pos, _hit);
            if (w.hit) { t.pos = w.point + w.normal * 0.14f; t.hitN = w.normal; burst(t, false); return; }
            if (t.pos.y < PLAYER.waterY - 1.8f) { fizzle(t); return; }
            if (t.age >= s.armTime)
            {
                var tg = findTarget(t, s.lockRange);
                if (tg != null) { startLock(t, tg); return; }
            }
            if (t.age > s.flyLife) burst(t, false);
        }
        static Actor findTarget(Item t, float range)
        {
            Actor best = null; float bd = range;
            foreach (var e in G.actors)
            {
                if (e.team == t.team || liveFoe(e) == null) continue;
                if (e.form == "squid" && e.submerged && !(e.status.track > 0 && e.status.trackTeam == t.team)) continue;
                Vector3 c = chest(e);
                float d = Vector3.Distance(c, t.pos);
                if (d >= bd || !G.physics.los(t.pos, c)) continue;
                bd = d; best = e;
            }
            return best;
        }
        static void startLock(Item t, Actor e)
        {
            t.state = "unfold"; t.t = 0; t.target = e;
            t.vel = Vector3.zero; t.hover = t.pos;
            t.ring = makeRing(t.team); t.ringT = 0;
            t.ring.t.localPosition = t.pos + UP * 0.06f;
            t.ring.t.localRotation = ThreeSpace.eulerXYZ(-Mathf.PI / 2, 0, 0);
            if (nearCam(t.pos, 40)) G.audio?.play("torpedo_transform", new AudioOpts { pos = t.pos, volume = 0.8f });
            if ((e != null && e.isLocal) || t.owner.isLocal) G.audio?.play("torpedo_lock", new AudioOpts { volume = e != null && e.isLocal ? 0.7f : 0.45f });
            if (e != null) emit("sub:arm", new Ev { kind = "torpedo", pos = t.pos, team = t.team, radius = t.sub.radius, target = e, actor = t.owner });
        }
        static void unfold(Item t, float h)
        {
            var s = t.sub;
            if (liveFoe(t.target) == null) t.target = findTarget(t, s.lockRange * 1.4f) ?? t.target;
            float k = clamp(t.t / s.unfoldTime, 0, 1);
            if (liveFoe(t.target) != null)
            {
                Vector3 want = chest(t.target) - t.pos;
                if (want.sqrMagnitude > 1e-6f) t.dir = Vector3.Lerp(t.dir, want.normalized, 1 - Mathf.Exp(-9 * h)).normalized;
            }
            float shake = k > 0.6f ? (k - 0.6f) * 0.03f : 0;
            t.pos = new Vector3(t.hover.x + (random() - 0.5f) * shake,
                t.hover.y + Mathf.Sin(t.t * 13) * 0.025f + 0.06f * backOut(k, 1.5f) * 0.4f,
                t.hover.z + (random() - 0.5f) * shake);
            if (t.t >= s.unfoldTime)
            {
                t.state = "launch"; t.t = 0; t.speed = s.launchSpeed0;
                if (nearCam(t.pos, 40)) G.audio?.play("torpedo_launch", new AudioOpts { pos = t.pos, volume = 0.8f });
                if (nearCam(t.pos, 30)) G.fx?.burst(t.pos + t.dir * -0.4f, -t.dir, G.teamColors[t.team], new FxOpts { count = 4, speed = 1.8f, size = 0.04f, sheet = false });
            }
        }
        static void launch(Item t, float h)
        {
            var s = t.sub;
            t.speed = Mathf.Min(s.launchSpeed, t.speed + s.launchAccel * h);
            if (liveFoe(t.target) != null)
            {
                Vector3 want = chest(t.target) - t.pos;
                if (want.sqrMagnitude > 1e-4f)
                {
                    want.Normalize();
                    float ang = Mathf.Acos(clamp(Vector3.Dot(t.dir, want), -1, 1)), max = s.turnRate * h;
                    if (ang > 1e-4f)
                    {
                        Vector3 axis = Vector3.Cross(t.dir, want);
                        if (axis.sqrMagnitude < 1e-8f) axis = UP;
                        t.dir = (Quaternion.AngleAxis(Mathf.Min(ang, max) * Mathf.Rad2Deg, axis.normalized) * t.dir).normalized;
                    }
                }
            }
            t.prev = t.pos;
            t.pos += t.dir * (t.speed * h);
            if (touchFoe(t) != null) { burst(t, true); return; }
            if (touchDevice(t)) { burst(t, true); return; }
            var w = G.physics.segment(t.prev, t.pos, _hit);
            if (w.hit) { t.pos = w.point + w.normal * 0.16f; t.hitN = w.normal; burst(t, true); return; }
            if (t.pos.y < PLAYER.waterY - 0.2f) { fizzle(t); return; }
            if (t.t > s.launchLife) burst(t, true);
        }
        static Actor touchFoe(Item t)
        {
            foreach (var e in G.actors)
            {
                if (e.team == t.team || !e.alive) continue;
                if (Mathf.Abs(e.pos.x - t.pos.x) > 3 || Mathf.Abs(e.pos.z - t.pos.z) > 3) continue;
                var (dist, tt) = Physics.segmentCapsuleDist(t.prev, t.pos, hitBase(e), PLAYER.radius, hitH(e));
                if (dist < PLAYER.radius + 0.16f) { t.pos = Vector3.Lerp(t.prev, t.pos, tt); t.hitFoe = e; return e; }
            }
            return null;
        }
        // enemy curtains, shields, sprinklers, beacons, bubbles … count as surfaces (the probe does no damage)
        static bool touchDevice(Item t) => (G.subs?.blockShot(t.prev, t.pos, t.team, 0) ?? false) || (G.specials?.shotHit(t.prev, t.pos, t.team, 0, t.owner) ?? false);

        static void lethalContact(Item t)
        {
            var P = G.projectiles;
            if (P?.bombs != null)
            {
                foreach (var b in P.bombs.ToArray())
                {
                    if (b.kind != "bomb" || b.team == t.team || Vector3.Distance(b.pos, t.pos) > 0.62f) continue;
                    P.explodeBombNow(b); P.defuseBomb(b);
                    if (!live(t)) return;
                }
            }
            var S = G.subs;
            if (S?.items != null)
            {
                foreach (var it in S.items.ToArray())
                {
                    if (it.state != "fly" || it.team == t.team || !(it.kind == "sticky" || it.kind == "seeker")) continue;
                    if (Vector3.Distance(it.pos, t.pos) > 0.62f) continue;
                    S.blastNow(it);
                    if (!live(t)) return;
                }
            }
        }

        // ---- the burst: full = wide locked blast with droplets; else the plain bomb-like one
        static void burst(Item t, bool full)
        {
            if (!live(t)) return;
            t.state = "dead";
            var s = t.sub; Color col = G.teamColors[t.team];
            Vector3 c = t.pos;
            Vector3 pc = c;
            var g = G.physics.raycast(new Vector3(c.x, c.y + 0.2f, c.z), DOWN, 2.6f, _hit, true);
            if (g.hit) pc = g.point + g.normal * 0.2f;
            if (full)
            {
                float area = G.paint.splat(pc, s.paintRadius, t.team, new SplatOpts { seed = random() });
                for (int k = 0; k < 5; k++)
                {
                    float a = random() * Mathf.PI * 2, rr = s.paintRadius * (0.55f + random() * 0.45f);
                    area += G.paint.splat(new Vector3(pc.x + Mathf.Cos(a) * rr, pc.y + 0.3f, pc.z + Mathf.Sin(a) * rr),
                        0.55f + random() * 0.45f, t.team, new SplatOpts { seed = random() });
                }
                credit(t, area);
            }
            else credit(t, G.paint.splat(pc, s.fallbackPaint, t.team, new SplatOpts { seed = random() }));
            G.fx?.explosion(c, col, full ? s.radius : s.radius * 0.8f);
            G.audio?.play("bomb_explode", new AudioOpts { pos = c, volume = full ? 0.75f : 0.6f, pitch = full ? 1.08f : 1.15f });
            G.audio?.play("torpedo_pop", new AudioOpts { pos = c, volume = full ? 0.9f : 0.6f, pitch = full ? 1f : 1.12f });
            emit("shake", new Ev { pos = c, amount = full ? 0.55f : 0.45f });
            emit("bomb:explode", new Ev { actor = t.owner, pos = c, team = t.team, radius = s.radius, kind = "torpedo" });
            foreach (var e in G.actors)
            {
                if (e.team == t.team || !e.alive) continue;
                Vector3 ec = chest(e);
                float d = e == t.hitFoe ? 0 : Vector3.Distance(ec, c);
                if (d > s.radius) continue;
                if (d > 0 && !G.physics.los(c + t.hitN * 0.25f, ec)) continue;
                float dmg = d <= s.coreRadius ? s.damageMax : lerp(s.damageMax, s.damageMin, (d - s.coreRadius) / (s.radius - s.coreRadius));
                G.projectiles.applyHit(t.owner, e, dmg, "torpedo");
            }
            G.subs?.damageArea(c, s.radius, 60, t.team);
            if (full) spawnDrops(t, c);
        }
        static void shootDown(Item t)
        {
            if (!live(t)) return;
            t.state = "dead"; t.shot = true;
            if (nearCam(t.pos, 40))
            {
                G.fx?.burst(t.pos, UP, G.teamColors[t.team], new FxOpts { count = 12, speed = 4, size = 0.08f, mist = true });
                G.audio?.play("splat_small", new AudioOpts { pos = t.pos, volume = 0.8f, pitch = 0.75f });
                G.audio?.play("torpedo_pop", new AudioOpts { pos = t.pos, volume = 0.45f, pitch = 1.6f });
            }
            emit("sub:destroyed", new Ev { kind = "torpedo", pos = t.pos, team = t.team });
        }
        static void fizzle(Item t)
        {
            if (!live(t)) return;
            t.state = "dead";
            if (nearCam(t.pos, 40)) G.fx?.waterPlop(t.pos, 0.4f);
        }
        static void hurt(Item t, float dmg)
        {
            if (!live(t) || !(dmg > 0)) return;
            t.flash = 1;
            t.hp -= dmg;
            if (t.hp <= 0) shootDown(t);
        }

        // ================================================================================================= droplets
        static Drop getDrop()
        {
            if (dropPool.Count > 0)
            {
                var d = dropPool.Pop(); d.t.gameObject.SetActive(true); return d;
            }
            var go = ThreeSpace.create("torpedoDrop");
            go.AddComponent<MeshFilter>().sharedMesh = dropMesh();
            go.AddComponent<MeshRenderer>();
            return new Drop { t = go.transform };
        }
        static void spawnDrops(Item t, Vector3 c)
        {
            var s = t.sub;
            var rec = new Dictionary<Actor, int>();
            float bas = random() * Mathf.PI * 2;
            for (int k = 0; k < s.drops; k++)
            {
                if (drops.Count >= MAX_DROPS) break;
                float a = bas + (k / s.drops) * Mathf.PI * 2 + (random() - 0.5f) * 0.4f;
                float hs = 3.2f + random() * 3.4f, vy = 3.2f + random() * 3.2f;
                Vector3 p = new Vector3(c.x + Mathf.Cos(a) * 0.22f, c.y + 0.15f, c.z + Mathf.Sin(a) * 0.22f);
                var d = getDrop();
                d.pos = p; d.prev = p; d.vel = new Vector3(Mathf.Cos(a) * hs, vy, Mathf.Sin(a) * hs);
                d.age = 0; d.owner = t.owner; d.team = t.team; d.sp = t.sp; d.hits = rec;
                d.size = 0.075f + random() * 0.035f; d.dmg = s.dropDamage; d.cap = (int)s.dropHits; d.paint = s.dropPaint;
                d.t.localPosition = p;
                d.t.GetComponent<MeshRenderer>().sharedMaterial = CharacterMats.ink(G.teamColors[t.team]);
                drops.Add(d);
            }
        }
        static void tickDrops(float dt)
        {
            for (int i = drops.Count - 1; i >= 0; i--)
            {
                var d = drops[i];
                bool dead = dropStep(d, dt);
                if (dead)
                {
                    d.t.gameObject.SetActive(false); dropPool.Push(d); drops.RemoveAt(i); continue;
                }
                float sp = d.vel.magnitude, st = 1 + Mathf.Min(1.2f, sp * 0.06f);
                d.t.localPosition = d.pos;
                d.t.localRotation = Quaternion.FromToRotation(ZAX, d.vel / Mathf.Max(sp, 1e-3f));
                d.t.localScale = new Vector3(d.size, d.size, d.size * st);
            }
        }
        static bool dropStep(Drop d, float dt)
        {
            d.age += dt;
            d.prev = d.pos;
            d.vel.y -= 21 * dt;
            d.pos += d.vel * dt;
            bool dead = d.age > 2.4f || d.pos.y < PLAYER.waterY - 1;
            if (!dead)
            {
                foreach (var e in G.actors)
                {
                    if (e.team == d.team || !e.alive) continue;
                    if (Mathf.Abs(e.pos.x - d.pos.x) > 2.5f || Mathf.Abs(e.pos.z - d.pos.z) > 2.5f) continue;
                    var (dist, tt) = Physics.segmentCapsuleDist(d.prev, d.pos, hitBase(e), PLAYER.radius, hitH(e));
                    if (dist < PLAYER.radius + d.size)
                    {
                        d.hits.TryGetValue(e, out int k);
                        if (k < d.cap) { d.hits[e] = k + 1; G.projectiles.applyHit(d.owner, e, d.dmg, "torpedo"); }
                        Vector3 at = Vector3.Lerp(d.prev, d.pos, tt);
                        G.fx?.burst(at, -d.vel.normalized, G.teamColors[d.team], new FxOpts { count = 5, speed = 2.5f, size = 0.06f });
                        dead = true; break;
                    }
                }
            }
            if (!dead && (G.subs?.blockShot(d.prev, d.pos, d.team, d.dmg) ?? false)) dead = true;
            if (!dead)
            {
                var w = G.physics.segment(d.prev, d.pos, _hit, true);
                if (w.hit)
                {
                    float area = G.paint.splat(w.point + w.normal * 0.1f, d.paint * (0.8f + random() * 0.4f), d.team,
                        new SplatOpts { seed = random(), stretch = d.vel.normalized, stretchAmt = 0.5f });
                    creditDrop(d, area);
                    if (nearCam(w.point, 26)) G.fx?.bounceSplash(w.point, w.normal, G.teamColors[d.team]);
                    if (random() < 0.35f && nearCam(w.point, 22)) G.audio?.play("splat_small", new AudioOpts { pos = w.point, volume = 0.3f, pitch = 1.2f });
                    dead = true;
                }
            }
            return dead;
        }

        // ================================================================================================= look
        static void visuals(Item t, float dt)
        {
            var s = t.sub;
            if (t.state == "fly")
            {
                t.roll += dt * 7; t.fin = 0; t.glowI = 0.45f + 0.25f * Mathf.Sin(t.age * 20);
            }
            else if (t.state == "unfold")
            {
                float k = clamp(t.t / s.unfoldTime, 0, 1);
                t.fin = backOut(clamp(k / 0.65f, 0, 1), 2.4f);
                t.propOpen = backOut(clamp((k - 0.45f) / 0.45f, 0, 1), 2);
                t.roll += dt * 2 * (1 - k);
                t.glowI = 1.2f + 2.6f * k + 0.8f * Mathf.Sin(t.t * 40) * k;
                t.propA += dt * 30 * k;
            }
            else if (t.state == "launch")
            {
                t.fin = 1; t.propOpen = 1;
                t.propA += dt * (30 + t.speed * 6);
                t.roll += dt * 1.2f;
                t.glowI = 3.2f + 0.8f * Mathf.Sin(t.t * 30);
                if (nearCam(t.pos, 26)) G.fx?.bombTrail(t.pos - t.dir * 0.45f, -t.dir * t.speed, G.teamColors[t.team]);
            }
            if (t.flash > 0) { t.flash = Mathf.Max(0, t.flash - dt * 6); t.glowI += 5 * t.flash; }
            t.m.outer.localPosition = t.pos;
            orient(t);
            poseRig(t);
            if (t.ring != null)
            {
                var e = t.target;
                if (e == null || !e.alive) t.ring.t.gameObject.SetActive(false);
                else
                {
                    t.ringT += dt;
                    var loc = G.local;
                    bool show = loc == null || loc.team == t.team || loc == e;
                    t.ring.t.gameObject.SetActive(show);
                    t.ring.t.localPosition = new Vector3(e.pos.x, e.pos.y + e.smoothY + 0.06f, e.pos.z);
                    float k = clamp(t.ringT / 0.22f, 0, 1);
                    float sc = lerp(1.9f, 1, 1 - Mathf.Pow(1 - k, 3)) * (1 + 0.04f * Mathf.Sin(t.ringT * 9));
                    t.ring.t.localScale = Vector3.one * sc;
                    t.ring.t.localRotation = ThreeSpace.eulerXYZ(-Mathf.PI / 2, 0, t.ringT * 0.7f);
                    t.ring.mat.SetFloat(ID_Time, t.ringT);
                    t.ring.mat.SetFloat(ID_Alpha, k * (t.state == "launch" ? 1 : 0.8f + 0.2f * Mathf.Sin(t.ringT * 18)));
                }
            }
            if (t.whirr != null)
                t.whirr.set(t.state == "fly" ? 0.3f : 0.55f,
                    t.state == "launch" ? 1 + t.speed / 10 : t.state == "unfold" ? 0.8f + 0.6f * clamp(t.t / s.unfoldTime, 0, 1) : 0.8f, t.pos);
        }
        static void dispose(Item t)
        {
            if (t.m != null) { destroy(t.m.outer.gameObject); destroy(t.m.glowMat); t.m = null; }
            dropRing(t.ring); t.ring = null;
            t.whirr?.stop(0.12f); t.whirr = null;
        }
        public override void clear()
        {
            foreach (var t in items) { t.state = "dead"; dispose(t); }
            items.Clear();
            foreach (var d in drops) { d.t.gameObject.SetActive(false); dropPool.Push(d); }
            drops.Clear();
        }

        // ================================================================================================= shoot-down
        static Vector3 closestOnSeg(Vector3 a, Vector3 b, Vector3 p)
        {
            Vector3 ab = b - a; float L2 = ab.sqrMagnitude;
            float k = L2 > 1e-9f ? clamp(Vector3.Dot(p - a, ab) / L2, 0, 1) : 0;
            return a + ab * k;
        }
        public override bool blockShot(Vector3 prev, Vector3 pos, int team, float dmg)
        {
            foreach (var t in items)
            {
                if (!live(t) || t.team == team) continue;
                if (Mathf.Abs(t.pos.x - pos.x) > 4 || Mathf.Abs(t.pos.z - pos.z) > 4) continue;
                if (Vector3.Distance(closestOnSeg(prev, pos, t.pos), t.pos) > HIT_R) continue;
                hurt(t, dmg);
                return true;
            }
            return false;
        }
        public override float blockRay(Vector3 from, Vector3 dir, float len, int team, float dmg)
        {
            float best = len; Item hitT = null;
            foreach (var t in items)
            {
                if (!live(t) || t.team == team) continue;
                Vector3 v = t.pos - from;
                float tc = Vector3.Dot(v, dir);
                if (tc <= 0 || tc > best + HIT_R) continue;
                float d2 = v.sqrMagnitude - tc * tc;
                if (d2 > HIT_R * HIT_R) continue;
                float tt = Mathf.Max(0.05f, tc - Mathf.Sqrt(HIT_R * HIT_R - d2));
                if (tt < best) { best = tt; hitT = t; }
            }
            if (hitT != null) hurt(hitT, dmg);
            return best;
        }
        public override void damageArea(Vector3 c, float radius, float dmg, int team)
        {
            foreach (var t in items)
            {
                if (!live(t) || t.team == team) continue;
                if (Vector3.Distance(t.pos, c) > radius + 0.3f) continue;
                areaHit(t, dmg);
            }
        }
        static void areaHit(Item t, float dmg)
        {
            if (locked(t) && dmg >= 50) burst(t, true);
            else hurt(t, dmg);
        }

        // ================================================================================================= bots
        class Threat : ThreatInfo
        {
            public Item it;
            public override Vector3 pos => it.pos;
            public override bool live => it.state != "dead";
            public override string state => it.state;
            public override float hp => it.hp;
            public override bool shootable => it.state != "dead";
            public override bool locked => it.state == "unfold" || it.state == "launch";
            public override Actor target => locked && it.target != null && it.target.alive ? it.target : null;
            public override Vector3 vel => it.state == "launch" ? it.dir * it.speed : it.state == "fly" ? it.vel : Vector3.zero;
        }
        static Threat threatOf(Item it)
        {
            if (it.thr != null) return it.thr;
            var s = it.sub;
            return it.thr = new Threat { it = it, kind = "torpedo", team = it.team, owner = it.owner, aimY = 0, radius = s.radius, trigger = HIT_R + PLAYER.radius, speed = s.launchSpeed, lockRange = s.lockRange, ground = false };
        }
        public override void threats(List<ThreatInfo> o) { foreach (var it in items) if (live(it)) o.Add(threatOf(it)); }
        static bool botFight(BotBrain brain, float dist)
        {
            var a = brain.actor;
            foreach (var t in items) if (t.owner == a && live(t)) return false;
            bool go = dist > 4 && dist < 13.5f && random() < 0.035f;
            if (go) brain.bombCd = 6 + random() * 5;
            return go;
        }
        static bool botPaint(BotBrain brain) => false;
    }
}
