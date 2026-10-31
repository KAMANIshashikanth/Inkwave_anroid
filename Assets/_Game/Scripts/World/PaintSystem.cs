// Port of src/world/paint.js — the ink paint system.
//  * GPU: every paintable face owns a rectangle in one big atlas render target. Splats are drawn as quads straight into
//    the atlas in face space (texture-space painting), so ink wraps across floors/walls/corners exactly like a spherical
//    splash would. (Shaders/Paint/PaintSplat.shader)
//  * CPU: a parallel coarse grid (0.25 m cells) per face answers gameplay queries — "is this spot my ink?" — and
//    tracks turf coverage for scoring. Both sides evaluate the same body edge (blobWobble / wob, or the roller band)
//    so they agree; everything drawn beyond that edge (rays, satellite droplets, fine spatter, wall drips, cosmetic
//    specks) is finer than the gameplay grid and never claims turf.
//
// How a splat lands (all timings scale with its size): the body floods out from ≈ 40 % with a strong ease-out, rays
// shoot out ahead of it, satellite droplets thrown off the crown land a beat later (the farthest last), fine spatter
// lands after that, and on walls the lower edge sags into drips that keep running for 1.5–3 s. Each splat also sends
// a ripple across the ink surface (ripple — also used by footsteps / dives / landings).
//
// Not ported yet: region floods (Zone Control, M7).
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace Inkwave.World
{
    public class SplatOpts
    {
        public Vector3? stretch;       // travel direction (world)
        public float? stretchAmt;      // smear amount (default 1 when stretch is given)
        public float? seed;
        public string kind;            // 'shot' 'line' 'blast' 'bomb' 'trail' 'drop' 'roll' 'speck'
        public bool instant, cosmetic;
    }

    public class RegionStats { public float own, enemy, empty; public int n; }

    public class PaintSystem
    {
        const int MAX_QUADS = 6000;
        public const int RIP_N = 24;
        const int K_SHOT = 0, K_LINE = 1, K_BLAST = 2, K_BOMB = 3, K_TRAIL = 4, K_DROP = 5, K_ROLL = 6, K_SPECK = 7;
        static readonly Dictionary<string, int> K = new Dictionary<string, int> { ["shot"] = 0, ["line"] = 1, ["blast"] = 2, ["bomb"] = 3, ["trail"] = 4, ["drop"] = 5, ["roll"] = 6, ["speck"] = 7 };
        // quad half-extent in footprint radii (satellites / spatter reach) and the extra reach below wall splats (drips)
        static readonly float[] REACH = { 2.45f, 2.1f, 2.7f, 2.75f, 2.3f, 1.9f, 1.25f, 1.35f };
        const float DRIP_REACH = 3.9f;
        const float WOB_MAX = 1.5f;   // upper bound of blobWobble (reach of the CPU cell loop)
        // roller band segment (face space, along = roll direction): half length / half width / corner rounding, × radius
        const float BAND_L = 0.55f, BAND_W = 0.62f, BAND_R = 0.1f;

        // Main-blob outline: organic lobes + two narrow "fingers" thrown out by the impact. The GPU splat shader evaluates
        // the identical function (wob), so the gameplay grid and the rendered ink agree on the edge.
        public static float blobWobble(float ang, float seed)
        {
            return 1 + 0.12f * Mathf.Sin(3 * ang + seed * 6.2831f) + 0.08f * Mathf.Sin(5 * ang + seed * 17.0f) +
                0.05f * Mathf.Sin(7 * ang + seed * 41.0f) + 0.03f * Mathf.Sin(11 * ang + seed * 73.0f) + 0.018f * Mathf.Sin(17 * ang + seed * 29.0f) +
                0.17f * Mathf.Pow(Mathf.Max(Mathf.Cos(ang - seed * 37.7f), 0), 28) + 0.12f * Mathf.Pow(Mathf.Max(Mathf.Cos(ang - seed * 53.3f - 2.1f), 0), 36);
        }

        public readonly Level level;
        public readonly int size;
        public readonly float cell;
        public readonly int pad = 8;            // ≥ 2^maxInkLod texels so mip levels never bleed between faces
        public float ppm, usedHeight;
        public List<Face> paintFaces;
        public byte[] grid;                      // 0 none, 1 team0, 2 team1
        public byte[] dead;                      // cells buried inside other geometry
        public int turfTotal;
        public float turfArea;
        public readonly int[] counts = new int[2];   // live turf cells per team
        public int version;                      // bumps whenever the CPU grid changes (minimap polling)
        public float clock;
        public int frame;
        public Vector3? viewPos;                 // camera position (setView) — ripples far from it are skipped / evicted first
        // ripple table read by the level shader: xyz + birth (paint clock) · amp, wavelength, speed, life
        public readonly Vector4[] rip = new Vector4[RIP_N], ripP = new Vector4[RIP_N];
        readonly float[] _ripS = new float[RIP_N];
        float _dryAcc;

        class Growing
        {
            public List<(Face f, float lu, float lv, float dn, float sdu, float sdv, float sa)> entries;
            public float R, seed, age, dur, dripDur, cx, cy, cz;
            public int team, kind;
        }
        readonly List<Growing> growing = new List<Growing>();   // splats still spreading / dripping on screen

        // GPU
        public RenderTexture texture;
        Material mat;
        Mesh quadMesh, dryMesh;
        // one interleaved stream (aPos, aLocal, aSplat, aStretch, aGrow); the mesh keeps MAX_QUADS vertices and only the
        // live range is uploaded each frame
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct PaintVert { public Vector3 pos, local; public Vector4 splat; public Vector3 stretch; public Vector4 grow; }
        PaintVert[] verts;
        int quads;
        bool dryNow;
        float dryValue;
        CommandBuffer cmd;
        MaterialPropertyBlock dryProps;
        readonly List<int> _qb = new List<int>(), _qr = new List<int>();

        public PaintSystem(Level level, int atlasSize = 4096, float maxDensity = 30, float cell = 0.25f)
        {
            this.level = level;
            size = atlasSize;
            this.cell = cell;
            layout(maxDensity);
            initGrid();
            initGPU();
        }

        // ------------------------------------------------------------ atlas layout (shelf packing)
        void layout(float maxDensity)
        {
            paintFaces = level.faces.Where(f => f.paintable).ToList();
            float p = maxDensity;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                if (tryPack(paintFaces, p, size)) break;
                p *= 0.92f;
            }
            ppm = p;
        }

        bool tryPack(List<Face> faces, float p, int S)
        {
            var rects = faces.Select(f => new { f, w = Mathf.CeilToInt(f.su * p) + pad * 2, h = Mathf.CeilToInt(f.sv * p) + pad * 2 }).ToList();
            // rotate nothing; sort by height (stable, like Array.prototype.sort)
            rects = rects.OrderByDescending(r => r.h).ToList();
            int x = 0, y = 0, rowH = 0;
            var placed = new List<(Face f, int x, int y)>();
            foreach (var r in rects)
            {
                if (r.w > S) return false;
                if (x + r.w > S) { x = 0; y += rowH; rowH = 0; }
                if (y + r.h > S) return false;
                placed.Add((r.f, x, y));
                x += r.w; rowH = Math.Max(rowH, r.h);
            }
            foreach (var r in placed) r.f.atlas = new AtlasRect { x = r.x, y = r.y, ppm = p, pad = pad };
            usedHeight = y + rowH;
            return true;
        }

        // ------------------------------------------------------------ CPU grid
        void initGrid()
        {
            int total = 0;
            foreach (var f in paintFaces)
            {
                f.nu = Math.Max(1, Mathf.RoundToInt(f.su / cell));
                f.nv = Math.Max(1, Mathf.RoundToInt(f.sv / cell));
                f.cu = f.su / f.nu; f.cv = f.sv / f.nv;
                f.grid = total;
                total += f.nu * f.nv;
            }
            grid = new byte[total];
            dead = new byte[total];
            turfTotal = 0;
            turfArea = 0;
            foreach (var f in paintFaces)
                for (int j = 0; j < f.nv; j++)
                    for (int i = 0; i < f.nu; i++)
                    {
                        Vector3 p = f.origin + f.u * ((i + 0.5f) * f.cu) + f.v * ((j + 0.5f) * f.cv) + f.n * 0.06f;
                        int k = f.grid + j * f.nu + i;
                        if (level.pointInside(p, 0, f.block)) dead[k] = 1;
                        else if (f.turf) { turfTotal++; turfArea += f.cu * f.cv; }
                    }
        }

        // ------------------------------------------------------------ GPU
        void initGPU()
        {
            int S = size;
            texture = new RenderTexture(S, S, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
            {
                name = "PaintAtlas", useMipMap = true, autoGenerateMips = false,
                filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp, anisoLevel = 8,
            };
            texture.Create();
            mat = new Material(Shader.Find("Hidden/Inkwave/PaintSplat"));
            verts = new PaintVert[MAX_QUADS * 4];
            var idx = new int[MAX_QUADS * 6];
            for (int i = 0; i < MAX_QUADS; i++)
            {
                idx[i * 6] = i * 4; idx[i * 6 + 1] = i * 4 + 1; idx[i * 6 + 2] = i * 4 + 2;
                idx[i * 6 + 3] = i * 4; idx[i * 6 + 4] = i * 4 + 2; idx[i * 6 + 5] = i * 4 + 3;
            }
            quadMesh = new Mesh { name = "paintQuads" };
            quadMesh.MarkDynamic();
            quadMesh.SetVertexBufferParams(MAX_QUADS * 4,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 4),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord3, VertexAttributeFormat.Float32, 4));
            quadMesh.SetVertexBufferData(verts, 0, 0, verts.Length);
            quadMesh.SetIndexBufferParams(MAX_QUADS * 6, IndexFormat.UInt32);
            quadMesh.SetIndexBufferData(idx, 0, 0, idx.Length);
            quadMesh.subMeshCount = 1;
            quadMesh.SetSubMesh(0, new SubMeshDescriptor(0, 0), MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            quadMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            // drying: subtract wetness from the used part of the atlas, drawn in the same pass as the splats so the mip
            // chain is rebuilt once per frame
            float yTop = Mathf.Min(1, ((usedHeight + 2) / S) * 2 - 1);
            dryMesh = new Mesh { name = "paintDry" };
            dryMesh.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, yTop, 0), new Vector3(-1, yTop, 0) };
            dryMesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            dryMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            dryProps = new MaterialPropertyBlock();
            cmd = new CommandBuffer { name = "Paint" };
            clear();
        }

        public void clear()
        {
            var c = new CommandBuffer { name = "PaintClear" };
            c.SetRenderTarget(texture);
            c.ClearRenderTarget(false, true, Color.clear);
            Graphics.ExecuteCommandBuffer(c);
            c.Release();
            texture.GenerateMips();
            Array.Clear(grid, 0, grid.Length);
            counts[0] = counts[1] = 0;
            quads = 0;
            growing.Clear();
            for (int i = 0; i < RIP_N; i++) { rip[i] = new Vector4(0, -999, 0, -99); ripP[i] = new Vector4(0, 0.2f, 1, 0.01f); _ripS[i] = 0; }
            _dryAcc = 0;
            version++;
        }

        // Camera position for ripple priorities (fx calls it every frame).
        public void setView(Vector3 pos) { viewPos = pos; }

        int kindOf(SplatOpts opts, float radius, bool st, float sAmt)
        {
            if (opts.kind != null && K.TryGetValue(opts.kind, out int k) && (k != K_ROLL || st)) return k;
            if (st) return sAmt >= 1 ? K_LINE : K_SHOT;
            if (radius >= 1.9f) return K_BOMB;
            if (radius >= 1.05f) return K_BLAST;
            if (radius < 0.3f) return K_DROP;
            return K_TRAIL;
        }

        // ------------------------------------------------------------ splat
        // center (three space), radius (m), team 0|1. Returns the area (m²) newly claimed by `team` (turf points / special gauge).
        public float splat(Vector3 center, float radius, int team, SplatOpts opts = null)
        {
            opts ??= new SplatOpts();
            float seed = opts.seed ?? Ctx.random();
            bool cosmetic = opts.cosmetic;
            bool st = opts.stretch.HasValue;
            float sAmt = st ? (opts.stretchAmt ?? 1) : 0;
            int kind = cosmetic && opts.kind == null ? K_SPECK : kindOf(opts, radius, st, sAmt);
            if (kind == K_ROLL) sAmt = 0;   // the direction orients the band; no smear
            float reachK = REACH[kind];
            float reach = radius * Mathf.Max(3.2f, reachK + 1.4f * sAmt + 0.3f);
            var ids = level.queryBlocks(center.x - reach, center.z - reach, center.x + reach, center.z + reach, _qb);
            float claimed = 0;
            var entries = new List<(Face, float, float, float, float, float, float)>();
            bool wall = false;
            foreach (int bid in ids)
            {
                var b = level.blocks[bid];
                // quick reject by AABB distance
                if (center.x < b.aabbMin.x - reach || center.x > b.aabbMax.x + reach ||
                    center.y < b.aabbMin.y - reach || center.y > b.aabbMax.y + reach ||
                    center.z < b.aabbMin.z - reach || center.z > b.aabbMax.z + reach) continue;
                for (int fi = 0; fi < 6; fi++)
                {
                    int fid = b.faces[fi];
                    if (fid < 0) continue;
                    var f = level.faces[fid];
                    if (f.atlas == null) continue;
                    Vector3 rel = center - f.origin;
                    float dn = Vector3.Dot(rel, f.n);
                    if (dn > radius || dn < -0.12f) continue;
                    float lu = Vector3.Dot(rel, f.u), lv = Vector3.Dot(rel, f.v);
                    float rr = Mathf.Sqrt(Mathf.Max(0, radius * radius - dn * dn));
                    float ext = rr * (reachK + 1.4f * sAmt);
                    if (lu < -ext || lu > f.su + ext || lv < -ext - (f.wall ? rr * DRIP_REACH : 0) || lv > f.sv + ext) continue;
                    // stretch / band direction projected into face space
                    float sdu = 0, sdv = 0, sa = 0;
                    if (st)
                    {
                        Vector3 s = opts.stretch.Value;
                        sdu = Vector3.Dot(s, f.u); sdv = Vector3.Dot(s, f.v);
                        float l = Mathf.Sqrt(sdu * sdu + sdv * sdv);
                        if (l > 0.2f) { sdu /= l; sdv /= l; sa = sAmt * l; }
                        else if (kind == K_ROLL) { sdu = 1; sdv = 0; }
                        else { sdu = sdv = 0; }
                    }
                    if (!cosmetic) claimed += cpuSplat(f, lu, lv, rr, team, seed, sdu, sdv, sa, kind);
                    entries.Add((f, lu, lv, dn, sdu, sdv, sa));
                    if (f.wall && rr > radius * 0.3f) wall = true;
                }
            }
            if (entries.Count > 0)
            {
                // an older splat of the other team still spreading underneath this one finishes instantly, so the newer
                // ink always ends up on top (matching the gameplay grid)
                if (!cosmetic)
                    for (int i = growing.Count - 1; i >= 0; i--)
                    {
                        var gg = growing[i];
                        if (gg.team == team || gg.kind == K_SPECK) continue;
                        float dx = gg.cx - center.x, dy = gg.cy - center.y, dz = gg.cz - center.z;
                        float rs = gg.R * (gg.dripDur > 0 ? DRIP_REACH : REACH[gg.kind]) + radius * REACH[kind];
                        if (dx * dx + dy * dy + dz * dz < rs * rs) { emitGrowth(gg, 3, 1, false); growing.RemoveAt(i); }
                    }
                int drips = wall && kind != K_SPECK && kind != K_ROLL ? 1 : 0;
                var g = new Growing
                {
                    entries = entries, R = radius, team = team, seed = seed, kind = kind, age = 0,
                    // the body floods out in ≈ 0.1–0.3 s (bigger = heavier), droplets land up to ~1.3× that later; drips run on
                    dur = kind == K_SPECK ? 0.05f : 0.085f + Mathf.Min(0.22f, radius * 0.075f),
                    dripDur = drips > 0 ? 1.1f + Mathf.Min(2.2f, radius * 1.5f) : 0,
                    cx = center.x, cy = center.y, cz = center.z,
                };
                if (opts.instant) emitGrowth(g, 3, 1, false);
                else growing.Add(g);
                if (!cosmetic && radius >= 0.15f && !rippledNear(center, radius))
                {
                    // a ripple runs out across the wet ink from the impact (one per cluster: a roller stroke or a burst of
                    // trail drips does not turn the ink into rain)
                    float rc = Mathf.Min(radius, 3);
                    ripple(center, 0.0038f + 0.0036f * rc, 0.1f + 0.05f * rc, 0.85f + 0.35f * rc, 0.55f + 0.2f * rc);
                }
            }
            return claimed;
        }

        // Cosmetic micro-splat where a flying droplet lands: GPU only (finer than the gameplay grid, never claims turf).
        public float speck(Vector3 center, float radius, int team, float? seed = null)
            => splat(center, Mathf.Min(radius, 0.12f), team, new SplatOpts { seed = seed ?? Ctx.random(), kind = "speck", cosmetic = true });

        // A ripple of at least this size already started nearby within the last ~0.16 s?
        bool rippledNear(Vector3 pos, float radius)
        {
            float reach = 0.55f + radius * 0.6f;
            for (int i = 0; i < RIP_N; i++)
            {
                float age = clock - rip[i].w;
                if (age < 0 || age > 0.16f || ripP[i].x < 0.0036f + 0.0036f * Mathf.Min(radius, 3) * 0.7f) continue;
                float dx = rip[i].x - pos.x, dy = rip[i].y - pos.y, dz = rip[i].z - pos.z;
                if (dx * dx + dy * dy + dz * dz < reach * reach) return true;
            }
            return false;
        }

        // A ripple across the ink surface at pos (only visible where there is ink). amp in metres (≈ 0.003–0.015),
        // wavelength in metres, speed m/s, life s. Keeps the RIP_N most important live ripples (size × nearness × life left).
        public void ripple(Vector3 pos, float amp = 0.006f, float wavelength = 0.14f, float speed = 1.2f, float life = 0.7f)
        {
            float d2 = 0;
            if (viewPos.HasValue)
            {
                d2 = (pos - viewPos.Value).sqrMagnitude;
                if (d2 > 38 * 38) return;
            }
            float score = amp / (1 + d2 * 0.012f);
            int best = -1; float bestS = float.PositiveInfinity;
            for (int i = 0; i < RIP_N; i++)
            {
                float age = clock - rip[i].w, L = ripP[i].w;
                if (age >= L || age < -1) { best = i; bestS = -1; break; }
                float s = _ripS[i] * (1 - age / L);
                if (s < bestS) { bestS = s; best = i; }
            }
            if (best < 0 || bestS > score) return;
            rip[best] = new Vector4(pos.x, pos.y, pos.z, clock);
            ripP[best] = new Vector4(amp, wavelength, speed, life);
            _ripS[best] = score;
        }

        // Draw one growth step of a splat. tn = age / spread time (the body spreads over [0,1], droplets + spatter land up
        // to ≈ 1.7), dT = drip progress 0..1, dripOnly = the body is done: redraw only the running drips on walls. The
        // atlas alpha is max-blended and the colour "over" blended with the same shape, so redrawing every frame is idempotent.
        void emitGrowth(Growing g, float tn, float dT, bool dripOnly)
        {
            float R = g.R; int kind = g.kind;
            float reachK = REACH[kind];
            foreach (var (f, lu, lv, dn, sdu, sdv, sa) in g.entries)
            {
                if (dn >= R) continue;
                float rr = Mathf.Sqrt(R * R - dn * dn);
                if (dripOnly)
                {
                    if (!f.wall || rr < R * 0.3f) continue;
                    pushQuad(f, lu - rr * 0.95f, lu + rr * 0.95f, lv - rr * DRIP_REACH, lv - rr * 0.3f, lu, lv, dn, R, g.team, g.seed, kind, sdu, sdv, sa, tn, dT, 1);
                }
                else
                {
                    float ext = rr * (reachK + 1.4f * sa);
                    float down = f.wall && g.dripDur > 0 ? rr * DRIP_REACH : 0;
                    pushQuad(f, lu - ext, lu + ext, lv - Mathf.Max(ext, down), lv + ext, lu, lv, dn, R, g.team, g.seed, kind, sdu, sdv, sa, tn, dT, 0);
                }
            }
        }

        float cpuSplat(Face f, float lu, float lv, float r, int team, float seed, float sdu, float sdv, float sa, int kind)
        {
            if (r <= 0.02f) return 0;
            byte val = (byte)(team + 1);
            bool roll = kind == K_ROLL;
            float ext = roll ? r * (Mathf.Sqrt(BAND_L * BAND_L + BAND_W * BAND_W) + BAND_R + 0.05f) : r * (1 + sa) * WOB_MAX;
            int i0 = Math.Max(0, Mathf.FloorToInt((lu - ext) / f.cu)), i1 = Math.Min(f.nu - 1, Mathf.FloorToInt((lu + ext) / f.cu));
            int j0 = Math.Max(0, Mathf.FloorToInt((lv - ext) / f.cv)), j1 = Math.Min(f.nv - 1, Mathf.FloorToInt((lv + ext) / f.cv));
            if (i1 < i0 || j1 < j0) return 0;
            float claimed = 0;
            float cellA = f.cu * f.cv;
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    float px = (i + 0.5f) * f.cu - lu, py = (j + 0.5f) * f.cv - lv;
                    if (roll)
                    {
                        float qa = Mathf.Abs(px * sdu + py * sdv) - r * BAND_L, qb = Mathf.Abs(-px * sdv + py * sdu) - r * BAND_W;
                        float sd = Mathf.Sqrt(Mathf.Max(qa, 0) * Mathf.Max(qa, 0) + Mathf.Max(qb, 0) * Mathf.Max(qb, 0)) + Mathf.Min(Mathf.Max(qa, qb), 0) - r * BAND_R;
                        if (sd > -0.03f * r) continue;
                    }
                    else
                    {
                        if (sa > 0)
                        {
                            float a = px * sdu + py * sdv;
                            float qx = px - a * sdu, qy = py - a * sdv;
                            float s = a > 0 ? 1 + sa : 1 + 0.25f * sa;
                            px = qx + sdu * (a / s); py = qy + sdv * (a / s);
                        }
                        float d = Mathf.Sqrt(px * px + py * py);
                        if (d > r * WOB_MAX) continue;
                        if (d / (r * blobWobble(Mathf.Atan2(py, px), seed)) > 0.97f) continue;
                    }
                    int k = f.grid + j * f.nu + i;
                    byte prev = grid[k];
                    if (prev == val) continue;
                    grid[k] = val;
                    claimed += cellA;
                    if (f.turf && dead[k] == 0)
                    {
                        if (prev != 0) counts[prev - 1]--;
                        counts[team]++;
                    }
                }
            if (claimed > 0) version++;
            return claimed;
        }

        void pushQuad(Face f, float u0, float u1, float v0, float v1, float lu, float lv, float dn, float R, int team, float seed, int kind,
            float sdu, float sdv, float sa, float tn, float dT, float dripOnly)
        {
            if (quads >= MAX_QUADS) drawQuads();
            var a = f.atlas; float S = size;
            float padM = (a.pad - 0.5f) / a.ppm;
            u0 = Mathf.Max(-padM, u0); u1 = Mathf.Min(f.su + padM, u1);
            v0 = Mathf.Max(-padM, v0); v1 = Mathf.Min(f.sv + padM, v1);
            if (u1 <= u0 || v1 <= v0) return;
            int q = quads++;
            float flags = (f.wall ? 1 : 0) + 2 * kind;
            for (int c = 0; c < 4; c++)
            {
                float cu = c == 1 || c == 2 ? u1 : u0, cv = c >= 2 ? v1 : v0;
                int vi = q * 4 + c;
                float px = a.x + a.pad + cu * a.ppm, py = a.y + a.pad + cv * a.ppm;
                verts[vi] = new PaintVert
                {
                    pos = new Vector3((px / S) * 2 - 1, (py / S) * 2 - 1, 0),
                    local = new Vector3(cu - lu, cv - lv, dn),
                    splat = new Vector4(R, team, seed, flags),
                    stretch = new Vector3(sdu, sdv, sa),
                    grow = new Vector4(tn, dT, dripOnly, 0),
                };
            }
        }

        // Advance spreading / dripping splats, dry the ink a little, and draw everything into the atlas. Call once per frame.
        public void flush(float dt = 1 / 60f)
        {
            clock += dt;
            frame++;
            for (int i = 0; i < growing.Count; i++)
            {
                var g = growing[i];
                g.age += dt;
                float tn = g.age / g.dur;
                float td = g.dripDur > 0 ? Mathf.Min(1, g.age / g.dripDur) : 1;
                float dT = 1 - Mathf.Pow(1 - td, 2.2f);   // viscous: runs fast, then creeps to a stop
                bool bodyDone = tn >= 1.75f;
                if (bodyDone && td >= 1)
                {
                    emitGrowth(g, 3, 1, g.dripDur > 0);
                    growing[i] = growing[growing.Count - 1]; growing.RemoveAt(growing.Count - 1); i--;
                    continue;
                }
                emitGrowth(g, Mathf.Min(tn, 3), dT, bodyDone);
            }
            // drying: 1/255 of wetness every 1/40 s (≈ 6.4 s from landing to dry), applied in steps of ≥ 2
            _dryAcc += dt;
            int n = Mathf.FloorToInt(_dryAcc * 40);
            if (n >= 2)
            {
                int k = Math.Min(n, 12);
                _dryAcc -= k / 40f;
                dryValue = k / 255f;
                dryNow = true;
            }
            drawQuads();
            dryNow = false;
        }

        void drawQuads()
        {
            if (quads == 0 && !dryNow) return;
            cmd.Clear();
            cmd.SetRenderTarget(texture);
            if (dryNow)
            {
                dryProps.SetFloat("_Dry", dryValue);
                cmd.DrawMesh(dryMesh, Matrix4x4.identity, mat, 0, 1, dryProps);
            }
            if (quads > 0)
            {
                const MeshUpdateFlags F = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers;
                quadMesh.SetVertexBufferData(verts, 0, 0, quads * 4, 0, F);
                quadMesh.SetSubMesh(0, new SubMeshDescriptor(0, quads * 6), F);
                cmd.DrawMesh(quadMesh, Matrix4x4.identity, mat, 0, 0);
            }
            cmd.GenerateMips(texture);
            Graphics.ExecuteCommandBuffer(cmd);
            quads = 0;
            dryNow = false;
        }

        // ------------------------------------------------------------ queries
        // Team at face-local (u,v): 0 none, 1 = team0, 2 = team1
        public int sample(int faceId, float u, float v)
        {
            if (faceId < 0) return 0;
            var f = level.faces[faceId];
            if (f == null || f.atlas == null) return 0;
            int i = Math.Min(f.nu - 1, Math.Max(0, Mathf.FloorToInt(u / f.cu)));
            int j = Math.Min(f.nv - 1, Math.Max(0, Mathf.FloorToInt(v / f.cv)));
            return grid[f.grid + j * f.nu + i];
        }

        // Team at a world point lying on face faceId.
        public int sampleWorld(int faceId, Vector3 p)
        {
            if (faceId < 0) return 0;
            var f = level.faces[faceId];
            Vector3 rel = p - f.origin;
            return sample(faceId, Vector3.Dot(rel, f.u), Vector3.Dot(rel, f.v));
        }

        // Turf coverage fractions [team0, team1] of all live turf cells.
        public Vector2 coverage() => new Vector2((float)counts[0] / turfTotal, (float)counts[1] / turfTotal);

        // Fractions of turf cells within radius of (x, z) near height y: { own, enemy, empty } relative to `team`.
        public RegionStats regionStats(float x, float y, float z, float radius, int team, RegionStats o = null)
        {
            o ??= new RegionStats();
            o.own = o.enemy = o.empty = 0; o.n = 0;
            var ids = level.queryBlocks(x - radius, z - radius, x + radius, z + radius, _qr);
            int own = team + 1;
            foreach (int bid in ids)
            {
                var b = level.blocks[bid];
                for (int fi = 0; fi < 6; fi++)
                {
                    int fid = b.faces[fi];
                    if (fid < 0) continue;
                    var f = level.faces[fid];
                    if (!f.turf || f.atlas == null) continue;
                    if (Mathf.Abs(f.origin.y - y) > 2.5f) continue;
                    Vector3 rel = new Vector3(x, y, z) - f.origin;
                    float lu = Vector3.Dot(rel, f.u), lv = Vector3.Dot(rel, f.v);
                    int i0 = Math.Max(0, Mathf.FloorToInt((lu - radius) / f.cu)), i1 = Math.Min(f.nu - 1, Mathf.FloorToInt((lu + radius) / f.cu));
                    int j0 = Math.Max(0, Mathf.FloorToInt((lv - radius) / f.cv)), j1 = Math.Min(f.nv - 1, Mathf.FloorToInt((lv + radius) / f.cv));
                    for (int j = j0; j <= j1; j += 2)
                        for (int i = i0; i <= i1; i += 2)
                        {
                            float du = (i + 0.5f) * f.cu - lu, dv = (j + 0.5f) * f.cv - lv;
                            if (du * du + dv * dv > radius * radius) continue;
                            int k = f.grid + j * f.nu + i;
                            if (dead[k] != 0) continue;
                            int g = grid[k];
                            o.n++;
                            if (g == own) o.own++; else if (g != 0) o.enemy++; else o.empty++;
                        }
                }
            }
            if (o.n > 0) { o.own /= o.n; o.enemy /= o.n; o.empty /= o.n; }
            return o;
        }

        public void dispose()
        {
            if (texture != null) texture.Release();
            cmd?.Release();
            Ctx.destroy(quadMesh);
            Ctx.destroy(dryMesh);
            Ctx.destroy(mat);
        }
    }
}
