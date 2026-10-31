// Port of specials.js IMPL.strike + Missile + Tornado — Vortex Strike: pick a spot (bots: the thickest enemy cluster, or
// the most enemy ink), launch a missile that climbs out of view and drops onto it, where it becomes a swirling ink vortex
// that churns paint over its footprint and hurts (and gently drags in) enemies inside.
// Aiming: JS drives a cursor over the opened stage map; until the full map exists the local player's cursor is a ground
// ring the move stick slides (PlayerController → SpecialSystem.aimMove), and fire launches.
using Inkwave.World;
using UnityEngine;
using static Inkwave.Ctx;

namespace Inkwave
{
    public class Strike : SpecialImpl
    {
        static readonly Hit _hit = new Hit();
        public class State { public bool aiming, confirm; public Vector3 target; public float? autoT; public Transform cursor; public Material cursorMat; }

        public override void start(SpecialSystem sys, Actor a, SpecialState s)
        {
            s.body = true; s.dur = 0;
            var st = new State { aiming = true };
            s.data = st;
            var B = G.level.bounds;
            st.target = new Vector3(clamp(a.pos.x + Mathf.Sin(a.aimYaw) * 16, B.minX + 2, B.maxX - 2), 0, clamp(a.pos.z + Mathf.Cos(a.aimYaw) * 16, B.minZ + 2, B.maxZ - 2));
            if (!a.isLocal || a.bot != null) { botTarget(a, st); st.autoT = 0.7f + random() * 0.6f; }
            a.character.trigger("throw");
        }
        public override void body(SpecialSystem sys, Actor a, SpecialState s, float dt)
        {
            sys.stand(a, dt);
            var st = (State)s.data;
            if (!st.aiming) return;
            if (st.autoT.HasValue && s.t >= st.autoT.Value) st.confirm = true;
            if (a.isLocal && a.bot == null) cursor(sys, a, s, st);
            if (st.confirm || s.t >= s.def.aimTime) launch(sys, a, s, st);
        }
        // the local player's target marker (stands in for the map cursor)
        static void cursor(SpecialSystem sys, Actor a, SpecialState s, State st)
        {
            if (st.cursor == null)
            {
                st.cursorMat = SpecialSystem.tintMat(a.color, 0.7f);
                st.cursor = SpecialSystem.part("strikeCursor", null, SpecialSystem.ringGeo(), st.cursorMat, false).transform;
                sys.add(st.cursor.gameObject);
            }
            var g = G.physics.raycast(new Vector3(st.target.x, 40, st.target.z), Vector3.down, 80, _hit, true);
            float y = g.hit ? g.point.y : 0;
            st.cursor.localPosition = new Vector3(st.target.x, y + 0.08f, st.target.z);
            st.cursor.localRotation = Quaternion.AngleAxis(s.t * 2 * Mathf.Rad2Deg, Vector3.up);
            st.cursor.localScale = Vector3.one * s.def.radius;
            Color c = a.color.linear;
            st.cursorMat.SetVector(SpecialSystem.ID_Color, new Vector4(c.r, c.g, c.b, 0.45f + 0.35f * Mathf.Abs(Mathf.Sin(s.t * 6))));
        }
        static void launch(SpecialSystem sys, Actor a, SpecialState s, State st)
        {
            st.aiming = false;
            var g = G.physics.raycast(new Vector3(st.target.x, 40, st.target.z), Vector3.down, 80, _hit, true);
            Vector3 to = g.hit ? g.point : new Vector3(st.target.x, 0, st.target.z);
            sys.world.Add(new Missile(sys, a, to));
            emit("special:launch", new Ev { actor = a, to = to });
            sys.end(a, "launch");
        }
        public override void end(SpecialSystem sys, Actor a, SpecialState s, string reason)
        {
            var st = (State)s.data;
            if (st.cursor != null) { sys.remove(st.cursor.gameObject); destroy(st.cursorMat); st.cursor = null; }
        }
        // bots: the thickest cluster of enemies, else the most enemy ink
        static readonly RegionStats _stats = new RegionStats();
        static void botTarget(Actor a, State st)
        {
            Vector3? best = null; int bs = 0;
            foreach (var e in G.actors)
            {
                if (e.team == a.team || !e.alive) continue;
                int n = 1; foreach (var o in G.actors) if (o != e && o.team == e.team && o.alive && Vector3.Distance(o.pos, e.pos) < 5) n++;
                if (n > bs) { bs = n; best = e.pos; }
            }
            var B = G.level.bounds;
            if (best == null || random() < 0.35f)
            {
                float bv = -1;
                // sample walkable ground (nav nodes) — on a skewed / jagged stage most of the bounds box is open water
                var nodes = G.nav != null && G.nav.nodes.Count > 0 ? G.nav.nodes : null;
                for (int k = 0; k < 24; k++)
                {
                    float x, z;
                    if (nodes != null) { var n = nodes[(int)(random() * nodes.Count)]; x = n.x; z = n.z; }
                    else { x = lerp(B.minX + 3, B.maxX - 3, random()); z = lerp(B.minZ + 3, B.maxZ - 3, random()); }
                    float y = G.level.groundHeight(x, z, 30); if (float.IsNegativeInfinity(y)) continue;
                    var rs = G.paint.regionStats(x, y, z, 4, a.team, _stats);
                    float v = rs.n > 0 ? rs.enemy * 1.5f + rs.empty : -1;
                    if (v > bv) { bv = v; best = new Vector3(x, y, z); }
                }
            }
            if (best.HasValue) st.target = new Vector3(best.Value.x, 0, best.Value.z);
        }
    }

