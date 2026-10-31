// Port of specials.js IMPL.stamp + ThrownStamp — Mega Stamp: a huge stamp (sp_stamp) that slams the ground in front of
// you. The body turns toward the aim slowly and moves mostly along its facing; each swing guards the front (enemy shots
// knocked out of the air, bombs smashed, hits deflected: filterDamage); a mid-air swing flips over (behind, then a long
// smash in front); the sub button throws the stamp as a long-range blast and ends the special.
using System.Collections.Generic;
using UnityEngine;
using static Inkwave.Ctx;

namespace Inkwave
{
    public class Stamp : SpecialImpl
    {
        static readonly Hit _hit = new Hit();
        static readonly HashSet<string> STAMP_SMASHES = new HashSet<string> { "bomb", "sticky", "burst", "seeker", "mist", "mine" };   // sub kinds a swing smashes
        public class Flip { public float t; public bool back, front; }
        public class State
        {
            public float cd, pending = -1, guard, bodyYaw; public bool subWas; public Flip flip;
            public bool mvSet; public Vector3 mvIn, mvOut;
        }
        // the body's facing (it turns slower than the aim) and a front-arc test (is p within arcDeg of the facing?)
        public static Vector3 stampFwd(State st) => new Vector3(Mathf.Sin(st.bodyYaw), 0, Mathf.Cos(st.bodyYaw));
        public static bool stampFront(Actor a, State st, Vector3 p, float arcDeg)
        {
            float dx = p.x - a.pos.x, dz = p.z - a.pos.z, l = Mathf.Sqrt(dx * dx + dz * dz);
            if (l < 0.3f) return true;
            return (dx * Mathf.Sin(st.bodyYaw) + dz * Mathf.Cos(st.bodyYaw)) / l >= Mathf.Cos(arcDeg * Mathf.Deg2Rad);
        }

