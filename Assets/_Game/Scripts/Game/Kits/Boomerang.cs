// Port of src/game/kits/boomerang.js — Whirl Boomerang (sub kind 'boomerang'), see Kits.cs.
//
// Thrown flat along the aim, it spins out ~9 m (stopping short at a wall), hovers there shredding anything close (small
// rapid ticks + ink under it), then whirls back to the thrower, circles them for a couple of seconds (grazing foes it
// touches) and bursts beside them like a small Splat Bomb. Should it hit a foe while it flies (out or back), it stops
// dead, hovers a moment and goes off like a full Splat Bomb instead, without returning. One out at a time (blocked() →
// the core's "Can't use"). If the thrower is splatted while it's out it fizzles: it drops and pops in a little splash of
// ink, no damage (a boomerang already latched onto a foe still goes off, like any bomb).
// Numbers: Config.SUBS["boomerang"]. Model baked from boomerang.js's builder (sub_boomerang); spin blur:
// Shaders/Fx/SpinBlur. Not ported: the online ghosts, the sounds (M5: by name).
using System.Collections.Generic;
using Inkwave.World;
using UnityEngine;
using UnityEngine.Rendering;
using static Inkwave.Ctx;

namespace Inkwave
{
    public class Boomerang : SubKit
    {
        const string KIND = "boomerang";
        const float WORLD_SCALE = 2.5f;   // the prop is modelled at hand size; in the air it reads bigger (~0.75 m across)
        static readonly Vector3 UP = new Vector3(0, 1, 0), DOWN = new Vector3(0, -1, 0);
        static readonly Hit _hit = new Hit(), _hit2 = new Hit();
        static readonly RegionStats _stats = new RegionStats();
        static readonly int ID_Color = Shader.PropertyToID("_Color"), ID_Alpha = Shader.PropertyToID("_Alpha"), ID_Ang = Shader.PropertyToID("_Ang");
        static bool nearCam(Vector3 p, float r = 34) => (G.camPos - p).sqrMagnitude < r * r;

        public Boomerang()
        {
            bot = new SubKitBot { fight = botFight, paint = botPaint };
            on("splatted", e =>
            {
                foreach (var it in items)
                {
                    if (it.owner != (object)e.victim || it.state == "dead" || it.state == "armed" || it.state == "fizzle") continue;
                    it.state = "fizzle"; it.t = 0;
                    it.vel *= 0.25f;
                    setLoop(it, null, 0, 0);
                }
            });
        }
        public override bool noArc => true;
        public override bool blocked(Actor a, SubDef sub) => active(a);

        // ================================================================================================= model
        // world prop: outer (position) → tilt (bank) → spin (about +Y) → model (centred on the blade's middle, world scale),
        // plus a spin-blur disc on the tilt node that fades in with the spin rate
        class Proto { public WeaponModel.SubDef d; public Vector3 center; public float radius; public Mesh disc; }
        static Proto _proto;
        static Proto proto()
        {
            if (_proto != null) return _proto;
            var d = WeaponModel.getSubDef(KIND);
            var bb = d.body.bounds; Vector3 c = new Vector3(bb.center.x, 0, bb.center.z);
            float r = 0; foreach (var p in d.body.vertices) r = Mathf.Max(r, Mathf.Sqrt((p.x - c.x) * (p.x - c.x) + (p.z - c.z) * (p.z - c.z)));
            // CircleGeometry(r·1.04, 48).rotateX(-π/2): a disc in XZ, uv across the circle
            float R = r * WORLD_SCALE * 1.04f; const int N = 48;
            var v = new Vector3[N + 1]; var uv = new Vector2[N + 1]; var idx = new int[N * 3];
            v[0] = Vector3.zero; uv[0] = new Vector2(0.5f, 0.5f);
            for (int i = 0; i < N; i++)
            {
                float a = i * Mathf.PI * 2 / N, x = Mathf.Cos(a), y = Mathf.Sin(a);
                v[i + 1] = new Vector3(x * R, 0, -y * R); uv[i + 1] = new Vector2((x + 1) / 2, (y + 1) / 2);
                idx[i * 3] = 0; idx[i * 3 + 1] = i + 1; idx[i * 3 + 2] = (i + 1) % N + 1;
            }
            var disc = new Mesh { name = "boomerang-blur" }; disc.vertices = v; disc.uv = uv; disc.triangles = idx; disc.RecalculateBounds();
            return _proto = new Proto { d = d, center = c, radius = r * WORLD_SCALE, disc = disc };
        }
        static void makeMesh(Item it)
        {
            var P = proto(); Color col = G.teamColors[it.team];
            it.outer = ThreeSpace.create("boomerang").transform;
            it.tilt = ThreeSpace.create("tilt", it.outer).transform;
            it.spin = ThreeSpace.create("spin", it.tilt).transform;
            var model = ThreeSpace.create("model", it.spin).transform;
            model.localScale = Vector3.one * WORLD_SCALE;
            model.localPosition = new Vector3(-P.center.x * WORLD_SCALE, 0, -P.center.z * WORLD_SCALE);
            part("body", model, P.d.body, CharacterMats.plastic, true);
            part("ink", model, P.d.ink, CharacterMats.ink(col), true);
            it.blurMat = new Material(Shader.Find("Inkwave/Fx/SpinBlur")) { name = "iw-boomerang-blur", renderQueue = 3003 };
            it.blur = part("blur", it.tilt, P.disc, it.blurMat, false);
            it.blur.enabled = false;
        }
        static MeshRenderer part(string name, Transform parent, Mesh m, Material mat, bool shadow)
        {
            var g = ThreeSpace.create(name, parent);
            g.AddComponent<MeshFilter>().sharedMesh = m;
            var r = g.AddComponent<MeshRenderer>(); r.sharedMaterial = mat;
            r.shadowCastingMode = shadow ? ShadowCastingMode.On : ShadowCastingMode.Off;
            return r;
        }

