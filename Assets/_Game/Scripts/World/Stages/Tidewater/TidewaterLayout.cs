// Port of src/world/stages/tidewater/layout.js (+ the SURF ids of surfaces.js) — Tidewater Plaza stage layout.
//
// A Victorian seaside town's civic square where the harbour meets the promenade — laid out as a crescent round a bay.
// The Town Hall (Alpha, -Z) and the Custom House (Bravo, +Z) sit out on wings at the two tips; between them the square
// fans out round the round Jubilee terrace and its clock tower. Seen from above the playable deck is a lens: two
// crescents meeting at the spawn wings. Per team (Alpha looking +Z; its right hand is -X):
//   • spawn: the Town Hall's first-floor loggia (2.6 m above the square) — the central balcony with twin stone flights
//     running out sideways, one onto the tip of the promenade, one to the foot of the colonnade; a drop at the front
//   • right lane "the Promenade" (-X): a raised sea-front walk sweeping round the bay in a crescent (R 46 m), cast-iron
//     railing on the sea side, a glazed shelter, the ice-cream kiosk, Punch & Judy; it opens onto the pier gate at mid
//   • centre "the Square": sunk 20 cm below the pavements, fanning out from 22 m wide at the spawn to 45 m at mid — the
//     fountain, the bandstand, then the round terrace (1.2 m) with four flights and the tower on its square dais
//   • left lane "the Colonnade" (+X): a bent terrace of three buildings (the Crescent) with a cast-iron colonnade in
//     front — covered walk below, a 3.7 m terrace walk on top, reached from the spawn wing and, near mid, by a stair
//     along its front (the enemy's way up) or by climbing the landing pier as a squid
// The two lanes swap at mid: your Promenade runs into the enemy's Colonnade, and vice versa.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static Inkwave.World.MapKit;

namespace Inkwave.World.Stages
{
    public static class Tidewater
    {
        // surfaces.js: this stage's three reserved PATTERN slots
        public static class SURF { public const int herringbone = 28, terrazzo = 29, stucco = 30; }

        static class M
        {
            public const string plaza = "#c89c84", prom = "#d9d0c0", colFloor = "#dccdc0", loggia = "#e9e0d0", terrace = "#ddd3c1", stucco = "#efe6d3",
                mint = "#cfe3d6", pink = "#eed7cb", butter = "#efe0bb", granite = "#b9b3a8", stone = "#d8d0c0", seawall = "#b8b0a3",
                planter = "#b9ad9a", joint = "#a9a399";
        }
        static readonly List<Vector3> NP_ALL = new List<Vector3> { v(1, 0, 0), v(-1, 0, 0), v(0, 0, 1), v(0, 0, -1), v(0, 1, 0) };
        static BlockDef stucco(string color, BlockDef o = null) => BlockDef.merge(new BlockDef { color = color, pattern = SURF.stucco }, o);
        static List<Vector3> np(params Vector3[] n) => n.ToList();
        static List<MuralRef> mur(float x, float y, float z, int id) => new List<MuralRef> { new MuralRef(x, y, z, id) };

        // ---------------------------------------------------------------------------------------------------------------
        // Shared geometry (the props import it so the dressing follows the curves exactly)
        // ---------------------------------------------------------------------------------------------------------------
        public const float SQ = -0.2f;                                             // the square's paving; pavements stand at 0
        // the promenade crescent (Alpha's, on -X): an 8 m raised walk round a circle, sea edge at rOut
        public static class PROM { public static readonly Vector2 c = new Vector2(19, -4); public const float rIn = 38, rOut = 46, a0 = 180, a1 = 228; public const int n = 13; }
        // the round Jubilee terrace at mid: a ring of turned slabs (top 1.0) round a square dais (1.1) carrying the tower
        public static class TERR { public const float rIn = 5.8f, rOut = 8.5f, a0 = 5, top = 1.0f, dais = 5.8f, daisTop = 1.1f; public const int n = 36; }
        // the Crescent (Alpha's colonnade, on +X): the shop-front line (bends between three buildings), the covered walk /
        // terrace walk depth in front of it, the shops' depth behind it, and the terrace walk slab
        public static class COLO
        {
            public static readonly Vector2[] front = { new Vector2(18.9f, -38.3f), new Vector2(22.5f, -29), new Vector2(24.5f, -19), new Vector2(24.5f, -9) };
            public const float deck = 4.5f, floor = 5.1f, shops = 3, deckBot = 3.35f, deckTop = 3.7f;
        }
        // the landing pier + stair (on the straight north section) and the spawn wing's threshold landing
        public static class STAIR { public static readonly float[] x = { 17.5f, 20 }; public const float foot = -7.0f, top = -15.5f, landing = -18.5f; }

