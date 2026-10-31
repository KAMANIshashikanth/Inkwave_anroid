// Port of src/game/kits/waddle.js — Waddle Bomb (kit sub 'waddle'), see Kits.cs.
//
// Thrown in an arc. When it lands it shows a sensing circle (senseRadius). If a foe is inside that circle, either at
// once or at any point during the short fuse, the bomb stands up and waddles after them. It is a little legged bomb
// that turns freely and follows its foe along the nav graph: it walks, steps off ledges and hops up steps, and it never
// walks into the sea. It keeps going for up to `life` s or `maxTravel` m, and goes off when it reaches a foe
// (triggerDist) or runs out. If nobody is near when it lands, it blows where it sits after `fuse`, like a Cling Charge.
// It lays no ink while walking and pays no mind to enemy ink. It patters and beeps the whole time it tracks (a
// positional loop), so its foe hears it coming, and the path it takes gives away where they're hiding. Enemy fire or
// an enemy blast pops it harmlessly (`hp`).
//
// Events: 'sub:use' { actor, kind }, 'sub:land' { kind, pos, team, radius }, 'sub:lock' { kind, pos, team, actor, target },
// 'bomb:explode' { actor, pos, team, radius, kind }, 'sub:destroyed' { kind, pos, team }, 'sub:end' { kind, why, team, pos }.
// The walking model is baked from waddle.js's worldGeo (sub_waddle: worldBody, worldInk, legR / legL, lamp); the sensing
// circle is Shaders/Fx/SenseRing. Not ported: the online ghosts, the Boss Battle splash (M7), the sounds (M5: by name).
using System.Collections.Generic;
using Inkwave.World;
using UnityEngine;
using UnityEngine.Rendering;
using static Inkwave.Ctx;

namespace Inkwave
{
    public class Waddle : SubKit
    {
        static readonly Vector3 UP = new Vector3(0, 1, 0);
        static readonly Hit _hit = new Hit(), _hit2 = new Hit();
        const float GRAV = 24;
        const float WSCALE = 1.8f;               // hand-scale model → world (≈0.4 m tall with its antenna)
        const float MID = 0.11f * WSCALE;        // world height of the body's middle (it tumbles about it in flight)
        const float STRIDE = 5.5f;               // stride cycles per second at full walking speed (two steps each)
        static readonly Vector3[] HIP = { new Vector3(0.03f, 0.058f, 0.004f), new Vector3(-0.03f, 0.058f, 0.004f) };
        static readonly Vector3 LAMP = new Vector3(0, 0.2195f, 0);
        static readonly int ID_Color = Shader.PropertyToID("_Color"), ID_Time = Shader.PropertyToID("_Time2"), ID_Alpha = Shader.PropertyToID("_Alpha"),
            ID_Grow = Shader.PropertyToID("_Grow"), ID_Lock = Shader.PropertyToID("_Lock"), ID_Sweep = Shader.PropertyToID("_Sweep");

        public Waddle() { bot = new SubKitBot { fight = botFight, paint = botPaint }; }

