// three materials of a baked stage (Tools/bake/pages/stage.js descriptors) → Unity materials on Inkwave/Prop.
//   MeshStandard / MeshPhysical → lit (base colour × vertex colour × map, roughness, metalness, clear coat, emissive,
//                                 alpha test, side), MeshBasic → unlit, transparent → alpha / additive blended,
//   MeshDepthMaterial (custom shadow depth) → not drawn.
// environment.js patchScenery materials (HZ_*) → Inkwave/Env/Scenery, the Sea ShaderMaterial → Inkwave/Env/Sea.
// The lighthouse beam → Inkwave/Env/LighthouseBeam, the spawn pads' face / barrier → Inkwave/Env/SpawnPad /
// SpawnBarrier, cloth (bunting, banners, team flags) → Inkwave/Env/Cloth (drawn instanced by StageDecor).
// three colours are linear: they are written with SetVector (SetColor would treat them as sRGB).
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Inkwave.World
{
    public class StageMaterials
    {
        readonly StageLoader.StageJ s;
        readonly Dictionary<int, Material> made = new Dictionary<int, Material>();
        readonly Dictionary<string, Texture2D> texs = new Dictionary<string, Texture2D>();
        static Shader shader;
        public int count => made.Count;

        public StageMaterials(StageLoader.StageJ s) { this.s = s; }

        public Material get(StageLoader.MatJ m, StageLoader.ObjJ o)
        {
            if (made.TryGetValue(m.id, out var mat)) return mat;
            mat = make(m, o);
            made[m.id] = mat;
            return mat;
        }

        Material make(StageLoader.MatJ m, StageLoader.ObjJ o)
        {
            if (m.type == "MeshDepthMaterial" || m.type == "MeshDistanceMaterial") return null;
            if (m.type == "ShaderMaterial" && m.name == "Sea" && Shader.Find("Inkwave/Env/Sea") != null)
                return new Material(Shader.Find("Inkwave/Env/Sea")) { name = "stage:sea" };   // env globals: StageEnvironment
            if (m.type == "ShaderMaterial" && o.name == "LighthouseBeam" && m.uniforms?.uCol != null && m.uniforms.uCol.Length >= 3 && Shader.Find("Inkwave/Env/LighthouseBeam") != null)
            {
                var b = new Material(Shader.Find("Inkwave/Env/LighthouseBeam")) { name = "stage:lighthouse-beam", renderQueue = (int)RenderQueue.Transparent + o.renderOrder };
                b.SetVector("_Col", new Vector4(m.uniforms.uCol[0], m.uniforms.uCol[1], m.uniforms.uCol[2], 1));
                return b;
            }
            if (m.type == "ShaderMaterial" && o.name == "spawnPad:face" && Shader.Find("Inkwave/Env/SpawnPad") != null)
            {
                var pm = new Material(Shader.Find("Inkwave/Env/SpawnPad")) { name = "stage:spawn-pad" };
                var em = texture(m.uniforms?.uEmblem?.tex);
                if (em != null) pm.SetTexture("_Emblem", em);
                return pm;
            }
            if (m.type == "ShaderMaterial" && o.name == "spawnPad:barrier" && Shader.Find("Inkwave/Env/SpawnBarrier") != null)
                return new Material(Shader.Find("Inkwave/Env/SpawnBarrier")) { name = "stage:spawn-barrier", renderQueue = (int)RenderQueue.Transparent + o.renderOrder };
            if (shader == null) shader = Shader.Find("Inkwave/Prop");
            // environment.js patchScenery materials (customProgramCacheKey 'inkwave-env:<defines>') → the HZ port
            bool scenery = m.custom != null && m.custom.StartsWith("inkwave-env:") && Shader.Find("Inkwave/Env/Scenery") != null;
            var mat = new Material(scenery ? Shader.Find("Inkwave/Env/Scenery") : shader) { name = $"stage:{m.id}:{m.type}:{o.name}" };
            if (scenery)
            {
                bool has(string d) => m.defineList != null && System.Array.IndexOf(m.defineList, d) >= 0;
                string kw = has("HZ_CITY") ? "HZ_CITY" : has("HZ_TERRAIN") ? "HZ_TERRAIN_SHORE" : has("HZ_WATERLINE") ? "HZ_WATERLINE"
                          : has("HZ_SHORE") ? "HZ_SHORE" : has("HZ_GULL") ? "HZ_GULL" : null;
                if (kw != null) mat.EnableKeyword(kw);
                mat.SetVector("_BaseColor", m.color != null && m.color.Length >= 3 ? new Vector4(m.color[0], m.color[1], m.color[2], 1) : Vector4.one);
                mat.SetFloat("_UseVC", m.vertexColors ? 1 : 0);
                mat.SetFloat("_Roughness", m.roughness);
                mat.SetFloat("_Metallic", m.metalness);
                mat.SetFloat("_Cull", m.side == 2 ? (float)CullMode.Off : m.side == 1 ? (float)CullMode.Front : (float)CullMode.Back);
                return mat;
            }
            Vector4 col = m.color != null && m.color.Length >= 3 ? new Vector4(m.color[0], m.color[1], m.color[2], 1) : Vector4.one;
            float alpha = m.opacity;
            bool unlit = m.type == "MeshBasicMaterial";
            float rough = m.roughness, metal = m.metalness;
            bool transparent = m.transparent;
            int blending = m.blending;
            if (m.type == "ShaderMaterial")
            {
                if (m.name == "Sea" && s.envUniforms?.uSeaDeep != null && s.envUniforms.uSeaDeep.Length >= 3)
                {
                    var d = s.envUniforms.uSeaDeep; var sh = s.envUniforms.uSeaShallow ?? d;
                    col = Vector4.Lerp(new Vector4(d[0], d[1], d[2], 1), new Vector4(sh[0], sh[1], sh[2], 1), 0.35f);
                    rough = 0.12f; metal = 0;
                }
                else
                {
                    var c = m.uniforms?.uColor != null && m.uniforms.uColor.Length >= 3 ? m.uniforms.uColor : m.uniforms?.uCol;
                    if (c != null && c.Length >= 3) col = new Vector4(c[0], c[1], c[2], 1);
                    unlit = true;
                    if (transparent) alpha = m.uniforms != null ? Mathf.Clamp01(m.uniforms.uAlpha) : 0.5f;
                }
            }
            mat.SetVector("_BaseColor", new Vector4(col.x, col.y, col.z, alpha));
            mat.SetFloat("_UseVC", m.vertexColors ? 1 : 0);
            mat.SetFloat("_Roughness", rough);
            mat.SetFloat("_Metallic", metal);
            mat.SetFloat("_Cutoff", m.alphaTest);
            if (m.emissive != null && m.emissive.Length >= 3)
                mat.SetVector("_Emission", new Vector4(m.emissive[0], m.emissive[1], m.emissive[2], 0) * m.emissiveIntensity);
            var tex = texture(m.map);
            if (tex != null) mat.SetTexture("_BaseMap", tex);
            if (unlit) mat.EnableKeyword("PROP_UNLIT");
            if (m.defineList != null && System.Array.IndexOf(m.defineList, "HZ_GULL") >= 0) mat.EnableKeyword("PROP_GULL");
            if (m.clearcoat > 0 && !unlit)
            {
                mat.EnableKeyword("PROP_COAT");
                mat.SetFloat("_ClearCoat", m.clearcoat);
                mat.SetFloat("_ClearCoatRough", m.clearcoatRoughness);
            }
            // side: 0 front, 1 back, 2 double (Unity flips the cull of the mirrored root's children itself)
            mat.SetFloat("_Cull", m.side == 2 ? (float)CullMode.Off : m.side == 1 ? (float)CullMode.Front : (float)CullMode.Back);
            if (m.polygonOffset != null && m.polygonOffset.Length >= 2) { mat.SetFloat("_OffF", m.polygonOffset[0]); mat.SetFloat("_OffU", m.polygonOffset[1]); }
            if (transparent)
            {
                mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                mat.SetFloat("_DstBlend", (float)(blending == 2 ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
                mat.SetFloat("_ZWrite", m.depthWrite ? 1 : 0);
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.renderQueue = (int)RenderQueue.Transparent + o.renderOrder;
                mat.SetShaderPassEnabled("ShadowCaster", false);
            }
            else if (m.alphaTest > 0) mat.renderQueue = (int)RenderQueue.AlphaTest;
            return mat;
        }

        /** props.js clothMaterial / decor.js flagMaterial → Inkwave/Env/Cloth (instanced by StageDecor; not cached) */
        public Material cloth(StageLoader.MatJ m, bool flag)
        {
            var sh = Shader.Find("Inkwave/Env/Cloth");
            if (sh == null) return null;
            var mat = new Material(sh) { name = flag ? "stage:flag" : "stage:cloth", enableInstancing = true };
            var t = texture(m.map);
            if (t != null) mat.SetTexture("_BaseMap", t);
            mat.SetFloat("_Roughness", m.roughness);
            if (flag) mat.EnableKeyword("CLOTH_FLAG");
            return mat;
        }

        public Texture2D texture(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            if (texs.TryGetValue(file, out var t)) return t;
            t = Resources.Load<Texture2D>($"Stages/{s.id}/tex/{file}");
            if (t == null) Debug.LogWarning($"[inkwave] stage texture missing: {s.id}/tex/{file}");
            else
            {
                foreach (var tj in s.textures)
                    if (tj.file == file)
                    {
                        // three wrap: 1000 repeat, 1001 clamp, 1002 mirror
                        t.wrapModeU = tj.wrapS == 1000 ? TextureWrapMode.Repeat : tj.wrapS == 1002 ? TextureWrapMode.Mirror : TextureWrapMode.Clamp;
                        t.wrapModeV = tj.wrapT == 1000 ? TextureWrapMode.Repeat : tj.wrapT == 1002 ? TextureWrapMode.Mirror : TextureWrapMode.Clamp;
                    }
                t.anisoLevel = 4;
            }
            texs[file] = t;
            return t;
        }

        public Texture2D muralTexture() => texture(s.murals?.tex);

        public void dispose()
        {
            foreach (var m in made.Values) if (m != null) Object.Destroy(m);
            made.Clear();
        }
    }
}