        // turned segments along a circle arc (centre c, radii rIn…rOut, angles a0…a1 degrees, n pieces) meeting at their inner
        // corners (like mapkit ARC); fill: lower slabs over the outer wedges between them (and at the chain ends if `ends`)
        // (y0 may be [even, odd]: the bottoms alternate to sit on arcSub's alternating tops)
        // ends: 0 = none, 1 = fillers at both chain ends too, 2 = 'closed' (a full ring, one filler per joint)
        public static List<BlockDef> arcBand(Vector2 c, float rIn, float rOut, float a0, float a1, int n, float[] y0, float y1, BlockDef o,
            Fill fill = null, int ends = 0, Func<int, bool> skip = null)
        {
            var outL = new List<BlockDef>();
            float da = (a1 - a0) / n, t = rOut - rIn, rm = (rIn + rOut) / 2, h = Mathf.Abs(da) * D2R / 2, len = 2 * rIn * Mathf.Tan(h);
            for (int i = 0; i < n; i++)
            {
                if (skip != null && skip(i)) continue;
                double a = (a0 + da * (i + 0.5)) * D2Rd;
                outL.Add(O((float)(c.x + rm * System.Math.Cos(a)), (float)(c.y + rm * System.Math.Sin(a)), t, len, y0[i % y0.Length], y1, -a / D2Rd, o));
            }
            if (fill != null)
                for (int i = ends != 0 ? 0 : 1; i <= (ends == 2 ? n - 1 : ends != 0 ? n : n - 1); i++)
                {
                    double a = (a0 + da * i) * D2Rd;
                    float r0 = rIn / Mathf.Cos(h), rmf = (r0 + rOut) / 2, gap = 2 * (rOut - rIn) * Mathf.Tan(h) + 0.14f;
                    outL.Add(O((float)(c.x + rmf * System.Math.Cos(a)), (float)(c.y + rmf * System.Math.Sin(a)), rOut - r0, gap, fill.y0, fill.y1, -a / D2Rd, fill.o));
                }
            return outL;
        }

        // the sea-wall plinth under a band: the same segments down in the water, but meeting at their OUTER corners (so the
        // outer face runs on unbroken and there are no wedges to fill); the overlapping inner corners take alternate tops
        public static List<BlockDef> arcSub(Vector2 c, float rIn, float rOut, float a0, float a1, int n, float y0, float[] tops, BlockDef o)
        {
            var outL = new List<BlockDef>();
            float da = (a1 - a0) / n, t = rOut - rIn, rm = (rIn + rOut) / 2, h = Mathf.Abs(da) * D2R / 2, len = 2 * rOut * Mathf.Tan(h);
            for (int i = 0; i < n; i++)
            {
                double a = (a0 + da * (i + 0.5)) * D2Rd;
                outL.Add(O((float)(c.x + rm * System.Math.Cos(a)), (float)(c.y + rm * System.Math.Sin(a)), t, len, y0, tops[i % 2], -a / D2Rd, o));
            }
            return outL;
        }

        // sections along a polyline P (travelling so the outward side is on the right: n = (dz, -dx)), spanning offsets
        // dIn…dOut from the line (negative = inward); pieces meet at their inner corners, fill covers the outer wedges
        public class Section { public Vector2 p, d, n, q0, q1; public float l, h, len; }
        static Vector3 unit(Vector2 a, Vector2 b) { float dx = b.x - a.x, dz = b.y - a.y, l = Mathf.Sqrt(dx * dx + dz * dz); return new Vector3(dx / l, dz / l, l); }