        // ================================================================================================= model
        class WorldGeo { public Mesh body, ink, lamp; public Mesh[] legs; }
        static WorldGeo _wg;
        static WorldGeo worldGeo() => _wg ??= new WorldGeo
        {
            body = WeaponModel.loadMesh("sub_waddle/worldBody"), ink = WeaponModel.loadMesh("sub_waddle/worldInk"), lamp = WeaponModel.loadMesh("sub_waddle/lamp"),
            legs = new[] { WeaponModel.loadMesh("sub_waddle/legR"), WeaponModel.loadMesh("sub_waddle/legL") },
        };
        // outer (at the feet, yaw = heading) → rock (waddle roll about the feet) → tilt (tumble about the middle) → model
        class Rig { public Transform outer, rock, tilt, model; public Transform[] legs; public Material lampMat; public float ry, tx, tz; }
        static Rig makeMesh(int team)
        {
            var g = worldGeo(); Color col = G.teamColors[team];
            var M = new Rig { outer = ThreeSpace.create("waddle").transform };
            M.rock = ThreeSpace.create("rock", M.outer).transform;
            M.tilt = ThreeSpace.create("tilt", M.rock).transform; M.tilt.localPosition = new Vector3(0, MID, 0);
            M.model = ThreeSpace.create("model", M.tilt).transform; M.model.localPosition = new Vector3(0, -MID, 0); M.model.localScale = Vector3.one * WSCALE;
            part("body", M.model, g.body, CharacterMats.plastic, true);
            part("ink", M.model, g.ink, CharacterMats.ink(col), true);
            M.legs = new Transform[2];
            for (int i = 0; i < 2; i++)
            {
                var pv = ThreeSpace.create("leg" + i, M.model).transform; pv.localPosition = HIP[i];
                part("legMesh", pv, g.legs[i], CharacterMats.plastic, true);
                M.legs[i] = pv;
            }
            M.lampMat = CharacterMats.lampMaterial(new Color(0x3a / 255f, 0x08 / 255f, 0x06 / 255f), 0.3f);
            CharacterMats.setLamp(M.lampMat, new Color(0xff / 255f, 0x2a / 255f, 0x1a / 255f).linear, 0.3f);
            part("lamp", M.model, g.lamp, M.lampMat, false).transform.localPosition = LAMP;
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
        static readonly Color LAMP_EM = new Color(0xff / 255f, 0x2a / 255f, 0x1a / 255f);
        static void setLamp(Rig M, float k) => CharacterMats.setLamp(M.lampMat, LAMP_EM.linear, k);

        // ---- the sensing circle: a team-coloured rim, a dashed inner ring, a radar sweep and a pulse; _Grow 0..1 opens it
        static Mesh _ringGeo;
        static Mesh ringGeo()
        {
            if (_ringGeo != null) return _ringGeo;
            // PlaneGeometry(2, 2).rotateX(-π/2): flat on the floor, uv (0,0) at (-1, +1) … three's plane uv
            var m = new Mesh { name = "waddle-ring" };
            m.vertices = new[] { new Vector3(-1, 0, -1), new Vector3(1, 0, -1), new Vector3(-1, 0, 1), new Vector3(1, 0, 1) };
            m.uv = new[] { new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, 0), new Vector2(1, 0) };
            m.triangles = new[] { 0, 2, 1, 1, 2, 3 };
            m.RecalculateBounds();
            return _ringGeo = m;
        }
        class Ring { public Transform t; public Material mat; }
        static Ring makeRing(Color col, float alpha = 1)
        {
            var R = new Ring { mat = new Material(Shader.Find("Inkwave/Fx/SenseRing")) { name = "iw-waddle-ring", renderQueue = 3003 } };
            Color c = col.linear;
            R.mat.SetVector(ID_Color, new Vector4(c.r, c.g, c.b, 1)); R.mat.SetFloat(ID_Alpha, alpha); R.mat.SetFloat(ID_Grow, 0); R.mat.SetFloat(ID_Lock, 0); R.mat.SetFloat(ID_Sweep, 1);
            var go = ThreeSpace.create("waddleRing");
            go.AddComponent<MeshFilter>().sharedMesh = ringGeo();
            var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = R.mat; r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
            R.t = go.transform;
            return R;
        }
        static void dropRing(Ring R) { if (R == null) return; destroy(R.t.gameObject); destroy(R.mat); }

        // ================================================================================================= the bomb
        static void botLob(Actor a, SubDef sub, float shortBy)
        {
            var b = a.bot; var t = b?.target;
            if (t == null || !t.alive || b.mode != "fight") return;
            float tx = t.pos.x + t.vel.x * 0.5f, tz = t.pos.z + t.vel.z * 0.5f;          // lead it by about the flight time
            var p = Shaker.lobPitch(Mathf.Max(1.5f, Mathf.Sqrt((tx - a.pos.x) * (tx - a.pos.x) + (tz - a.pos.z) * (tz - a.pos.z)) - shortBy), t.pos.y - a.pos.y, sub.throwSpeed);
            if (p == null) return;
            a.aimYaw = Mathf.Atan2(tx - a.pos.x, tz - a.pos.z); a.aimPitch = p.Value;
        }
        class Hop { public float t, dur, x0, y0, z0, x1, y1, z1; }
        class Item
        {
            public Actor owner, target; public int team; public SubDef sub; public Rig m; public bool sp, air, noisy, blinked, blasted;
            public Vector3 pos, vel, spin; public string state, why; public float t, age, hp, heading, travel, walkT, lostT, repath, phase, ringT, blink;
            public List<int> path; public int pi; public Hop hop;
            public float progT, progX, progZ;
            public Ring ring; public AudioLoop loop; public Threat thr;
        }
        static readonly List<Item> items = new List<Item>();

        public override void use(SubSystem subs, Actor a, SubDef sub)
        {
            if (a.bot != null) botLob(a, sub, 2);         // land it just short of the foe: it walks the rest
            Vector3 pos = a.pos; pos.y += 1.35f;
            Vector3 vel = G.projectiles.throwVelocity(a, sub.throwSpeed);
            var it = new Item
            {
                owner = a, team = a.team, sub = sub, m = makeMesh(a.team), pos = pos, vel = vel, state = "fly", hp = sub.hp, sp = a.specialActive != null,
                heading = a.aimYaw, spin = new Vector3(2 + random() * 3, 0, 2 + random() * 3),
            };
            it.m.outer.localPosition = new Vector3(pos.x, pos.y - MID, pos.z);
            it.m.ry = a.aimYaw; it.m.outer.localRotation = Quaternion.AngleAxis(a.aimYaw * Mathf.Rad2Deg, UP);
            items.Add(it);
            if (a.isLocal || a.nearCamera()) G.audio?.play("bomb_throw", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = 0.6f, pitch = 1.08f });
            emit("sub:use", new Ev { actor = a, kind = "waddle" });
        }

