// Port of specials.js IMPL.zooka + Twister — Twister Zooka: the main weapon becomes the zooka (sp_zooka model); each shot
// is a tall spinning twister flying straight that pierces players (one hit each), paints a line under its path and stops
// at walls, enemy devices and special objects. You move slower, face the aim and can't turn into a squid.
using System.Collections.Generic;
using UnityEngine;
using static Inkwave.Ctx;

namespace Inkwave
{
    public class Zooka : SpecialImpl
    {
        public class State { public float cd; }

        public override void start(SpecialSystem sys, Actor a, SpecialState s)
        {
            s.data = new State { cd = 0.25f };
            s.speed = 4.2f; s.aimFace = true; s.noSquid = true;
            sys.swapWeapon(a, "sp_zooka");
        }
        public override bool weapon(SpecialSystem sys, Actor a, SpecialState s, float dt, WeaponInput inp)
        {
            var st = (State)s.data;
            st.cd -= dt;
            if (inp.fire && st.cd <= 0)
            {
                st.cd = s.def.interval; s.firing = 0.45f;
                Vector3 m = G.projectiles.muzzle(a);
                Vector3 dir = Projectiles.aimFrom(a, m);
                sys.world.Add(new Twister(sys, a, m, dir));
                a.character.trigger("shoot");
                if (SpecialSystem.hearable(a)) SpecialSystem.play("zooka_fire", new AudioOpts { pos = a.isLocal ? (Vector3?)null : m, volume = a.isLocal ? 0.9f : 0.6f });
                if (a.isLocal) emit("recoil", new Ev { amount = 0.02f });
                Actor.rumble(a, 0.45f, 0.5f, 140);
                emit("weapon:fire", new Ev { actor = a, weaponId = "zooka", muzzle = m, dir = dir });
            }
            return true;
        }
        public override void end(SpecialSystem sys, Actor a, SpecialState s, string reason) { sys.restoreWeapon(a); }
    }

    // a Twister Zooka shot: a spinning funnel flying straight; pierces players, stops at walls
    public class Twister : SpecialWorld
    {
        readonly SpecialSystem sys;
        Vector3 pos, prev; readonly Vector3 vel; float t, paintAcc, spin; readonly float life;
        readonly HashSet<Actor> hit = new HashSet<Actor>();
        readonly Transform mesh; readonly Material mat;
        static readonly Hit _hit = new Hit();
        public Twister(SpecialSystem sys, Actor a, Vector3 from, Vector3 dir)
        {
            var d = Config.SPECIALS["zooka"];
            this.sys = sys; kind = "twister"; owner = a; team = a.team;
            pos = from; prev = from; vel = dir * d.speed;
            life = d.range / d.speed;
            mat = SpecialSystem.swirlMat(a.color, 3, 0.3f, 1, 0.12f);
            mesh = SpecialSystem.part("twister", null, SpecialSystem.funnelGeo(), mat, false).transform;
            mesh.localScale = new Vector3(d.radius * 1.4f, d.height, d.radius * 1.4f);
            mesh.localPosition = new Vector3(pos.x, pos.y - d.height * 0.45f, pos.z);
        }
        public override bool update(float dt)
        {
            var d = Config.SPECIALS["zooka"];
            t += dt;
            prev = pos;
            pos += vel * dt;
            // walls end it
            var w = G.physics.segment(prev, pos, _hit, true);
            if (w.hit) { burst(w.point, w.normal, false); return false; }
            if (G.subs != null && G.subs.blockShot(prev, pos, team, 120)) { burst(pos, Vector3.up, false); return false; }
            if (sys.shotHit(prev, pos, team, 120, owner)) { burst(pos, Vector3.up, false); return false; }
            // players inside the column
            foreach (var e in G.actors.ToArray())
            {
                if (e.team == team || !e.alive || hit.Contains(e)) continue;
                float top = e.pos.y + (e.form == "squid" ? 0.6f : 1.6f);
                if (top < pos.y - d.height * 0.5f || e.pos.y > pos.y + d.height * 0.5f) continue;
                // horizontal distance from the player axis to the segment
                var (dist, _) = Physics.segmentCapsuleDist(prev, new Vector3(pos.x, prev.y, pos.z), new Vector3(e.pos.x, prev.y - 0.5f, e.pos.z), 0.01f, 1);
                if (dist < d.radius + (e.hitR ?? PLAYER.radius))
                {
                    hit.Add(e);
                    G.projectiles.applyHit(owner, e, d.damage, "zooka");
                    G.fx?.burst(new Vector3(e.pos.x, e.pos.y + 0.8f, e.pos.z), Vector3.up, owner.color, new FxOpts { count = 10, speed = 4, size = 0.09f });
                }
            }
            // a line of ink under the path
            paintAcc += vel.magnitude * dt;
            if (paintAcc > d.paintEvery)
            {
                paintAcc = 0;
                var g = SpecialSystem.groundBelow(pos, 3.5f);
                if (g.HasValue) SpecialSystem.paint(owner, g.Value + Vector3.up * 0.15f, d.paintRadius * (0.85f + random() * 0.3f), team);
            }
            mesh.localPosition = new Vector3(pos.x, pos.y - d.height * 0.45f, pos.z);
            mat.SetFloat(SpecialSystem.ID_Time, t);
            mat.SetFloat(SpecialSystem.ID_Alpha, Mathf.Min(1, (life - t) / 0.25f));
            spin += dt * 14;
            mesh.localRotation = Quaternion.AngleAxis(spin * Mathf.Rad2Deg, Vector3.up);
            if (t > life) { burst(pos, Vector3.up, true); return false; }
            return true;
        }
        void burst(Vector3 p, Vector3 n, bool soft)
        {
            SpecialSystem.paint(owner, p + n * 0.15f, soft ? 1.1f : 1.6f, team);
            if (SpecialSystem.near(p, 40)) { G.fx?.burst(p, n, owner.color, new FxOpts { count = 12, speed = 4, size = 0.09f }); SpecialSystem.play("splat_big", new AudioOpts { pos = p, volume = 0.6f }); }
        }
        public override void dispose() { destroy(mesh.gameObject); destroy(mat); }
    }
}
