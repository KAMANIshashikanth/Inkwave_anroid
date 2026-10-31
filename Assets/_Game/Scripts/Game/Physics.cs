// Port of src/game/physics.js — collision queries against the level's oriented boxes: raycasts, capsule resolution,
// point/segment helpers, plus the character-controller queries (flat-footprint ground probe, lifted body capsule,
// camera probe). Pure three-space maths; Unity's PhysX is not involved.
using System.Collections.Generic;
using Inkwave.World;
using UnityEngine;

namespace Inkwave
{
    public static partial class G
    {
        public static Level level;
        public static Physics physics;
    }

    public class Hit
    {
        public bool hit; public float dist; public Vector3 point, normal; public int block = -1, face = -1; public float u, v;
    }

    // Result of Physics.groundProbe (feet support under a character).
    public class GroundHit
    {
        public bool hit; public float y; public Vector3 normal = Vector3.up; public int block = -1, face = -1; public float u, v; public bool center, grate;
    }

    public class Contacts
    {
        public bool ground, wall, ceiling; public Vector3 groundNormal = Vector3.up, wallNormal; public int groundBlock = -1, wallBlock = -1;
    }

    public class CameraProbeResult { public float hard, soft; public bool floor; }

    public class Physics
    {
        public const float WALKABLE = 0.68f;   // min ground normal.y a character can stand on (≈47°)
        static readonly Vector3 DOWNV = new Vector3(0, -1, 0);
        // footprint ring (unit directions) for the ground probe: 8 samples so a ledge edge is caught at any heading
        static readonly Vector2[] RING = new Vector2[8];
        // camera probe rays: [side, up, weight] in units of the probe radius (centre is the hard line of sight)
        static readonly List<Vector3> CAM_RAYS = new List<Vector3> { new Vector3(0, 0, 1) };

        static Physics()
        {
            for (int i = 0; i < 8; i++) RING[i] = new Vector2(Mathf.Cos((i / 8f) * Mathf.PI * 2), Mathf.Sin((i / 8f) * Mathf.PI * 2));
            for (int i = 0; i < 6; i++) { float a = (i / 6f) * Mathf.PI * 2; CAM_RAYS.Add(new Vector3(Mathf.Cos(a) * 0.5f, Mathf.Sin(a) * 0.5f, 0.75f)); }
            for (int i = 0; i < 8; i++) { float a = (i / 8f) * Mathf.PI * 2 + 0.39f; CAM_RAYS.Add(new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0.4f)); }
        }

        public readonly Level level;
        readonly List<int> _ids = new List<int>();
        readonly Hit _losHit = new Hit(), _gh = new Hit(), _gh2 = new Hit(), _camHit = new Hit();

        public Physics(Level level) { this.level = level; }

        // Ray vs all blocks. dir must be normalized. Ignores blocks containing the origin.
        // skipGrates: ink, shots, droplets and the camera pass straight through grates
        public Hit raycast(Vector3 origin, Vector3 dir, float maxDist, Hit o = null, bool skipGrates = false)
        {
            o ??= new Hit();
            o.hit = false; o.dist = maxDist; o.block = -1; o.face = -1;
            float ex = origin.x + dir.x * maxDist, ez = origin.z + dir.z * maxDist;
            var ids = level.queryBlocks(Mathf.Min(origin.x, ex), Mathf.Min(origin.z, ez), Mathf.Max(origin.x, ex), Mathf.Max(origin.z, ez), _ids);
            var blocks = level.blocks;
            float best = maxDist; int bestK = -1, bestSign = 0, bestB = -1;
            for (int i = 0; i < ids.Count; i++)
            {
                var b = blocks[ids[i]];
                if (!b.solid || (skipGrates && b.grate)) continue;
                Vector3 ob = origin - b.center;
                float tmin = float.NegativeInfinity, tmax = float.PositiveInfinity; int kmin = -1, smin = 0; bool miss = false;
                for (int k = 0; k < 3; k++)
                {
                    Vector3 ax = b.axes[k];
                    float oo = Vector3.Dot(ob, ax), d = Vector3.Dot(dir, ax), h = b.halfAt(k);
                    if (Mathf.Abs(d) < 1e-9f) { if (oo < -h || oo > h) { miss = true; break; } continue; }
                    float t1 = (-h - oo) / d, t2 = (h - oo) / d; int s1 = -1;
                    if (t1 > t2) { float t = t1; t1 = t2; t2 = t; s1 = 1; }
                    if (t1 > tmin) { tmin = t1; kmin = k; smin = s1; }
                    if (t2 < tmax) tmax = t2;
                    if (tmin > tmax) { miss = true; break; }
                }
                if (miss || tmax < 0 || tmin < 0 || tmin > best) continue;
                best = tmin; bestK = kmin; bestSign = smin; bestB = b.id;
            }
            if (bestB < 0) return o;
            var bb = blocks[bestB];
            o.hit = true; o.dist = best; o.block = bestB;
            o.point = origin + dir * best;
            o.normal = bb.axes[bestK] * bestSign;
            o.face = bb.faces[bestK * 2 + (bestSign > 0 ? 0 : 1)];
            if (o.face >= 0)
            {
                var f = level.faces[o.face];
                Vector3 p = o.point - f.origin;
                o.u = Vector3.Dot(p, f.u); o.v = Vector3.Dot(p, f.v);
            }
            return o;
        }