        public static List<Section> chainSections(Vector2[] P, float dIn)
        {
            var S = new List<Section>();
            for (int i = 0; i < P.Length - 1; i++)
            {
                var u = unit(P[i], P[i + 1]);
                S.Add(new Section { p = P[i], d = new Vector2(u.x, u.y), n = new Vector2(u.y, -u.x), l = u.z, h = Mathf.Atan2(u.x, u.y) / D2R });
            }
            // inner-line corner points Q (intersections of consecutive inward offset lines)
            var Q = new List<Vector2>();
            for (int i = 0; i <= S.Count; i++)
            {
                if (i == 0) { var s = S[0]; Q.Add(s.p + s.n * dIn); continue; }
                if (i == S.Count) { var s = S[i - 1]; Q.Add(P[i] + s.n * dIn); continue; }
                var a = S[i - 1]; var b = S[i];
                Vector2 pa = a.p + a.n * dIn, pb = b.p + b.n * dIn;
                float den = a.d.x * b.d.y - a.d.y * b.d.x, t = ((pb.x - pa.x) * b.d.y - (pb.y - pa.y) * b.d.x) / den;
                Q.Add(pa + a.d * t);
            }
            for (int i = 0; i < S.Count; i++) { S[i].q0 = Q[i]; S[i].q1 = Q[i + 1]; S[i].len = (Q[i + 1] - Q[i]).magnitude; }
            return S;
        }

        public static List<BlockDef> chainBand(Vector2[] P, float dIn, float dOut, float[] y0, float y1, BlockDef o, Fill fill = null, Func<int, bool> only = null)
        {
            var outL = new List<BlockDef>();
            var S = chainSections(P, dIn);
            for (int i = 0; i < S.Count; i++)
            {
                if (only != null && !only(i)) continue;
                var s = S[i];
                float w = dOut - dIn, cx = (s.q0.x + s.q1.x) / 2 + s.n.x * w / 2, cz = (s.q0.y + s.q1.y) / 2 + s.n.y * w / 2;
                outL.Add(O(cx, cz, w, s.len, y0[i % y0.Length], y1, s.h, o));
                if (fill != null && i > 0)
                {
                    var a = S[i - 1];
                    var nbv = unit(Vector2.zero, a.n + s.n);
                    Vector2 nb = new Vector2(nbv.x, nbv.y), db = new Vector2(-nb.y, nb.x);
                    // (fill.e: start the filler that far out along the bisector, so its inner corners stay behind the front faces)
                    float half = Mathf.Acos(Mathf.Max(-1, Mathf.Min(1, a.d.x * s.d.x + a.d.y * s.d.y))) / 2, e = fill.e, L = w / Mathf.Cos(half) - e, gap = 2 * w * Mathf.Tan(half) + 0.14f;
                    outL.Add(O(s.q0.x + nb.x * (e + L / 2), s.q0.y + nb.y * (e + L / 2), L, gap, fill.y0, fill.y1, Mathf.Atan2(db.x, db.y) / D2R, fill.o));
                }
            }
            return outL;
        }

        // chainBand's plinth: sections meeting at their outer corners (offset dOut), alternate tops
        public static List<BlockDef> chainSub(Vector2[] P, float dIn, float dOut, float y0, float[] tops, BlockDef o)
        {
            float w = dOut - dIn;
            return chainSections(P, dOut).Select((s, i) => O((s.q0.x + s.q1.x) / 2 - s.n.x * w / 2, (s.q0.y + s.q1.y) / 2 - s.n.y * w / 2, w, s.len, y0, tops[i % 2], s.h, o)).ToList();
        }

        static float[] Y(params float[] a) => a;

