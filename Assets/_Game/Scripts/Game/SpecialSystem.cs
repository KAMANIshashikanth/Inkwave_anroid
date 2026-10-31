using System;
using UnityEngine;
namespace Inkwave
{
    // Minimal placeholder so that G.specials can be referenced without CS0246.
    // The full implementation (barrage, bubbler, sonar, etc.) will be added in Task 3‑4.
    public class SpecialSystem
    {
        // Stub methods used by the current Task 3 edits.
        public void tick(Actor a, SpecialState s, float dt) { }
        public float rayHit(Vector3 from, Vector3 dir, float len, int team, float dmg, string owner) { return len; }
        public bool shotHit(Vector3 prev, Vector3 pos, int team, float dmg, string owner) { return false; }

        // Additional stubs that may be needed later (currently commented out).
        // public void body(Actor a, SpecialState s, float dt) { }
        // public bool weapon(Actor a, SpecialState s, float dt, WeaponInput inp) { return false; }
        // public void end(Actor a, string reason = "time") { }
        // public void onSplat(Actor a) { }
        // public float remaining(Actor a) { return 0; }
        // public string prompt(Actor a) { return null; }
        // public void filterDamage(Actor a, float amount, Actor attacker, string source) { }
        // void _knock(Actor a, Actor attacker, float k) { }
        // void _hitFlash(Actor a) { a.hurtFlash = Math.Min(1, a.hurtFlash + 0.25f); }
    }
}