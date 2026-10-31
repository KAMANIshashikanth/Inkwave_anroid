// Port of src/core/touch.js — on-screen controls for touch devices (phones / tablets).
//
// How it talks to the rest of the game: it writes into the existing Input snapshot rather than being a parallel
// device. Look deltas land in `mouse.dx/dy` — the same field the map cursors read — so those UIs work on a phone
// without knowing touch exists, and the camera reads the same field with its own gain. Buttons land in `input.touch.*`
// and are folded into the mouse fields by InputState.pollTouch().
//
// Two rules this file exists to respect:
//   * ONE input layer, with geometric hit-testing (every touch is classified by where it lands, not by which UI element
//     received it), so a thumb sliding off a button keeps its role and zones are trivially recomputed on resize.
//   * Every control stays clear of the screen edges. Android 10+ gesture navigation owns edge swipes, and it wins.
//
// Units: like the CSS original, all geometry is in density-independent points (1 pt = 1 CSS px = 1 dp); `dpScale`
// converts Unity's physical pixels. The visuals are a uGUI canvas whose scale factor is that same dpScale.
// The orientation is locked to landscape by the player settings, so the JS portrait gate is not needed.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem.EnhancedTouch;
using UnityEngine.UI;
using ETouch = UnityEngine.InputSystem.EnhancedTouch.Touch;
using TouchPhase = UnityEngine.InputSystem.TouchPhase;

namespace Inkwave
{
    public class TouchControls
    {
        // Left share of the screen that owns the movement stick. The rest is look / fire / buttons.
        const float MOVE_ZONE = 0.44f;

        // Button layout, anchored to the bottom-right corner (the right thumb's natural arc). x/y are distances from the
        // right / bottom edge in units of the step `u`, r is the radius in the same units, so the whole cluster scales
        // together. +x = left, +y = up. Tuned on device upstream — these are the numbers to move.
        struct BtnDef { public string name, label, cls; public float x, y, r; public bool top; }
        static readonly BtnDef[] LAYOUT =
        {
            new BtnDef { name = "fire", label = "", x = 1.50f, y = 1.50f, r = 1.50f, cls = "is-fire" },
            new BtnDef { name = "jump", label = "JUMP", x = 4.55f, y = 1.20f, r = 1.05f },
            new BtnDef { name = "squid", label = "SQUID", x = 4.55f, y = 3.40f, r = 1.05f },
            new BtnDef { name = "sub", label = "SUB", x = 1.50f, y = 4.35f, r = 1.05f },
            new BtnDef { name = "special", label = "SP", x = 4.20f, y = 5.30f, r = 1.05f },
            // Top-right row: not thumb-arc controls, so they sit on their own line. `top` anchors them to the top edge.
            new BtnDef { name = "map", label = "MAP", x = 1.10f, y = 1.10f, r = 0.90f, cls = "is-small", top = true },
            new BtnDef { name = "cheer", label = "YEAH", x = 2.95f, y = 1.10f, r = 0.90f, cls = "is-small", top = true },
            new BtnDef { name = "pause", label = "II", x = 4.80f, y = 1.10f, r = 0.90f, cls = "is-small", top = true },
        };

        // Buttons whose press must also reach the game as a one-frame key edge (the controller reads those via wasPressed).
        // Levels must NOT go through that path: a synthesized `KeyM` would open and close the map inside a single frame.
        static readonly Dictionary<string, string> EDGE_CODES = new Dictionary<string, string> { ["cheer"] = "KeyC" };

        // Controls that are NOT levels, so lifting the finger must not clear them: `map` is a latch and `pause` a one-shot.
        static readonly HashSet<string> NOT_LEVEL = new HashSet<string> { "map", "pause" };

        // Is the PRIMARY pointer a finger?
        public static bool isTouchDevice() => Application.isMobilePlatform && UnityEngine.InputSystem.Touchscreen.current != null;

        // settings.touchControls: 'on' | 'off' force it, 'auto' decides by device.
        public static bool shouldUseTouch(Settings settings)
        {
            string mode = settings?.touchControls ?? "auto";
            if (mode == "on") return true;
            if (mode == "off") return false;
            return isTouchDevice();
        }

        class Btn
        {
            public string name; public float ux, uy, ur; public bool top; public float cx, cy, r;
            public RectTransform rt; public Image fill, ring, dot; public Text label; public bool fire, isDown, isOn;
        }