        public Hit segment(Vector3 a, Vector3 b, Hit o = null, bool skipGrates = false)
        {
            o ??= new Hit();
            Vector3 d = b - a;
            float len = d.magnitude;
            if (len < 1e-6f) { o.hit = false; return o; }
            return raycast(a, d / len, len, o, skipGrates);
        }

        // Line of sight between two points (true = clear).
        public bool los(Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float len = ab.magnitude;
            if (len < 1e-4f) return true;
            return !raycast(a, ab / len, len - 0.05f, _losHit, true).hit;
        }

        public Vector3 closestOnBlock(Block b, Vector3 p)
        {
            Vector3 ob = p - b.center;
            Vector3 o = b.center;
            for (int k = 0; k < 3; k++)
            {
                Vector3 ax = b.axes[k]; float h = b.halfAt(k);
                float d = Vector3.Dot(ob, ax);
                if (d > h) d = h; else if (d < -h) d = -h;
                o += ax * d;
            }
            return o;
        }

        // capsule segment a→b vs block: closest segment point _s, closest block point _q (3 alternating projections)
        void segBlock(Block b, Vector3 a, Vector3 bTop, out Vector3 s, out Vector3 q)
        {
            Vector3 ab = bTop - a;
            float abLen2 = Mathf.Max(1e-6f, ab.sqrMagnitude);
            float t = 0.5f;
            for (int k = 0; k < 3; k++)
            {
                s = a + ab * t;
                q = closestOnBlock(b, s);
                t = Mathf.Min(1, Mathf.Max(0, Vector3.Dot(q - a, ab) / abLen2));
            }
            s = a + ab * t;
            q = closestOnBlock(b, s);
        }

        // segment point inside the box: push out along the axis of least penetration
        static float leastPen(Block b, Vector3 s, out Vector3 n)
        {
            Vector3 ob = s - b.center;
            float bestPen = float.PositiveInfinity; n = Vector3.up;
            for (int k = 0; k < 3; k++)
            {
                Vector3 ax = b.axes[k]; float h = b.halfAt(k);
                float d = Vector3.Dot(ob, ax), pk = h - Mathf.Abs(d);
                if (pk < bestPen) { bestPen = pk; n = ax * (d >= 0 ? 1 : -1); }
            }
            return bestPen;
        }

        // Resolve a vertical capsule (feet at pos) against the level. Mutates pos. Fills contact info in `c`.
        // squid = true: grates don't collide (squids slip through the mesh)
        public Contacts collideCapsule(ref Vector3 pos, float radius, float height, Contacts c, int iterations = 3, bool squid = false)
        {
            c.ground = false; c.wall = false; c.ceiling = false;
            c.groundNormal = Vector3.up; c.wallNormal = Vector3.zero; c.groundBlock = -1; c.wallBlock = -1;
            var blocks = level.blocks;
            float top = Mathf.Max(radius, height - radius);
            for (int it = 0; it < iterations; it++)
            {
                var ids = level.queryBlocks(pos.x - radius - 0.2f, pos.z - radius - 0.2f, pos.x + radius + 0.2f, pos.z + radius + 0.2f, _ids);
                bool moved = false;
                for (int i = 0; i < ids.Count; i++)
                {
                    var b = blocks[ids[i]];
                    if (!b.solid || (squid && b.grate)) continue;
                    if (pos.y + height < b.aabbMin.y - 0.05f || pos.y > b.aabbMax.y + 0.05f) continue;
                    segBlock(b, new Vector3(pos.x, pos.y + radius, pos.z), new Vector3(pos.x, pos.y + top, pos.z), out var s, out var q);
                    Vector3 n = s - q;
                    float dist = n.magnitude, pen;
                    if (dist > 1e-5f)
                    {
                        if (dist >= radius) continue;
                        n /= dist;
                        pen = radius - dist;
                    }
                    else pen = leastPen(b, s, out n) + radius;
                    // prefer resolving as ground when standing on top edges (avoid being shoved sideways off ledges)
                    pos += n * (pen + 1e-4f);
                    moved = true;
                    if (n.y > 0.6f) { c.ground = true; c.groundNormal = n; c.groundBlock = b.id; }
                    else if (n.y < -0.6f) c.ceiling = true;
                    else if (Mathf.Abs(n.y) < 0.55f) { c.wall = true; c.wallNormal = n; c.wallBlock = b.id; }
                }
                if (!moved) break;
            }
            return c;
        }