        static bool near(Vector3 p, float r) => (G.camPos - p).sqrMagnitude < r * r;
        static void credit(Item it, float area) { if (it.sp) it.owner.addTurfNoSpecial(area); else it.owner.addTurf(area); }
        static bool alive(Actor e) => e != null && e.alive && e.superJumpState == null;
        // the nearest foe inside the circle (horizontal senseRadius, within senseUp above / below)
        static Actor scan(Item it, float R)
        {
            Actor best = null; float bd = float.PositiveInfinity;
            foreach (var e in G.actors)
            {
                if (e.team == it.team || !alive(e)) continue;
                float d = Mathf.Sqrt((e.pos.x - it.pos.x) * (e.pos.x - it.pos.x) + (e.pos.z - it.pos.z) * (e.pos.z - it.pos.z));
                if (d > R || Mathf.Abs(e.pos.y - it.pos.y) > it.sub.senseUp) continue;
                if (d < bd) { bd = d; best = e; }
            }
            return best;
        }
        static void setRy(Item it, float y) { it.m.ry = y; it.m.outer.localRotation = Quaternion.AngleAxis(y * Mathf.Rad2Deg, UP); }

        // ---- flight: arcs like the other thrown subs; floors stop it, walls and ceilings bounce it
        static void fly(Item it, float dt)
        {
            it.vel.y -= GRAV * dt;
            Vector3 prev = it.pos;
            it.pos += it.vel * dt;
            var h = G.physics.segment(prev, it.pos, _hit);
            if (h.hit)
            {
                Vector3 n = h.normal;
                if (n.y > 0.6f) { land(it, h); return; }
                it.pos = h.point + n * 0.15f;
                float vn = Vector3.Dot(it.vel, n);
                it.vel = (it.vel + n * (-vn * 1.45f)) * 0.55f;
            }
            if (it.pos.y < PLAYER.waterY - 1.8f) { it.why = "sea"; plop(it); return; }
            it.m.outer.localPosition = new Vector3(it.pos.x, it.pos.y - MID, it.pos.z);
            it.m.tx += it.spin.x * dt; it.m.tz += it.spin.z * dt;
            it.m.tilt.localRotation = ThreeSpace.eulerXYZ(it.m.tx, 0, it.m.tz);
        }
        static void plop(Item it)
        {
            if (near(it.pos, 40)) G.audio?.play("splat_small", new AudioOpts { pos = it.pos, volume = 0.5f, pitch = 0.7f });
            it.state = "dead";
        }
        static void land(Item it, Hit h)
        {
            var s = it.sub;
            it.pos = h.point;
            it.vel = Vector3.zero;
            it.state = "sense"; it.t = 0;
            it.m.tx = it.m.tz = 0; it.m.tilt.localRotation = Quaternion.identity;
            it.m.outer.localPosition = it.pos;
            Color col = G.teamColors[it.team];
            it.ring = makeRing(col);
            it.ring.t.localPosition = it.pos + h.normal * 0.03f;
            it.ring.t.localScale = Vector3.one * s.senseRadius;
            if (near(it.pos, 40))
            {
                G.audio?.play("waddle_land", new AudioOpts { pos = it.pos, volume = 0.8f });
                G.fx?.ring(it.pos, h.normal, col, new FxOpts { radius = 0.55f, life = 0.3f });
            }
            emit("sub:land", new Ev { kind = "waddle", pos = it.pos, team = it.team, radius = s.senseRadius });
        }

