// Port of src/game/subs.js — sub weapons (every kind except the Splat Bomb, which lives in Weapons.cs): thrown and placed
// devices, clouds, curtains, mines and jump beacons, plus the status effects they put on players (tracking, poison).
//
//   G.subs.use(actor, subDef)             throw or place (called by the weapon runner after the ink is paid)
//   G.subs.update(dt)                     per frame
//   G.subs.blockActor(actor)              push an actor out of enemy ink curtains (after its movement)
//   G.subs.blockShot(prev, pos, team, dmg) true if an enemy curtain / device absorbed this projectile segment
//   G.subs.blockRay(from, dir, len, team, dmg)  distance to the first enemy curtain / device on a beam (or len)
//   G.subs.damageArea(center, radius, dmg, team)  blasts hurt enemy devices
//   G.subs.beaconsFor(team)               live jump beacons, for the super-jump map
//   G.subs.jumpToBeacon(actor, beacon)
//   G.subs.clear()
//
// Props come from WeaponModel.getSubDef(kind) (origin at the bottom centre, +Y away from the surface, +Z forward).
// Not ported: the online ghosts (netGhost / netHurt: offline only), the Boss Battle splash (M7), specials.js areaHit
// (with that module).
using System.Collections.Generic;
using Inkwave.World;
using UnityEngine;
using UnityEngine.Rendering;
using static Inkwave.Ctx;

namespace Inkwave
{
    public static partial class G
    {
        public static SubSystem subs;
    }

    /** One live sub (subs.js `it`): a thrown device in flight, a stuck / placed one, a cloud, mist or curtain. */
    public class SubItem
    {
        public string kind, state; public SubDef sub; public Actor owner; public int team;
        public bool sp;   // thrown during a special (Bomb Barrage): its ink never charges the meter
        public Transform mesh, inner, spinT; public float lift;
        public Vector3 pos, vel, spinV, dir, normal = Vector3.up, n, tan;
        public float age, t, born, fuse, beepT, heading, trail, stuckT, pulseT, spin, hp, uses, rx, rz;
        public bool lit;   // a mine's fuse is burning (subs.js `it.fuse != null`)
        public bool dash;
        public Actor target, direct;
        public int face = -1; public float u, v;
        // the cloud (echo orb) / mist puffs / curtain sheet, and their materials
        public Transform cloud; public Material cloudMat; public Transform[] puffs; public float[] ph;
        public Transform sheet; public Material sheetMat;
        public HashSet<Actor> tagged;
    }

    public class SubSystem
    {
        static readonly Vector3 UP = new Vector3(0, 1, 0), DOWN = new Vector3(0, -1, 0);
        static readonly Hit _hit = new Hit(), _hit2 = new Hit();
        const float GRAV = 24;
        const float SUB_SCALE = 1.9f;   // prop models are built at hand scale; in the world they read at the splat bomb's size
        static readonly int ID_Color = Shader.PropertyToID("_Color"), ID_Time = Shader.PropertyToID("_Time2"), ID_Life = Shader.PropertyToID("_Life");

        /** the kinds this module (or Weapons.cs: the bomb) runs itself; the rest are kits (Kits.SUB) */
        public static readonly HashSet<string> BUILTIN = new HashSet<string> { "bomb", "sticky", "burst", "seeker", "scan", "curtain", "sprinkler", "mine", "beacon", "mist" };
        public static bool ported(string kind) => BUILTIN.Contains(kind) || Kits.SUB.ContainsKey(kind);

        public readonly List<SubItem> items = new List<SubItem>();
        public static int beaconLandings;   // super jumps that came down on a beacon (tests read it)
        readonly Mesh cloudGeo;
        static Shader _tint, _curtain;
        readonly Material[] _glow = new Material[2];
        readonly List<System.Action> unsubs = new List<System.Action>();

        static bool nearCam(Vector3 p, float r = 30) => (G.camPos - p).sqrMagnitude < r * r;

        public SubSystem()
        {
            cloudGeo = Geo.sphere(1, 24, 14).toMesh("sub-cloud");
            _tint ??= Shader.Find("Inkwave/Fx/Tint");
            _curtain ??= Shader.Find("Inkwave/Fx/Curtain");
            // a splatted owner loses their sprinkler; a super jump that lands on a beacon uses it up
            unsubs.Add(on("splatted", e => { foreach (var it in items) if (it.owner == (object)e.victim && it.kind == "sprinkler" && it.state == "spray") destroyItem(it); }));
            unsubs.Add(on("superjump:land", e => landedOnBeacon(e.actor as Actor)));
        }

        public void dispose() { clear(); foreach (var u in unsubs) u(); unsubs.Clear(); }

        public void clear()
        {
            foreach (var it in items) disposeItem(it);
            items.Clear();
            foreach (var k in Kits.SUB.Values) k.clear();
        }

        // ---------------------------------------------------------------------------------------------- deploy
        public void use(Actor a, SubDef sub)
        {
            var kit = Kits.sub(sub.kind);
            if (kit != null) { kit.use(this, a, sub); return; }   // kit subs (Game/Kits/*)
            if (sub.placed) { place(a, sub); return; }
            Vector3 pos = a.pos; pos.y += 1.35f;
            Vector3 vel = G.projectiles.throwVelocity(a, sub.throwSpeed);
            throwItem(a, sub, pos, vel);
            emit("sub:use", new Ev { actor = a, kind = sub.kind });
        }
        // a thrown sub in flight
        SubItem throwItem(Actor a, SubDef sub, Vector3 pos, Vector3 vel)
        {
            var it = new SubItem
            {
                sp = a.specialActive != null,
                kind = sub.kind, sub = sub, owner = a, team = a.team, pos = pos, vel = vel, state = "fly", age = 0, t = 0,
                spinV = new Vector3(3 + random() * 6, 3 + random() * 6, 0),
                dir = new Vector3(vel.x, 0, vel.z).normalized,
            };
            prop(it);
            it.mesh.localPosition = pos;
            it.inner.localPosition = new Vector3(0, -it.lift, 0);   // tumble about the middle while flying
            items.Add(it);
            if (a.isLocal || a.nearCamera()) G.audio?.play("bomb_throw", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = 0.65f, pitch = sub.kind == "burst" ? 1.2f : 1 });
            return it;
        }