        // Flat-footprint ground probe — the character's feet. Vertical rays at the centre and on a ring of radius `foot`
        // cast from `up` above the feet (y) to `down` below them. The centre surface wins when it is walkable and in range,
        // so slopes are exact (no hovering, no sphere-on-plane bounce); ring samples take over when the centre is over a
        // gap (feet stay planted on a ledge until the whole footprint is off) or when one of them sits on a clear step
        // (> stepMin above the centre: curbs/lips are stepped onto as soon as the foot reaches them).
        public GroundHit groundProbe(float x, float y, float z, float up, float down, float foot, GroundHit o, bool skipGrates = false, float stepMin = 0.12f)
        {
            o.hit = false; o.center = false;
            var h = _gh;
            float len = up + down;
            float cy = float.NegativeInfinity; bool have = false;
            // centre
            raycast(new Vector3(x, y + up, z), DOWNV, len, h, skipGrates);
            if (h.hit && h.normal.y >= WALKABLE)
            {
                have = true; cy = h.point.y;
                fillGround(o, h, true);
            }
            // ring: best clear step above the centre, or (centre missing) the highest walkable sample
            float by = have ? cy + stepMin : float.NegativeInfinity; int bi = -1;
            var hr = _gh2;
            for (int i = 0; i < RING.Length; i++)
            {
                raycast(new Vector3(x + RING[i].x * foot, y + up, z + RING[i].y * foot), DOWNV, len, hr, skipGrates);
                if (!hr.hit || hr.normal.y < WALKABLE) continue;
                if (hr.point.y > by) { by = hr.point.y; bi = i; fillGround(o, hr, false); }
            }
            o.hit = have || bi >= 0;
            return o;
        }

        void fillGround(GroundHit o, Hit h, bool center)
        {
            o.y = h.point.y; o.normal = h.normal; o.block = h.block; o.face = h.face; o.u = h.u; o.v = h.v;
            o.center = center; o.grate = h.block >= 0 && level.blocks[h.block].grate;
        }

        // Resolve the character BODY: a vertical capsule whose bottom is lifted `lift` above the feet (everything below the
        // lift is the feet's job — step-ups, curbs). `horizontal` (grounded) turns push-outs into horizontal pushes so walls
        // never shove a grounded character up or down (no fighting with the ground snap). Mutates pos, fills contacts `c`.
        public Contacts collideBody(ref Vector3 pos, float radius, float lift, float height, Contacts c, bool horizontal = false, bool skipGrates = false, int iterations = 3)
        {
            c.ground = false; c.wall = false; c.ceiling = false;
            c.groundNormal = Vector3.up; c.wallNormal = Vector3.zero; c.groundBlock = -1; c.wallBlock = -1;
            var blocks = level.blocks;
            float bot = lift + radius, top = Mathf.Max(bot, height - radius);
            for (int it = 0; it < iterations; it++)
            {
                var ids = level.queryBlocks(pos.x - radius - 0.2f, pos.z - radius - 0.2f, pos.x + radius + 0.2f, pos.z + radius + 0.2f, _ids);
                bool moved = false;
                for (int i = 0; i < ids.Count; i++)
                {
                    var b = blocks[ids[i]];
                    if (!b.solid || (skipGrates && b.grate)) continue;
                    if (pos.y + height < b.aabbMin.y - 0.05f || pos.y + lift > b.aabbMax.y + 0.05f) continue;
                    segBlock(b, new Vector3(pos.x, pos.y + bot, pos.z), new Vector3(pos.x, pos.y + top, pos.z), out var s, out var q);
                    Vector3 n = s - q;
                    float dist = n.magnitude, pen;
                    if (dist > 1e-5f)
                    {
                        if (dist >= radius) continue;
                        n /= dist;
                        pen = radius - dist;
                    }
                    else pen = leastPen(b, s, out n) + radius;
                    float hl = Mathf.Sqrt(n.x * n.x + n.z * n.z);
                    if (horizontal && hl > 0.3f && n.y > -0.6f)
                    {
                        // grounded: push straight out sideways by the horizontal distance that clears the penetration
                        float push = Mathf.Min(0.45f, pen / hl) + 1e-4f;
                        pos.x += (n.x / hl) * push; pos.z += (n.z / hl) * push;
                        c.wall = true; c.wallNormal = new Vector3(n.x / hl, 0, n.z / hl); c.wallBlock = b.id;
                    }
                    else
                    {
                        pos += n * (pen + 1e-4f);
                        if (n.y > 0.6f) { c.ground = true; c.groundNormal = n; c.groundBlock = b.id; }
                        else if (n.y < -0.6f) c.ceiling = true;
                        else if (Mathf.Abs(n.y) < 0.6f) { c.wall = true; c.wallNormal = n; c.wallBlock = b.id; }
                    }
                    moved = true;
                }
                if (!moved) break;
            }
            return c;
        }