        // ================================================================================================= the throw plan
        // Start at the throwing hand, flat along the aim (pitch clamped), stop ~range out or short of a wall, never skimming
        // the floor. Shared by the throw and the local player's aim guide.
        class Plan { public Vector3 dir, start, end, wallPoint, wallNormal; public float len, ground = float.NegativeInfinity; public bool wall; }
        static Plan plan(Actor a, SubDef s, Plan o)
        {
            float pitch = clamp(a.aimPitch, -0.5f, 0.25f), cp = Mathf.Cos(pitch);
            o.dir = new Vector3(Mathf.Sin(a.aimYaw) * cp, Mathf.Sin(pitch), Mathf.Cos(a.aimYaw) * cp);
            o.start = a.pos; o.start.y += 1.35f;
            o.start.x += Mathf.Sin(a.aimYaw) * 0.3f; o.start.z += Mathf.Cos(a.aimYaw) * 0.3f;
            var h = G.physics.raycast(o.start, o.dir, s.range + 0.4f, _hit);
            o.wall = h.hit;
            o.len = h.hit ? Mathf.Max(0.3f, h.dist - 0.4f) : s.range;
            if (h.hit) { o.wallPoint = h.point; o.wallNormal = h.normal; }
            o.end = o.start + o.dir * o.len;
            float gy = G.level.groundHeight(o.end.x, o.end.z, o.end.y + 0.3f);
            if (gy > float.NegativeInfinity && o.end.y - gy < 0.55f) o.end.y = gy + 0.55f;
            o.ground = gy;
            return o;
        }
        static readonly Plan _plan = new Plan();

        // ================================================================================================= state
        class Item
        {
            public Actor owner, victim; public int team; public SubDef sub; public bool sp, fromWall, blasted;
            public Transform outer, tilt, spin; public MeshRenderer blur; public Material blurMat;
            public string state; public float t, age, outT, spinA, spinW, trail, tickT, paintT, fxT, beepT, orbitA, blurAng, tx, ty, tz;
            public Vector3 pos, prev, vel, from; public Plan plan;
            public readonly Dictionary<Actor, float> hitCd = new Dictionary<Actor, float>();
            public AudioLoop loop; public string loopName;
        }
        static readonly List<Item> items = new List<Item>();
        static bool active(Actor a) { foreach (var it in items) if (it.owner == a && it.state != "dead") return true; return false; }
        static Vector3 chest(Actor e) => new Vector3(e.pos.x, e.pos.y + (e.form == "squid" ? 0.3f : 0.75f), e.pos.z);
        static void credit(Item it, float area) { if (area > 0) { if (it.sp) it.owner.addTurfNoSpecial(area); else it.owner.addTurf(area); } }
        static Color color(Item it) => G.teamColors[it.team];
        static float bodyH(Actor e) => e.form == "squid" ? PLAYER.squidHeight : PLAYER.height;