        // ---- sensing: the circle opens; a foe inside (now or before the fuse runs out) wakes it, else it blows where it sits
        static void sense(Item it, float dt)
        {
            var s = it.sub; var M = it.m;
            var tgt = scan(it, s.senseRadius * Mathf.Min(1, it.t / 0.3f + 0.35f));
            if (tgt != null) { lockOn(it, tgt); return; }
            float k = clamp(it.t / s.fuse, 0, 1);
            // looks around, lamp blinking faster, a nervous jitter at the end
            setRy(it, it.heading + Mathf.Sin(it.t * 5) * 0.55f * (1 - k * 0.5f));
            M.rock.localRotation = Quaternion.AngleAxis((k > 0.6f ? (random() * 2 - 1) * 0.06f * k : 0) * Mathf.Rad2Deg, Vector3.forward);
            blinkLamp(it, dt, lerp(0.45f, 0.1f, k), 2.5f + 3 * k);
            if (it.blinked && near(it.pos, 30)) G.audio?.play("bomb_beep", new AudioOpts { pos = it.pos, volume = 0.3f + 0.35f * k, pitch = 1.05f + 0.3f * k });
            if (it.t >= s.fuse) { it.why = "fuse"; blast(it); }
        }
        static void lockOn(Item it, Actor tgt)
        {
            it.target = tgt; it.state = "wake"; it.t = 0; it.ringT = 0; it.repath = 0;
            it.heading = it.m.ry;
            if (near(it.pos, 40))
            {
                G.audio?.play("waddle_lock", new AudioOpts { pos = it.pos });
                G.fx?.glint(new Vector3(it.pos.x, it.pos.y + 0.55f, it.pos.z), G.teamColors[it.team], 0.3f);
            }
            emit("sub:lock", new Ev { kind = "waddle", pos = it.pos, team = it.team, actor = it.owner, target = tgt });
        }
        // ---- waking: a little hop on the spot to face its foe ("!"), then off it goes
        static void wake(Item it, float dt)
        {
            var T = it.target; var M = it.m;
            if (alive(T)) it.heading += clamp(angleDiff(it.heading, Mathf.Atan2(T.pos.x - it.pos.x, T.pos.z - it.pos.z)), -14 * dt, 14 * dt);
            float u = clamp(it.t / 0.32f, 0, 1);
            M.outer.localPosition = new Vector3(it.pos.x, it.pos.y + Mathf.Sin(u * Mathf.PI) * 0.16f, it.pos.z);
            setRy(it, it.heading);
            setLamp(M, 4);
            it.noisy = true;
            if (it.t >= 0.32f) { it.state = "walk"; it.t = 0; it.progT = 0; it.progX = it.pos.x; it.progZ = it.pos.z; }
        }

