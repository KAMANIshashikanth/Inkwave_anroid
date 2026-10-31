// Port of src/core/ctx.js — global game context + tiny event bus + shared math helpers.
//
// Port conventions (apply to every file under Assets/_Game/Scripts):
//  * Member names follow the JS source verbatim (camelCase fields and methods) so any C# line can be traced back to
//    the module it was translated from; each file's header names that module.
//  * Gameplay runs in THREE SPACE: the JS numbers unchanged (metres, Y up, kid faces +Z at yaw 0, right-handed). The
//    whole world is parented under one root mirrored on Z (see Core/Three/ThreeSpace.cs), which makes Unity draw the
//    right-handed scene exactly as three.js does — so positions, quaternions and yaws are copied as-is.
//  * Modules read shared systems off G (G.paint, G.physics, ...). G is partial: each system declares its own slot
//    next to its class, so porting a module never edits a central list.
//  * `using static Inkwave.Ctx;` brings clamp/lerp/damp/... into scope exactly as `import { clamp } from ctx.js` does.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Inkwave
{
    public static partial class G
    {
        public static Settings settings;
        public static QualityDef quality;
        public static Color[] teamColors = new Color[2];          // sRGB (materials convert to linear themselves)
        public static string[] teamHex = { "#ff8a14", "#2f5bff" };
        public static float time;
        public static string mode = "boot";                        // 'boot' | 'menu' | 'match'
    }

    // One payload type for every bus event (JS passes object literals). Fields are the union of the payloads listed in
    // docs/EVENTS.md; each emitter fills the ones its event documents. Rare extras go in `data`.
    public class Ev
    {
        public object actor, attacker, victim, target, match, weapon;
        public float damage, amount, radius, speed, area, need, charge, time, share, len;
        public bool killed, on, swim, final, moved, helped, launched;
        public string weaponId, cause, source, id, phase, kind, state, form, reason, why;
        public int team = -1, n, surface, owner = -1, prev = -1, level, max, hand;
        public Vector3 pos, normal, dir, to, muzzle;
        public Dictionary<string, object> data;
    }

    public static class Ctx
    {
        static readonly Dictionary<string, List<Action<Ev>>> listeners = new Dictionary<string, List<Action<Ev>>>();

        // on(name, fn) → unsubscribe function
        public static Action on(string name, Action<Ev> fn)
        {
            if (!listeners.TryGetValue(name, out var set)) listeners[name] = set = new List<Action<Ev>>();
            set.Add(fn);
            return () => set.Remove(fn);
        }

        public static void emit(string name, Ev payload = null)
        {
            if (!listeners.TryGetValue(name, out var set) || set.Count == 0) return;
            payload ??= new Ev();
            // copy: a listener may unsubscribe itself (or others) while the event is being delivered
            var snapshot = set.ToArray();
            for (int i = 0; i < snapshot.Length; i++) snapshot[i](payload);
        }

        public static void clearListeners() => listeners.Clear();

        // ---- small math helpers shared by core modules ----
        public static float clamp(float v, float a, float b) => v < a ? a : v > b ? b : v;
        public static float lerp(float a, float b, float t) => a + (b - a) * t;
        public static float damp(float a, float b, float lambda, float dt) => lerp(a, b, 1 - Mathf.Exp(-lambda * dt));
        public static float smoothstep(float a, float b, float x) { float t = clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }
        public static float angleDiff(float a, float b)
        {
            float d = b - a;
            while (d > Mathf.PI) d -= Mathf.PI * 2;
            while (d < -Mathf.PI) d += Mathf.PI * 2;
            return d;
        }
        public static float dampAngle(float a, float b, float lambda, float dt) => a + angleDiff(a, b) * (1 - Mathf.Exp(-lambda * dt));

        // Deterministic hash → [0,1) (double precision, like the JS Number maths it mirrors)
        public static float hash1(double n)
        {
            double s = Math.Sin(n * 127.1 + 311.7) * 43758.5453123;
            return (float)(s - Math.Floor(s));
        }

        // Seeded RNG (mulberry32). uint arithmetic wraps mod 2^32 exactly like Math.imul / `| 0`.
        public static Func<float> rng(double seed)
        {
            uint a = unchecked((uint)(long)seed);
            return () =>
            {
                unchecked
                {
                    a += 0x6d2b79f5;
                    uint t = (a ^ (a >> 15)) * (1 | a);
                    t = (t + ((t ^ (t >> 7)) * (61 | t))) ^ t;
                    return (float)((t ^ (t >> 14)) / 4294967296.0);
                }
            };
        }

        // Math.random()
        public static float random() => UnityEngine.Random.value * 0.99999994f;

        // dispose a Unity object from play mode or from an editor tool
        public static void destroy(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o); else UnityEngine.Object.DestroyImmediate(o);
        }

        // '#rrggbb' → sRGB Color
        public static Color hex(string h)
        {
            ColorUtility.TryParseHtmlString(h, out var c);
            return c;
        }
    }
}