        public override void use(SubSystem subs, Actor a, SubDef sub)
        {
            var P = plan(a, sub, new Plan());
            var it = new Item
            {
                owner = a, team = a.team, sub = sub, sp = a.specialActive != null, state = "out",
                pos = P.start, prev = P.start, plan = P, outT = Mathf.Max(0.18f, sub.outTime * Mathf.Sqrt(P.len / sub.range)),
                spinA = random() * 6, spinW = 30, fromWall = P.wall,
            };
            makeMesh(it);
            it.outer.localPosition = P.start;
            items.Add(it);
            setLoop(it, "boomerang_whirr", 0.55f, 1.15f);
            if (a.isLocal || a.nearCamera()) G.audio?.play("boomerang_throw", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = 0.8f });
            emit("sub:use", new Ev { actor = a, kind = KIND });
        }

        static void setLoop(Item it, string name, float vol, float pitch)
        {
            if (it.loopName == name) { it.loop?.set(vol, pitch, it.pos); return; }
            it.loop?.stop(0.12f); it.loop = null; it.loopName = name;
            if (name != null && G.audio != null && nearCam(it.pos, 45)) it.loop = G.audio.loop(name, new AudioOpts { pos = it.pos, volume = vol, pitch = pitch });
        }

        public override void tick(float dt)
        {
            for (int i = items.Count - 1; i >= 0; i--)
            {
                var it = items[i];
                it.t += dt; it.age += dt;
                it.prev = it.pos;
                switch (it.state)
                {
                    case "out": outStep(it, dt); break;
                    case "hover": hover(it, dt); break;
                    case "back": back(it, dt); break;
                    case "orbit": orbit(it, dt); break;
                    case "armed": armed(it, dt); break;
                    case "fizzle": fizzle(it, dt); break;
                }
                if (it.state == "dead") { dispose(it); items.RemoveAt(i); continue; }
                draw(it, dt);
            }
            guideTick();
        }

        // ---- outbound: eases from throw speed to a stop at the plan's end point; a foe in the way → armed
        static void outStep(Item it, float dt)
        {
            var P = it.plan; float u = clamp(it.t / it.outT, 0, 1), s = P.len * (1 - (1 - u) * (1 - u));
            it.pos = P.start + P.dir * s;
            it.vel = P.dir * ((2 * P.len / it.outT) * (1 - u));
            it.spinW = lerp(30, 24, u);
            if (contact(it)) return;
            streak(it, 0.29f);
            if (u >= 1)
            {
                if (it.fromWall)
                {
                    // stopped at a wall: a splat of ink on it where it struck
                    credit(it, G.paint.splat(P.wallPoint + P.wallNormal * 0.1f, 0.65f, it.team, new SplatOpts { seed = random() }));
                    if (nearCam(it.pos)) { G.fx?.burst(P.wallPoint, P.wallNormal, color(it), new FxOpts { count = 8, speed = 3, size = 0.07f }); G.audio?.play("ink_hit_wall", new AudioOpts { pos = P.wallPoint, volume = 0.6f }); }
                }
                it.state = "hover"; it.t = 0; it.tickT = 0; it.paintT = 0;
                setLoop(it, "boomerang_whirr", 0.75f, 1.35f);
            }
        }

        // ---- hover: spin fast in place, shred what's close (ticks + device damage), ink the floor under it
        static void hover(Item it, float dt)
        {
            var s = it.sub;
            it.spinW = lerp(it.spinW, 34, 1 - Mathf.Exp(-8 * dt));
            it.vel = Vector3.zero;
            // settle to a useful height over the floor (thrown off a ledge / aimed low)
            float gy = G.level.groundHeight(it.pos.x, it.pos.z, it.pos.y + 0.2f);
            if (gy > float.NegativeInfinity && it.pos.y - gy < 4.5f)
            {
                float h = it.pos.y - gy, want = h > 1.6f ? gy + 1.1f : h < 0.6f ? gy + 0.8f : it.pos.y;
                it.pos.y = lerp(it.pos.y, want, 1 - Mathf.Exp(-2.5f * dt));
            }
            shred(it, dt, 1);
            if (it.t >= s.hover) startBack(it);
        }

