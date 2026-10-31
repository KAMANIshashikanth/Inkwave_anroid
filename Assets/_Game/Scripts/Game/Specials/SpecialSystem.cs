// Port of src/game/specials.js — the specials framework (everything except Tidal Slam and Ink Tempest, which live in
// Actor.cs / Weapons.cs).
//
// A running special is `actor.specialActive` (SpecialState: id, kind, def, t, dur, …). Flags on it steer the actor's own
// update:
//   body      the special owns movement this frame (G.specials.body runs instead of the normal controller)
//   speed     ground speed override (m/s); accel: acceleration override
//   noSquid   can't turn into a squid;  inkProof: enemy ink neither slows nor hurts
//   faceYaw   body yaw to hold (crab hull);  faceMove: face the velocity;  aimFace: face the aim
//   firing    > 0 → firing pose;  hang > 0 → clinging in place (zipline)
//   seat      lift the character (riding the crab)
// Hooks called by the actor / projectiles: per kind (SpecialImpl) start, end, tick, body, move, weapon, filterDamage;
// system-wide onSplat, shotHit, rayHit, areaHit. World objects (tornados, twisters, speakers, bubbles, …) live in
// `world` and update in update(dt).
//
// Kinds register in IMPL (init()). A special whose kind is not registered yet plays as Tidal Slam (resolve(id), one
// warning per id). Not ported: the online ghosts (offline only), the minimap layer (drawMap — with the full minimap),
// the HUD prompt text (with the full HUD), the Boss Battle splash (M7). Later kinds bring their own world objects,
// Return markers and the cheers.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using static Inkwave.Ctx;

namespace Inkwave
{
    public static partial class G
    {
        public static SpecialSystem specials;
    }

    /** One special kind's behaviour (specials.js IMPL[kind]); every hook is optional, like the JS object's methods. */
    public abstract class SpecialImpl
    {
        public virtual void start(SpecialSystem sys, Actor a, SpecialState s) { }
        public virtual void end(SpecialSystem sys, Actor a, SpecialState s, string reason) { }
        /** per frame for non-body specials (after the actor's own movement + weapon) */
        public virtual void tick(SpecialSystem sys, Actor a, SpecialState s, float dt) { }
        /** body-owning specials (s.body): moves the actor instead of the normal controller */
        public virtual void body(SpecialSystem sys, Actor a, SpecialState s, float dt) { }
        /** before the actor moves: steer / shape the movement input */
        public virtual void move(SpecialSystem sys, Actor a, SpecialState s, float dt) { }
        /** main-weapon replacement: true = the special consumed the trigger (the weapon runner is skipped) */
        public virtual bool weapon(SpecialSystem sys, Actor a, SpecialState s, float dt, WeaponInput inp) => false;
        /** damage to its wielder (specials.js filterDamage branches): null = no say, else the damage left (≤ 0 cancels) */
        public virtual float? filterDamage(SpecialSystem sys, Actor v, SpecialState s, float amount, Actor attacker, string source) => null;
    }

    /** A special's world object (specials.js this.world entries). update returns false when it is done. */
    public abstract class SpecialWorld
    {
        public string kind; public Actor owner; public int team; public bool dead;
        public abstract bool update(float dt);
        public virtual void dispose() { }
        /** a projectile segment prev→pos: true = absorbed */
        public virtual bool shotHit(Vector3 prev, Vector3 pos, int team, float dmg, Actor owner) => false;
        /** distance along a beam to this object when nearer than `best` (else best) … */
        public virtual float rayDist(Vector3 from, Vector3 dir, float best) => best;
        /** … and the hit, on the nearest one */
        public virtual void rayHit(int team, float dmg, Actor owner) { }
        public virtual void areaHit(Vector3 c, float radius, float dmg, int team, Actor owner) { }
    }

    public class SpecialSystem
    {
        static readonly Vector3 UP = new Vector3(0, 1, 0);