        void place(Actor a, SubDef sub)
        {
            var g = G.physics.raycast(new Vector3(a.pos.x, a.pos.y + 0.4f, a.pos.z), DOWN, 3, _hit);
            if (!g.hit) return;
            { var gb = g.block >= 0 && g.block < G.level.blocks.Count ? G.level.blocks[g.block] : null; if (gb != null && (gb.roof || gb.rail || gb.perch)) return; }   // nothing gets planted on an off-limits roof, a railing or a crane perch
            // per-player limits: mines — the oldest beyond the limit goes off; beacons — the oldest is removed
            var mine = new List<SubItem>();
            foreach (var o in items) if (o.owner == a && o.kind == sub.kind && o.state != "dead") mine.Add(o);
            mine.Sort((x, y) => x.born.CompareTo(y.born));
            while (mine.Count >= sub.max) { var old = mine[0]; mine.RemoveAt(0); if (sub.kind == "mine") mineBlast(old); else destroyItem(old); }
            var it = plant(a, sub, g.point, g.normal, a.yaw, g);
            emit("sub:use", new Ev { actor = a, kind = sub.kind, pos = it.pos });
        }
        // a placed device (mine / beacon) on the floor at pos (g: the floor hit, for the ink under it)
        SubItem plant(Actor a, SubDef sub, Vector3 pos, Vector3 normal, float yaw, Hit g)
        {
            var it = new SubItem
            {
                kind = sub.kind, sub = sub, owner = a, team = a.team, pos = pos, state = sub.kind, age = 0, t = 0, born = G.time, normal = normal, sp = a.specialActive != null,
                face = g != null ? g.face : -1, u = g != null ? g.u : 0, v = g != null ? g.v : 0, hp = sub.hp > 0 ? sub.hp : 1, uses = sub.uses,
            };
            prop(it);
            it.mesh.localPosition = pos;
            it.mesh.localRotation = Quaternion.FromToRotation(UP, normal) * Quaternion.AngleAxis(yaw * Mathf.Rad2Deg, UP);
            items.Add(it);
            if (sub.kind == "mine") paintUnder(it, 1.1f);
            if (a.isLocal || a.nearCamera()) G.audio?.play("bomb_beep", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = 0.5f, pitch = sub.kind == "beacon" ? 1.3f : 0.8f });
            return it;
        }

        // prop meshes: an outer node (world transform) → inner (offset while flying so it tumbles about its middle) → model
        void prop(SubItem it)
        {
            var d = WeaponModel.getSubDef(it.kind);
            var outer = ThreeSpace.create("sub:" + it.kind).transform;
            var inner = ThreeSpace.create("inner", outer).transform;
            var model = ThreeSpace.create("model", inner).transform;
            model.localScale = Vector3.one * SUB_SCALE;
            Color col = G.teamColors[it.team];
            part("body", model, d.body, CharacterMats.plastic, true);
            part("ink", model, d.ink, CharacterMats.ink(col), true);
            if (d.glow != null) part("glow", model, d.glow, glowMat(it.team), false);
            if (d.spin != null)
            {
                var spin = ThreeSpace.create("spin", model).transform; spin.localPosition = d.spinAt;
                part("spinBody", spin, d.spin, CharacterMats.plastic, true).transform.localPosition = -d.spinAt;
                if (d.spinInk != null) part("spinInk", spin, d.spinInk, CharacterMats.ink(col), false).transform.localPosition = -d.spinAt;
                it.spinT = spin;
            }
            it.mesh = outer; it.inner = inner;
            it.lift = (d.body != null ? d.body.bounds.max.y : 0.1f) * SUB_SCALE * 0.5f;
        }
        static MeshRenderer part(string name, Transform parent, Mesh m, Material mat, bool shadow)
        {
            var g = ThreeSpace.create(name, parent);
            g.AddComponent<MeshFilter>().sharedMesh = m;
            var r = g.AddComponent<MeshRenderer>(); r.sharedMaterial = mat;
            r.shadowCastingMode = shadow ? ShadowCastingMode.On : ShadowCastingMode.Off;
            return r;
        }
        // lit parts (scan orb band, beacon lamp): team colour that glows a little
        Material glowMat(int team)
        {
            if (_glow[team] == null)
            {
                Color c = G.teamColors[team].linear;
                _glow[team] = CharacterMats.matte(c * 0.35f, c, 1.1f, 0.35f, 0);
            }
            return _glow[team];
        }
        /** the team colours changed (palette): rebuild the glow materials on next use */
        public void refreshColors() { for (int i = 0; i < 2; i++) { if (_glow[i] != null) destroy(_glow[i]); _glow[i] = null; } }

        // ---------------------------------------------------------------------------------------------- per frame
        public void update(float dt)
        {
            foreach (var k in Kits.SUB.Values) k.tick(dt);   // kit subs' own objects
            for (int i = items.Count - 1; i >= 0; i--)
            {
                var it = items[i];
                it.age += dt; it.t += dt;
                step(it, dt);
                if (it.state == "dead") { disposeItem(it); items.RemoveAt(i); }
            }
        }
        void step(SubItem it, float dt)
        {
            switch (it.state)
            {
                case "fly": fly(it, dt); break;
                case "stuck": stuck(it, dt); break;
                case "run": run(it, dt); break;
                case "cloud": cloudStep(it, dt); break;
                case "mist": mist(it, dt); break;
                case "curtain": curtain(it, dt); break;
                case "spray": spray(it, dt); break;
                case "mine": mineStep(it, dt); break;
                case "beacon": beacon(it, dt); break;
            }
        }

        void fly(SubItem it, float dt)
        {
            var s = it.sub;
            it.vel.y -= GRAV * dt;
            Vector3 prev = it.pos;
            it.pos += it.vel * dt;
            // direct hits on enemies (pellet pops on them, murk bomb poisons them for the whole mist)
            if (it.kind == "burst" || it.kind == "mist")
            {
                foreach (var e in G.actors)
                {
                    if (e.team == it.team || !e.alive) continue;
                    var (dist, tt) = Physics.segmentCapsuleDist(prev, it.pos, e.pos, PLAYER.radius + 0.1f, e.form == "squid" ? PLAYER.squidHeight : PLAYER.height);
                    if (dist < PLAYER.radius + 0.12f)
                    {
                        it.pos = Vector3.Lerp(prev, it.pos, tt);
                        if (it.kind == "burst") { pelletBlast(it, e, null); it.state = "dead"; } else { it.direct = e; startMist(it); }
                        return;
                    }
                }
            }
            // clouds burst in mid-air when their fuse runs out
            if ((it.kind == "scan" || it.kind == "mist") && it.age > s.fuse) { if (it.kind == "scan") startCloud(it); else startMist(it); return; }
            var hit = G.physics.segment(prev, it.pos, _hit);
            if (hit.hit)
            {
                Vector3 n = hit.normal; bool floor = n.y > 0.6f;
                switch (it.kind)
                {
                    case "sticky": stick(it, hit, "stuck", s.fuse); return;
                    case "sprinkler": stick(it, hit, "spray", 0); return;
                    case "burst": pelletBlast(it, null, hit.point); it.state = "dead"; return;
                    case "scan": it.pos = hit.point + n * 0.3f; startCloud(it); return;
                    case "mist": it.pos = hit.point + n * 0.3f; startMist(it); return;
                    case "seeker": if (floor) { startRun(it, hit); return; } break;
                    case "curtain": if (floor) { startCurtain(it, hit); return; } break;
                }
                // bounce (seeker / curtain off walls, until they find a floor)
                it.pos = hit.point + n * 0.12f;
                float vn = Vector3.Dot(it.vel, n);
                it.vel = (it.vel + n * (-vn * 1.4f)) * 0.55f;
            }
            if (it.pos.y < PLAYER.waterY - 1.8f) { it.state = "dead"; return; }
            it.mesh.localPosition = it.pos;
            it.rx += it.spinV.x * dt; it.rz += it.spinV.y * dt;
            it.mesh.localRotation = ThreeSpace.eulerXYZ(it.rx, 0, it.rz);
        }

        void stick(SubItem it, Hit hit, string state, float fuse)
        {
            it.state = state; it.t = 0; it.fuse = fuse; it.beepT = 0;
            it.normal = hit.normal;
            it.pos = hit.point + hit.normal * 0.005f;
            it.face = hit.face; it.u = hit.u; it.v = hit.v;
            it.hp = it.sub.hp > 0 ? it.sub.hp : 1; it.born = G.time;
            it.inner.localPosition = Vector3.zero;
            it.mesh.localPosition = it.pos;
            it.mesh.localRotation = Quaternion.FromToRotation(UP, hit.normal);
            if (state == "spray")
            {
                // one sprinkler per player: a new one replaces the old
                foreach (var o in items) if (o != it && o.owner == it.owner && o.kind == "sprinkler" && o.state == "spray") destroyItem(o);
                it.pulseT = 0; it.spin = 0;
            }
            if (nearCam(it.pos)) G.audio?.play("bomb_beep", new AudioOpts { pos = it.pos, volume = 0.55f, pitch = state == "spray" ? 1.4f : 1 });
            emit("sub:land", new Ev { kind = it.kind, pos = it.pos, team = it.team, radius = it.sub.radius });
        }

        // ---- cling charge: stuck, blinking faster, then a wide blast
        void stuck(SubItem it, float dt)
        {
            it.beepT -= dt;
            float k = clamp(it.t / it.fuse, 0, 1);
            it.mesh.localScale = Vector3.one * (1 + k * 0.3f + Mathf.Sin(it.t * 40) * 0.025f * k);
            if (it.beepT <= 0) { it.beepT = lerp(0.45f, 0.1f, k); if (nearCam(it.pos)) G.audio?.play("bomb_beep", new AudioOpts { pos = it.pos, volume = 0.3f + 0.4f * k, pitch = 1 + 0.3f * k }); }
            if (it.t >= it.fuse)
            {
                var s = it.sub;
                blast(it, it.pos, s.radius, s.damageMax, s.damageMin, s.paintRadius, it.normal);
                it.state = "dead";
            }
        }

        // ---- skitter bomb: lands, scuttles after the nearest enemy laying a swimmable trail, bursts on reaching them
        void startRun(SubItem it, Hit hit)
        {
            it.state = "run"; it.t = 0;
            it.pos = hit.point;
            it.heading = Mathf.Atan2(it.dir.x, it.dir.z);
            it.trail = 0; it.stuckT = 0; it.target = null; it.dash = false;
            it.inner.localPosition = Vector3.zero;
            it.mesh.localRotation = Quaternion.AngleAxis(it.heading * Mathf.Rad2Deg, UP);
        }
        void run(SubItem it, float dt)
        {
            var s = it.sub;
            if (it.target == null || !it.target.alive)
            {
                Actor best = null; float bd = s.seekRange;
                foreach (var e in G.actors)
                {
                    if (e.team == it.team || !e.alive) continue;
                    float d = Vector3.Distance(e.pos, it.pos);
                    if (d < bd) { bd = d; best = e; }
                }
                if (best != it.target) it.dash = false;
                it.target = best;
            }
            // steering, made to be sidestepped: it turns at most s.turnRate (a wide circle at full speed) and, once close and
            // lined up (s.commitDist), dashes straight without steering, so a late sidestep makes it overshoot and swing round.
            // Onto a slow / standing target that sits inside its circle it slows into the turn (s.creep) instead of orbiting.
            float speed = s.speed;
            if (it.target != null)
            {
                var tg = it.target; float dx = tg.pos.x - it.pos.x, dz = tg.pos.z - it.pos.z, dist = Mathf.Sqrt(dx * dx + dz * dz);
                float d = Mathf.Atan2(dx, dz) - it.heading; while (d > Mathf.PI) d -= Mathf.PI * 2; while (d < -Mathf.PI) d += Mathf.PI * 2;
                if (!it.dash && dist < s.commitDist && Mathf.Abs(d) < 0.35f) it.dash = true;
                else if (it.dash && (Mathf.Abs(d) > Mathf.PI / 2 || dist > s.commitDist + 1)) it.dash = false;
                if (!it.dash)
                {
                    it.heading += clamp(d, -s.turnRate * dt, s.turnRate * dt);
                    if (Mathf.Sqrt(tg.vel.x * tg.vel.x + tg.vel.z * tg.vel.z) < 2.5f) speed *= 1 - s.creep * clamp((Mathf.Abs(d) - 0.7f) / 0.6f, 0, 1);
                }
            }
            float hx = Mathf.Sin(it.heading), hz = Mathf.Cos(it.heading), stepL = speed * dt;
            // walls: turn away along them
            var w = G.physics.raycast(new Vector3(it.pos.x, it.pos.y + 0.2f, it.pos.z), new Vector3(hx, 0, hz), stepL + 0.3f, _hit2, true);
            if (w.hit && Mathf.Abs(w.normal.y) < 0.5f)
            {
                it.stuckT += dt;
                float side = hx * w.normal.z - hz * w.normal.x >= 0 ? 1 : -1;
                it.heading += side * 6 * dt;
            }
            else
            {
                it.stuckT = Mathf.Max(0, it.stuckT - dt);
                float nx = it.pos.x + hx * stepL, nz = it.pos.z + hz * stepL;
                float gy = G.level.groundHeight(nx, nz, it.pos.y + 0.6f);
                if (float.IsNegativeInfinity(gy)) { it.state = "dead"; if (nearCam(it.pos)) G.fx?.burst(it.pos, UP, G.teamColors[it.team], new FxOpts { count = 10, speed = 3, size = 0.08f }); return; }
                it.pos = new Vector3(nx, gy, nz);
                it.trail += stepL;
                if (it.trail > 0.35f) { it.trail = 0; credit(it, G.paint.splat(new Vector3(it.pos.x, it.pos.y + 0.12f, it.pos.z), s.trailRadius, it.team, new SplatOpts { seed = random() })); }
            }
            it.mesh.localPosition = it.pos;
            it.mesh.localRotation = Quaternion.AngleAxis(it.heading * Mathf.Rad2Deg, UP);
            it.inner.localPosition = new Vector3(0, Mathf.Abs(Mathf.Sin(it.t * 26)) * 0.025f, 0);
            bool reached = it.target != null && Vector3.Distance(it.target.pos, it.pos) < s.triggerDist;
            if (reached || it.t > s.life || it.stuckT > 0.6f)
            {
                blast(it, new Vector3(it.pos.x, it.pos.y + 0.2f, it.pos.z), s.radius, s.damageMax, s.damageMin, s.paintRadius, UP);
                it.state = "dead";
            }
        }

        Material tintMat(Color linear, float opacity)
        {
            var m = new Material(_tint) { name = "iw-sub-cloud" };
            m.SetVector(ID_Color, new Vector4(linear.r, linear.g, linear.b, opacity));
            return m;
        }
        static void setOpacity(Material m, Color linear, float opacity) => m.SetVector(ID_Color, new Vector4(linear.r, linear.g, linear.b, opacity));
        Transform puff(Transform parent, Material m)
        {
            var p = ThreeSpace.create("puff", parent);
            p.AddComponent<MeshFilter>().sharedMesh = cloudGeo;
            var r = p.AddComponent<MeshRenderer>(); r.sharedMaterial = m; r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
            return p.transform;
        }

        // ---- echo orb: a sensing cloud that tags enemies it touches (tracked for the thrower's team)
        void startCloud(SubItem it)
        {
            it.state = "cloud"; it.t = 0;
            it.mesh.gameObject.SetActive(false);
            Color col = Color.Lerp(G.teamColors[it.team].linear, Color.white, 0.35f);
            it.cloudMat = tintMat(col, 0.4f);
            it.cloud = puff(ThreeSpace.root, it.cloudMat);
            it.cloud.localPosition = it.pos; it.cloud.localScale = Vector3.one * 0.2f;
            it.tagged = new HashSet<Actor>();
            if (nearCam(it.pos, 40)) G.audio?.play("special_activate", new AudioOpts { pos = it.pos, volume = 0.45f, pitch = 1.6f });
            emit("sub:cloud", new Ev { kind = "scan", pos = it.pos, team = it.team, radius = it.sub.radius });
        }
        void cloudStep(SubItem it, float dt)
        {
            var s = it.sub;
            float grow = clamp(it.t / 0.25f, 0, 1), fade = clamp((it.t - s.cloudTime * 0.5f) / (s.cloudTime * 0.5f), 0, 1);
            it.cloud.localScale = Vector3.one * lerp(0.2f, s.radius, 1 - Mathf.Pow(1 - grow, 3));
            Color col = Color.Lerp(G.teamColors[it.team].linear, Color.white, 0.35f);
            setOpacity(it.cloudMat, col, 0.4f * (1 - fade));
            foreach (var e in G.actors)
            {
                if (e.team == it.team || !e.alive || it.tagged.Contains(e)) continue;
                if (Vector3.Distance(new Vector3(e.pos.x, e.pos.y + 0.7f, e.pos.z), it.pos) < s.radius * grow)
                {
                    it.tagged.Add(e);
                    track(e, it.team, s.trackTime);
                }
            }
            if (it.t >= s.cloudTime) it.state = "dead";
        }

        // ---- murk bomb: a poison mist (no damage) — slows and drains ink while inside; a direct hit lasts the whole mist
        static Color mistColor(int team) => Color.Lerp(G.teamColors[team].linear, new Color(0.25f, 0.2f, 0.3f), 0.55f);
        void startMist(SubItem it)
        {
            it.state = "mist"; it.t = 0;
            it.mesh.gameObject.SetActive(false);
            it.cloudMat = tintMat(mistColor(it.team), 0.3f);
            it.cloud = ThreeSpace.create("mist").transform;
            it.puffs = new Transform[7]; it.ph = new float[7];
            for (int k = 0; k < 7; k++)
            {
                var p = puff(it.cloud, it.cloudMat);
                float a = (k / 7f) * Mathf.PI * 2;
                p.localPosition = new Vector3(Mathf.Cos(a) * 1.3f * (k > 0 ? 1 : 0), (k % 2) * 0.5f, Mathf.Sin(a) * 1.3f * (k > 0 ? 1 : 0));
                it.puffs[k] = p; it.ph[k] = random() * 6;
            }
            it.cloud.localPosition = it.pos;
            it.cloud.localScale = Vector3.one * 0.3f;
            if (it.direct != null) poison(it.direct, it.sub.mistTime);
            if (nearCam(it.pos, 40)) G.audio?.play("enemy_ink_sizzle", new AudioOpts { pos = it.pos, volume = 0.7f, pitch = 0.7f });
            emit("sub:cloud", new Ev { kind = "mist", pos = it.pos, team = it.team, radius = it.sub.radius });
        }
        void mist(SubItem it, float dt)
        {
            var s = it.sub;
            float grow = clamp(it.t / 0.4f, 0, 1), fade = clamp((it.t - (s.mistTime - 0.8f)) / 0.8f, 0, 1);
            it.cloud.localScale = Vector3.one * lerp(0.3f, s.radius * 0.72f, 1 - Mathf.Pow(1 - grow, 3));
            setOpacity(it.cloudMat, mistColor(it.team), 0.3f * (1 - fade));
            for (int k = 0; k < it.puffs.Length; k++)
            {
                var p = it.puffs[k];
                p.localScale = Vector3.one * (1 + 0.12f * Mathf.Sin(it.t * 1.7f + it.ph[k]));
                p.localRotation *= Quaternion.AngleAxis(dt * 0.3f * Mathf.Rad2Deg, UP);
            }
            foreach (var e in G.actors)
            {
                if (e.team == it.team || !e.alive) continue;
                if (Vector3.Distance(new Vector3(e.pos.x, e.pos.y + 0.6f, e.pos.z), it.pos) < s.radius * grow) poison(e, 0.6f);
            }
            if (it.t >= s.mistTime) it.state = "dead";
        }

        // ---- drip curtain: a sheet of falling ink across the throw line; blocks enemy players and shots
        static Mesh _plane;
        static Mesh plane()
        {
            if (_plane != null) return _plane;
            // PlaneGeometry(1, 1): the XY plane facing +Z, uv (0,0) at the bottom left; scaled per curtain
            var m = new Mesh { name = "sub-curtain" };
            m.vertices = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(-0.5f, 0.5f, 0), new Vector3(0.5f, 0.5f, 0) };
            m.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            m.triangles = new[] { 0, 1, 2, 2, 1, 3 };
            m.RecalculateBounds();
            return _plane = m;
        }
        void startCurtain(SubItem it, Hit hit)
        {
            var s = it.sub;
            it.state = "curtain"; it.t = 0; it.hp = s.hp;
            it.pos = hit.point;
            it.n = it.dir;                                       // curtain faces the thrower
            it.tan = new Vector3(-it.n.z, 0, it.n.x);
            it.inner.localPosition = Vector3.zero;
            it.mesh.localPosition = it.pos;
            it.mesh.localRotation = Quaternion.AngleAxis(Mathf.Atan2(it.n.x, it.n.z) * Mathf.Rad2Deg, UP);
            it.sheetMat = new Material(_curtain) { name = "iw-curtain" };
            Color c = G.teamColors[it.team].linear;
            it.sheetMat.SetVector(ID_Color, new Vector4(c.r, c.g, c.b, 1));
            it.sheetMat.SetFloat(ID_Time, 0); it.sheetMat.SetFloat(ID_Life, 1);
            var sh = ThreeSpace.create("sheet", it.mesh);
            sh.AddComponent<MeshFilter>().sharedMesh = plane();
            var r = sh.AddComponent<MeshRenderer>(); r.sharedMaterial = it.sheetMat; r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
            it.sheet = sh.transform;
            it.sheet.localPosition = new Vector3(0, s.height / 2 + 0.05f, 0);
            it.sheet.localScale = new Vector3(s.width, s.height, 1);
            paintUnder(it, 1.4f);
            if (nearCam(it.pos, 40)) G.audio?.play("swim_splash", new AudioOpts { pos = it.pos, volume = 0.8f, pitch = 0.8f });
            emit("sub:land", new Ev { kind = "curtain", pos = it.pos, team = it.team, radius = s.width / 2 });
        }
        void curtain(SubItem it, float dt)
        {
            var s = it.sub;
            it.hp -= s.decay * dt;
            it.sheetMat.SetFloat(ID_Time, it.t);
            it.sheetMat.SetFloat(ID_Life, clamp(it.hp / s.hp, 0, 1));
            float k = it.t < 0.25f ? it.t / 0.25f : 1;
            it.sheet.localScale = new Vector3(s.width, s.height * k, 1); it.sheet.localPosition = new Vector3(0, (s.height * k) / 2 + 0.05f, 0);
            if (it.hp <= 0) destroyItem(it);
        }

        // ---- twirl sprinkler: stuck to a surface, spraying pulses of droplets around itself (weaker after a while)
        void spray(SubItem it, float dt)
        {
            var s = it.sub;
            it.pulseT -= dt;
            it.spin += dt * (it.t < s.sprayFade ? 9 : 4.5f);
            if (it.spinT != null) it.spinT.localRotation = Quaternion.AngleAxis(it.spin * Mathf.Rad2Deg, UP);
            if (it.pulseT > 0) return;
            it.pulseT = s.pulse * (it.t < s.sprayFade ? 1 : 2);
            Vector3 n = it.normal;
            // a tangent frame on the mounting surface; drops fan out around it, lobbed away from the surface
            Vector3 t1 = new Vector3(1, 0, 0); if (Mathf.Abs(n.x) > 0.9f) t1 = new Vector3(0, 0, 1);
            t1 = (t1 - n * Vector3.Dot(t1, n)).normalized;
            Vector3 t2 = Vector3.Cross(n, t1);
            Vector3 bas = it.pos + n * 0.2f;
            for (int k = 0; k < s.drops; k++)
            {
                float a = it.spin * 1.7f + (k / s.drops) * Mathf.PI * 2 + random() * 0.4f;
                float outK = random() * 0.4f + 0.6f, sp = 3.2f + random() * 2.6f;
                Vector3 v = (t1 * Mathf.Cos(a) + t2 * Mathf.Sin(a)) * outK + n * 0.75f;
                G.projectiles.spawnDrop(it.owner, bas, v * sp, 0.08f, 0.5f + random() * 0.25f, s.dropDamage, "sprinkler");
            }
        }

        // ---- lurk mine: hidden in its owner's ink; an enemy coming close sets it off (damage + tracking)
        void mineStep(SubItem it, float dt)
        {
            var s = it.sub;
            var loc = G.local;
            bool inOwnInk = it.face >= 0 && G.paint.sample(it.face, it.u, it.v) - 1 == it.team;
            bool vis = loc == null || loc.team == it.team || !inOwnInk;
            if (it.lit)
            {
                it.fuse -= dt;
                vis = true;
                it.mesh.localScale = Vector3.one * (1 + Mathf.Sin(it.t * 50) * 0.08f);
                if (it.mesh.gameObject.activeSelf != vis) it.mesh.gameObject.SetActive(vis);
                if (it.fuse <= 0) mineBlast(it);
                return;
            }
            if (it.mesh.gameObject.activeSelf != vis) it.mesh.gameObject.SetActive(vis);
            if (it.t < s.armTime) return;
            foreach (var e in G.actors)
            {
                if (e.team == it.team || !e.alive) continue;
                if (Vector3.Distance(e.pos, it.pos) < s.triggerRadius)
                {
                    it.lit = true; it.fuse = s.delay;
                    if (nearCam(it.pos, 40)) G.audio?.play("bomb_beep", new AudioOpts { pos = it.pos, volume = 0.8f, pitch = 1.5f });
                    emit("sub:arm", new Ev { kind = "mine", pos = it.pos, team = it.team, radius = s.radius });
                    return;
                }
            }
        }
        void mineBlast(SubItem it)
        {
            if (it.state == "dead") return;
            var s = it.sub; Vector3 c = new Vector3(it.pos.x, it.pos.y + 0.3f, it.pos.z);
            paint(it, c, s.paintRadius);
            G.fx?.explosion(c, G.teamColors[it.team], s.radius * 0.8f);
            G.audio?.play("bomb_explode", new AudioOpts { pos = c, volume = 0.7f, pitch = 1.2f });
            emit("bomb:explode", new Ev { actor = it.owner, pos = c, team = it.team, radius = s.radius });
            foreach (var e in G.actors)
            {
                if (e.team == it.team || !e.alive) continue;
                Vector3 ep = new Vector3(e.pos.x, e.pos.y + 0.7f, e.pos.z);
                if (Vector3.Distance(ep, c) > s.radius || !G.physics.los(c, ep)) continue;
                G.projectiles.applyHit(it.owner, e, s.damage, "mine");
                track(e, it.team, s.trackTime);
            }
            damageArea(c, s.radius, 30, it.team);
            it.state = "dead";
        }

        // ---- hop beacon: a super-jump point for the team (two uses)
        void beacon(SubItem it, float dt)
        {
            it.spin += dt * 1.5f;
            if (it.spinT != null) it.spinT.localRotation = Quaternion.AngleAxis(it.spin * Mathf.Rad2Deg, UP);
        }
        public List<SubItem> beaconsFor(int team, List<SubItem> o = null)
        {
            o ??= new List<SubItem>(); o.Clear();
            foreach (var it in items) if (it.kind == "beacon" && it.state == "beacon" && it.team == team) o.Add(it);
            return o;
        }
        public bool jumpToBeacon(Actor actor, SubItem b)
        {
            if (b == null || b.state != "beacon" || b.team != actor.team) return false;
            bool ok = actor.superJump(b.pos);
            if (ok) actor._jumpBeacon = b;
            return ok;
        }
        void landedOnBeacon(Actor actor)
        {
            var b = actor?._jumpBeacon;
            if (b == null) return;
            actor._jumpBeacon = null;
            if (b.state != "beacon") return;
            beaconLandings++;
            b.uses--;
            // a jumper who carries beacons themselves uses it up on landing
            if (b.uses <= 0 || actor.sub?.kind == "beacon") destroyItem(b);
        }

        // ---------------------------------------------------------------------------------------------- effects
        public void track(Actor e, int team, float time)
        {
            var st = e.status;
            bool fresh = !(st.track > 0 && st.trackTeam == team);
            st.track = Mathf.Max(st.trackTeam == team ? st.track : 0, time); st.trackTeam = team;
            if (fresh) emit("actor:tracked", new Ev { actor = e, team = team });
        }
        public void poison(Actor e, float time)
        {
            bool fresh = !(e.status.poison > 0);
            e.status.poison = Mathf.Max(e.status.poison, time);
            if (fresh) emit("actor:poisoned", new Ev { actor = e });
        }

        // paint + damage enemies (with line of sight) around a blast, like the splat bomb
        void blast(SubItem it, Vector3 center, float radius, float dmgMax, float dmgMin, float paintRadius, Vector3 n)
        {
            paint(it, center + n * 0.2f, paintRadius);
            G.fx?.explosion(center, G.teamColors[it.team], radius);
            G.audio?.play("bomb_explode", new AudioOpts { pos = center });
            emit("shake", new Ev { pos = center, amount = 0.6f });
            emit("bomb:explode", new Ev { actor = it.owner, pos = center, team = it.team, radius = radius });
            Vector3 from = center + n * 0.3f;
            foreach (var e in G.actors)
            {
                if (e.team == it.team || !e.alive) continue;
                Vector3 ep = new Vector3(e.pos.x, e.pos.y + 0.7f, e.pos.z);
                float d = Vector3.Distance(ep, center);
                if (d > radius || !G.physics.los(from, ep)) continue;
                float k = 1 - clamp((d - 0.8f) / (radius - 0.8f), 0, 1);
                G.projectiles.applyHit(it.owner, e, lerp(dmgMin, dmgMax, k * k), it.kind);
            }
            damageArea(center, radius, 60, it.team);
            // (Boss Battle splash — M7)
        }
        void pelletBlast(SubItem it, Actor direct, Vector3? at)
        {
            var s = it.sub; Vector3 c = at ?? it.pos;
            paint(it, new Vector3(c.x, c.y + 0.2f, c.z), s.paintRadius);
            G.fx?.explosion(c, G.teamColors[it.team], s.radius * 0.8f);
            G.audio?.play("blaster_boom", new AudioOpts { pos = c, volume = 0.6f, pitch = 1.2f });
            emit("bomb:explode", new Ev { actor = it.owner, pos = c, team = it.team, radius = s.radius });
            if (direct != null) G.projectiles.applyHit(it.owner, direct, s.directDamage, "burst");
            Vector3 from = new Vector3(c.x, c.y + 0.3f, c.z);
            foreach (var e in G.actors)
            {
                if (e.team == it.team || !e.alive || e == direct) continue;
                Vector3 ep = new Vector3(e.pos.x, e.pos.y + 0.7f, e.pos.z);
                if (Vector3.Distance(ep, c) > s.radius || !G.physics.los(from, ep)) continue;
                G.projectiles.applyHit(it.owner, e, s.splashDamage, "burst");
            }
            damageArea(c, s.radius, 25, it.team);
            // (Boss Battle splash — M7)
        }
        void paint(SubItem it, Vector3 c, float r)
        {
            float area = G.paint.splat(c, r, it.team, new SplatOpts { seed = random() });
            for (int k = 0; k < 4; k++)
            {
                float a = random() * Mathf.PI * 2, rr = r * (0.55f + random() * 0.45f);
                area += G.paint.splat(new Vector3(c.x + Mathf.Cos(a) * rr, c.y + 0.3f, c.z + Mathf.Sin(a) * rr), 0.5f + random() * 0.4f, it.team, new SplatOpts { seed = random() });
            }
            credit(it, area);
        }
        void paintUnder(SubItem it, float r) => credit(it, G.paint.splat(new Vector3(it.pos.x, it.pos.y + 0.2f, it.pos.z), r, it.team, new SplatOpts { seed = random() }));
        // turf for the thrower; ink from a special (Bomb Barrage throws) never charges the special meter
        static void credit(SubItem it, float area) { if (it.sp) it.owner.addTurfNoSpecial(area); else it.owner.addTurf(area); }

        // ---------------------------------------------------------------------------------------------- blocking + damage
        void hurt(SubItem it, float dmg)
        {
            if (it.state == "dead") return;
            it.hp -= dmg;
            if (it.state == "curtain") return;               // curtains fade instead; curtain() removes them at 0
            if (it.hp <= 0) destroyItem(it);
        }
        // an enemy shot segment: curtains absorb it (and lose hp); devices it touches are damaged
        public bool blockShot(Vector3 prev, Vector3 pos, int team, float dmg)
        {
            foreach (var k in Kits.MAIN.Values) if (k.blockShot(prev, pos, team, dmg)) return true;   // kit shields
            foreach (var k in Kits.SUB.Values) if (k.blockShot(prev, pos, team, dmg)) return true;    // shoot-able bombs
            foreach (var it in items)
            {
                if (it.team == team) continue;
                if (it.state == "curtain")
                {
                    var s = it.sub;
                    float a = Vector3.Dot(prev - it.pos, it.n), b = Vector3.Dot(pos - it.pos, it.n);
                    if ((a > 0) == (b > 0)) continue;
                    float f = a / (a - b);
                    Vector3 p = Vector3.Lerp(prev, pos, f) - it.pos;
                    if (Mathf.Abs(Vector3.Dot(p, it.tan)) > s.width / 2 || p.y < -0.2f || p.y > s.height) continue;
                    hurt(it, dmg * s.shotMul);
                    if (nearCam(it.pos)) G.fx?.burst(p + it.pos, it.n, G.teamColors[it.team], new FxOpts { count = 3, speed = 2, size = 0.06f });
                    return true;
                }
                if (it.state == "spray" || it.state == "beacon")
                {
                    float h = it.lift * 2;                // model height
                    Vector3 c = it.pos + it.normal * (h * 0.5f);
                    var (dist, _) = Physics.segmentCapsuleDist(prev, pos, new Vector3(c.x, c.y - h * 0.5f, c.z), 0.3f, h);
                    if (dist < 0.34f) { hurt(it, dmg); return true; }
                }
            }
            return false;
        }
        public float blockRay(Vector3 from, Vector3 dir, float len, int team, float dmg)
        {
            foreach (var k in Kits.MAIN.Values) len = Mathf.Min(len, k.blockRay(from, dir, len, team, dmg));   // kit shields
            foreach (var k in Kits.SUB.Values) len = Mathf.Min(len, k.blockRay(from, dir, len, team, dmg));
            float best = len; SubItem hitIt = null;
            foreach (var it in items)
            {
                if (it.team == team || it.state != "curtain") continue;
                var s = it.sub;
                float dn = Vector3.Dot(dir, it.n);
                if (Mathf.Abs(dn) < 1e-4f) continue;
                float t = Vector3.Dot(it.pos - from, it.n) / dn;
                if (t <= 0 || t >= best) continue;
                Vector3 p = from + dir * t - it.pos;
                if (Mathf.Abs(Vector3.Dot(p, it.tan)) > s.width / 2 || p.y < -0.2f || p.y > s.height) continue;
                best = t; hitIt = it;
            }
            if (hitIt != null) hurt(hitIt, dmg * hitIt.sub.shotMul);
            return best;
        }
        public void damageArea(Vector3 c, float radius, float dmg, int team)
        {
            // (specials.js areaHit: bubbles and tanks in the blast — with that module)
            foreach (var k in Kits.SUB.Values) k.damageArea(c, radius, dmg, team);   // kit subs caught in a blast (torpedo)
            foreach (var it in items)
            {
                if (it.team == team || !(it.state == "curtain" || it.state == "spray" || it.state == "beacon")) continue;
                if (Vector3.Distance(it.pos, c) < radius + 0.4f) hurt(it, dmg);
            }
        }
        // enemy players can't walk (or swim) through a curtain: push them back out to the side they're on
        public void blockActor(Actor a)
        {
            foreach (var it in items)
            {
                if (it.state != "curtain" || it.team == a.team) continue;
                var s = it.sub;
                Vector3 d = a.pos - it.pos;
                float along = Vector3.Dot(d, it.tan), across = Vector3.Dot(d, it.n), r = PLAYER.radius + 0.12f;
                if (Mathf.Abs(along) > s.width / 2 + PLAYER.radius || d.y < -1.2f || d.y > s.height || Mathf.Abs(across) > r) continue;
                float side = across >= 0 ? 1 : -1;
                a.pos += it.n * (side * r - across);
                float vn = Vector3.Dot(a.vel, it.n);
                if (vn * side < 0) a.vel -= it.n * vn;
            }
        }

        void destroyItem(SubItem it)
        {
            if (it.state == "dead") return;
            Vector3 c = it.pos + it.normal * 0.2f;
            if (nearCam(c, 40))
            {
                G.fx?.burst(c, it.normal, G.teamColors[it.team], new FxOpts { count = 12, speed = 4, size = 0.08f });
                G.audio?.play("splat_small", new AudioOpts { pos = c, volume = 0.7f, pitch = 0.8f });
            }
            emit("sub:destroyed", new Ev { kind = it.kind, pos = c, team = it.team });
            it.state = "dead";
        }
        void disposeItem(SubItem it)
        {
            if (it.mesh != null) destroy(it.mesh.gameObject);
            if (it.cloud != null) destroy(it.cloud.gameObject);
            if (it.cloudMat != null) destroy(it.cloudMat);
            if (it.sheetMat != null) destroy(it.sheetMat);
        }
    }
}
