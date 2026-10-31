// Port of src/game/nav.js — navigation graph auto-generated from the level: 1 m grid of walkable samples on every
// floor/platform/ramp, connected by walk / jump-up / drop-down (+ ink-climb) edges. A* with a binary heap.
using System;
using System.Collections.Generic;
using System.Linq;
using Inkwave.World;
using UnityEngine;

namespace Inkwave
{
    public class NavEdge { public int to; public float cost; public string type; public Vector3 wallP; public Vector2 wallN; public float topY; }

    public class NavNode { public int id, ix, iz, zone = -1, wet; public float x, y, z; public bool overWater; public readonly List<NavEdge> nb = new List<NavEdge>(); public Vector3 pos => new Vector3(x, y, z); }

    public class NavGraph
    {
        public readonly Level level;
        readonly Physics physics;
        public readonly float step = 1.0f;
        public readonly List<NavNode> nodes = new List<NavNode>();
        public float x0, z0;
        public int nx, nz;
        public List<int>[] cells;
        public byte[] valid, exitable;
        public List<int> validIds;
        readonly Hit _bh = new Hit();

        public NavGraph(Level level, Physics physics)
        {
            this.level = level; this.physics = physics;
            build();
        }

        void build()
        {
            var L = level; var B = L.bounds; float st = step;
            x0 = B.minX + st / 2; z0 = B.minZ + st / 2;
            nx = Mathf.FloorToInt((B.maxX - B.minX) / st); nz = Mathf.FloorToInt((B.maxZ - B.minZ) / st);
            cells = new List<int>[nx * nz];
            var ids = new List<int>();
            for (int iz = 0; iz < nz; iz++)
                for (int ix = 0; ix < nx; ix++)
                {
                    float x = x0 + ix * st, z = z0 + iz * st;
                    var heights = new List<float>();
                    L.queryBlocks(x - 0.01f, z - 0.01f, x + 0.01f, z + 0.01f, ids);
                    foreach (int id in ids)
                    {
                        var b = L.blocks[id];
                        if (!b.solid || b.axes[1].y < 0.6f || b.roof || b.rail || b.noNav) continue;   // (roofs: off limits; rail tops / noNav walls)
                        var n = b.axes[1];
                        Vector3 top = b.center + n * b.half.y;
                        float y = top.y - (n.x * (x - top.x) + n.z * (z - top.z)) / n.y;
                        if (!L.pointInBlock(b, new Vector3(x, y - 0.02f, z), 0.001f)) continue;
                        if (heights.Any(h => Mathf.Abs(h - y) < 0.15f)) continue;
                        heights.Add(y);
                    }
                    var list = new List<int>();
                    foreach (float y in heights)
                    {
                        // headroom + clearance (not inside geometry, not hugging walls)
                        if (!clear(x, y, z)) continue;
                        var node = new NavNode { id = nodes.Count, x = x, y = y, z = z, ix = ix, iz = iz, zone = -1 };
                        for (int t = 0; t < 2; t++)
                        {
                            var pad = L.spawnPads[t];
                            if (Mathf.Sqrt((x - pad.x) * (x - pad.x) + (z - pad.z) * (z - pad.z)) < L.spawnBarrier + 0.6f && y > pad.y - 1) node.zone = t;
                        }
                        nodes.Add(node);
                        list.Add(node.id);
                    }
                    cells[iz * nx + ix] = list;
                }
            // water proximity: wet = 2 with open water within ~1.2 m, 1 within ~2.2 m (paths pay extra to hug such edges);
            // overWater = standing on a grate with water under it (a squid would drop through)
            foreach (var n in nodes)
            {
                n.wet = 0;
                foreach (var (rad, lvl) in new[] { (1.2f, 2), (2.2f, 1) })
                {
                    if (n.wet != 0) break;
                    for (int k = 0; k < 8; k++)
                    {
                        float a = (k / 8f) * Mathf.PI * 2;
                        if (float.IsNegativeInfinity(L.groundHeight(n.x + Mathf.Cos(a) * rad, n.z + Mathf.Sin(a) * rad, n.y + 0.6f))) { n.wet = lvl; break; }
                    }
                }
                n.overWater = float.IsNegativeInfinity(L.groundHeight(n.x, n.z, n.y + 0.3f, true));
            }
            // edges
            foreach (var n in nodes)
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int jx = n.ix + dx, jz = n.iz + dz;
                        if (jx < 0 || jz < 0 || jx >= nx || jz >= nz) continue;
                        foreach (int mid in cells[jz * nx + jx])
                        {
                            var m = nodes[mid];
                            float dy = m.y - n.y;
                            float flat = Mathf.Sqrt(dx * dx + dz * dz) * step;
                            if (Mathf.Abs(dy) <= 0.5f)
                            {
                                if (dx != 0 && dz != 0)
                                {   // diagonal: both orthogonal neighbours must exist at similar height
                                    if (!has(n.ix + dx, n.iz, n.y) || !has(n.ix, n.iz + dz, n.y)) continue;
                                }
                                if (blocked(n, m, Mathf.Max(n.y, m.y))) continue;   // a railing / thin wall between the two cells
                                n.nb.Add(new NavEdge { to = mid, cost = flat, type = "walk" });
                            }
                            else if (dy > 0.5f && dy <= 1.25f && !(dx != 0 && dz != 0))
                            {
                                if (blocked(n, m, m.y)) continue;                    // rail along the ledge above
                                n.nb.Add(new NavEdge { to = mid, cost = flat + 2.5f, type = "jump" });
                            }
                            else if (dy < -0.5f && dy >= -3.4f && !(dx != 0 && dz != 0) && openAbove(m, n.y))
                            {
                                if (blocked(n, m, n.y)) continue;                    // rail along the ledge you'd drop off
                                n.nb.Add(new NavEdge { to = mid, cost = flat + 0.8f, type = "drop" });
                            }
                        }
                    }
            climbEdges();
            // keep nodes connected to the spawn both ways (see prune)
            prune();
        }