        // ============================================================================================= registry
        public static readonly Dictionary<string, SpecialImpl> IMPL = new Dictionary<string, SpecialImpl>();
        static bool _inited;
        public static void init()
        {
            if (_inited) return;
            _inited = true;
            IMPL["barrage"] = new Barrage();
            IMPL["bubbler"] = new Bubbler();
            IMPL["sonar"] = new Sonar();
        }
        /** the behaviour kind (the Bomb Barrage variants all share 'barrage') */
        public static string kindOf(string id) => id != null && Config.SPECIALS.TryGetValue(id, out var d) && d.kind != null ? d.kind : id;
        /** slam and storm live in Actor / Weapons; every other special once its kind is registered */
        public static bool ported(string id)
        {
            init();
            return id == "slam" || id == "storm" || (id != null && Config.SPECIALS.ContainsKey(id) && IMPL.ContainsKey(kindOf(id)));
        }
        static readonly HashSet<string> _warned = new HashSet<string>();
        /** the special an actor actually plays: its own, or Tidal Slam while that kind is not ported (warned once per id) */
        public static string resolve(string id)
        {
            if (id == null || !Config.SPECIALS.ContainsKey(id)) return "slam";
            if (ported(id)) return id;
            if (_warned.Add(id)) Debug.LogWarning($"[specials] '{id}' is not ported yet: it plays as Tidal Slam");
            return "slam";
        }

        // specials that transform you / take over your weapon for a while: they sound a "wearing off" cue before they end
        static readonly HashSet<string> TRANSFORMS = new HashSet<string> { "jetpack", "crab", "kraken", "stamp", "zipcaster", "zooka", "blower", "bubbler" };
        const float ENDING_CUE = 2.0f;   // seconds left when it sounds

        public static bool near(Vector3 p, float r = 32) => (G.camPos - p).sqrMagnitude < r * r;
        public static bool hearable(Actor a) => a.isLocal || a.nearCamera();
        public static void play(string name, AudioOpts o = null) => G.audio?.play(name, o);

        public readonly List<SpecialWorld> world = new List<SpecialWorld>();
        class Shield { public Transform t; public Material mat; public float hit, snd; public bool owner; }
        readonly Dictionary<Actor, Shield> shieldMeshes = new Dictionary<Actor, Shield>();
        static Mesh _sphere;
        static Mesh sphereGeo() => _sphere ??= Geo.sphere(1, 40, 28).toMesh("specials-sphere");
        static readonly int ID_Color = Shader.PropertyToID("_Color"), ID_Time = Shader.PropertyToID("_Time2"),
            ID_Alpha = Shader.PropertyToID("_Alpha"), ID_Hit = Shader.PropertyToID("_Hit");

        public void clear()
        {
            foreach (var w in world) w.dispose();
            world.Clear();
            foreach (var kv in shieldMeshes) disposeShield(kv.Value);
            shieldMeshes.Clear();
        }

        // ============================================================================================= lifecycle
        public void start(Actor a, string id)
        {
            var def = Config.SPECIALS[id];
            // `kind` picks the behaviour (the Bomb Barrage variants all share 'barrage')
            var s = new SpecialState { id = id, kind = def.kind ?? id, def = def, t = 0, dur = def.duration };
            a.specialActive = s;
            if (IMPL.TryGetValue(s.kind, out var impl)) impl.start(this, a, s);
            emit("special:start", new Ev { actor = a, id = id });
        }
        // transformation specials: a "wearing off" jingle ~2 s before the time runs out (loud for you, positional for others)
        void endCue(Actor a, SpecialState s)
        {
            if (s.endCued || !(s.dur > 0) || !TRANSFORMS.Contains(s.kind) || s.dur - s.t > ENDING_CUE) return;
            s.endCued = true;
            if (a.isLocal) play("special_ending", new AudioOpts { volume = 0.75f });
            else if (hearable(a)) play("special_ending", new AudioOpts { pos = a.pos, volume = 0.4f });
        }
        // per-frame for non-body specials (after the actor's own movement + weapon)
        public void tick(Actor a, SpecialState s, float dt)
        {
            s.t += dt;
            s.firing = Mathf.Max(0, s.firing - dt);
            endCue(a, s);
            if (IMPL.TryGetValue(s.kind, out var impl)) impl.tick(this, a, s, dt);
            if (a.specialActive == s && s.dur > 0 && s.t >= s.dur) end(a, "time");
        }
        // body-owning specials (strike aiming, wail placement, sonar cast, jetpack, zipline zips)
        public void body(Actor a, SpecialState s, float dt)
        {
            s.t += dt;
            s.firing = Mathf.Max(0, s.firing - dt);
            endCue(a, s);
            if (IMPL.TryGetValue(s.kind, out var impl)) impl.body(this, a, s, dt);
            if (a.specialActive == s && s.dur > 0 && s.t >= s.dur) end(a, "time");
            // the sea still wins
            if (a.alive && a.pos.y < PLAYER.fallDeathY && float.IsNegativeInfinity(G.level.groundHeight(a.pos.x, a.pos.z, a.pos.y + 0.6f)))
            {
                if (a.specialActive != null) end(a, "splat");
                a.splat(a.lastDamage < 4 ? a.lastAttacker : null, "water");
            }
        }
        // before the actor moves: a special may steer / shape the movement input (Mega Stamp)
        public void move(Actor a, SpecialState s, float dt) { if (IMPL.TryGetValue(s.kind, out var impl)) impl.move(this, a, s, dt); }
        // main-weapon replacement; returns true when the special consumed the trigger (the weapon runner is skipped)
        public bool weapon(Actor a, SpecialState s, float dt, WeaponInput inp) => IMPL.TryGetValue(s.kind, out var impl) && impl.weapon(this, a, s, dt, inp);
        public void end(Actor a, string reason = "time")
        {
            var s = a.specialActive;
            if (s == null || s.ended || s.id == "slam" || s.id == "storm") return;
            s.ended = true;
            if (IMPL.TryGetValue(s.kind, out var impl)) impl.end(this, a, s, reason);
            if (a.specialActive == s) a.specialActive = null;
            emit("special:end", new Ev { actor = a, id = s.id, reason = reason });
            if (reason == "time" && hearable(a)) play("special_end", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = a.isLocal ? 0.7f : 0.45f });
            // jetpack / zipline: super jump back to where the special started (the Return marker comes with those kinds)
            if (s.jumpBack && reason != "splat" && a.alive && s.origin.HasValue && Vector3.Distance(a.pos, s.origin.Value) > 2.5f)
                a.superJump(s.origin.Value, instant: true, home: true);
        }
        public void onSplat(Actor a)
        {
            if (a.specialActive != null && a.specialActive.id != "slam" && a.specialActive.id != "storm") end(a, "splat");
            if (a.status.shield > 0) dropShield(a, false);
        }
        // remaining fraction of a running special (HUD gauge drains)
        public float remaining(Actor a)
        {
            var s = a.specialActive;
            if (s == null) return 0;
            if (s.dur > 0) return Mathf.Max(0, 1 - s.t / s.dur);
            return 1;
        }

