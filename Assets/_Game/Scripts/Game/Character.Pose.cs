// Port of src/game/character.js — pose construction (gameplay layers → P): weapon hold tables, idle, locomotion,
// lean springs, air, weapon holds + aim + recoil, one-shots (flick, bucket, swipe, blade, land, locker, form, slosh,
// dodge, throw, sub aim, spawn, leap, slam, mitts, fidgets).
using System.Collections.Generic;
using UnityEngine;
using static Inkwave.Ctx;

namespace Inkwave
{
    public partial class Character
    {
        // ---------------------------------------------------------------------------------------------------------
        // Weapon holds. Anchor = weapon grip frame in kid space (rest torso): p = position, r = YXZ euler [pitch(+down),
        // yaw(+left), roll]. aim.p is relative to AIM_PIVOT and rotates with aim pitch. stance = aim-stance feet
        // [lx, lz, lyaw, rx, rz, ryaw] (kid space); hip/chest = aim-stance yaw distribution.
        // ---------------------------------------------------------------------------------------------------------
        public class HP { public float[] p, r; public HP(float[] p, float[] r) { this.p = p; this.r = r; } }
        public class Rc { public float kick, back, hz, z, jit, torso, head, crouch, brace; }
        public class Hold
        {
            public HP carry, run, aim, roll, @lock, raise, lobby;
            public float twoCarry, twoAim, lobbyTwo, hip, chest, crouch;
            public float[] poleR, poleL, stance;
            public Rc rc;
            public string fire;
            public bool dual;
        }
        static HP hp(float px, float py, float pz, float rx, float ry, float rz) => new HP(new[] { px, py, pz }, new[] { rx, ry, rz });
        static float[] f3(float a, float b, float c) => new[] { a, b, c };

        static readonly Vector3 AIM_PIVOT = new Vector3(-0.03f, 0.93f, 0.05f);
        static readonly Dictionary<string, Hold> HOLD = new Dictionary<string, Hold>
        {
            ["shooter"] = new Hold
            {
                fire = "recoil",
                carry = hp(-0.19f, 0.76f, 0.14f, 0.62f, 0.12f, -0.16f), twoCarry = 0,
                run = hp(-0.175f, 0.8f, 0.2f, 0.12f, 0.06f, -0.3f),
                aim = hp(-0.04f, -0.075f, 0.27f, 0, 0.035f, 0), twoAim = 1,
                poleR = f3(-0.8f, -0.55f, -0.3f), poleL = f3(0.75f, -0.65f, -0.25f),
                rc = new Rc { kick = 0.09f, back = 0.04f, hz = 6.5f, z = 0.78f, jit = 0.028f, torso = 0.34f, head = 0.12f, crouch = 0.006f, brace = 1 },
                hip = -0.12f, chest = 0.05f, crouch = 0.012f,
                stance = new[] { 0.105f, 0.03f, 0.16f, -0.098f, -0.035f, -0.34f },
                raise = hp(-0.2f, 1.24f, 0.12f, -1.15f, 0.25f, -0.3f),
                lobby = hp(-0.17f, 0.99f, 0.19f, -1.0f, 0.55f, -0.45f), lobbyTwo = 0,
            },
            ["blaster"] = new Hold
            {
                fire = "pump",
                carry = hp(-0.13f, 0.78f, 0.17f, 0.38f, 0.34f, 0.24f), twoCarry = 1,
                aim = hp(-0.035f, -0.09f, 0.2f, 0, 0.04f, 0), twoAim = 1,
                poleR = f3(-0.85f, -0.5f, -0.25f), poleL = f3(0.8f, -0.6f, -0.2f),
                rc = new Rc { kick = 0.42f, back = 0.075f, hz = 3.4f, z = 0.7f, jit = 0.035f, torso = 0.42f, head = 0.3f, crouch = 0.03f, brace = 0.6f },
                hip = -0.16f, chest = 0.08f, crouch = 0.018f,
                stance = new[] { 0.118f, 0.045f, 0.22f, -0.112f, -0.05f, -0.38f },
                raise = hp(-0.18f, 1.22f, 0.14f, -1.1f, 0.3f, -0.3f),
                lobby = hp(-0.16f, 0.97f, 0.2f, -0.95f, 0.5f, -0.4f), lobbyTwo = 0,
            },
            ["charger"] = new Hold
            {
                fire = "charge",
                carry = hp(-0.12f, 0.8f, 0.17f, -0.25f, 0.85f, 0.6f), twoCarry = 1,
                aim = hp(-0.05f, -0.02f, 0.12f, 0, 0.08f, 0), twoAim = 1,
                poleR = f3(-0.95f, -0.35f, -0.1f), poleL = f3(0.6f, -0.75f, -0.3f),
                rc = new Rc { kick = 0.22f, back = 0.06f, hz = 4.2f, z = 0.62f, jit = 0.012f, torso = 0.36f, head = 0.28f, crouch = 0.02f, brace = 0 },
                hip = -0.42f, chest = 0.3f, crouch = 0.024f,
                stance = new[] { 0.112f, 0.085f, 0.3f, -0.098f, -0.085f, -0.78f },
                raise = hp(-0.17f, 1.22f, 0.1f, -1.05f, 0.3f, -0.3f),
                lobby = hp(-0.13f, 0.8f, 0.2f, -0.3f, 0.78f, 0.55f), lobbyTwo = 1,
            },
            ["roller"] = new Hold
            {
                fire = "flick",
                carry = hp(-0.12f, 0.83f, 0.16f, 0.8f, 0.1f, 0.0f), twoCarry = 1,
                roll = hp(-0.07f, 0.79f, 0.24f, 0.9f, 0.06f, 0),
                aim = hp(-0.07f, -0.13f, 0.2f, 0.9f, 0.08f, 0), twoAim = 1,
                poleR = f3(-0.7f, -0.4f, -0.6f), poleL = f3(0.7f, -0.4f, -0.6f),
                rc = new Rc { kick = 0.04f, back = 0.012f, hz = 9, z = 0.45f, jit = 0.01f, torso = 0.1f, head = 0.05f, crouch = 0 },
                hip = -0.1f, chest = 0.04f, crouch = 0.035f,
                stance = new[] { 0.12f, 0.06f, 0.16f, -0.11f, -0.08f, -0.32f },
                raise = hp(-0.16f, 1.25f, 0.08f, -1.25f, 0.25f, -0.2f),
                lobby = hp(-0.21f, 0.86f, 0.14f, 1.2f, 0.25f, 0), lobbyTwo = 0,
            },
            // ---- kinds added by the arsenal stream (dual wield mirrors the anchor to the left fist, `fire` names the one-shot)
            ["dualies"] = new Hold
            {
                dual = true,   // a second pistol in the LEFT fist: the left anchor is the right one mirrored across the kid's midline
                carry = hp(-0.165f, 0.745f, 0.12f, 0.95f, 0.12f, -0.18f), twoCarry = 0,
                run = hp(-0.16f, 0.79f, 0.18f, 0.3f, 0.06f, -0.22f),
                aim = hp(-0.055f, -0.07f, 0.27f, 0, 0.03f, 0), twoAim = 0,
                @lock = hp(-0.07f, -0.05f, 0.31f, 0, 0.02f, -0.06f),          // post-roll turret stance (relative to AIM_PIVOT)
                poleR = f3(-0.85f, -0.55f, -0.25f), poleL = f3(0.85f, -0.55f, -0.25f),
                rc = new Rc { kick = 0.07f, back = 0.03f, hz = 7, z = 0.8f, jit = 0.03f, torso = 0.25f, head = 0.1f, crouch = 0.004f, brace = 1 },
                hip = -0.05f, chest = 0.02f, crouch = 0.016f,
                stance = new[] { 0.12f, 0.02f, 0.2f, -0.12f, -0.02f, -0.2f },
                raise = hp(-0.2f, 1.24f, 0.12f, -1.15f, 0.25f, -0.3f),
                lobby = hp(-0.2f, 0.84f, 0.16f, 0.2f, 0.35f, -0.5f), lobbyTwo = 0,
            },
            ["slosher"] = new Hold
            {
                fire = "slosh",
                carry = hp(-0.2f, 0.62f, 0.1f, 0, 0.2f, 0), twoCarry = 0,
                run = hp(-0.215f, 0.64f, 0.08f, 0.12f, 0.28f, 0.06f),
                aim = hp(-0.04f, -0.2f, 0.26f, 0, 0.05f, 0), twoAim = 1,
                poleR = f3(-0.85f, -0.5f, -0.25f), poleL = f3(0.8f, -0.6f, -0.2f),
                rc = new Rc { kick = 0.02f, back = 0.01f, hz = 5, z = 0.7f, jit = 0.005f, torso = 0.1f, head = 0.05f, crouch = 0.004f, brace = 0 },
                hip = -0.16f, chest = 0.08f, crouch = 0.018f,
                stance = new[] { 0.118f, 0.045f, 0.22f, -0.112f, -0.05f, -0.38f },
                raise = hp(-0.2f, 1.08f, 0.16f, -0.6f, 0.3f, -0.25f),
                lobby = hp(-0.21f, 0.63f, 0.12f, 0.05f, 0.4f, -0.05f), lobbyTwo = 0,
            },
            ["splatling"] = new Hold
            {
                fire = "spin",
                carry = hp(-0.16f, 0.74f, 0.16f, 0.45f, 0.2f, -0.1f), twoCarry = 1,
                aim = hp(-0.07f, -0.16f, 0.2f, 0.05f, 0.06f, 0), twoAim = 1,
                poleR = f3(-0.85f, -0.6f, -0.2f), poleL = f3(0.75f, -0.7f, -0.2f),
                rc = new Rc { kick = 0.035f, back = 0.02f, hz = 8, z = 0.8f, jit = 0.012f, torso = 0.2f, head = 0.06f, crouch = 0.004f, brace = 1 },
                hip = -0.24f, chest = 0.12f, crouch = 0.03f,
                stance = new[] { 0.125f, 0.07f, 0.24f, -0.11f, -0.07f, -0.42f },
                raise = hp(-0.17f, 1.2f, 0.1f, -1.0f, 0.3f, -0.3f),
                lobby = hp(-0.15f, 0.8f, 0.2f, 0.25f, 0.4f, -0.15f), lobbyTwo = 1,
            },
            // pail held low in front with both hands: right fist on the handle, left palm under the bottom
            ["bucket"] = new Hold
            {
                carry = hp(-0.1f, 0.7f, 0.24f, 0.25f, 0.1f, 0), twoCarry = 1,
                aim = hp(-0.06f, -0.52f, 0.34f, 0.35f, 0.06f, 0), twoAim = 1,
                poleR = f3(-0.8f, -0.6f, -0.2f), poleL = f3(0.7f, -0.75f, -0.2f),
                rc = new Rc { kick = 0.05f, back = 0.02f, hz = 6, z = 0.4f, jit = 0.01f, torso = 0.15f, head = 0.08f, crouch = 0.02f },
                hip = -0.14f, chest = 0.06f, crouch = 0.03f,
                stance = new[] { 0.118f, 0.045f, 0.22f, -0.112f, -0.05f, -0.38f },
                raise = hp(-0.18f, 1.22f, 0.14f, -1.1f, 0.3f, -0.3f),
                lobby = hp(-0.12f, 0.72f, 0.24f, 0.3f, 0.2f, 0.1f), lobbyTwo = 1,
            },
            // heavy rotary cannon braced at the hip with both hands, wide planted stance
            ["spinner"] = new Hold
            {
                carry = hp(-0.1f, 0.74f, 0.2f, 0.3f, 0.2f, 0.15f), twoCarry = 1,
                aim = hp(-0.06f, -0.42f, 0.18f, 0, 0.06f, 0), twoAim = 1,
                poleR = f3(-0.9f, -0.5f, -0.1f), poleL = f3(0.7f, -0.8f, -0.1f),
                rc = new Rc { kick = 0.03f, back = 0.02f, hz = 14, z = 0.5f, jit = 0.018f, torso = 0.12f, head = 0.06f, crouch = 0.02f },
                hip = -0.3f, chest = 0.18f, crouch = 0.05f,
                stance = new[] { 0.13f, 0.07f, 0.26f, -0.12f, -0.07f, -0.55f },
                raise = hp(-0.17f, 1.22f, 0.1f, -1.05f, 0.3f, -0.3f),
                lobby = hp(-0.12f, 0.76f, 0.2f, 0.2f, 0.6f, 0.4f), lobbyTwo = 1,
            },
            // special: shoulder-fired launcher (Twister Zooka) — tube resting on the right shoulder beside the head, both hands
            ["sp_zooka"] = new Hold
            {
                carry = hp(-0.2f, 0.93f, 0.1f, -0.22f, 0.04f, 0), twoCarry = 1,
                aim = hp(-0.17f, 0.02f, 0.07f, 0, 0.03f, 0), twoAim = 1,
                poleR = f3(-0.55f, -0.85f, 0.05f), poleL = f3(0.55f, -0.8f, -0.1f),
                rc = new Rc { kick = 0.3f, back = 0.07f, hz = 5, z = 0.38f, jit = 0.012f, torso = 0.3f, head = 0.22f, crouch = 0.03f },
                hip = -0.36f, chest = 0.26f, crouch = 0.03f,
                stance = new[] { 0.112f, 0.085f, 0.3f, -0.098f, -0.085f, -0.7f },
                raise = hp(-0.17f, 1.22f, 0.1f, -1.05f, 0.3f, -0.3f),
                lobby = hp(-0.2f, 0.93f, 0.1f, -0.3f, 0.1f, 0), lobbyTwo = 1,
            },
            // ---- Brine Cutlass: one-handed sword. Carry low and forward at the right side, run with the blade trailing,
            // a raised guard while fighting; the cuts themselves are _poseBlade (one-shots).
            ["blade"] = new Hold
            {
                carry = hp(-0.21f, 0.72f, 0.16f, 0.62f, 0.2f, -0.95f), twoCarry = 0,
                run = hp(-0.22f, 0.76f, 0.04f, 2.0f, 0.3f, -0.35f),
                aim = hp(-0.12f, -0.06f, 0.26f, -0.45f, 0.4f, -0.7f), twoAim = 0,
                poleR = f3(-0.9f, -0.5f, -0.2f), poleL = f3(0.75f, -0.65f, -0.25f),
                rc = new Rc { kick = 0.02f, back = 0.01f, hz = 6, z = 0.6f, jit = 0.005f, torso = 0.1f, head = 0.05f, crouch = 0.004f, brace = 0 },
                hip = -0.16f, chest = 0.07f, crouch = 0.03f,
                stance = new[] { 0.13f, 0.07f, 0.22f, -0.12f, -0.08f, -0.45f },
                raise = hp(-0.2f, 1.3f, 0.1f, -1.35f, 0.2f, -0.2f),
                lobby = hp(-0.2f, 0.98f, 0.02f, -2.35f, 0.25f, 0.3f), lobbyTwo = 0,
            },
            // ---- Sponge Mitts: a sponge boxing glove in each fist (dual wield), a boxer's guard in front of the face
            ["mitts"] = new Hold
            {
                dual = true,
                carry = hp(-0.125f, 0.87f, 0.15f, -0.25f, 0.32f, 0.12f), twoCarry = 0,
                run = hp(-0.13f, 0.86f, 0.16f, -0.1f, 0.28f, 0.1f),
                aim = hp(-0.085f, 0.03f, 0.2f, -0.12f, 0.2f, 0.05f), twoAim = 0,
                poleR = f3(-0.9f, -0.6f, -0.1f), poleL = f3(0.9f, -0.6f, -0.1f),
                rc = new Rc { kick = 0.012f, back = 0.004f, hz = 7, z = 0.8f, jit = 0.01f, torso = 0.05f, head = 0.03f, crouch = 0.002f, brace = 0 },
                hip = -0.14f, chest = 0.06f, crouch = 0.03f,
                stance = new[] { 0.12f, 0.07f, 0.3f, -0.115f, -0.07f, -0.34f },
                raise = hp(-0.16f, 1.26f, 0.12f, -1.2f, 0.3f, 0.1f),
                lobby = hp(-0.13f, 0.95f, 0.17f, -0.35f, 0.4f, 0.1f), lobbyTwo = 0,
            },
            // ---- Tideline Bow: animates as the charger family with its own HOLD. The weapon origin is the nock on the
            // string in the RIGHT fist; the left fist holds the riser ahead.
            ["bow"] = new Hold
            {
                fire = "charge",
                carry = hp(-0.2f, 0.72f, 0.14f, 0.8f, -0.15f, -0.55f), twoCarry = 1,
                aim = hp(-0.14f, 0.16f, 0.33f, 0, 0.03f, 0), twoAim = 1,         // full draw: the fist beside the right cheek
                poleR = f3(-1, 0.3f, -0.45f), poleL = f3(0.6f, -0.5f, 0.1f),
                rc = new Rc { kick = 0.06f, back = 0.04f, hz = 5, z = 0.5f, jit = 0.01f, torso = 0.25f, head = 0.1f, crouch = 0.01f, brace = 0 },
                hip = -0.9f, chest = -0.8f, crouch = 0.02f,                        // side-on: the bow shoulder leads
                stance = new[] { 0.112f, 0.085f, 0.3f, -0.098f, -0.085f, -0.78f },
                raise = hp(-0.17f, 1.22f, 0.1f, -1.05f, 0.3f, -0.3f),
                lobby = hp(-0.12f, 0.76f, 0.2f, 0.5f, 0.35f, 0.1f), lobbyTwo = 1,
            },
        };
        // (HIPW spelled out: static initialisers of different partial files run in no defined order)
        static readonly float[] STANCE_IDLE = { Mathf.Abs(CharacterGeo.REST["footL"].x) + 0.008f + 0.012f, 0.014f, 0.19f, -(Mathf.Abs(CharacterGeo.REST["footL"].x) + 0.008f) - 0.008f, -0.01f, -0.2f };   // ready stance: a bit wide, toes out, left foot a touch ahead
        static readonly float[] STANCE_LOCK = { 0.165f, 0.035f, 0.42f, -0.165f, -0.035f, -0.42f };   // dualies' post-roll turret: wide and planted
        // idle re-plants of one foot (kid space, left-foot convention: +x out, +z forward, +yaw toes out)
        static readonly float[][] SHUFFLES = { new[] { 0.08f, 0.015f, 0.12f }, new[] { 0.012f, -0.085f, 0.05f }, new[] { 0.055f, -0.065f, 0.18f }, new[] { -0.012f, 0.075f, -0.08f }, new[] { 0.03f, -0.02f, 0.45f } };
        // weapons animated with another kind's pose family (animKind); every other kind is its own family
        static readonly Dictionary<string, string> ANIM_OF = new Dictionary<string, string>
        {
            ["twins"] = "shooter", ["brush"] = "roller", ["sp_blower"] = "shooter", ["sp_jetgun"] = "blaster", ["sp_stamp"] = "roller",
            ["brolly"] = "shooter", ["bow"] = "charger",
        };
        // families too heavy / long for the idle and dance weapon twirls
        static readonly HashSet<string> NO_TWIRL = new HashSet<string> { "roller", "charger", "spinner", "bucket", "sp_zooka", "sp_stamp", "mitts" };
        const float BOW_CANT = -0.85f;   // roll of the whole bow about the arrow on the ground (rad; top limb to the kid's left)
        static readonly HP HOLD_HERO = hp(-0.14f, 1.05f, 0.25f, -0.35f, 0.35f, -0.2f);
        static readonly float[] K_SL_T = { 0, 0.13f, 0.25f, 0.4f, 0.62f };
        static readonly float[] K_SL_X = { 0, -0.08f, 0.02f, 0.025f, 0 }, K_SL_Y = { 0, -0.22f, 0.24f, 0.33f, 0 }, K_SL_Z = { 0, -0.34f, 0.04f, -0.04f, 0 };
        static readonly float[] K_SL_P = { 0, -0.55f, 1.5f, 1.95f, 0 }, K_SL_W = { 0, 0.32f, -0.08f, -0.14f, 0 };
        static void setAnc(float[] D, HP h) { setE(D, ANC, h.p[0], h.p[1], h.p[2]); setE(D, ANCR, h.r[0], h.r[1], h.r[2]); }
        static readonly string[] FIDGETS = { "goggles", "twirl", "look", "stretch", "tank", "bounce", "shake" };
        static readonly float[] FIDGET_LEN = { 1.35f, 1.2f, 2.3f, 1.9f, 1.3f, 1.25f, 0.95f };
        static readonly Dictionary<string, int> DANCE_VARIANTS = new Dictionary<string, int> { ["victory"] = 3, ["defeat"] = 3 };
        static readonly float[] FINGER_SPREAD = { -0.1f, -0.03f, 0.04f, 0.11f }; // index … pinky, about local X
        static readonly float[] K_GOG_T = { 0, 0.3f, 0.45f, 0.62f, 0.8f, 1.0f, 1.35f }, K_GOG_V = { 0, 1, 1, 1, 1, 1, 0 };
        static readonly float[] K_LOOK_T = { 0, 0.35f, 0.9f, 1.25f, 1.8f, 2.3f }, K_LOOK_V = { 0, 0.75f, 0.75f, -0.7f, -0.7f, 0 };
        static readonly float[] K_TANK_T = { 0, 0.3f, 1.0f, 1.3f }, K_TANK_V = { 0, 1, 1, 0 };
        static readonly float[] K_MENU_T = { 0, 1.2f, 1.8f, 3.0f, 3.5f, 5.0f, 5.6f, 8 }, K_MENU_V = { 0, 0, 0.5f, 0.5f, -0.4f, -0.4f, 0.05f, 0 };
        // kid ⇄ squid gesture keys (see _updateFormScales): EM = squid → kid (emerge), DV = kid → squid (dive)
        static readonly float[] K_EM_ST = { 0, 0.03f, 0.07f, 0.095f }, K_EM_SY = { 1, 0.72f, 1.42f, 1.6f }, K_EM_SX = { 1, 1.18f, 0.78f, 0.62f };
        static readonly float[] K_EM_KT = { 0.045f, 0.1f, 0.16f, 0.24f, 0.32f, 0.42f }, K_EM_KY = { 1.36f, 1.13f, 0.88f, 1.05f, 0.985f, 1 }, K_EM_KX = { 0.68f, 0.9f, 1.09f, 0.975f, 1.008f, 1 };
        static readonly float[] K_DV_KT = { 0, 0.03f, 0.07f, 0.095f }, K_DV_KY = { 1, 0.82f, 0.34f, 0.16f }, K_DV_KX = { 1, 1.1f, 1.4f, 1.2f };
        static readonly float[] K_DV_ST = { 0.045f, 0.08f, 0.13f, 0.2f, 0.28f, 0.38f }, K_DV_SY = { 0.3f, 0.72f, 1.3f, 0.9f, 1.045f, 1 }, K_DV_SX = { 1.5f, 1.2f, 0.83f, 1.07f, 0.98f, 1 };

