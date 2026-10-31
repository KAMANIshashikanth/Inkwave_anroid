// Port of src/game/kits/shaker.js — Shaker Bomb (kit sub 'shaker'), see Kits.cs.
//
// Hold the sub button to shake it up: the charge climbs from one blast to two to three, with a clink at each new charge
// and a louder, brighter clink on the third. Plain holding takes SUBS.shaker.chargeTime (≈2.7 s) for all three; mashing
// jump, running and waggling the stick / swiping the look shake it faster. Released, it is thrown like any bomb and
// sprays a thin ink trail as it flies. After it lands it pops once per charge, and each pop hops it forward. If you are
// splatted while holding it, it fizzles out in your hand: no ink is spent and nothing is thrown.
//
// Per-actor charge state lives on the weapon runner as `runner.subKit` (a ShakerState; runner.kit belongs to kit main
// weapons). Events: 'sub:charge' { actor, kind: 'shaker', level, final (max) } at each new charge; 'sub:use' { actor,
// kind, level }; 'bomb:arm' / 'bomb:explode' { actor, pos, team, radius, kind: 'shaker', n } per blast.
// The model is baked from shaker.js's own builder (Tools/bake/weapons.mjs: sub_shaker + its lamp dome). The HUD pips are
// HudLite's (the sub meter). Not ported: the online ghosts, the Boss Battle splash (M7), the sounds (M5: by name).
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Rendering;
using static Inkwave.Ctx;

namespace Inkwave
{
    /** runner.subKit while a Shaker is being shaken up */
    public class ShakerState
    {
        public float meter, t0, t1, budget = 0.1f, kick, fizz, yawPrev, lastWag = -9, botMax;
        public int level = 1, jumps, shakes, lookSign, botLevel = -1;
        public readonly int[] axis = new int[2];
        public bool jumpPrev;
        public AudioLoop loop;
    }

    public class Shaker : SubKit
    {
        static SubDef S => Config.SUBS["shaker"];
        static readonly Vector3 UP = new Vector3(0, 1, 0), DOWN = new Vector3(0, -1, 0);
        static readonly Hit _hit = new Hit(), _hit2 = new Hit();
        const float GRAV = 24;
        const float SCALE = 1.9f;                // hand-scale model → world (the other sub props read at this size too)
        const float CY = 0.107f;                 // model-space height of the can's middle (the thrown can tumbles about it)
        const float RAD = 0.19f;                 // world contact radius (the centre rests this far off a surface)
        static readonly float[] LAMP_Y = { 0.0665f, 0.081f, 0.0955f }; const float LAMP_R = 0.0547f;
        static readonly Vector3 GRIP = new Vector3(0, 0.1915f, 0);   // the knob the left fist holds (prop space)
        // fizz vent tip + direction (prop space: the builder's NOZ)
        static readonly Vector3 NOZ_N = new Vector3(Mathf.Sin(0.95f) * 0.72f, 0.7f, Mathf.Cos(0.95f) * 0.72f).normalized;
        static readonly Vector3 NOZ_P = new Vector3(Mathf.Sin(0.95f) * 0.029f, 0.1648f, Mathf.Cos(0.95f) * 0.029f) + NOZ_N * 0.019f;
        static readonly Color LAMP_BASE = new Color(0x14 / 255f, 0x14 / 255f, 0x18 / 255f);

        public Shaker() { bot = new SubKitBot { fight = botFight, paint = botPaint }; on("splatted", e => { var v = e.victim as Actor; if (v?.weaponRunner?.subKit is ShakerState st) drop(v, G.time - st.t1 < 0.25f); }); }

        static Mesh _lampGeo;
        static Mesh lampGeo => _lampGeo ??= WeaponModel.loadMesh("sub_shaker/lamp");
        static Material lampMat(Color team)
        {
            var m = CharacterMats.lampMaterial(LAMP_BASE, 0.3f);
            CharacterMats.setLamp(m, team.linear, 0.05f);
            return m;
        }
        static bool near(Vector3 p, float r) => (G.camPos - p).sqrMagnitude < r * r;
        static bool isBot(Actor a) => a.bot != null;   // bot brains drive the intent (a human local player has none; the autopilot does)

