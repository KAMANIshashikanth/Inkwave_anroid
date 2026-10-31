// Port of src/core/input.js — keyboard + mouse (cursor lock) + standard gamepad + on-screen touch controls
// (Core/TouchControls). Produces a unified per-frame snapshot. The touch layer writes into `mouse.*` rather than being
// a parallel device: the map UIs steer their cursors off `mouse.dx/dy` + `locked`, and routing touch through the same
// fields keeps those paths working on a phone untouched.
//
// Unity's Input System replaces the DOM listeners. Key codes keep the DOM `KeyboardEvent.code` names ('KeyW', 'Space',
// 'ShiftLeft', ...) so the gameplay ports read `inp.down("KeyW")` exactly like the JS. Gamepad buttons / axes keep the
// W3C "standard" mapping indices (0 = A/south … 16 = home; axis 1 / 3 positive = stick DOWN).
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Inkwave
{
    public static partial class G
    {
        public static InputState input;
    }

    public class InputState
    {
        public class MouseState { public float dx, dy; public bool left, right, leftPressed, rightPressed; }
        public class TouchState { public bool active; public int fingers; public float mx, mz; public bool fire, sub, squid, jump, special, cheer, map; }
        public class StickOut { public float x, y, mag; }

        public readonly MouseState mouse = new MouseState();
        public readonly TouchState touch = new TouchState();
        public bool enabled = true;
        public Gamepad pad;
        public readonly HashSet<int> padPressed = new HashSet<int>();
        public string lastDevice = "kbm";
        public Func<string, bool, bool> onKey;    // (code, repeat) => consumed  (menus)
        public Action onPause;                     // the touch pause button
        public Action onUnlock;

        readonly HashSet<string> synthDown = new HashSet<string>();     // pressCode() levels
        readonly HashSet<string> synthPressed = new HashSet<string>();  // pressCode() edges, this frame
        readonly HashSet<string> latched = new HashSet<string>();      // touch buttons asserted at any point since the last endFrame()
        bool touchPrevLeft, touchPrevRight;
        bool _plock;
        float _rumbleUntil, _rumbleMag;

        // Does the game own the cursor? True under cursor lock, and true for the whole session on a touch device.
        public bool locked => _plock || touch.active;

        // Drop every held level (focus loss, the touch layer invalidated, a menu opening).
        public void releaseAll()
        {
            synthDown.Clear();
            mouse.left = mouse.right = false;
            mouse.leftPressed = mouse.rightPressed = false;
            touch.fire = touch.sub = touch.squid = touch.jump = touch.special = touch.cheer = false;
            touch.mx = touch.mz = 0;
            latched.Clear();
        }

        // ---- touch device API (called by TouchControls) -------------------------------------------------------------
        // Levels are set/cleared here so `latched` can preserve a tap shorter than one frame.
        public void setTouchMode(bool on)
        {
            if (touch.active == on) return;
            touch.active = on;
            if (!on) releaseAll();
        }
        public void touchDown(string name) { setTouch(name, true); latched.Add(name); }
        public void touchUp(string name) { setTouch(name, false); }
        public bool touchHeld(string name) => getTouch(name) || latched.Contains(name);
        public void touchMapToggle() { touch.map = !touch.map; }
        // Synthesized one-frame codes for the actions the controller reads as key edges (cheer, map jump targets).
        public void pressCode(string code) { synthDown.Add(code); synthPressed.Add(code); }
        public void releaseCode(string code) { synthDown.Remove(code); }

        void setTouch(string name, bool v)
        {
            switch (name)
            {
                case "fire": touch.fire = v; break;
                case "sub": touch.sub = v; break;
                case "squid": touch.squid = v; break;
                case "jump": touch.jump = v; break;
                case "special": touch.special = v; break;
                case "cheer": touch.cheer = v; break;
                case "map": touch.map = v; break;
            }
        }
        bool getTouch(string name)
        {
            switch (name)
            {
                case "fire": return touch.fire;
                case "sub": return touch.sub;
                case "squid": return touch.squid;
                case "jump": return touch.jump;
                case "special": return touch.special;
                case "cheer": return touch.cheer;
                case "map": return touch.map;
            }
            return false;
        }

        public void requestLock()
        {
            if (touch.active || _plock) return;   // touch has no pointer to lock
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            _plock = true;
        }
        public void exitLock()
        {
            if (!_plock) return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            _plock = false;
            mouse.left = mouse.right = false;
            onUnlock?.Invoke();
        }

        public bool down(string code) => synthDown.Contains(code) || keyIs(code, k => k.isPressed);
        public bool wasPressed(string code) => synthPressed.Contains(code) || keyIs(code, k => k.wasPressedThisFrame);

        static bool keyIs(string code, Func<KeyControl, bool> test)
        {
            var kb = Keyboard.current;
            if (kb == null || !KEYS.TryGetValue(code, out var key)) return false;
            return test(kb[key]);
        }

        // Read the devices once per frame (the DOM version received events; Unity is polled). Call before the game reads.
        public void poll()
        {
            if (!enabled) return;
            var kb = Keyboard.current;
            if (kb != null && kb.anyKey.wasPressedThisFrame)
            {
                lastDevice = "kbm";
                if (onKey != null)
                    foreach (var kv in KEYS)
                        if (kb[kv.Value].wasPressedThisFrame) onKey(kv.Key, false);
            }

            // a lost focus must not come back with a button still held down
            if (!Application.isFocused) { releaseAll(); if (_plock) exitLock(); }
            if (_plock && Cursor.lockState != CursorLockMode.Locked) exitLock();

            var m = Mouse.current;
            if (m != null && _plock)
            {
                Vector2 d = m.delta.ReadValue();
                if (d.sqrMagnitude > 0) lastDevice = "kbm";
                mouse.dx += d.x; mouse.dy -= d.y;   // DOM movementY is positive DOWN
                if (m.leftButton.wasPressedThisFrame) { mouse.left = true; mouse.leftPressed = true; }
                if (m.rightButton.wasPressedThisFrame) { mouse.right = true; mouse.rightPressed = true; }
            }
            if (m != null)
            {
                if (m.leftButton.wasReleasedThisFrame) mouse.left = false;
                if (m.rightButton.wasReleasedThisFrame) mouse.right = false;
            }
        }

        public void pollPad()
        {
            pad = Gamepad.current;
            padPressed.Clear();
            if (pad == null) return;
            for (int i = 0; i < 17; i++)
            {
                var b = padButtonControl(i);
                if (b != null && b.wasPressedThisFrame) { padPressed.Add(i); lastDevice = "pad"; }
            }
            if (Mathf.Abs(padRaw(0)) > 0.3f || Mathf.Abs(padRaw(1)) > 0.3f || Mathf.Abs(padRaw(2)) > 0.3f || Mathf.Abs(padRaw(3)) > 0.3f) lastDevice = "pad";
        }

        // Apply the touch layer's buttons to the mouse fields the rest of the game already reads, once per frame alongside
        // pollPad(). The edges are derived here (level now vs level last frame), so a tap that both starts and ends
        // between two frames still produces exactly one `leftPressed`.
        public void pollTouch()
        {
            if (!touch.active) return;
            bool left = touchHeld("fire"), right = touchHeld("sub");
            mouse.leftPressed = mouse.leftPressed || (left && !touchPrevLeft);
            mouse.rightPressed = mouse.rightPressed || (right && !touchPrevRight);
            touchPrevLeft = left; touchPrevRight = right;
            mouse.left = left; mouse.right = right;
        }

        ButtonControl padButtonControl(int i)
        {
            if (pad == null) return null;
            switch (i)
            {
                case 0: return pad.buttonSouth;
                case 1: return pad.buttonEast;
                case 2: return pad.buttonWest;
                case 3: return pad.buttonNorth;
                case 4: return pad.leftShoulder;
                case 5: return pad.rightShoulder;
                case 6: return pad.leftTrigger;
                case 7: return pad.rightTrigger;
                case 8: return pad.selectButton;
                case 9: return pad.startButton;
                case 10: return pad.leftStickButton;
                case 11: return pad.rightStickButton;
                case 12: return pad.dpad.up;
                case 13: return pad.dpad.down;
                case 14: return pad.dpad.left;
                case 15: return pad.dpad.right;
            }
            return null;
        }

        public float padRaw(int i)
        {
            if (pad == null) return 0;
            switch (i)
            {
                case 0: return pad.leftStick.x.ReadValue();
                case 1: return -pad.leftStick.y.ReadValue();
                case 2: return pad.rightStick.x.ReadValue();
                case 3: return -pad.rightStick.y.ReadValue();
            }
            return 0;
        }

        public bool padButton(int i) { var b = padButtonControl(i); return b != null && b.isPressed; }
        public float padValue(int i) { var b = padButtonControl(i); return b != null ? b.ReadValue() : 0; }
        public float padAxis(int i)
        {
            if (pad == null) return 0;
            float v = padRaw(i);
            const float dz = 0.14f;
            return Mathf.Abs(v) < dz ? 0 : Mathf.Sign(v) * (Mathf.Abs(v) - dz) / (1 - dz);
        }

        // Stick with a RADIAL dead zone (no axis snapping on diagonals), an outer dead zone (full deflection is reachable
        // on worn sticks) and an optional response exponent applied to the magnitude only (direction is preserved).
        public StickOut padStick(int ix, int iy, StickOut o, float dz = 0.12f, float outer = 0.96f, float expo = 1)
        {
            o.x = 0; o.y = 0; o.mag = 0;
            if (pad == null) return o;
            float x = padRaw(ix), y = padRaw(iy);
            float mg = Mathf.Sqrt(x * x + y * y);
            if (mg <= dz) return o;
            float k = Mathf.Min(1, (mg - dz) / (outer - dz));
            float c = expo == 1 ? k : Mathf.Pow(k, expo);
            o.x = (x / mg) * c; o.y = (y / mg) * c; o.mag = c;
            return o;
        }

        // Dual-rumble pulse. strong = low-frequency motor, weak = high-frequency motor (0..1), ms = duration.
        // A pulse only pre-empts a running one if it is at least as strong, so rapid fire never becomes a constant buzz.
        public void rumble(float strong, float weak, float ms = 60)
        {
            if (pad == null || lastDevice != "pad") return;
            float k = G.settings?.rumble ?? 1;
            if (!(k > 0)) return;
            float now = Time.unscaledTime * 1000f;
            float mag = Mathf.Max(strong, weak) * k;
            if (now < _rumbleUntil && mag < _rumbleMag * 0.95f) return;
            _rumbleUntil = now + ms; _rumbleMag = mag;
            pad.SetMotorSpeeds(Mathf.Min(1, strong * k), Mathf.Min(1, weak * k));
        }

        // stop a rumble pulse once its time is up (WebAudio's playEffect had a duration; motors here run until told)
        public void updateRumble()
        {
            if (pad != null && _rumbleMag > 0 && Time.unscaledTime * 1000f >= _rumbleUntil) { pad.SetMotorSpeeds(0, 0); _rumbleMag = 0; }
        }

        // Call once at the very end of each frame.
        public void endFrame()
        {
            synthPressed.Clear();
            mouse.dx = 0; mouse.dy = 0;
            mouse.leftPressed = false; mouse.rightPressed = false;
            latched.Clear();   // a tap lives exactly one frame, never two
        }

        // DOM KeyboardEvent.code → Input System key
        static readonly Dictionary<string, Key> KEYS = new Dictionary<string, Key>
        {
            ["KeyA"] = Key.A, ["KeyB"] = Key.B, ["KeyC"] = Key.C, ["KeyD"] = Key.D, ["KeyE"] = Key.E, ["KeyF"] = Key.F,
            ["KeyG"] = Key.G, ["KeyH"] = Key.H, ["KeyI"] = Key.I, ["KeyJ"] = Key.J, ["KeyK"] = Key.K, ["KeyL"] = Key.L,
            ["KeyM"] = Key.M, ["KeyN"] = Key.N, ["KeyO"] = Key.O, ["KeyP"] = Key.P, ["KeyQ"] = Key.Q, ["KeyR"] = Key.R,
            ["KeyS"] = Key.S, ["KeyT"] = Key.T, ["KeyU"] = Key.U, ["KeyV"] = Key.V, ["KeyW"] = Key.W, ["KeyX"] = Key.X,
            ["KeyY"] = Key.Y, ["KeyZ"] = Key.Z,
            ["Digit0"] = Key.Digit0, ["Digit1"] = Key.Digit1, ["Digit2"] = Key.Digit2, ["Digit3"] = Key.Digit3, ["Digit4"] = Key.Digit4,
            ["Digit5"] = Key.Digit5, ["Digit6"] = Key.Digit6, ["Digit7"] = Key.Digit7, ["Digit8"] = Key.Digit8, ["Digit9"] = Key.Digit9,
            ["Space"] = Key.Space, ["Tab"] = Key.Tab, ["Escape"] = Key.Escape, ["Enter"] = Key.Enter, ["Backspace"] = Key.Backspace,
            ["ShiftLeft"] = Key.LeftShift, ["ShiftRight"] = Key.RightShift, ["ControlLeft"] = Key.LeftCtrl, ["ControlRight"] = Key.RightCtrl,
            ["AltLeft"] = Key.LeftAlt, ["AltRight"] = Key.RightAlt,
            ["ArrowUp"] = Key.UpArrow, ["ArrowDown"] = Key.DownArrow, ["ArrowLeft"] = Key.LeftArrow, ["ArrowRight"] = Key.RightArrow,
            ["Slash"] = Key.Slash, ["Quote"] = Key.Quote, ["Backquote"] = Key.Backquote, ["Comma"] = Key.Comma, ["Period"] = Key.Period,
            ["F1"] = Key.F1, ["F2"] = Key.F2, ["F3"] = Key.F3, ["F4"] = Key.F4,
        };
    }
}
