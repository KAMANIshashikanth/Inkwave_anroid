// Runtime side of src/game/character-mats.js: the per-character materials (skin, cloth, hair, eyes, squid, tank fill,
// glow — one Material each, carrying that kid's uniform bundle) and the shared weapon materials (plastic, team ink by
// colour, lamps, charge coils). Shaders: Shaders/Kid/Kid.shader (_Kind picks the part) and KidGlass.shader.
// Colours: hex / team colours are sRGB → SetColor (Unity linearises them); values three computes in linear space
// (glow = colour × k, emissive pulses, hurt tint) → SetVector with the linear colour.
using System.Collections.Generic;
using UnityEngine;

namespace Inkwave
{
    public class CharacterMats
    {
        public static readonly int ID_Emissive = Shader.PropertyToID("_Emissive");
        static readonly int ID_Kind = Shader.PropertyToID("_Kind"), ID_Base = Shader.PropertyToID("_BaseColor"), ID_Team = Shader.PropertyToID("_Team"),
            ID_Skin = Shader.PropertyToID("_Skin"), ID_Shirt = Shader.PropertyToID("_Shirt"), ID_Shorts = Shader.PropertyToID("_Shorts"), ID_Shoe = Shader.PropertyToID("_Shoe"),
            ID_Sole = Shader.PropertyToID("_Sole"), ID_Sock = Shader.PropertyToID("_Sock"), ID_Strap = Shader.PropertyToID("_Strap"), ID_Pattern = Shader.PropertyToID("_Pattern"),
            ID_Iris = Shader.PropertyToID("_Iris"), ID_Iris2 = Shader.PropertyToID("_Iris2"), ID_Hurt = Shader.PropertyToID("_Hurt"), ID_HurtSeed = Shader.PropertyToID("_HurtSeed"),
            ID_Flash = Shader.PropertyToID("_Flash"), ID_Glow = Shader.PropertyToID("_Glow"), ID_Look = Shader.PropertyToID("_Look"), ID_Gaze = Shader.PropertyToID("_Gaze"),
            ID_Lid = Shader.PropertyToID("_Lid"), ID_Pupil = Shader.PropertyToID("_Pupil"), ID_Mouth = Shader.PropertyToID("_Mouth"), ID_Mouth2 = Shader.PropertyToID("_Mouth2"),
            ID_Wig = Shader.PropertyToID("_Wig"), ID_Clock = Shader.PropertyToID("_Clock"), ID_Rim = Shader.PropertyToID("_Rim"), ID_RimL = Shader.PropertyToID("_RimL"),
            ID_Fill = Shader.PropertyToID("_Fill"), ID_RimK = Shader.PropertyToID("_RimK"), ID_LodFade = Shader.PropertyToID("_LodFade"), ID_EmissiveI = Shader.PropertyToID("_EmissiveI"),
            ID_Rough = Shader.PropertyToID("_Rough"), ID_Metal = Shader.PropertyToID("_Metal"), ID_Coil = Shader.PropertyToID("_Coil"), ID_Ghost = Shader.PropertyToID("_Ghost");

        // _Kind values (Kid.shader)
        const int K_SKIN = 0, K_CLOTH = 1, K_HAIR = 2, K_EYE = 3, K_SQUID = 4, K_DARK = 5, K_PLASTIC = 6, K_INK = 7, K_LAMP = 8, K_COIL = 9, K_FILL = 10, K_GLOW = 11, K_MATTE = 12;

        static Shader _body, _glassShader;
        static Shader body => _body ??= Shader.Find("Inkwave/Kid/Body");
        static Shader glassShader => _glassShader ??= Shader.Find("Inkwave/Kid/Glass");

        static Material make(int kind, string name)
        {
            var m = new Material(body) { name = "iw-" + name };
            m.SetFloat(ID_Kind, kind);
            m.SetVector(ID_Rim, new Vector4(0, 0, 0, 3));
            return m;
        }
        static Color hex(string h) => ColorUtility.TryParseHtmlString(h, out var c) ? c : Color.white;