        // ================================================================================================= charging (hold)
        static ShakerState newState(Actor a) => new ShakerState { t0 = G.time, t1 = G.time, jumpPrev = a.intent.jump, yawPrev = a.aimYaw };

        // Waggles this frame, for the local player: fast reversals of the aim's turn direction (mouse / right stick / a
        // look swipe) and the left stick thrown from one side to the other.
        static int waggles(ShakerState st, Actor a, float dt)
        {
            int n = 0;
            float dy = angleDiff(st.yawPrev, a.aimYaw); st.yawPrev = a.aimYaw;
            if (dt > 0 && Mathf.Abs(dy) / dt > 3)
            {
                int s = dy > 0 ? 1 : -1;
                if (s != st.lookSign) { if (st.lookSign != 0 && G.time - st.lastWag > 0.05f) { n++; st.lastWag = G.time; } st.lookSign = s; }
            }
            var inp = G.input;
            if (inp?.pad != null && inp.lastDevice == "pad")
            {
                for (int i = 0; i < 2; i++)
                {
                    float x = inp.padRaw(i); int s = x > 0.6f ? 1 : x < -0.6f ? -1 : 0;
                    if (s != 0 && s != st.axis[i]) { if (st.axis[i] != 0 && G.time - st.lastWag > 0.04f) { n++; st.lastWag = G.time; } st.axis[i] = s; }
                }
            }
            return n;
        }

        public override void hold(WeaponRunner runner, float dt, WeaponInput inp, SubDef sub)
        {
            var a = runner.a;
            var st = runner.subKit as ShakerState;
            if (st != null && G.time - st.t1 > 0.15f) { drop(a, false); st = null; }   // a stale charge (the hold was interrupted)
            if (st == null) runner.subKit = st = newState(a);
            st.t1 = G.time;
            bool paid = (a.specialActive != null && a.specialActive.kind == "barrage") || a.ink >= sub.inkCost;
            bool jump = a.intent.jump, jp = jump && !st.jumpPrev; st.jumpPrev = jump;
            int wag = a.isLocal && !isBot(a) ? waggles(st, a, dt) : 0;
            if (paid && st.meter < 1)
            {
                float move = clamp((Mathf.Sqrt(a.vel.x * a.vel.x + a.vel.z * a.vel.z) - 1) / (PLAYER.runSpeed - 1), 0, 1);
                float add = (dt / sub.chargeTime) * (1 + sub.moveBoost * move);
                if (jp) { add += sub.jumpBoost; st.jumps++; }
                if (wag > 0) { add += wag * sub.shakeBoost; st.shakes += wag; }
                st.budget = Mathf.Min(0.3f, st.budget + sub.maxRate * dt);   // bursts of presses can't beat the cap
                add = Mathf.Min(add, st.budget); st.budget -= add;
                st.meter = Mathf.Min(1, st.meter + add);
            }
            int lv = st.meter >= 1 ? 3 : st.meter >= 0.5f ? 2 : 1;
            if (lv > st.level)
            {
                st.level = lv; st.kick = 1;
                clink(a, lv);
                emit("sub:charge", new Ev { actor = a, kind = "shaker", level = lv, final = lv == 3 });
            }
            st.kick = Mathf.Max(0, st.kick - dt * 4);
            if (isBot(a)) botHold(a, st, inp);
            handFx(a, st, dt);
        }

        static void clink(Actor a, int lv)
        {
            bool nearA = a.isLocal || a.nearCamera();
            if (!nearA) return;
            G.audio?.play(lv >= 3 ? "shaker_clink_max" : "shaker_clink", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = a.isLocal ? 1 : 0.75f, pitch = lv >= 3 ? 1 : 0.94f });
            var hp = handProp(a);
            if (hp != null)
            {
                Vector3 p = nozzleWorld(hp);
                if (lv >= 3) G.fx?.chargeFull(p, a.color);
                else G.fx?.burst(p, hp.nDir, a.color, new FxOpts { count = 5, speed = 2.2f, size = 0.035f, sheet = false });
            }
            Actor.rumble(a, lv >= 3 ? 0.3f : 0.12f, lv >= 3 ? 0.5f : 0.25f, lv >= 3 ? 120 : 60);
        }