        // ============================================================================================= damage + hits
        public float filterDamage(Actor v, float amount, Actor attacker, string source)
        {
            var s = v.specialActive;
            if (s != null && s.kind != null && IMPL.TryGetValue(s.kind, out var impl))
            {
                float? f = impl.filterDamage(this, v, s, amount, attacker, source);
                if (f.HasValue) return f.Value;
            }
            if (v.status.shield > 0)
            {
                var d = Config.SPECIALS["bubbler"];
                knock(v, attacker, Mathf.Min(d.knockMax, amount * d.knockPerDamage));
                if (shieldMeshes.TryGetValue(v, out var m))
                {
                    m.hit = 0.6f;
                    if (G.time - m.snd > 0.12f && near(v.pos)) { m.snd = G.time; play("shield_hit", new AudioOpts { pos = v.isLocal ? (Vector3?)null : v.pos, volume = 0.45f }); }
                }
                return 0;
            }
            return amount;
        }
        public void knock(Actor v, Actor attacker, float k)
        {
            if (!(k > 0)) return;
            float dx = 0, dz = 0;
            if (attacker != null && attacker != v) { dx = v.pos.x - attacker.pos.x; dz = v.pos.z - attacker.pos.z; }
            float l = Mathf.Sqrt(dx * dx + dz * dz);
            if (l < 1e-3f) { dx = -Mathf.Sin(v.yaw); dz = -Mathf.Cos(v.yaw); } else { dx /= l; dz /= l; }
            v.vel.x += dx * k; v.vel.z += dz * k;
            if (v.grounded) { v.vel.y = Mathf.Max(v.vel.y, 1.4f + k * 0.18f); v.grounded = false; }
        }
        public void hitFlash(Actor v) { v.hurtFlash = Mathf.Min(1, v.hurtFlash + 0.25f); }

        // projectile segment against special objects: true = absorbed
        public bool shotHit(Vector3 prev, Vector3 pos, int team, float dmg, Actor owner)
        {
            foreach (var w in world) if (!w.dead && w.shotHit(prev, pos, team, dmg, owner)) return true;
            return false;
        }
        // a beam: the distance to the nearest special object on it (which takes the hit), else len
        public float rayHit(Vector3 from, Vector3 dir, float len, int team, float dmg, Actor owner)
        {
            float best = len; SpecialWorld hitW = null;
            foreach (var w in world)
            {
                if (w.dead) continue;
                float d = w.rayDist(from, dir, best);
                if (d < best) { best = d; hitW = w; }
            }
            hitW?.rayHit(team, dmg, owner);
            return best;
        }
        public void areaHit(Vector3 c, float radius, float dmg, int team, Actor owner = null)
        {
            foreach (var w in world.ToArray()) if (!w.dead) w.areaHit(c, radius, dmg, team, owner);
        }

