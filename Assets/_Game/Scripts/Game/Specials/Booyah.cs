// Port of specials.js IMPL.booyah + Orb — Cheer Orb: hold a glowing orb overhead while it charges (teammates' cheers
// speed it up: SpecialSystem.cheer), then throw it in the bomb arc; it lands, swells as a warning and bursts in a huge
// blast. You move slower and can't turn into a squid while holding it; it throws itself after autoThrow.
using UnityEngine;
using static Inkwave.Ctx;

namespace Inkwave
{
    public class Booyah : SpecialImpl
    {
        public class State
        {
            public float charge, fullT, cheered; public bool thrown;
            public Transform ball, halo; public Material ballMat, haloMat; public AudioLoop loop;
        }

        public override void start(SpecialSystem sys, Actor a, SpecialState s)
        {
            // `bomb` gives the throw-arc preview the orb's launch speed (the orb flies with the same gravity as bombs)
            s.speed = s.def.moveSpeed; s.noSquid = true; s.aimFace = true;
            s.bomb = new SubDef { kind = "booyah", throwSpeed = s.def.throwSpeed };
            var st = new State();
            s.data = st;
            a.character.hideSubProp = true;
            st.ballMat = SpecialSystem.tintMat(a.color, 1);
            Color c = a.color.linear; st.ballMat.SetVector(SpecialSystem.ID_Color, new Vector4(c.r * 2, c.g * 2, c.b * 2, 1));
            st.haloMat = SpecialSystem.bubbleMat(a.color);
            st.ball = SpecialSystem.part("orbBall", null, SpecialSystem.sphere(), st.ballMat, false).transform;
            st.halo = SpecialSystem.part("orbHalo", null, SpecialSystem.sphere(), st.haloMat, false).transform;
            sys.add(st.ball.gameObject); sys.add(st.halo.gameObject);
            st.loop = SpecialSystem.hearable(a) ? G.audio?.loop("booyah_charge", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = a.isLocal ? 0.55f : 0.4f }) : null;
        }
        public override bool weapon(SpecialSystem sys, Actor a, SpecialState s, float dt, WeaponInput inp)
        {
            var d = s.def; var st = (State)s.data;
            st.charge = Mathf.Min(1, st.charge + dt / d.charge);
            if (st.charge >= 1)
            {
                st.fullT += dt;
                if ((inp.firePressed && st.fullT > 0.05f) || st.fullT > d.autoThrow) { throwIt(sys, a, s, st); return true; }
            }
            s.raise = true;                        // arm up (the sub-aim pose), ball overhead
            return true;
        }
        public override void tick(SpecialSystem sys, Actor a, SpecialState s, float dt)
        {
            var st = (State)s.data;
            if (st.ball == null) return;
            Vector3 h = a.character is Character ch ? ch.getHeadPosition() : a.pos + Vector3.up * 1.5f;
            float r = 0.16f + 0.2f * st.charge;
            st.ball.localPosition = new Vector3(h.x, h.y + 0.42f + r, h.z);
            st.halo.localPosition = st.ball.localPosition;
            st.cheered = Mathf.Max(0, st.cheered - dt);
            float pulse = 1 + Mathf.Sin(s.t * (6 + st.charge * 12)) * 0.05f + st.cheered * 0.4f;
            st.ball.localScale = Vector3.one * (r * pulse);
            st.halo.localScale = Vector3.one * (r * 1.3f * pulse);
            st.haloMat.SetFloat(SpecialSystem.ID_Time, s.t);
            st.loop?.set(pitch: 1 + st.charge, pos: a.isLocal ? (Vector3?)null : a.pos);
        }
        static void throwIt(SpecialSystem sys, Actor a, SpecialState s, State st)
        {
            st.thrown = true;
            Vector3 from = new Vector3(a.pos.x, a.pos.y + 1.35f, a.pos.z);
            Vector3 vel = G.projectiles.throwVelocity(a, s.def.throwSpeed);
            sys.world.Add(new Orb(a, from, vel));
            s.raise = false;
            a.character.trigger("throw");
            if (SpecialSystem.hearable(a)) SpecialSystem.play("booyah_throw", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = 0.9f });
            sys.end(a, "throw");
        }
        public override void end(SpecialSystem sys, Actor a, SpecialState s, string reason)
        {
            var st = (State)s.data;
            st.loop?.stop(0.2f); st.loop = null;
            if (st.ball != null) { sys.remove(st.ball.gameObject); sys.remove(st.halo.gameObject); destroy(st.ballMat); destroy(st.haloMat); st.ball = null; }
            a.character.hideSubProp = false;
        }
    }

    // the thrown Cheer Orb: flies in the bomb arc, then swells on the spot and bursts
    public class Orb : SpecialWorld
    {
        public Vector3 pos; Vector3 prev, vel; public float t; public string phase = "fly";
        readonly Transform mesh, halo; readonly Material meshMat, haloMat;
        static readonly Hit _hit = new Hit();
        public Orb(Actor a, Vector3 from, Vector3 vel)
        {
            kind = "orb"; owner = a; team = a.team;
            pos = from; prev = from; this.vel = vel;
            meshMat = SpecialSystem.tintMat(a.color, 1);
            Color c = a.color.linear; meshMat.SetVector(SpecialSystem.ID_Color, new Vector4(c.r * 2.2f, c.g * 2.2f, c.b * 2.2f, 1));
            haloMat = SpecialSystem.bubbleMat(a.color);
            mesh = SpecialSystem.part("orb", null, SpecialSystem.sphere(), meshMat, false).transform;
            halo = SpecialSystem.part("orbHalo", null, SpecialSystem.sphere(), haloMat, false).transform;
            mesh.localScale = Vector3.one * 0.5f; halo.localScale = Vector3.one * 0.8f;
            mesh.localPosition = pos; halo.localPosition = pos;
        }
        public override bool update(float dt)
        {
            var d = Config.SPECIALS["booyah"];
            t += dt;
            haloMat.SetFloat(SpecialSystem.ID_Time, t);
            if (phase == "fly")
            {
                prev = pos;
                vel.y -= 24 * dt;
                pos += vel * dt;
                var hit = G.physics.segment(prev, pos, _hit, true);
                if (hit.hit) { pos = hit.point + hit.normal * 0.5f; phase = "fuse"; t = 0; if (SpecialSystem.near(pos, 50)) SpecialSystem.play("bomb_beep", new AudioOpts { pos = pos, volume = 1, pitch = 0.6f }); }
                else if (t > 4 || pos.y < PLAYER.waterY - 2) return false;
            }
            else if (phase == "fuse")
            {
                // swell (warning sphere grows toward the blast radius)
                float k = t / d.fuse;
                halo.localScale = Vector3.one * (0.8f + (d.radius - 0.8f) * k * k);
                haloMat.SetFloat(SpecialSystem.ID_Alpha, 0.35f + 0.3f * Mathf.Abs(Mathf.Sin(t * (6 + k * 20))));
                mesh.localScale = Vector3.one * (0.5f + Mathf.Sin(t * 30) * 0.05f * k);
                if (k >= 1) { blast(); return false; }
            }
            mesh.localPosition = pos; halo.localPosition = pos;
            return true;
        }
        void blast()
        {
            var d = Config.SPECIALS["booyah"]; Vector3 c = pos;
            var g = SpecialSystem.groundBelow(c, 6);
            Vector3 b = g ?? c; b.y += 0.3f;
            SpecialSystem.paint(owner, b, d.radius * 0.72f, team);
            for (int k = 0; k < 12; k++)
            {
                float ang = (k / 12f) * Mathf.PI * 2 + random() * 0.3f, rr = d.radius * (0.5f + random() * 0.45f);
                var gg = SpecialSystem.groundBelow(new Vector3(c.x + Mathf.Cos(ang) * rr, c.y + 2, c.z + Mathf.Sin(ang) * rr), 8);
                if (gg.HasValue) SpecialSystem.paint(owner, gg.Value + Vector3.up * 0.2f, 1.3f + random() * 0.7f, team);
            }
            G.fx?.explosion(c, owner.color, d.radius);
            SpecialSystem.play("booyah_blast", new AudioOpts { pos = c, volume = 1 });
            emit("shake", new Ev { pos = c, amount = 1.2f });
            emit("special:slam", new Ev { actor = owner, pos = c, radius = d.radius });
            SpecialSystem.blast(owner, team, c, d.radius, d.damageMax, d.damageMin, "booyah", d.killRadius);
        }
        public override void dispose() { destroy(mesh.gameObject); destroy(halo.gameObject); destroy(meshMat); destroy(haloMat); }
    }
}