        // Bots can't mash: they press once, then hold here until the charge they planned (by the fight's range), or ~3 s.
        static void botHold(Actor a, ShakerState st, WeaponInput inp)
        {
            var b = a.bot;
            if (st.botLevel < 0)
            {
                var t = b.target; float d = t != null ? Mathf.Sqrt((t.pos.x - a.pos.x) * (t.pos.x - a.pos.x) + (t.pos.z - a.pos.z) * (t.pos.z - a.pos.z)) : 8;
                if (b.mode == "fight") st.botLevel = d > 10 ? 3 : d > 6.5f ? (random() < 0.6f ? 3 : 2) : (random() < 0.55f ? 2 : 1);
                else st.botLevel = random() < 0.5f ? 3 : 2;   // painting: a long ink line
                st.botMax = b.mode == "fight" ? 3.2f : 2.2f;
            }
            bool keep = a.alive && a.form == "kid" && st.level < st.botLevel && G.time - st.t0 < st.botMax;
            if (keep) { inp.sub = true; inp.subReleased = false; }
            else if (!inp.sub) inp.subReleased = true;       // the brain let go frames ago: throw now
        }

        // ---- the can in the fist: rattles harder as it charges (jolts on each clink), its lamps count the charges, the vent fizzes
        class HandProp { public Transform group; public Material[] mats; public Vector3 pb; public Quaternion qb; public Vector3 nDir; }
        static readonly ConditionalWeakTable<WeaponModel.SubInstance, HandProp> _hand = new ConditionalWeakTable<WeaponModel.SubInstance, HandProp>();
        static HandProp handProp(Actor a)
        {
            var b = (a.character as Character)?.bomb;
            if (b == null || b.kind != "shaker") return null;
            if (_hand.TryGetValue(b, out var hp)) return hp;
            var g = b.group;
            hp = new HandProp { group = g, mats = new Material[3], pb = g.localPosition, qb = g.localRotation };
            for (int i = 0; i < 3; i++)
            {
                hp.mats[i] = lampMat(a.color);
                var m = ThreeSpace.create("lamp" + i, g);
                m.transform.localPosition = new Vector3(0, LAMP_Y[i], LAMP_R);
                m.AddComponent<MeshFilter>().sharedMesh = lampGeo;
                var r = m.AddComponent<MeshRenderer>(); r.sharedMaterial = hp.mats[i]; r.shadowCastingMode = ShadowCastingMode.Off;
            }
            _hand.Add(b, hp);
            return hp;
        }
        static Vector3 nozzleWorld(HandProp hp)
        {
            hp.nDir = ThreeSpace.toThree(hp.group.TransformVector(NOZ_N)).normalized;
            return ThreeSpace.toThree(hp.group.TransformPoint(NOZ_P));
        }
        static void restoreHand(Actor a)
        {
            var b = (a.character as Character)?.bomb;
            if (b == null || !_hand.TryGetValue(b, out var hp)) return;
            hp.group.localPosition = hp.pb; hp.group.localRotation = hp.qb;
            foreach (var m in hp.mats) CharacterMats.setLampIntensity(m, 0.05f);
        }
        static void handFx(Actor a, ShakerState st, float dt)
        {
            var hp = handProp(a);
            if (hp == null) return;
            bool nearA = a.isLocal || a.nearCamera();
            float f = st.meter; bool max = st.level >= 3;
            // rattle about the grip: random per-frame jolts (a vibration, not a wobble)
            float amp = (0.035f + 0.09f * f + 0.22f * st.kick) * (nearA ? 1 : 0.5f);
            var q = ThreeSpace.eulerXYZ((random() * 2 - 1) * amp, (random() * 2 - 1) * amp * 0.5f, (random() * 2 - 1) * amp);
            hp.group.localRotation = hp.qb * q;
            hp.group.localPosition = hp.pb + hp.qb * (GRIP - q * GRIP);   // qb·(pivot − R·pivot)
            // lamps: lit per charge; the next one glows with the progress toward it; at max they all throb
            float part = st.level < 3 ? clamp(f * 2 - (st.level - 1), 0, 1) : 1;
            Color lin = a.color.linear;
            for (int i = 0; i < 3; i++)
            {
                float k = i < st.level ? (max ? 2.4f + 1.2f * Mathf.Sin(G.time * 18) : 2.2f) + 2.5f * st.kick
                    : i == st.level ? 0.12f + 0.9f * part * (0.6f + 0.4f * Mathf.Sin(G.time * 22)) : 0.05f;
                CharacterMats.setLamp(hp.mats[i], lin, k);
            }
            // fizz out of the vent (more with the charge, a steady spray at max) + the fizz loop for the local player
            if (nearA && G.fx != null)
            {
                st.fizz += dt * (4 + 26 * f + (max ? 22 : 0));
                if (st.fizz >= 1)
                {
                    Vector3 p = nozzleWorld(hp), n = hp.nDir;
                    Color col = Color.Lerp(a.color, Color.white, 0.3f);
                    Vector3 v = Vector3.zero;
                    while (st.fizz >= 1)
                    {
                        st.fizz -= 1;
                        // fine spray out of the vent, thrown upward in the world (the can rides upside down in the fist)
                        float sp = 1.6f + random() * (1.5f + 2 * f);
                        v = new Vector3(n.x * 0.7f + (random() - 0.5f) * 0.8f, 1.3f + random() * 0.5f, n.z * 0.7f + (random() - 0.5f) * 0.8f).normalized * sp;
                        G.fx.drop(p, v, col, new FxOpts { size = 0.004f + random() * 0.006f, life = 0.5f, gravity = 1, quiet = true });
                    }
                    if (f > 0.3f && random() < dt * (3 + 8 * f)) G.fx.mist(p, new Vector3(n.x * 0.5f, 0.9f, n.z * 0.5f), col, 0.04f + 0.04f * f, 0.2f);
                }
            }
            if (a.isLocal && G.audio != null)
            {
                st.loop ??= G.audio.loop("shaker_fizz", new AudioOpts { volume = 0.3f, pitch = 0.85f });
                st.loop?.set(0.3f + 0.8f * f + (max ? 0.25f : 0), 0.85f + 0.55f * f + (max ? 0.15f * Mathf.Sin(G.time * 9) : 0));
            }
        }

