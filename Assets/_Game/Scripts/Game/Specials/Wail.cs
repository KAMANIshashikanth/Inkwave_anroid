// Port of specials.js IMPL.wail + Speaker — Howl Box: hold the speaker up on your shoulder and aim (a faint guide shows
// the line); fire sets it down (bots: after a moment, everyone: after holdTime) and it charges, then blasts a sound
// beam down its line that hurts every enemy in it, walls or not.
using UnityEngine;
using static Inkwave.Ctx;

namespace Inkwave
{
    public class Wail : SpecialImpl
    {
        static readonly Hit _hit = new Hit();
        public class State { public Transform heldG, guide; public Material guideMat; public float? autoT; }

        public override void start(SpecialSystem sys, Actor a, SpecialState s)
        {
            s.speed = s.def.holdSpeed; s.noSquid = true; s.aimFace = true; s.dur = 0;
            a.character.hideWeapon = true;
            var st = new State();
            s.data = st;
            st.heldG = ThreeSpace.create("heldSpeaker").transform;
            var pd = SpecialProps.get("speaker");
            Transform m = pd != null ? SpecialSystem.propMesh(pd, a.team, sys.glowMat(a.team), st.heldG)
                : SpecialSystem.part("box", st.heldG, SpecialSystem.sphere(), CharacterMats.plastic, true).transform;
            m.localScale = Vector3.one * 0.36f;
            st.guideMat = SpecialSystem.beamMat(a.color);
            st.guide = SpecialSystem.part("wailGuide", null, SpecialSystem.beamGeo(), st.guideMat, false).transform;
            sys.add(st.heldG.gameObject); sys.add(st.guide.gameObject);
            if (a.bot != null) st.autoT = 0.45f + random() * 0.5f;
            tick(sys, a, s, 0);
        }
        public override bool weapon(SpecialSystem sys, Actor a, SpecialState s, float dt, WeaponInput inp)
        {
            var st = (State)s.data;
            s.firing = 0.2f;
            if ((inp.firePressed && s.t > 0.2f) || (st.autoT.HasValue && s.t >= st.autoT.Value) || s.t >= s.def.holdTime) place(sys, a, s);
            return true;
        }
        static Vector3 dirOf(Actor a)
        {
            float p = clamp(a.aimPitch, -0.3f, 0.3f);
            return new Vector3(Mathf.Sin(a.aimYaw) * Mathf.Cos(p), Mathf.Sin(p), Mathf.Cos(a.aimYaw) * Mathf.Cos(p));
        }
        public override void tick(SpecialSystem sys, Actor a, SpecialState s, float dt)
        {
            var st = (State)s.data;
            if (st.heldG == null) return;
            // boombox-style on the right shoulder, pointing where you aim (visible from the follow camera)
            Vector3 f = SpecialSystem.fwd(a); float rx = -f.z, rz = f.x;   // the kid's right
            st.heldG.localPosition = new Vector3(a.pos.x + rx * 0.24f + f.x * 0.06f, a.pos.y + a.smoothY + 1.06f + Mathf.Sin(s.t * 9) * 0.01f, a.pos.z + rz * 0.24f + f.z * 0.06f);
            st.heldG.localRotation = Quaternion.AngleAxis(a.aimYaw * Mathf.Rad2Deg, Vector3.up);
            // guide: where the beam will go once the speaker is down
            Vector3 dir = dirOf(a);
            st.guide.localPosition = new Vector3(a.pos.x + f.x * 1.3f, a.pos.y + 0.85f, a.pos.z + f.z * 1.3f);
            st.guide.localRotation = SpecialSystem.lookRot(dir);
            st.guide.localScale = new Vector3(0.07f, 0.07f, s.def.range);
            st.guideMat.SetFloat(SpecialSystem.ID_Time, s.t); st.guideMat.SetFloat(SpecialSystem.ID_Charge, 0);
            st.guideMat.SetFloat(SpecialSystem.ID_Alpha, 0.3f + 0.15f * Mathf.Sin(s.t * 6));
        }
        static void place(SpecialSystem sys, Actor a, SpecialState s)
        {
            Vector3 f = SpecialSystem.fwd(a);
            Vector3 at = a.pos + f * 1.3f;
            var g = G.physics.raycast(new Vector3(at.x, a.pos.y + 1.2f, at.z), Vector3.down, 4, _hit, true);
            Vector3 pos = g.hit && g.normal.y > 0.6f ? g.point : a.pos;
            a.character.trigger("throw");
            sys.world.Add(new Speaker(sys, a, pos, dirOf(a)));
            sys.end(a, "place");
        }
        public override void end(SpecialSystem sys, Actor a, SpecialState s, string reason)
        {
            var st = (State)s.data;
            if (st.heldG != null) { sys.remove(st.heldG.gameObject); sys.remove(st.guide.gameObject); destroy(st.guideMat); st.heldG = null; }
            a.character.hideWeapon = false;
        }
    }