        // punch extension over time since that glove fired (out in 45 ms, back by 210 ms)
        static float mitPunch(float u) => u < 0.045f ? easeOut(u / 0.045f) : u < 0.21f ? 1 - ease((u - 0.045f) / 0.165f) : 0;
        // a kid-space point pushed onto the wall plane p·n = wd (n = the wall normal in kid space), `off` metres out from it
        static void mitOnWall(float x, float z, float off, float nx, float nz, float wd, float[] o) { float d = x * nx + z * nz - wd - off; o[0] = x - nx * d; o[1] = z - nz * d; }

        // Tideline Bow: the draw + the cant
        void poseBow(float[] P, float dt)
        {
            var w = weapon; float len = w.def.drawLen > 0 ? w.def.drawLen : 0.2f;
            // draw 0..1: a quick first pull, heavier toward full. After a release the fist holds at the cheek a beat, then
            // eases forward to the string again (the model snaps the string home by itself)
            float c = clamp(charge, 0, 1);
            if (lastRelease < _bowLR) _bowHold = _bowDraw;
            _bowLR = lastRelease;
            float draw = 1 - (1 - c) * (1 - c);
            if (lastRelease < 0.5f && c < 0.05f) draw = Mathf.Max(draw, _bowHold * (1 - ease((lastRelease - 0.1f) / 0.3f)));
            _bowDraw = draw; w.bowDraw = draw;
            // the fist back along the arrow (the weapon's +Z, in kid space)
            Vector3 v1 = qYXZ(P[ANCR], P[ANCR + 1], P[ANCR + 2]) * new Vector3(0, 0, -len * draw);
            P[ANC] += v1.x; P[ANC + 1] += v1.y; P[ANC + 2] += v1.z;
            // cant (eased, so a jump visibly swings the bow upright)
            _bowCant = damp(_bowCant, grounded ? BOW_CANT : 0, 12, dt);
            P[ANCR + 2] += _bowCant * wAim;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Pose construction (gameplay layers → P)
        // ---------------------------------------------------------------------------------------------------------
        void _buildPose(float dt, AnimState s)
        {
            var H = hold;
            poseNeutral(P);
            float gw = gaitW, v = gs, rw = runW * gw;
            float ph = phase;
            float pL = frac(ph);
            float air = wAir;
            float idleW = (1 - gw) * (1 - air);

            // ---------------- ready stance (idle): soft knees, pelvis tipped forward, chest up and a little proud, arms
            // hanging loose with bent elbows — a coiled little athlete, never a mannequin
            float rdy = idleW * (1 - 0.35f * wTired);
            P[HIPS_P + 1] -= 0.036f * idleW;
            P[HIPS] += 0.07f * rdy; P[SPINE] -= 0.03f * rdy; P[CHEST] -= 0.012f * rdy; P[NECK] -= 0.02f * rdy;
            P[UARML] -= 0.1f * rdy; P[UARML + 2] += 0.07f * rdy; P[FARML] -= 0.32f * rdy; P[HANDL + 2] -= 0.12f * rdy;
            P[CLAVL + 2] -= 0.02f * rdy; P[CLAVR + 2] += 0.02f * rdy;

            // (breathing is applied post-dance in _lifePost → _breathe, so menus and dances breathe the same way)
            float shift = spr(sp, S_SHIFT, shiftTgt * idleW * (1 - wAim * 0.8f) * (1 - wTired * 0.3f), 0.7f, 0.85f, dt);
            // weight over one leg: the pelvis slides over it and its hip rides up, shoulders tilt back the other way, head tips
            P[HIPS_P] += 0.03f * shift; P[HIPS + 2] += 0.08f * shift; P[SPINE + 2] -= 0.05f * shift; P[CHEST + 2] -= 0.035f * shift;
            P[HIPS + 1] += 0.04f * shift; P[CHEST + 1] -= 0.025f * shift; P[HEAD + 2] += 0.035f * shift;
            P[HIPS_P + 1] -= 0.008f * Mathf.Abs(shift);
            shiftS = shift;
            float ms = idleW * (1 - wAim);
            P[HIPS + 1] += 0.018f * Mathf.Sin(t * 0.41f + 1.3f) * ms; P[SPINE + 2] += 0.012f * Mathf.Sin(t * 0.53f) * ms; P[CHEST + 1] += 0.02f * Mathf.Sin(t * 0.29f + 2) * ms;
            // hips follow the planted feet when the body turns in place; the chest keeps facing the aim
            float ftw = footTwist * (1 - gw);
            P[HIPS + 1] += ftw * 0.55f; P[SPINE + 1] -= ftw * 0.3f; P[CHEST + 1] -= ftw * 0.25f;

            // ---------------- locomotion
            if (gw > 0.001f)
            {
                float bk = sstep(0.1f, -0.7f, mdz), rn = runW;
                float yawOsc = -lerp(0.11f, 0.2f, rn) * Mathf.Cos(TAU * pL) * gw * (1 - bk * 0.5f);
                float rollOsc = lerp(0.06f, 0.075f, rn) * Mathf.Cos(TAU * (pL - duty * 0.5f)) * gw;
                float swayX = lerp(0.022f, 0.013f, rn) * Mathf.Cos(TAU * (pL - duty * 0.5f - 0.06f)) * gw;
                // vertical: a run compresses through each stance (lowest ~45 % into it) and floats through the flight; a walk
                // vaults over the planted leg (highest mid-stance)
                float c2 = Mathf.Cos(TAU * 2 * (ph - duty * 0.45f));
                float bob = lerp(0.014f, -0.036f, rn) * c2 * gw * sstep(0.3f, 2.0f, v);
                P[HIPS_P + 1] += gw * lerp(-0.024f, -0.064f, rn) * (1 + 0.5f * wGoo) + bob;
                P[HIPS_P] += swayX;
                P[HIPS + 1] += hipTwist + yawOsc;
                P[HIPS + 2] += rollOsc;
                P[SPINE + 1] -= hipTwist * 0.45f + yawOsc * 0.65f;
                P[CHEST + 1] -= hipTwist * 0.55f + yawOsc * 0.8f;
                P[SPINE + 2] -= rollOsc * 0.6f; P[CHEST + 2] -= rollOsc * 0.35f;
                // run posture: a real forward lean into the stride (less when aiming, backwards when backpedalling); goo
                // wading hunches forward. The stabilised head stays level, so the lean reads as drive, not as falling over.
                float lean = gw * (lerp(0.05f, 0.3f, rn) * (1 - bk * 1.3f) * (1 - 0.55f * wAim) + 0.14f * wGoo);
                P[HIPS] += lean * 0.3f; P[SPINE] += lean * 0.45f; P[CHEST] += lean * 0.25f;
                // strafing leans into the direction of travel
                float lat = clamp(kgx / 6, -1, 1) * gw * (1 - 0.3f * wGoo);
                P[SPINE + 2] -= 0.07f * lat; P[CHEST + 2] -= 0.04f * lat; P[HIPS + 2] -= 0.03f * lat;
                // every footfall: a little squash on the compression, a stretch through the flight; a nod that the
                // stabilised head mostly soaks up
                float sq2 = 0.024f * c2 * rn * gw;
                P[SQY] *= 1 - sq2; P[SQXZ] *= 1 + sq2 * 0.45f;
                P[HEAD] -= 0.03f * c2 * rn * gw; P[NECK] += 0.02f * c2 * rn * gw;
            }

            // ---------------- lean springs: acceleration, braking, turn banking
            {
                float af = clamp(kaz, -48, 48), al = clamp(kax, -48, 48);
                float g = 1 - air * 0.75f;
                float lp = spr(sp, S_LEANP, clamp(af * 0.0075f, -0.36f, 0.3f) * g, 2.2f, 0.4f, dt);
                float lr = spr(sp, S_LEANR, clamp(-al * 0.0068f, -0.34f, 0.34f) * g, 2.0f, 0.48f, dt);
                P[HIPS] += lp * 0.3f; P[SPINE] += lp * 0.42f; P[CHEST] += lp * 0.28f;
                P[HIPS + 2] += lr * 0.4f; P[SPINE + 2] += lr * 0.35f; P[CHEST + 2] += lr * 0.25f;
                // momentum: the body lags the root when it bursts off and carries past it when it brakes, then springs back
                float lagZ = spr(sp, S_LAGZ, clamp(-kaz * 0.0012f, -0.055f, 0.07f) * g, 2.3f, 0.6f, dt);
                float lagX = spr(sp, S_LAGX, clamp(-kax * 0.0012f, -0.05f, 0.05f) * g, 2.3f, 0.6f, dt);
                P[HIPS_P + 2] += lagZ; P[HIPS_P] += lagX;
                P[HIPS_P + 1] -= Mathf.Abs(lp) * 0.06f + Mathf.Abs(lr) * 0.03f;
                // hard braking from a run: skid crouch + arms fling forward (the lean spring does the body)
                if (kaz < -26 && gs > 3 && tr[T_BRAKE] > 0.5f) { tr[T_BRAKE] = 0; sp[S_PELY + 1] -= 0.55f; sp[S_ARML + 1] -= 5; sp[S_ARMR + 1] -= 1.5f; _hairKick(0, 0, 3); sp[S_TANKZ + 1] += 1.8f; }
            }

            // ---------------- springs: pelvis dip, squash, hit reactions, stagger
            float pelY = spr(sp, S_PELY, 0, 3.4f, 0.42f, dt);
            P[HIPS_P + 1] += clamp(pelY, -0.2f, 0.08f);
            float sq = spr(sp, S_SQ, 0, 4.8f, 0.34f, dt);
            P[SQY] *= 1 + clamp(sq, -0.22f, 0.16f); P[SQXZ] *= 1 - clamp(sq, -0.22f, 0.16f) * 0.5f;
            float hpp = spr(sp, S_HITP, 0, 3.4f, 0.38f, dt), hrl = spr(sp, S_HITR, 0, 3.4f, 0.38f, dt), hy = spr(sp, S_HITY, 0, 3.4f, 0.42f, dt);
            P[SPINE] += hpp * 0.5f; P[CHEST] += hpp * 0.6f; P[HIPS] += hpp * 0.2f;
            P[SPINE + 2] += hrl * 0.5f; P[CHEST + 2] += hrl * 0.5f;
            P[SPINE + 1] += hy * 0.5f; P[CHEST + 1] += hy * 0.6f;
            P[HIPS_P + 2] += hpp * 0.04f + spr(sp, S_STAG, 0, 2.2f, 0.5f, dt) * 0.05f;
            float clv = spr(sp, S_CLAV, 0, 3.5f, 0.45f, dt);
            P[CLAVL + 2] += clv * 0.12f; P[CLAVR + 2] -= clv * 0.12f; P[UARML + 2] -= clv * 0.08f; P[UARMR + 2] += clv * 0.08f;
            P[HIPS_P + 1] -= Mathf.Abs(hpp) * 0.08f;

            // ---------------- tired / goo posture
            if (wTired > 0.01f) { float w = wTired; P[SPINE] += 0.1f * w; P[CHEST] += 0.08f * w; P[NECK] += 0.05f * w; P[HIPS_P + 1] -= 0.02f * w; P[CLAVL + 2] -= 0.05f * w; P[CLAVR + 2] += 0.05f * w; }

            // ---------------- arms: gait swing with follow-through (springs). A runner's arms: elbows bent near 90°, the
            // fist pumping forward-in to chin height and back-out past the hip; a walk just swings loose.
            {
                float two = wTwo, rn = runW;
                float armA = gw * lerp(0.3f, 0.95f, rn) * (1 - 0.35f * wGoo);
                float tgt = armA * Mathf.Cos(TAU * (pL - 0.03f));
                float aL = spr(sp, S_ARML, tgt, lerp(3.2f, 4.8f, rn), 0.5f, dt);
                float aR = spr(sp, S_ARMR, -tgt, lerp(3.2f, 4.8f, rn), 0.5f, dt);
                float fw = 1 - two;
                P[UARML] += aL * fw; P[UARMR] += aR;
                P[FARML] -= (gw * lerp(0.3f, 1.45f, rn) + 0.4f * Mathf.Max(0, -aL) * rn) * fw;
                P[UARML + 2] += (0.05f + 0.13f * rw + 0.08f * Mathf.Max(0, aL) * rn + 0.35f * wGoo * gw) * fw;
                P[UARML + 1] += 0.18f * Mathf.Max(0, -aL) * rn * fw;                 // the forward swing crosses in a little
                P[CLAVL + 1] += 0.09f * aL * fw; P[CLAVR + 1] -= 0.07f * aR;
                P[CLAVL] -= 0.05f * Mathf.Max(0, -aL) * rn * fw;                     // shoulder rides up on the forward pump
                P[HANDL] -= 0.15f * rw * fw;
                P[HANDPL] = 1 - 0.45f * rw;
                armR = aR; armL = aL;
            }

            // ---------------- air
            if (air > 0.001f) _poseAir(P, dt, air);

            // ---------------- weapon holds + aim
            _poseWeapon(dt, s);

            // ---------------- one-shots
            if (tr[T_FLICK] < 0.7f)
            {
                if (weaponKind == "brush") _poseSwipe(P, tr[T_FLICK]);
                else if (weaponKind == "bucket") _poseBucket(P, tr[T_FLICK]);
                else if (animKind == "roller") _poseFlick(P, tr[T_FLICK]);
            }
            if (animKind == "blade") _poseBlade(P, dt, s);   // Brine Cutlass cuts + charge raise
            if (wSub > 0.001f && tr[T_THROW] > 0.05f) _poseSubAim(P, wSub);
            if (tr[T_THROW] < 0.62f) _poseThrow(P, tr[T_THROW]);
            if (tr[T_SPAWN] < 1.4f) _poseSpawn(P, tr[T_SPAWN]);
            if (tr[T_LEAP] < 1.9f) _poseLeap(P, tr[T_LEAP]);
            if (tr[T_SLAM] < 1.4f) _poseSlam(P, tr[T_SLAM]);
            if (fidget >= 0) _poseFidget(P, fidget, fidgetT);
            if (tr[T_LAND] < 0.8f && landAmp > 0.3f && kidForm && grounded && tr[T_SPAWN] > 1.4f) _poseLand(P, tr[T_LAND]);
            if (formT < 0.5f) _poseForm(P);
            if (tr[T_ADMIRE] < 1.6f || tr[T_FLIP] < 1.0f || tr[T_WINK] < 0.8f) _poseLocker(P);
            if (tr[T_SLOSH] < 0.66f && hold.fire == "slosh") _poseSlosh(P, tr[T_SLOSH]);
            if (tr[T_DODGE] < dodgeDur + 0.3f) _poseDodge(P, tr[T_DODGE]);
            else { tumble = 0; tumbleDrop = 0; }
            if (weaponKind == "mitts") _poseMitts(P, dt, s);   // Sponge Mitts: punches, leap charge, leap, wall cling

            // ---------------- head look + face
            _poseLook(dt, s);
            _poseFace(dt, s);

            // ---------------- dances (override everything, blended)
            if (wDance > 0.001f || dance != null)
            {
                var D = PD;
                if (dance != null)
                {
                    poseNeutral(D); _poseDance(D, dance, danceT + danceOfs, dt);
                    if (danceFade < 1 && prevDance != null)
                    {
                        var X = PY; poseNeutral(X); _poseDance(X, prevDance, prevDanceT + danceOfs, 0);
                        poseLerp(D, X, D, ease(danceFade));
                    }
                    lastDance = dance;
                }
                else if (lastDance != null) { poseNeutral(D); _poseDance(D, lastDance, danceT + danceOfs, dt); }
                poseLerp(P, P, D, ease(wDance));
            }
            // ---------------- life layer on top of everything (micro-expressions, menu head attention, pupils)
            _lifePost(dt);
        }

        // Air: push-off stretch → knees tuck while rising (a running jump is a leap: the swing knee drives up, the push-off
        // leg trails) → floaty apex → the legs come through and reach for the ground while falling (arms up for balance,
        // windmilling on long falls). The takeoff speed and lead leg are captured by trigger('jump').
        void _poseAir(float[] P, float dt, float air)
        {
            var X = PX; System.Array.Copy(P, X, PN);
            float vy = vyS, jt = tr[T_JUMP];
            float up = sstep(-1.5f, 4, vy);                                  // 1 rising … 0 falling
            float launch = jt < 0.3f ? 1 - sstep(0.03f, 0.2f, jt) : 0;        // legs still extended from the push-off
            float apex = 1 - sstep(0.6f, 3.2f, Mathf.Abs(vy));                 // hang time at the top
            float reach = (1 - up) * sstep(0.95f, 0.15f, gnd);                // ground coming up: legs reach, knees soft
            float fallLong = sstep(0.4f, 1.0f, airT) * (1 - up) * (1 - reach);
            float lp = jt < 1.2f ? jumpRun : 0;                               // running leap vs standing jump
            float ld = jumpLead == 1 ? -1 : 1;                                // lead leg side: +1 left, −1 right
            // standing jump: knees tuck up together, then extend down, a little apart
            float tk = (1 - launch) * Mathf.Max(apex, up * 0.85f) * (1 - reach);
            setE(X, FOOTL, 0.1f, lerp(0.16f, 0.34f, tk), lerp(0.0f, 0.07f, tk)); setE(X, FOOTLR, lerp(0.15f, 0.55f, tk), 0.16f, 0);
            setE(X, FOOTR, -0.1f, lerp(0.16f, 0.31f, tk), lerp(-0.03f, 0.04f, tk)); setE(X, FOOTRR, lerp(0.25f, 0.6f, tk), -0.16f, 0);
            // running leap: lead knee driven up and forward, trail leg stretched back with the toes pointed; it cycles
            // through (legs pass) on the way down
            if (lp > 0.001f)
            {
                float pass = sstep(0.1f, -3.5f, vy);                            // 0 on the way up … 1 falling: the legs switch
                int lf = ld > 0 ? FOOTL : FOOTR, lr = ld > 0 ? FOOTLR : FOOTRR, tf = ld > 0 ? FOOTR : FOOTL, tr2 = ld > 0 ? FOOTRR : FOOTLR;
                float w = lp * (1 - reach * 0.7f);
                lerpE(X, lf, 0.09f * ld, lerp(0.36f, 0.16f, pass), lerp(0.2f, -0.12f, pass), w); lerpE(X, lr, lerp(0.25f, 0.9f, pass), 0.12f * ld, 0, w);
                lerpE(X, tf, -0.09f * ld, lerp(0.15f, 0.33f, pass), lerp(-0.27f, 0.14f, pass), w); lerpE(X, tr2, lerp(1.15f, 0.3f, pass), -0.12f * ld, 0, w);
                X[HIPS + 1] += 0.12f * ld * (1 - 2 * pass) * w; X[CHEST + 1] -= 0.1f * ld * (1 - 2 * pass) * w;
            }
            // push-off: both legs long, toes pointed (the body has left the ground, the feet trail)
            lerpE(X, FOOTL, 0.09f, 0.07f, -0.06f - 0.06f * lp, launch); lerpE(X, FOOTLR, 1.0f, 0.08f, 0, launch);
            lerpE(X, FOOTR, -0.09f, 0.09f, -0.1f - 0.06f * lp, launch); lerpE(X, FOOTRR, 1.1f, -0.08f, 0, launch);
            // landing reach: legs long and slightly apart under the body, feet flat, ready to absorb
            lerpE(X, FOOTL, 0.11f, 0.1f, 0.07f, reach); lerpE(X, FOOTLR, -0.08f, 0.14f, 0, reach);
            lerpE(X, FOOTR, -0.11f, 0.12f, -0.03f, reach); lerpE(X, FOOTRR, 0.05f, -0.14f, 0, reach);
            X[WPL] = 0; X[WPR] = 0;
            X[KNEEL] = 0.12f; X[KNEER] = -0.12f;
            X[HIPS_P + 1] = -0.03f + 0.03f * up - 0.02f * tk - 0.03f * reach;
            X[HIPS] += 0.1f * tk - 0.06f * launch; X[SPINE] += 0.12f * tk - 0.1f * launch + 0.04f * reach; X[CHEST] += 0.05f * tk - 0.05f * launch;
            X[HLP] += -0.1f * launch + 0.12f * reach + 0.05f * tk;
            // free arm: thrown up with the push-off, spread at the apex, up and out for balance falling, windmilling on
            // long falls; the weapon arm follows its anchor (IK) but the carry lifts a little
            float wm = Mathf.Sin(t * 10) * 0.5f * fallLong;
            X[UARML] = lerp(lerp(-1.0f, -0.5f, up), -2.3f, launch) + wm; X[UARML + 2] = lerp(lerp(1.25f, 0.95f, up), 0.4f, launch) + 0.2f * apex; X[FARML] = lerp(-0.7f, -0.45f, up) - 0.4f * launch;
            X[UARMR] = lerp(lerp(-0.8f, -0.35f, up), -1.9f, launch) - wm; X[UARMR + 2] = -lerp(lerp(1.15f, 0.75f, up), 0.3f, launch); X[FARMR] = lerp(-0.6f, -0.4f, up);
            X[CLAVL + 2] += 0.1f * (1 - up) + 0.06f * launch; X[CLAVR + 2] -= 0.1f * (1 - up);
            X[ANC + 1] += 0.03f * (1 - wAim) * (apex + launch); X[ANCR] -= 0.2f * (1 - wAim) * launch;
            X[HANDPL] = 1.4f + 0.5f * (1 - up) + 0.1f * fallLong; X[EARS] += 0.7f * fallLong - 0.4f * launch + 0.4f * apex;
            X[SQY] *= 1 + 0.09f * launch + 0.035f * sstep(-4, -12, vy) - 0.02f * apex; X[SQXZ] *= 1 - 0.045f * launch;
            X[MOPEN] = Mathf.Max(X[MOPEN], 0.25f * apex + 0.45f * fallLong); X[MCURVE] += 0.25f * apex - 0.8f * fallLong;
            X[EYE] += 0.12f * apex + 0.15f * fallLong; X[BROWY] += 0.3f * apex + 0.6f * fallLong;
            X[LOOKY] -= 0.15f * reach;
            poseLerp(P, P, X, air);
        }

        // Weapon anchor (rest-torso kid space) blended over carry / aim / roll + follow weights, stance yaw, recoil, charge.
        // Kinds are data (HOLD): dual wield mirrors the anchor into the left fist; lock / spin / slosh are generic layers.
        void _poseWeapon(float dt, AnimState s)
        {
            var H = hold;
            float ap = clamp(s.aimPitch, -1.0f, 1.15f);
            float aimPose = clamp(ap, -0.8f, 1.0f);
            float gw = gaitW;
            aimP = ap;
            // carry (one-handed carries swing a little with the arm; a dual carry swings each pistol with its own arm)
            var c = H.carry;
            float one = 1 - H.twoCarry;
            float swR = clamp(armR, -0.45f, 0.45f) * one, swL = dual ? clamp(armL, -0.45f, 0.45f) : 0;
            float swA = dual ? 0 : swR;
            setE(P, ANC, c.p[0], c.p[1] + 0.012f * Mathf.Sin(TAU * 2 * phase) * gw, c.p[2] - swA * 0.1f);
            setE(P, ANCR, c.r[0] + swA * 0.55f, c.r[1], c.r[2]);
            // running carry: the weapon comes up and forward, ready (HOLD.run, or the carry lifted toward level)
            float rk = gw * runW;
            if (rk > 0.001f)
            {
                if (H.run != null) { lerpE(P, ANC, H.run.p[0], H.run.p[1] + 0.012f * Mathf.Sin(TAU * 2 * phase), H.run.p[2] - swA * 0.08f, rk); lerpE(P, ANCR, H.run.r[0] + swA * 0.4f, H.run.r[1], H.run.r[2], rk); }
                else { P[ANC + 1] += 0.025f * rk; P[ANC + 2] += 0.04f * rk; P[ANCR] -= 0.18f * rk; }
            }
            // aim: rotate about the aim pivot with the camera pitch (the dualies' post-roll lock is a lower, wider variant)
            if (wAim > 0.001f)
            {
                var a = H.aim; float lk = H.@lock != null ? lockW : 0;
                float ax = lerp(a.p[0], lk > 0 ? H.@lock.p[0] : 0, lk), ay = lerp(a.p[1], lk > 0 ? H.@lock.p[1] : 0, lk), az = lerp(a.p[2], lk > 0 ? H.@lock.p[2] : 0, lk);
                Vector3 v1 = axisAngle(new Vector3(ax, ay, az), XAX, -aimPose) + AIM_PIVOT;
                lerpE(P, ANC, v1.x, v1.y, v1.z, wAim);
                lerpE(P, ANCR, a.r[0] - ap, lerp(a.r[1], lk > 0 ? H.@lock.r[1] : 0, lk), lerp(a.r[2], lk > 0 ? H.@lock.r[2] : 0, lk), wAim);
                lerpE(P, POLER, H.poleR[0], H.poleR[1], H.poleR[2], wAim);
                lerpE(P, POLEL, H.poleL[0], H.poleL[1], H.poleL[2], wAim);
                P[AFOLR] = lerp(P[AFOLR], 0, wAim);
                // aim stance: hips turn with the feet, chest turns back to the target, spine pitches with the aim
                float st = wAim * (1 - 0.6f * gw);
                P[HIPS + 1] += H.hip * st; P[SPINE + 1] -= H.hip * 0.35f * st; P[CHEST + 1] += (H.chest - H.hip * 0.65f) * st;
                P[SPINE] -= aimPose * 0.1f * wAim; P[CHEST] -= aimPose * 0.18f * wAim;
                P[HIPS_P + 1] -= H.crouch * st;
                P[NECK] -= ap * 0.08f * wAim;
                // charger: charge breathing — sway shrinks as the charge builds, a tremble at full charge, cheek to the scope
                if (animKind == "charger")
                {
                    float ch = charge;
                    float bw = (1 - ch) * 0.012f * wAim;
                    P[ANC + 1] += Mathf.Sin(TAU * brPh) * bw; P[ANC] += Mathf.Sin(TAU * brPh * 0.5f + 1) * bw * 0.6f;
                    float tremble = ch >= 0.99f ? 0.0025f * Mathf.Sin(t * 71) + 0.0018f * Mathf.Sin(t * 53 + 1) : 0;
                    P[ANC + 1] += tremble; P[ANCR] += tremble * 3;
                    P[HEAD + 2] -= 0.12f * ch * wAim; P[NECK + 2] -= 0.05f * ch * wAim; P[HLY] -= 0.05f * ch * wAim;
                    P[HIPS_P + 1] -= 0.02f * ch * wAim; P[CHEST] += 0.04f * ch * wAim;
                }
                // dualies lock (after a roll): planted turret — wide low stance, both arms locked forward, no carry sway
                if (lk > 0.001f)
                {
                    P[HIPS_P + 1] -= 0.05f * lk; P[HIPS] += 0.1f * lk; P[SPINE] += 0.04f * lk; P[CHEST] -= 0.02f * lk;
                    P[HLP] += 0.04f * lk; P[KNEEL] += 0.12f * lk; P[KNEER] -= 0.12f * lk;
                    P[CLAVL + 1] -= 0.08f * lk; P[CLAVR + 1] += 0.08f * lk;
                }
            }
            if (weaponKind == "bow") poseBow(P, dt);   // Tideline Bow: the draw + the cant
            // splatling: spin-up leans back onto the rear foot with the muzzle rising from low to level (tremble at full
            // charge); the stream plants the kid leaning into a sustained push-back
            if (H.fire == "spin")
            {
                var R = _runner();
                bool charging = R != null ? R.charging : s.firing && s.charge > 0.001f;
                bool streaming = R != null && R.burstT > 0;
                float chg = R != null ? clamp(R.charge, 0, 1) : clamp(s.charge, 0, 1);
                spinW = damp(spinW, charging ? 1 : 0, charging ? 12 : 7, dt);
                streamW = damp(streamW, streaming ? 1 : 0, streaming ? 16 : 5, dt);
                float cw = spinW * wAim, sw2 = streamW * wAim;
                if (cw > 0.001f)
                {
                    P[ANCR] += 0.42f * (1 - ease(chg)) * cw;                                  // muzzle low → level as it charges
                    P[ANC + 1] -= 0.03f * (1 - chg) * cw; P[ANC + 2] -= 0.02f * cw;
                    P[SPINE] -= 0.07f * cw; P[CHEST] -= 0.05f * cw; P[HIPS_P + 2] -= 0.025f * cw; P[HIPS_P] -= 0.012f * cw;
                    P[HIPS_P + 1] -= 0.012f * cw; P[HLP] += 0.03f * cw;
                    float full = sstep(0.9f, 1, chg) * cw;
                    if (full > 0.001f)
                    {
                        float tr1 = 0.0035f * Mathf.Sin(t * 67) + 0.0024f * Mathf.Sin(t * 91 + 1.7f), tr2b = 0.0028f * Mathf.Sin(t * 59 + 0.6f);
                        P[ANC + 1] += tr1 * full; P[ANC] += tr2b * full; P[ANCR] += tr1 * 3 * full; P[CHEST + 2] += tr2b * 2 * full;
                    }
                    _effort = Mathf.Max(_effort, 0.35f + 0.5f * chg * cw);
                }
                if (sw2 > 0.001f)
                {
                    P[SPINE] += 0.075f * sw2; P[CHEST] += 0.045f * sw2; P[HIPS_P + 2] -= 0.018f * sw2; P[HIPS_P + 1] -= 0.018f * sw2;
                    P[ANC + 2] -= 0.012f * sw2; P[NECK] += 0.02f * sw2; P[KNEEL] += 0.08f * sw2; P[KNEER] -= 0.08f * sw2;
                    P[ANCR] += (0.008f * Mathf.Sin(t * 5.3f) + 0.004f * Mathf.Sin(t * 11.1f)) * sw2;   // fighting the stream
                    _effort = Mathf.Max(_effort, 0.7f * sw2);
                }
            }
            // roller push: arms extended, leaning into the handle, drum pressed to the ground
            if (wRoll > 0.001f && H.roll != null)
            {
                var a = H.roll;
                lerpE(P, ANC, a.p[0], a.p[1], a.p[2], wRoll);
                lerpE(P, ANCR, a.r[0], a.r[1], a.r[2], wRoll);
                P[AFOLT] = lerp(P[AFOLT], 0.45f, wRoll); P[AFOLR] = lerp(P[AFOLR], 0.15f, wRoll);
                P[SPINE] += 0.17f * wRoll; P[CHEST] += 0.1f * wRoll; P[HIPS] += 0.08f * wRoll;
                P[HIPS_P + 1] -= H.crouch * wRoll; P[HIPS_P + 2] -= 0.02f * wRoll;
                P[HLP] += 0.05f * wRoll;
                lerpE(P, POLER, -0.7f, -0.35f, -0.7f, wRoll); lerpE(P, POLEL, 0.7f, -0.35f, -0.7f, wRoll);
                P[HIPS + 1] += H.hip * wRoll * (1 - gw);
            }
            // two hands on one weapon (foregrip IK) — a dual wield is never "two-handed": each fist holds its own pistol
            wTwo = dual ? 0 : Mathf.Max(lerp(H.twoCarry, H.twoAim, wAim), H.roll != null ? 1 : 0);
            P[IKL] = dual ? 1 : wTwo;
            P[CLAVL + 1] -= 0.28f * wTwo; P[CLAVR + 1] += 0.1f * wTwo; P[CLAVL + 2] += 0.04f * wTwo;
            // support hand can't quite reach the foregrip: protract the shoulder and turn the chest into the gun until it
            // does (integrating on last frame's IK error — the grip never visibly separates)
            float le = ikErrPre * (wTwo > 0.5f ? 1 : 0);
            lReach = clamp(lReach + (le > 0.004f ? le * 30 : -0.5f) * dt, 0, 0.55f);
            P[CLAVL + 1] -= lReach; P[CHEST + 1] -= lReach * 0.35f; P[CLAVL] -= lReach * 0.3f;
            if (wTwo > 0) P[UARML] *= 1 - wTwo;
            // sustained fire: lean into the gun, knees soften, a slow fight-the-muzzle wander; letting go gives a small dip
            // + settle (follow-through). Blaster: racking the pump tips the muzzle down and turns the chest into the pull.
            {
                float br = H.rc.brace;
                float want = br > 0 && s.firing && lastShot < 0.22f && animKind != "roller" ? 1 : 0;
                if (want == 0 && _fireWant != 0) sp[S_RCP + 1] -= 1.1f * br * fireHold;
                _fireWant = want;
                fireHold = damp(fireHold, want, want > 0 ? 9 : 4, dt);
                float fh = fireHold * wAim * br;
                if (fh > 0.001f)
                {
                    P[CHEST] += 0.055f * fh; P[SPINE] += 0.02f * fh; P[HIPS_P + 1] -= 0.012f * fh; P[NECK] += 0.02f * fh;
                    P[ANCR] += (0.012f * Mathf.Sin(t * 4.1f + 1) + 0.006f * Mathf.Sin(t * 9.7f)) * fh; P[ANCR + 1] += 0.01f * Mathf.Sin(t * 3.3f) * fh;
                    P[ANC + 2] -= 0.012f * fh;
                }
                float pk = weapon != null ? weapon.pump : 0;
                if (pk != 0) { P[ANCR] += 0.06f * pk; P[CHEST + 1] += 0.05f * pk * wAim; P[SPINE + 1] += 0.02f * pk * wAim; P[ANC + 2] -= 0.01f * pk; }
            }
            // dual wield: the left pistol's anchor is the right one mirrored across the kid's midline (+ its own arm swing)
            if (dual)
            {
                P[ANL] = -P[ANC]; P[ANL + 1] = P[ANC + 1]; P[ANL + 2] = P[ANC + 2];
                P[ANLR] = P[ANCR]; P[ANLR + 1] = -P[ANCR + 1]; P[ANLR + 2] = -P[ANCR + 2];
                float cw = 1 - wAim;
                P[ANC + 2] -= swR * 0.1f * cw; P[ANCR] += swR * 0.55f * cw;
                P[ANL + 2] -= swL * 0.1f * cw; P[ANLR] += swL * 0.55f * cw;
            }
            // recoil springs (impulses come from trigger('shoot' / 'charge_release'); dual wield: one set per hand)
            var rc = H.rc;
            float rp0 = sprA(sp, S_RCP, 0, rc.hz, rc.z, dt), rz0 = sprA(sp, S_RCZ, 0, rc.hz, rc.z, dt);
            sprA(sp, S_RCY, 0, rc.hz * 1.3f, 0.5f, dt); sprA(sp, S_RCR, 0, rc.hz * 1.3f, 0.5f, dt);
            P[CHEST] -= rp0 * rc.torso; P[SPINE] -= rp0 * rc.torso * 0.4f; P[HEAD] -= rp0 * rc.head;
            P[HIPS_P + 2] -= rz0 * 0.35f; P[HIPS_P + 1] -= Mathf.Abs(rz0) * rc.crouch * 8;
            P[CLAVR + 1] -= rz0 * 1.5f;
            rcP = rp0; rcZ = rz0;
            if (dual)
            {
                float rp2 = sprA(sp, S_RCP2, 0, rc.hz, rc.z, dt), rz2 = sprA(sp, S_RCZ2, 0, rc.hz, rc.z, dt);
                sprA(sp, S_RCY2, 0, rc.hz * 1.3f, 0.5f, dt);
                P[CHEST] -= rp2 * rc.torso; P[SPINE] -= rp2 * rc.torso * 0.4f; P[HEAD] -= rp2 * rc.head;
                P[HIPS_P + 2] -= rz2 * 0.35f; P[CLAVL + 1] += rz2 * 1.5f;
                // alternating shots twist the chest a hair toward the firing side
                P[CHEST + 1] += (rz2 - rz0) * 0.6f;
                rcP2 = rp2; rcZ2 = rz2;
            }
        }

        // Moving weapon parts: WeaponModel.animate(w, st) runs once per held instance per frame with one reused state
        // object. Near/far LOD by camera distance (far = merged static weapon).
        void _animWeapon(float dt, AnimState s, WeaponModel.Instance w)
        {
            bool near = true;
            if (inWorld && !isLocal && G.rig != null) near = (G.rig.camera.position - root.localPosition).sqrMagnitude < 15 * 15;
            bool farT = lod.tier == T_FAR && lod.to < 0;
            if (farT) near = false;
            var st = _wst;
            st.t = t; st.dt = dt; st.color = color; st.near = near; st.hand = 0;
            st.runner = _runner(); st.sinceShoot = tr[T_SHOOT]; st.sinceFlick = tr[T_FLICK]; st.sinceRelease = lastRelease;
            st.charge = charge; st.full = fullT > 0; st.chargeFlash = chargeFlash; st.lowInk = wLow; st.firing = s.firing;
            st.rolling = wRoll; st.grounded = grounded; st.groundSpeed = gv;
            st.worldQuat = w.def.kind == "slosher" && near ? (Quaternion?)w.off.rotation : null;
            WeaponModel.animate(w, st);
            if (w.left != null)
            {
                st.hand = 1; st.sinceShoot = tr[T_SHOOTL];
                WeaponModel.animate(w.left, st);
            }
        }

        // Roller flick: coiled windup over the shoulder → whip (release at the weapon's windup time) → follow-through.
        void _poseFlick(float[] P, float ft)
        {
            var X = PX; System.Array.Copy(P, X, PN);
            float kUp = ease(ft / 0.15f);                 // coil
            float kWhip = ease((ft - 0.15f) / 0.08f);      // whip through release (~0.22 s)
            float kFol = easeOut((ft - 0.23f) / 0.14f);    // follow-through
            float kRec = ease((ft - 0.42f) / 0.26f);       // recover
            float ax = -0.11f, ay = 0.84f, az = 0.18f, rx = 0.8f, ry = 0.1f;
            ax = lerp(ax, -0.13f, kUp); ay = lerp(ay, 1.22f, kUp); az = lerp(az, -0.02f, kUp); rx = lerp(rx, -2.3f, kUp); ry = lerp(ry, 0.3f, kUp);
            ax = lerp(ax, -0.07f, kWhip); ay = lerp(ay, 0.98f, kWhip); az = lerp(az, 0.3f, kWhip); rx = lerp(rx, -0.1f, kWhip); ry = lerp(ry, 0.05f, kWhip);
            ax = lerp(ax, -0.06f, kFol); ay = lerp(ay, 0.74f, kFol); az = lerp(az, 0.3f, kFol); rx = lerp(rx, 0.75f, kFol);
            float w = 1 - kRec;
            lerpE(X, ANC, ax, ay, az, w); lerpE(X, ANCR, rx, ry, 0, w);
            X[AFOLT] = lerp(X[AFOLT], 0.6f, w); X[AFOLR] = lerp(X[AFOLR], 0.25f, w);
            float coil = kUp * (1 - kWhip), whip = kWhip * (1 - kRec);
            X[SPINE] += -0.22f * coil + 0.34f * whip; X[CHEST] += -0.14f * coil + 0.2f * whip;
            X[SPINE + 1] += 0.2f * coil - 0.12f * whip; X[CHEST + 1] += 0.18f * coil - 0.1f * whip;
            X[HIPS_P + 1] -= 0.03f * coil + 0.05f * whip; X[HIPS_P + 2] += -0.02f * coil + 0.03f * whip;
            X[HLP] += 0.06f * coil - 0.1f * whip;
            lerpE(X, POLER, -0.8f, 0.1f, -0.3f, coil); lerpE(X, POLEL, 0.8f, 0.1f, -0.3f, coil);
            _effort = Mathf.Max(_effort, coil + whip * 0.7f);
            poseLerp(P, P, X, 1);
        }

        // Bucket (trigger 'flick'): two-handed underarm throw — dip and swing the pail back beside the hip, heave it
        // forward and up so the mouth tips out (ink leaves at ~0.12 s), follow through, settle back into the hold.
        void _poseBucket(float[] P, float ft)
        {
            var X = PX; System.Array.Copy(P, X, PN);
            float kBack = ease(ft / 0.11f);
            float kFwd = ease((ft - 0.09f) / 0.1f);
            float kFol = easeOut((ft - 0.19f) / 0.12f);
            float kRec = ease((ft - 0.34f) / 0.24f);
            float ax = -0.1f, ay = 0.7f, az = 0.24f, rx = 0.25f;
            ax = lerp(ax, -0.2f, kBack); ay = lerp(ay, 0.48f, kBack); az = lerp(az, -0.1f, kBack); rx = lerp(rx, 0.95f, kBack);
            ax = lerp(ax, -0.08f, kFwd); ay = lerp(ay, 0.9f, kFwd); az = lerp(az, 0.42f, kFwd); rx = lerp(rx, -0.5f, kFwd);
            ay = lerp(ay, 0.98f, kFol); az = lerp(az, 0.46f, kFol); rx = lerp(rx, -0.85f, kFol);
            float w = 1 - kRec;
            lerpE(X, ANC, ax, ay, az, w); lerpE(X, ANCR, rx, 0.05f, 0, w);
            float back = kBack * (1 - kFwd), heave = kFwd * (1 - kRec);
            X[SPINE] += 0.3f * back - 0.12f * heave; X[CHEST] += 0.14f * back - 0.1f * heave;
            X[SPINE + 1] += 0.16f * back - 0.06f * heave; X[CHEST + 1] += 0.12f * back;
            X[HIPS_P + 1] -= 0.07f * back - 0.02f * heave; X[HIPS_P + 2] += -0.04f * back + 0.05f * heave;
            X[HLP] += 0.08f * back - 0.06f * heave;
            lerpE(X, POLER, -0.8f, -0.7f, 0.1f, back); lerpE(X, POLEL, 0.8f, -0.7f, 0.1f, back);
            _effort = Mathf.Max(_effort, back * 0.6f + heave);
            poseLerp(P, P, X, 1);
        }

        // Brush: a quick sideways swipe low across the body (alternating direction), torso twisting with the sweep.
        void _poseSwipe(float[] P, float ft)
        {
            var X = PX; System.Array.Copy(P, X, PN);
            float sd = swipeSide != 0 ? swipeSide : 1;
            float k = ease(ft / 0.13f);
            float kRec = ease((ft - 0.15f) / 0.2f);
            float u = lerp(-sd, sd, k);                       // -1 … 1 across the body
            float w = 1 - kRec;
            lerpE(X, ANC, -0.06f + 0.3f * u, 0.78f, 0.28f, w);
            lerpE(X, ANCR, 0.9f, 0.95f * u, -0.2f * u, w);
            X[SPINE + 1] += 0.3f * u * w; X[CHEST + 1] += 0.24f * u * w; X[HIPS + 1] += 0.1f * u * w;
            X[SPINE] += 0.14f * w; X[CHEST] += 0.06f * w; X[HIPS_P + 1] -= 0.03f * w;
            lerpE(X, POLER, -0.7f, -0.35f, -0.7f, w); lerpE(X, POLEL, 0.7f, -0.35f, -0.7f, w);
            _effort = Mathf.Max(_effort, 0.5f * w);
            poseLerp(P, P, X, 1);
        }

        // ---- Brine Cutlass (kits/blade.js) ----------------------------------------------------------------------------
        // Quick cut ('blade_slash', side ±1): the fist sweeps a flat, slightly diagonal arc across the chest, the edge
        // leading, the torso whipping round with it; follow-through, then back to the guard. Charging: the blade rises
        // over the right shoulder, cocked back. Charged cut ('blade_heavy'): the blade whips over and down in front.
        float blRaise;
        void _poseBlade(float[] P, float dt, AnimState s)
        {
            float bt = blT, sd = blSide != 0 ? blSide : 1; bool heavy = blHeavy;
            float ch = clamp(s.charge, 0, 1);
            bool up = ch > 0.001f && kidForm && dance == null;
            blRaise = damp(blRaise, up ? 1 : 0, up ? 18 : 9, dt);
            float rw = blRaise * (heavy && bt < 0.45f ? 0 : 1);
            float cutW = heavy ? (bt < 0.6f ? 1 - ease((bt - 0.26f) / 0.3f) : 0) : (bt < 0.4f ? ease(bt / 0.03f) * (1 - ease((bt - 0.17f) / 0.2f)) : 0);
            if (rw < 0.001f && cutW < 0.001f) return;
            var X = PX; System.Array.Copy(P, X, PN);
            if (rw > 0.001f)
            {
                float k = ease(ch), full = ch >= 0.999f ? 1 : 0;
                float trem = full > 0 ? 0.004f * Mathf.Sin(t * 63) + 0.003f * Mathf.Sin(t * 41 + 1) : 0;
                lerpE(X, ANC, -0.2f, lerp(1.16f, 1.3f, k) + trem, lerp(0.05f, -0.03f, k), rw);
                lerpE(X, ANCR, lerp(-1.75f, -2.35f, k) + trem * 4, lerp(0.12f, 0.22f, k), lerp(0.05f, 0.22f, k), rw);
                X[AFOLT] = lerp(X[AFOLT], 0.55f, rw); X[AFOLR] = lerp(X[AFOLR], 0.25f, rw);
                X[SPINE + 1] -= 0.16f * k * rw; X[CHEST + 1] -= 0.18f * k * rw; X[HIPS + 1] -= 0.05f * rw;   // coil: shoulders turn right
                X[SPINE] -= 0.05f * k * rw; X[CHEST] -= 0.05f * rw; X[HIPS_P + 1] -= 0.025f * k * rw;
                lerpE(X, POLER, -0.95f, 0.15f, -0.35f, rw);
                _effort = Mathf.Max(_effort, (0.3f + 0.6f * k) * rw);
            }
            if (cutW > 0.001f)
            {
                float w = cutW;
                float ax, ay, az, rx, ry, rz;
                if (!heavy)
                {
                    // sweep 0.02 → 0.11 s along the arc (φ = where the fist is round the pivot), the blade pointing out along it
                    float k = easeOut(clamp((bt - 0.015f) / 0.095f, 0, 1));
                    float f0 = sd > 0 ? -1.3f : 1.15f, f1 = sd > 0 ? 1.15f : -1.3f, f = lerp(f0, f1, k);
                    ax = -0.08f + 0.34f * Mathf.Sin(f); az = 0.06f + 0.3f * Mathf.Cos(f);
                    ay = sd > 0 ? 1.03f - 0.13f * k : 0.91f + 0.11f * k;
                    ry = f * 1.08f; rx = sd > 0 ? lerp(-0.18f, 0.28f, k) : lerp(0.24f, -0.16f, k); rz = sd * Mathf.PI * 0.5f;
                    float u = lerp(-sd, sd, k);                                 // −1 … 1 across the body (+ = toward the left)
                    X[SPINE + 1] += 0.26f * u * w; X[CHEST + 1] += 0.24f * u * w; X[HIPS + 1] += 0.1f * u * w;
                    X[SPINE] += 0.08f * w; X[HIPS_P + 1] -= 0.02f * w;
                    X[CLAVR + 1] += 0.12f * Mathf.Max(0, u) * w;                   // shoulder reaches across on the forehand
                    lerpE(X, POLER, -0.8f, -0.55f, -0.1f, w);
                    _effort = Mathf.Max(_effort, 0.55f * w);
                }
                else
                {
                    // over and down: raised (cocked) → level in front (0.06 s) → low in front (0.12 s), the body lunging into it
                    float k1 = ease(bt / 0.06f), k2 = easeOut((bt - 0.055f) / 0.07f);
                    ax = lerp(lerp(-0.2f, -0.1f, k1), -0.07f, k2); ay = lerp(lerp(1.3f, 1.08f, k1), 0.62f, k2); az = lerp(lerp(-0.03f, 0.42f, k1), 0.47f, k2);
                    rx = lerp(lerp(-2.35f, -0.15f, k1), 0.95f, k2); ry = lerp(lerp(0.22f, 0.08f, k1), 0.04f, k2); rz = lerp(0.22f, 0, k1);
                    float lg = ease(bt / 0.08f) * (1 - ease((bt - 0.3f) / 0.25f));
                    X[SPINE] += 0.2f * lg; X[CHEST] += 0.1f * lg; X[HIPS] += 0.06f * lg; X[NECK] -= 0.1f * lg;
                    X[HIPS_P + 1] -= 0.045f * lg; X[HIPS_P + 2] += 0.04f * lg;
                    X[SPINE + 1] += 0.12f * lg; X[CHEST + 1] += 0.08f * lg;       // uncoil through the cut
                    X[KNEEL] += 0.05f * lg; X[KNEER] -= 0.05f * lg;
                    lerpE(X, POLER, -0.85f, -0.45f, -0.3f, w);
                    _effort = Mathf.Max(_effort, 0.9f * w);
                }
                lerpE(X, ANC, ax, ay, az, w); lerpE(X, ANCR, rx, ry, rz, w);
                X[AFOLT] = lerp(X[AFOLT], 0.35f, w); X[AFOLR] = lerp(X[AFOLR], 0.12f, w);
            }
            poseLerp(P, P, X, 1);
        }

        // Heavy landing (falls from height): the springs give the squash; this adds the absorb — a deep squat with the
        // chest folding over the knees, the free arm flung down and out (a hand to the deck on the biggest drops), head
        // dipping, then a push back up. Scaled by the impact; small hops only get the springs.
        void _poseLand(float[] P, float lt)
        {
            float a = landAmp; var X = PX; System.Array.Copy(P, X, PN);
            float k = win(lt, 0, 0.035f, 0.07f + 0.13f * a, 0.3f + 0.38f * a) * sstep(0.3f, 0.75f, a);
            if (k <= 0.001f) return;
            float big = sstep(0.7f, 0.95f, a) * (1 - wAim) * (1 - wTwo);
            X[HIPS_P + 1] -= 0.11f * a; X[HIPS_P + 2] -= 0.02f * a; X[HIPS] += 0.14f * a; X[SPINE] += 0.26f * a; X[CHEST] += 0.1f * a;
            X[HLP] += 0.12f * a; X[NECK] += 0.08f * a; X[KNEEL] += 0.16f * a; X[KNEER] -= 0.16f * a;
            X[UARML] = lerp(X[UARML], -0.35f, 0.8f); X[UARML + 2] = lerp(X[UARML + 2], 0.95f, 0.8f); X[FARML] = lerp(X[FARML], -0.25f, 0.8f);
            X[HANDPL] = lerp(X[HANDPL], 2, 0.8f);
            // biggest drops: the free hand slaps the deck beside the front foot
            X[LTW] = Mathf.Max(X[LTW], big); setE(X, LTGT, 0.24f, 0.1f, 0.2f); X[IKL] *= 1 - big;
            lerpE(X, POLEL, 1, 0.2f, 0, big); X[HIPS_P + 1] -= 0.05f * big; X[SPINE] += 0.12f * big;
            X[SQUINT] += 0.5f * a; X[MCURVE] -= 0.3f * a; X[EARS] -= 0.7f * a;
            _effort = Mathf.Max(_effort, 0.5f * a * k);
            poseLerp(P, P, X, k);
        }

        // Locker one-shots: 'admire' (glance down at the new outfit, tug the tee hem, a satisfied nod), 'hairflip' (a head
        // toss that flings the tentacles, the free hand brushing past), 'wink' (tilt, wink, grin).
        bool _tugged;
        void _poseLocker(float[] P)
        {
            var X = PX; System.Array.Copy(P, X, PN);
            float w = 0;
            if (tr[T_ADMIRE] < 1.6f)
            {
                float tt = tr[T_ADMIRE], k = win(tt, 0, 0.25f, 1.05f, 1.5f), tug = win(tt, 0.35f, 0.5f, 0.75f, 0.95f);
                w = Mathf.Max(w, k);
                X[HLP] += 0.42f * k; X[HLY] += 0.22f * k; X[SPINE] += 0.06f * k; X[CHEST + 1] += 0.12f * k; X[LOOKY] -= 0.25f * k;
                X[LTW] = k; setE(X, LTGT, 0.08f, 0.735f - 0.035f * tug, 0.13f); X[IKL] *= 1 - k;
                lerpE(X, POLEL, 0.8f, -0.4f, 0.2f, k); X[HANDPL] = lerp(X[HANDPL], -0.6f, k);
                X[MCURVE] += 0.45f * k; X[BROWY] += 0.3f * k; X[EARS] += 0.3f * k;
                float nod = win(tt, 1.0f, 1.12f, 1.2f, 1.4f); X[HLP] += 0.12f * nod; X[HEAD] += 0.06f * nod;
                if (tug > 0.9f && !_tugged) { _tugged = true; sp[S_HEMP + 1] -= 2; } else if (tug < 0.2f) _tugged = false;
            }
            if (tr[T_FLIP] < 1.0f)
            {
                float tt = tr[T_FLIP], k = win(tt, 0, 0.12f, 0.55f, 0.95f), toss = win(tt, 0.1f, 0.22f, 0.3f, 0.5f);
                w = Mathf.Max(w, k);
                X[HEAD + 2] += -0.25f * k + 0.4f * toss; X[HLP] -= 0.15f * toss; X[HLY] += 0.15f * k;
                X[LTW] = Mathf.Max(X[LTW], win(tt, 0.02f, 0.15f, 0.3f, 0.55f)); setE(X, LTGT, 0.2f, 1.22f, 0.02f); X[IKL] *= 1 - k;
                lerpE(X, POLEL, 1, 0.3f, -0.2f, k); X[HANDPL] = lerp(X[HANDPL], 1.9f, k);
                X[MCURVE] += 0.6f * k; X[MTILT] += 0.2f * k; X[EYE] -= 0.35f * toss; X[EARS] += 0.6f * toss;
                if (tt >= 0.2f && tt - _dt < 0.2f) { _hairKick(-2.5f, 3, 1); sp[S_EARL + 1] += 4; sp[S_EARR + 1] += 4; }
            }
            if (tr[T_WINK] < 0.8f)
            {
                float tt = tr[T_WINK], k = win(tt, 0, 0.1f, 0.45f, 0.75f);
                w = Mathf.Max(w, k);
                X[WINK] = Mathf.Max(X[WINK], win(tt, 0.08f, 0.14f, 0.4f, 0.5f)); X[HEAD + 2] -= 0.16f * k; X[HLY] += 0.08f * k;
                X[MCURVE] += 0.7f * k; X[MTILT] += 0.25f * k; X[MOPEN] = Mathf.Max(X[MOPEN], 0.15f * k); X[BROW] -= 0.2f * k; X[EARS] += 0.5f * k;
            }
            poseLerp(P, P, X, clamp(w * 4, 0, 1));
        }

        // Kid-side acting for the transform: diving in, a dip with the free arm thrown up (like diving into water);
        // springing out, the free arm flings up and out, ears perk, a quick grin — the kid arrives with a flourish.
        void _poseForm(float[] P)
        {
            float ft = formT;
            if (form != "kid" && formPrev == "kid")
            {
                if (ft > 0.1f) return;
                float k = ease(ft / 0.05f), fw = 1 - wTwo;
                P[UARML] = lerp(P[UARML], -2.4f, k * fw); P[UARML + 2] = lerp(P[UARML + 2], 0.35f, k * fw); P[FARML] = lerp(P[FARML], -0.2f, k * fw);
                P[SPINE] += 0.2f * k; P[CHEST] += 0.08f * k; P[HLP] += 0.22f * k; P[HIPS_P + 1] -= 0.05f * k; P[EARS] -= 0.8f * k;
                P[HANDPL] = lerp(P[HANDPL], 2, k); P[EYE] -= 0.45f * k; P[MCURVE] += 0.35f * k;
            }
            else if (form == "kid" && formPrev != "kid")
            {
                if (ft >= 0.045f && ft - _dt < 0.045f)
                {
                    // out of the ink: the tentacles start from rest and get flung up with the body
                    System.Array.Clear(hx, 0, hx.Length); System.Array.Clear(hv, 0, hv.Length); System.Array.Clear(tipX, 0, tipX.Length); System.Array.Clear(tipV, 0, tipV.Length);
                    _hairKick(0, 4.2f, 0.6f); sp[S_TANKL + 1] += 2.2f; sp[S_EARL + 1] += 5; sp[S_EARR + 1] += 5;
                }
                float fl = win(ft, 0.045f, 0.1f, 0.17f, 0.42f), fw = 1 - wTwo, na = 1 - wAim;
                P[UARML] = lerp(P[UARML], -1.75f, fl * fw); P[UARML + 2] = lerp(P[UARML + 2], 1.0f, fl * fw); P[FARML] = lerp(P[FARML], -0.35f, fl * fw);
                P[HANDPL] = lerp(P[HANDPL], 2, fl); P[EARS] += 1.0f * fl; P[SPINE] -= 0.08f * fl; P[CHEST] -= 0.05f * fl; P[HLP] -= 0.12f * fl;
                P[ANC + 1] += 0.04f * fl * na; P[ANCR] -= 0.3f * fl * na;
                P[MCURVE] += 0.5f * fl; P[MOPEN] = Mathf.Max(P[MOPEN], 0.35f * fl); P[BROWY] += 0.6f * fl;
            }
        }

        // Slosher heave (trigger 'slosh' at the start of the windup; the ink leaves at +0.13 s): dip the bucket back and
        // low, a big upward-forward heave that turns the open top to the target over the lip, follow-through, settle by
        // ~0.6 s. The support hand lets go of the carry bar for the throw and swings back for balance.
        void _poseSlosh(float[] P, float st)
        {
            var X = PX; System.Array.Copy(P, X, PN);
            float dx = kc(st, K_SL_T, K_SL_X), dy = kc(st, K_SL_T, K_SL_Y), dz = kc(st, K_SL_T, K_SL_Z);
            float rp = kc(st, K_SL_T, K_SL_P), ry = kc(st, K_SL_T, K_SL_W);
            X[ANC] += dx; X[ANC + 1] += dy; X[ANC + 2] += dz; X[ANCR] += rp; X[ANCR + 1] += ry;
            X[AFOLR] = Mathf.Max(X[AFOLR], 0.35f);
            float wu = win(st, 0, 0.1f, 0.12f, 0.2f);          // windup
            float hvv = win(st, 0.12f, 0.2f, 0.3f, 0.46f);     // heave + follow-through
            X[HIPS_P + 1] += -0.045f * wu + 0.022f * hvv; X[HIPS_P + 2] += -0.02f * wu + 0.025f * hvv;
            X[SPINE] += 0.14f * wu - 0.13f * hvv; X[CHEST] += 0.06f * wu - 0.1f * hvv; X[HIPS] += 0.06f * wu;
            X[SPINE + 1] += 0.12f * wu - 0.1f * hvv; X[CHEST + 1] += 0.22f * wu - 0.18f * hvv;
            X[HLP] += -0.08f * wu + 0.1f * hvv; X[TIPTOE] = Mathf.Max(X[TIPTOE], 0.85f * hvv);
            X[KNEEL] += 0.1f * wu; X[KNEER] -= 0.1f * wu;
            // support hand: off the carry bar, swung back for balance, back on by the settle
            float rel = win(st, 0.06f, 0.13f, 0.36f, 0.56f);
            X[IKL] *= 1 - rel;
            X[UARML] = lerp(X[UARML], 0.15f + 0.75f * hvv - 0.2f * wu, rel); X[UARML + 2] = lerp(X[UARML + 2], 0.25f + 0.2f * hvv, rel);
            X[FARML] = lerp(X[FARML], -0.5f - 0.3f * hvv, rel); X[HANDPL] = lerp(X[HANDPL], 1.6f, rel);
            X[SQY] *= 1 - 0.03f * wu + 0.045f * hvv; X[SQXZ] *= 1 + 0.015f * wu - 0.02f * hvv;
            X[EARS] += 0.5f * hvv - 0.3f * wu;
            _effort = Mathf.Max(_effort, 0.6f * wu + hvv);
            if (st >= 0.12f && st - _dt < 0.12f) { sp[S_TANKL + 1] += 2.2f; _hairKick(0, 2.4f, 1.6f); sp[S_PELY + 1] += 0.35f; }
            poseLerp(P, P, X, win(st, 0, 0.015f, 0.5f, 0.66f));
        }
        // Dualies dodge roll (trigger 'dodge'; root motion comes from the engine): squash → tucked roll over the shoulder
        // along the roll direction, pistols hugged to the chest → unfurl with the feet landing wide, straight into the lock
        // stance. The tumble itself is a kid-group rotation (tumble about tumbleX/Z) applied in _applyPose.
        void _poseDodge(float[] P, float tt)
        {
            var X = PX; System.Array.Copy(P, X, PN);
            float D = dodgeDur, u = clamp(tt / D, 0, 1);
            float tuck = win(u, 0, 0.1f, 0.62f, 0.96f), @out = sstep(0.6f, 1, u);
            X[WPL] = lerp(X[WPL], 0, tuck); X[WPR] = lerp(X[WPR], 0, tuck);
            lerpE(X, FOOTL, 0.1f, 0.38f, 0.12f, tuck); lerpE(X, FOOTLR, 1.15f, 0.25f, 0, tuck);
            lerpE(X, FOOTR, -0.1f, 0.35f, 0.08f, tuck); lerpE(X, FOOTRR, 1.25f, -0.25f, 0, tuck);
            lerpE(X, FOOTL, STANCE_LOCK[0], ANKLE_H, STANCE_LOCK[1], @out * (1 - tuck)); lerpE(X, FOOTR, STANCE_LOCK[3], ANKLE_H, STANCE_LOCK[4], @out * (1 - tuck));
            X[FOOTLR + 1] = lerp(X[FOOTLR + 1], STANCE_LOCK[2], @out); X[FOOTRR + 1] = lerp(X[FOOTRR + 1], STANCE_LOCK[5], @out);
            X[KNEEL] += 0.25f * tuck; X[KNEER] -= 0.25f * tuck;
            X[HIPS_P + 1] -= 0.05f * tuck + 0.05f * @out * (1 - tuck);
            X[SPINE] += 0.6f * tuck; X[CHEST] += 0.35f * tuck; X[NECK] += 0.25f * tuck; X[HLP] += 0.4f * tuck; X[STAB] *= 1 - 0.85f * tuck;
            // pistols hugged to the chest through the roll (both fists), then snapped forward into the lock
            lerpE(X, ANC, -0.09f, 0.93f, 0.17f, tuck); lerpE(X, ANCR, -0.5f, 0.35f, 0.4f, tuck);
            lerpE(X, ANL, 0.09f, 0.93f, 0.17f, tuck); lerpE(X, ANLR, -0.5f, -0.35f, -0.4f, tuck);
            X[AFOLT] = lerp(X[AFOLT], 1, tuck); X[AFOLR] = lerp(X[AFOLR], 1, tuck);
            lerpE(X, POLER, -0.9f, -0.2f, -0.3f, tuck); lerpE(X, POLEL, 0.9f, -0.2f, -0.3f, tuck);
            X[SQY] *= 1 - 0.07f * tuck + 0.05f * @out * (1 - @out); X[SQXZ] *= 1 + 0.035f * tuck;
            X[EARS] -= 0.7f * tuck; X[SQUINT] += 0.7f * tuck; X[MCURVE] -= 0.5f * tuck; X[BROW] -= 0.3f * tuck;
            _effort = Mathf.Max(_effort, tuck);
            // one full turn about (up × dir): fast out of the push-off, easing into the landing
            tumble = u < 1 ? TAU * (1 - Mathf.Pow(1 - u, 2.4f)) : 0;
            tumbleX = dodgeZ; tumbleZ = -dodgeX;
            tumbleDrop = 0.26f * tuck;
            poseLerp(P, P, X, win(tt, 0, 0.015f, D, D + 0.28f));
        }

        // Bomb / storm throw with the free (left) arm: cocked → whip → follow-through (the projectile leaves at t≈0).
        void _poseThrow(float[] P, float tt)
        {
            var X = PX; System.Array.Copy(P, X, PN);
            float w = win(tt, 0, 0.03f, 0.4f, 0.62f);
            float cock = 1 - ease(tt / 0.06f), whip = ease(tt / 0.1f) * (1 - ease((tt - 0.1f) / 0.16f)), fol = ease((tt - 0.08f) / 0.14f);
            X[IKL] = 0; X[LTW] = 0;
            X[UARML] = -2.6f * cock + -1.35f * whip + -0.55f * fol * (1 - whip); X[UARML + 2] = 0.45f * cock + 0.1f * whip - 0.1f * fol;
            X[FARML] = -1.9f * cock - 0.3f * whip - 0.4f * fol; X[HANDL] = 0.4f * whip;
            X[CLAVL + 2] += 0.16f * cock; X[CLAVL + 1] += -0.1f * cock + 0.12f * whip;
            X[CHEST + 1] += 0.4f * cock - 0.42f * whip - 0.18f * fol; X[SPINE + 1] += 0.16f * cock - 0.18f * whip;
            X[SPINE] += -0.1f * cock + 0.22f * whip + 0.08f * fol; X[CHEST] += 0.1f * whip;
            X[HIPS_P + 2] += 0.03f * whip; X[HIPS_P + 1] -= 0.025f * whip;
            X[HANDPL] = lerp(1.95f, 1.05f, ease((tt - 0.12f) / 0.35f));
            _effort = Mathf.Max(_effort, whip);
            poseLerp(P, P, X, w);
        }

        // Holding the splat bomb up behind the head (free arm cocked, chest turned), ready to throw.
        void _poseSubAim(float[] P, float w)
        {
            var X = PX; System.Array.Copy(P, X, PN);
            X[IKL] = 0; X[LTW] = 0;
            X[UARML] = -2.55f; X[UARML + 2] = 0.48f; X[FARML] = -1.85f; X[HANDL] = 0.2f;
            X[CLAVL + 2] += 0.15f; X[CLAVL + 1] -= 0.08f;
            X[CHEST + 1] += 0.34f; X[SPINE + 1] += 0.12f; X[SPINE] -= 0.06f;
            X[HLY] -= 0.25f; X[HANDPL] = 0;
            _effort = Mathf.Max(_effort, 0.35f * w);
            poseLerp(P, P, X, w);
        }

        // Respawn drop: superhero dive → three-point landing → pop up.
        void _poseSpawn(float[] P, float st)
        {
            var X = PX; System.Array.Copy(P, X, PN);
            bool landed = tr[T_LAND] < st; // landed during this spawn
            float lt = landed ? tr[T_LAND] : -1;
            if (!landed)
            {
                // falling: legs together pointed down, arms up/out, looking down at the pad
                float k = ease(st / 0.12f);
                setE(X, FOOTL, 0.07f, 0.07f, -0.02f); setE(X, FOOTLR, 0.9f, 0.05f, 0); setE(X, FOOTR, -0.07f, 0.09f, -0.06f); setE(X, FOOTRR, 1.0f, -0.05f, 0);
                X[WPL] = 0; X[WPR] = 0;
                X[UARML] = -2.3f; X[UARML + 2] = 0.55f; X[FARML] = -0.2f;
                X[SPINE] -= 0.08f; X[HLP] -= 0.2f; X[HANDPL] = 1.7f; X[EARS] += 0.8f;
                X[SQY] *= 1.1f; X[SQXZ] *= 0.95f;
                poseLerp(P, P, X, k);
                return;
            }
            // landed: three-point crouch (left hand to the ground), hold, then pop up with a flourish
            float crouch = win(lt, 0, 0.05f, 0.3f, 0.55f);
            float pop = win(lt, 0.3f, 0.42f, 0.5f, 0.8f);
            X[HIPS_P + 1] -= 0.24f * crouch; X[HIPS_P + 2] -= 0.02f * crouch;
            X[SPINE] += 0.42f * crouch; X[CHEST] += 0.18f * crouch; X[HLP] += 0.25f * crouch; X[NECK] -= 0.2f * crouch;
            X[LTW] = crouch; setE(X, LTGT, 0.2f, 0.08f, 0.2f); X[IKL] *= 1 - crouch;
            lerpE(X, POLEL, 1, 0.2f, 0, crouch);
            X[KNEEL] += 0.25f * crouch; X[KNEER] -= 0.25f * crouch;
            X[UARMR] -= 0.4f * crouch;
            // pop: stretch up, arms flung out, then settle
            X[SQY] *= 1 + 0.08f * pop; X[SQXZ] *= 1 - 0.04f * pop;
            X[UARML] = lerp(X[UARML], -1.2f, pop); X[UARML + 2] = lerp(X[UARML + 2], 1.2f, pop); X[FARML] = lerp(X[FARML], -0.3f, pop);
            X[SPINE] -= 0.08f * pop; X[HLP] += 0.08f * pop;
            X[HANDPL] = lerp(X[HANDPL], 2, crouch); X[HANDPL] = lerp(X[HANDPL], 1.8f, pop); X[EARS] += 0.9f * pop - 0.4f * crouch;
            float w = 1 - ease((lt - 0.75f) / 0.3f);
            poseLerp(P, P, X, w);
        }

        // Tidal Slam: crouch → launch stretch → forward somersault → overhead hang (engine: rise 0.55 s, hang 0.25 s).
        void _poseLeap(float[] P, float lt)
        {
            var X = PX; System.Array.Copy(P, X, PN);
            float launch = win(lt, 0, 0.04f, 0.12f, 0.24f);
            float tuck = win(lt, 0.12f, 0.26f, 0.42f, 0.56f);
            float hang = win(lt, 0.44f, 0.58f, 1.6f, 1.9f);
            X[WPL] = 0; X[WPR] = 0;
            // legs
            lerpE(X, FOOTL, 0.08f, 0.08f, -0.05f, launch); lerpE(X, FOOTLR, 1.0f, 0.1f, 0, launch);
            lerpE(X, FOOTR, -0.08f, 0.1f, -0.1f, launch); lerpE(X, FOOTRR, 1.1f, -0.1f, 0, launch);
            lerpE(X, FOOTL, 0.1f, 0.4f, 0.14f, tuck); lerpE(X, FOOTLR, 0.7f, 0.1f, 0, tuck);
            lerpE(X, FOOTR, -0.1f, 0.36f, 0.1f, tuck); lerpE(X, FOOTRR, 0.8f, -0.1f, 0, tuck);
            lerpE(X, FOOTL, 0.12f, 0.3f, 0.1f, hang); lerpE(X, FOOTLR, 0.4f, 0.15f, 0, hang);
            lerpE(X, FOOTR, -0.12f, 0.2f, -0.12f, hang); lerpE(X, FOOTRR, 0.9f, -0.15f, 0, hang);
            X[HIPS_P + 1] = -0.03f * tuck;
            // body
            X[SPINE] += -0.14f * launch + 0.5f * tuck - 0.22f * hang; X[CHEST] += -0.08f * launch + 0.25f * tuck - 0.16f * hang;
            X[HLP] += 0.2f * launch - 0.1f * tuck - 0.35f * hang; X[NECK] += 0.12f * hang;
            X[SQY] *= 1 + 0.12f * launch; X[SQXZ] *= 1 - 0.06f * launch;
            // one forward somersault through the tuck
            float flip = ease((lt - 0.14f) / 0.36f);
            X[MODELR] += lt > 0.14f ? wrapA(flip * TAU) : 0;
            // weapon: thrust up on launch, tucked to the chest, raised overhead two-handed for the hang
            float up = Mathf.Max(launch, hang);
            lerpE(X, ANC, -0.06f, 1.34f, 0.06f, up); lerpE(X, ANCR, -1.85f, 0.05f, 0, up);
            lerpE(X, ANC, -0.1f, 0.86f, 0.2f, tuck); lerpE(X, ANCR, -0.6f, 0.1f, 0, tuck);
            X[AFOLT] = 1; X[AFOLR] = 0.5f + 0.5f * tuck;
            X[IKL] = lerp(X[IKL], 1, Mathf.Max(hang, tuck)); X[IKL] = lerp(X[IKL], 0, launch);
            X[UARML] = lerp(X[UARML], -2.7f, launch); X[UARML + 2] = lerp(X[UARML + 2], 0.25f, launch);
            lerpE(X, POLER, -0.9f, 0.3f, -0.2f, up); lerpE(X, POLEL, 0.9f, 0.3f, -0.2f, up);
            X[STAB] = 0.3f; X[HANDPL] = lerp(X[HANDPL], -1, launch); X[EARS] += 0.8f * (launch + hang);
            _effort = Mathf.Max(_effort, launch + hang);
            float w = win(lt, 0, 0.04f, 1.55f, 1.9f);
            if (leapEnd >= 0) w *= 1 - ease((lt - leapEnd) / 0.1f); // the slam takes over smoothly
            poseLerp(P, P, X, w);
        }

        // Slam: whip the weapon down while diving, then a crouched impact with the weapon smashed into the ground.
        void _poseSlam(float[] P, float st)
        {
            var X = PX; System.Array.Copy(P, X, PN);
            if (!slamGround && grounded && st > 0.02f) { slamGround = true; tr[T_IMPACT] = 0; sp[S_SQ + 1] -= 3.2f; sp[S_PELY + 1] -= 0.8f; _hairKick(0, -3, 0); }
            float it = slamGround ? tr[T_IMPACT] : -1;
            float dive = slamGround ? 0 : ease(st / 0.07f);
            float imp = it >= 0 ? win(it, 0, 0.02f, 0.35f, 0.8f) : 0;
            X[WPL] = 0; X[WPR] = 0;
            // diving: legs extended down, weapon whipped from overhead to the front
            lerpE(X, FOOTL, 0.1f, 0.1f, 0.06f, dive); lerpE(X, FOOTLR, 0.5f, 0.1f, 0, dive);
            lerpE(X, FOOTR, -0.1f, 0.12f, -0.08f, dive); lerpE(X, FOOTRR, 0.7f, -0.1f, 0, dive);
            lerpE(X, ANC, -0.06f, 1.3f, 0.12f, dive * (1 - ease(st / 0.12f))); lerpE(X, ANCR, -1.9f, 0.05f, 0, dive * (1 - ease(st / 0.12f)));
            lerpE(X, ANC, -0.05f, 0.62f, 0.36f, ease((st - 0.04f) / 0.1f) * (1 - imp)); lerpE(X, ANCR, 0.9f, 0.05f, 0, ease((st - 0.04f) / 0.1f) * (1 - imp));
            X[SPINE] += 0.3f * dive; X[CHEST] += 0.15f * dive;
            // impact: wide low crouch, weapon planted in front, head down
            lerpE(X, FOOTL, 0.2f, ANKLE_H, 0.06f, imp); lerpE(X, FOOTLR, 0, 0.35f, 0, imp);
            lerpE(X, FOOTR, -0.2f, ANKLE_H, -0.1f, imp); lerpE(X, FOOTRR, 0, -0.5f, 0, imp);
            X[HIPS_P + 1] -= 0.25f * imp; X[SPINE] += 0.45f * imp; X[CHEST] += 0.25f * imp; X[HLP] += 0.2f * imp;
            lerpE(X, ANC, -0.05f, animKind == "roller" ? 0.5f : 0.36f, 0.4f, imp); lerpE(X, ANCR, animKind == "roller" ? 1.2f : 1.35f, 0.05f, 0, imp);
            X[KNEEL] += 0.3f * imp; X[KNEER] -= 0.3f * imp;
            X[IKL] = 1; X[AFOLT] = 1; X[AFOLR] = 0.6f; X[EARS] -= 0.6f * dive; X[HANDPL] = -1;
            lerpE(X, POLER, -0.9f, -0.2f, -0.3f, imp); lerpE(X, POLEL, 0.9f, -0.2f, -0.3f, imp);
            _effort = Mathf.Max(_effort, dive + imp);
            float w = win(st, 0, 0.03f, 1.0f, 1.4f);
            poseLerp(P, P, X, w);
        }

        // ---- Sponge Mitts (the mechanics live in kits/mitts.js; this is only its body motion) --------------------------
        // Reads the runner's kit state (charging / charge, leaping, landT, cling + clingN / clingPt in world space).
        //  · punches: each glove thrusts out along the aim and snaps back (right = tr[T_SHOOT], left = tr[T_SHOOTL])
        //  · leap charge: a coiled crouch, gloves drawn back low by the hips, trembling at full charge
        //  · leap: pitched into the flight with both gloves thrown forward on the way up, raised overhead on the way down;
        //    landing: a crouched double-glove smash into the ground
        //  · wall cling: the left glove stuck flat on the wall beside the shoulder, feet against the wall, knees up
        float mitCh, mitLp, mitCl;
        readonly float[] _mitO = new float[2];
        void _poseMitts(float[] P, float dt, AnimState s)
        {
            var R = _runner(); var K = R?.kit as KitState; if (K != null && !K.mitts) K = null;
            bool kd = kidForm && dance == null;
            // ---- punches
            float eR = kd ? mitPunch(tr[T_SHOOT]) : 0, eL = kd ? mitPunch(tr[T_SHOOTL]) : 0;
            if (eR + eL > 0.001f)
            {
                float ap = clamp(aimP, -0.8f, 1.0f), fy = Mathf.Sin(ap) * 0.24f, fz = Mathf.Cos(ap) * 0.24f;
                P[ANC] += 0.07f * eR; P[ANC + 1] += fy * eR; P[ANC + 2] += fz * eR;
                P[ANL] -= 0.07f * eL; P[ANL + 1] += fy * eL; P[ANL + 2] += fz * eL;
                P[ANCR + 1] += 0.22f * eR; P[ANCR + 2] -= 0.45f * eR;
                P[ANLR + 1] -= 0.22f * eL; P[ANLR + 2] += 0.45f * eL;
                float tw = eR - eL;
                P[CHEST + 1] += 0.3f * tw; P[SPINE + 1] += 0.12f * tw; P[HIPS + 1] += 0.05f * tw;
                P[CLAVR + 1] += 0.22f * eR; P[CLAVL + 1] -= 0.22f * eL;
                P[SPINE] += 0.035f * (eR + eL);
                _effort = Mathf.Max(_effort, 0.45f * Mathf.Max(eR, eL));
            }
            // ---- leap charge: coiled crouch
            bool chOn = kd && K != null && K.charging;
            float chW = mitCh = damp(mitCh, chOn ? 1 : 0, chOn ? 14 : 9, dt);
            if (chW > 0.001f)
            {
                float c = chOn ? K.charge : 0;
                float tb = c >= 0.999f ? 0.004f * Mathf.Sin(t * 71) + 0.003f * Mathf.Sin(t * 53 + 1) : 0.0015f * Mathf.Sin(t * 40) * c;
                P[HIPS_P + 1] -= (0.13f + 0.07f * c) * chW; P[HIPS_P + 2] -= 0.04f * chW;
                P[HIPS] += 0.3f * chW; P[SPINE] += 0.28f * chW; P[CHEST] += 0.1f * chW; P[NECK] -= 0.2f * chW; P[HLP] -= 0.1f * chW;
                P[KNEEL] += 0.24f * chW; P[KNEER] -= 0.24f * chW;
                lerpE(P, ANC, -0.2f, 0.56f + tb, -0.08f, chW); lerpE(P, ANCR, 1.5f, 0.35f, 0.3f, chW);
                lerpE(P, ANL, 0.2f, 0.56f - tb, -0.08f, chW); lerpE(P, ANLR, 1.5f, -0.35f, -0.3f, chW);
                lerpE(P, POLER, -0.9f, 0.2f, -0.7f, chW); lerpE(P, POLEL, 0.9f, 0.2f, -0.7f, chW);
                P[SQY] *= 1 - (0.03f + 0.05f * c) * chW; P[SQXZ] *= 1 + 0.03f * chW;
                P[EARS] -= 0.6f * chW;
                addExpr(P, X_EFFORT, (0.3f + 0.6f * c) * chW);
                _effort = Mathf.Max(_effort, (0.4f + 0.5f * c) * chW);
            }
            // ---- leap: superman on the way up, gloves raised to hammer down on the way down
            bool lpOn = kd && K != null && K.leaping;
            float lpW = mitLp = damp(mitLp, lpOn ? 1 : 0, lpOn ? 18 : 8, dt);
            if (lpW > 0.001f)
            {
                float rise = sstep(-3, 4, vyS), wr = rise * lpW, wf = (1 - rise) * lpW;
                P[MODELR] += 0.5f * wr + 0.12f * wf;
                P[SQY] *= 1 + 0.07f * wr; P[SQXZ] *= 1 - 0.035f * wr;
                lerpE(P, ANC, -0.09f, 1.26f, 0.32f, wr); lerpE(P, ANCR, -0.95f, 0.12f, -0.15f, wr);
                lerpE(P, ANL, 0.09f, 1.26f, 0.32f, wr); lerpE(P, ANLR, -0.95f, -0.12f, 0.15f, wr);
                lerpE(P, ANC, -0.11f, 1.36f, 0.1f, wf); lerpE(P, ANCR, -1.7f, 0.2f, 0, wf);
                lerpE(P, ANL, 0.11f, 1.36f, 0.1f, wf); lerpE(P, ANLR, -1.7f, -0.2f, 0, wf);
                P[AFOLT] = lerp(P[AFOLT], 1, lpW); P[AFOLR] = lerp(P[AFOLR], 0.7f, lpW);
                lerpE(P, POLER, -0.8f, 0.3f, -0.3f, lpW); lerpE(P, POLEL, 0.8f, 0.3f, -0.3f, lpW);
                lerpE(P, FOOTL, 0.09f, 0.2f, -0.26f, wr); lerpE(P, FOOTLR, 1.2f, 0.1f, 0, wr);
                lerpE(P, FOOTR, -0.09f, 0.26f, -0.2f, wr); lerpE(P, FOOTRR, 1.0f, -0.1f, 0, wr);
                lerpE(P, FOOTL, 0.11f, 0.34f, 0.1f, wf); lerpE(P, FOOTLR, 0.5f, 0.12f, 0, wf);
                lerpE(P, FOOTR, -0.11f, 0.3f, 0.04f, wf); lerpE(P, FOOTRR, 0.6f, -0.12f, 0, wf);
                P[WPL] *= 1 - lpW; P[WPR] *= 1 - lpW;
                P[EARS] += 0.9f * lpW;
                addExpr(P, X_DETERM, 0.8f * lpW); P[MOPEN] = Mathf.Max(P[MOPEN], 0.3f * wf);
                _effort = Mathf.Max(_effort, 0.6f * lpW);
            }
            // ---- landing: crouched double-glove smash
            float smW = K != null && kd ? win(K.landT, 0, 0.03f, 0.16f, 0.5f) : 0;
            if (smW > 0.001f)
            {
                P[HIPS_P + 1] -= 0.2f * smW; P[HIPS_P + 2] -= 0.02f * smW;
                P[HIPS] += 0.2f * smW; P[SPINE] += 0.4f * smW; P[CHEST] += 0.2f * smW; P[HLP] += 0.2f * smW;
                P[KNEEL] += 0.3f * smW; P[KNEER] -= 0.3f * smW;
                lerpE(P, ANC, -0.13f, 0.3f, 0.34f, smW); lerpE(P, ANCR, 1.35f, 0.2f, 0, smW);
                lerpE(P, ANL, 0.13f, 0.3f, 0.34f, smW); lerpE(P, ANLR, 1.35f, -0.2f, 0, smW);
                P[AFOLT] = lerp(P[AFOLT], 1, smW); P[AFOLR] = lerp(P[AFOLR], 0.6f, smW);
                lerpE(P, POLER, -0.9f, -0.2f, -0.3f, smW); lerpE(P, POLEL, 0.9f, -0.2f, -0.3f, smW);
                P[EARS] -= 0.6f * smW; addExpr(P, X_EFFORT, 0.7f * smW);
                _effort = Mathf.Max(_effort, smW);
            }
            // ---- wall cling (wall plane + contact point taken into kid space; the body hugs 10 cm in toward the wall)
            bool clOn = kd && K != null && K.cling;
            float clW = mitCl = damp(mitCl, clOn ? 1 : 0, clOn ? 20 : 9, dt);
            if (clW > 0.001f && K != null && K.hasCling)
            {
                float c = Mathf.Cos(yaw), sn = Mathf.Sin(yaw); Vector3 rp0 = root.localPosition, n = K.clingN;
                float nx = n.x * c - n.z * sn, nz = n.x * sn + n.z * c, hug = 0.1f * clW;
                P[MODEL] -= nx * hug; P[MODEL + 2] -= nz * hug;
                float px = K.clingPt.x - rp0.x, pz = K.clingPt.z - rp0.z;
                float wd = (px * c - pz * sn + nx * hug) * nx + (px * sn + pz * c + nz * hug) * nz;   // wall plane: p·n = wd
                var o = _mitO;
                mitOnWall(0.21f, 0.02f, 0.085f, nx, nz, wd, o);
                lerpE(P, ANL, o[0], 1.12f, o[1], clW); lerpE(P, ANLR, -0.25f, Mathf.Atan2(-nx, -nz), 0, clW);
                P[AFOLT] *= 1 - clW; P[AFOLR] *= 1 - clW;
                lerpE(P, POLEL, 0.9f, 0.2f, -0.2f, clW);
                mitOnWall(0.1f, 0.02f, 0.11f, nx, nz, wd, o); lerpE(P, FOOTL, o[0], 0.36f, o[1], clW); lerpE(P, FOOTLR, 0.35f, 0.15f, 0, clW);
                mitOnWall(-0.1f, 0, 0.11f, nx, nz, wd, o); lerpE(P, FOOTR, o[0], 0.27f, o[1], clW); lerpE(P, FOOTRR, 0.45f, -0.15f, 0, clW);
                P[WPL] *= 1 - clW; P[WPR] *= 1 - clW;
                P[KNEEL] += 0.12f * clW; P[KNEER] -= 0.12f * clW;
                P[HIPS_P + 1] -= 0.02f * clW; P[SPINE] += 0.06f * clW;
                P[EARS] += 0.3f * clW;
                addExpr(P, X_DETERM, 0.5f * clW);
            }
        }

        // Idle fidgets (hand-keyed-style one-offs while standing around).
        void _poseFidget(float[] P, int id, float ft)
        {
            var X = PX; System.Array.Copy(P, X, PN);
            float L = FIDGET_LEN[id];
            float w = win(ft, 0, 0.22f, L - 0.28f, L);
            string kind = animKind;
            switch (FIDGETS[id])
            {
                case "goggles": // push the goggles up the nose, adjust twice
                {
                    float a = kf(ft, K_GOG_T, K_GOG_V);
                    float jig = Mathf.Sin(Mathf.Max(0, ft - 0.4f) * 22) * win(ft, 0.4f, 0.5f, 0.85f, 0.95f);
                    X[LTW] = a; setE(X, LTGT, 0.075f, 1.18f + 0.008f * jig, 0.2f); X[IKL] *= 1 - a;
                    lerpE(X, POLEL, 0.9f, -0.6f, -0.3f, a);
                    X[HLP] += 0.08f * a; X[HEAD + 2] += 0.08f * a; X[SQUINT] += 0.5f * a; X[MCURVE] -= 0.3f * a;
                    X[HANDPL] = lerp(X[HANDPL], 0.3f, a);
                    break;
                }
                case "twirl":
                {
                    if (kind == "shooter" || kind == "blaster" || kind == "dualies")
                    {
                        float k = ease((ft - 0.2f) / 0.6f), lift = win(ft, 0.1f, 0.3f, 0.75f, 1.0f);
                        X[SPIN] = wrapA(TAU * 2 * k); if (!dual) X[IKL] = 0;
                        X[ANC + 1] += 0.08f * lift; X[ANC + 2] += 0.08f * lift; X[ANC] -= 0.03f * lift; X[ANCR] -= 0.7f * lift;
                        X[HLY] -= 0.15f * lift; X[HLP] -= 0.1f * lift; X[MCURVE] += 0.3f * lift; X[MTILT] += 0.15f * lift;
                    }
                    else if (kind == "slosher")
                    { // lift the bucket, peer in, give it a swirl
                        float k = win(ft, 0.1f, 0.35f, 0.8f, 1.15f), sw = Mathf.Sin((ft - 0.35f) * 14) * win(ft, 0.35f, 0.45f, 0.7f, 0.8f);
                        lerpE(X, ANC, -0.1f, 0.8f, 0.24f, k); lerpE(X, ANCR, 0.25f, 0.25f + 0.25f * sw, 0.12f * sw, k);
                        X[HLP] += 0.28f * k; X[HLY] -= 0.1f * k; X[SPINE] += 0.05f * k; X[LOOKY] -= 0.2f * k; X[MCURVE] += 0.25f * k; X[BROWY] += 0.4f * k;
                    }
                    else if (kind == "splatling")
                    { // heft it up, bounce to re-seat the grip
                        float k = win(ft, 0.1f, 0.3f, 0.75f, 1.1f), b = Mathf.Max(0, Mathf.Sin((ft - 0.3f) * 17)) * win(ft, 0.3f, 0.35f, 0.6f, 0.7f);
                        X[ANC + 1] += 0.07f * k + 0.02f * b; X[ANCR] -= 0.35f * k; X[HIPS_P + 1] -= 0.02f * b; X[SPINE] -= 0.06f * k;
                        X[HLP] -= 0.08f * k; X[MCURVE] += 0.3f * k; X[MTILT] -= 0.15f * k;
                    }
                    else if (kind == "charger")
                    { // raise and peek through the scope
                        float k = win(ft, 0.1f, 0.4f, 0.85f, 1.15f);
                        lerpE(X, ANC, -0.05f, 1.1f, 0.1f, k); lerpE(X, ANCR, -0.05f, 0.06f, 0, k); X[IKL] = 1;
                        X[HEAD + 2] -= 0.15f * k; X[SQUINT] += 0.6f * k; X[HLY] -= 0.08f * k;
                    }
                    else
                    { // roller: hitch the handle up onto the shoulder and back down
                        float k = win(ft, 0.1f, 0.35f, 0.8f, 1.15f);
                        lerpE(X, ANC, -0.14f, 1.06f, 0.02f, k); lerpE(X, ANCR, -2.3f, 0.3f, 0.15f, k); X[IKL] = lerp(X[IKL], 0, k);
                        X[HIPS_P + 1] -= 0.03f * win(ft, 0.3f, 0.4f, 0.45f, 0.6f);
                    }
                    break;
                }
                case "look": // glance over each shoulder
                {
                    float y = kc(ft, K_LOOK_T, K_LOOK_V);
                    X[HLY] += y; X[SPINE + 1] += y * 0.18f; X[CHEST + 1] += y * 0.22f; X[LOOKX] += y * 0.25f;
                    X[HLP] += 0.06f * Mathf.Abs(y);
                    break;
                }
                case "stretch": // arms up, lean back, yawn
                {
                    float k = win(ft, 0.1f, 0.55f, 1.2f, 1.7f);
                    X[UARML] = lerp(X[UARML], -2.85f, k); X[UARML + 2] = lerp(X[UARML + 2], 0.25f, k); X[FARML] = lerp(X[FARML], -0.15f, k); X[IKL] *= 1 - k;
                    lerpE(X, ANC, -0.12f, 1.25f, -0.02f, k * 0.8f); lerpE(X, ANCR, -2.6f, 0.2f, 0.2f, k * 0.8f);
                    X[SPINE] -= 0.14f * k; X[CHEST] -= 0.12f * k; X[HLP] += 0.22f * k; X[HIPS_P + 2] += 0.02f * k;
                    X[MOPEN] = Mathf.Max(X[MOPEN], 0.85f * win(ft, 0.35f, 0.6f, 1.05f, 1.3f)); X[MCURVE] -= 0.7f * k; X[EYE] -= 0.85f * k; X[BROW] += 0.3f * k;
                    X[SQY] *= 1 + 0.02f * k; X[HANDPL] = lerp(X[HANDPL], 2, k); X[EARS] -= 0.5f * k;
                    break;
                }
                case "tank": // reach back and tap the ink tank twice
                {
                    float a = kf(ft, K_TANK_T, K_TANK_V);
                    float tap = Mathf.Max(0, Mathf.Sin((ft - 0.35f) * 18)) * win(ft, 0.35f, 0.4f, 0.8f, 0.9f);
                    X[LTW] = a; setE(X, LTGT, 0.165f, 0.83f + 0.012f * tap, -0.125f - 0.012f * tap); X[IKL] *= 1 - a;
                    lerpE(X, POLEL, 0.7f, -0.5f, -0.5f, a);
                    X[CHEST + 1] += 0.2f * a; X[HLY] += 0.35f * a; X[HLP] -= 0.1f * a; X[LOOKX] += 0.3f * a; X[HANDPL] = lerp(X[HANDPL], 1.6f, a);
                    if (tap > 0.9f && !_tapped) { _tapped = true; sp[S_TANKX + 1] += 0.8f; sp[S_TANKL + 1] += 1.2f; } else if (tap < 0.2f) _tapped = false;
                    break;
                }
                case "bounce": // bounce on the toes
                {
                    float b = Mathf.Max(0, Mathf.Sin((ft - 0.15f) * TAU * 2.6f)) * win(ft, 0.1f, 0.2f, 1.0f, 1.2f);
                    X[HIPS_P + 1] += 0.035f * b - 0.02f * win(ft, 0.1f, 0.2f, 1.0f, 1.2f); _toeUp = b;
                    X[UARML + 2] += 0.12f * b; X[CLAVL + 2] += 0.05f * b; X[CLAVR + 2] -= 0.05f * b;
                    X[MCURVE] += 0.2f;
                    break;
                }
                case "shake": // shake the ink off
                {
                    float k = win(ft, 0.05f, 0.15f, 0.7f, 0.95f);
                    float s1 = Mathf.Sin(ft * 42) * k;
                    X[HIPS + 2] += 0.06f * s1; X[CHEST + 2] -= 0.1f * s1; X[HEAD + 2] += 0.14f * s1; X[HLY] += 0.2f * Mathf.Sin(ft * 38 + 1) * k;
                    X[UARML + 2] += 0.15f * k + 0.1f * s1; X[SQUINT] += 0.8f * k; X[MCURVE] -= 0.3f * k; X[HANDPL] = lerp(X[HANDPL], 1.85f, k); X[EARS] += 0.6f * s1;
                    if (k > 0.3f && Mathf.Abs(s1) > 0.95f) _hairKick(s1 * 0.8f, 0.3f, 0);
                    break;
                }
            }
            poseLerp(P, P, X, w);
        }
    }
}
