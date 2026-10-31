// Port of src/game/character-style.js — the squidkid style catalog: skin tones, hair styles, outfits, eye colours,
// headgear, brows and named preset looks. APPEND-ONLY (saved profiles and bots store indices).
// A style is { hair, skin, outfit, eyes, hat, brows }; missing fields (-1) derive from the character's name seed.
using System;

namespace Inkwave
{
    public static class CharacterStyle
    {
        public class Style
        {
            public int hair = -1, skin = -1, outfit = -1, eyes = -1, hat = -1, brows = -1;
            public Style Clone() => (Style)MemberwiseClone();
        }

        public class Outfit { public string shirt, shorts, shoe, sole, sock, strap; public int pattern; }

        // ---- skin: natural tones with deliberate undertones (the skin shader adds warmth/sheen from the base colour)
        public static readonly string[] SKIN_TONES =
        {
            "#ffd9c2", // 0 fair, rosy
            "#eab48e", // 1 light warm (freckled)
            "#b37a52", // 2 tan
            "#6e4429", // 3 deep brown
            "#f9e1d3", // 4 porcelain, cool pink undertone
            "#d9a577", // 5 honey, golden undertone
            "#c29a72", // 6 olive, green-gold undertone
            "#8f5a3a", // 7 chestnut, warm red undertone
            "#4a2c20", // 8 ebony, deep cool undertone
        };
        public static readonly string[] SKIN_NAMES = { "Rosy", "Peach", "Tan", "Cocoa", "Porcelain", "Honey", "Olive", "Chestnut", "Ebony" };

        // ---- outfits: tee cut, graphic language (pattern → cloth shader), coordinated colourway
        public static readonly Outfit[] OUTFITS =
        {
            new Outfit { shirt = "#f4f2ec", shorts = "#27304a", shoe = "#272b34", sole = "#f4f2ec", sock = "#f7f7f4", strap = "#30343d", pattern = 0 },
            new Outfit { shirt = "#2b2e36", shorts = "#cfbb92", shoe = "#f3f2ee", sole = "#c98b4e", sock = "#f7f7f4", strap = "#24262c", pattern = 1 },
            new Outfit { shirt = "#bfc5cf", shorts = "#1f2127", shoe = "#f3f2ee", sole = "#2a2c33", sock = "#2a2c33", strap = "#2a2c33", pattern = 2 },
            new Outfit { shirt = "#f2e6c9", shorts = "#3a5683", shoe = "#3a3f4b", sole = "#f4f2ec", sock = "#f7f7f4", strap = "#3a3f4b", pattern = 3 },
            new Outfit { shirt = "#f3efe4", shorts = "#2c3038", shoe = "#ece9e0", sole = "#c69256", sock = "#f5f4ef", strap = "#2a3550", pattern = 4 },
            new Outfit { shirt = "#26282e", shorts = "#4a5160", shoe = "#1e2026", sole = "#f2f1ec", sock = "#1e2026", strap = "#1b1d22", pattern = 5 },
            new Outfit { shirt = "#5f6446", shorts = "#b5a37f", shoe = "#6a5238", sole = "#2b2622", sock = "#e6e0d0", strap = "#3b3a2c", pattern = 6 },
            new Outfit { shirt = "#ebe4d6", shorts = "#41608f", shoe = "#f2f1ec", sole = "#d9cbb0", sock = "#f7f7f4", strap = "#39404f", pattern = 7 },
            new Outfit { shirt = "#f2f2ef", shorts = "#23262d", shoe = "#23262d", sole = "#f2f1ec", sock = "#f7f7f4", strap = "#23262d", pattern = 8 },
            new Outfit { shirt = "#1f2a44", shorts = "#1f2a44", shoe = "#f2f1ec", sole = "#1f2a44", sock = "#f7f7f4", strap = "#161b29", pattern = 9 },
        };
        public static readonly string[] OUTFIT_NAMES = { "Basic Ringer", "Night Pinstripe", "Raglan Runner", "Chevron Tee", "Breton Stripe", "Splatter Tee", "Splat Camo", "Dip-Dye Tee", "Pro Jersey", "Track Top" };