        // ---- shared materials ------------------------------------------------------------------------------------
        static Material _plastic, _dark, _glass, _glowShared;
        static readonly Dictionary<Color, Material> _ink = new Dictionary<Color, Material>();
        /** Weapon / prop plastic: vertex colour with per-vertex surface class (aMat). */
        public static Material plastic => _plastic ??= make(K_PLASTIC, "plastic");
        /** Squid visor band: vertex-coloured dark lacquer. */
        public static Material dark => _dark ??= make(K_DARK, "dark");
        /** Ink tank glass capsule. */
        public static Material glass => _glass ??= new Material(glassShader) { name = "iw-glass", renderQueue = 3002 };
        public static Material glowShared => _glowShared ??= make(K_GLOW, "glow");
        /** Team-ink gloss, shared per colour. */
        public static Material ink(Color c)
        {
            if (_ink.TryGetValue(c, out var m)) return m;
            m = make(K_INK, "ink"); m.SetColor(ID_Base, c);
            _ink[c] = m;
            return m;
        }
        /** Per-instance glowing material for lamp parts (LEDs, scope lenses, hot muzzle ports). */
        public static Material lamp(WeaponModel.Lamp spec)
        {
            var m = make(K_LAMP, "lamp");
            m.SetColor(ID_Base, spec?.color ?? new Color(0.133f, 0.133f, 0.133f));
            m.SetColor(ID_Emissive, spec?.emissive ?? Color.white);
            m.SetFloat(ID_EmissiveI, spec?.intensity ?? 1);
            m.SetFloat(ID_Rough, spec?.roughness ?? 0.25f);
            return m;
        }
        /** A plain lit material (MeshStandardMaterial stand-in for world props: bomb caps, storm clouds …). Colours are
         *  linear (three's Color maths); emissive = colour × intensity. */
        public static Material matte(Color linearBase, Color linearEmissive, float intensity, float rough, float metal)
        {
            var m = make(K_MATTE, "matte");
            m.SetVector(ID_Base, new Vector4(linearBase.r, linearBase.g, linearBase.b, 1));
            m.SetVector(ID_Emissive, new Vector4(linearEmissive.r, linearEmissive.g, linearEmissive.b, 1));
            m.SetFloat(ID_EmissiveI, intensity);
            m.SetFloat(ID_Rough, rough); m.SetFloat(ID_Metal, metal);
            return m;
        }
        /** A lamp-kind material of one colour (sRGB), emissive off: bombs pulse it with setLamp / setLampIntensity. */
        public static Material lampMaterial(Color srgb, float rough)
        {
            var m = make(K_LAMP, "lamp");
            m.SetColor(ID_Base, srgb);
            m.SetFloat(ID_EmissiveI, 0);
            m.SetFloat(ID_Rough, rough);
            return m;
        }
        /** Canopy Brolly fabric (kits/brolly-model.js makeFabric), one per instance: vertex colour × colour (sRGB: the team
         *  ink, or white for the cream gores); emissive = hit flash + low-hp flicker (setLamp); see-through via setFade. */
        public static Material fabric(Color srgb, bool team)
        {
            var m = make(13, team ? "brolly-ink" : "brolly-cream");
            m.SetColor(ID_Base, srgb);
            m.SetFloat(ID_Rough, team ? 0.32f : 0.5f);
            m.SetFloat(ID_Metal, team ? 0.7f : 0.25f);   // kind 13: the clearcoat amount
            m.SetVector(ID_Emissive, Vector4.zero); m.SetFloat(ID_EmissiveI, 1);
            return m;
        }
        /** Cutlass ink edge (kits/blade-model.js makeEdgeMaterial), one per instance: setBase (team, sRGB) + setLamp (team,
         *  linear) + setCoil (charge, full, flash, time) */
        public static Material bladeEdge() => make(14, "blade-edge");
        /** Sponge Mitts pad (kits/mitts-model.js makeSpongeMaterial), one per glove: setBase (team, sRGB) + setCoil(glow, soak, 0, 0) */
        public static Material sponge() => make(15, "sponge");
        static readonly Dictionary<Color, Material> _fist = new Dictionary<Color, Material>();
        /** the Mitts' flying ink fist: team gloss with a rim glow, shared per colour */
        public static Material fist(Color c)
        {
            if (_fist.TryGetValue(c, out var m)) return m;
            m = make(16, "fist"); m.SetColor(ID_Base, c);
            return _fist[c] = m;
        }
        public static void setBase(Material m, Color srgb) => m.SetColor(ID_Base, srgb);
        /** dithered see-through: share f (0..1) of the pixels dropped (the LOD cross-fade's outgoing side) */
        public static void setFade(Material m, float f) => m.SetVector(ID_LodFade, new Vector4(f, 1, 0, 0));
        /** emissive colour (linear) + intensity of a lamp part */
        public static void setLamp(Material m, Color linear, float intensity) { m.SetVector(ID_Emissive, linear); m.SetFloat(ID_EmissiveI, intensity); }
        public static void setLampIntensity(Material m, float intensity) => m.SetFloat(ID_EmissiveI, intensity);
        /** Charger coil / splatling meter: emissive = team colour, lit by (charge, full, flash, time). */
        public static Material coilMaterial() => make(K_COIL, "coil");
        public static void setCoil(Material m, float charge, float full, float flash, float t) => m.SetVector(ID_Coil, new Vector4(charge, full, flash, t));

