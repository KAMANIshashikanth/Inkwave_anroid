// The rendered level: Level.buildGeometry() under the world root with the Level/ink material (the part of
// src/main.js + levelMaterial.js createLevelMaterial that wires the paint atlas into the surface shader).
using UnityEngine;
using UnityEngine.Rendering;

namespace Inkwave.World
{
    public class LevelView
    {
        public readonly GameObject go;
        public readonly Material mat;
        readonly Vector4[] ripU = new Vector4[PaintSystem.RIP_N];

        public readonly Texture2D lightmap;

        /** texlib: the texture library is bound (Texlib.bind) — surface patterns on, else the procedural fallback */
        public LevelView(Level level, PaintSystem paint, bool ripples = true, string layoutId = null, bool texlib = false)
        {
            // baked AO first: it lays out the faces' light rects that buildGeometry writes into the mesh
            lightmap = layoutId != null ? Lightmaps.load(layoutId, level) : null;
            var mesh = level.buildGeometry(paint.size);
            mesh.RecalculateBounds();
            mat = new Material(Shader.Find("Inkwave/Level"));
            mat.SetTexture("_Paint", paint.texture);
            mat.SetFloat("_AtlasSize", paint.size);
            if (lightmap != null) { mat.SetTexture("_Light", lightmap); mat.SetFloat("_AO", 1); }
            if (ripples) mat.EnableKeyword("INK_RIPPLES");
            if (texlib) mat.EnableKeyword("USE_TEXLIB");
            go = ThreeSpace.create("level");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.On;
            mr.receiveShadows = true;
        }

        // team ink colours for every ink-reading shader (sRGB in, linear out)
        public static void setTeamColors(Color a, Color b)
        {
            Shader.SetGlobalVector("_TeamA", a.linear);
            Shader.SetGlobalVector("_TeamB", b.linear);
        }

        // the stage's mural / signage atlas + its placement table (murals.js, from the stage bake); null = no murals.
        // Missing rows take levelMaterial.js's defaults.
        public static void setMurals(Texture tex, StageLoader.MuralRow[] rows)
        {
            const int MUR = 12;
            var A = new Vector4[MUR]; var B = new Vector4[MUR]; var C = new Vector4[MUR];
            for (int i = 0; i < MUR; i++)
            {
                var r = rows != null && i < rows.Length ? rows[i] : null;
                A[i] = r?.rect != null && r.rect.Length >= 4 ? new Vector4(r.rect[0], r.rect[1], r.rect[2], r.rect[3]) : new Vector4(0, 1, 0, 0.25f);
                B[i] = r?.place != null && r.place.Length >= 4 ? new Vector4(r.place[0], r.place[1], r.place[2], r.place[3]) : new Vector4(0, -8, 0, 0);
                C[i] = r?.fx != null && r.fx.Length >= 2 ? new Vector4(r.fx[0], r.fx[1], 0, 0) : Vector4.zero;
            }
            Shader.SetGlobalVectorArray("_MurA", A);
            Shader.SetGlobalVectorArray("_MurB", B);
            Shader.SetGlobalVectorArray("_MurC", C);
            if (tex != null) Shader.SetGlobalTexture("_Mural", tex);
            Shader.SetGlobalFloat("_MurOn", tex != null ? 1 : 0);
        }

        // per frame: the paint clock + the ripple table (three space → Unity world: z mirrored)
        public void update(PaintSystem paint)
        {
            Shader.SetGlobalFloat("_InkClock", paint.clock);
            for (int i = 0; i < ripU.Length; i++) { var r = paint.rip[i]; ripU[i] = new Vector4(r.x, r.y, -r.z, r.w); }
            Shader.SetGlobalVectorArray("_Rip", ripU);
            Shader.SetGlobalVectorArray("_RipP", paint.ripP);
        }

        public void dispose()
        {
            Ctx.destroy(go);
            Ctx.destroy(mat);
        }
    }
}
