// Port of src/world/mapkit.js — the map-building kit shared by every stage layout.
// A map is a list of oriented boxes. Everything in `half` is also mirrored by a 180° rotation about the Y axis
// ((x,z) -> (-x,-z)) so both teams get an identical arena. Team Alpha spawns at -Z, Bravo at +Z.
//
// box helpers:
//   B(x0,x1, y0,y1, z0,z1, opts)             axis-aligned box
//   R([x,y,z] low, [x,y,z] high, width, opts) ramp slab whose top surface runs from low edge-centre to high edge-centre
// opts: a BlockDef holding only option fields (color, pattern, paint (default true), solid (default true), tag, …).
// JS spreads option objects ({ ...a, ...b }); here `merge(a, b)` does the same: b's set fields win.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Inkwave.World
{
    public static class PATTERN
    {
        public const int plain = 0, deck = 1, tiles = 2, concrete = 3, hazard = 4, container = 5, wood = 6, metal = 7, spawn = 8, planter = 9,
            asphalt = 10, metalpanel = 11, grate = 12, brick = 13, rubber = 14, glasstile = 15, pavers = 16,
            // marina set (texlib: planks = hardwood dock decking in its own colours; the rest take the block colour as paint)
            planks = 17, hullpaint = 18, nonslip = 19, gelcoat = 20, yard = 21, weatherboard = 22, render = 23,
            // stairs + ramps (ramp tops are textured in the ramp's own frame: treads / cleats always run across the slope)
            treads = 24, stonestep = 25, rampboard = 26, gangdeck = 27;
    }

    public static class MapColors
    {
        public const string deck = "#d8d2c4", deckEdge = "#c9c1b0", tile = "#d9dfe0", cream = "#ece4d4", sand = "#e6d3b3", slate = "#a9b4bc",
            stone = "#c7c2b8", trim = "#8e98a0", teal = "#8fb3b1", rust = "#cf9c88", mustard = "#dcc48e", lav = "#b3abd0",
            spawn = "#eae6de", wood = "#c9a27c", planter = "#b9ad9a", white = "#f3f1ec";
    }

    public class MuralRef { public Vector3 n; public int id; public MuralRef(float x, float y, float z, int id) { n = new Vector3(x, y, z); this.id = id; } }

    // A layout piece: geometry (kind + its fields) and options. Option fields are nullable so merge() can tell "unset".
    public class BlockDef
    {
        // geometry
        public string kind;                         // 'box' | 'obox' | 'ramp'
        public Vector3 min, max, center, size, low, high;
        public double rotY;                         // degrees; double so a turned face picks its u axis as JS does (Level: axX)
        public float width, thickness = 0.6f;
        public bool thin;
        // options
        public string color, tag, onlyIn, notIn;
        public int? pattern;
        public bool? paint, solid, grate, roof, perch, noNav, rail, hidden, render;
        public float? bevel;
        public List<MuralRef> mural;
        public List<Vector3> noPaint;
        public Vector3? oct;                         // (cx, cz, R) — lets the stage thumbnail draw one octagon

        public BlockDef copy()
        {
            var d = (BlockDef)MemberwiseClone();
            if (mural != null) d.mural = new List<MuralRef>(mural);
            if (noPaint != null) d.noPaint = new List<Vector3>(noPaint);
            return d;
        }

        // { ...a, ...b } for option fields (b wins where set); geometry comes from a
        public static BlockDef merge(BlockDef a, BlockDef b)
        {
            var d = a != null ? a.copy() : new BlockDef();
            if (b == null) return d;
            if (b.color != null) d.color = b.color;
            if (b.tag != null) d.tag = b.tag;
            if (b.onlyIn != null) d.onlyIn = b.onlyIn;
            if (b.notIn != null) d.notIn = b.notIn;
            if (b.pattern != null) d.pattern = b.pattern;
            if (b.paint != null) d.paint = b.paint;
            if (b.solid != null) d.solid = b.solid;
            if (b.grate != null) d.grate = b.grate;
            if (b.roof != null) d.roof = b.roof;
            if (b.perch != null) d.perch = b.perch;
            if (b.noNav != null) d.noNav = b.noNav;
            if (b.rail != null) d.rail = b.rail;
            if (b.hidden != null) d.hidden = b.hidden;
            if (b.render != null) d.render = b.render;
            if (b.bevel != null) d.bevel = b.bevel;
            if (b.mural != null) d.mural = new List<MuralRef>(b.mural);
            if (b.noPaint != null) d.noPaint = new List<Vector3>(b.noPaint);
            if (b.oct != null) d.oct = b.oct;
            if (b.thin) d.thin = true;
            if (b.thickness != 0.6f) d.thickness = b.thickness;
            return d;
        }
    }

    // a filler spec for the band helpers: { y0, y1, o } (+ e: start the filler that far out along the bisector)
    public class Fill { public float y0, y1, e; public BlockDef o; }

    public class StageBounds { public float minX, maxX, minZ, maxZ; }

    public class IntroDef { public Vector3 from, lookFrom; public float toBack; }

    public class LayoutDef
    {
        public string id, water;
        public StageBounds bounds;
        public Vector3[] spawnPads;
        public float spawnBarrier;
        public IntroDef intro;
        public Vector3 artFrom, artLook;
        public float artFov;
        public List<BlockDef> single = new List<BlockDef>(), half = new List<BlockDef>();
        public List<Vector2> lamps = new List<Vector2>(), palms = new List<Vector2>();
        public List<Vector3> flags = new List<Vector3>();
    }

    public static class MapKit
    {
        public const float D2R = Mathf.PI / 180;
        public const double D2Rd = System.Math.PI / 180;   // (turn angles: degrees ↔ radians in double, as the JS)

        public static BlockDef B(float x0, float x1, float y0, float y1, float z0, float z1, BlockDef o = null)
        {
            var d = BlockDef.merge(null, o);
            d.kind = "box"; d.min = new Vector3(x0, y0, z0); d.max = new Vector3(x1, y1, z1);
            return d;
        }

        public static BlockDef R(Vector3 low, Vector3 high, float width, BlockDef o = null)
        {
            var d = BlockDef.merge(null, o);
            d.kind = "ramp"; d.low = low; d.high = high; d.width = width;
            d.thickness = o != null ? o.thickness : 0.6f;
            return d;
        }

        // box turned about the vertical axis by `deg` degrees: centre (cx, cz), w along its local x, d along its local z
        public static BlockDef O(float cx, float cz, float w, float dd, float y0, float y1, double deg, BlockDef o = null)
        {
            var d = BlockDef.merge(null, o);
            d.kind = "obox"; d.center = new Vector3(cx, (y0 + y1) / 2, cz); d.size = new Vector3(w, y1 - y0, dd); d.rotY = deg;
            return d;
        }

        // Regular octagon platform (circumradius R): a plus of three boxes + four 45° corner slabs 10 cm lower. The corner
        // slabs tuck under the arms (that part is buried: no z-fighting, and paint under it is dead), leaving a shallow lip.
        public static List<BlockDef> OCT(float cx, float cz, float Rr, float y0, float y1, BlockDef o0 = null)
        {
            float A = Rr * Mathf.Cos(Mathf.PI / 8), hs = Rr * Mathf.Sin(Mathf.PI / 8);   // apothem, half side
            var o = BlockDef.merge(o0, null);
            o.oct = new Vector3(cx, cz, Rr);
            var outL = new List<BlockDef>
            {
                B(cx - A, cx + A, y0, y1, cz - hs, cz + hs, o),
                B(cx - hs, cx + hs, y0, y1, cz + hs, cz + A, o),
                B(cx - hs, cx + hs, y0, y1, cz - A, cz - hs, o),
            };
            // corner slabs reach past the arm corners by a margin small enough that neighbouring slabs never meet (≤ 0.158·R)
            float depth = A - hs * Mathf.Sqrt(2) + Mathf.Min(0.3f, 0.12f * Rr), k = (A - depth / 2) / Mathf.Sqrt(2);
            foreach (var (sx, sz) in new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) })
                outL.Add(O(cx + sx * k, cz + sz * k, 2 * hs, depth, y0, y1 - 0.1f, sx * sz > 0 ? 45 : -45, o));
            return outL;
        }

        // Curved wall: n straight segments following a circle (centre cx,cz; r = wall centre line) from a0° to a1°
        // (0° = +x, 90° = +z). Segments meet at their inner corners, so they never overlap.
        public static List<BlockDef> ARC(float cx, float cz, float r, float t, float y0, float y1, float a0, float a1, int n, BlockDef o = null)
        {
            var outL = new List<BlockDef>();
            float da = (a1 - a0) / n, len = 2 * (r - t / 2) * Mathf.Tan((Mathf.Abs(da) * Mathf.PI) / 360);
            for (int i = 0; i < n; i++)
            {
                float a = ((a0 + da * (i + 0.5f)) * Mathf.PI) / 180;
                outL.Add(O(cx + r * Mathf.Cos(a), cz + r * Mathf.Sin(a), t, len, y0, y1, (-a * 180) / Mathf.PI, o));
            }
            return outL;
        }

        // ramp onto an octagon's diagonal face: (sx, sz) = quadrant sign (±1, ±1), run = horizontal length
        public static BlockDef OCTRAMP(float cx, float cz, float Rr, float yLow, float yTop, float sx, float sz, float run, float width, BlockDef o = null)
        {
            float A = Rr * Mathf.Cos(Mathf.PI / 8), ux = sx / Mathf.Sqrt(2), uz = sz / Mathf.Sqrt(2);
            float hx = cx + ux * A, hz = cz + uz * A;
            return R_(hx + ux * run, yLow, hz + uz * run, hx, yTop - 0.1f, hz, width, o);
        }
        public static BlockDef R_(float lx, float ly, float lz, float hx, float hy, float hz, float width, BlockDef o) => R(new Vector3(lx, ly, lz), new Vector3(hx, hy, hz), width, o);

        public static Vector3 v(float x, float y, float z) => new Vector3(x, y, z);

        // ---- variants.js: mode variants of a stage (onlyIn / notIn tagged pieces)
        public static bool inMode(BlockDef it, string mode) => (it.onlyIn == null || it.onlyIn == mode) && (it.notIn == null || it.notIn != mode);

        public static LayoutDef layoutFor(LayoutDef layout, string mode)
        {
            var l = (LayoutDef)typeof(LayoutDef).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(layout, null);
            l.single = layout.single.FindAll(d => inMode(d, mode));
            l.half = layout.half.FindAll(d => inMode(d, mode));
            return l;
        }
    }
}
