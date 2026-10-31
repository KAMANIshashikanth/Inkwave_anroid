// The stage dressing around the level — what main.js _buildWorldNow + the Environment put in the scene (prop kit, decor,
// sea, scenery, boats, gulls), baked in the browser by Tools/bake/pages/stage.js into Resources/Stages/<id>/:
//   <time>.json (objects, materials, textures, lights, theme), geo/g<n>.bytes (IWS1 meshes), tex/t<n>[.lin].png.
// load() rebuilds that scene under the three-space root: one GameObject per scene mesh (its world TRS from the bake),
// static instanced meshes (pilings, trees, bunting) merged into one mesh, animated ones (sailboats, buoys, gulls) one
// object per instance. Materials map by three material type onto Inkwave/Prop (lit / unlit / cut-out / transparent);
// custom shader chunks are first-pass stand-ins (see StageMaterials). Lighting, sky and fog: StageEnvironment.
//
// IWS1 = IWK1's layout (Tools/bake/iwk.mjs: 'IWS1', vertex count, index count, flags, stride, attribute count,
// {VertexAttribute, VertexAttributeFormat, dimension, 0} per attribute, interleaved vertices, indices) with 32-bit
// indices when flags & 2.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Inkwave.World
{
    public static class StageLoader
    {
        // ---- stage.json (JsonUtility: only the fields Unity reads)
        [Serializable] public class FogJ { public float[] color; public float near, far; }
        [Serializable] public class LightJ { public string type, name; public float[] color, groundColor, position, target; public float intensity; public bool visible, castShadow; }
        [Serializable] public class SkyJ { public string file; public int size; }
        [Serializable] public class MuralRow { public float[] rect, place, fx; }
        [Serializable] public class MuralsJ { public string tex; public MuralRow[] table; }
        [Serializable] public class GradeJ { public float uExposure = 1, uSat = 1, uVib, uContrast = 1, uLift, uVignette; public float[] uShadowTint, uHighTint, bloom; }
        [Serializable] public class ShaderU { public float[] uColor, uCol; public float uAlpha = 1; }
        [Serializable] public class EnvU { public float[] uSeaDeep, uSeaShallow, uSeaCrest, uHorizon, uSkyMid, uZenith, uSunDir; public float uNight; }
        [Serializable]
        public class MatJ
        {
            public int id, side, blending;
            public string type, name, custom, map, emissiveMap;
            public bool transparent, depthWrite = true, vertexColors, toneMapped = true, fog = true;
            public float opacity = 1, alphaTest, roughness = 1, metalness, clearcoat, clearcoatRoughness, emissiveIntensity = 1;
            public float[] color, emissive, polygonOffset;
            public string[] defineList;
            public ShaderU uniforms;
        }
        [Serializable] public class TexJ { public string file; public int wrapS, wrapT; public bool srgb; }
        [Serializable] public class AttrJ { public string name; public int channel, dim; }
        [Serializable]
        public class ObjJ
        {
            public string name, path, geo;
            public AttrJ[] attrs;
            public int[] mat, groups;
            public float[] pos, rot, scale, inst, instanceColors;
            public int instN, renderOrder;
            public bool visible, castShadow, receiveShadow;
        }
        [Serializable] public class SailJ { public float a, w, d, phase, s, amp; }
        [Serializable] public class BuoyJ { public float x, z, phase, s, amp; }
        [Serializable] public class GullJ { public float a0, w, cx, cz, r, h, s; }
        [Serializable] public class MooredJ { public string name; public float phase, roll, x, z, yaw, amp; }
        [Serializable] public class AnimJ { public float waterY = -1.6f; public SailJ[] sailboats; public BuoyJ[] buoys; public GullJ[] gulls; public MooredJ[] moored; }
        [Serializable]
        public class StageJ
        {
            public AnimJ anim;
            public string id, time, theme, quality;
            public float night, exposure = 1, environmentIntensity = 1;
            public FogJ fog;
            public LightJ[] lights;
            public SkyJ sky;
            public MuralsJ murals;
            public GradeJ grade;
            public EnvU envUniforms;
            public MatJ[] materials;
            public TexJ[] textures;
            public ObjJ[] objects;
        }

        /** the baked description of a stage at a time of day ("day" | "dusk"); null if not baked */
        public static StageJ describe(string id, string time)
        {
            var ta = Resources.Load<TextAsset>($"Stages/{id}/{time}");
            if (ta == null) return null;
            var j = JsonUtility.FromJson<StageJ>(ta.text);
            Resources.UnloadAsset(ta);
            return j;
        }

        // animated in StageEnvironment (environment.js _animate): kept one object per instance
        static readonly HashSet<string> ANIMATED = new HashSet<string> { "Sailboats", "Buoys", "Gulls" };

        /** build the dressing under the three-space root; the returned root owns every object + material it made */
        public static GameObject load(StageJ s)
        {
            var root = ThreeSpace.create("stage:" + s.id);
            var mats = new StageMaterials(s);
            var meshes = new Dictionary<string, Raw>();
            int tris = 0, objs = 0;
            foreach (var o in s.objects)
            {
                if (o.mat.Length == 0) continue;
                var mj = s.materials[o.mat[0]];
                var mat = mats.get(mj, o);
                if (mat == null) continue;   // not drawn (depth-only material)
                if (!meshes.TryGetValue(o.geo, out var raw)) meshes[o.geo] = raw = readIWS($"Stages/{s.id}/geo/{o.geo}");
                if (raw == null) continue;
                if (o.instN > 0 && !ANIMATED.Contains(o.name))
                {
                    // static instances → merged in world (three) space, cut into culling chunks
                    foreach (var m in emit(o.name, mergeInstances(raw, o))) { add(root, o, m, mat, Vector3.zero, Quaternion.identity, Vector3.one); objs++; }
                    tris += raw.ic / 3 * o.instN;
                }
                else if (o.instN > 0)
                {
                    var mesh = buildMesh(o.geo, raw);
                    var grp = new GameObject(o.name);
                    grp.transform.SetParent(root.transform, false);
                    for (int i = 0; i < o.instN; i++)
                    {
                        var m = matrix(o.inst, i);
                        var go = add(grp, o, mesh, mat, m.GetColumn(3), m.rotation, m.lossyScale);
                        go.name = $"{o.name}_{i}";
                        if (o.instanceColors != null && o.instanceColors.Length >= (i + 1) * 3)
                        {
                            var mpb = new MaterialPropertyBlock();
                            mpb.SetVector("_InstColor", new Vector4(o.instanceColors[i * 3], o.instanceColors[i * 3 + 1], o.instanceColors[i * 3 + 2], 1));   // linear
                            go.GetComponent<MeshRenderer>().SetPropertyBlock(mpb);
                        }
                    }
                    tris += raw.ic / 3 * o.instN; objs += o.instN;
                }
                else
                {
                    foreach (var m in emit(o.geo, geoOf(raw))) { add(root, o, m, mat, v3(o.pos), new Quaternion(o.rot[0], o.rot[1], o.rot[2], o.rot[3]), v3(o.scale)); objs++; }
                    tris += raw.ic / 3;
                }
            }
            root.AddComponent<StageOwner>().materials = mats;
            if (s.anim != null) root.AddComponent<StageAnimator>().init(s.anim);
            Debug.Log($"[Inkwave] stage dressing {s.id}/{s.time}: {objs} objects, {tris} tris, {mats.count} materials");
            return root;
        }

        static GameObject add(GameObject parent, ObjJ o, Mesh mesh, Material mat, Vector3 pos, Quaternion rot, Vector3 scale)
        {
            var go = new GameObject(o.name);
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = pos; go.transform.localRotation = rot; go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = o.castShadow ? ShadowCastingMode.On : ShadowCastingMode.Off;
            mr.receiveShadows = o.receiveShadow;
            go.SetActive(o.visible);
            return go;
        }

        static Vector3 v3(float[] a) => new Vector3(a[0], a[1], a[2]);
        static Matrix4x4 matrix(float[] a, int i)
        {
            int k = i * 16;   // three column-major
            return new Matrix4x4(new Vector4(a[k], a[k + 1], a[k + 2], a[k + 3]), new Vector4(a[k + 4], a[k + 5], a[k + 6], a[k + 7]),
                                 new Vector4(a[k + 8], a[k + 9], a[k + 10], a[k + 11]), new Vector4(a[k + 12], a[k + 13], a[k + 14], a[k + 15]));
        }

        // ---- IWS1
        class Raw
        {
            public int vc, ic, stride, posOfs = -1, nrmOfs = -1, colOfs = -1;
            public bool wide;
            public VertexAttributeDescriptor[] layout;
            public byte[] data; public int vOfs, iOfs;
        }

        static Raw readIWS(string path)
        {
            var ta = Resources.Load<TextAsset>(path);
            if (ta == null) { Debug.LogWarning("[inkwave] stage mesh missing: " + path); return null; }
            var b = ta.bytes;
            Resources.UnloadAsset(ta);
            if (b.Length < 24 || b[0] != 'I' || b[1] != 'W' || b[2] != 'S' || b[3] != '1') { Debug.LogWarning("[inkwave] bad IWS1 header: " + path); return null; }
            var r = new Raw { data = b };
            r.vc = BitConverter.ToInt32(b, 4); r.ic = BitConverter.ToInt32(b, 8); r.wide = (BitConverter.ToInt32(b, 12) & 2) != 0;
            r.stride = BitConverter.ToInt32(b, 16);
            int na = BitConverter.ToInt32(b, 20), ofs = 0;
            r.layout = new VertexAttributeDescriptor[na];
            for (int k = 0; k < na; k++)
            {
                var va = (VertexAttribute)b[24 + k * 4]; var fmt = (VertexAttributeFormat)b[25 + k * 4]; int dim = b[26 + k * 4];
                r.layout[k] = new VertexAttributeDescriptor(va, fmt, dim);
                if (va == VertexAttribute.Position) r.posOfs = ofs;
                if (va == VertexAttribute.Normal) r.nrmOfs = ofs;
                if (va == VertexAttribute.Color) r.colOfs = ofs;
                ofs += dim * (fmt == VertexAttributeFormat.Float32 ? 4 : fmt == VertexAttributeFormat.Float16 ? 2 : 1);
            }
            r.vOfs = 24 + na * 4;
            r.iOfs = r.vOfs + r.vc * r.stride;
            return r;
        }

        const MeshUpdateFlags QUIET = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds;

        // vertices (interleaved, the file's layout) + 32-bit indices, before they become one or more meshes
        class Geo { public VertexAttributeDescriptor[] layout; public byte[] vd; public int vOfs, vc, stride, posOfs; public int[] idx; }

        static Geo geoOf(Raw r)
        {
            var idx = new int[r.ic];
            if (r.wide) Buffer.BlockCopy(r.data, r.iOfs, idx, 0, r.ic * 4);
            else for (int k = 0; k < r.ic; k++) idx[k] = BitConverter.ToUInt16(r.data, r.iOfs + k * 2);
            return new Geo { layout = r.layout, vd = r.data, vOfs = r.vOfs, vc = r.vc, stride = r.stride, posOfs = r.posOfs, idx = idx };
        }

        static Mesh buildMesh(string name, Raw r) => build("stage:" + name, geoOf(r));

        static Vector3 pos(Geo g, int v) { int p = g.vOfs + v * g.stride + g.posOfs; return new Vector3(BitConverter.ToSingle(g.vd, p), BitConverter.ToSingle(g.vd, p + 4), BitConverter.ToSingle(g.vd, p + 8)); }

        static Mesh build(string name, Geo g)
        {
            int ic = g.idx.Length;
            bool wide = g.vc > 65535;
            var m = new Mesh { name = name, indexFormat = wide ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            m.SetVertexBufferParams(g.vc, g.layout);
            m.SetVertexBufferData(g.vd, g.vOfs, 0, g.vc * g.stride, 0, QUIET);
            if (wide) { m.SetIndexBufferParams(ic, IndexFormat.UInt32); m.SetIndexBufferData(g.idx, 0, 0, ic, QUIET); }
            else
            {
                var i16 = new ushort[ic]; for (int k = 0; k < ic; k++) i16[k] = (ushort)g.idx[k];
                m.SetIndexBufferParams(ic, IndexFormat.UInt16); m.SetIndexBufferData(i16, 0, 0, ic, QUIET);
            }
            m.subMeshCount = 1;
            m.SetSubMesh(0, new SubMeshDescriptor(0, ic), MeshUpdateFlags.DontRecalculateBounds);
            // bounds from the positions (the mesh keeps no CPU copy)
            Vector3 lo = Vector3.positiveInfinity, hi = Vector3.negativeInfinity;
            for (int i = 0; i < g.vc; i++) { var v = pos(g, i); lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v); }
            m.bounds = g.vc > 0 ? new Bounds((lo + hi) * 0.5f, hi - lo) : new Bounds();
            m.UploadMeshData(true);
            return m;
        }

        // Culling chunks: three draws each prop-kit material as ONE whole-stage mesh; here a big mesh is cut by triangle
        // centroid into square cells (object space; ≥ CHUNK m, at most CHUNK_N × CHUNK_N per mesh, so the kilometre-wide
        // sea and far scenery stay a handful of pieces) so frustum culling and the shadow pass skip what is out of view.
        // Small meshes stay whole.
        const int CHUNK_TRIS = 12000, CHUNK_N = 8;
        const float CHUNK = 20f;

        static List<Mesh> emit(string name, Geo g)
        {
            var list = new List<Mesh>();
            int tc = g.idx.Length / 3;
            if (tc <= CHUNK_TRIS) { list.Add(build("stage:" + name, g)); return list; }
            Vector3 lo = Vector3.positiveInfinity, hi = Vector3.negativeInfinity;
            for (int i = 0; i < g.vc; i++) { var v = pos(g, i); lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v); }
            float cell = Mathf.Max(CHUNK, Mathf.Max(hi.x - lo.x, hi.z - lo.z) / CHUNK_N);
            var cells = new Dictionary<long, List<int>>();
            for (int t = 0; t < tc; t++)
            {
                var c = (pos(g, g.idx[t * 3]) + pos(g, g.idx[t * 3 + 1]) + pos(g, g.idx[t * 3 + 2])) / 3f;
                long key = ((long)Mathf.FloorToInt((c.x - lo.x) / cell) << 32) ^ (uint)Mathf.FloorToInt((c.z - lo.z) / cell);
                if (!cells.TryGetValue(key, out var l)) cells[key] = l = new List<int>();
                l.Add(t);
            }
            var remap = new int[g.vc];
            int stamp = 0;
            var stampOf = new int[g.vc];
            foreach (var kv in cells)
            {
                stamp++;
                var tris = kv.Value;
                var verts = new List<int>();
                var idx = new int[tris.Count * 3];
                for (int k = 0; k < tris.Count; k++)
                    for (int j = 0; j < 3; j++)
                    {
                        int v = g.idx[tris[k] * 3 + j];
                        if (stampOf[v] != stamp) { stampOf[v] = stamp; remap[v] = verts.Count; verts.Add(v); }
                        idx[k * 3 + j] = remap[v];
                    }
                var vd = new byte[verts.Count * g.stride];
                for (int k = 0; k < verts.Count; k++) Buffer.BlockCopy(g.vd, g.vOfs + verts[k] * g.stride, vd, k * g.stride, g.stride);
                list.Add(build($"stage:{name}:{list.Count}", new Geo { layout = g.layout, vd = vd, vOfs = 0, vc = verts.Count, stride = g.stride, posOfs = g.posOfs, idx = idx }));
            }
            return list;
        }

        // every instance's copy of the template, transformed into world space (normals by the inverse transpose, colours
        // × the instance colour) — the pilings / trees / bunting as one mesh
        static Geo mergeInstances(Raw r, ObjJ o)
        {
            int n = o.instN, vc = r.vc * n, st = r.stride;
            var vd = new byte[vc * st];
            var src = geoOf(r).idx;
            var idx = new int[src.Length * n];
            for (int i = 0; i < n; i++)
            {
                var M = matrix(o.inst, i);
                var N = M.inverse.transpose;
                Color ic3 = o.instanceColors != null && o.instanceColors.Length >= (i + 1) * 3 ? new Color(o.instanceColors[i * 3], o.instanceColors[i * 3 + 1], o.instanceColors[i * 3 + 2]) : Color.white;
                Buffer.BlockCopy(r.data, r.vOfs, vd, i * r.vc * st, r.vc * st);
                for (int v = 0; v < r.vc; v++)
                {
                    int b = (i * r.vc + v) * st;
                    int p = b + r.posOfs;
                    var P = M.MultiplyPoint3x4(new Vector3(BitConverter.ToSingle(vd, p), BitConverter.ToSingle(vd, p + 4), BitConverter.ToSingle(vd, p + 8)));
                    putF(vd, p, P.x); putF(vd, p + 4, P.y); putF(vd, p + 8, P.z);
                    if (r.nrmOfs >= 0)
                    {
                        int q = b + r.nrmOfs;
                        var Nn = N.MultiplyVector(new Vector3(getH(vd, q), getH(vd, q + 2), getH(vd, q + 4))).normalized;
                        putH(vd, q, Nn.x); putH(vd, q + 2, Nn.y); putH(vd, q + 4, Nn.z);
                    }
                    if (r.colOfs >= 0 && ic3 != Color.white)
                    {
                        int c = b + r.colOfs;
                        vd[c] = (byte)Mathf.Clamp(Mathf.RoundToInt(vd[c] * ic3.r), 0, 255);
                        vd[c + 1] = (byte)Mathf.Clamp(Mathf.RoundToInt(vd[c + 1] * ic3.g), 0, 255);
                        vd[c + 2] = (byte)Mathf.Clamp(Mathf.RoundToInt(vd[c + 2] * ic3.b), 0, 255);
                    }
                }
                for (int k = 0; k < src.Length; k++) idx[i * src.Length + k] = src[k] + i * r.vc;
            }
            return new Geo { layout = r.layout, vd = vd, vOfs = 0, vc = vc, stride = st, posOfs = r.posOfs, idx = idx };
        }

        static void putF(byte[] b, int o, float v) { var x = BitConverter.GetBytes(v); b[o] = x[0]; b[o + 1] = x[1]; b[o + 2] = x[2]; b[o + 3] = x[3]; }
        static float getH(byte[] b, int o) => Mathf.HalfToFloat(BitConverter.ToUInt16(b, o));
        static void putH(byte[] b, int o, float v) { ushort h = Mathf.FloatToHalf(v); b[o] = (byte)h; b[o + 1] = (byte)(h >> 8); }
    }

    /** owns the stage's materials (destroyed with the dressing) */
    public class StageOwner : MonoBehaviour
    {
        public StageMaterials materials;
        void OnDestroy() => materials?.dispose();
    }
}