        // ---- walking after its foe
        static void repath(Item it)
        {
            var nav = G.nav; var T = it.target;
            it.path = null; it.pi = 0;
            if (nav == null || T == null) return;
            int a = nav.nearest(it.pos, 0.8f), b = nav.nearest(T.pos, 1.5f);
            if (a < 0 || b < 0 || a == b) return;
            var p = nav.path(a, b, it.team, 2500, true);   // no climb edges: it can't swim up walls
            if (p != null && p.Count > 1) { it.path = p; it.pi = 1; }
        }
        // where to head: the foe itself when close on the same level (or with no route), else the next node on the route
        static NavNode goal(Item it, out Vector3 g)
        {
            var T = it.target; var nav = G.nav;
            float dh = Mathf.Sqrt((T.pos.x - it.pos.x) * (T.pos.x - it.pos.x) + (T.pos.z - it.pos.z) * (T.pos.z - it.pos.z)), dy = T.pos.y - it.pos.y;
            if ((dh < 2.2f && Mathf.Abs(dy) < 0.8f) || it.path == null) { g = T.pos; return null; }
            while (it.pi < it.path.Count)
            {
                var nn = nav.nodes[it.path[it.pi]];
                if (Mathf.Sqrt((nn.x - it.pos.x) * (nn.x - it.pos.x) + (nn.z - it.pos.z) * (nn.z - it.pos.z)) < 0.45f && Mathf.Abs(nn.y - it.pos.y) < 0.6f) it.pi++; else break;
            }
            if (it.pi >= it.path.Count) { g = T.pos; return null; }
            var n = nav.nodes[it.path[it.pi]];
            g = new Vector3(n.x, n.y, n.z);
            return n;
        }
        static void walk(Item it, float dt)
        {
            var s = it.sub;
            it.walkT += dt;
            // lost its foe (splatted, super jumped away): the nearest other foe in range, else it goes off
            if (!alive(it.target))
            {
                var nt = scan(it, s.senseRadius);
                if (nt != null) { it.target = nt; it.repath = 0; it.lostT = 0; }
                else if ((it.lostT += dt) > 0.5f) { it.why = "lost"; blast(it); return; }
            }
            var T = alive(it.target) ? it.target : null;
            // reached a foe (any foe) → boom
            Vector3 c = it.pos; c.y += 0.3f;
            foreach (var e in G.actors)
            {
                if (e.team == it.team || !e.alive) continue;
                Vector3 ep = new Vector3(e.pos.x, e.pos.y + (e.form == "squid" ? 0.25f : 0.7f), e.pos.z);
                if (Vector3.Distance(ep, c) < s.triggerDist) { it.why = "reached"; blast(it); return; }
            }
            if (it.walkT > s.life || it.travel > s.maxTravel) { it.why = it.travel > s.maxTravel ? "travel" : "life"; blast(it); return; }
            float moved = 0;
            if (it.hop != null) moved = hopStep(it, dt);
            else if (it.air) moved = fallStep(it, dt);
            else if (T != null)
            {
                if ((it.repath -= dt) <= 0) { it.repath = 0.35f; repath(it); }
                var node = goal(it, out var g);
                float turn = angleDiff(it.heading, Mathf.Atan2(g.x - it.pos.x, g.z - it.pos.z));
                it.heading += clamp(turn, -s.turnRate * dt, s.turnRate * dt);
                // the next node is a step up (a nav 'jump' edge): hop onto it
                if (node != null && node.y - it.pos.y > 0.45f && Mathf.Sqrt((node.x - it.pos.x) * (node.x - it.pos.x) + (node.z - it.pos.z) * (node.z - it.pos.z)) < 1.35f) { startHop(it, node); moved = hopStep(it, dt); }
                else moved = stepWalk(it, dt, s.speed * (Mathf.Abs(turn) > 1.3f ? 0.45f : 1));
            }
            if (it.state == "dead") return;
            it.travel += moved;
            // stuck (pressed into a wall, stopped at the water's edge with no way round): give up and go off
            it.progT += dt;
            if (it.progT > 1.4f)
            {
                float pd = Mathf.Sqrt((it.pos.x - it.progX) * (it.pos.x - it.progX) + (it.pos.z - it.progZ) * (it.pos.z - it.progZ));
                if (pd < 0.4f && it.hop == null && !it.air) { it.why = "stuck"; blast(it); return; }
                it.progT = 0; it.progX = it.pos.x; it.progZ = it.pos.z;
            }
            look(it, dt, moved, T);
        }
        static void look(Item it, float dt, float moved, Actor T)
        {
            var s = it.sub; var M = it.m;
            // ---- look: the waddle (alternating feet, a side-to-side roll, a forward lean), beacon blinking, noise
            float k = s.speed > 0 ? clamp(moved / (s.speed * dt + 1e-6f), 0, 1.2f) : 0;
            it.phase += dt * STRIDE * k;
            float sw = Mathf.Sin(it.phase * Mathf.PI * 2);
            M.legs[0].localRotation = Quaternion.AngleAxis(0.6f * sw * k * Mathf.Rad2Deg, Vector3.right); M.legs[1].localRotation = Quaternion.AngleAxis(-0.6f * sw * k * Mathf.Rad2Deg, Vector3.right);
            M.legs[0].localPosition = new Vector3(HIP[0].x, HIP[0].y + Mathf.Max(0, -sw) * 0.009f * k, HIP[0].z);
            M.legs[1].localPosition = new Vector3(HIP[1].x, HIP[1].y + Mathf.Max(0, sw) * 0.009f * k, HIP[1].z);
            M.rock.localRotation = ThreeSpace.eulerXYZ(-0.1f * k, 0, 0.17f * sw * k);
            M.model.localPosition = new Vector3(0, -MID + Mathf.Abs(sw) * 0.012f * k, 0);
            if (it.hop == null && !it.air) M.outer.localPosition = it.pos;
            setRy(it, it.heading + 0.1f * sw * k);
            float close = T != null ? clamp(1 - Mathf.Sqrt((T.pos.x - it.pos.x) * (T.pos.x - it.pos.x) + (T.pos.z - it.pos.z) * (T.pos.z - it.pos.z)) / s.senseRadius, 0, 1) : 0;
            blinkLamp(it, dt, lerp(0.55f, 0.18f, close), 3.5f);
            noise(it, 1 + 0.45f * close);
        }
        // one step on the flat: slide along walls, snap onto the ground (steps ≤ 0.45 m), never onto open water
        static float stepWalk(Item it, float dt, float spd)
        {
            float hx = Mathf.Sin(it.heading), hz = Mathf.Cos(it.heading), step = spd * dt;
            float mx = hx * step, mz = hz * step;
            Vector3 o = it.pos; o.y += 0.42f;
            var w = G.physics.raycast(o, new Vector3(hx, 0, hz), step + 0.2f, _hit);
            if (w.hit && Mathf.Abs(w.normal.y) < 0.5f)
            {
                Vector3 n = w.normal; float dn = mx * n.x + mz * n.z;
                mx -= dn * n.x; mz -= dn * n.z;
                float l = Mathf.Sqrt(mx * mx + mz * mz);
                if (l > 1e-5f) { var w2 = G.physics.raycast(o, new Vector3(mx / l, 0, mz / l), l + 0.2f, _hit2); if (w2.hit && Mathf.Abs(w2.normal.y) < 0.5f) { mx = 0; mz = 0; } }
            }
            float nx = it.pos.x + mx, nz = it.pos.z + mz, moved = Mathf.Sqrt(mx * mx + mz * mz);
            if (moved < 1e-6f) return 0;
            float gy = G.level.groundHeight(nx, nz, it.pos.y + 0.45f);
            if (float.IsNegativeInfinity(gy)) return 0;                      // the sea / nothing ahead: stay on the deck
            if (gy < it.pos.y - 0.4f)
            {                                                                 // off a ledge: drop down
                it.air = true; it.vel = new Vector3(mx / dt, 0.6f, mz / dt);
                it.pos.x = nx; it.pos.z = nz;
                return moved;
            }
            it.pos = new Vector3(nx, gy, nz);
            return moved;
        }
        static float fallStep(Item it, float dt)
        {
            float x0 = it.pos.x, z0 = it.pos.z, y0 = it.pos.y;
            it.vel.y -= GRAV * dt;
            Vector3 o = it.pos; o.y += 0.3f;
            float hl = Mathf.Sqrt(it.vel.x * it.vel.x + it.vel.z * it.vel.z);
            if (hl > 1e-4f) { var w = G.physics.raycast(o, new Vector3(it.vel.x / hl, 0, it.vel.z / hl), hl * dt + 0.15f, _hit); if (w.hit && Mathf.Abs(w.normal.y) < 0.5f) { it.vel.x = 0; it.vel.z = 0; } }
            it.pos += it.vel * dt;
            float gy = G.level.groundHeight(it.pos.x, it.pos.z, y0 + 0.05f);
            if (!float.IsNegativeInfinity(gy) && it.pos.y <= gy)
            {
                it.pos.y = gy; it.air = false; it.vel = Vector3.zero; it.repath = 0;
                if (near(it.pos, 30)) G.audio?.play("waddle_land", new AudioOpts { pos = it.pos, volume = 0.45f, pitch = 1.2f });
            }
            if (it.pos.y < PLAYER.waterY - 1.2f) { it.why = "fell"; plop(it); return 0; }
            it.m.outer.localPosition = it.pos;
            return Mathf.Sqrt((it.pos.x - x0) * (it.pos.x - x0) + (it.pos.z - z0) * (it.pos.z - z0));
        }
        static void startHop(Item it, NavNode node)
        {
            it.hop = new Hop { t = 0, dur = 0.5f, x0 = it.pos.x, y0 = it.pos.y, z0 = it.pos.z, x1 = node.x, y1 = node.y, z1 = node.z };
            it.heading = Mathf.Atan2(node.x - it.pos.x, node.z - it.pos.z);
            it.pi++;
            if (near(it.pos, 30)) G.audio?.play("waddle_hop", new AudioOpts { pos = it.pos, volume = 0.6f });
        }
        // a hop up a step: rises first, then over (horizontal eased in), landing on the node
        static float hopStep(Item it, float dt)
        {
            var H = it.hop; float x0 = it.pos.x, z0 = it.pos.z;
            H.t += dt;
            float u = clamp(H.t / H.dur, 0, 1), p = u * u;
            it.pos = new Vector3(lerp(H.x0, H.x1, p), lerp(H.y0, H.y1, u) + 4 * 0.5f * u * (1 - u), lerp(H.z0, H.z1, p));
            it.m.outer.localPosition = it.pos;
            if (u >= 1) { it.hop = null; it.repath = 0; }
            return Mathf.Sqrt((it.pos.x - x0) * (it.pos.x - x0) + (it.pos.z - z0) * (it.pos.z - z0));
        }

