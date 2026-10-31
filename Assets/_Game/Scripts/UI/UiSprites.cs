// Runtime-generated UI primitives (anti-aliased disc / ring sprites, the built-in font, RectTransform helpers).
// The web build drew these with CSS border-radius; here they are tiny textures made once and shared.
using UnityEngine;
using UnityEngine.UI;

namespace Inkwave
{
    public static class UiSprites
    {
        static Sprite _disc, _ring;
        static Font _font;

        public static Sprite disc => _disc != null ? _disc : (_disc = make(128, (d, R) => Mathf.Clamp01(R - d + 0.5f)));
        // a 2-pt border at the button size the layer uses most (~60 pt across → ring width ≈ 1/30 of the diameter)
        public static Sprite ring => _ring != null ? _ring : (_ring = make(128, (d, R) => Mathf.Clamp01(R - d + 0.5f) * Mathf.Clamp01(d - (R - 4.3f) + 0.5f)));
        public static Font font => _font != null ? _font : (_font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));

        static Sprite make(int size, System.Func<float, float, float> alpha)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[size * size];
            float c = size * 0.5f, R = size * 0.5f - 1;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x + 0.5f - c) * (x + 0.5f - c) + (y + 0.5f - c) * (y + 0.5f - c));
                    px[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha(d, R) * 255));
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100);
        }

        public static RectTransform node(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            return rt;
        }

        // a full-size Image child
        public static Image image(RectTransform parent, Sprite sprite, Color color)
        {
            var rt = node("img", parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = sprite;
            img.color = color;
            img.raycastTarget = false;
            return img;
        }
    }
}