        // a charge that's dropped (splatted, dived into ink, loadout swap …): no throw, no ink; the fizz stops
        static void drop(Actor a, bool fizzle)
        {
            var r = a.weaponRunner; var st = r?.subKit as ShakerState;
            if (st == null) return;
            r.subKit = null;
            st.loop?.stop(0.1f);
            if (fizzle && (a.isLocal || a.nearCamera()))
            {
                var hp = handProp(a);
                Vector3 p = hp != null ? nozzleWorld(hp) : new Vector3(a.pos.x, a.pos.y + 1.2f, a.pos.z);
                G.fx?.burst(p, UP, a.color, new FxOpts { count = 7, speed = 1.6f, size = 0.04f, sheet = false });
                G.fx?.mist(p, new Vector3(0, 0.8f, 0), Color.Lerp(a.color, Color.white, 0.4f), 0.12f, 0.3f);
                G.audio?.play("shaker_fizzle", new AudioOpts { pos = a.isLocal ? (Vector3?)null : p, volume = a.isLocal ? 0.8f : 0.6f });
            }
            restoreHand(a);
        }

        // ================================================================================================= thrown can
        // Bots lob it at their foe: the aim pitch that lands a throw (throwVelocity: pitch + 0.28 rad, +1.5 m/s up, released
        // 1.35 m up, gravity 24) d m out and dy above the thrower's feet; null if it can't reach (as bots.js lobPitch).
        public static float? lobPitch(float d, float dy, float speed)
        {
            float? best = null; float be = float.PositiveInfinity;
            for (float p = -0.5f; p <= 0.75f; p += 0.025f)
            {
                float tp = clamp(p + 0.28f, -0.3f, 1.1f), vx = Mathf.Cos(tp) * speed, vy = Mathf.Sin(tp) * speed + 1.5f, disc = vy * vy + 48 * (1.35f - dy);
                if (disc < 0) continue;
                float e = Mathf.Abs((vx * (vy + Mathf.Sqrt(disc))) / 24 - d);
                if (e < be) { be = e; best = p; }
            }
            return be < 1.2f ? best : null;
        }
        static void botLob(Actor a, SubDef sub, float shortBy)
        {
            var b = a.bot; var t = b?.target;
            if (t == null || !t.alive || b.mode != "fight") return;
            float tx = t.pos.x + t.vel.x * 0.5f, tz = t.pos.z + t.vel.z * 0.5f;          // lead it by about the flight time
            var p = lobPitch(Mathf.Max(1.5f, Mathf.Sqrt((tx - a.pos.x) * (tx - a.pos.x) + (tz - a.pos.z) * (tz - a.pos.z)) - shortBy), t.pos.y - a.pos.y, sub.throwSpeed);
            if (p == null) return;
            a.aimYaw = Mathf.Atan2(tx - a.pos.x, tz - a.pos.z); a.aimPitch = p.Value;
        }