        static void blinkLamp(Item it, float dt, float period, float bright)
        {
            it.blink -= dt; it.blinked = false;
            if (it.blink <= 0) { it.blink = period; it.blinked = true; }
            setLamp(it.m, it.blink > period - 0.08f ? bright : 0.25f);
        }
        static void noise(Item it, float pitch)
        {
            it.noisy = true;
            if (near(it.pos, 42) && G.audio != null)
            {
                it.loop ??= G.audio.loop("waddle_walk", new AudioOpts { pos = it.pos, volume = 0.9f, pitch = pitch });
                it.loop?.set(null, pitch, it.pos);
            }
            else if (it.loop != null) { it.loop.stop(0.15f); it.loop = null; }
        }
        static void hush(Item it) { it.noisy = false; if (it.loop != null) { it.loop.stop(0.1f); it.loop = null; } }

        static void blast(Item it)
        {
            if (it.state == "dead") return;
            var s = it.sub; var a = it.owner; int team = it.team; Color col = G.teamColors[team];
            it.blasted = true;
            Vector3 c = it.pos; c.y += 0.25f;
            float area = G.paint.splat(new Vector3(it.pos.x, it.pos.y + 0.2f, it.pos.z), s.paintRadius, team, new SplatOpts { seed = random() });
            for (int k = 0; k < 4; k++)
            {
                float ang = random() * Mathf.PI * 2, r = s.paintRadius * (0.55f + random() * 0.45f);
                area += G.paint.splat(new Vector3(c.x + Mathf.Cos(ang) * r, c.y + 0.3f, c.z + Mathf.Sin(ang) * r), 0.5f + random() * 0.4f, team, new SplatOpts { seed = random() });
            }
            credit(it, area);
            G.fx?.explosion(c, col, s.radius);
            G.audio?.play("bomb_explode", new AudioOpts { pos = c });
            emit("shake", new Ev { pos = c, amount = 0.6f });
            emit("bomb:explode", new Ev { actor = a, pos = c, team = team, radius = s.radius, kind = "waddle" });
            var loc = G.local;
            if (loc != null && loc.alive) { float d = Vector3.Distance(loc.pos, c); if (d < 14) Actor.rumble(loc, clamp(1 - d / 14, 0, 1) * 0.6f, clamp(1 - d / 14, 0, 1) * 0.5f, 160); }
            Vector3 from = new Vector3(c.x, c.y + 0.3f, c.z);
            foreach (var e in G.actors)
            {
                if (e.team == team || !e.alive) continue;
                Vector3 ep = new Vector3(e.pos.x, e.pos.y + (e.form == "squid" ? 0.3f : 0.7f), e.pos.z);
                float d = Vector3.Distance(ep, c);
                if (d > s.radius || !G.physics.los(from, ep)) continue;
                float kk = 1 - clamp((d - 0.8f) / (s.radius - 0.8f), 0, 1);
                G.projectiles.applyHit(a, e, lerp(s.damageMin, s.damageMax, kk * kk), "waddle");
            }
            G.subs.damageArea(c, s.radius, 60, team);
            // (Boss Battle splash — M7)
            it.state = "dead";
        }
        // popped by enemy fire / an enemy blast: harmless
        static void hurt(Item it, float dmg)
        {
            if (it.state == "dead" || it.state == "fly") return;
            it.hp -= dmg;
            Vector3 c = it.pos; c.y += 0.22f;
            if (near(c, 30)) G.fx?.burst(c, UP, G.teamColors[it.team], new FxOpts { count = 3, speed = 2, size = 0.05f, sheet = false });
            if (it.hp > 0) return;
            pop(it);
        }
        static void pop(Item it)
        {
            Vector3 c = it.pos; c.y += 0.22f;
            if (near(c, 40))
            {
                G.fx?.burst(c, UP, G.teamColors[it.team], new FxOpts { count = 12, speed = 3.5f, size = 0.07f });
                G.audio?.play("splat_small", new AudioOpts { pos = c, volume = 0.8f, pitch = 1.3f });
            }
            emit("sub:destroyed", new Ev { kind = "waddle", pos = c, team = it.team });
            it.why = "popped"; it.state = "dead";
        }