        public override void start(SpecialSystem sys, Actor a, SpecialState s)
        {
            s.data = new State { bodyYaw = a.yaw };
            s.speed = s.def.moveSpeed; s.noSquid = true; s.faceYaw = a.yaw;
            sys.swapWeapon(a, "sp_stamp");
        }
        // heavy: the body swings round toward the aim slowly (quicker once stopped), and moves mostly along its facing —
        // sideways input is cut to a shuffle
        public override void move(SpecialSystem sys, Actor a, SpecialState s, float dt)
        {
            var d = s.def; var st = (State)s.data;
            float hs = Mathf.Sqrt(a.vel.x * a.vel.x + a.vel.z * a.vel.z);
            float rate = (hs > 1.5f ? d.turnRate : d.turnRateStill) * dt;
            st.bodyYaw += Mathf.Clamp(angleDiff(st.bodyYaw, a.aimYaw), -rate, rate);
            s.faceYaw = st.bodyYaw;
            Vector3 f = stampFwd(st), mv = a.intent.move;
            // (input that nobody rewrote since last frame is still our output: filter the original again, never compound)
            if (st.mvSet && mv.x == st.mvOut.x && mv.z == st.mvOut.z) { mv.x = st.mvIn.x; mv.z = st.mvIn.z; }
            st.mvIn = mv;
            float along = mv.x * f.x + mv.z * f.z;
            mv.x = f.x * along + (mv.x - f.x * along) * d.strafe;
            mv.z = f.z * along + (mv.z - f.z * along) * d.strafe;
            a.intent.move = mv; st.mvOut = mv; st.mvSet = true;
        }
        public override bool weapon(SpecialSystem sys, Actor a, SpecialState s, float dt, WeaponInput inp)
        {
            var d = s.def; var st = (State)s.data;
            st.cd -= dt;
            bool subPress = inp.sub && !st.subWas; st.subWas = inp.sub;
            if (subPress)
            {
                // throw it: a long-range blast, and the special is over
                Vector3 m = G.projectiles.muzzle(a);
                Vector3 dir = Projectiles.aimFrom(a, m);
                a.character.trigger("throw");
                sys.world.Add(new ThrownStamp(a, new Vector3(a.pos.x, a.pos.y + 1.5f, a.pos.z), dir));
                sys.end(a, "throw");
                return true;
            }
            if (inp.fire && st.cd <= 0 && st.flip == null)
            {
                if (!a.grounded)
                {
                    // mid-air swing: a single flip — behind first as the stamp goes over, then a long smash in front
                    Vector3 f = stampFwd(st);
                    st.flip = new Flip(); st.guard = d.flipTime; st.cd = d.flipTime + 0.1f;
                    a.character.trigger("roll", d.flipTime, new Vector2(f.x, f.z));
                    if (SpecialSystem.hearable(a)) SpecialSystem.play("stamp_swing", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = 0.7f, pitch = 0.85f });
                }
                else
                {
                    st.pending = 0.15f; st.cd = d.interval; st.guard = d.guardTime;
                    if (SpecialSystem.hearable(a)) SpecialSystem.play("stamp_swing", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = 0.6f });
                    a.character.trigger("flick");
                }
                s.firing = 0.5f;
            }
            if (st.pending >= 0)
            {
                st.pending -= dt;
                if (st.pending < 0)
                {
                    slam(a, s, st, d.radius, d.reach);
                    Vector3 f = stampFwd(st);
                    a.vel.x += f.x * d.lunge; a.vel.z += f.z * d.lunge;
                }
            }
            return true;
        }
        public override void tick(SpecialSystem sys, Actor a, SpecialState s, float dt)
        {
            var d = s.def; var st = (State)s.data;
            if (st.flip != null)
            {
                var fl = st.flip; fl.t += dt;
                if (!fl.back && fl.t >= d.flipTime * 0.3f) { fl.back = true; slam(a, s, st, d.flipBackRadius, -d.flipBackReach); }
                if (!fl.front && fl.t >= d.flipTime * 0.75f) { fl.front = true; slam(a, s, st, d.flipRadius, d.flipReach); emit("shake", new Ev { pos = a.pos, amount = 0.5f }); }
                if (fl.t >= d.flipTime) st.flip = null;
            }
            if (st.guard > 0) { st.guard -= dt; guardSweep(a, s, st); }
        }
        // the swing's guard: enemy shots in front are knocked out of the air, enemy bombs in front are smashed
        static void guardSweep(Actor a, SpecialState s, State st)
        {
            var d = s.def; float ay = a.pos.y + 1.0f;
            Vector3 chest = new Vector3(a.pos.x, ay, a.pos.z);
            var shots = G.projectiles.list;
            for (int i = shots.Count - 1; i >= 0; i--)
            {
                var p = shots[i];
                if (p.team == a.team || Mathf.Abs(p.pos.y - ay) > 2.0f) continue;
                float dx = p.pos.x - a.pos.x, dz = p.pos.z - a.pos.z;
                if (Mathf.Sqrt(dx * dx + dz * dz) > d.deflectR || !stampFront(a, st, p.pos, d.deflectArc)) continue;
                Vector3 at = p.pos;
                G.projectiles.removeShot(p);
                if (SpecialSystem.near(at, 30)) G.fx?.burst(at, Vector3.up, a.color, new FxOpts { count = 4, speed = 3.5f, size = 0.06f, ring = false });
            }
            foreach (var b in G.projectiles.bombs.ToArray())
            {
                if (b.team == a.team || b.kind != "bomb") continue;
                if (Vector3.Distance(b.pos, chest) > d.bombClearR || !stampFront(a, st, b.pos, d.deflectArc)) continue;
                Vector3 at = b.pos;
                G.projectiles.defuseBomb(b);
                if (SpecialSystem.near(at, 40)) { G.fx?.burst(at, Vector3.up, a.color, new FxOpts { count = 10, speed = 4, size = 0.08f }); SpecialSystem.play("shield_hit", new AudioOpts { pos = at, volume = 0.6f, pitch = 0.8f }); }
            }
            if (G.subs != null)
                foreach (var it in G.subs.items.ToArray())
                {
                    if (it.team == a.team || it.state == "dead" || !STAMP_SMASHES.Contains(it.kind)) continue;
                    if (Vector3.Distance(it.pos, chest) > d.bombClearR || !stampFront(a, st, it.pos, d.deflectArc)) continue;
                    G.subs.destroyItem(it);
                }
        }
        static void slam(Actor a, SpecialState s, State st, float radius, float reach)
        {
            Vector3 f = stampFwd(st);
            Vector3 c = a.pos + f * reach;
            var g = G.physics.raycast(new Vector3(c.x, a.pos.y + 1.2f, c.z), Vector3.down, 3, _hit, true);
            Vector3 at = g.hit ? g.point : new Vector3(c.x, a.pos.y, c.z);
            SpecialSystem.paint(a, at + Vector3.up * 0.2f, radius * 0.95f, a.team);
            Vector3 p2 = at + f * (radius * 0.7f); p2.y = at.y + 0.2f;
            SpecialSystem.paint(a, p2, radius * 0.6f, a.team);
            if (SpecialSystem.near(at, 40)) { G.fx?.explosion(at + Vector3.up * 0.3f, a.color, radius); SpecialSystem.play("stamp_slam", new AudioOpts { pos = at, volume = 0.85f }); }
            Actor.rumble(a, 0.5f, 0.4f, 120);
            foreach (var e in G.actors.ToArray())
            {
                if (e.team == a.team || !e.alive) continue;
                float dx = e.pos.x - at.x, dz = e.pos.z - at.z;
                if (Mathf.Sqrt(dx * dx + dz * dz) > radius + PLAYER.radius || Mathf.Abs(e.pos.y - at.y) > 1.8f) continue;
                if (!G.physics.los(at + Vector3.up * 0.5f, e.pos + Vector3.up * 0.6f)) continue;
                G.projectiles.applyHit(a, e, s.def.damage, "stamp");
            }
            G.subs?.damageArea(at, radius, 80, a.team);
        }
        // mid-swing: anything coming from the front is deflected (sides + back stay open)
        public override float? filterDamage(SpecialSystem sys, Actor v, SpecialState s, float amount, Actor attacker, string source)
        {
            var st = (State)s.data;
            if (!(st.guard > 0) || attacker == null || attacker == v || !stampFront(v, st, attacker.pos, s.def.deflectArc)) return null;
            if (SpecialSystem.near(v.pos, 40))
            {
                SpecialSystem.play("shield_hit", new AudioOpts { pos = v.isLocal ? (Vector3?)null : v.pos, volume = 0.6f, pitch = 1.3f });
                G.fx?.burst(new Vector3(v.pos.x, v.pos.y + 1.1f, v.pos.z) + stampFwd(st) * 0.9f, Vector3.up, v.color, new FxOpts { count = 6, speed = 4, size = 0.07f });
            }
            return 0;
        }
        public override void end(SpecialSystem sys, Actor a, SpecialState s, string reason) { sys.restoreWeapon(a); }
    }

    // the thrown Mega Stamp: a tumbling long-range throw that bursts on whatever it hits
    public class ThrownStamp : SpecialWorld
    {
        Vector3 pos, prev, vel; float t;
        readonly Transform mesh;
        static readonly Hit _hit = new Hit();
        public ThrownStamp(Actor a, Vector3 from, Vector3 dir)
        {
            var d = Config.SPECIALS["stamp"];
            kind = "stamp"; owner = a; team = a.team;
            pos = from; prev = from; vel = dir * d.throwSpeed; vel.y += 2.5f;
            var wd = WeaponModel.getWeaponDef("sp_stamp");
            mesh = SpecialSystem.partMesh(wd?.body, wd?.ink, a.team, null);
            mesh.localScale = Vector3.one * 1.6f;
            mesh.localPosition = pos;
            if (SpecialSystem.hearable(a)) SpecialSystem.play("stamp_throw", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = 0.9f });
        }
        public override bool update(float dt)
        {
            t += dt;
            prev = pos;
            vel.y -= 12 * dt;
            pos += vel * dt;
            Vector3? at = null;
            var hit = G.physics.segment(prev, pos, _hit, true);
            if (hit.hit) at = hit.point + hit.normal * 0.3f;
            if (!at.HasValue)
                foreach (var e in G.actors)
                {
                    if (e.team == team || !e.alive) continue;
                    var (dist, tt) = Physics.segmentCapsuleDist(prev, pos, e.pos, 0.7f, 1.6f);
                    if (dist < 0.8f) { at = Vector3.Lerp(prev, pos, tt); break; }
                }
            if (!at.HasValue && (G.specials?.shotHit(prev, pos, team, 200, owner) ?? false)) at = pos;
            mesh.localPosition = pos;
            mesh.localRotation = ThreeSpace.eulerXYZ(t * 14, Mathf.Atan2(vel.x, vel.z), 0);
            if (at.HasValue || t > 3 || pos.y < PLAYER.waterY - 2)
            {
                if (at.HasValue) boom(at.Value);
                return false;
            }
            return true;
        }
        void boom(Vector3 c)
        {
            var d = Config.SPECIALS["stamp"];
            var g = SpecialSystem.groundBelow(c, 4);
            Vector3 b = g ?? c; b.y += 0.2f;
            SpecialSystem.paint(owner, b, d.throwRadius * 0.8f, team);
            for (int k = 0; k < 4; k++)
            {
                float ang = random() * Mathf.PI * 2, rr = d.throwRadius * (0.5f + random() * 0.4f);
                SpecialSystem.paint(owner, new Vector3(c.x + Mathf.Cos(ang) * rr, c.y + 0.5f, c.z + Mathf.Sin(ang) * rr), 0.9f, team);
            }
            G.fx?.explosion(c, owner.color, d.throwRadius);
            SpecialSystem.play("stamp_slam", new AudioOpts { pos = c, volume = 1 });
            emit("shake", new Ev { pos = c, amount = 0.8f });
            emit("bomb:explode", new Ev { actor = owner, pos = c, team = team, radius = d.throwRadius });
            SpecialSystem.blast(owner, team, c, d.throwRadius, d.throwDamageMax, d.throwDamageMin, "stamp", 1.2f);
        }
        public override void dispose() { destroy(mesh.gameObject); }
    }
}
