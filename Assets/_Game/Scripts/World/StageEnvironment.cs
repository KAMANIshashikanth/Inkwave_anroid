// Lighting, sky, fog and post for a baked stage — the values environment.js setTheme + main.js leave on the renderer
// (sun, hemisphere sky fill, image-based light at scene.environmentIntensity, fog, sky dome, exposure), read from the
// stage bake (World/StageLoader.cs, Tools/bake/pages/stage.js).
//
// three → URP units: three's Lambert divides by π, URP's does not, so the sun's intensity is /π and the hemisphere
// light becomes radiance (sky above, ground below) × intensity / π. The ambient probe is projected from that
// hemisphere plus the image-based light (the ENV_PASS sky cube × environmentIntensity): each cube texel is a tiny
// directional light of weight dω/π, which reproduces three's diffuse IBL (PMREM irradiance / π). The same cube is the
// default reflection at reflectionIntensity = environmentIntensity.
// Cubes are baked in three space; Unity's world is three mirrored on Z, so the faces are rearranged on load (+Z ↔ −Z,
// every face flipped on the axis the mirror reverses).
// First-pass post (until renderer.js's grade pass is ported): URP neutral tonemapping, exposure, saturation, contrast,
// bloom on the tiers that have it.
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Inkwave.World
{
    public static class StageEnvironment
    {
        public static Cubemap sky, env;
        static Material skyMat;
        static GameObject volumeGo;
        static VolumeProfile profile;

        public static void apply(StageLoader.StageJ s, Light sun, Camera cam, bool shadows, bool bloom)
        {
            dispose();
            sky = loadCube($"Stages/{s.id}/{s.time}.sky", s.sky.size);
            env = loadCube($"Stages/{s.id}/{s.time}.env", s.sky.size) ?? sky;
            StageLoader.LightJ dl = null, hl = null;
            foreach (var L in s.lights) { if (L.type == "DirectionalLight") dl = L; if (L.type == "HemisphereLight") hl = L; }
            if (dl != null)
            {
                sun.type = LightType.Directional;
                sun.color = lin(dl.color).gamma;
                sun.intensity = dl.intensity / Mathf.PI;
                var dir = ThreeSpace.toUnity(new Vector3(dl.target[0] - dl.position[0], dl.target[1] - dl.position[1], dl.target[2] - dl.position[2]));
                sun.transform.rotation = Quaternion.LookRotation(dir.normalized);
            }
            sun.shadows = shadows ? LightShadows.Soft : LightShadows.None;
            // ambient + reflections
            float envK = s.environmentIntensity;
            RenderSettings.ambientMode = AmbientMode.Custom;
            RenderSettings.ambientProbe = ambientSH(env, s.sky.size, envK, hl);
            if (env != null)
            {
                RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
                RenderSettings.customReflectionTexture = env;
                RenderSettings.reflectionIntensity = envK;
            }
            // fog (three Fog: linear)
            RenderSettings.fog = s.fog != null && s.fog.color != null && s.fog.color.Length >= 3;
            if (RenderSettings.fog)
            {
                RenderSettings.fogMode = FogMode.Linear;
                RenderSettings.fogColor = lin(s.fog.color).gamma;
                RenderSettings.fogStartDistance = s.fog.near;
                RenderSettings.fogEndDistance = s.fog.far;
            }
            // sky
            if (sky != null)
            {
                skyMat = new Material(Shader.Find("Inkwave/Env/Sky"));
                skyMat.SetTexture("_Sky", sky);
                RenderSettings.skybox = skyMat;
                cam.clearFlags = CameraClearFlags.Skybox;
            }
            post(cam, s, bloom);
        }

        static Color lin(float[] c) => new Color(c[0], c[1], c[2], 1);

        // ---- cubes (RGBA half, faces +X −X +Y −Y +Z −Z in three's cube-camera layout)
        static Cubemap loadCube(string path, int n)
        {
            var ta = Resources.Load<TextAsset>(path);
            if (ta == null || n <= 0) return null;
            var b = ta.bytes;
            Resources.UnloadAsset(ta);
            int face = n * n * 4;
            if (b.Length != face * 2 * 6) { Debug.LogWarning($"[inkwave] bad sky cube {path}"); return null; }
            var src = new ushort[face * 6];
            System.Buffer.BlockCopy(b, 0, src, 0, b.Length);
            var cube = new Cubemap(n, TextureFormat.RGBAHalf, true) { name = path, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
            // Unity face ← (three face, flip): mirror z ⇒ +X/−X flip s, +Y/−Y flip t, +Z ← −Z and −Z ← +Z flipping s
            var map = new (CubemapFace f, int src, bool fs, bool ft)[]
            {
                (CubemapFace.PositiveX, 0, true, false), (CubemapFace.NegativeX, 1, true, false),
                (CubemapFace.PositiveY, 2, false, true), (CubemapFace.NegativeY, 3, false, true),
                (CubemapFace.PositiveZ, 5, true, false), (CubemapFace.NegativeZ, 4, true, false),
            };
            var dst = new ushort[face];
            foreach (var m in map)
            {
                int so = m.src * face;
                for (int t = 0; t < n; t++)
                    for (int s = 0; s < n; s++)
                    {
                        int ss = m.fs ? n - 1 - s : s, tt = m.ft ? n - 1 - t : t;
                        int d = (t * n + s) * 4, q = so + (tt * n + ss) * 4;
                        dst[d] = src[q]; dst[d + 1] = src[q + 1]; dst[d + 2] = src[q + 2]; dst[d + 3] = src[q + 3];
                    }
                cube.SetPixelData(dst, 0, m.f);
            }
            cube.Apply(true, true);
            src = null;
            // keep the three-layout data for the ambient projection
            lastData = b; lastN = n;
            return cube;
        }
        static byte[] lastData; static int lastN;

        // ---- ambient probe: hemisphere (radiance sky / ground × I/π) + the env cube × envK, projected as dω/π lights
        static SphericalHarmonicsL2 ambientSH(Cubemap envCube, int n, float envK, StageLoader.LightJ hemi)
        {
            var sh = new SphericalHarmonicsL2();
            Color hs = hemi != null ? lin(hemi.color) * (hemi.intensity / Mathf.PI) : Color.black;
            Color hg = hemi != null && hemi.groundColor != null ? lin(hemi.groundColor) * (hemi.intensity / Mathf.PI) : Color.black;
            byte[] b = envCube != null ? lastData : null;
            int N = b != null ? lastN : 16, step = Mathf.Max(1, N / 32), face = N * N * 4;
            for (int f = 0; f < 6; f++)
                for (int t = step / 2; t < N; t += step)
                    for (int s = step / 2; s < N; s += step)
                    {
                        float sc = 2f * (s + 0.5f) / N - 1f, tc = 2f * (t + 0.5f) / N - 1f;
                        Vector3 d3 = f switch
                        {
                            0 => new Vector3(1, -tc, -sc), 1 => new Vector3(-1, -tc, sc), 2 => new Vector3(sc, 1, tc),
                            3 => new Vector3(sc, -1, -tc), 4 => new Vector3(sc, -tc, 1), _ => new Vector3(-sc, -tc, -1),
                        };
                        float r2 = 1f + sc * sc + tc * tc;
                        float dw = 4f / (N * N) * step * step / (r2 * Mathf.Sqrt(r2));   // texel solid angle
                        var d = d3.normalized;
                        Color L = d.y > 0 ? hs : hg;
                        if (b != null)
                        {
                            int q = (f * face + (t * N + s) * 4) * 2;
                            L += new Color(Mathf.HalfToFloat(System.BitConverter.ToUInt16(b, q)), Mathf.HalfToFloat(System.BitConverter.ToUInt16(b, q + 2)),
                                           Mathf.HalfToFloat(System.BitConverter.ToUInt16(b, q + 4))) * envK;
                        }
                        sh.AddDirectionalLight(ThreeSpace.toUnity(d), L, dw / Mathf.PI);
                    }
            return sh;
        }

        // ---- post (first pass)
        static void post(Camera cam, StageLoader.StageJ s, bool bloom)
        {
            var g = s.grade ?? new StageLoader.GradeJ();
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            var tm = profile.Add<Tonemapping>(true); tm.mode.Override(TonemappingMode.Neutral);
            var ca = profile.Add<ColorAdjustments>(true);
            ca.postExposure.Override(Mathf.Log(Mathf.Max(1e-3f, s.exposure * g.uExposure), 2));
            ca.saturation.Override(Mathf.Clamp((g.uSat - 1) * 100, -100, 100));
            ca.contrast.Override(Mathf.Clamp((g.uContrast - 1) * 100, -100, 100));
            if (bloom)
            {
                var bl = profile.Add<Bloom>(true);
                var b = g.bloom != null && g.bloom.Length >= 3 ? g.bloom : new[] { 0.28f, 0.45f, 2.4f };   // renderer.js BLOOM
                bl.intensity.Override(b[0]); bl.scatter.Override(Mathf.Clamp01(b[1])); bl.threshold.Override(b[2]);
            }
            volumeGo = new GameObject("StagePost");
            var vol = volumeGo.AddComponent<Volume>();
            vol.isGlobal = true; vol.priority = 10; vol.sharedProfile = profile;
            var cd = cam.GetUniversalAdditionalCameraData();
            if (cd != null) cd.renderPostProcessing = true;
        }

        public static void dispose()
        {
            if (volumeGo != null) Object.Destroy(volumeGo);
            if (profile != null) Object.Destroy(profile);
            if (skyMat != null) Object.Destroy(skyMat);
            if (sky != null && sky != env) Object.Destroy(sky);
            if (env != null) Object.Destroy(env);
            sky = env = null; skyMat = null; volumeGo = null; profile = null; lastData = null;
        }
    }
}