        // is the walk between two neighbouring cells cut by something solid at waist height (grates and rails count)
        bool blocked(NavNode n, NavNode m, float y)
        {
            if (physics == null) return false;
            Vector3 p = new Vector3(n.x, y + 0.6f, n.z), d = new Vector3(m.x - n.x, 0, m.z - n.z);
            float len = d.magnitude; if (len < 1e-4f) return false;
            return physics.raycast(p, d / len, len, _bh, false).hit;
        }

        // a drop lands on m only if nothing solid hangs over it below the ledge it drops from
        bool openAbove(NavNode m, float fromY)
        {
            for (float y = m.y + 1.0f; y < fromY - 0.05f; y += 0.3f) if (level.pointInside(new Vector3(m.x, y, m.z), 0)) return false;
            return true;
        }

        bool has(int ix, int iz, float y)
        {
            if (ix < 0 || iz < 0 || ix >= nx || iz >= nz) return false;
            return cells[iz * nx + ix].Any(id => Mathf.Abs(nodes[id].y - y) <= 0.5f);
        }

        bool clear(float x, float y, float z)
        {
            float r = PLAYER.radius + 0.08f;
            // the body ring is tested from just above step-up height: the capsule rides that high (curbs below it are walked
            // onto), and a lower ring would dip into the slope itself on any ramp steeper than ~18°
            foreach (float h in new[] { 0.15f, 0.4f, 0.85f, 1.4f })
            {
                if (level.pointInside(new Vector3(x, y + h, z), 0)) return false;
                if (h < PLAYER.stepUp) continue;
                for (int k = 0; k < 8; k++)
                {
                    float a = (k / 8f) * Mathf.PI * 2;
                    if (level.pointInside(new Vector3(x + Mathf.Cos(a) * r, y + h, z + Mathf.Sin(a) * r), 0)) return false;
                }
            }
            return true;
        }

