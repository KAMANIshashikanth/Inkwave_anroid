// Port of environment.js update / _animate for a baked stage: moored boats bob and roll, sailboats circle the bay,
// buoys bob and tilt, gulls circle and bank (their wings flap in Inkwave/Prop, PROP_GULL), the ferris wheel turns, the
// lighthouse beam sweeps. The specs come from the bake (stage.json anim); waterHeightAt = waterY + swell(x, z, t) × amp
// with amp read back per spot by the exporter. Everything runs in three space (children of the mirrored root), so the
// JS numbers apply unchanged; three Euler orders become quaternion products (XYZ = qx·qy·qz, YXZ = qy·qx·qz).
// Sets the global _StageTime (environment time) for the stage shaders.
using System.Collections.Generic;
using UnityEngine;

namespace Inkwave.World
{
    public class StageAnimator : MonoBehaviour
    {
        StageLoader.AnimJ A;
        readonly List<Transform> sails = new List<Transform>(), buoys = new List<Transform>(), gulls = new List<Transform>();
        readonly List<(Transform t, StageLoader.MooredJ m)> moored = new List<(Transform, StageLoader.MooredJ)>();
        readonly List<(Transform t, Quaternion q0)> ferris = new List<(Transform, Quaternion)>(), beam = new List<(Transform, Quaternion)>();
        float time;
        static readonly int StageTime = Shader.PropertyToID("_StageTime");

        public void init(StageLoader.AnimJ a)
        {
            A = a;
            int mi = 0;
            foreach (Transform c in transform)
            {
                if (c.name == "Sailboats") foreach (Transform k in c) sails.Add(k);
                else if (c.name == "Buoys") foreach (Transform k in c) buoys.Add(k);
                else if (c.name == "Gulls") foreach (Transform k in c) gulls.Add(k);
                else if (c.name.StartsWith("Moored_") && A.moored != null && mi < A.moored.Length) moored.Add((c, A.moored[mi++]));
                else if (c.name == "FerrisWheel") ferris.Add((c, c.localRotation));
                else if (c.name == "LighthouseBeam") beam.Add((c, c.localRotation));
            }
            animate(0);
        }

        void Update()
        {
            time += Mathf.Min(Time.deltaTime, 0.1f);
            animate(time);
        }

        static float swell(float x, float z, float t) =>
            0.45f * Mathf.Sin(x * 0.11f + z * 0.047f + t * 0.95f) + 0.35f * Mathf.Sin(-x * 0.052f + z * 0.097f + t * 1.13f + 1.7f) + 0.2f * Mathf.Sin(x * 0.173f - z * 0.141f + t * 1.61f + 4.1f);
        float water(float x, float z, float amp, float t) => A.waterY + swell(x, z, t) * amp;

        static Quaternion ax(float a) => Quaternion.AngleAxis(a * Mathf.Rad2Deg, Vector3.right);
        static Quaternion ay(float a) => Quaternion.AngleAxis(a * Mathf.Rad2Deg, Vector3.up);
        static Quaternion az(float a) => Quaternion.AngleAxis(a * Mathf.Rad2Deg, Vector3.forward);

        void animate(float t)
        {
            Shader.SetGlobalFloat(StageTime, t);
            if (A == null) return;
            // moored boats: gentle bob + roll
            foreach (var (tr, m) in moored)
            {
                tr.localPosition = new Vector3(m.x, water(m.x, m.z, m.amp, t) + 0.03f * Mathf.Sin(t * 1.3f + m.phase), m.z);
                tr.localRotation = ax(0.012f * Mathf.Sin(t * 0.7f + m.phase * 2)) * ay(m.yaw) * az(m.roll * Mathf.Sin(t * 0.9f + m.phase));
            }
            // sailboats circle the bay slowly
            for (int i = 0; i < sails.Count && A.sailboats != null && i < A.sailboats.Length; i++)
            {
                var s = A.sailboats[i];
                float a = s.a + s.w * t, sg = Mathf.Sign(s.w);
                float x = Mathf.Cos(a) * s.d, z = Mathf.Sin(a) * s.d;
                float heading = Mathf.Atan2(-Mathf.Sin(a) * sg, Mathf.Cos(a) * sg);
                sails[i].localPosition = new Vector3(x, water(x, z, s.amp, t), z);
                sails[i].localRotation = ay(heading) * ax(0.03f * Mathf.Sin(t * 0.8f + s.phase)) * az((0.13f + 0.05f * Mathf.Sin(t * 0.6f + s.phase)) * sg);
                sails[i].localScale = Vector3.one * s.s;
            }
            // buoys bob and tilt
            for (int i = 0; i < buoys.Count && A.buoys != null && i < A.buoys.Length; i++)
            {
                var b = A.buoys[i];
                buoys[i].localPosition = new Vector3(b.x, water(b.x, b.z, b.amp, t) + 0.06f * Mathf.Sin(t * 1.7f + b.phase), b.z);
                buoys[i].localRotation = ay(b.phase) * ax(0.09f * Mathf.Sin(t * 1.1f + b.phase)) * az(0.09f * Mathf.Cos(t * 0.93f + b.phase * 1.3f));
                buoys[i].localScale = Vector3.one * b.s;
            }
            // gulls circle + bank
            for (int i = 0; i < gulls.Count && A.gulls != null && i < A.gulls.Length; i++)
            {
                var g = A.gulls[i];
                float a = g.a0 + g.w * t, dir = Mathf.Sign(g.w);
                gulls[i].localPosition = new Vector3(g.cx + Mathf.Cos(a) * g.r, g.h + Mathf.Sin(t * 0.4f + g.a0) * 1.5f, g.cz + Mathf.Sin(a) * g.r);
                gulls[i].localRotation = ay(Mathf.Atan2(-Mathf.Sin(a) * dir, Mathf.Cos(a) * dir)) * az(-0.35f * dir);
                gulls[i].localScale = Vector3.one * g.s;
            }
            // ferris wheel + lighthouse beam
            foreach (var (tr, q0) in ferris) tr.localRotation = q0 * az(t * 0.045f);
            foreach (var (tr, q0) in beam) tr.localRotation = q0 * ay(t * 0.55f);
        }
    }
}