    // Howl Box: charges, then blasts a sound wave down its line through walls
    public class Speaker : SpecialWorld
    {
        public readonly Vector3 pos, mouth; public Vector3 dir; public readonly float range, radius; public string phase = "charge";
        float t; readonly Transform group, cone, beam; readonly Material beamMat; AudioLoop loop;
        public Speaker(SpecialSystem sys, Actor a, Vector3 pos, Vector3 dir)
        {
            var d = Config.SPECIALS["wail"];
            kind = "speaker"; owner = a; team = a.team;
            this.pos = pos; this.dir = dir.normalized; range = d.range; radius = d.radius;
            float yaw = Mathf.Atan2(this.dir.x, this.dir.z);
            var rot = Quaternion.AngleAxis(yaw * Mathf.Rad2Deg, Vector3.up);
            group = ThreeSpace.create("speaker").transform;
            group.localPosition = pos; group.localRotation = rot;
            var pd = SpecialProps.get("speaker");
            Vector3 mouthLocal;
            if (pd != null)
            {
                SpecialSystem.propMesh(pd, a.team, sys.glowMat(a.team), group);
                if (pd.cone != null)
                {
                    cone = ThreeSpace.create("cone", group).transform; cone.localPosition = pd.coneAt;
                    var cm = SpecialSystem.partMesh(pd.cone, pd.coneInk, a.team, cone); cm.localPosition = -pd.coneAt;
                }
                mouthLocal = pd.mouth ?? new Vector3(0, 0.8f, 0.45f);
            }
            else
            {
                var box = SpecialSystem.part("box", group, SpecialSystem.sphere(), CharacterMats.plastic, true).transform;
                box.localPosition = new Vector3(0, 0.7f, 0); box.localScale = new Vector3(0.55f, 0.7f, 0.4f);
                mouthLocal = new Vector3(0, 0.8f, 0.5f);
            }
            mouth = pos + rot * mouthLocal;
            // aim pitch: keep the beam a little tilted at most
            float pitch = Mathf.Asin(clamp(this.dir.y, -0.3f, 0.3f));
            this.dir = new Vector3(Mathf.Sin(yaw) * Mathf.Cos(pitch), Mathf.Sin(pitch), Mathf.Cos(yaw) * Mathf.Cos(pitch));
            beamMat = SpecialSystem.beamMat(a.color);
            beam = SpecialSystem.part("wailBeam", null, SpecialSystem.beamGeo(), beamMat, false).transform;
            beam.localPosition = mouth;
            beam.localRotation = SpecialSystem.lookRot(this.dir);
            beam.localScale = new Vector3(0.12f, 0.12f, range);
            group.localScale = Vector3.one * 0.2f;
            if (SpecialSystem.near(pos, 60)) SpecialSystem.play("wail_charge", new AudioOpts { pos = pos, volume = 1 });
            emit("special:wail", new Ev { actor = a, pos = pos, dir = this.dir });
        }
        public override bool update(float dt)
        {
            var d = Config.SPECIALS["wail"];
            t += dt;
            beamMat.SetFloat(SpecialSystem.ID_Time, t);
            float pop = Mathf.Min(1, t / 0.25f);
            if (phase != "fade") group.localScale = Vector3.one * (0.2f + 0.8f * (1 - Mathf.Pow(1 - pop, 3)));
            if (phase == "charge")
            {
                float k = t / d.charge;
                beam.localScale = new Vector3(0.08f + 0.1f * k, 0.08f + 0.1f * k, range);
                beamMat.SetFloat(SpecialSystem.ID_Charge, 0); beamMat.SetFloat(SpecialSystem.ID_Alpha, 0.35f + 0.35f * Mathf.Abs(Mathf.Sin(t * (8 + k * 20))));
                if (cone != null) cone.localScale = new Vector3(1, 1, 1 + Mathf.Sin(t * 40) * 0.05f * k);
                if (t >= d.charge)
                {
                    phase = "blast"; t = 0;
                    loop = G.audio?.loop("wail_blast", new AudioOpts { pos = pos, volume = 1 });
                    emit("shake", new Ev { pos = pos, amount = 0.5f });
                }
                return true;
            }
            if (phase == "blast")
            {
                float o = Mathf.Min(1, t / 0.18f);
                beam.localScale = new Vector3(radius * o, radius * o, range);
                beamMat.SetFloat(SpecialSystem.ID_Charge, 1); beamMat.SetFloat(SpecialSystem.ID_Alpha, 0.9f);
                if (cone != null) cone.localScale = new Vector3(1, 1, 1 + Mathf.Sin(t * 60) * 0.12f);
                // everyone on the other side in the beam, walls or not
                foreach (var e in G.actors.ToArray())
                {
                    if (e.team == team || !e.alive) continue;
                    Vector3 v = e.pos + Vector3.up * 0.8f - mouth;
                    float along = Vector3.Dot(v, dir);
                    if (along < -0.5f || along > range) continue;
                    float off = (v - dir * along).magnitude;
                    if (off < radius + 0.45f) SpecialSystem.tickDamage(owner, e, d.dps * dt, "wail");
                }
                if (SpecialSystem.near(mouth, 40) && random() < dt * 30)
                    G.fx?.ring(mouth + dir * (1 + random() * 6), dir, owner.color, new FxOpts { radius = radius * 1.3f, life = 0.3f, snap = false });
                if (t >= d.blast) { phase = "fade"; t = 0; loop?.stop(0.3f); loop = null; }
                return true;
            }
            // fade
            float kf = t / 0.6f;
            beam.localScale = new Vector3(radius * (1 - kf), radius * (1 - kf), range);
            beamMat.SetFloat(SpecialSystem.ID_Alpha, 0.9f * (1 - kf));
            group.localScale = Vector3.one * Mathf.Max(0.01f, 1 - kf);
            return kf < 1;
        }
        public override void dispose() { destroy(group.gameObject); destroy(beam.gameObject); destroy(beamMat); loop?.stop(0.2f); loop = null; }
    }
}