        // the rapid shredding ticks (hover / armed): foes whose body is within hoverRadius, with a clear line to it
        static void shred(Item it, float dt, float paintK)
        {
            var s = it.sub;
            it.tickT -= dt;
            if (it.tickT <= 0)
            {
                it.tickT += 1 / s.tickRate;
                bool any = false;
                foreach (var e in G.actors)
                {
                    if (e.team == it.team || !e.alive) continue;
                    if (Physics.pointCapsuleDist(it.pos, e.pos, PLAYER.radius, bodyH(e)) > s.hoverRadius) continue;
                    if (!G.physics.los(it.pos, chest(e))) continue;
                    G.projectiles.applyHit(it.owner, e, s.tickDamage, KIND);
                    any = true;
                }
                G.subs.damageArea(it.pos, s.hoverRadius, s.tickDamage, it.team);
                if (any && nearCam(it.pos)) G.audio?.play("boomerang_shred", new AudioOpts { pos = it.pos, volume = 0.7f });
            }
            it.paintT -= dt;
            if (it.paintT <= 0 && paintK > 0)
            {
                it.paintT = 0.2f;
                var g = G.physics.raycast(it.pos, DOWN, 3.4f, _hit2);
                if (g.hit)
                {
                    float a = random() * Mathf.PI * 2, r = random() * 0.45f;
                    credit(it, G.paint.splat(g.point + new Vector3(Mathf.Cos(a) * r, 0.12f, Mathf.Sin(a) * r), s.hoverPaint * paintK * (0.8f + random() * 0.4f), it.team, new SplatOpts { seed = random() }));
                }
            }
        }

        // ---- return: a curving homing flight back to the thrower (through anything); a foe in the way → armed
        static void startBack(Item it)
        {
            it.state = "back"; it.t = 0;
            Vector3 d = chest(it.owner) - it.pos; d.y = 0;
            if (d.sqrMagnitude < 1e-4f) d = new Vector3(0, 0, 1);
            d.Normalize();
            it.vel = new Vector3(-d.z, 0.6f, d.x) * 5.5f;          // kicks out to one side first, so it swings home
            if (nearCam(it.pos, 40)) G.audio?.play("boomerang_return", new AudioOpts { pos = it.pos, volume = 0.8f });
            setLoop(it, "boomerang_whirr", 0.6f, 1.2f);
        }
        static void back(Item it, float dt)
        {
            var s = it.sub; var o = it.owner;
            Vector3 tgt = chest(o); tgt.y += 0.2f;
            float sp = Mathf.Min(s.returnSpeed, 4 + it.t * 34);
            Vector3 v = tgt - it.pos;
            float d = v.magnitude;
            v *= sp / Mathf.Max(d, 1e-3f);
            it.vel = Vector3.Lerp(it.vel, v, 1 - Mathf.Exp(-5 * dt));
            it.pos += it.vel * dt;
            it.spinW = lerp(it.spinW, 26, 1 - Mathf.Exp(-4 * dt));
            if (contact(it)) return;
            streak(it, 0.22f);
            if (Vector3.Distance(it.pos, tgt) < s.orbitRadius + 0.35f)
            {
                it.state = "orbit"; it.t = 0;
                it.orbitA = Mathf.Atan2(it.pos.z - o.pos.z, it.pos.x - o.pos.x);
                it.from = it.pos;
                it.paintT = 0;
                setLoop(it, "boomerang_orbit", 0.7f, 1);
            }
            else if (it.t > 3) burst(it);              // couldn't catch up (super jump …): goes off where it is
        }