        static void dispose(Item it)
        {
            emit("sub:end", new Ev { kind = "waddle", why = it.why ?? "cleared", team = it.team, pos = it.pos });
            hush(it);
            destroy(it.m.outer.gameObject); destroy(it.m.lampMat);
            dropRing(it.ring); it.ring = null;
        }

        // the ring: open while sensing; on a lock it flashes and closes in on the bomb; gone once it's walking
        static void ringFx(Item it, float dt)
        {
            var R = it.ring;
            if (R == null) return;
            R.mat.SetFloat(ID_Time, G.time);
            if (it.state == "sense") { R.mat.SetFloat(ID_Grow, 1 - Mathf.Pow(1 - clamp(it.t / 0.3f, 0, 1), 3)); R.mat.SetFloat(ID_Alpha, 1); }
            else
            {
                it.ringT += dt;
                float k = clamp(it.ringT / 0.4f, 0, 1);
                R.mat.SetFloat(ID_Lock, 1 - k); R.mat.SetFloat(ID_Sweep, 0); R.mat.SetFloat(ID_Grow, 1 - k * k * 0.92f); R.mat.SetFloat(ID_Alpha, 1 - k * k);
                if (k >= 1) { dropRing(R); it.ring = null; }
            }
        }

        // aiming preview: the sensing circle around the throw arc's landing ring
        static Ring preview;
        static void previewFx()
        {
            var a = G.local; var ar = G.projectiles?.arcRingShown;
            bool show = a != null && a.alive && a.weaponRunner.aimingSub && a.sub?.kind == "waddle" && ar != null;
            if (!show) { if (preview != null) preview.t.gameObject.SetActive(false); return; }
            if (preview == null) { preview = makeRing(a.color, 0.4f); preview.mat.SetFloat(ID_Grow, 1); preview.mat.SetFloat(ID_Sweep, 0); }
            Color c = a.color.linear;
            preview.mat.SetVector(ID_Color, new Vector4(c.r, c.g, c.b, 1));
            preview.mat.SetFloat(ID_Time, G.time);
            preview.t.localPosition = ar.localPosition;
            preview.t.localScale = Vector3.one * a.sub.senseRadius;
            preview.t.gameObject.SetActive(true);
        }