        void prune()
        {
            // Directed reachability from the team-0 spawn. valid = nodes you can reach from spawn AND walk/jump back from
            // (goals: never send a bot into a pit it can't leave); exitable = nodes that can get back to spawn.
            int N = nodes.Count;
            var radj = new List<int>[N];
            for (int i = 0; i < N; i++) radj[i] = new List<int>();
            foreach (var n in nodes) foreach (var e in n.nb) radj[e.to].Add(n.id);
            byte[] walk(int start, Func<int, IEnumerable<int>> next)
            {
                var seen = new byte[N]; var stack = new Stack<int>(); stack.Push(start); seen[start] = 1;
                while (stack.Count > 0) { int k = stack.Pop(); foreach (int j in next(k)) if (seen[j] == 0) { seen[j] = 1; stack.Push(j); } }
                return seen;
            }
            // seed = the node of the largest undirected region nearest the team-0 spawn pad (never a lone node on a prop top)
            IEnumerable<int> und(int k) => nodes[k].nb.Select(e => e.to).Concat(radj[k]);
            var comp = Enumerable.Repeat(-1, N).ToArray();
            int bestC = -1, bestSize = 0, c = 0;
            for (int i = 0; i < N; i++)
            {
                if (comp[i] >= 0) continue;
                var seen = walk(i, und); int size = 0;
                for (int k = 0; k < N; k++) if (seen[k] != 0) { comp[k] = c; size++; }
                if (size > bestSize) { bestSize = size; bestC = c; }
                c++;
            }
            var pad = level.spawnPads[0];
            int seed = -1; float sd = float.PositiveInfinity;
            foreach (var n in nodes)
            {
                if (comp[n.id] != bestC) continue;
                float d = (n.x - pad.x) * (n.x - pad.x) + (n.z - pad.z) * (n.z - pad.z) + ((n.y - pad.y) * 3) * ((n.y - pad.y) * 3);
                if (d < sd) { sd = d; seed = n.id; }
            }
            valid = new byte[N];
            if (seed < 0) { exitable = valid; validIds = new List<int>(); return; }
            var fwd = walk(seed, k => nodes[k].nb.Select(e => e.to));
            var back = walk(seed, k => radj[k]);
            exitable = back;
            int count = 0;
            for (int i = 0; i < N; i++) { valid[i] = (byte)(fwd[i] != 0 && back[i] != 0 ? 1 : 0); count += valid[i]; }
            // safety net: if the two-way set is implausibly small, fall back to the whole region rather than strand the bots
            if (count < bestSize * 0.5f) { for (int i = 0; i < N; i++) valid[i] = (byte)(comp[i] == bestC ? 1 : 0); exitable = valid; }
            validIds = nodes.Where(n => valid[n.id] != 0).Select(n => n.id).ToList();
        }