        // ---- orbit: circles the thrower, grazing foes it touches, lightly inking a ring; then the small burst
        static void orbit(Item it, float dt)
        {
            var s = it.sub; var o = it.owner;
            it.orbitA += s.orbitSpin * dt;
            float h = o.form == "squid" ? 0.55f : 0.95f;
            Vector3 want = new Vector3(o.pos.x + Mathf.Cos(it.orbitA) * s.orbitRadius, o.pos.y + h + 0.08f * Mathf.Sin(it.t * 9), o.pos.z + Mathf.Sin(it.orbitA) * s.orbitRadius);
            float k = clamp(it.t / 0.18f, 0, 1);
            it.pos = Vector3.Lerp(it.from, want, k * k * (3 - 2 * k));
            it.vel = new Vector3(-Mathf.Sin(it.orbitA), 0, Mathf.Cos(it.orbitA)) * (s.orbitSpin * s.orbitRadius);
            it.spinW = lerp(it.spinW, 30, 1 - Mathf.Exp(-4 * dt));
            foreach (var e in G.actors)
            {
                if (e.team == it.team || !e.alive) continue;
                if (Physics.pointCapsuleDist(it.pos, e.pos, PLAYER.radius, bodyH(e)) > PLAYER.radius + 0.4f) continue;
                if (G.time - (it.hitCd.TryGetValue(e, out var last) ? last : -9) < s.orbitHitCd) continue;
                it.hitCd[e] = G.time;
                G.projectiles.applyHit(it.owner, e, s.orbitDamage, KIND);
                if (nearCam(it.pos)) G.audio?.play("boomerang_shred", new AudioOpts { pos = it.pos, volume = 0.55f, pitch = 0.85f });
            }
            it.paintT -= dt;
            if (it.paintT <= 0)
            {
                it.paintT = 0.1f;
                var g = G.physics.raycast(it.pos, DOWN, 2.5f, _hit2);
                if (g.hit) credit(it, G.paint.splat(new Vector3(g.point.x, g.point.y + 0.1f, g.point.z), 0.55f, it.team, new SplatOpts { seed = random() }));
            }
            if (it.t >= s.orbit) burst(it);
        }

        // ---- a foe in its path: stop dead, whirr and blink in place, then a full Splat Bomb
        static bool contact(Item it)
        {
            var s = it.sub;
            foreach (var e in G.actors)
            {
                if (e.team == it.team || !e.alive || e.invuln > 0) continue;
                var (d, t) = Physics.segmentCapsuleDist(it.prev, it.pos, e.pos, PLAYER.radius, bodyH(e));
                if (d > PLAYER.radius + s.contact) continue;
                it.pos = Vector3.Lerp(it.prev, it.pos, t);
                G.projectiles.applyHit(it.owner, e, s.tickDamage, KIND);
                arm(it, e);
                return true;
            }
            return false;
        }
        // caught a foe: stops dead, whirrs and blinks in place, then the full blast
        static void arm(Item it, Actor e)
        {
            var s = it.sub;
            it.state = "armed"; it.t = 0; it.tickT = 1 / s.tickRate; it.paintT = 0; it.beepT = 0; it.victim = e;
            it.vel = Vector3.zero;
            if (nearCam(it.pos, 40)) { G.fx?.burst(it.pos, UP, color(it), new FxOpts { count = 10, speed = 3.5f, size = 0.08f }); G.audio?.play("bomb_beep", new AudioOpts { pos = it.pos, volume = 0.8f, pitch = 1.2f }); }
            setLoop(it, "boomerang_whirr", 0.85f, 1.6f);
            emit("sub:arm", new Ev { kind = KIND, pos = it.pos, team = it.team, radius = s.hitRadius });
        }
        static void armed(Item it, float dt)
        {
            var s = it.sub; float k = clamp(it.t / s.hitFuse, 0, 1);
            it.spinW = lerp(it.spinW, 44, 1 - Mathf.Exp(-6 * dt));
            shred(it, dt, 0);
            it.beepT -= dt;
            if (it.beepT <= 0) { it.beepT = lerp(0.2f, 0.08f, k); if (nearCam(it.pos, 40)) G.audio?.play("bomb_beep", new AudioOpts { pos = it.pos, volume = 0.5f + 0.4f * k, pitch = 1.1f + 0.4f * k }); }
            if (it.t >= s.hitFuse) blast(it, s.hitRadius, s.hitDamageMax, s.hitDamageMin, s.hitPaintRadius, true);
        }

        // ---- the thrower was splatted: drop out of the air and pop (ink, no damage)
        static void fizzle(Item it, float dt)
        {
            it.vel.y -= 20 * dt; it.vel.x *= 1 - 2 * dt; it.vel.z *= 1 - 2 * dt;
            it.pos += it.vel * dt;
            it.spinW = Mathf.Max(0, it.spinW - 50 * dt);
            var g = G.physics.segment(it.prev, it.pos, _hit2);
            if (g.hit || it.t > 0.7f || it.pos.y < PLAYER.waterY - 1)
            {
                Vector3 c = g.hit ? g.point : it.pos;
                if (it.pos.y > PLAYER.waterY) credit(it, G.paint.splat(new Vector3(c.x, c.y + 0.15f, c.z), 0.8f, it.team, new SplatOpts { seed = random() }));
                if (nearCam(c, 40)) { G.fx?.burst(c, g.hit ? g.normal : UP, color(it), new FxOpts { count = 12, speed = 3, size = 0.08f }); G.audio?.play("splat_small", new AudioOpts { pos = c, volume = 0.8f, pitch = 0.9f }); }
                it.state = "dead";
            }
        }