        // ---- per character ------------------------------------------------------------------------------------------
        public readonly Material skin, cloth, hair, eye, fill, squid, squidGhost, glow;
        readonly Material[] body4;                 // skin, cloth, hair, eye (the LOD tier parts)
        readonly List<Material> own = new List<Material>();
        readonly Dictionary<string, Material>[] dith = { new Dictionary<string, Material>(), new Dictionary<string, Material>() };
        Color team = new Color(1, 0.54f, 0.08f);

        public CharacterMats(CharacterStyle.Style st, float hurtSeed)
        {
            skin = make(K_SKIN, "skin"); cloth = make(K_CLOTH, "cloth"); hair = make(K_HAIR, "hair"); eye = make(K_EYE, "eye");
            squid = make(K_SQUID, "squid"); fill = make(K_FILL, "fill"); glow = make(K_GLOW, "glow");
            squidGhost = new Material(glassShader) { name = "iw-squidGhost", renderQueue = 3003 }; squidGhost.SetFloat(ID_Ghost, 1);
            own.AddRange(new[] { skin, cloth, hair, eye, squid, fill, glow, squidGhost });
            body4 = new[] { skin, cloth, hair, eye };
            // rim strength per material (hair > skin > cloth)
            skin.SetFloat(ID_RimK, 1.0f); cloth.SetFloat(ID_RimK, 0.7f); hair.SetFloat(ID_RimK, 1.2f); squid.SetFloat(ID_RimK, 1.0f);
            // style: skin tone, outfit colourway + pattern, iris gradient (character-style.js applyStyleUniforms)
            skin.SetColor(ID_Skin, hex(CharacterStyle.SKIN_TONES[st.skin]));
            eye.SetColor(ID_Skin, hex(CharacterStyle.SKIN_TONES[st.skin]));
            var o = CharacterStyle.OUTFITS[st.outfit];
            foreach (var m in new[] { cloth, hair })
            {
                m.SetColor(ID_Shirt, hex(o.shirt)); m.SetColor(ID_Shorts, hex(o.shorts)); m.SetColor(ID_Shoe, hex(o.shoe));
                m.SetColor(ID_Sole, hex(o.sole)); m.SetColor(ID_Sock, hex(o.sock)); m.SetColor(ID_Strap, hex(o.strap));
                m.SetFloat(ID_Pattern, o.pattern);
            }
            var ir = CharacterStyle.IRIS[st.eyes];
            eye.SetColor(ID_Iris, hex(ir[0])); eye.SetColor(ID_Iris2, hex(ir[1]));
            squid.SetColor(ID_Iris, hex(ir[0])); squid.SetColor(ID_Iris2, hex(ir[1]));
            foreach (var m in body4) m.SetFloat(ID_HurtSeed, hurtSeed);
            setFace(new Vector4(0.75f, 1, 0, 0), Vector2.zero, Vector4.zero, Vector4.zero, 0.5f, Vector4.zero);
        }

        public Material get(string key) => key switch
        {
            "skin" => skin, "cloth" => cloth, "hair" => hair, "eye" => eye, "squid" => squid, "fill" => fill, "glow" => glow, "dark" => dark,
            _ => skin,
        };

        /** A clone of a body material whose fragments dither against the LOD fade (side 0 = outgoing, 1 = incoming). */
        public Material dither(string key, int side)
        {
            var D = dith[side];
            if (D.TryGetValue(key, out var m)) return m;
            m = new Material(get(key)) { name = get(key).name + (side == 0 ? ":out" : ":in") };
            D[key] = m; own.Add(m);
            return m;
        }

        /** f ∈ (0,1] cross-fade progress (0 = off) */
        public void setLodFade(float f, int to)
        {
            for (int side = 0; side < 2; side++)
                foreach (var kv in dith[side])
                {
                    // the clones follow their source's live uniforms (cheap: a fade lasts 0.3 s)
                    kv.Value.CopyPropertiesFromMaterial(get(kv.Key));
                    kv.Value.SetVector(ID_LodFade, new Vector4(f, side == 0 ? 1 : -1, 0, 0));
                }
        }

