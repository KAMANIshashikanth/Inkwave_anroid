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

        public LevelView(Level level, PaintSystem paint, bool ripples = true)
        {
            var mesh = level.buildGeometry(paint.size);
            mesh.RecalculateBounds();
            mat = new Material(Shader.Find("Inkwave/Level"));
            mat.SetTexture("_Paint", paint.texture);
            mat.SetFloat("_AtlasSize", paint.size);
            if (ripples) mat.EnableKeyword("INK_RIPPLES");
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