        // ---- bursts
        static void burst(Item it) { var s = it.sub; blast(it, s.radius, s.damageMax, s.damageMin, s.paintRadius, false); }
        static void blast(Item it, float radius, float dmgMax, float dmgMin, float paintR, bool big)
        {
            Vector3 c = it.pos;
            it.blasted = true;
            // ink: on the floor under it (the burst is in the air) plus around the burst itself
            var g = G.physics.raycast(c, DOWN, 3.2f, _hit2);
            Vector3 pc = g.hit ? new Vector3(g.point.x, g.point.y + 0.2f, g.point.z) : c;
            float area = G.paint.splat(pc, paintR, it.team, new SplatOpts { seed = random() });
            for (int k = 0; k < (big ? 5 : 4); k++)
            {
                float a = random() * Mathf.PI * 2, rr = paintR * (0.55f + random() * 0.45f);
                area += G.paint.splat(new Vector3(pc.x + Mathf.Cos(a) * rr, pc.y + 0.4f, pc.z + Mathf.Sin(a) * rr), 0.5f + random() * 0.4f, it.team, new SplatOpts { seed = random() });
            }
            if (!g.hit || c.y - g.point.y > 1.2f) area += G.paint.splat(c, paintR * 0.6f, it.team, new SplatOpts { seed = random() });
            credit(it, area);
            G.fx?.explosion(c, color(it), big ? radius : radius * 0.85f);
            G.audio?.play(big ? "bomb_explode" : "boomerang_burst", new AudioOpts { pos = c });
            emit("shake", new Ev { pos = c, amount = big ? 0.6f : 0.4f });
            emit("bomb:explode", new Ev { actor = it.owner, pos = c, team = it.team, radius = radius });
            foreach (var e in G.actors)
            {
                if (e.team == it.team || !e.alive) continue;
                Vector3 ec = chest(e); float d = Vector3.Distance(ec, c);
                if (d > radius || !G.physics.los(c, ec)) continue;
                float k = 1 - clamp((d - 0.8f) / (radius - 0.8f), 0, 1);
                G.projectiles.applyHit(it.owner, e, lerp(dmgMin, dmgMax, k * k), KIND);
            }
            G.subs.damageArea(c, radius, big ? 60 : 35, it.team);
            it.state = "dead";
        }

        // ---- a light ink streak on the floor under its flight (thinner on the way back)
        static void streak(Item it, float r)
        {
            it.trail += Vector3.Distance(it.pos, it.prev);
            if (it.trail < 0.8f) return;
            it.trail = 0;
            var g = G.physics.raycast(it.pos, DOWN, 2.6f, _hit2);
            if (g.hit) credit(it, G.paint.splat(new Vector3(g.point.x, g.point.y + 0.1f, g.point.z), r * (0.85f + random() * 0.3f), it.team, new SplatOpts { seed = random() }));
        }