        // ---- query hook for bots (they dodge / shoot enemy Waddles): one stable descriptor per bomb, the getters read its
        // live state. Read-only: nothing here changes how the bomb behaves.
        class Threat : ThreatInfo
        {
            public Item it;
            public override Vector3 pos => it.pos;
            public override bool live => it.state != "dead";
            public override string state => it.state;
            public override float hp => it.hp;
            public override bool shootable => it.state != "fly" && it.state != "dead";
            public override bool locked => it.state == "wake" || it.state == "walk";
            public override Actor target => (it.state == "wake" || it.state == "walk") && alive(it.target) ? it.target : null;
            // walking: its heading at full speed (it lays no velocity of its own); in the air / falling: its velocity
            public override Vector3 vel
            {
                get
                {
                    var s = it.sub;
                    if (it.state == "walk" && it.hop == null && !it.air) return new Vector3(Mathf.Sin(it.heading) * s.speed, 0, Mathf.Cos(it.heading) * s.speed);
                    return it.state == "fly" || it.air ? it.vel : Vector3.zero;
                }
            }
            // seconds of chase it has left before it gives up and goes off where it is (life / max travel)
            public override float left => it.state == "walk" ? Mathf.Max(0, Mathf.Min(it.sub.life - it.walkT, (it.sub.maxTravel - it.travel) / it.sub.speed)) : it.sub.life;
        }
        static Threat threatOf(Item it)
        {
            if (it.thr != null) return it.thr;
            var s = it.sub;
            return it.thr = new Threat { it = it, kind = "waddle", team = it.team, owner = it.owner, aimY = MID, radius = s.radius, trigger = s.triggerDist, speed = s.speed, senseRadius = s.senseRadius, ground = true };
        }

        // ================================================================================================= registration
        public override void threats(List<ThreatInfo> o) { foreach (var it in items) if (it.state != "dead") o.Add(threatOf(it)); }
        public override void tick(float dt)
        {
            for (int i = items.Count - 1; i >= 0; i--)
            {
                var it = items[i];
                it.age += dt; it.t += dt;
                switch (it.state)
                {
                    case "fly": fly(it, dt); break;
                    case "sense": sense(it, dt); break;
                    case "wake": wake(it, dt); break;
                    case "walk": walk(it, dt); break;
                }
                if (it.state != "dead") ringFx(it, dt);
                if (it.state == "dead") { dispose(it); items.RemoveAt(i); }
            }
            previewFx();
        }
        public override void clear()
        {
            foreach (var it in items) dispose(it);
            items.Clear();
            if (preview != null) { dropRing(preview); preview = null; }
        }
        // enemy shots and beams pop it (it absorbs the shot)
        public override bool blockShot(Vector3 prev, Vector3 pos, int team, float dmg)
        {
            foreach (var it in items)
            {
                if (it.team == team || it.state == "fly" || it.state == "dead") continue;
                var (d, _) = Physics.segmentCapsuleDist(prev, pos, it.pos, 0.17f, 0.4f);
                if (d < 0.2f) { hurt(it, dmg); return true; }
            }
            return false;
        }
        public override float blockRay(Vector3 from, Vector3 dir, float len, int team, float dmg)
        {
            float best = len; Item hitIt = null;
            foreach (var it in items)
            {
                if (it.team == team || it.state == "fly" || it.state == "dead") continue;
                Vector3 c = it.pos; c.y += 0.2f;
                float t = Vector3.Dot(c - from, dir);
                if (t <= 0 || t >= best) continue;
                if (Vector3.Distance(from + dir * t, c) < 0.24f) { best = t; hitIt = it; }
            }
            if (hitIt != null) hurt(hitIt, dmg);
            return best;
        }
        public override void damageArea(Vector3 c, float radius, float dmg, int team)
        {
            foreach (var it in items)
            {
                if (it.team == team || it.state == "fly" || it.state == "dead") continue;
                if (Vector3.Distance(it.pos, c) < radius + 0.3f) hurt(it, dmg);
            }
        }

        // lob it into the fight: it walks the rest of the way
        static bool botFight(BotBrain brain, float dist)
        {
            bool go = dist > 4.5f && dist < 15 && random() < 0.02f * (1 + brain.diff.fireDiscipline);
            if (go) brain.bombCd = 6 + random() * 5;
            return go;
        }
        // scouting: a foe we can't see is lurking near where the throw would land — send it to flush them out
        static bool botPaint(BotBrain brain)
        {
            if (random() > 0.03f) return false;
            var a = brain.actor; float lx = a.pos.x + Mathf.Sin(a.aimYaw) * 7.5f, lz = a.pos.z + Mathf.Cos(a.aimYaw) * 7.5f;
            foreach (var e in G.actors)
            {
                if (e.team == a.team || !e.alive || brain.target == e) continue;
                if (Mathf.Sqrt((e.pos.x - lx) * (e.pos.x - lx) + (e.pos.z - lz) * (e.pos.z - lz)) < 6.5f && Mathf.Abs(e.pos.y - a.pos.y) < 3) { brain.bombCd = 9 + random() * 6; return true; }
            }
            return false;
        }
    }
}
