// Port of specials.js IMPL.kraken — Kraken: you become a big ink kraken (the kid hides; the prop rides your position):
// fast, ink-proof, no squid, faces its movement, leaves an ink trail. Jump hops high; fire leaps forward (or dives when
// airborne) and the landing slams (paint + blast). Hits only shove it (SpecialSystem.filterDamage → this.filterDamage).
using UnityEngine;
using static Inkwave.Ctx;

namespace Inkwave
{
    public class Kraken : SpecialImpl
    {
        public class State { public float cd, trail; public bool attack, air; public Transform body3; }

        public override void start(SpecialSystem sys, Actor a, SpecialState s)
        {
            s.noSquid = true; s.inkProof = true; s.speed = s.def.speed; s.accel = 45; s.faceMove = true;
            var st = new State();
            s.data = st;
            a.form = "kid";
            a.weaponRunner.reset();
            a.character.setVisible(false);
            st.body3 = ThreeSpace.create("kraken").transform;
            var d = SpecialProps.get("kraken");
            if (d != null) SpecialSystem.propMesh(d, a.team, sys.glowMat(a.team), st.body3);
            else
            {
                var m = SpecialSystem.part("kraken", st.body3, SpecialSystem.sphere(), CharacterMats.ink(a.color), true).transform;
                m.localPosition = new Vector3(0, 1.1f, 0); m.localScale = new Vector3(0.7f, 1.1f, 0.7f);
            }
            sys.add(st.body3.gameObject);
            if (SpecialSystem.hearable(a)) SpecialSystem.play("kraken_on", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = 0.9f });
            G.fx?.explosion(new Vector3(a.pos.x, a.pos.y + 0.8f, a.pos.z), a.color, 1.6f);
        }
        public override bool weapon(SpecialSystem sys, Actor a, SpecialState s, float dt, WeaponInput inp)
        {
            var st = (State)s.data;
            st.cd -= dt;
            if (inp.firePressed && st.cd <= 0)
            {
                st.cd = s.def.cooldown; st.attack = true;
                if (a.grounded)
                {
                    Vector3 f = SpecialSystem.fwd(a);
                    a.vel.x += f.x * s.def.attackFwd * 0.5f; a.vel.z += f.z * s.def.attackFwd * 0.5f;
                    a.vel.y = s.def.attackVel; a.grounded = false;
                }
                else a.vel.y = Mathf.Min(a.vel.y, -16);   // dive onto whoever is below
                if (SpecialSystem.hearable(a)) SpecialSystem.play("kraken_jump", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = 0.8f });
            }
            return true;
        }
        public override void tick(SpecialSystem sys, Actor a, SpecialState s, float dt)
        {
            var st = (State)s.data;
            // big hops on jump
            if (!a.grounded && !st.air && a.vel.y > 3 && !st.attack)
            {
                a.vel.y = Mathf.Max(a.vel.y, s.def.hopVel);
                if (SpecialSystem.hearable(a)) SpecialSystem.play("kraken_jump", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = 0.6f, pitch = 1.2f });
            }
            bool landed = st.air && a.grounded;
            st.air = !a.grounded;
            if (landed && st.attack)
            {
                st.attack = false;
                Vector3 c = a.pos;
                SpecialSystem.paint(a, c + Vector3.up * 0.3f, s.def.radius * 0.8f, a.team);
                G.fx?.explosion(c + Vector3.up * 0.4f, a.color, s.def.radius);
                SpecialSystem.play("kraken_slam", new AudioOpts { pos = c, volume = 0.9f });
                emit("shake", new Ev { pos = c, amount = 0.6f });
                emit("special:slam", new Ev { actor = a, pos = c, radius = s.def.radius });
                SpecialSystem.blast(a, a.team, c + Vector3.up * 0.4f, s.def.radius, s.def.damage, s.def.damage * 0.6f, "kraken", s.def.radius * 0.7f);
                Actor.rumble(a, 0.6f, 0.5f, 200);
            }
            // ink trail
            if (a.grounded)
            {
                st.trail += Mathf.Sqrt(a.vel.x * a.vel.x + a.vel.z * a.vel.z) * dt;
                if (st.trail > 0.5f) { st.trail = 0; SpecialSystem.paint(a, a.pos + Vector3.up * 0.2f, s.def.paintRadius, a.team); }
            }
            // visual
            var b = st.body3;
            b.localPosition = new Vector3(a.pos.x, a.pos.y + a.smoothY, a.pos.z);
            b.localRotation = Quaternion.AngleAxis(a.yaw * Mathf.Rad2Deg, Vector3.up);
            float hs = Mathf.Sqrt(a.vel.x * a.vel.x + a.vel.z * a.vel.z);
            float sq = a.grounded ? 1 + Mathf.Sin(s.t * 14) * 0.04f * Mathf.Min(1, hs / 4) : clamp(1 + a.vel.y * 0.025f, 0.8f, 1.25f);
            b.localScale = new Vector3(1 / Mathf.Sqrt(sq), sq, 1 / Mathf.Sqrt(sq));
        }
        // a hit only shoves the kraken
        public override float? filterDamage(SpecialSystem sys, Actor v, SpecialState s, float amount, Actor attacker, string source)
        {
            sys.knock(v, attacker, Mathf.Min(4, amount * s.def.knockPerDamage));
            sys.hitFlash(v);
            return 0;
        }
        public override void end(SpecialSystem sys, Actor a, SpecialState s, string reason)
        {
            var st = (State)s.data;
            sys.remove(st.body3.gameObject);
            a.character.setVisible(true);
            if (a.alive) G.fx?.explosion(new Vector3(a.pos.x, a.pos.y + 0.8f, a.pos.z), a.color, 1.4f);
        }
    }
}
