// Port of src/world/level.js — turns a map layout (oriented boxes) into collision blocks, paintable faces and render
// geometry.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Inkwave.World
{
    public class Block
    {
        public int id;
        public Vector3 center, half;
        public Vector3[] axes = { new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1) };
        public Vector3 aabbMin, aabbMax;
        public bool paint, solid, grate, roof, perch, noNav, rail, hidden, aligned = true, render;
        public Color color;
        public int pattern;
        public string tag;
        public List<MuralRef> mural;
        public List<Vector3> noPaint;
        public float? bevel;
        public int[] faces = { -1, -1, -1, -1, -1, -1 };
        public float halfAt(int k) => k == 0 ? half.x : k == 1 ? half.y : half.z;
    }

    public class AtlasRect { public float x, y, ppm, pad; }

    public class Face
    {
        public int id, block;
        public Vector3 n, u, v, origin;
        public float su, sv;
        public bool wall, turf, ceiling, paintable, groundedBottom;
        public int pattern, mural = -1;
        public Color color;
        public AtlasRect atlas, light;
        public int grid = -1, nu, nv;
        public float cu, cv;   // gameplay grid cell size on this face (paint)
    }

    public partial class Level
    {
        public readonly LayoutDef layout;
        public readonly StageBounds bounds;
        public readonly Vector3[] spawnPads;
        public readonly float spawnBarrier;
        public readonly List<Block> blocks = new List<Block>();
        public readonly List<Face> faces = new List<Face>();
        public bool hasRails;
        public int renderTris;
        public float lightSize = 1, lightUsed;
        public string layoutHash;
        readonly List<ExtraCollider> extra;
        uint _stamp = 1;

        // extra: collision-only boxes from set dressing — solid, un-inkable, never rendered (the prop mesh is)
        public class ExtraCollider { public Vector3 min, max, center, size; public float rotY; public bool obox, roof, rail, perch; }

        public Level(LayoutDef layout, List<ExtraCollider> extra = null)
        {
            this.extra = extra ?? new List<ExtraCollider>();
            this.layout = layout;
            bounds = layout.bounds;
            spawnPads = layout.spawnPads.ToArray();
            spawnBarrier = layout.spawnBarrier;
            build();
        }

        // ---------------------------------------------------------------- blocks
        void build()
        {
            var L = layout;
            var defs = new List<BlockDef>();
            defs.AddRange(L.single);
            defs.AddRange(L.half);
            defs.AddRange(L.half.Select(mirrorDef));
            foreach (var c in extra)
            {
                defs.Add(c.obox
                    ? new BlockDef { kind = "obox", center = c.center, size = c.size, rotY = c.rotY, paint = false, hidden = true, color = "#888888", roof = c.roof, rail = c.rail, perch = c.perch }
                    : new BlockDef { kind = "box", min = c.min, max = c.max, paint = false, hidden = true, color = "#888888", roof = c.roof, rail = c.rail, perch = c.perch });
            }
            foreach (var d in defs) addBlock(d);
            hasRails = blocks.Any(b => b.rail);   // (actors only run the railing foot check where there are rails)
            buildHash();
            foreach (var b in blocks) buildFaces(b);
        }

        void addBlock(BlockDef d)
        {
            bool grate = d.grate == true, rail = d.rail == true;
            var b = new Block
            {
                id = blocks.Count,
                paint = d.paint != false && !grate && !rail, solid = d.solid != false,
                color = Ctx.hex(d.color ?? "#dddddd"),
                pattern = d.pattern ?? PATTERN.plain,
                tag = d.tag,
                mural = d.mural,
                noPaint = d.noPaint,
                grate = grate || rail,          // walkable for kids, squids + ink + shots pass through, never inkable
                roof = d.roof == true,          // off-limits top (roofs, crane legs …): never inkable, anyone landing on it slides off
                perch = d.perch == true,        // a top you can stand on (overhead steel reached by a super jump) but never ink
                noNav = d.noNav == true,        // no route runs along its top (narrow walls a bot would only fall off)
                // rail: railings / fences — kids can't walk through (but can hop up and stand on top), shots + ink + squids
                // pass. Collision-only (the railing you see is a prop): a grate block that is never drawn or inked
                rail = rail,
                hidden = d.hidden == true || rail,   // collision-only (prop colliders, rails)
                render = d.render == true,
                bevel = d.bevel,
            };
            if (d.kind == "box")
            {
                b.center = (d.min + d.max) / 2;
                b.half = (d.max - d.min) / 2;
            }
            else if (d.kind == "obox")
            {
                // box turned about the vertical axis by rotY degrees (a stage laid out at an angle, turned props)
                float a = d.rotY * Mathf.PI / 180, c = Mathf.Cos(a), s = Mathf.Sin(a);
                b.center = d.center;
                b.half = d.size / 2;
                b.axes = new[] { new Vector3(c, 0, -s), new Vector3(0, 1, 0), new Vector3(s, 0, c) };
                b.aligned = false;
            }
            else
            {
                // Ramp: a thick tilted slab whose top surface runs from low → high and whose underside reaches the floor.
                Vector3 L0 = d.low, H0 = d.high;
                Vector3 s = H0 - L0; float len = s.magnitude; s.Normalize();
                Vector3 flat = new Vector3(s.x, 0, s.z).normalized;
                Vector3 side = Vector3.Cross(Vector3.up, flat).normalized;
                Vector3 n = Vector3.Cross(s, side).normalized;
                if (n.y < 0) { n = -n; side = -side; }
                float cosT = n.y;
                float rise = H0.y - L0.y;
                // thin: a free-spanning plank/gangway (keeps its own thickness instead of reaching down to the floor)
                float thick = d.thin ? d.thickness : Mathf.Max(d.thickness, rise * cosT + 0.35f);
                const float ext = 0.6f;   // extend under the floor at the low end
                Vector3 a = L0 - s * ext;
                Vector3 topMid = (a + H0) * 0.5f;
                b.center = topMid - n * (thick / 2);
                b.half = new Vector3(d.width / 2, thick / 2, (len + ext) / 2);
                b.axes = new[] { side, n, s };
                b.aligned = false;
                // keep right-handed: side x n should equal s
                if (Vector3.Dot(Vector3.Cross(side, n), s) < 0) b.axes[0] = -b.axes[0];
            }
            // AABB
            b.aabbMin = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            b.aabbMax = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            for (int i = 0; i < 8; i++)
            {
                Vector3 p = b.center
                    + b.axes[0] * ((i & 1) != 0 ? 1 : -1) * b.half.x
                    + b.axes[1] * ((i & 2) != 0 ? 1 : -1) * b.half.y
                    + b.axes[2] * ((i & 4) != 0 ? 1 : -1) * b.half.z;
                b.aabbMin = Vector3.Min(b.aabbMin, p); b.aabbMax = Vector3.Max(b.aabbMax, p);
            }
            blocks.Add(b);
        }

        // 2D spatial hash over XZ for broadphase
        float hashCell, hx0, hz0;
        int hw, hd;
        List<int>[] hash;
        uint[] blockStamp;

        void buildHash()
        {
            float cs = hashCell = 4;
            var bx = bounds;
            hx0 = bx.minX - 8; hz0 = bx.minZ - 8;
            hw = Mathf.CeilToInt((bx.maxX - bx.minX + 16) / cs);
            hd = Mathf.CeilToInt((bx.maxZ - bx.minZ + 16) / cs);
            hash = new List<int>[hw * hd];
            for (int i = 0; i < hash.Length; i++) hash[i] = new List<int>();
            blockStamp = new uint[blocks.Count];
            foreach (var b in blocks)
            {
                int x0 = hxi(b.aabbMin.x), x1 = hxi(b.aabbMax.x);
                int z0 = hzi(b.aabbMin.z), z1 = hzi(b.aabbMax.z);
                for (int z = z0; z <= z1; z++) for (int x = x0; x <= x1; x++) hash[z * hw + x].Add(b.id);
            }
        }
        int hxi(float x) => Math.Max(0, Math.Min(hw - 1, Mathf.FloorToInt((x - hx0) / hashCell)));
        int hzi(float z) => Math.Max(0, Math.Min(hd - 1, Mathf.FloorToInt((z - hz0) / hashCell)));

        // Unique block ids whose hash cells overlap the XZ rectangle.
        public List<int> queryBlocks(float minX, float minZ, float maxX, float maxZ, List<int> outL)
        {
            outL.Clear();
            uint st = ++_stamp;
            int x0 = hxi(minX), x1 = hxi(maxX), z0 = hzi(minZ), z1 = hzi(maxZ);
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                {
                    var cell = hash[z * hw + x];
                    for (int i = 0; i < cell.Count; i++)
                    {
                        int id = cell[i];
                        if (blockStamp[id] != st) { blockStamp[id] = st; outL.Add(id); }
                    }
                }
            return outL;
        }

        public bool pointInBlock(Block b, Vector3 p, float pad = 0)
        {
            Vector3 d = p - b.center;
            return Mathf.Abs(Vector3.Dot(d, b.axes[0])) < b.half.x + pad &&
                   Mathf.Abs(Vector3.Dot(d, b.axes[1])) < b.half.y + pad &&
                   Mathf.Abs(Vector3.Dot(d, b.axes[2])) < b.half.z + pad;
        }

        readonly List<int> _qtmp = new List<int>(), _qtmp2 = new List<int>();

        public bool pointInside(Vector3 p, float pad = 0, int exclude = -1)
        {
            var ids = queryBlocks(p.x - 0.01f, p.z - 0.01f, p.x + 0.01f, p.z + 0.01f, _qtmp);
            foreach (int id in ids)
            {
                if (id == exclude) continue;
                var b = blocks[id];
                if (!b.solid) continue;
                if (pointInBlock(b, p, pad)) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- faces
        void buildFaces(Block b)
        {
            if (b.hidden) return;
            var ax = b.axes;
            float[] h = { b.half.x, b.half.y, b.half.z };
            for (int k = 0; k < 3; k++)
            {
                foreach (int sign in new[] { 1, -1 })
                {
                    Vector3 n = ax[k] * sign;
                    if (n.y < -0.5f)
                    {
                        // underside: only keep if it floats well above the floor (overhangs)
                        float lowest = b.center.y - h[1];
                        if (lowest < 0.5f) continue;
                    }
                    int o0 = k == 0 ? 1 : 0, o1 = k == 2 ? 1 : 2;   // the other two axes, in order
                    int ui, vi;
                    if (Mathf.Abs(n.y) < 0.5f)
                    {
                        // wall: v = the in-plane axis most aligned with world up
                        vi = Mathf.Abs(ax[o0].y) > Mathf.Abs(ax[o1].y) ? o0 : o1;
                        ui = o0 == vi ? o1 : o0;
                    }
                    else
                    {
                        ui = Mathf.Abs(ax[o0].x) >= Mathf.Abs(ax[o1].x) ? o0 : o1;
                        vi = o0 == ui ? o1 : o0;
                    }
                    Vector3 v = ax[vi];
                    if (Mathf.Abs(n.y) < 0.5f) { if (v.y < 0) v = -v; }
                    else if (v.z < 0 && Mathf.Abs(v.z) > 0.3f) v = -v;
                    Vector3 u = Vector3.Cross(v, n);   // u x v = n
                    float su = 2 * h[ui], sv = 2 * h[vi];
                    Vector3 origin = b.center + n * h[k] - u * (su / 2) - v * (sv / 2);
                    var face = new Face
                    {
                        id = faces.Count, block = b.id, n = n, u = u, v = v, origin = origin, su = su, sv = sv,
                        wall = Mathf.Abs(n.y) < 0.3f, turf = n.y > 0.7f, ceiling = n.y < -0.5f,
                        paintable = b.paint && n.y > -0.5f,
                        pattern = b.pattern, color = b.color,
                    };
                    if (faceHidden(face)) continue;
                    face.mural = -1;
                    if (b.mural != null) foreach (var m in b.mural) if (Vector3.Dot(n, m.n) > 0.9f) face.mural = m.id;
                    if (b.noPaint != null) foreach (var m in b.noPaint) if (Vector3.Dot(n, m) > 0.9f) face.paintable = false;
                    if ((b.roof || b.perch) && n.y > 0.5f) face.paintable = false;
                    if (face.wall)
                    {
                        Vector3 p = origin + u * (su / 2) - v * 0.06f + n * 0.06f;
                        face.groundedBottom = pointInside(p, 0, b.id) || p.y < 0.02f;
                    }
                    b.faces[k * 2 + (sign > 0 ? 0 : 1)] = face.id;
                    faces.Add(face);
                }
            }
        }

        // A face is hidden when every sample point just outside it lies inside another solid block.
        bool faceHidden(Face f)
        {
            int nu = Math.Max(2, Mathf.CeilToInt(f.su / 1.25f)), nv = Math.Max(2, Mathf.CeilToInt(f.sv / 1.25f));
            for (int j = 0; j <= nv; j++)
                for (int i = 0; i <= nu; i++)
                {
                    float uu = Mathf.Min(f.su - 0.05f, Mathf.Max(0.05f, ((float)i / nu) * f.su));
                    float vv = Mathf.Min(f.sv - 0.05f, Mathf.Max(0.05f, ((float)j / nv) * f.sv));
                    Vector3 p = f.origin + f.u * uu + f.v * vv + f.n * 0.03f;
                    if (!pointInside(p, 0, f.block)) return false;
                }
            return true;
        }

        // ---------------------------------------------------------------- baked-lighting atlas
        // Deterministic shelf layout of every rendered face at a fixed density, independent of the paint atlas/quality.
        public bool layoutLightmap(float ppm = 5, int size = 1024, int pad = 2)
        {
            var fs = faces.Where(f => !blocks[f.block].grate).ToList();
            var rects = fs.Select(f => (f, w: Mathf.CeilToInt(f.su * ppm) + pad * 2, h: Mathf.CeilToInt(f.sv * ppm) + pad * 2)).ToList();
            rects.Sort((a, b) => b.h != a.h ? b.h - a.h : b.w != a.w ? b.w - a.w : a.f.id - b.f.id);
            int x = 0, y = 0, rowH = 0;
            foreach (var r in rects)
            {
                if (x + r.w > size) { x = 0; y += rowH; rowH = 0; }
                r.f.light = new AtlasRect { x = x, y = y, ppm = ppm, pad = pad };
                x += r.w; rowH = Math.Max(rowH, r.h);
            }
            lightSize = size;
            lightUsed = y + rowH;
            // checksum so a stale bake is never applied to a changed layout
            uint hsh = 2166136261;
            unchecked
            {
                foreach (var f in faces)
                    foreach (float val in new[] { f.origin.x, f.origin.y, f.origin.z, f.su, f.sv })
                    {
                        hsh ^= (uint)(int)Math.Round(val * 100, MidpointRounding.AwayFromZero);
                        hsh *= 16777619;
                    }
            }
            layoutHash = hsh.ToString("x");
            return lightUsed <= size;
        }

        // ---------------------------------------------------------------- render geometry
        // Every block renders as a rounded box: exposed edges get a real bevel (quarter-round strips + octant corners),
        // edges flush against other geometry stay sharp so floors never show grooves. Bevel vertices map into their face's
        // paint atlas (the padding holds extrapolated ink), so ink wraps smoothly over the rounded edges.
        // Requires faces to have been laid out in the paint atlas (face.atlas) — non-paintable faces render too.
        //
        // Vertex streams (three's custom attributes, packed into Unity's channels):
        //   COLOR      block colour (linear)
        //   uv0 xyzw   paintUv.xy, lightUv.xy
        //   uv1 xyzw   faceUv.xy (metres in the face frame), faceFlags.wall, faceFlags.groundedBottom
        //   uv2 xyzw   faceData: pattern, hasAtlas, su, sv
        //   uv3 xyzw   faceTan.xyz, faceFlags.mural
        public Mesh buildGeometry(float atlasSize, Func<Block, bool> filter = null)
        {
            filter ??= (b => !b.grate);
            var pos = new List<Vector3>(); var nor = new List<Vector3>(); var col = new List<Color>();
            var uv0 = new List<Vector4>(); var uv1 = new List<Vector4>(); var uv2 = new List<Vector4>(); var uv3 = new List<Vector4>();
            var idx = new List<int>();
            float LS = lightSize > 0 ? lightSize : 1;

            int vert(Face f, Vector3 p, Vector3 n)
            {
                int i = pos.Count;
                pos.Add(p); nor.Add(n);
                col.Add(f.color.linear);
                Vector3 q = p - f.origin;
                float cu = Vector3.Dot(q, f.u), cv = Vector3.Dot(q, f.v);
                Vector2 puv = f.atlas != null ? new Vector2((f.atlas.x + f.atlas.pad + cu * f.atlas.ppm) / atlasSize, (f.atlas.y + f.atlas.pad + cv * f.atlas.ppm) / atlasSize) : Vector2.zero;
                Vector2 luv = f.light != null ? new Vector2((f.light.x + f.light.pad + cu * f.light.ppm) / LS, (f.light.y + f.light.pad + cv * f.light.ppm) / LS) : new Vector2(-1, -1);
                uv0.Add(new Vector4(puv.x, puv.y, luv.x, luv.y));
                uv1.Add(new Vector4(cu, cv, f.wall ? 1 : 0, f.groundedBottom ? 1 : 0));
                uv2.Add(new Vector4(f.pattern, f.atlas != null ? 1 : 0, f.su, f.sv));
                uv3.Add(new Vector4(f.u.x, f.u.y, f.u.z, f.mural));
                return i;
            }
            // triangle with winding chosen so it faces along `n` (three's counter-clockwise front)
            void tri(int i0, int i1, int i2, Vector3 n)
            {
                Vector3 c = Vector3.Cross(pos[i1] - pos[i0], pos[i2] - pos[i0]);
                if (Vector3.Dot(c, n) >= 0) { idx.Add(i0); idx.Add(i1); idx.Add(i2); }
                else { idx.Add(i0); idx.Add(i2); idx.Add(i1); }
            }

            foreach (var b in blocks)
            {
                if ((!b.solid && !b.render) || !filter(b)) continue;
                var ax = b.axes;
                float[] h = { b.half.x, b.half.y, b.half.z };
                float minH = Mathf.Min(h[0], Mathf.Min(h[1], h[2]));
                // bevel scales with the block: chunky rounded edges on big structures, finer on crates/ramps/rails
                float bev = Mathf.Min(b.bevel ?? Mathf.Max(0.04f, Mathf.Min(0.13f, minH * 0.14f)), minH * 0.45f);
                int faceOf(int k, int s) => b.faces[k * 2 + (s > 0 ? 0 : 1)];
                // exposed-edge test (both faces rendered and nothing solid hugging the edge)
                var edgeCache = new Dictionary<int, bool>();
                bool bevelled(int k1, int s1, int k2, int s2)
                {
                    if (bev < 0.01f) return false;
                    int key = k1 < k2 ? (k1 * 1000 + (s1 + 1) * 100 + k2 * 10 + (s2 + 1)) : (k2 * 1000 + (s2 + 1) * 100 + k1 * 10 + (s1 + 1));
                    if (edgeCache.TryGetValue(key, out bool cached)) return cached;
                    bool ok = faceOf(k1, s1) >= 0 && faceOf(k2, s2) >= 0;
                    if (ok)
                    {
                        int k3 = 3 - k1 - k2;
                        for (float t = -1; t <= 1; t += 0.25f)
                        {
                            Vector3 E = b.center + ax[k1] * (s1 * h[k1]) + ax[k2] * (s2 * h[k2]) + ax[k3] * (t * h[k3] * 0.98f);
                            Vector3 Q = E + ax[k1] * (s1 * 0.04f) + ax[k2] * (s2 * 0.04f);
                            if (pointInside(Q, 0, b.id)) { ok = false; break; }
                        }
                    }
                    edgeCache[key] = ok;
                    return ok;
                }
                // --- faces (inset where the edge is bevelled)
                for (int k = 0; k < 3; k++)
                    foreach (int s in new[] { 1, -1 })
                    {
                        int fid = faceOf(k, s);
                        if (fid < 0) continue;
                        var f = faces[fid];
                        int i = k == 0 ? 1 : 0, j = k == 2 ? 1 : 2;
                        float ip = bevelled(k, s, i, 1) ? bev : 0, im = bevelled(k, s, i, -1) ? bev : 0;
                        float jp = bevelled(k, s, j, 1) ? bev : 0, jm = bevelled(k, s, j, -1) ? bev : 0;
                        Vector3 cf = b.center + ax[k] * (s * h[k]);
                        int corner(int si, int sj)
                        {
                            float di = si > 0 ? h[i] - ip : -(h[i] - im), dj = sj > 0 ? h[j] - jp : -(h[j] - jm);
                            return vert(f, cf + ax[i] * di + ax[j] * dj, f.n);
                        }
                        int v0 = corner(-1, -1), v1 = corner(1, -1), v2 = corner(1, 1), v3 = corner(-1, 1);
                        tri(v0, v1, v2, f.n); tri(v0, v2, v3, f.n);
                    }
                if (bev < 0.01f) continue;
                // --- edge strips
                const int SEG = 2;   // per half (4 segments per quarter-round)
                for (int k1 = 0; k1 < 3; k1++)
                    for (int k2 = k1 + 1; k2 < 3; k2++)
                        foreach (int s1 in new[] { 1, -1 })
                            foreach (int s2 in new[] { 1, -1 })
                            {
                                if (!bevelled(k1, s1, k2, s2)) continue;
                                int k3 = 3 - k1 - k2;
                                var fa = faces[faceOf(k1, s1)]; var fb = faces[faceOf(k2, s2)];
                                Vector3 nA = ax[k1] * s1, nB = ax[k2] * s2;
                                float endCut(int s3) => faceOf(k3, s3) >= 0 && bevelled(k3, s3, k1, s1) && bevelled(k3, s3, k2, s2) ? bev : 0;
                                float t0 = -h[k3] + endCut(-1), t1 = h[k3] - endCut(1);
                                Vector3 bas = b.center + nA * (h[k1] - bev) + nB * (h[k2] - bev);
                                for (int hf = 0; hf <= 1; hf++)
                                {
                                    var f = hf == 1 ? fb : fa;
                                    var rows = new List<(int r0, int r1, Vector3 n)>();
                                    for (int q = 0; q <= SEG; q++)
                                    {
                                        float th = (Mathf.PI / 4) * (hf + (float)q / SEG);
                                        Vector3 Nn = nA * Mathf.Cos(th) + nB * Mathf.Sin(th);
                                        int r0 = vert(f, bas + ax[k3] * t0 + Nn * bev, Nn);
                                        int r1 = vert(f, bas + ax[k3] * t1 + Nn * bev, Nn);
                                        rows.Add((r0, r1, Nn));
                                    }
                                    for (int q = 0; q < SEG; q++)
                                    {
                                        var (a0, a1, na) = rows[q]; var (b0, b1, _) = rows[q + 1];
                                        tri(a0, a1, b1, na); tri(a0, b1, b0, na);
                                    }
                                }
                            }
                // --- rounded corners (octant patches) where all three edges are bevelled
                const int NC = 3;
                foreach (int s0 in new[] { 1, -1 })
                    foreach (int s1 in new[] { 1, -1 })
                        foreach (int s2 in new[] { 1, -1 })
                        {
                            int[] sg = { s0, s1, s2 };
                            if (!(bevelled(0, s0, 1, s1) && bevelled(0, s0, 2, s2) && bevelled(1, s1, 2, s2))) continue;
                            Vector3 n0 = ax[0] * s0, n1 = ax[1] * s1, n2 = ax[2] * s2;
                            Vector3 c = b.center + n0 * (h[0] - bev) + n1 * (h[1] - bev) + n2 * (h[2] - bev);
                            // map the whole patch to the most "up" face (usually the top) so its ink matches what you see from above
                            int best = 0; float bd = -2;
                            Vector3[] ns = { n0, n1, n2 };
                            for (int a = 0; a < 3; a++) if (ns[a].y > bd) { bd = ns[a].y; best = a; }
                            var f = faces[faceOf(best, sg[best])];
                            var ids = new List<List<int>>();
                            for (int a = 0; a <= NC; a++)
                            {
                                ids.Add(new List<int>());
                                for (int bb = 0; bb <= NC - a; bb++)
                                {
                                    int cc = NC - a - bb;
                                    Vector3 Nn = (n0 * a + n1 * bb + n2 * cc).normalized;
                                    ids[a].Add(vert(f, c + Nn * bev, Nn));
                                }
                            }
                            Vector3 mid = (n0 + n1 + n2).normalized;
                            for (int a = 0; a < NC; a++)
                                for (int bb = 0; bb < NC - a; bb++)
                                {
                                    tri(ids[a][bb], ids[a + 1][bb], ids[a][bb + 1], mid);
                                    if (bb < NC - a - 1) tri(ids[a + 1][bb], ids[a + 1][bb + 1], ids[a][bb + 1], mid);
                                }
                        }
            }
            var mesh = new Mesh { name = "level", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(pos);
            mesh.SetNormals(nor);
            mesh.SetColors(col);
            mesh.SetUVs(0, uv0);
            mesh.SetUVs(1, uv1);
            mesh.SetUVs(2, uv2);
            mesh.SetUVs(3, uv3);
            mesh.SetTriangles(idx, 0, true);
            renderTris = idx.Count / 3;
            return mesh;
        }

        // Highest walkable surface under (x,z) below yMax (used by nav + spawns). Returns y or -Infinity.
        // skipGrates: what a squid would land on (squids drop straight through grates)
        public float groundHeight(float x, float z, float yMax = 50, bool skipGrates = false)
        {
            float best = float.NegativeInfinity;
            var ids = queryBlocks(x - 0.01f, z - 0.01f, x + 0.01f, z + 0.01f, _qtmp2);
            foreach (int id in ids)
            {
                var b = blocks[id];
                if (!b.solid || (skipGrates && b.grate)) continue;
                // intersect vertical line with the block's top face plane
                Vector3 n = b.axes[1];
                if (n.y < 0.5f) continue;
                Vector3 top = b.center + n * b.half.y;
                // plane: n·(p - top) = 0 → y = top.y - (n.x (x-top.x) + n.z (z-top.z)) / n.y
                float y = top.y - (n.x * (x - top.x) + n.z * (z - top.z)) / n.y;
                if (y <= yMax && y > best && pointInBlock(b, new Vector3(x, y - 0.01f, z), 0.001f)) best = y;
            }
            return best;
        }

        static BlockDef mirrorDef(BlockDef d)
        {
            var m = d.copy();
            if (d.mural != null) m.mural = d.mural.Select(x => new MuralRef(-x.n.x, x.n.y, -x.n.z, x.id)).ToList();
            if (d.noPaint != null) m.noPaint = d.noPaint.Select(x => new Vector3(-x.x, x.y, -x.z)).ToList();
            if (d.kind == "box") { m.min = new Vector3(-d.max.x, d.min.y, -d.max.z); m.max = new Vector3(-d.min.x, d.max.y, -d.min.z); }
            else if (d.kind == "obox") m.center = new Vector3(-d.center.x, d.center.y, -d.center.z);
            else { m.low = new Vector3(-d.low.x, d.low.y, -d.low.z); m.high = new Vector3(-d.high.x, d.high.y, -d.high.z); }
            return m;
        }
    }
}