        // Would a body capsule (same shape as collideBody) at `pos` overlap solid geometry? (step-up / ledge checks)
        public bool bodyFits(Vector3 pos, float radius, float lift, float height, bool skipGrates = false, float margin = 0.01f)
        {
            var blocks = level.blocks;
            float bot = lift + radius, top = Mathf.Max(bot, height - radius);
            var ids = level.queryBlocks(pos.x - radius - 0.1f, pos.z - radius - 0.1f, pos.x + radius + 0.1f, pos.z + radius + 0.1f, _ids);
            for (int i = 0; i < ids.Count; i++)
            {
                var b = blocks[ids[i]];
                if (!b.solid || (skipGrates && b.grate)) continue;
                if (pos.y + height < b.aabbMin.y || pos.y + lift > b.aabbMax.y) continue;
                segBlock(b, new Vector3(pos.x, pos.y + bot, pos.z), new Vector3(pos.x, pos.y + top, pos.z), out var s, out var q);
                if ((s - q).sqrMagnitude < (radius - margin) * (radius - margin)) return false;
            }
            return true;
        }

        // Soft camera probe ("sphere-cast feel"): a cylinder of rays parallel to the boom — the centre plus two rings
        // (radius 0.5·R and R). Each ray's hit limits the boom length; off-axis rays only partially (weight w), so an
        // obstacle sweeping toward the line of sight starts pulling the camera in several frames before it crosses it.
        // `back` = unit boom direction (pivot → lens). hard = centre-ray limit, soft = blended limit (≤ hard),
        // floor = true when the centre ray hit an upward-facing surface (never go under).
        public CameraProbeResult cameraProbe(Vector3 pivot, Vector3 back, float want, float radius, CameraProbeResult o, float pad = 0.3f)
        {
            var h = _camHit;
            Vector3 a = Mathf.Abs(back.y) > 0.95f ? new Vector3(1, 0, 0) : new Vector3(0, 1, 0);
            Vector3 side = Vector3.Cross(back, a).normalized;
            Vector3 upish = Vector3.Cross(side, back).normalized;
            float hard = want, soft = want;
            o.floor = false;
            for (int i = 0; i < CAM_RAYS.Count; i++)
            {
                float ox = CAM_RAYS[i].x, oy = CAM_RAYS[i].y, w = CAM_RAYS[i].z;
                Vector3 s = pivot + side * (ox * radius) + upish * (oy * radius);
                raycast(s, back, want + pad, h, true);
                if (!h.hit) continue;
                float lim = Mathf.Max(0, h.dist - pad);
                if (w >= 1) { if (lim < hard) { hard = lim; o.floor = h.normal.y > 0.6f; } }
                float blended = lim + (want - lim) * (1 - w);
                if (blended < soft) soft = blended;
            }
            o.hard = hard; o.soft = Mathf.Min(soft, hard);
            return o;
        }

        // Distance from point p to the vertical capsule (feet at base).
        public static float pointCapsuleDist(Vector3 p, Vector3 bas, float radius, float height)
        {
            float y = Mathf.Min(Mathf.Max(p.y, bas.y + radius), bas.y + Mathf.Max(radius, height - radius));
            float dx = p.x - bas.x, dy = p.y - y, dz = p.z - bas.z;
            return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        // Closest approach of segment a→b to the vertical capsule; returns distance and t (0..1 along segment).
        public static (float dist, float t) segmentCapsuleDist(Vector3 a, Vector3 b, Vector3 bas, float radius, float height)
        {
            // sample-based closest point (short segments; robust enough for projectiles)
            float best = float.PositiveInfinity, bt = 0;
            const int steps = 6;
            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                float d = pointCapsuleDist(Vector3.Lerp(a, b, t), bas, radius, height);
                if (d < best) { best = d; bt = t; }
            }
            // refine
            float lo = Mathf.Max(0, bt - 1f / steps), hi = Mathf.Min(1, bt + 1f / steps);
            for (int k = 0; k < 8; k++)
            {
                float m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
                float d1 = pointCapsuleDist(Vector3.Lerp(a, b, m1), bas, radius, height);
                float d2 = pointCapsuleDist(Vector3.Lerp(a, b, m2), bas, radius, height);
                if (d1 < d2) hi = m2; else lo = m1;
            }
            float tt = (lo + hi) / 2;
            return (pointCapsuleDist(Vector3.Lerp(a, b, tt), bas, radius, height), tt);
        }
    }
}
