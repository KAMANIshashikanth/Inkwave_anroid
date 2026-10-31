// Port of src/game/kits/blade-fx.js — the Brine Cutlass's slash trails and the charged ink wave's look (used by Blade.cs).
//   Slash arcs: pooled annular-sector meshes (a swoosh that sweeps with the blade and fades from its tail): a flat,
//   slightly diagonal arc round the chest for the quick cuts (alternating sides), a tall vertical arc for the charged
//   overhead cut. Ink wave: a pooled crescent (a lens-shaped front sheet you see from behind + a crescent profile with
//   a streaming tail you see from the side), team ink with a white-hot leading edge. Shaders/Fx/BladeFx draws both; the
//   sounds and the icon arrive with M5 / M6.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using static Inkwave.Ctx;

namespace Inkwave
{
    public static class BladeFX
    {
        const float DEG = Mathf.PI / 180;

        // ---------------------------------------------------------------------------------------- geometry
        // arc: u along the sweep (0 start → 1 end), v across (0 inner → 1 outer, the blade tip's path). plane 'h' = XZ
        // (angle from +Z toward +X), 'v' = YZ (angle from +Z toward +Y)
        static Mesh arcGeometry(float a0, float a1, float r0, float r1, char plane, int nu = 44, int nv = 7)
        {
            var pos = new List<Vector3>(); var uv = new List<Vector3>(); var idx = new List<int>();
            for (int i = 0; i <= nu; i++)
            {
                float u = (float)i / nu, th = a0 + (a1 - a0) * u;
                for (int j = 0; j <= nv; j++)
                {
                    float v = (float)j / nv, r = r0 + (r1 - r0) * v;
                    pos.Add(plane == 'h' ? new Vector3(Mathf.Sin(th) * r, 0, Mathf.Cos(th) * r) : new Vector3(0, Mathf.Sin(th) * r, Mathf.Cos(th) * r));
                    uv.Add(new Vector3(u, v, 0));
                    if (i < nu && j < nv) { int a = i * (nv + 1) + j, b = a + nv + 1; idx.AddRange(new[] { a, b, a + 1, a + 1, b, b + 1 }); }
                }
            }
            return mesh("bladeArc", pos, uv, idx, new Bounds(Vector3.zero, Vector3.one * 4));
        }
        // wave: sheet A (k = 0) = a lens across X·Y at the crescent's front; sheet B (k = 1) = the crescent profile in Y·Z with
        // a tail streaming back along −Z. uv: A = (x −1…1, y −1…1); B = (tail 0…1, y −1…1). Unit size (scaled per wave).
        static Mesh waveGeometry()
        {
            var pos = new List<Vector3>(); var uv = new List<Vector3>(); var idx = new List<int>();
            const float bow = 0.32f; const int NY = 18, NX = 8, NT = 10;
            int bas = 0;
            for (int i = 0; i <= NY; i++)
            {
                float y = ((float)i / NY) * 2 - 1, lens = Mathf.Sqrt(Mathf.Max(0, 1 - y * y));
                for (int j = 0; j <= NX; j++)
                {
                    float x = ((float)j / NX) * 2 - 1;
                    pos.Add(new Vector3(x * 0.6f * (0.25f + 0.75f * lens), y, bow * (1 - y * y) - 0.08f * x * x));
                    uv.Add(new Vector3(x, y, 0));
                    if (i < NY && j < NX) { int a = bas + i * (NX + 1) + j, b = a + NX + 1; idx.AddRange(new[] { a, b, a + 1, a + 1, b, b + 1 }); }
                }
            }
            bas = pos.Count;
            for (int i = 0; i <= NY; i++)
            {
                float y = ((float)i / NY) * 2 - 1, c = 1 - y * y;
                for (int j = 0; j <= NT; j++)
                {
                    float t = (float)j / NT;
                    pos.Add(new Vector3(0, y * (1 - 0.3f * t), bow * c - t * (0.35f + 0.65f * c) * 1.6f));
                    uv.Add(new Vector3(t, y, 1));
                    if (i < NY && j < NT) { int a = bas + i * (NT + 1) + j, b = a + NT + 1; idx.AddRange(new[] { a, b, a + 1, a + 1, b, b + 1 }); }
                }
            }
            return mesh("bladeWave", pos, uv, idx, new Bounds(new Vector3(0, 0, -0.5f), Vector3.one * 4));
        }
        static Mesh mesh(string name, List<Vector3> pos, List<Vector3> uv, List<int> idx, Bounds b)
        {
            var m = new Mesh { name = name };
            m.SetVertices(pos); m.SetUVs(0, uv); m.SetTriangles(idx, 0); m.bounds = b;
            return m;
        }

