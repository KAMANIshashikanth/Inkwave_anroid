// The character contract actors drive (docs/CONTRACTS.md §1 — src/game/character.js), implemented by the procedural
// squidkid (Character.cs; the M1 stand-in CapsuleCharacter honours it too).
using System;
using UnityEngine;

namespace Inkwave
{
    // AnimState (docs/CONTRACTS.md §1): written by Actor._finishFrame every frame
    public class AnimState
    {
        public float time, speed, vy, aimPitch, charge, ink = 1, special, turnRate, hp = 1;
        public Vector2 localMove;            // move direction relative to facing, |v|<=1, +y (z) forward, +x right
        public bool grounded = true, firing, rolling, lowInk, invuln, inEnemyInk, subAim;
        public string form = "kid";          // 'kid' | 'squid' | 'swim' | 'climb'
        public Vector3 wallNormal;
        public int surface;                  // 0 dry · 1 own ink · 2 enemy ink
        public string specialId;
    }

    public interface ICharacter
    {
        Transform root { get; }                       // localPosition = feet (three space), localRotation = facing yaw
        Action<string, Ev> onEvent { get; set; }      // character → actor events ('footstep' …)
        void setColor(Color c);
        void setWeapon(string id);
        bool hideWeapon { get; set; }
        void setSub(string kind);
        void update(float dt, AnimState s);
        void trigger(string name, float arg = 1, Vector2? dir = null);
        void setHurt(float amount, Color enemyColor);
        void setVisible(bool on);
        Vector3 getMuzzle();                          // world (three-space) muzzle position of the held weapon
        Vector3 getMuzzleHand(int hand = 0);          // hand 1 = the left pistol when dual wielding (else the main muzzle)
        float aimReady();                             // 0..1: how far the held weapon is into its aim pose
        bool getAimMuzzle(float pitch, out Vector3 o);   // the muzzle of the full aim pose at `pitch` (false: none)
        bool dual { get; }                            // a weapon in each fist (twin pistols)
        void dispose();
    }
}
