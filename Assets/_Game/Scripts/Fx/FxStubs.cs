// Call surfaces of src/fx/fx.js (G.fx) and src/audio/audio.js (G.audio). Gameplay modules call these exactly where the
// JS does; M1 ships them as no-ops (the slots stay null-safe) and M5 ports the particle and procedural-audio systems
// behind the same methods.
using UnityEngine;

namespace Inkwave
{
    public static partial class G
    {
        public static Fx fx;
        public static AudioSys audio;
    }

    public class FxOpts { public int count = 8; public float speed = 3, size = 0.08f, radius = 1, life = 0.5f, spread = 1; public float gravity = -1; public bool paint = true, sheet = true, snap = true, mist, quiet, ring; }

    public class Fx
    {
        public virtual void burst(Vector3 pos, Vector3 normal, Color color, FxOpts o = null) { }
        public virtual void ring(Vector3 pos, Vector3 normal, Color color, FxOpts o = null) { }
        public virtual void spawnFlash(Vector3 pos, Color color) { }
        public virtual void splatted(Vector3 pos, Color color) { }
        public virtual void explosion(Vector3 pos, Color color, float radius) { }
        public virtual void wake(Vector3 pos, Vector3 dir, Color color, float speed) { }
        public virtual void muzzle(Vector3 pos, Vector3 dir, Color color, string kind) { }
        public virtual void rain(Vector3 pos, float radius, Color color, float dt, bool cloud = true) { }
        // a lodged / armed device's warning: a pulse on each beep, and the danger ring filling toward the blast (k 0..1)
        public virtual void beepPulse(Vector3 pos, Color color, float radius, float k) { }
        public virtual void dangerRing(Vector3 pos, Vector3 normal, Color color, float radius, float k) { }
        public virtual void waterPlop(Vector3 pos, float size) { }
        // a loose FX droplet (spray off a blade, a wave slumping): o.size, o.life, o.paint
        public virtual void drop(Vector3 pos, Vector3 vel, Color color, FxOpts o = null) { }
        // a ground / wall marking (the Mitts' landing telegraph): radius, dashes, opacity, phase, width, fill
        public virtual void mark(Vector3 pos, Vector3 normal, Color color, float radius, int dashes, float opacity, float phase, float width, float fill) { }
        public virtual void chargeGlow(Vector3 pos, Color color, float k) { }
        // a charge that just topped out (the Shaker's third clink): a bright pop of light and sparkle
        public virtual void chargeFull(Vector3 pos, Color color) { }
        // a small bright sparkle (a tracer bolt's head, its hit)
        public virtual void glint(Vector3 pos, Color color, float size) { }
        // a thrown device's light mist + drips in flight (the boomerang)
        public virtual void bombTrail(Vector3 pos, Vector3 vel, Color color) { }
        // a ricochet's splash off a surface (tracer bounces)
        public virtual void bounceSplash(Vector3 pos, Vector3 normal, Color color) { }
        // a soft puff of fine spray drifting off at vel (size m, life s)
        public virtual void mist(Vector3 pos, Vector3 vel, Color color, float size, float life) { }
        public virtual void superJumpLand(Vector3 pos, Color color) { }
        public virtual void superJumpTrail(Vector3 pos, Vector3 vel, Color color) { }
        public virtual void climbDrip(Vector3 pos, Vector3 normal, Color color) { }
        public virtual void update(float dt) { }
    }

    public class AudioOpts { public Vector3? pos; public float volume = 1, pitch = 1, delay; }

    // a looping voice (charger hum, roller drum, storm rain …): retuned every frame, faded out on stop
    public class AudioLoop
    {
        public virtual void set(float? volume = null, float? pitch = null, Vector3? pos = null) { }
        public virtual void stop(float fade = 0.1f) { }
    }

    public class AudioSys
    {
        public virtual void play(string name, AudioOpts o = null) { }
        public virtual AudioLoop loop(string name, AudioOpts o = null) => null;
        public virtual void setListener(Vector3 pos, Vector3 forward, Vector3 up) { }
    }
}