        // ---------------------------------------------------------------------------------------- pools
        const int ARC_N = 14, WAVE_N = 12;
        public class Arc { public bool busy, heavy; public Actor a; public float t, dur, fade, side, yaw, head, alpha, tail; public Color col; public readonly MaterialPropertyBlock mpb = new MaterialPropertyBlock(); }
        public class Wave { public bool busy; public Color col; public float t, fade = 1; public Matrix4x4 m; public readonly MaterialPropertyBlock mpb = new MaterialPropertyBlock(); }
        static readonly List<Arc> arcs = new List<Arc>(), live = new List<Arc>();
        static readonly List<Wave> waves = new List<Wave>();
        static Mesh _geoH, _geoV, _geoW;
        static Material _mat;
        static readonly int ID_Color = Shader.PropertyToID("_Color"), ID_P = Shader.PropertyToID("_P"), ID_Mode = Shader.PropertyToID("_Mode");
        static bool ensure()
        {
            if (_mat != null) return true;
            // tap arc: 150° round the chest, 0.35 → 1.55 m; heavy arc: overhead (behind the head) → down past the feet
            _geoH = arcGeometry(-75 * DEG, 75 * DEG, 0.34f, 1.55f, 'h');
            _geoV = arcGeometry(118 * DEG, -58 * DEG, 0.3f, 1.7f, 'v');
            _geoW = waveGeometry();
            _mat = new Material(Shader.Find("Inkwave/Fx/BladeFx")) { name = "iw-blade-fx" };
            for (int i = 0; i < ARC_N; i++) arcs.Add(new Arc());
            for (int i = 0; i < WAVE_N; i++) waves.Add(new Wave());
            return true;
        }

        // a slash arc riding with actor a: side ±1 (+1 sweeps the kid's right → left), heavy = the overhead cut
        public static void slash(Actor a, float side, bool heavy, float dur, float yaw)
        {
            if ((G.camPos - a.pos).sqrMagnitude > 50 * 50) return;   // too far to read: skip the trail
            if (!ensure()) return;
            Arc s = null;
            foreach (var x in arcs) if (!x.busy) { s = x; break; }
            if (s == null) { if (live.Count == 0) return; s = live[0]; live.RemoveAt(0); }
            s.busy = true; s.a = a; s.t = 0; s.dur = dur; s.side = side; s.heavy = heavy; s.yaw = yaw;
            s.fade = heavy ? 0.26f : 0.2f;
            s.col = G.teamColors[a.team]; s.head = 0; s.alpha = 1; s.tail = heavy ? 0.4f : 0.32f;
            live.Add(s);
        }
        public static Wave waveMesh(Color color)
        {
            if (!ensure()) return null;
            foreach (var w in waves) if (!w.busy) { w.busy = true; w.col = color; w.fade = 1; w.t = 0; return w; }
            return null;
        }
        public static void freeWave(Wave w) { if (w != null) w.busy = false; }

        public static void tick(float dt)
        {
            for (int i = live.Count - 1; i >= 0; i--)
            {
                var s = live[i];
                s.t += dt;
                s.head = Mathf.Min(1.04f, s.t / s.dur * 1.04f);
                float after = s.t - s.dur;
                s.alpha = after <= 0 ? 1 : Mathf.Max(0, 1 - after / s.fade);
                if (after > s.fade || s.a == null || !s.a.alive) { s.busy = false; live.RemoveAt(i); }
            }
            draw();
        }
        public static void clear()
        {
            foreach (var s in live) s.busy = false;
            live.Clear();
            foreach (var w in waves) w.busy = false;
        }

        static void draw()
        {
            if (_mat == null) return;
            var rootM = ThreeSpace.root.localToWorldMatrix;
            var rp = new RenderParams(_mat) { shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false };
            foreach (var s in live)
            {
                var a = s.a;
                // the group rides the kid (yaw = the cut's heading); the tap arc mirrored per side (sweep direction) and
                // tilted into a diagonal (high at the start → low at the end)
                var grp = Matrix4x4.TRS(new Vector3(a.pos.x, a.pos.y + a.smoothY + (s.heavy ? 1.18f : 0.98f), a.pos.z), ThreeSpace.eulerXYZ(0, s.yaw, 0), Vector3.one);
                var loc = Matrix4x4.TRS(new Vector3(s.heavy ? -0.12f : 0, 0, 0), ThreeSpace.eulerXYZ(0, 0, s.heavy ? 0.2f : -s.side * 0.4f), new Vector3(s.heavy ? 1 : s.side, 1, 1));
                var cl = s.col.linear;
                s.mpb.SetVector(ID_Color, new Vector4(cl.r, cl.g, cl.b, 1));
                s.mpb.SetVector(ID_P, new Vector4(s.head, s.alpha, s.tail, s.heavy ? 1 : 0));
                s.mpb.SetFloat(ID_Mode, 0);
                rp.matProps = s.mpb;
                Graphics.RenderMesh(rp, s.heavy ? _geoV : _geoH, 0, rootM * grp * loc);
            }
            foreach (var w in waves)
            {
                if (!w.busy) continue;
                var cl = w.col.linear;
                w.mpb.SetVector(ID_Color, new Vector4(cl.r, cl.g, cl.b, 1));
                w.mpb.SetVector(ID_P, new Vector4(w.t, w.fade, 0, 0));
                w.mpb.SetFloat(ID_Mode, 1);
                rp.matProps = w.mpb;
                Graphics.RenderMesh(rp, _geoW, 0, rootM * w.m);
            }
        }
    }
}
