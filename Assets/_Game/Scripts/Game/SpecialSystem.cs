using System;
using UnityEngine;
namespace Inkwave
{
    // Minimal placeholder so that G.specials can be referenced without CS0246.
    // The full implementation (barrage, bubbler, sonar, etc.) will be added later.
    public class SpecialSystem
    {
        // Stub methods used by the current Task 4 edits.
        public void tick(Actor a, SpecialState s, float dt) { }
        public float rayHit(Vector3 from, Vector3 dir, float len, int team, float dmg, string owner) { return len; }
        public bool shotHit(Vector3 prev, Vector3 pos, int team, float dmg, string owner) { return false; }
    }
}