// Runtime side of src/game/character-geo.js: the rig tables (BONE_NAMES / BONE_PARENT / REST …) and the shared, cached
// kid meshes. The meshes themselves are not rebuilt here — Tools/bake/character.mjs runs INKWAVE's own generators
// under Node and writes them to Resources/Kid (kid.json + one IWK1 file per mesh, layout documented in the baker).
// Loading an IWK1 file is a straight copy into the vertex / index buffers.
//
// Vertex channels (shared by every kid shader): POSITION · NORMAL · TEXCOORD0 = uv · TEXCOORD1 = color (rgb) ·
// TEXCOORD2 = aEx | aTint | aMat · TEXCOORD3 = (aHead | aCloth | aHair | aEyeS | aSq | aSeg).xyz + aOcc · TEXCOORD4 = aFace ·
// TEXCOORD5 = rest position (skinned meshes; the vertex shader only sees the skinned one)
using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Inkwave
{
    public static class CharacterGeo
    {
        public const int HAIR_MAX = 8, HAIR_SEGS = 3;
        public static readonly string[] TIERS = { "hero", "game", "far" };

        [Serializable] class BoneRec { public string name; public int parent; public float[] rest; }
        [Serializable] class TankRec { public float[] offset, center, offsetTank; public float tilt, fillBottom, fillHeight; }
        [Serializable] class StrandRec { public float[] dir; public float len, K, G; }
        [Serializable] class HairRec { public int hair, hat, dropped; public string name, hatName; public float[] rest; public StrandRec[] strands; }
        [Serializable] class Manifest { public int version; public string quality; public string[] tiers; public BoneRec[] bones; public TankRec tank; public HairRec[] hairs; public string[] hairBones; }

        public class Strand { public Vector3 dir; public float len, K, G; }
        public class HairStyle { public Mesh geo; public Dictionary<string, Vector3> rest; public Strand[] meta; public string name, hat; public int dropped; }
        public class Tank { public Vector3 offset, center, offsetTank; public float tilt, fillBottom, fillHeight; public Mesh glass, fill; }
        public class Squid { public Mesh body, dark, eyes; }
        public class KidShared { public Mesh skin, cloth, eyes; public Tank tank; public Squid squid; }

        public static string[] BONE_NAMES;
        public static Dictionary<string, int> BONE_INDEX;
        public static Dictionary<string, string> BONE_PARENT;
        public static Dictionary<string, Vector3> REST;
        static Manifest _man;
        static readonly Dictionary<string, KidShared> _shared = new Dictionary<string, KidShared>();
        static readonly Dictionary<string, HairStyle> _hair = new Dictionary<string, HairStyle>();
        static readonly Dictionary<string, Mesh> _brows = new Dictionary<string, Mesh>();

        static Vector3 v3(float[] a, int i = 0) => new Vector3(a[i], a[i + 1], a[i + 2]);

        // the rig tables are read by other classes' static initialisers (Character's ANKLE_H …): load on first touch
        static CharacterGeo() { load(); }

        static void load()
        {
            if (_man != null) return;
            var ta = Resources.Load<TextAsset>("Kid/kid");
            if (ta == null) throw new Exception("[character] Resources/Kid/kid.json missing — run Tools/bake/character.mjs");
            _man = JsonUtility.FromJson<Manifest>(ta.text);
            int n = _man.bones.Length;
            BONE_NAMES = new string[n]; BONE_INDEX = new Dictionary<string, int>(); BONE_PARENT = new Dictionary<string, string>(); REST = new Dictionary<string, Vector3>();
            for (int i = 0; i < n; i++) { BONE_NAMES[i] = _man.bones[i].name; BONE_INDEX[BONE_NAMES[i]] = i; }
            for (int i = 0; i < n; i++)
            {
                var b = _man.bones[i];
                BONE_PARENT[b.name] = b.parent >= 0 ? BONE_NAMES[b.parent] : null;
                REST[b.name] = v3(b.rest);
            }
        }

        public static void ensureLoaded() => load();

        // A tier that was not baked falls back to its neighbour (the phone set has game + far only)
        static string bakedTier(string tier)
        {
            load();
            if (Array.IndexOf(_man.tiers, tier) >= 0) return tier;
            return tier == "hero" ? "game" : _man.tiers[0];
        }

        /** Shared kid meshes for a LOD tier ('hero' · 'game' · 'far'). */
        public static KidShared getKidShared(string tier)
        {
            tier = bakedTier(tier);
            if (_shared.TryGetValue(tier, out var sh)) return sh;
            var T = _man.tank;
            sh = new KidShared
            {
                skin = loadMesh(tier + "/skin", REST),
                cloth = loadMesh(tier + "/cloth", REST),
                eyes = loadMesh(tier + "/eyes", REST),
                tank = new Tank
                {
                    offset = v3(T.offset), center = v3(T.center), offsetTank = v3(T.offsetTank), tilt = T.tilt, fillBottom = T.fillBottom, fillHeight = T.fillHeight,
                    glass = loadMesh(tier + "/tankGlass", null), fill = loadMesh(tier + "/tankFill", null),
                },
                squid = new Squid { body = loadMesh(tier + "/squidBody", null), dark = loadMesh(tier + "/squidDark", null), eyes = loadMesh(tier + "/squidEyes", null) },
            };
            _shared[tier] = sh;
            return sh;
        }

        static HairRec hairRec(int hair, int hat)
        {
            foreach (var h in _man.hairs) if (h.hair == hair && h.hat == hat) return h;
            return _man.hairs[0];
        }

        static int wrapN(int v, int n) => ((v % n) + n) % n;

        /** Hair mesh of a style at a LOD tier: tentacles, scalp cap, accessories, headgear and the brow strokes. */
        public static HairStyle getHairStyle(CharacterStyle.Style st, string tier)
        {
            load();
            tier = bakedTier(tier);
            int hair = wrapN(st.hair, CharacterStyle.HAIR_STYLES), hat = wrapN(st.hat, CharacterStyle.HATS.Length), brows = wrapN(st.brows, CharacterStyle.BROWS.Length);
            string key = $"{hair}.{hat}.{brows}.{tier}";
            if (_hair.TryGetValue(key, out var hs)) return hs;
            var rec = hairRec(hair, hat);
            var rest = getRestPositions(st);
            var main = readIWK(tier + "/hair_" + hair + "_" + hat);
            var bk = tier + "/brows_" + brows;
            var brw = readIWK(bk);
            hs = new HairStyle
            {
                geo = buildMesh("hair_" + key, join(main, brw), rest),
                rest = rest, name = rec.name, hat = rec.hatName, dropped = rec.dropped,
                meta = Array.ConvertAll(rec.strands, s => new Strand { dir = v3(s.dir), len = s.len, K = s.K, G = s.G }),
            };
            _hair[key] = hs;
            return hs;
        }

        /** Rest position of every bone for a style (the hair bones depend on hair + hat). */
        public static Dictionary<string, Vector3> getRestPositions(CharacterStyle.Style st)
        {
            load();
            var rec = hairRec(wrapN(st.hair, CharacterStyle.HAIR_STYLES), wrapN(st.hat, CharacterStyle.HATS.Length));
            var o = new Dictionary<string, Vector3>(REST);
            for (int i = 0; i < _man.hairBones.Length; i++) o[_man.hairBones[i]] = v3(rec.rest, i * 3);
            return o;
        }

        // ---- IWK1 -----------------------------------------------------------------------------------------------
        class Raw
        {
            public int vc, ic, stride; public bool skinned;
            public VertexAttributeDescriptor[] layout;
            public byte[] data; public int vOfs, sOfs, iOfs;
        }

        static Raw readIWK(string path, string folder = "Kid/")
        {
            var ta = Resources.Load<TextAsset>(folder + path);
            if (ta == null) throw new Exception("[character] missing baked mesh " + folder + path);
            var b = ta.bytes;
            if (b[0] != 'I' || b[1] != 'W' || b[2] != 'K' || b[3] != '1') throw new Exception("[character] bad IWK1 header " + path);
            var r = new Raw { data = b };
            r.vc = BitConverter.ToInt32(b, 4); r.ic = BitConverter.ToInt32(b, 8); r.skinned = (BitConverter.ToInt32(b, 12) & 1) != 0;
            r.stride = BitConverter.ToInt32(b, 16);
            int na = BitConverter.ToInt32(b, 20);
            r.layout = new VertexAttributeDescriptor[na];
            for (int k = 0; k < na; k++)
                r.layout[k] = new VertexAttributeDescriptor((VertexAttribute)b[24 + k * 4], (VertexAttributeFormat)b[25 + k * 4], b[26 + k * 4]);
            r.vOfs = 24 + na * 4;
            r.sOfs = r.vOfs + r.vc * r.stride;
            r.iOfs = r.sOfs + (r.skinned ? r.vc * 8 : 0);
            Resources.UnloadAsset(ta);
            return r;
        }

        // Concatenate two meshes of the same layout (hair + brows)
        static Raw join(Raw a, Raw b)
        {
            if (a.stride != b.stride || a.skinned != b.skinned) throw new Exception("[character] join: layouts differ");
            int vc = a.vc + b.vc, ic = a.ic + b.ic;
            var r = new Raw { vc = vc, ic = ic, stride = a.stride, skinned = a.skinned, layout = a.layout };
            r.vOfs = 0; r.sOfs = vc * a.stride; r.iOfs = r.sOfs + (a.skinned ? vc * 8 : 0);
            r.data = new byte[r.iOfs + ic * 2];
            Buffer.BlockCopy(a.data, a.vOfs, r.data, 0, a.vc * a.stride);
            Buffer.BlockCopy(b.data, b.vOfs, r.data, a.vc * a.stride, b.vc * b.stride);
            if (a.skinned)
            {
                Buffer.BlockCopy(a.data, a.sOfs, r.data, r.sOfs, a.vc * 4);                         // indices
                Buffer.BlockCopy(b.data, b.sOfs, r.data, r.sOfs + a.vc * 4, b.vc * 4);
                Buffer.BlockCopy(a.data, a.sOfs + a.vc * 4, r.data, r.sOfs + vc * 4, a.vc * 4);      // weights
                Buffer.BlockCopy(b.data, b.sOfs + b.vc * 4, r.data, r.sOfs + vc * 4 + a.vc * 4, b.vc * 4);
            }
            Buffer.BlockCopy(a.data, a.iOfs, r.data, r.iOfs, a.ic * 2);
            for (int i = 0; i < b.ic; i++)
            {
                int v = BitConverter.ToUInt16(b.data, b.iOfs + i * 2) + a.vc;
                r.data[r.iOfs + (a.ic + i) * 2] = (byte)(v & 0xff); r.data[r.iOfs + (a.ic + i) * 2 + 1] = (byte)(v >> 8);
            }
            return r;
        }

        static Mesh loadMesh(string path, Dictionary<string, Vector3> rest) => buildMesh(path, readIWK(path), rest);

        /** A rigid (unskinned) baked mesh from any Resources folder (weapons: "KidWeapons/"); null when not baked.
         *  readable: keep the CPU copy when a caller reads .vertices (the boomerang's spin-blur disc measures it). */
        public static Mesh loadRigid(string folder, string path, bool readable = false)
        {
            if (Resources.Load<TextAsset>(folder + path) == null) return null;
            return buildMesh(path, readIWK(path, folder), null, readable);
        }

        static Mesh buildMesh(string name, Raw r, Dictionary<string, Vector3> rest, bool readable = false)
        {
            var m = new Mesh { name = "kid:" + name };
            m.SetVertexBufferParams(r.vc, r.layout);
            m.SetVertexBufferData(r.data, r.vOfs, 0, r.vc * r.stride, 0, MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds);
            var idx = new ushort[r.ic];
            Buffer.BlockCopy(r.data, r.iOfs, idx, 0, r.ic * 2);
            m.SetIndexBufferParams(r.ic, IndexFormat.UInt16);
            m.SetIndexBufferData(idx, 0, 0, r.ic, MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds);
            m.subMeshCount = 1;
            m.SetSubMesh(0, new SubMeshDescriptor(0, r.ic), MeshUpdateFlags.DontRecalculateBounds);
            if (r.skinned)
            {
                // bone weights (sorted descending in the file; zero weights dropped)
                var per = new NativeArray<byte>(r.vc, Allocator.Temp);
                var list = new List<BoneWeight1>(r.vc * 2);
                for (int i = 0; i < r.vc; i++)
                {
                    byte n = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        byte w = r.data[r.sOfs + r.vc * 4 + i * 4 + k];
                        if (w == 0) continue;
                        list.Add(new BoneWeight1 { boneIndex = r.data[r.sOfs + i * 4 + k], weight = w / 255f });
                        n++;
                    }
                    if (n == 0) { list.Add(new BoneWeight1 { boneIndex = 0, weight = 1 }); n = 1; }
                    per[i] = n;
                }
                var ws = new NativeArray<BoneWeight1>(list.ToArray(), Allocator.Temp);
                m.SetBoneWeights(per, ws);
                per.Dispose(); ws.Dispose();
                // bind poses: every bone's rest orientation is identity, so the inverse bind is a pure translation
                var bp = new Matrix4x4[BONE_NAMES.Length];
                for (int i = 0; i < bp.Length; i++) bp[i] = Matrix4x4.Translate(-rest[BONE_NAMES[i]]);
                m.bindposes = bp;
                m.bounds = new Bounds(new Vector3(0, 0.75f, 0), Vector3.one * 2.6f);   // Builder's bounding sphere (0, 0.75, 0) r 1.3
            }
            else m.RecalculateBounds();
            m.UploadMeshData(!readable);
            return m;
        }
    }
}