        // start = true also accepts nodes that are only a way *out* (see prune)
        public int nearest(Vector3 pos, float maxUp = 0.8f, bool start = false)
        {
            int ix = Mathf.RoundToInt((pos.x - x0) / step), iz = Mathf.RoundToInt((pos.z - z0) / step);
            int best = -1; float bd = float.PositiveInfinity;
            for (int r = 0; r <= 3; r++)
            {
                for (int dz = -r; dz <= r; dz++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != r) continue;
                        int jx = ix + dx, jz = iz + dz;
                        if (jx < 0 || jz < 0 || jx >= nx || jz >= nz) continue;
                        foreach (int id in cells[jz * nx + jx])
                        {
                            if (valid[id] == 0 && !(start && exitable[id] != 0)) continue;
                            var n = nodes[id];
                            if (n.y > pos.y + maxUp) continue;
                            float d = (n.x - pos.x) * (n.x - pos.x) + (n.z - pos.z) * (n.z - pos.z) + ((n.y - pos.y) * 2.5f) * ((n.y - pos.y) * 2.5f);
                            if (d < bd) { bd = d; best = id; }
                        }
                    }
                if (best >= 0) return best;
            }
            return best;
        }

        float[] _g; int[] _from; uint[] _seen, _closed; uint _stamp;
        readonly Heap heap = new Heap();

        // A* from node a to node b; `team` blocks the enemy spawn zone. Returns node ids (incl. a and b) or null.
        // noClimb: plan without climb edges (a bot that just failed a climb)
        public List<int> path(int a, int b, int team, int maxIter = 6000, bool noClimb = false)
        {
            if (a < 0 || b < 0) return null;
            int N = nodes.Count;
            if (_g == null || _g.Length != N) { _g = new float[N]; _from = new int[N]; _seen = new uint[N]; _closed = new uint[N]; _stamp = 0; }
            var g = _g; var from = _from; var seen = _seen; var closed = _closed;
            uint st = ++_stamp;
            var goal = nodes[b];
            float h(NavNode n) => Mathf.Sqrt((n.x - goal.x) * (n.x - goal.x) + (n.z - goal.z) * (n.z - goal.z)) + Mathf.Abs(n.y - goal.y) * 0.5f;
            heap.clear();
            g[a] = 0; from[a] = -1; seen[a] = st;
            heap.push(a, h(nodes[a]));
            int it = 0;
            while (heap.size > 0 && it++ < maxIter)
            {
                int cur = heap.pop();
                if (cur == b) break;
                if (closed[cur] == st) continue;
                closed[cur] = st;
                var n = nodes[cur];
                foreach (var e in n.nb)
                {
                    if (noClimb && e.type == "climb") continue;
                    var m = nodes[e.to];
                    if (m.zone >= 0 && m.zone != team) continue;
                    float ng = g[cur] + e.cost + (m.wet == 2 ? 2.0f : m.wet == 1 ? 0.5f : 0);
                    if (seen[e.to] != st || ng < g[e.to])
                    {
                        seen[e.to] = st; g[e.to] = ng; from[e.to] = cur;
                        heap.push(e.to, ng + h(m));
                    }
                }
            }
            if (seen[b] != st) return null;
            var outL = new List<int>();
            for (int k = b; k != -1; k = from[k]) { outL.Add(k); if (outL.Count > 4000) break; }
            outL.Reverse();
            return outL;
        }

        public string edgeType(int a, int b)
        {
            foreach (var e in nodes[a].nb) if (e.to == b) return e.type;
            return "walk";
        }
        public NavEdge edge(int a, int b)
        {
            foreach (var e in nodes[a].nb) if (e.to == b) return e;
            return null;
        }

        // Climb edges: an inkable wall right in front of a node, whose block top holds a node 1.3–5.5 m higher. Bots
        // ink the wall up to the top and swim up it (the same squid wall-climb players use).
        void climbEdges()
        {
            var L = level; var P = physics; var hit = new Hit();
            foreach (var n in nodes)
            {
                var seenTop = new HashSet<int>();
                for (int k = 0; k < 8; k++)
                {
                    float a = (k / 8f) * Mathf.PI * 2;
                    Vector3 d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                    Vector3 o = new Vector3(n.x, n.y + 0.35f, n.z);
                    if (!P.raycast(o, d, 1.3f, hit, true).hit) continue;
                    if (Mathf.Abs(hit.normal.y) > 0.3f || d.x * hit.normal.x + d.z * hit.normal.z > -0.7f) continue;   // wall, faced head-on
                    var f = hit.face >= 0 ? L.faces[hit.face] : null;
                    if (f == null || f.atlas == null) continue;                                                     // must take ink
                    var b = L.blocks[f.block];
                    if (b.grate || b.hidden) continue;
                    float topY = b.center.y + b.axes[1].y * b.half.y;
                    if (topY - n.y < 1.3f || topY - n.y > 5.5f) continue;
                    // node on top, just past the wall
                    float qx = hit.point.x - hit.normal.x * 0.9f, qz = hit.point.z - hit.normal.z * 0.9f;
                    int ix = Mathf.RoundToInt((qx - x0) / step), iz = Mathf.RoundToInt((qz - z0) / step);
                    if (ix < 0 || iz < 0 || ix >= nx || iz >= nz) continue;
                    int top = -1;
                    foreach (int id in cells[iz * nx + ix]) if (Mathf.Abs(nodes[id].y - topY) < 0.25f) top = id;
                    if (top < 0 || seenTop.Contains(top)) continue;
                    seenTop.Add(top);
                    var m = nodes[top]; float rise = m.y - n.y;
                    n.nb.Add(new NavEdge
                    {
                        to = top, cost = Mathf.Sqrt((m.x - n.x) * (m.x - n.x) + (m.z - n.z) * (m.z - n.z)) + 3.5f + rise * 1.5f, type = "climb",
                        wallP = hit.point, wallN = new Vector2(hit.normal.x, hit.normal.z), topY = topY,
                    });
                }
            }
        }

        class Heap
        {
            readonly List<int> ids = new List<int>(); readonly List<float> pr = new List<float>();
            public int size => ids.Count;
            public void clear() { ids.Clear(); pr.Clear(); }
            public void push(int id, float p)
            {
                int i = ids.Count; ids.Add(id); pr.Add(p);
                while (i > 0) { int j = (i - 1) >> 1; if (pr[j] <= p) break; ids[i] = ids[j]; pr[i] = pr[j]; i = j; }
                ids[i] = id; pr[i] = p;
            }
            public int pop()
            {
                int top = ids[0];
                int lid = ids[ids.Count - 1]; float lp = pr[pr.Count - 1];
                ids.RemoveAt(ids.Count - 1); pr.RemoveAt(pr.Count - 1);
                if (ids.Count > 0)
                {
                    int i = 0, n = ids.Count;
                    while (true)
                    {
                        int l = i * 2 + 1, r = l + 1, m = i;
                        float mp = lp;
                        if (l < n && pr[l] < mp) { m = l; mp = pr[l]; }
                        if (r < n && pr[r] < mp) { m = r; mp = pr[r]; }
                        if (m == i) break;
                        ids[i] = ids[m]; pr[i] = pr[m]; i = m;
                    }
                    ids[i] = lid; pr[i] = lp;
                }
                return top;
            }
        }
    }
}