    // the missile: climbs straight up and out of view, then drops onto the target; a pulsing ring marks where it lands
    public class Missile : SpecialWorld
    {
        readonly SpecialSystem sys; float t; readonly Vector3 from, to; readonly float flight;
        readonly Transform mesh, ring; readonly Material ringMat; float ringA; bool whistled;
        public Missile(SpecialSystem sys, Actor a, Vector3 to)
        {
            this.sys = sys; kind = "missile"; team = a.team; owner = a;
            from = new Vector3(a.pos.x, a.pos.y + 1.6f, a.pos.z); this.to = to;
            flight = Config.SPECIALS["strike"].flight;
            var d = SpecialProps.get("missile");
            if (d != null) mesh = SpecialSystem.propMesh(d, a.team, sys.glowMat(a.team));
            else   // (props not baked: an ink capsule)
            {
                mesh = SpecialSystem.part("missile", null, SpecialSystem.sphere(), CharacterMats.ink(a.color), true).transform;
                mesh.localScale = new Vector3(0.18f, 0.18f, 0.63f);
            }
            mesh.localPosition = from;
            ringMat = SpecialSystem.tintMat(a.color, 0.7f);
            ring = SpecialSystem.part("strikeRing", null, SpecialSystem.ringGeo(), ringMat, false).transform;
            ring.localPosition = new Vector3(to.x, to.y + 0.06f, to.z);
            ring.localScale = Vector3.one * Config.SPECIALS["strike"].radius;
            if (SpecialSystem.hearable(a)) SpecialSystem.play("strike_launch", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = 0.8f });
        }
        public override bool update(float dt)
        {
            t += dt;
            float k = t / flight;
            Vector3 p;
            if (k < 0.35f)
            {
                // climb straight up and out of view
                float u = k / 0.35f;
                p = new Vector3(from.x, from.y + u * u * 30, from.z);
                mesh.localRotation = SpecialSystem.lookRot(Vector3.up);
            }
            else
            {
                float u = (k - 0.35f) / 0.65f;
                p = new Vector3(to.x, to.y + 34 * (1 - u) * (1 - u) + 0.3f, to.z);
                mesh.localRotation = SpecialSystem.lookRot(Vector3.down);
                if (!whistled && SpecialSystem.near(to, 40)) { whistled = true; SpecialSystem.play("strike_whistle", new AudioOpts { pos = to, volume = 0.9f }); }
            }
            mesh.localPosition = p;
            if (t % 0.05f < dt && SpecialSystem.near(p, 45)) G.fx?.burst(p, Vector3.up, owner.color, new FxOpts { count = 2, speed = 1.5f, size = 0.08f, ring = false, mist = false });
            Color c = owner.color.linear;
            ringMat.SetVector(SpecialSystem.ID_Color, new Vector4(c.r, c.g, c.b, 0.35f + 0.35f * Mathf.Abs(Mathf.Sin(t * 9))));
            ringA += dt * 2;
            ring.localRotation = Quaternion.AngleAxis(ringA * Mathf.Rad2Deg, Vector3.up);
            if (k >= 1)
            {
                sys.world.Add(new Tornado(sys, owner, team, to));
                SpecialSystem.play("strike_impact", new AudioOpts { pos = to, volume = 1 });
                emit("shake", new Ev { pos = to, amount = 0.8f });
                return false;
            }
            return true;
        }
        public override void dispose() { destroy(mesh.gameObject); destroy(ring.gameObject); destroy(ringMat); }
    }

    // the vortex: two counter-scaled swirling funnels; paints its footprint, hurts and drags enemies inside
    public class Tornado : SpecialWorld
    {
        public readonly Vector3 pos; public readonly float radius;
        float t, paintT, spin; readonly float dur;
        readonly Transform group, inner, outer; readonly Material innerMat, outerMat; AudioLoop loop;
        public Tornado(SpecialSystem sys, Actor owner, int team, Vector3 pos)
        {
            var d = Config.SPECIALS["strike"];
            kind = "tornado"; this.owner = owner; this.team = team; this.pos = pos;
            dur = d.duration; radius = d.radius;
            Color col = G.teamColors[team];
            group = ThreeSpace.create("tornado").transform; group.localPosition = pos;
            innerMat = SpecialSystem.swirlMat(col, 1.9f, 0.25f, 1, 0.35f);
            outerMat = SpecialSystem.swirlMat(Color.Lerp(col, Color.white, 0.25f), 1.2f, 0.4f, 1.15f, 0.5f);
            inner = SpecialSystem.part("inner", group, SpecialSystem.funnelGeo(), innerMat, false).transform;
            outer = SpecialSystem.part("outer", group, SpecialSystem.funnelGeo(), outerMat, false).transform;
            inner.localScale = new Vector3(radius * 0.8f, 8, radius * 0.8f);
            outer.localScale = new Vector3(radius, 9, radius);
            loop = G.audio?.loop("tornado", new AudioOpts { pos = pos, volume = 0.8f });
            SpecialSystem.paint(owner, new Vector3(pos.x, pos.y + 0.4f, pos.z), radius * 0.7f, team);
            G.fx?.explosion(new Vector3(pos.x, pos.y + 0.6f, pos.z), col, radius * 0.8f);
            emit("special:strike", new Ev { actor = owner, pos = pos, radius = radius });
        }
        public override bool update(float dt)
        {
            var d = Config.SPECIALS["strike"];
            t += dt;
            float grow = Mathf.Min(1, t / 0.4f), fade = Mathf.Max(0, Mathf.Min(1, (dur - t) / 0.6f));
            foreach (var m in new[] { innerMat, outerMat }) { m.SetFloat(SpecialSystem.ID_Time, t); m.SetFloat(SpecialSystem.ID_Alpha, grow * fade); }
            spin += dt * 4;
            group.localRotation = Quaternion.AngleAxis(spin * Mathf.Rad2Deg, Vector3.up);
            group.localScale = new Vector3(1, 0.3f + 0.7f * grow, 1);
            // churn ink all over the footprint
            paintT -= dt;
            if (paintT <= 0)
            {
                paintT = 0.1f;
                for (int k = 0; k < 2; k++)
                {
                    float ang = random() * Mathf.PI * 2, r = Mathf.Sqrt(random()) * radius;
                    var g = SpecialSystem.groundBelow(new Vector3(pos.x + Mathf.Cos(ang) * r, pos.y + 3, pos.z + Mathf.Sin(ang) * r), 8);
                    if (g.HasValue) SpecialSystem.paint(owner, g.Value + Vector3.up * 0.2f, 0.9f + random() * 0.8f, team);
                }
                if (SpecialSystem.near(pos, 38))
                    G.fx?.burst(new Vector3(pos.x + (random() - 0.5f) * radius, pos.y + 0.4f + random() * 3, pos.z + (random() - 0.5f) * radius), Vector3.up, G.teamColors[team], new FxOpts { count = 3, speed = 5, size = 0.1f, ring = false, mist = false });
            }
            // hurt (and gently drag) enemies inside
            foreach (var e in G.actors.ToArray())
            {
                if (e.team == team || !e.alive) continue;
                float dx = pos.x - e.pos.x, dz = pos.z - e.pos.z, dh = Mathf.Sqrt(dx * dx + dz * dz);
                if (dh > radius || e.pos.y < pos.y - 1.5f || e.pos.y > pos.y + 7) continue;
                SpecialSystem.tickDamage(owner, e, d.dps * dt * fade, "strike");
                if (dh > 0.3f) { e.vel.x += (dx / dh) * d.pull * dt; e.vel.z += (dz / dh) * d.pull * dt; }
            }
            loop?.set(0.8f * fade, 1 + 0.2f * grow);
            return t < dur;
        }
        public override void dispose()
        {
            destroy(group.gameObject); destroy(innerMat); destroy(outerMat);
            loop?.stop(0.4f); loop = null;
        }
    }
}
