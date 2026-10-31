// Port of specials.js IMPL.zipcaster — Zipline: a dark shroud with a team-coloured rim; the main weapon keeps working and
// the sub button fires a tether: you zip to where it lands (walls: you cling there for a while, jump or push away to
// let go), slamming into any enemy in the way; every arrival is a mini explosion round you. When it ends you super jump
// back to the take-off point (Return marker).
using UnityEngine;
using static Inkwave.Ctx;

namespace Inkwave
{
    public class Zipcaster : SpecialImpl
    {
        static readonly Hit _hit = new Hit();
        public class Zip { public Vector3 to, n, anchor; public bool wall; public float t, stuck; }
        public class State
        {
            public float cd, wisp, missT; public bool subWas; public Zip zip; public Vector3 anchor, hangN, missTo; public bool hasHangN;
            public Transform aura, line; public Material auraMat, lineMat;
        }
        static Mesh _lineGeo;
        static Mesh lineGeo() => _lineGeo ??= Geo.cylinder(0.025f, 0.025f, 1, 6, 1).translate(0, 0.5f, 0).rotateX(Mathf.PI / 2).toMesh("zip-line");

        public override void start(SpecialSystem sys, Actor a, SpecialState s)
        {
            var st = new State();
            s.data = st;
            s.origin = a.pos; s.jumpBack = true; s.hang = 0;
            s.marker = new ReturnMarker(a, a.pos); sys.world.Add(s.marker);
            // a mysterious aura: a dark, shifting shroud with a team-coloured rim, trailing wisps
            st.auraMat = new Material(Shader.Find("Inkwave/Fx/Aura")) { name = "iw-aura", renderQueue = 3003 };
            Color c = a.color.linear; st.auraMat.SetVector(SpecialSystem.ID_Color, new Vector4(c.r, c.g, c.b, 1));
            st.aura = SpecialSystem.part("zipAura", null, SpecialSystem.sphere(), st.auraMat, false).transform;
            sys.add(st.aura.gameObject);
            st.lineMat = SpecialSystem.tintMat(a.color, 1);
            st.lineMat.SetVector(SpecialSystem.ID_Color, new Vector4(c.r * 1.4f, c.g * 1.4f, c.b * 1.4f, 1));
            st.line = SpecialSystem.part("zipLine", null, lineGeo(), st.lineMat, false).transform;
            st.line.gameObject.SetActive(false);
            sys.add(st.line.gameObject);
        }
        // the main weapon keeps working; the sub button fires the tether
        public override bool weapon(SpecialSystem sys, Actor a, SpecialState s, float dt, WeaponInput inp)
        {
            var st = (State)s.data;
            st.cd -= dt;
            bool press = inp.sub && !st.subWas; st.subWas = inp.sub;
            inp.sub = false; inp.subReleased = false;
            if (press && st.cd <= 0 && st.zip == null) fire(a, s, st);
            return false;
        }
        static void fire(Actor a, SpecialState s, State st)
        {
            Vector3 from = new Vector3(a.pos.x, a.pos.y + 1.2f, a.pos.z);
            Vector3 dir = (a.aimPoint - from).normalized;
            st.cd = s.def.cooldown;
            if (SpecialSystem.hearable(a)) SpecialSystem.play("zip_fire", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = 0.7f });
            var g = G.physics.raycast(from, dir, s.def.range, _hit, true);
            if (!g.hit) { st.missTo = from + dir * s.def.range; st.missT = 0.2f; return; }
            Vector3 n = g.normal; bool wall = Mathf.Abs(n.y) < 0.55f;
            Vector3 to = g.point + n * (PLAYER.radius + 0.08f);
            if (wall) to.y -= 1.0f; else to.y = g.point.y;
            st.zip = new Zip { to = to, n = n, wall = wall, anchor = g.point };
            st.anchor = g.point;
            s.body = true; s.hang = 0;
            SpecialSystem.paint(a, g.point + n * 0.1f, 1.0f, a.team);
            if (SpecialSystem.near(g.point, 40)) SpecialSystem.play("zip_latch", new AudioOpts { pos = g.point, volume = 0.8f });
            if (SpecialSystem.hearable(a)) SpecialSystem.play("zip_pull", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = 0.7f });
        }
        public override void body(SpecialSystem sys, Actor a, SpecialState s, float dt)
        {
            var st = (State)s.data; var z = st.zip;
            if (z == null) { s.body = false; return; }
            z.t += dt;
            Vector3 v = z.to - a.pos;
            float dist = v.magnitude, step = s.def.speed * dt;
            // an enemy in the way: the body slams into them and the zip stops there
            foreach (var e in G.actors.ToArray())
            {
                if (e.team == a.team || !e.alive) continue;
                float dx = e.pos.x - a.pos.x, dz = e.pos.z - a.pos.z;
                if (Mathf.Sqrt(dx * dx + dz * dz) < PLAYER.radius * 2 + 0.2f && Mathf.Abs(e.pos.y - a.pos.y) < 1.4f)
                {
                    a.vel = Vector3.zero;
                    s.body = false; st.zip = null; s.faceYaw = null;
                    impact(a, s, e);
                    return;
                }
            }
            if (dist <= step || z.t > 1.6f || z.stuck > 0.15f)
            {
                a.vel = Vector3.zero;
                if (dist <= step) a.pos = z.to;
                s.body = false; st.zip = null; s.faceYaw = null;
                if (z.wall) { s.hang = s.def.hang; st.hangN = z.n; st.hasHangN = true; a.grounded = false; }
                impact(a, s, null);
                return;
            }
            a.vel = v * (s.def.speed / dist);
            Vector3 before = a.pos; float py = a.pos.y;
            a.pos += a.vel * dt;
            a.resolve(false, py, false);
            if (Vector3.Distance(a.pos, before) < step * 0.3f) z.stuck += dt; else z.stuck = 0;
            s.faceYaw = Mathf.Atan2(a.vel.x, a.vel.z);
            st.anchor = z.anchor;
            tick(sys, a, s, dt);
        }
        // body impact: a mini explosion round the zipper — a direct hit (the enemy slammed into, or anyone right at the
        // body) takes impactDirect, the rest of the blast impactSplash
        static void impact(Actor a, SpecialState s, Actor direct)
        {
            var d = s.def; Vector3 c = new Vector3(a.pos.x, a.pos.y + 0.7f, a.pos.z);
            foreach (var e in G.actors.ToArray())
            {
                if (e.team == a.team || !e.alive) continue;
                Vector3 v = e.pos + Vector3.up * 0.8f;
                float dist = Vector3.Distance(v, c); bool hitDirect = e == direct || dist <= d.impactDirectR;
                if (!hitDirect && dist > d.impactRadius + 0.3f) continue;
                if (e != direct && !G.physics.los(c, v)) continue;
                G.projectiles.applyHit(a, e, hitDirect ? d.impactDirect : d.impactSplash, "zipcaster");
            }
            G.subs?.damageArea(c, d.impactRadius, d.impactSplash, a.team);
            var g = SpecialSystem.groundBelow(c, 3);
            Vector3 b = g ?? c; b.y += 0.2f;
            SpecialSystem.paint(a, b, d.impactRadius * 0.75f, a.team);
            G.fx?.explosion(c, a.color, d.impactRadius);
            if (SpecialSystem.near(c, 40)) SpecialSystem.play("blaster_boom", new AudioOpts { pos = a.isLocal ? (Vector3?)null : c, volume = 0.6f, pitch = 1.25f });
            emit("shake", new Ev { pos = c, amount = 0.35f });
        }
        public override void tick(SpecialSystem sys, Actor a, SpecialState s, float dt)
        {
            var st = (State)s.data;
            if (st.zip == null) s.faceYaw = null;
            if (st.aura != null)
            {
                bool sq = a.form == "squid";
                st.aura.localPosition = new Vector3(a.pos.x, a.pos.y + a.smoothY + (sq ? 0.35f : 0.85f), a.pos.z);
                float br = 1 + Mathf.Sin(s.t * 3) * 0.04f;
                st.aura.localScale = new Vector3((sq ? 0.7f : 0.62f) * br, (sq ? 0.45f : 1.05f) * br, (sq ? 0.7f : 0.62f) * br);
                st.auraMat.SetFloat(SpecialSystem.ID_Time, s.t);
                st.wisp -= dt;
                if (st.wisp <= 0 && SpecialSystem.near(a.pos, 26))
                {
                    st.wisp = 0.07f;
                    Color c = Color.Lerp(a.color, new Color(0.1f, 0.01f, 0.18f), 0.8f);
                    G.fx?.burst(new Vector3(a.pos.x + (random() - 0.5f) * 0.7f, a.pos.y + 0.3f + random() * 1.3f, a.pos.z + (random() - 0.5f) * 0.7f), Vector3.up, c, new FxOpts { count = 1, speed = 0.8f, size = 0.06f, gravity = -0.15f, ring = false, mist = false });
                }
            }
            if (s.hang > 0)
            {
                s.hang -= dt;
                // jump (or push away from the wall) lets go
                var mv = a.intent.move;
                bool away = st.hasHangN && (mv.x * st.hangN.x + mv.z * st.hangN.z) > 0.5f;
                if (a.intent.jump || away)
                {
                    s.hang = 0;
                    Vector3 n = st.hasHangN ? st.hangN : Vector3.up;
                    a.vel = new Vector3(n.x * 4, 8, n.z * 4); a.grounded = false;
                }
            }
            // tether line: hand → anchor while zipping or hanging
            bool show = st.zip != null || s.hang > 0 || st.missT > 0;
            st.line.gameObject.SetActive(show);
            if (show)
            {
                Vector3 h = a.character is Character ch && ch.bones != null && ch.bones.TryGetValue("handL", out var hand) ? ThreeSpace.toThree(hand.position) : a.pos + Vector3.up * 1.2f;
                Vector3 to = st.zip != null ? st.zip.anchor : s.hang > 0 ? st.anchor : st.missTo;
                if (st.missT > 0) st.missT -= dt;
                st.line.localPosition = h;
                st.line.localRotation = SpecialSystem.lookRot(to - h);
                st.line.localScale = new Vector3(1, 1, Mathf.Max(0.01f, Vector3.Distance(h, to)));
            }
        }
        public override void end(SpecialSystem sys, Actor a, SpecialState s, string reason)
        {
            var st = (State)s.data;
            sys.remove(st.line.gameObject); sys.remove(st.aura.gameObject);
            destroy(st.lineMat); destroy(st.auraMat);
            st.aura = null;
            s.hang = 0;
        }
    }
}