        public static LayoutDef build()
        {
            var F = COLO.front;
            // deck construction (the environment reads every box whose top is between -2.5 and 0 and whose bottom is below -1 as
            // a deck rect: sea masking, foam, pilings, waterline): the walking surfaces stop at -0.9 / -0.99 and the few slabs
            // that reach the sea are a plinth under them (PLINTH tops alternate between neighbours), 22 per half
            float[] PL = { -0.9f, -0.99f };
            var PLINTH = stucco(M.seawall, new BlockDef { tag = "sea-wall-plinth" });
            var JOINT = new Fill { y0 = -0.99f, y1 = -0.1f, o = stucco(M.joint, new BlockDef { tag = "paving-joint" }) };
            var L = new LayoutDef
            {
                id = "tidewater",
                bounds = new StageBounds { minX = -28, maxX = 28, minZ = -47, maxZ = 47 },
                spawnPads = new[] { v(0, 2.4f, -41.8f), v(0, 2.4f, 41.8f) },
                spawnBarrier = 4.2f,
                // match intro: opens high beside the clock tower's dials, then sweeps back down the square to your loggia
                intro = new IntroDef { from = v(11, 16.5f, 12), lookFrom = v(0, 10.5f, 0), toBack = 3.0f },
                // stage-select hero shot: from out over the bay off the promenade crescent, across the round terrace and its
                // tower to the Town Hall, the bandstand and the bent Crescent
                artFrom = v(-32, 19.5f, 9), artLook = v(3.5f, 0.5f, -15.5f), artFov = 64,
            };
            var single = L.single;
            // ---- the round Jubilee terrace: ring of slabs (1.0) with granite joints, the square dais (1.1), the tower
            single.AddRange(arcBand(Vector2.zero, TERR.rIn, TERR.rOut, TERR.a0, TERR.a0 + 360, TERR.n, Y(-0.6f), TERR.top, stucco(M.terrace, new BlockDef { tag = "terrace-ring" }),
                new Fill { y0 = -0.6f, y1 = TERR.top - 0.1f, o = stucco(M.joint, new BlockDef { tag = "terrace-joint" }) }, 2));
            single.Add(B(-TERR.dais, TERR.dais, -0.6f, TERR.daisTop, -TERR.dais, TERR.dais, stucco(M.terrace, new BlockDef { tag = "terrace-dais", mural = mur(0, 1, 0, 5) })));
            single.Add(B(-1.7f, 1.7f, TERR.daisTop, 3.6f, -1.7f, 1.7f, stucco(M.stucco, new BlockDef { tag = "tower-base" })));
            single.Add(B(-1.7f, 1.7f, 3.6f, 12.5f, -1.7f, 1.7f, stucco(M.stucco, new BlockDef { tag = "tower", noPaint = NP_ALL, roof = true })));
            // the terrace balustrade (collision only — the stone balustrade you see is a prop), open at the four flights
            single.AddRange(arcBand(Vector2.zero, TERR.rOut - 0.34f, TERR.rOut - 0.1f, TERR.a0, TERR.a0 + 360, TERR.n, Y(TERR.top), TERR.top + 1.05f, new BlockDef { rail = true, tag = "terrace-rail" }, null, 0,
                i => new[] { 8, 17, 26, 35 }.Any(k => Math.Abs((i - k + 36) % 36) <= 1 || Math.Abs((k - i + 36) % 36) <= 1)));

            var half = L.half;
            BlockDef o(string tag, string color, int? pattern = null, List<Vector3> noPaint = null, List<MuralRef> mural = null, bool? roof = null)
                => new BlockDef { tag = tag, color = color, pattern = pattern, noPaint = noPaint, mural = mural, roof = roof };
            // ================= the square (paving at SQ; its ragged edges hide under the pavements / buildings)
            // (the herringbone runs from each slab's max-x / min-z corner and repeats every 2.4 m: the open seams sit on that
            //  lattice so the brickwork carries straight across them; the rest are under granite kerbs or other pieces)
            half.Add(B(-19.5f, 21, -0.9f, SQ, -25, 0, o("square", M.plaza, SURF.herringbone)));
            // the fountain band: symmetric about its fountain in both halves, so the crest mosaic lands the same way round
            half.Add(B(-9, 9, -0.9f, SQ, -37, -25, o("square-fountain", M.plaza, SURF.herringbone, mural: mur(0, 1, 0, 4))));
            half.Add(B(9, 19, -0.9f, SQ, -37, -25, o("square", M.plaza, SURF.herringbone)));
            half.Add(B(-15, -9, -0.9f, SQ, -29, -25, o("square", M.plaza, SURF.herringbone)));
            half.Add(B(-11.5f, -9, -0.9f, SQ, -37, -29, o("square", M.plaza, SURF.herringbone)));
            // the tip by the hall (its sea faces show: these two reach the sea themselves)
            half.Add(B(-11.5f, 19, -1.2f, SQ, -38.4f, -37, o("square", M.plaza, SURF.herringbone)));
            half.Add(B(-9.5f, 17.9f, -1.2f, SQ, -45.3f, -38.4f, o("square-under-hall", M.plaza, SURF.herringbone)));
            // the square's own deck slabs, hidden under its paving
            half.Add(B(-19.5f, 21, -2.0f, -1.1f, -25, 0, stucco(M.seawall, new BlockDef { tag = "square-deck" })));
            half.Add(B(-12.8f, 19, -2.0f, -1.1f, -37, -25, stucco(M.seawall, new BlockDef { tag = "square-deck" })));

            // ================= the Promenade crescent (terrazzo walk at 0, granite joints between its slabs)
            half.AddRange(arcBand(PROM.c, PROM.rIn, PROM.rOut, PROM.a0, PROM.a1, PROM.n, PL, 0, o("promenade", M.prom, SURF.terrazzo), JOINT, 1));
            half.AddRange(arcSub(PROM.c, PROM.rIn, PROM.rOut, PROM.a0, PROM.a1, PROM.n, -2.0f, PL, PLINTH));
            // the pier-gate forecourt at mid (west side; its mirror is the east side's), between the promenade and the Crescent
            half.Add(B(-27, -17, -2.0f, 0, -4, 9, o("pier-forecourt", M.prom, SURF.terrazzo)));
            half.Add(B(-27, -24, 0, 2.6f, -5.5f, -2.5f, stucco(M.stucco, new BlockDef { tag = "pier-booth", roof = true, mural = mur(0, 0, 1, 11) })));
            half.Add(B(24, 27, 0, 2.6f, -5.5f, -2.5f, stucco(M.stucco, new BlockDef { tag = "pier-booth", roof = true, mural = mur(0, 0, 1, 11) })));
            // sea railings (collision only; the cast iron is a prop): round the crescent, the forecourt, the tip by the hall
            half.AddRange(arcBand(PROM.c, PROM.rOut - 0.24f, PROM.rOut - 0.08f, PROM.a0, PROM.a1, PROM.n, Y(0), 1.18f, new BlockDef { rail = true, tag = "sea-rail" }));
            half.Add(B(-26.95f, -26.79f, 0, 1.18f, 5.5f, 9, new BlockDef { rail = true, tag = "sea-rail" }));
            half.Add(B(-11.6f, -9.5f, SQ, 1.0f, -38.36f, -38.2f, new BlockDef { rail = true, tag = "sea-rail" }));

            // ================= Town Hall: spawn loggia, central balcony, twin stone flights out to the two lanes
            half.Add(B(-9.5f, 22, -1.2f, 5.2f, -46, -45.4f, stucco(M.stucco, new BlockDef { tag = "townhall-wall", roof = true })));
            half.Add(B(-9.5f, 9.5f, SQ, 2.2f, -45.4f, -38.4f, stucco(M.stucco, new BlockDef { tag = "loggia-body", noPaint = np(v(1, 0, 0), v(-1, 0, 0)) })));
            half.Add(B(-9.5f, 9.5f, 2.2f, 2.4f, -45.4f, -38.4f, o("loggia", M.loggia, SURF.terrazzo, np(v(1, 0, 0), v(-1, 0, 0)))));
            half.Add(B(-4.8f, 4.8f, SQ, 2.2f, -38.4f, -36, stucco(M.stucco, new BlockDef { tag = "balcony-body", noPaint = np(v(0, 0, 1)) })));
            half.Add(B(-4.8f, 4.8f, 2.2f, 2.4f, -38.4f, -36, o("balcony", M.loggia, SURF.terrazzo, np(v(0, 0, 1)))));
            half.Add(R(v(-10.8f, SQ, -37.2f), v(-4.8f, 2.4f, -37.2f), 2.4f, o("flight-w", M.granite, PATTERN.stonestep)));
            half.Add(R(v(10.8f, SQ, -37.2f), v(4.8f, 2.4f, -37.2f), 2.4f, o("flight-e", M.granite, PATTERN.stonestep)));
            // the loggia's seaward side stands over the water: a balustrade (collision here, stone as a prop)
            half.Add(B(-9.45f, -9.2f, 2.4f, 3.45f, -45.3f, -38.4f, new BlockDef { rail = true, tag = "loggia-rail" }));
            // east wing walk (2.4) → steps → the threshold landing (3.8) over the Crescent's first terrace-walk slab (3.7)
            half.Add(B(9.5f, 17.9f, SQ, 2.2f, -45.4f, -41.5f, stucco(M.stucco, new BlockDef { tag = "wing-body", noPaint = np(v(0, 0, 1)) })));
            half.Add(B(9.5f, 17.9f, 2.2f, 2.4f, -45.4f, -41.5f, o("wing-walk", M.loggia, SURF.terrazzo, np(v(0, 0, 1)))));
            half.Add(R(v(16.0f, 2.4f, -41.5f), v(16.0f, 3.8f, -38.3f), 3.8f, o("wing-steps", M.granite, PATTERN.stonestep)));
            // the steps' solid base: the flight starts 2.4 m up, and the space under it was a pocket kids and bots walked
            // into through the skirt and got stuck in. Its face stands just behind the skirt; its top is buried in the flight.
            half.Add(B(14.16f, 17.9f, SQ, 2.3f, -41.5f, -38.3f, stucco(M.stone, new BlockDef { tag = "wing-steps-base", noPaint = NP_ALL })));
            half.Add(B(13.9f, 17.9f, -1.2f, 3.8f, -38.3f, -36.4f, stucco(M.pink, new BlockDef { tag = "colonnade-end", noPaint = np(v(0, 0, 1), v(1, 0, 0)), mural = mur(0, 0, 1, 7) })));
            half.Add(B(17.9f, 25, -1.2f, 5.4f, -45.4f, -38.3f, stucco(M.pink, new BlockDef { tag = "crescent-corner", noPaint = NP_ALL, roof = true })));
            half.Add(B(17.9f, 25, -2.0f, -1.2f, -45.4f, -38.3f, stucco(M.seawall, new BlockDef { tag = "corner-quay", noPaint = NP_ALL })));

            // ================= the Crescent: covered-walk floor (runs on under the shops: the deck's sea edge is their back
            //                   wall), terrace walk, shops, upper storeys (three buildings; wedge fillers close the backs' bends)
            half.AddRange(chainBand(F, -COLO.floor, COLO.shops, PL, 0, o("colonnade-walk", M.colFloor, SURF.terrazzo), JOINT));
            half.AddRange(chainSub(F, -COLO.floor, COLO.shops, -2.0f, PL, PLINTH));
            half.AddRange(chainBand(F, -COLO.deck, 0, Y(COLO.deckBot), COLO.deckTop, o("colonnade-walk-top", M.colFloor, SURF.terrazzo),
                new Fill { y0 = COLO.deckBot, y1 = COLO.deckTop - 0.1f, o = o("walk-joint", M.colFloor, SURF.terrazzo) }));
            half.AddRange(chainBand(F, 0, COLO.shops, Y(0), COLO.deckTop, stucco(M.pink, new BlockDef { tag = "crescent-shops", mural = mur(0, 0, 1, 9) }),
                new Fill { e = 0.06f, y0 = 0, y1 = COLO.deckTop - 0.1f, o = stucco(M.pink, new BlockDef { tag = "crescent-shops-joint", noPaint = NP_ALL }) }));
            half.AddRange(chainBand(F, 0, COLO.shops, Y(COLO.deckTop), 7.6f, stucco(M.pink, new BlockDef { tag = "crescent-upper", noPaint = NP_ALL, roof = true }), null, i => i == 0));
            half.AddRange(chainBand(F, 0, COLO.shops, Y(COLO.deckTop), 9.2f, stucco(M.butter, new BlockDef { tag = "hotel-upper", noPaint = NP_ALL, roof = true }),
                new Fill { e = 0.06f, y0 = COLO.deckTop, y1 = 7.5f, o = stucco(M.pink, new BlockDef { tag = "crescent-upper-joint", noPaint = NP_ALL, roof = true }) }, i => i == 1));
            half.AddRange(chainBand(F, 0, COLO.shops, Y(COLO.deckTop), 7.2f, stucco(M.mint, new BlockDef { tag = "crescent-upper", noPaint = NP_ALL, roof = true }),
                new Fill { e = 0.06f, y0 = COLO.deckTop, y1 = 7.1f, o = stucco(M.mint, new BlockDef { tag = "crescent-upper-joint", noPaint = NP_ALL, roof = true }) }, i => i == 2));
            // the Grand Hotel's roof parapet balustrade (collision only, on its off-limits roof)
            half.AddRange(chainBand(F, 0.02f, 0.3f, Y(9.2f), 10.05f, new BlockDef { rail = true, tag = "hotel-parapet" }, null, i => i == 1));
            // terrace-walk railing (collision only), on the first two sections; the north section's is split round the landing
            half.AddRange(chainBand(F, -COLO.deck, -COLO.deck + 0.16f, Y(COLO.deckTop), COLO.deckTop + 1.1f, new BlockDef { rail = true, tag = "walk-rail" }, null, i => i < 2));
            half.Add(B(STAIR.x[0], 20.0f, COLO.deckTop, COLO.deckTop + 1.1f, STAIR.landing, STAIR.landing + 0.16f, new BlockDef { rail = true, tag = "walk-rail" }));
            // the landing pier (squid route up: its square-side face) + the stair up from the pier forecourt
            half.Add(B(STAIR.x[0], STAIR.x[1], -1.2f, COLO.deckTop, STAIR.landing, STAIR.top, o("colonnade-landing", M.colFloor, SURF.terrazzo, np(v(0, 0, -1)))));
            half.Add(R(v(18.75f, 0, STAIR.foot), v(18.75f, COLO.deckTop, STAIR.top), 2.5f, o("colonnade-stair", M.granite, PATTERN.stonestep)));

            // ================= promenade furniture blocks: cross-plan shelter screens, the ice-cream kiosk
            half.Add(B(-23, -20.65f, 0, 1.15f, -19.15f, -18.85f, stucco(M.mint, new BlockDef { tag = "shelter-screen" })));
            half.Add(B(-20.35f, -18, 0, 1.15f, -19.15f, -18.85f, stucco(M.mint, new BlockDef { tag = "shelter-screen" })));
            half.Add(B(-20.65f, -20.35f, 0, 1.15f, -21.5f, -16.5f, stucco(M.mint, new BlockDef { tag = "shelter-screen" })));
            half.Add(B(-24.9f, -22.3f, 0, 2.5f, -10.3f, -7.7f, stucco(M.mint, new BlockDef { tag = "icecream-kiosk", mural = new List<MuralRef> { new MuralRef(0, 0, 1, 8), new MuralRef(-1, 0, 0, 8) } })));

            // ================= the square: the bandstand (1 m stage above the paving), floral clock, border, tea rooms, anchor
            half.AddRange(OCT(0, -21, 3.8f, -0.6f, 0.8f, stucco(M.stone, new BlockDef { tag = "bandstand" })));
            half.Add(R(v(0, SQ, -27.2f), v(0, 0.8f, -24.51f), 2.4f, o("bandstand-steps", M.granite, PATTERN.stonestep)));
            half.Add(R(v(0, SQ, -14.8f), v(0, 0.8f, -17.49f), 2.4f, o("bandstand-steps", M.granite, PATTERN.stonestep)));
            half.Add(B(-10.7f, -6.3f, -0.6f, 0.5f, -27.2f, -22.8f, o("floral-clock", M.planter, PATTERN.planter)));
            half.Add(B(-15, -9, -0.6f, 0.7f, -14.5f, -9.5f, o("border", M.planter, PATTERN.planter)));
            half.Add(B(8.5f, 12.5f, -0.6f, 2.8f, -30, -26, stucco(M.butter, new BlockDef { tag = "tearooms", roof = true })));
            half.Add(B(11.4f, 13.6f, -0.6f, 0.8f, -18.6f, -16.4f, o("anchor-plinth", M.granite, SURF.stucco)));

            // ================= the terrace flights (Alpha's: south + west; the ring's other two are Bravo's)
            half.Add(R(v(0, SQ, -12.4f), v(0, TERR.top, -8.1f), 4, o("terrace-steps", M.granite, PATTERN.stonestep)));
            half.Add(R(v(-12.4f, SQ, 0), v(-8.1f, TERR.top, 0), 4, o("terrace-steps", M.granite, PATTERN.stonestep)));

            L.flags.Add(v(-8.9f, 2.4f, -44.8f));
            L.flags.Add(v(8.9f, 2.4f, -44.8f));
            return L;
        }
    }
}