        // ============================================================================================= shields
        public void giveShield(Actor a, float time, bool owner)
        {
            a.status.shield = Mathf.Max(a.status.shield, time);
            if (!shieldMeshes.TryGetValue(a, out var m))
            {
                m = new Shield { mat = new Material(Shader.Find("Inkwave/Fx/Bubble")) { name = "iw-shield", renderQueue = 3004 } };
                Color c = a.color.linear;
                m.mat.SetVector(ID_Color, new Vector4(c.r, c.g, c.b, 1));
                var go = ThreeSpace.create("shield");
                go.AddComponent<MeshFilter>().sharedMesh = sphereGeo();
                var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = m.mat;
                r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
                m.t = go.transform;
                m.t.localScale = Vector3.one * 0.2f;
                shieldMeshes[a] = m;
            }
            m.owner = owner;
            if (hearable(a)) play("shield_up", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = a.isLocal ? 0.8f : 0.5f });
            emit("special:shield", new Ev { actor = a, time = time });
        }
        void dropShield(Actor a, bool pop)
        {
            a.status.shield = 0;
            if (shieldMeshes.TryGetValue(a, out var m)) { disposeShield(m); shieldMeshes.Remove(a); }
            if (pop && near(a.pos))
            {
                play("shield_pop", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = 0.6f });
                G.fx?.burst(new Vector3(a.pos.x, a.pos.y + 0.9f, a.pos.z), UP, a.color, new FxOpts { count = 12, speed = 3, size = 0.07f });
            }
        }
        static void disposeShield(Shield m) { destroy(m.t.gameObject); destroy(m.mat); }

        // ============================================================================================= per frame
        readonly List<Actor> _drop = new List<Actor>();
        public void update(float dt)
        {
            // force fields: timers, follow, sharing on contact
            _drop.Clear();
            foreach (var kv in shieldMeshes) if (!(kv.Key.status.shield > 0) || !kv.Key.alive) _drop.Add(kv.Key);
            foreach (var a in _drop) dropShield(a, false);
            foreach (var a in G.actors)
            {
                if (!(a.status.shield > 0) || !a.alive) continue;
                a.status.shield -= dt;
                if (shieldMeshes.TryGetValue(a, out var m))
                {
                    float R = Config.SPECIALS["bubbler"].radius;
                    float grow = Mathf.Min(1, m.t.localScale.x / R + dt * 6);
                    m.t.localScale = Vector3.one * (R * grow * (1 + Mathf.Sin(G.time * 5 + a.slot) * 0.02f));
                    m.t.localPosition = new Vector3(a.pos.x, a.pos.y + a.smoothY + (a.form == "squid" ? 0.45f : 0.85f), a.pos.z);
                    m.hit = Mathf.Max(0, m.hit - dt * 3);
                    m.mat.SetFloat(ID_Time, G.time);
                    m.mat.SetFloat(ID_Hit, m.hit);
                    m.mat.SetFloat(ID_Alpha, a.status.shield < 1 ? 0.5f + 0.5f * Mathf.Abs(Mathf.Sin(G.time * 14)) : 1);
                    if (m.owner && a.specialActive != null && a.specialActive.id == "bubbler")
                    {
                        foreach (var o in G.actors)
                        {
                            if (o == a || o.team != a.team || !o.alive || o.status.shield > 0) continue;
                            if (Vector3.Distance(o.pos, a.pos) < Config.SPECIALS["bubbler"].shareRange) giveShield(o, a.status.shield, false);
                        }
                    }
                }
                if (a.status.shield <= 0) dropShield(a, true);
            }
            // world objects
            for (int i = world.Count - 1; i >= 0; i--)
            {
                var w = world[i];
                if (!w.update(dt) || w.dead) { w.dispose(); world.RemoveAt(i); }
            }
        }

        // ============================================================================================= shared helpers
        // stand in place: damped drift, gravity, collide (cast animations, aiming)
        public void stand(Actor a, float dt, float damp = 8)
        {
            a.vel.x *= Mathf.Exp(-damp * dt); a.vel.z *= Mathf.Exp(-damp * dt);
            if (!a.grounded) a.vel.y = Mathf.Max(-PLAYER.maxFall, a.vel.y - PLAYER.gravity * dt); else a.vel.y = 0;
            float py = a.pos.y;
            a.pos += a.vel * dt;
            a.resolve(false, py, a.grounded);
        }
    }
}