        public void setTeam(Color c)
        {
            team = c;
            foreach (var m in new[] { cloth, hair, squid, fill, squidGhost }) m.SetColor(ID_Team, c);
            glow.SetColor(ID_Base, c * 0.3f);
        }

        public void setHurt(Color enemy, float amount)
        {
            var l = enemy.linear; var v = new Vector4(l.r, l.g, l.b, amount);
            foreach (var m in body4) m.SetVector(ID_Hurt, v);
        }

        public void setFace(Vector4 mouth, Vector2 look, Vector4 gaze, Vector4 lid, float pupil, Vector4 mouth2)
        {
            skin.SetVector(ID_Mouth, mouth); skin.SetVector(ID_Mouth2, mouth2);
            eye.SetVector(ID_Look, look); eye.SetVector(ID_Gaze, gaze); eye.SetVector(ID_Lid, lid); eye.SetFloat(ID_Pupil, pupil);
            squid.SetVector(ID_Look, look);
        }

        public void setTime(float t) => squid.SetVector(ID_Clock, new Vector4(t, 0, 0, 0));
        public void setWig(Vector3 w) => squid.SetVector(ID_Wig, w);

        /** per frame: invuln flash, special-ready glow, tank fill emissive, charger glow intensity */
        public void setFrame(float flash, float glowK, float fillE, float glowI)
        {
            var fl = new Vector4(flash, flash, flash, 0);
            var tl = team.linear;
            var gl = new Vector4(tl.r * glowK, tl.g * glowK, tl.b * glowK, 0);
            foreach (var m in body4) { m.SetVector(ID_Flash, fl); m.SetVector(ID_Glow, gl); }
            squid.SetVector(ID_Flash, fl);
            fill.SetVector(ID_Emissive, new Vector4(tl.r, tl.g, tl.b, 0)); fill.SetFloat(ID_EmissiveI, fillE);
            glow.SetVector(ID_Emissive, new Vector4(tl.r, tl.g, tl.b, 0)); glow.SetFloat(ID_EmissiveI, glowI);
        }

        // ---- character rim / fill light (character.js _updateRim): sky horizon + sun tint, sun direction ------------
        static int _rimFrame = -1;
        static Vector4 _rimC, _rimL, _fillC;
        public static Vector2 CHAR_RIM = new Vector2(1.25f, 1.4f);   // (k, fill); 0 = off
        public void updateRim(bool inWorld)
        {
            if (_rimFrame != Time.frameCount) { _rimFrame = Time.frameCount; computeRim(); }
            foreach (var m in new[] { skin, cloth, hair, squid }) { m.SetVector(ID_Rim, _rimC); m.SetVector(ID_RimL, _rimL); m.SetVector(ID_Fill, _fillC); }
        }
        static void computeRim()
        {
            float k = CHAR_RIM.x, kf = CHAR_RIM.y;
            var sun = RenderSettings.sun;
            if (sun == null) { _rimC = new Vector4(0.06f * k, 0.065f * k, 0.075f * k, 3.4f); _rimL = new Vector4(0.3f, 0.6f, 0.75f, 0).normalized; _fillC = Vector4.zero; return; }
            Color h = (RenderSettings.ambientMode == UnityEngine.Rendering.AmbientMode.Trilight ? RenderSettings.ambientEquatorColor : RenderSettings.ambientLight).linear;
            Color sc = sun.color.linear;
            float si = Mathf.Min(1.5f, sun.intensity / 2.5f);
            _rimC = new Vector4((h.r * 0.55f + sc.r * 0.45f * si) * 0.32f * k, (h.g * 0.55f + sc.g * 0.45f * si) * 0.32f * k, (h.b * 0.55f + sc.b * 0.45f * si) * 0.32f * k, 3.2f);
            Vector3 sd = -sun.transform.forward;
            _rimL = new Vector4(sd.x, sd.y, sd.z, 0);
            // fill: a low sun (golden hour / sunset) leaves faces in shade → more fill, tinted by the sky
            float low = 1 - Mathf.Clamp01((sd.y - 0.25f) / (0.68f - 0.25f)); low = low * low * (3 - 2 * low);
            float lum = Mathf.Max(0.05f, h.r * 0.3f + h.g * 0.59f + h.b * 0.11f);
            float f = Mathf.Lerp(0.06f, 0.25f, low) * kf / lum;
            _fillC = new Vector4(Mathf.Lerp(h.r, lum, 0.5f) * f, Mathf.Lerp(h.g, lum, 0.5f) * f, Mathf.Lerp(h.b, lum, 0.5f) * f, 0);
        }

        public void dispose() { foreach (var m in own) Ctx.destroy(m); }
    }
}