        // ================================================================================================= visuals
        static void draw(Item it, float dt)
        {
            Color col = color(it);
            it.outer.localPosition = it.pos;
            it.spinA = (it.spinA + it.spinW * dt) % (Mathf.PI * 2);
            it.spin.localRotation = Quaternion.AngleAxis(it.spinA * Mathf.Rad2Deg, UP);
            // bank: tilted into the throw while flying, wobbling level while it hovers, tipped in toward you as it circles
            bool moving = it.state == "out" || it.state == "back" || it.state == "fizzle";
            float bx = 0, bz = 0;
            if (moving && it.vel.sqrMagnitude > 0.01f) { it.ty = Mathf.Atan2(it.vel.x, it.vel.z); bz = it.state == "back" ? -0.35f : 0.28f; bx = -0.08f; }
            else if (it.state == "orbit") { it.ty = -it.orbitA; bz = 0.3f; }
            else { bx = 0.07f * Mathf.Sin(it.age * 7.3f); bz = 0.07f * Mathf.Cos(it.age * 5.9f); }
            it.tx = lerp(it.tx, bx, 1 - Mathf.Exp(-10 * dt));
            it.tz = lerp(it.tz, bz, 1 - Mathf.Exp(-10 * dt));
            it.tilt.localRotation = ThreeSpace.eulerXYZ(it.tx, it.ty, it.tz);
            // spin blur: a smeared disc that fades in with the spin rate (armed: pulses hot before the burst)
            float bl = clamp((it.spinW - 8) / 22, 0, 1);
            it.blur.enabled = bl > 0.02f;
            float alpha = 0.72f * bl; Color bc = col.linear;
            it.blurAng -= dt * it.spinW * 0.12f;
            if (it.state == "armed") { float k = clamp(it.t / it.sub.hitFuse, 0, 1); alpha = 0.55f + 0.35f * Mathf.Abs(Mathf.Sin(it.t * (14 + 26 * k))); bc *= 1 + 1.4f * k; it.outer.localScale = Vector3.one * (1 + 0.18f * k); }
            it.blurMat.SetFloat(ID_Alpha, alpha); it.blurMat.SetFloat(ID_Ang, it.blurAng);
            it.blurMat.SetVector(ID_Color, new Vector4(bc.r, bc.g, bc.b, 1));
            // loop follows it
            it.loop?.set(null, null, it.pos);
            if (G.fx == null || !nearCam(it.pos, 40)) return;
            // flying: a light mist + drips; hovering / armed: ink flung off the blades, and a ring on the floor for its reach
            if (moving) G.fx.bombTrail(it.pos, it.vel, col);
            if (it.state == "hover" || it.state == "armed" || it.state == "orbit")
            {
                it.fxT -= dt;
                if (it.fxT <= 0)
                {
                    it.fxT = it.state == "orbit" ? 0.09f : 0.045f;
                    float a = random() * Mathf.PI * 2, r = proto().radius * 0.92f, sp = 4 + random() * 3;
                    G.fx.drop(new Vector3(it.pos.x + Mathf.Cos(a) * r, it.pos.y, it.pos.z + Mathf.Sin(a) * r), new Vector3(-Mathf.Sin(a) * sp, 0.6f + random(), Mathf.Cos(a) * sp), col, new FxOpts { size = 0.035f + random() * 0.025f, life = 0.5f, quiet = true });
                }
            }
            if (it.state == "hover" || it.state == "armed")
            {
                var g = G.physics.raycast(it.pos, DOWN, 3.4f, _hit2);
                if (g.hit)
                {
                    float R = it.state == "armed" ? it.sub.hitRadius : it.sub.hoverRadius;
                    float k = it.state == "armed" ? clamp(it.t / it.sub.hitFuse, 0, 1) : 0.3f + 0.2f * Mathf.Sin(it.age * 10);
                    G.fx.dangerRing(g.point + g.normal * 0.03f, g.normal, col, R, k);
                }
            }
        }

        static void dispose(Item it)
        {
            it.loop?.stop(0.1f); it.loop = null;
            destroy(it.outer.gameObject); destroy(it.blurMat);
        }
        public override void clear()
        {
            foreach (var it in items) dispose(it);
            items.Clear();
            if (gLine != null) { gLine.enabled = false; gRing.gameObject.SetActive(false); gDot.gameObject.SetActive(false); }
        }