        // ---- eyes: iris gradient [top, bottom]
        public static readonly string[][] IRIS =
        {
            new[] { "#ffcf3a", "#ff7a00" }, // 0 amber
            new[] { "#4ff0dc", "#0b7fb0" }, // 1 lagoon
            new[] { "#c9a2ff", "#5b2ad6" }, // 2 violet
            new[] { "#a8f56a", "#1d9a4a" }, // 3 lime
            new[] { "#ffa3cf", "#d0246e" }, // 4 rose
            new[] { "#e6b36a", "#6b3a14" }, // 5 hazel
            new[] { "#e4f4ff", "#4f7fc4" }, // 6 frost
            new[] { "#ff8f6b", "#b3121c" }, // 7 ember
        };
        public static readonly string[] IRIS_NAMES = { "Amber", "Lagoon", "Violet", "Lime", "Rose", "Hazel", "Frost", "Ember" };

        // ---- hair: built by character-geo.js (STYLES, same order). Tentacle hair always takes the team ink colour.
        public static readonly string[] HAIR_STYLE_NAMES = { "Tide", "Spike", "Twin", "Bob", "Pony", "Crest", "Knot", "Swoop" };
        public static readonly int HAIR_STYLES = HAIR_STYLE_NAMES.Length;

        // ---- headgear (optional, `hat`): built into the hair mesh. 0 = none.
        public static readonly string[] HATS = { "none", "cap", "beanie", "bucket" };
        public static readonly string[] HAT_NAMES = { "None", "Snapback", "Beanie", "Bucket Hat" };

        // ---- brows (optional, `brows`): shape of the painted-ink brow strokes. 0 = classic.
        public static readonly string[] BROWS = { "classic", "bold", "arched", "straight" };
        public static readonly string[] BROW_NAMES = { "Classic", "Bold", "Arched", "Straight" };

        static int wrap(int v, int n) => ((v % n) + n) % n;

        /** Normalise a (possibly partial / out-of-range) style to valid indices; unspecified fields derive from the seed. */
        public static Style resolveStyle(Style st, uint seed)
        {
            st ??= new Style();
            int s = unchecked((int)seed);   // JS `>>` works on the signed 32-bit value
            int hair = wrap(st.hair >= 0 ? st.hair : (int)(seed % (uint)HAIR_STYLES), HAIR_STYLES);
            int skin = wrap(st.skin >= 0 ? st.skin : (s >> 3) % SKIN_TONES.Length, SKIN_TONES.Length);
            int outfit = wrap(st.outfit >= 0 ? st.outfit : (int)(((long)hair + skin + (s >> 6)) % OUTFITS.Length), OUTFITS.Length);
            int eyes = wrap(st.eyes >= 0 ? st.eyes : (s >> 9) % IRIS.Length, IRIS.Length);
            int hat = wrap(st.hat >= 0 ? st.hat : 0, HATS.Length);
            int brows = wrap(st.brows >= 0 ? st.brows : 0, BROWS.Length);
            return new Style { hair = hair, skin = skin, outfit = outfit, eyes = eyes, hat = hat, brows = brows };
        }

        /** A complete random look (bots, "shuffle" in the locker). About a third of rolls wear headgear. */
        public static Style randomStyle(Func<float> rng)
        {
            int pick(int n) => Math.Min(n - 1, (int)(rng() * n));
            return new Style
            {
                hair = pick(HAIR_STYLES), skin = pick(SKIN_TONES.Length), outfit = pick(OUTFITS.Length), eyes = pick(IRIS.Length),
                hat = rng() < 0.34f ? 1 + pick(HATS.Length - 1) : 0, brows = pick(BROWS.Length),
            };
        }
    }
}