        class Can
        {
            public Actor owner; public int team, level, left, blasts; public SubDef sub;
            public Vector3 pos, vel, dir, spin, gp, gn = Vector3.up;
            public string state; public float age, fuse, next, trail, ground; public bool armed, sp, gOk;
            public Transform outer, inner, model; public Material[] mats;
            public float irx, iry, irz;   // the tumble (three Euler XYZ of `inner`)
        }
        static readonly List<Can> items = new List<Can>();

        public override void use(SubSystem subs, Actor a, SubDef sub)
        {
            var r = a.weaponRunner; var st = r.subKit as ShakerState;
            int level = st != null && G.time - st.t1 < 0.25f ? st.level : 1;
            if (st != null) { st.loop?.stop(0.08f); r.subKit = null; }
            restoreHand(a);
            if (isBot(a)) botLob(a, sub, 0.9f);   // its first blast (after the landing slide) on the foe
            Vector3 pos = a.pos; pos.y += 1.35f;
            Vector3 vel = G.projectiles.throwVelocity(a, sub.throwSpeed);
            spawn(a, sub, pos, vel, level);
            if (a.isLocal || a.nearCamera()) G.audio?.play("bomb_throw", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = 0.65f, pitch = 0.95f + 0.06f * level });
            emit("sub:use", new Ev { actor = a, kind = "shaker", level = level });
        }
        // the can in flight
        static void spawn(Actor a, SubDef sub, Vector3 pos, Vector3 vel, int level)
        {
            Vector3 dir = new Vector3(vel.x, 0, vel.z);
            if (dir.sqrMagnitude < 1e-6f) dir = new Vector3(Mathf.Sin(a.aimYaw), 0, Mathf.Cos(a.aimYaw));
            dir.Normalize();
            var it = new Can
            {
                owner = a, team = a.team, sub = sub, level = level, left = level, pos = pos, vel = vel, dir = dir, state = "fly", fuse = -1,
                sp = a.specialActive != null, spin = new Vector3(4 + random() * 5, 0, 3 + random() * 5), irx = random() * 6, iry = random() * 6,
            };
            makeMesh(it);
            it.outer.localPosition = pos;
            it.inner.localRotation = ThreeSpace.eulerXYZ(it.irx, it.iry, it.irz);
            items.Add(it);
            lamps(it);
        }
        // the thrown can: outer (world position = the can's middle) → inner (tumble) → model (scaled prop, lifted by CY)
        static void makeMesh(Can it)
        {
            var d = WeaponModel.getSubDef("shaker"); Color col = G.teamColors[it.team];
            it.outer = ThreeSpace.create("shaker").transform;
            it.inner = ThreeSpace.create("inner", it.outer).transform;
            it.model = ThreeSpace.create("model", it.inner).transform;
            it.model.localScale = Vector3.one * SCALE; it.model.localPosition = new Vector3(0, -CY * SCALE, 0);
            part("body", it.model, d.body, CharacterMats.plastic, true);
            part("ink", it.model, d.ink, CharacterMats.ink(col), true);
            it.mats = new Material[3];
            for (int i = 0; i < 3; i++)
            {
                it.mats[i] = lampMat(col);
                part("lamp" + i, it.model, lampGeo, it.mats[i], false).transform.localPosition = new Vector3(0, LAMP_Y[i], LAMP_R);
            }
        }
        static MeshRenderer part(string name, Transform parent, Mesh m, Material mat, bool shadow)
        {
            var g = ThreeSpace.create(name, parent);
            g.AddComponent<MeshFilter>().sharedMesh = m;
            var r = g.AddComponent<MeshRenderer>(); r.sharedMaterial = mat;
            r.shadowCastingMode = shadow ? ShadowCastingMode.On : ShadowCastingMode.Off;
            return r;
        }

        static void lamps(Can it)
        {
            Color col = G.teamColors[it.team].linear;
            for (int i = 0; i < 3; i++) CharacterMats.setLamp(it.mats[i], col, i < it.left ? 2.3f : 0.05f);
        }

        // three's Euler (order XYZ) of a rotation, as Object3D.rotation reads back after a quaternion write
        static Vector3 eulerXYZOf(Quaternion q)
        {
            var m = Matrix4x4.Rotate(q);
            float y = Mathf.Asin(clamp(m.m02, -1, 1));
            return Mathf.Abs(m.m02) < 0.9999999f ? new Vector3(Mathf.Atan2(-m.m12, m.m22), y, Mathf.Atan2(-m.m01, m.m00)) : new Vector3(Mathf.Atan2(m.m21, m.m11), y, 0);
        }

        static void update(Can it, float dt)
        {
            var s = it.sub;
            it.age += dt;
            // ---- motion: gravity, swept centre vs the world, then a resting contact (the centre kept RAD off the floor)
            it.vel.y -= GRAV * dt;
            Vector3 prev = it.pos;
            it.pos += it.vel * dt;
            bool floor = false;
            var h = G.physics.segment(prev, it.pos, _hit);
            if (h.hit)
            {
                Vector3 n = h.normal;
                it.pos = h.point + n * RAD;
                if (n.y > 0.6f) floor = contactFloor(it, n, dt);
                else
                {
                    float vn = Vector3.Dot(it.vel, n);
                    if (vn < 0) it.vel = (it.vel + n * (-vn * 1.5f)) * 0.62f;
                    // hops carry on away from the wall it bounced off
                    float dn = it.dir.x * n.x + it.dir.z * n.z;
                    if (dn < 0 && Mathf.Abs(n.y) < 0.6f) { it.dir.x -= 2 * dn * n.x; it.dir.z -= 2 * dn * n.z; it.dir.y = 0; it.dir.Normalize(); }
                }
            }
            if (!floor)
            {
                var g = G.physics.raycast(it.pos, DOWN, RAD, _hit2);
                if (g.hit && g.normal.y > 0.6f) { it.pos.y += RAD - g.dist; floor = contactFloor(it, g.normal, dt); }
            }
            it.ground = floor ? 0.12f : Mathf.Max(0, it.ground - dt);
            // ---- thin trail: droplets shaken from the vent while it's travelling through the air
            float hs = Mathf.Sqrt(it.vel.x * it.vel.x + it.vel.z * it.vel.z);
            if (it.ground <= 0 && hs > 1.5f)
            {
                it.trail += hs * dt;
                while (it.trail >= s.trailEvery) { it.trail -= s.trailEvery; drip(it); }
            }
            // ---- fuse (first landing) → blast → hop → blast …
            if (it.fuse >= 0) { it.fuse -= dt; if (it.fuse <= 0) { it.fuse = -1; blast(it); } }
            else if (it.next > 0) { it.next -= dt; if (it.next <= 0) blast(it); }
            if (it.state == "dead") return;
            if (it.pos.y < PLAYER.waterY - 1.8f) { it.state = "dead"; return; }        // sank: no blast
            if (!it.armed && it.age > 6) arm(it);                                     // never found a floor (wedged): go anyway
            // ---- look: tumbles in the air; once armed it stands up and rattles; the danger ring shows the next blast
            it.outer.localPosition = it.pos;
            if (it.ground > 0 && it.armed)
            {
                var q = Quaternion.Slerp(it.inner.localRotation, Quaternion.identity, 1 - Mathf.Exp(-14 * dt));
                it.inner.localRotation = q;
                var e = eulerXYZOf(q); it.irx = e.x; it.iry = e.y; it.irz = e.z;
                float k = it.fuse >= 0 ? 1 - it.fuse / s.fuse : 1 - it.next / s.gap;
                float j = 0.05f + 0.1f * k;
                it.model.localRotation = ThreeSpace.eulerXYZ((random() * 2 - 1) * j, 0, (random() * 2 - 1) * j);
            }
            else
            {
                it.irx += it.spin.x * dt; it.irz += it.spin.z * dt;
                it.inner.localRotation = ThreeSpace.eulerXYZ(it.irx, it.iry, it.irz);
                it.model.localRotation = Quaternion.identity;
            }
            if (it.armed && G.fx != null && near(it.pos, 40))
            {
                if (!it.gOk || it.ground > 0) groundUnder(it);
                float k = it.fuse >= 0 ? 1 - it.fuse / s.fuse : 1 - it.next / s.gap;
                if (it.gOk) G.fx.dangerRing(it.gp + it.gn * 0.02f, it.gn, G.teamColors[it.team], s.radius, clamp(k, 0, 1));
                float lit = 2.3f + 2.5f * Mathf.Max(0, Mathf.Sin(it.age * (14 + 20 * k)));
                Color col = G.teamColors[it.team].linear;
                for (int i = 0; i < 3; i++) CharacterMats.setLamp(it.mats[i], col, i < it.left ? lit : 0.05f);
            }
        }
        static bool contactFloor(Can it, Vector3 n, float dt)
        {
            float vn = Vector3.Dot(it.vel, n);
            if (vn < -2.2f) { it.vel = (it.vel + n * (-vn)) * 0.45f + n * (-vn * 0.3f); }   // a dull bounce: it lands near where it hits
            else if (vn < 0) { it.vel += n * (-vn); float f = Mathf.Exp(-6 * dt); it.vel.x *= f; it.vel.z *= f; }   // resting: slide to a stop
            if (!it.armed) arm(it);
            return true;
        }
        static void arm(Can it)
        {
            it.armed = true; it.fuse = it.sub.fuse;
            groundUnder(it);
            if (near(it.pos, 30)) G.audio?.play("bomb_beep", new AudioOpts { pos = it.pos, volume = 0.55f, pitch = 1.25f });
            emit("bomb:arm", new Ev { actor = it.owner, pos = it.pos, team = it.team, radius = it.sub.radius, kind = "shaker" });
        }
        static void groundUnder(Can it)
        {
            var g = G.physics.raycast(new Vector3(it.pos.x, it.pos.y + 0.1f, it.pos.z), DOWN, 2.5f, _hit2, true);
            it.gOk = g.hit;
            if (g.hit) { it.gp = g.point; it.gn = g.normal; }
        }
        static void credit(Can it, float area) { if (it.sp) it.owner.addTurfNoSpecial(area); else it.owner.addTurf(area); }

        static void drip(Can it)
        {
            Vector3 p = it.pos; p.y -= 0.1f;
            G.projectiles.spawnDrop(it.owner, p, new Vector3(it.vel.x * 0.2f + (random() - 0.5f) * 0.5f, -2.2f - random(), it.vel.z * 0.2f + (random() - 0.5f) * 0.5f),
                0.045f, it.sub.trailRadius * (0.85f + random() * 0.3f), 0);
        }

        static void blast(Can it)
        {
            var s = it.sub; var a = it.owner; int team = it.team; Color col = G.teamColors[team];
            Vector3 c = it.pos;
            it.blasts++; it.left--;
            // ink: the floor it stands on + a few satellite splats
            float area = 0;
            var g = G.physics.raycast(new Vector3(c.x, c.y + 0.2f, c.z), DOWN, 3.5f, _hit);
            area += G.paint.splat(g.hit ? g.point + g.normal * 0.1f : c, s.paintRadius, team, new World.SplatOpts { seed = random() });
            for (int k = 0; k < 3; k++)
            {
                float ang = random() * Mathf.PI * 2, r = s.paintRadius * (0.55f + random() * 0.4f);
                area += G.paint.splat(new Vector3(c.x + Mathf.Cos(ang) * r, c.y + 0.3f, c.z + Mathf.Sin(ang) * r), 0.45f + random() * 0.3f, team, new World.SplatOpts { seed = random() });
            }
            credit(it, area);
            G.fx?.explosion(c, col, s.radius * 0.95f);
            G.audio?.play("bomb_explode", new AudioOpts { pos = c, volume = 0.62f, pitch = 1.1f + 0.06f * it.blasts });
            emit("shake", new Ev { pos = c, amount = 0.45f });
            emit("bomb:explode", new Ev { actor = a, pos = c, team = team, radius = s.radius, kind = "shaker", n = it.blasts });
            var loc = G.local;
            if (loc != null && loc.alive) { float d = Vector3.Distance(loc.pos, c); if (d < 12) Actor.rumble(loc, clamp(1 - d / 12, 0, 1) * 0.45f, clamp(1 - d / 12, 0, 1) * 0.4f, 120); }
            // damage: like a smaller Splat Bomb (line of sight from just above the can)
            Vector3 from = new Vector3(c.x, c.y + 0.3f, c.z);
            foreach (var e in G.actors)
            {
                if (e.team == team || !e.alive) continue;
                Vector3 ep = new Vector3(e.pos.x, e.pos.y + (e.form == "squid" ? 0.3f : 0.7f), e.pos.z);
                float d = Vector3.Distance(ep, c);
                if (d > s.radius || !G.physics.los(from, ep)) continue;
                float kk = 1 - clamp((d - 0.8f) / (s.radius - 0.8f), 0, 1);
                G.projectiles.applyHit(a, e, lerp(s.damageMin, s.damageMax, kk * kk), "shaker");
            }
            G.subs.damageArea(c, s.radius, 40, team);
            // (Boss Battle splash — M7)
            if (it.left > 0)
            {
                // the blast kicks it forward and up; the next one goes off `gap` later (usually just after it lands)
                it.vel = new Vector3(it.dir.x * s.hopSpeed, s.hopUp, it.dir.z * s.hopSpeed);
                it.next = s.gap; it.ground = 0;
                it.spin = new Vector3(8 + random() * 6, 0, (random() - 0.5f) * 6);
                if (near(c, 30)) G.audio?.play("shaker_hop", new AudioOpts { pos = c, volume = 0.6f });
                lamps(it);
            }
            else it.state = "dead";
        }

        static void dispose(Can it)
        {
            destroy(it.outer.gameObject);
            foreach (var m in it.mats) destroy(m);
        }

        // ================================================================================================= registration
        public override void tick(float dt)
        {
            for (int i = items.Count - 1; i >= 0; i--)
            {
                var it = items[i];
                update(it, dt);
                if (it.state == "dead") { dispose(it); items.RemoveAt(i); }
            }
            // a charge whose hold stopped without a throw (dived into ink, loadout swap, …): drop it quietly
            foreach (var a in G.actors) { if (a.weaponRunner?.subKit is ShakerState st && G.time - st.t1 > 0.15f) drop(a, false); }
        }
        public override void clear()
        {
            foreach (var it in items) dispose(it);
            items.Clear();
            foreach (var a in G.actors) if (a.weaponRunner?.subKit is ShakerState) drop(a, false);
        }
        /** the local player's charge for the HUD pips (null while not shaking one up) */
        public static ShakerState charging(Actor a)
        {
            var st = a?.weaponRunner?.subKit as ShakerState;
            return st != null && a.alive && a.weaponRunner.aimingSub && G.time - st.t1 < 0.15f && a.sub?.kind == "shaker" ? st : null;
        }

        // a mid-range lob at the fight (the hold then charges it to the range: see botHold)
        static bool botFight(BotBrain brain, float dist)
        {
            bool go = dist > 4 && dist < 14 && random() < 0.03f * (1 + brain.diff.fireDiscipline);
            if (go) brain.bombCd = 5 + random() * 5;
            return go;
        }
        // now and then while painting: three hops leave a long stripe of ink
        static bool botPaint(BotBrain brain)
        {
            bool go = random() < 0.0012f;
            if (go) brain.bombCd = 9 + random() * 8;
            return go;
        }
    }
}