        // ================================================================================================= aim guide
        // While the local player holds the sub: a line along the flat throw to where it will hover, a drop line to the floor
        // and a ring there the size of its shredding reach (grey when it can't be thrown).
        static LineRenderer gLine; static Transform gRing, gDot; static Material gMat; static float gStamp = -1;
        static void guideMesh()
        {
            if (gLine != null) return;
            gMat = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { color = Color.white, renderQueue = 4000 }; gMat.SetFloat("_Cull", 0);
            gLine = new GameObject("boomerangGuide").AddComponent<LineRenderer>();
            gLine.useWorldSpace = true; gLine.positionCount = 3; gLine.widthMultiplier = 0.035f; gLine.material = gMat;
            gLine.shadowCastingMode = ShadowCastingMode.Off; gLine.enabled = false;
            var s = Config.SUBS[KIND];
            var ring = ThreeSpace.create("boomerangGuideRing");
            ring.AddComponent<MeshFilter>().sharedMesh = Kits.ringMesh(s.hoverRadius - 0.1f, s.hoverRadius, 48);
            var rr = ring.AddComponent<MeshRenderer>(); rr.sharedMaterial = gMat; rr.shadowCastingMode = ShadowCastingMode.Off;
            gRing = ring.transform; ring.SetActive(false);
            var dot = new GameObject("boomerangGuideDot");
            dot.AddComponent<MeshFilter>().sharedMesh = Kits.ringMesh(0.16f, 0.24f, 24);
            var dr = dot.AddComponent<MeshRenderer>(); dr.sharedMaterial = gMat; dr.shadowCastingMode = ShadowCastingMode.Off;
            gDot = dot.transform; dot.SetActive(false);
        }
        public override void hold(WeaponRunner runner, float dt, WeaponInput inp, SubDef sub)
        {
            var a = runner.a;
            if (!a.isLocal || !a.alive) return;
            guideMesh();
            var P = plan(a, sub, _plan);
            bool hasG = P.ground > float.NegativeInfinity && P.end.y - P.ground < 6;
            gLine.SetPosition(0, ThreeSpace.toUnity(P.start)); gLine.SetPosition(1, ThreeSpace.toUnity(P.end));
            gLine.SetPosition(2, ThreeSpace.toUnity(new Vector3(P.end.x, hasG ? P.ground : P.end.y, P.end.z)));
            gRing.gameObject.SetActive(hasG);
            if (hasG)
            {
                gRing.localPosition = new Vector3(P.end.x, P.ground + 0.04f, P.end.z);
                gRing.localRotation = Quaternion.AngleAxis(-90, Vector3.right);   // (RingGeometry.rotateX(-π/2): flat on the floor)
                gRing.localScale = Vector3.one * (1 + Mathf.Sin(G.time * 8) * 0.04f);
            }
            gDot.position = ThreeSpace.toUnity(P.end);
            var cam = Camera.main; if (cam != null) gDot.rotation = cam.transform.rotation;
            gDot.gameObject.SetActive(true);
            bool ok = a.ink >= sub.inkCost && !active(a);
            Color col = ok ? a.color * 1.4f : new Color(0.6f, 0.6f, 0.6f);
            gMat.color = new Color(col.r, col.g, col.b, 0.95f);
            gLine.enabled = true;
            gStamp = G.time;
        }
        static void guideTick()
        {
            if (gLine != null && gLine.enabled && gStamp < G.time - 1e-6f) { gLine.enabled = false; gRing.gameObject.SetActive(false); gDot.gameObject.SetActive(false); }
        }

        // ================================================================================================= bots
        // mid range: the hover catches them where they stand; point blank while under fire: a direct hit bursts on them
        static bool botFight(BotBrain brain, float dist)
        {
            var a = brain.actor; var tg = brain.target;
            if (active(a) || tg == null || !(brain.seeTimer > 0)) return false;
            // only with the aim on them: the flat throw goes where the bot looks (a direct hit bursts on them)
            float off = Mathf.Abs(angleDiff(a.aimYaw, Mathf.Atan2(tg.pos.x - a.pos.x, tg.pos.z - a.pos.z)));
            if (off > Mathf.Min(0.35f, 0.9f / Mathf.Max(dist, 1))) return false;
            bool close = dist < 3.2f && a.lastDamage < 0.8f && random() < 0.08f;
            bool mid = dist > 4 && dist < 10.5f && random() < 0.035f * (1 + (brain.diff?.fireDiscipline ?? 0.8f));
            if (!close && !mid) return false;
            brain.bombCd = 6 + random() * 5;
            return true;
        }
        // painting: now and then onto a patch ahead that isn't ours yet (the hover inks a disc, the orbit a ring round you)
        static bool botPaint(BotBrain brain)
        {
            var a = brain.actor;
            if (active(a) || random() > 0.006f) return false;
            var st = G.paint.regionStats(a.pos.x + Mathf.Sin(a.aimYaw) * 8, a.pos.y, a.pos.z + Mathf.Cos(a.aimYaw) * 8, 2.5f, a.team, _stats);
            if (st.n == 0 || st.own > 0.45f) return false;
            brain.bombCd = 9 + random() * 7;
            return true;
        }
    }
}
