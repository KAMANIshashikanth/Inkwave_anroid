// Port of specials.js IMPL.blower + Bubble (+ SpecialSystem._bubbleHit) — Bubble Blower: the main weapon becomes the
// blower (sp_blower model); hold fire to blow a bubble (up to `max`), let go to release it. Bubbles float, wall off
// enemies, soak up enemy fire (it shrinks them a little); your team's ink charges a bubble until it blows up in a big
// blast (which can set off other bubbles). The special ends 0.6 s after the last bubble.
using UnityEngine;
using static Inkwave.Ctx;

namespace Inkwave
{
    public class Blower : SpecialImpl
    {
        public class State { public float count, curT; public Bubble cur; public bool needRelease, done; public AudioLoop inflate; }

        public override void start(SpecialSystem sys, Actor a, SpecialState s)
        {
            s.data = new State();
            s.aimFace = true; s.noSquid = true;
            sys.swapWeapon(a, "sp_blower");
        }
        public override bool weapon(SpecialSystem sys, Actor a, SpecialState s, float dt, WeaponInput inp)
        {
            var d = s.def; var st = (State)s.data;
            if (!inp.fire) st.needRelease = false;
            if (inp.fire && st.cur == null && st.count < d.max && !st.done && !st.needRelease)
            {
                st.cur = new Bubble(a); st.curT = 0; sys.world.Add(st.cur);
                st.inflate = SpecialSystem.hearable(a) ? G.audio?.loop("blower_inflate", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = a.isLocal ? 0.6f : 0.4f }) : null;
            }
            if (st.cur != null)
            {
                st.curT += dt; s.firing = 0.3f;
                float k = clamp(st.curT / d.inflate, 0, 1);
                st.cur.r = lerp(d.rMin, d.rMax, 1 - (1 - k) * (1 - k));
                Vector3 m = G.projectiles.muzzle(a);
                Vector3 f = SpecialSystem.fwd(a);
                Vector3 p = m + f * (st.cur.r + 0.2f);
                p.y = Mathf.Max(p.y, a.pos.y + st.cur.r * 0.7f);
                st.cur.pos = p;
                st.inflate?.set(pitch: 1 + k);
                if (!inp.fire || st.curT > d.inflate + 0.6f)
                {
                    if (inp.fire) st.needRelease = true;
                    st.cur.release(f); st.cur = null; st.count++;
                    st.inflate?.stop(0.05f); st.inflate = null;
                    if (st.count >= d.max) { st.done = true; s.dur = s.t + 0.6f; }
                }
            }
            return true;
        }
        public override void end(SpecialSystem sys, Actor a, SpecialState s, string reason)
        {
            var st = (State)s.data;
            if (st.cur != null) { st.cur.release(SpecialSystem.fwd(a)); st.cur = null; }
            st.inflate?.stop(0.05f); st.inflate = null;
            sys.restoreWeapon(a);
        }
    }

    // a Bubble Blower bubble: floats, walls off enemies, soaks up enemy fire; team fire sets it off
    public class Bubble : SpecialWorld
    {
        public Vector3 pos, vel; public float r; public bool held = true;
        float t, life, charge, hit; readonly float ph;
        readonly Transform mesh; readonly Material mat;
        static readonly Hit _hit = new Hit();
        public Bubble(Actor a)
        {
            var d = Config.SPECIALS["blower"];
            kind = "bubble"; owner = a; team = a.team;
            r = d.rMin; life = d.life; ph = random() * 6;
            mat = SpecialSystem.bubbleMat(a.color);
            mesh = SpecialSystem.part("bubble", null, SpecialSystem.sphere(), mat, false).transform;
            mesh.localScale = Vector3.one * r;
        }
        public void release(Vector3 dir)
        {
            held = false;
            vel = new Vector3(dir.x, 0, dir.z).normalized * Config.SPECIALS["blower"].drift;
            vel.y = 0.25f;
            if (SpecialSystem.near(pos)) SpecialSystem.play("bubble_release", new AudioOpts { pos = pos, volume = 0.7f });
        }
        public override bool update(float dt)
        {
            t += dt;
            hit = Mathf.Max(0, hit - dt * 3);
            mat.SetFloat(SpecialSystem.ID_Time, t); mat.SetFloat(SpecialSystem.ID_Hit, hit);
            if (!held)
            {
                life -= dt;
                vel *= Mathf.Exp(-0.45f * dt);
                vel.y += Mathf.Sin(t * 1.7f + ph) * 0.15f * dt;
                Vector3 was = pos;
                pos += vel * dt;
                // don't sink into the deck; stop at walls
                float g = G.level.groundHeight(pos.x, pos.z, pos.y + 1);
                if (!float.IsNegativeInfinity(g) && pos.y - r * 0.85f < g) { pos.y = g + r * 0.85f; vel.y = Mathf.Max(0, vel.y); }
                if (vel.sqrMagnitude > 1e-8f)
                {
                    var w = G.physics.raycast(was, vel.normalized, r * 0.8f + vel.magnitude * dt, _hit, true);
                    if (w.hit && Mathf.Abs(w.normal.y) < 0.6f) { pos = was; vel = new Vector3(0, vel.y, 0); }
                }
                // enemies can't pass through
                foreach (var e in G.actors)
                {
                    if (e.team == team || !e.alive) continue;
                    float vx = e.pos.x - pos.x, vz = e.pos.z - pos.z;
                    float dy = e.pos.y + 0.8f - pos.y;
                    float R = Mathf.Sqrt(Mathf.Max(0, r * r - dy * dy)) + PLAYER.radius;
                    float dh = Mathf.Sqrt(vx * vx + vz * vz);
                    if (dh < R && dh > 1e-3f)
                    {
                        e.pos.x = pos.x + (vx / dh) * R; e.pos.z = pos.z + (vz / dh) * R;
                        float vn = (e.vel.x * vx + e.vel.z * vz) / dh;
                        if (vn < 0) { e.vel.x -= (vx / dh) * vn; e.vel.z -= (vz / dh) * vn; }
                    }
                }
                if (life <= 0) { pop(); return false; }
            }
            float wob = 1 + Mathf.Sin(t * 6 + ph) * 0.025f;
            mesh.localScale = new Vector3(r * wob, r / wob, r * wob);
            mesh.localPosition = pos;
            mat.SetFloat(SpecialSystem.ID_Alpha, life < 1 ? 0.4f + 0.6f * Mathf.Abs(Mathf.Sin(t * 16)) : 1);
            return !dead;
        }
        void pop()
        {
            dead = true;
            SpecialSystem.paint(owner, new Vector3(pos.x, pos.y - r * 0.5f, pos.z), r * 0.6f, team);
            if (SpecialSystem.near(pos)) { SpecialSystem.play("bubble_pop", new AudioOpts { pos = pos, volume = 0.7f }); G.fx?.burst(pos, Vector3.up, owner.color, new FxOpts { count = 10, speed = 3, size = 0.08f }); }
        }
        public void explode(Actor by)
        {
            if (dead) return;
            dead = true;
            var d = Config.SPECIALS["blower"]; float R = r * d.blastMul;
            Vector3 c = pos;
            var who = by != null && by.team == team ? by : owner;
            for (int k = 0; k < 6; k++)
            {
                float ang = (k / 6f) * Mathf.PI * 2 + random() * 0.4f, rr = R * (0.35f + random() * 0.5f);
                var g = SpecialSystem.groundBelow(new Vector3(c.x + Mathf.Cos(ang) * rr, c.y + 1, c.z + Mathf.Sin(ang) * rr), 6);
                if (g.HasValue) SpecialSystem.paint(who, g.Value + Vector3.up * 0.2f, 1.2f + random() * 0.6f, team);
            }
            var g0 = SpecialSystem.groundBelow(c, 6); if (g0.HasValue) SpecialSystem.paint(who, g0.Value + Vector3.up * 0.2f, R * 0.55f, team);
            G.fx?.explosion(c, owner.color, R);
            SpecialSystem.play("bubble_blast", new AudioOpts { pos = c, volume = 1 });
            emit("shake", new Ev { pos = c, amount = 0.7f });
            emit("bomb:explode", new Ev { actor = who, pos = c, team = team, radius = R });
            SpecialSystem.blast(who, team, c, R, d.damageMax, d.damageMin, "blower", R * 0.45f);   // (areaHit may chain other bubbles)
        }

        // ---- hits (SpecialSystem.shotHit / rayHit / areaHit): your team's ink charges it until it blows; enemy ink is
        // soaked up (and shrinks it a little)
        void bubbleHit(int byTeam, float dmg, Actor by)
        {
            if (dead) return;
            hit = 0.5f;
            var d = Config.SPECIALS["blower"];
            if (byTeam == team)
            {
                if (held) return;
                charge += dmg;
                if (charge >= d.popDamage) explode(by);
            }
            else r = Mathf.Max(d.rMin * 0.75f, r - dmg * 0.004f);
        }
        public override bool shotHit(Vector3 prev, Vector3 p, int byTeam, float dmg, Actor by)
        {
            var (dist, _) = Physics.segmentCapsuleDist(prev, p, new Vector3(pos.x, pos.y - r * 0.5f, pos.z), r * 0.5f, r);
            if (dist < r * 0.95f) { bubbleHit(byTeam, dmg, by); return true; }
            return false;
        }
        public override float rayDist(Vector3 from, Vector3 dir, float best)
        {
            Vector3 v = pos - from;
            float tc = Vector3.Dot(v, dir);
            if (tc <= 0 || tc > best) return best;
            float d2 = v.sqrMagnitude - tc * tc;
            if (d2 > r * r) return best;
            float tt = tc - Mathf.Sqrt(r * r - d2);
            return tt < best ? Mathf.Max(0.1f, tt) : best;
        }
        public override void rayHit(int byTeam, float dmg, Actor by) { bubbleHit(byTeam, dmg, by); }
        public override void areaHit(Vector3 c, float radius, float dmg, int byTeam, Actor by)
        {
            if (Vector3.Distance(pos, c) < radius + r * 0.8f) bubbleHit(byTeam, dmg * 1.5f, by);
        }
        public override void dispose() { destroy(mesh.gameObject); destroy(mat); }
    }
}