        readonly InputState input;
        readonly Dictionary<int, string> roles = new Dictionary<int, string>();   // touchId -> 'move' | 'look' | 'fire' | 'btn:<name>'
        int? lookId; float lookX, lookY;
        int? moveId; float moveOx, moveOy;
        readonly List<Btn> buttons = new List<Btn>();
        float u = TOUCH.minStep;
        float stickR = 1;
        public bool enabled;            // the game layer is live (in a match, no menu open)
        bool portrait;
        float dpScale = 1;
        Vector2Int lastScreen;

        Canvas canvas;
        RectTransform stick, knob;
        CanvasGroup layerGroup, stickGroup;

        public TouchControls(InputState input) { this.input = input; }

        // ---- lifecycle -------------------------------------------------------------------------------------------------
        public void mount()
        {
            EnhancedTouchSupport.Enable();
            var go = new GameObject("TouchControls");
            Object.DontDestroyOnLoad(go);
            canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 15;   // over the HUD (10), under the menu layer (20) and the overlays (30)
            go.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            layerGroup = go.AddComponent<CanvasGroup>();
            layerGroup.blocksRaycasts = false;
            layerGroup.interactable = false;
            layerGroup.alpha = 0;

            Sprite disc = UiSprites.disc, ring = UiSprites.ring;
            Font font = UiSprites.font;
            foreach (var b in LAYOUT)
            {
                var btn = new Btn { name = b.name, ux = b.x, uy = b.y, ur = b.r, top = b.top, fire = b.cls == "is-fire" };
                btn.rt = UiSprites.node("btn:" + b.name, go.transform);
                btn.fill = UiSprites.image(btn.rt, disc, Color.clear);
                btn.ring = UiSprites.image(btn.rt, ring, Color.clear);
                if (btn.fire)
                {
                    var dot = UiSprites.node("dot", btn.rt);
                    dot.anchorMin = new Vector2(0.29f, 0.29f); dot.anchorMax = new Vector2(0.71f, 0.71f);   // 42 %
                    btn.dot = dot.gameObject.AddComponent<Image>();
                    btn.dot.sprite = disc;
                    btn.dot.color = new Color(1, 1, 1, 0.82f);
                }
                if (!string.IsNullOrEmpty(b.label))
                {
                    var lt = UiSprites.node("label", btn.rt);
                    btn.label = lt.gameObject.AddComponent<Text>();
                    btn.label.font = font;
                    btn.label.fontStyle = FontStyle.Bold;
                    btn.label.alignment = TextAnchor.MiddleCenter;
                    btn.label.color = Color.white;
                    btn.label.text = b.label;
                    btn.label.raycastTarget = false;
                    btn.label.horizontalOverflow = HorizontalWrapMode.Overflow;
                }
                buttons.Add(btn);
                paint(btn);
            }
            stick = UiSprites.node("stick", go.transform);
            UiSprites.image(stick, disc, new Color(21 / 255f, 18 / 255f, 28 / 255f, 0.20f));
            UiSprites.image(stick, ring, new Color(1, 1, 1, 0.26f));
            stickGroup = stick.gameObject.AddComponent<CanvasGroup>();
            stickGroup.alpha = 0;
            knob = UiSprites.node("knob", stick);
            knob.anchorMin = knob.anchorMax = new Vector2(0.5f, 0.5f);
            UiSprites.image(knob, disc, new Color(1, 1, 1, 0.70f));
            layout();
        }

        public void dispose()
        {
            releaseAll();
            if (canvas != null) Object.Destroy(canvas.gameObject);
        }

        // ---- geometry --------------------------------------------------------------------------------------------------
        void layout()
        {
            lastScreen = new Vector2Int(Screen.width, Screen.height);
            dpScale = Mathf.Max(1f, Screen.dpi > 0 ? Screen.dpi / 160f : 1f);
            canvas.scaleFactor = dpScale;
            float w = Screen.width / dpScale, h = Screen.height / dpScale;
            portrait = h > w;
            // Step unit: driven by the short edge so the cluster keeps its proportions in landscape.
            u = Mathf.Max(TOUCH.minStep, Mathf.Min(TOUCH.maxStep, Mathf.Min(w, h) * TOUCH.stepFrac));
            stickR = u * TOUCH.stickR;
            float m = Mathf.Max(u * TOUCH.edgeSteps, TOUCH.edgeMin);   // absolute floor: the gesture-nav strip is fixed in dp, not in steps
            foreach (var b in buttons)
            {
                b.cx = w - m - b.ux * u;
                b.cy = b.top ? m + b.uy * u : h - m - b.uy * u;
                b.r = b.ur * u;
                place(b.rt, b.cx, b.cy, b.r);
                if (b.label != null) b.label.fontSize = Mathf.RoundToInt(Mathf.Clamp(b.top ? Mathf.Min(w, h) * 0.019f : Mathf.Min(w, h) * 0.025f, b.top ? 7 : 9, b.top ? 11 : 15));
            }
            if (moveId == null) hideStick();
        }

        // centre (cx, cy) in top-left points, radius r
        static void place(RectTransform rt, float cx, float cy, float r)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(r * 2, r * 2);
            rt.anchoredPosition = new Vector2(cx, -cy);
        }

        Btn btnAt(float x, float y)
        {
            foreach (var b in buttons) if (Mathf.Sqrt((x - b.cx) * (x - b.cx) + (y - b.cy) * (y - b.cy)) <= b.r) return b;
            return null;
        }

        bool taken(string role)
        {
            foreach (var r in roles.Values) if (r == role) return true;
            return false;
        }

        // ---- pointer handling ------------------------------------------------------------------------------------------
        void down(int id, float x, float y)
        {
            if (!input.touch.active || portrait) return;
            // The controls are invisible unless the layer is live, so they must be untouchable too.
            if (!enabled) return;
            var b = btnAt(x, y);
            if (b != null)
            {
                // The fire button is also a look origin: touch it and drag, and you fire while aiming with the same thumb.
                string role = b.name == "fire" ? "fire" : "btn:" + b.name;
                if (taken(role)) return;   // a second finger on a live control is inert
                roles[id] = role;
                b.isDown = true; paint(b);
                if (b.name == "fire") { lookId = id; lookX = x; lookY = y; }
                press(b.name);
                return;
            }
            float w = Screen.width / dpScale, h = Screen.height / dpScale;
            bool inLeft = x < w * MOVE_ZONE && y > h * TOUCH.topGuard;
            if (inLeft && moveId == null)
            {
                roles[id] = "move";
                moveId = id;
                moveOx = x; moveOy = y;
                showStick(x, y, x, y);
            }
            else if (!inLeft && lookId == null)
            {
                roles[id] = "look";
                lookId = id;
                lookX = x; lookY = y;
            }
            // else: an axis is already owned — this finger is inert
        }

        void move(int id, float x, float y)
        {
            if (!roles.TryGetValue(id, out var role)) return;
            if (role == "look" || role == "fire")
            {
                // Raw points into the field the camera and both map cursors read. The camera applies the touch gain itself.
                input.mouse.dx += x - lookX;
                input.mouse.dy += y - lookY;
                lookX = x; lookY = y;
                return;
            }
            if (role == "move")
            {
                float dx = x - moveOx, dy = y - moveOy;
                float mg = Mathf.Sqrt(dx * dx + dy * dy), r = stickR;
                float k = mg > r ? r / mg : 1;   // clamp to the rim; no runaway past the edge
                input.touch.mx = (dx / r) * k;
                input.touch.mz = -(dy / r) * k;  // screen-up is forward
                showStick(moveOx, moveOy, moveOx + dx * k, moveOy + dy * k);
            }
        }

        void up(int id)
        {
            if (!roles.TryGetValue(id, out var role)) return;
            roles.Remove(id);
            if (role == "move")
            {
                moveId = null;
                input.touch.mx = 0; input.touch.mz = 0;
                hideStick();
            }
            else if (role == "look" || role == "fire")
            {
                if (lookId == id) lookId = null;
                if (role == "fire") input.touchUp("fire");   // a cancel lands here too, and is a release
                foreach (var b in buttons) if (b.name == "fire") { b.isDown = false; paint(b); }
            }
            else if (role.StartsWith("btn:"))
            {
                string name = role.Substring(4);
                if (!NOT_LEVEL.Contains(name)) input.touchUp(name);   // a latch/one-shot is set on press and only press changes it
                foreach (var b in buttons) if (b.name == name) { b.isDown = false; paint(b); }
            }
            input.touch.fingers = roles.Count;
        }

        // Press side effects. `fire` is the only level that must survive past the event (it is polled per frame);
        // the movement values are written in move(). Everything else is a toggle or a one-shot.
        void press(string name)
        {
            input.touch.fingers = roles.Count;
            switch (name)
            {
                case "fire": input.touchDown("fire"); break;
                case "map": input.touchMapToggle(); syncMap(); break;
                case "pause": input.onPause?.Invoke(); break;
                default:
                    input.touchDown(name);
                    if (EDGE_CODES.TryGetValue(name, out var code)) input.pressCode(code);
                    break;
            }
        }

        void syncMap()
        {
            foreach (var b in buttons) if (b.name == "map") { b.isOn = input.touch.map; paint(b); }
        }

        // Drop every gesture and every held control. Called whenever the layer is invalidated: menu opened, orientation
        // changed, match ended, app backgrounded.
        public void releaseAll()
        {
            foreach (var b in buttons) { b.isDown = false; paint(b); }
            roles.Clear();
            moveId = null; lookId = null;
            input.touch.mx = 0; input.touch.mz = 0;
            input.touch.fingers = 0;
            input.touch.fire = false;
            hideStick();
        }

        // Force the map latch off — a menu opening must not leave it latched, or it springs back open when the menu closes.
        public void clearMap() { input.touch.map = false; syncMap(); }

        void showStick(float ox, float oy, float kx, float ky)
        {
            float r = stickR;
            stickGroup.alpha = 1;
            place(stick, ox, oy, r);
            knob.sizeDelta = new Vector2(r * 2 * 0.46f, r * 2 * 0.46f);
            knob.anchoredPosition = new Vector2(kx - ox, -(ky - oy));
        }
        void hideStick() { if (stickGroup != null) stickGroup.alpha = 0; }

        // the touch.css states, painted
        static void paint(Btn b)
        {
            if (b.fill == null) return;
            Color fill = new Color(21 / 255f, 18 / 255f, 28 / 255f, 0.34f), ring = new Color(1, 1, 1, 0.38f);
            if (b.fire) { fill = new Color(1, 138 / 255f, 20 / 255f, b.isDown ? 0.78f : 0.40f); ring = new Color(1, 1, 1, 0.55f); }
            else if (b.isDown) { fill = new Color(1, 1, 1, 0.30f); ring = new Color(1, 1, 1, 0.8f); }
            else if (b.isOn) { fill = new Color(1, 138 / 255f, 20 / 255f, 0.55f); ring = new Color(1, 138 / 255f, 20 / 255f, 1); }
            b.fill.color = fill;
            b.ring.color = ring;
            if (b.label != null) b.label.color = b.isOn ? new Color(21 / 255f, 18 / 255f, 28 / 255f) : Color.white;
            b.rt.localScale = Vector3.one * (b.isDown ? 0.92f : 1f);
            if (b.dot != null) b.dot.rectTransform.localScale = Vector3.one * (b.isDown ? 0.68f : 1f);
        }

        // ---- per-frame -------------------------------------------------------------------------------------------------
        // `live`: a live match with no menu open. Deliberately not "the local player can act" — that clears when the
        // player is splatted, which is exactly when the map (and super-jump planning) must still work.
        public void update(bool live)
        {
            if (canvas == null) return;
            if (Screen.width != lastScreen.x || Screen.height != lastScreen.y) { releaseAll(); layout(); }   // otherwise the zones move under the thumb
            live = live && input.touch.active;   // the layer only exists for touch devices (mouse/pad players never see it)
            if (live != enabled)
            {
                enabled = live;
                layerGroup.alpha = live ? 1 : 0;
                releaseAll();
                if (!live) clearMap();   // otherwise the map latch survives and springs open on the next match
            }
            if (!input.touch.active) return;
            foreach (var t in ETouch.activeTouches)
            {
                Vector2 p = t.screenPosition;
                float x = p.x / dpScale, y = (Screen.height - p.y) / dpScale;   // top-left points, like clientX/Y
                int id = t.touchId;
                switch (t.phase)
                {
                    case TouchPhase.Began: down(id, x, y); break;
                    case TouchPhase.Moved: case TouchPhase.Stationary: move(id, x, y); break;
                    case TouchPhase.Ended: case TouchPhase.Canceled: move(id, x, y); up(id); break;
                }
            }
        }
    }
}
