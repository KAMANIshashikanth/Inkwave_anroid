// Port of src/game/character.js — the squidkid: skinned procedural model + fully procedural, layered animation.
// API: docs/CONTRACTS.md §1 (ICharacter). The engine owns root.localPosition / root.localRotation (yaw); everything
// here animates children. update() is allocation-free (8 characters every frame).
//
// Animation architecture (all procedural, layered, spring-smoothed — nothing is a canned clip):
//   root tracking → world velocity / acceleration / turn rate measured from the root's own motion
//   stepping      → world-locked foot plants (zero sliding by construction), a phase-driven gait whose cadence, duty
//                   factor and lift follow speed, predictive landing targets, settle steps, catch-up steps, ground rays
//   pose layers   → idle · locomotion · lean springs · air · weapon holds + aim + recoil springs · one-shots · face ·
//                   dances
//   application   → kid squash/stretch → pelvis reach solve → torso FK → stabilised head look → two-bone IK legs/arms
//                   → face → hair spring chains → tank slosh → weapon extras
//
// Split over partial files like the JS sections: Character.cs (state, rig, LOD, API, update, root, states, forms,
// stepping), Character.Pose.cs (pose layers), Character.Life.cs (look, face, blinks, dances), Character.Apply.cs
// (application, IK, hair, squid, materials). The pose-buffer / spring / timer index tables live here only: C# runs
// static initialisers in textual order within one file, which is what the JS allocation counters rely on.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using static Inkwave.Ctx;
using static Inkwave.Config;

namespace Inkwave
{
    public partial class Character : ICharacter
    {
        // ---------------------------------------------------------------------------------------------------------
        // Math helpers (allocation-free)
        // ---------------------------------------------------------------------------------------------------------
        const float TAU = Mathf.PI * 2;
        static float sstep(float a, float b, float x) { float t = clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }
        static float ease(float t) { t = clamp(t, 0, 1); return t * t * (3 - 2 * t); }
        static float easeOut(float t) { t = clamp(t, 0, 1); return 1 - (1 - t) * (1 - t); }
        static float easeIn(float t) { t = clamp(t, 0, 1); return t * t; }
        static float mj(float t) { t = clamp(t, 0, 1); return t * t * t * (10 + t * (6 * t - 15)); }   // minimum-jerk
        static float backOut(float t, float s = 2.2f) { t = clamp(t, 0, 1) - 1; return t * t * ((s + 1) * t + s) + 1; }
        static float frac(float x) => x - Mathf.Floor(x);
        static uint hashStr(string s) { uint h = 2166136261; for (int i = 0; i < s.Length; i++) { h ^= s[i]; h = unchecked(h * 16777619); } return h; }
        static Func<float> mulberry(uint seed)
        {
            uint a = seed;
            return () =>
            {
                a = unchecked(a + 0x6d2b79f5);
                uint t = unchecked((a ^ (a >> 15)) * (1 | a));
                t = unchecked((t + ((t ^ (t >> 7)) * (61 | t))) ^ t);
                return ((t ^ (t >> 14)) >> 0) / 4294967296f;
            };
        }
        // impulse envelope: quick attack, exponential decay
        static float pulse(float t, float atk, float dec) => t < 0 ? 0 : t < atk ? t / atk : Mathf.Exp(-(t - atk) * dec);
        // window: 0 outside [a,d], eases up a..b, holds, eases down c..d
        static float win(float t, float a, float b, float c, float d) => t <= a || t >= d ? 0 : t < b ? ease((t - a) / (b - a)) : t > c ? 1 - ease((t - c) / (d - c)) : 1;
        /** hand-shape keys: h = -1 fist, 0 grip, 1 relaxed, 2 open */
        static float hk(float h, float fist, float grip, float relaxed, float open) => h < 0 ? grip + (fist - grip) * Mathf.Min(1, -h) : h < 1 ? grip + (relaxed - grip) * h : relaxed + (open - relaxed) * Mathf.Min(1, h - 1);
        static float wrapA(float a) { while (a > Mathf.PI) a -= TAU; while (a < -Mathf.PI) a += TAU; return a; }
        static float dampAngleC(float a, float b, float l, float dt) => a + wrapA(b - a) * (1 - Mathf.Exp(-l * dt));
        /** Keyed curve with eased (held) keys: T ascending times, V values. */
        static float kf(float t, float[] T, float[] V)
        {
            int n = T.Length; if (t <= T[0]) return V[0]; if (t >= T[n - 1]) return V[n - 1];
            int i = 1; while (T[i] < t) i++;
            float u = (t - T[i - 1]) / (T[i] - T[i - 1]);
            return V[i - 1] + (V[i] - V[i - 1]) * (u * u * (3 - 2 * u));
        }
        /** Keyed curve through keys with Catmull-Rom tangents (fluid, no stops at keys; flat at the ends). */
        static float kc(float t, float[] T, float[] V)
        {
            int n = T.Length; if (t <= T[0]) return V[0]; if (t >= T[n - 1]) return V[n - 1];
            int i = 1; while (T[i] < t) i++;
            float t0 = T[i - 1], h = T[i] - t0, u = (t - t0) / h;
            float m0 = i > 1 ? (V[i] - V[i - 2]) / (T[i] - T[i - 2]) : 0;
            float m1 = i < n - 1 ? (V[i + 1] - V[i - 1]) / (T[i + 1] - T[i - 1]) : 0;
            float u2 = u * u, u3 = u2 * u;
            return (2 * u3 - 3 * u2 + 1) * V[i - 1] + (u3 - 2 * u2 + u) * h * m0 + (-2 * u3 + 3 * u2) * V[i] + (u3 - u2) * h * m1;
        }
        /** Damped spring (semi-implicit Euler, sub-stepped). S[i] = x, S[i+1] = v. Returns x. */
        static float spr(float[] S, int i, float target, float hz, float zeta, float dt)
        {
            float w = TAU * hz, k = w * w, c = 2 * zeta * w;
            float x = S[i], v = S[i + 1];
            int n = Mathf.Max(1, Mathf.CeilToInt(dt * w * 1.1f)); float h = dt / n;
            for (int j = 0; j < n; j++) { v += (k * (target - x) - c * v) * h; x += v * h; }
            S[i] = x; S[i + 1] = v; return x;
        }
        /** Damped spring advanced with the exact (analytic) solution — stable for any damping ratio / step. */
        static float sprA(float[] S, int i, float target, float hz, float zeta, float dt)
        {
            float w = TAU * hz, x0 = S[i] - target, v0 = S[i + 1];
            float x, v;
            if (zeta < 0.999f)
            {
                float wd = w * Mathf.Sqrt(1 - zeta * zeta), e = Mathf.Exp(-zeta * w * dt), c = Mathf.Cos(wd * dt), sn = Mathf.Sin(wd * dt);
                float B = (v0 + zeta * w * x0) / wd;
                x = e * (x0 * c + B * sn);
                v = e * ((-zeta * w * x0 + wd * B) * c + (-zeta * w * B - wd * x0) * sn);
            }
            else
            {
                float e = Mathf.Exp(-w * dt), B = v0 + w * x0;
                x = e * (x0 + B * dt); v = e * (v0 - w * B * dt);
            }
            S[i] = x + target; S[i + 1] = v; return S[i];
        }
        static Quaternion qYXZ(float x, float y, float z) => ThreeSpace.eulerYXZ(x, y, z);
        static Quaternion qXYZ(float x, float y, float z) => ThreeSpace.eulerXYZ(x, y, z);
        static Vector3 axisAngle(Vector3 v, Vector3 axis, float a) => Quaternion.AngleAxis(a * Mathf.Rad2Deg, axis) * v;

        static readonly Vector3 UP = Vector3.up, DOWN = Vector3.down, XAX = Vector3.right, YAX = Vector3.up;
        // far-tier triangle targets per part (applied by the baker)
        /** character rim light strength (0 = off). */
        public static readonly Vector2 CHAR_RIM = new Vector2(1.25f, 1.4f);   // (k, fill)

        // rig constants (read from the rig so modelling tweaks flow through)
        static readonly float ANKLE_H = CharacterGeo.REST["footL"].y;          // ankle height above the sole
        static readonly float HIPW = Mathf.Abs(CharacterGeo.REST["footL"].x) + 0.008f;
        const float BALL_Z = 0.11f, HEEL_Z = 0.065f;                     // toe-roll / heel-roll pivots relative to the ankle projection
        static readonly Vector3 HEAD_CTR = new Vector3(0, 0.164f, 0.014f); // head-bone-relative head centre

        // ---------------------------------------------------------------------------------------------------------
        // Pose buffer layout (one float[] per layer; blended channel-wise)
        // ---------------------------------------------------------------------------------------------------------
        static int _k;
        static int S(int n = 1) { int i = _k; _k += n; return i; }
        static readonly int HIPS_P = S(3), HIPS = S(3), SPINE = S(3), CHEST = S(3), NECK = S(3), HEAD = S(3), CLAVL = S(3), CLAVR = S(3);
        static readonly int UARML = S(3), UARMR = S(3), FARML = S(3), FARMR = S(3), HANDL = S(3), HANDR = S(3);
        static readonly int FOOTL = S(3), FOOTLR = S(3), FOOTR = S(3), FOOTRR = S(3);          // kid-space ankle targets + (pitch, yaw, roll)
        static readonly int ANC = S(3), ANCR = S(3), POLER = S(3), POLEL = S(3), LTGT = S(3), LTGTR = S(3), KNEEL = S(3), KNEER = S(3);
        static readonly int IKR = S(), IKL = S(), LTW = S(), LTROT = S(), SPIN = S(), WPL = S(), WPR = S(), STAB = S(), AFOLT = S(), AFOLR = S();
        static readonly int BROW = S(), BROWY = S(), MCURVE = S(), MWIDTH = S(), MOPEN = S(), MTILT = S(), EYE = S(), WINK = S(), SQUINT = S(), LOOKX = S(), LOOKY = S();
        static readonly int MODEL = S(3), MODELR = S(3), SQY = S(), SQXZ = S(), HLY = S(), HLP = S(), CROUCH = S();
        // hand shapes: -1 fist · 0 grip (rest) · 1 relaxed · 2 open palm ; ears: -1 droop … +1 perk
        static readonly int HANDPL = S(), HANDPR = S(), EARS = S();
        // dual wield: the left weapon's anchor (kid space, like ANC/ANCR) · tiptoe: heels up on both planted feet (0..1)
        static readonly int ANL = S(3), ANLR = S(3), TIPTOE = S();
        // brow asymmetry (+ lifts the left brow, drops the right) · pupil dilation 0..1 · breath (0 out … 1 full, for the face)
        static readonly int BRAS = S(), PUPIL = S(), BREATH = S(), SNEER = S(), PUCKER = S(), BLUSH = S();
        static readonly int PN = _k;

        static void poseNeutral(float[] P)
        {
            Array.Clear(P, 0, P.Length);
            P[FOOTL] = HIPW; P[FOOTL + 1] = ANKLE_H; P[FOOTL + 2] = -0.004f; P[FOOTLR + 1] = 0.1f;
            P[FOOTR] = -HIPW; P[FOOTR + 1] = ANKLE_H; P[FOOTR + 2] = -0.004f; P[FOOTRR + 1] = -0.1f;
            P[POLER] = -0.7f; P[POLER + 1] = -0.55f; P[POLER + 2] = -0.45f;
            P[POLEL] = 0.7f; P[POLEL + 1] = -0.55f; P[POLEL + 2] = -0.45f;
            P[IKR] = 1; P[WPL] = 1; P[WPR] = 1; P[STAB] = 0.82f; P[AFOLT] = 1; P[AFOLR] = 1;
            P[MCURVE] = 0.75f; P[MWIDTH] = 1; P[EYE] = 1; P[SQY] = 1; P[SQXZ] = 1; P[HANDPL] = 1; P[PUPIL] = 0.5f;
            P[UARML + 2] = 0.1f; P[UARMR + 2] = -0.1f; P[FARML] = -0.3f; P[FARMR] = -0.3f;
            P[HANDL + 2] = -0.1f; P[HANDR + 2] = 0.1f;
        }
        static void poseLerp(float[] o, float[] a, float[] b, float t) { for (int i = 0; i < PN; i++) o[i] = a[i] + (b[i] - a[i]) * t; }
        static void setE(float[] P, int i, float x, float y, float z) { P[i] = x; P[i + 1] = y; P[i + 2] = z; }
        static void lerpE(float[] P, int i, float x, float y, float z, float w) { P[i] += (x - P[i]) * w; P[i + 1] += (y - P[i + 1]) * w; P[i + 2] += (z - P[i + 2]) * w; }

        // springs (index into the spring bank; each spring owns 2 floats)
        static int _sk;
        static int SPG() => (_sk++) * 2;
        static readonly int S_LEANP = SPG(), S_LEANR = SPG(), S_PELY = SPG(), S_SQ = SPG(), S_ARML = SPG(), S_ARMR = SPG(), S_ELBL = SPG();
        static readonly int S_WPX = SPG(), S_WPY = SPG(), S_WPZ = SPG(), S_WRX = SPG(), S_WRY = SPG();
        static readonly int S_RCP = SPG(), S_RCZ = SPG(), S_RCY = SPG(), S_RCR = SPG();
        static readonly int S_HITP = SPG(), S_HITR = SPG(), S_HITY = SPG(), S_HEADP = SPG(), S_HEADR = SPG();
        static readonly int S_HLY = SPG(), S_HLP = SPG(), S_SHIFT = SPG(), S_TANKX = SPG(), S_TANKZ = SPG(), S_TANKL = SPG();
        static readonly int S_SQY = SPG(), S_SQP = SPG(), S_SQR = SPG(), S_SQH = SPG(), S_CLAV = SPG(), S_STAG = SPG(), S_LAGX = SPG(), S_LAGZ = SPG(), S_HEMP = SPG(), S_HEMR = SPG(), S_TKY = SPG(), S_TKZ = SPG(), S_TKX = SPG(), S_EARL = SPG(), S_EARR = SPG(), S_HEMV = SPG();
        static readonly int S_RCP2 = SPG(), S_RCZ2 = SPG(), S_RCY2 = SPG();   // left-hand recoil (dual wield)
        static readonly int S_ATTY = SPG(), S_ATTP = SPG(), S_GRIP = SPG(), S_GRIPL = SPG();   // attention head turn (menus) · weapon-hand grip squeeze
        static readonly int SPN = _sk * 2;

        // one-shot timers (seconds since trigger)
        static int _tk;
        static int TK() => _tk++;
        static readonly int T_SHOOT = TK(), T_FLICK = TK(), T_THROW = TK(), T_LAND = TK(), T_JUMP = TK(), T_HIT = TK(), T_LEAP = TK(), T_SLAM = TK(), T_SPAWN = TK(), T_REL = TK(), T_IMPACT = TK(), T_BRAKE = TK(), T_FORM = TK(), T_STAG = TK();
        static readonly int T_SHOOTL = TK(), T_DODGE = TK(), T_SLOSH = TK(), T_ADMIRE = TK(), T_FLIP = TK(), T_WINK = TK();
        static readonly int TN = _tk;

        // stepping modes
        const int M_GAIT = 0, M_CATCH = 1, M_SETTLE = 2;

        // LOD tiers (character-lod.js)
        const int T_HERO = 0, T_GAME = 1, T_FAR = 2;
        const float KID_H = 1.32f, FADE_S = 0.3f, HYST = 0.12f;
        class LodQ { public float hero, heroLocal, far; public int menu; }
        static readonly Dictionary<string, LodQ> LOD_QUALITY = new Dictionary<string, LodQ>
        {
            ["low"] = new LodQ { hero = float.PositiveInfinity, heroLocal = float.PositiveInfinity, far = 150, menu = T_GAME },
            ["medium"] = new LodQ { hero = 760, heroLocal = 260, far = 110, menu = T_HERO },
            ["high"] = new LodQ { hero = 430, heroLocal = 150, far = 78, menu = T_HERO },
            ["ultra"] = new LodQ { hero = 300, heroLocal = 110, far = 54, menu = T_HERO },
        };
        /** Tier for a screen height `px` given the current tier `cur` (hysteresis), quality row `Q` and hero threshold `H`. */
        static int pickTier(float px, int cur, LodQ Q, float H)
        {
            float up = 1 + HYST, dn = 1 - HYST;
            int t = cur < 0 ? (px >= H ? T_HERO : px < Q.far ? T_FAR : T_GAME) : cur;
            if (t == T_HERO && px < H * dn) t = px < Q.far ? T_FAR : T_GAME;
            else if (t == T_GAME && px >= H * up) t = T_HERO;
            else if (t == T_GAME && px < Q.far * dn) t = T_FAR;
            else if (t == T_FAR && px >= Q.far * up) t = px >= H * up ? T_HERO : T_GAME;
            return t;
        }

        // ---------------------------------------------------------------------------------------------------------
        // State
        // ---------------------------------------------------------------------------------------------------------
        public class Foot
        {
            public int i, side; public string name;
            public Vector3 pw; public float yaw; public Vector3 n = Vector3.up; public bool planted = true, inSt = true; public float stU = 0.5f, stT = 1;
            public bool sw; public int mode = M_SETTLE; public float su, dur = 0.2f; public Vector3 from, to; public float fromYaw, toYaw;
            public float lift = 0.06f, toe, land; public Vector3 tn = Vector3.up;
            public Vector3 cw; public float cyaw, pitch; public Vector3 cn = Vector3.up;
            public Vector3 disp; public float dispYaw; public bool dispOK;
        }

        public string name;
        public bool isLocal;
        public uint seed;
        public CharacterStyle.Style style;
        readonly Func<float> rng;
        public Color color = Color.white;               // team colour (sRGB, like THREE.Color.set('#…'))
        public Color enemyColor = new Color(0.184f, 0.357f, 1f);
        /** Distance from root to the wall surface while climbing (engine keeps the player centre this far off the wall). */
        public float climbInset = PLAYER.radius;
        public Actor actor;                             // set by the owning actor (JS finds it through G.actors)

        readonly GameObject go;
        public Transform root => go.transform;
        public Action<string, Ev> onEvent { get; set; }
        public readonly Transform model, kid, squidRoot;
        public readonly CharacterMats mats;

        // rig
        public Dictionary<string, Transform> bones;
        Transform[] boneList;
        public Dictionary<string, Vector3> rest;
        CharacterGeo.Strand[] hairMeta;
        class Limb { public Transform up, lo, end; public float a, b; public Matrix4x4 Mu0T, Mf0T; }
        Limb limbL_arm, limbR_arm, limbL_leg, limbR_leg;
        float legReach;
        Vector3 faceRestBrowL, faceRestBrowR;
        Transform[] hairBones, hairTips;
        float[] hairIn, hairAx, hairA1, hairA2, hairPh;
        class XB { public Transform jaw, tank, hemF, hemB, toeL, toeR, earL, earR, cheekL, cheekR; }
        XB xb;
        Vector3?[] cheekRest = new Vector3?[2];
        class Fing { public Transform t1, t2; public Transform[] f1, f2; }
        Fing[] fing = new Fing[2];
        float[] handS = { 1, 0 };

        // LOD
        class TierPart { public string key, mat; public Mesh geo; public bool shadow; public int order; }
        class TierSet { public int t; public Dictionary<string, SkinnedMeshRenderer> meshes = new Dictionary<string, SkinnedMeshRenderer>(); public List<SkinnedMeshRenderer> list = new List<SkinnedMeshRenderer>(); public List<string> matKeys = new List<string>(); public List<bool> shadows = new List<bool>(); }
        class Lod { public int tier = -1, to = -1, force = -1; public float f, px; public bool props; public string q; }
        readonly Lod lod = new Lod();
        readonly TierSet[] lodSets = new TierSet[3];
        Dictionary<string, SkinnedMeshRenderer> meshes;

        // props
        class TankParts { public Transform group; public MeshRenderer glass, fill; public MeshFilter glassF, fillF; public float h, bottom, tilt; }
        TankParts tank;
        class SquidParts { public Transform pivot; public MeshRenderer body, ghost, dark, eyes; public MeshFilter bodyF, ghostF, darkF, eyesF; }
        SquidParts squid;

        // weapons
        readonly Dictionary<string, WeaponModel.Instance> weapons = new Dictionary<string, WeaponModel.Instance>();
        string weaponKind, animKind;
        WeaponModel.Instance weapon;
        Hold hold;
        bool dual;
        public WeaponModel.SubInstance bomb;   // the sub prop in the left hand (kits read it: the Shaker rattles it)

        // ---- animation state ----
        readonly float[] P = new float[PN], PD = new float[PN], PX = new float[PN], PY = new float[PN];
        readonly float[] sp = new float[SPN];
        readonly float[] tr = new float[TN];
        float t;
        // root motion
        Vector3 rp, rv, ra; bool rootInit;
        float yaw, prevYaw, yawRate, kvx, kvz, kax, kaz;
        float hs, gv, gs, gvx, gvz; bool tread; float tvx, tvz;
        float mdx, mdz = 1, hipTwist, vyS, gnd = 9, airT; bool grounded = true;
        // gait
        float phase; bool moving; float gaitW, runW, duty = 0.6f, cad = 1.5f, liftH = 0.06f;
        Foot[] feet; bool feetValid, replant = true; float settleCd, plantW = 1;
        float[] stance; float footTwist, hipDrop; readonly float[] stVar = new float[6]; float shufT;
        float stepOfsX, stepOfsZ;
        // weights
        float wSub, wAim, wRoll, wAir, wDance, wTwo, wGlow, wLow, wTired, wGoo;
        float exert, brPh, idleT, shiftT, shiftTgt = 1;
        int fidget = -1; float fidgetT, nextFidget;
        float lastShot = 99, lastRelease = 99, charge, chargeFlash, fullT, fireHold, _fireWant;
        float lReach, ikErrPre, leapEnd = -1, landAmp, hitX, hitZ = 1, hitAmp = 1, hitAcc; bool slamGround;
        string dance, prevDance, lastDance; float danceT, prevDanceT, danceFade = 1; int danceVar; float danceOfs;
        public string form = "kid"; string formPrev = "kid"; float formT = 99;
        float kidScale = 1, sqScale, kidPop = 1;
        // face
        float blinkK, blinkL, blinkR;
        class Blink { public float t = -1, c = 0.08f, h = 0.03f, o = 0.16f, amp = 1, lag = 0.008f, next; public bool dbl; public float squeeze; }
        readonly Blink bl = new Blink();
        class Gaze { public float x, y, x0, y0, x1, y1, st = -1, sd = 0.05f, fx, fy, mx, my, mT = 0.5f; }
        readonly Gaze gz = new Gaze();
        const int K_NONE = 0, K_VIEWER = 1, K_NEIGHBOUR = 2, K_GLANCE = 3;
        class Att { public int kind = K_NONE, prev = K_NONE; public float t; public Vector3 p = new Vector3(0, 1.2f, 5); public bool on; public Character who; public float gy, gp, dist = 5; }
        readonly Att att = new Att();
        class Mx { public float[] X, E; public float w, t = 99, a = 0.1f, h = 0.2f, d = 0.4f, bras; }
        readonly Mx[] mx = new Mx[6];
        float mxT, extGl; Quaternion _headQW = Quaternion.identity; bool _headSet;
        float brN, brAmp = 1, brRate = 1, sighT, sigh;
        float gripT, eyeX, eyeY;
        public class Face
        {
            public float blinkL, blinkR, lidL, lidR, squintL, squintR, gazeX, gazeY, verge, pupil = 0.5f,
                browInL, browInR, browOutL, browOutR, furrow, curve = 0.75f, width = 1, open, tilt, jaw, smile, sneer, pucker, breath;
        }
        public readonly Face face = new Face();
        int lifeLv = 2; float _hairAcc; bool _hairOdd;
        float lookT; Actor lookActor; float lookYaw, lookPitch, glanceYaw, glancePitch;
        float _ownT;
        readonly float[] xw = new float[12]; // smoothed expression weights
        float inkS = 1, hurt, slosh;
        // squid
        float hopPhase; bool hopAir; float sqYaw; Vector3 sqPos; Quaternion sqQuat = Quaternion.identity; bool sqInit;
        float sqBlink, sqRoll;
        float drumAngle; bool visible = true; readonly float[] ikErr = new float[4];
        // hair springs
        readonly float[] hx, hv, tipX, tipV;
        float headRY, headRZ, earT; bool bombHeld; float bombT; bool _subPrev;
        Vector3 headPrevPos, headPrevVel, headAcc, headVel; Quaternion headPrevQuat = Quaternion.identity; bool headInit;
        // events
        readonly Ev _fsData = new Ev();
        float kgx, kgz, shiftS, armR, aimP, rcP, rcZ, _effort, _toeUp; bool _tapped; int lastFidget = -1; bool inWorld; Physics phys; public bool kidForm = true;
        // weapon kinds (arsenal): dual wield, dodge roll + lock stance, slosh, splatling spin
        float jumpRun; int jumpLead;
        float kidSY = 1, kidSXZ = 1, sqSY = 1, sqSXZ = 1, kidLift;
        float tumble, tumbleX = 1, tumbleZ, tumbleDrop, _dt;
        bool weaponHidden, subPropHidden; float swipeSide = 1, spinRate, spinAngle;
        /** the ink tank's group (Ink Jet: the pack replaces the tank on the kid's back) */
        public Transform tankGroup => tank?.group;
        public void hideTank(bool on) { if (tank == null) return; tank.fill.gameObject.SetActive(!on); tank.glass.gameObject.SetActive(!on); }
        /** hide the sub prop in hand (Cheer Orb: the orb is held overhead instead) */
        public bool hideSubProp { get => subPropHidden; set => subPropHidden = value; }
        /** hide the held weapon (Howl Box: the speaker is held instead) */
        public bool hideWeapon
        {
            get => weaponHidden;
            set
            {
                weaponHidden = value;
                if (weapon == null) return;
                weapon.pivot.gameObject.SetActive(!value);
                if (weapon.left != null) weapon.left.pivot.gameObject.SetActive(!value);
            }
        }
        class Dodge { public float t; public Vector3 dir; public float dur = 0.3f; }
        Dodge dodge;
        float armL, rcP2, rcZ2, dodgeX, dodgeZ = 1, dodgeDur = 0.3f, lockW, spinW, streamW, bombSwap;
        readonly WeaponModel.State _wst = new WeaponModel.State();
        // blade (kits/blade.js)
        float blT = 99, blSide = 1; bool blHeavy;
        // bow
        float _bowLR = 99, _bowHold, _bowDraw, _bowCant = BOW_CANT;

        static readonly HashSet<Character> LIVE = new HashSet<Character>();

        // ---------------------------------------------------------------------------------------------------------
        public Character(Color color, string weapon = "shooter", CharacterStyle.Style style = null, string name = null, bool isLocal = false)
        {
            CharacterGeo.ensureLoaded();
            this.name = name ?? "Squidkid";
            this.isLocal = isLocal;
            seed = hashStr(this.name);
            this.style = CharacterStyle.resolveStyle(style, seed);
            rng = mulberry(seed);

            go = ThreeSpace.create("squidkid:" + this.name);
            model = ThreeSpace.create("model", go.transform).transform;
            kid = ThreeSpace.create("kid", model).transform;
            squidRoot = ThreeSpace.create("squid", model).transform;

            // materials (outfit colourway / pattern / iris from the style catalogue)
            mats = new CharacterMats(this.style, (seed % 997) * 0.37f);

            stance = (float[])STANCE_IDLE.Clone();
            shufT = 4 + rng() * 6;
            shiftT = 2 + rng() * 3;
            nextFidget = 3 + rng() * 3;
            danceOfs = frac(seed * 0.61803f) * 2.3f;
            bl.next = 0.6f + rng() * 2.5f;
            att.t = 0.3f + rng() * 0.8f;
            for (int i = 0; i < 6; i++) mx[i] = new Mx();
            mxT = 2 + rng() * 4; sighT = 4 + rng() * 5; gripT = 4 + rng() * 5;
            lookT = 0.5f + rng(); earT = 3 + rng() * 4;
            int n = CharacterGeo.HAIR_MAX * CharacterGeo.HAIR_SEGS * 3;
            hx = new float[n]; hv = new float[n];
            tipX = new float[CharacterGeo.HAIR_MAX * 3]; tipV = new float[CharacterGeo.HAIR_MAX * 3];
            for (int i = 0; i < TN; i++) tr[i] = 99;
            feet = new[] { mkFoot(0), mkFoot(1) };
            LIVE.Add(this); hookBus();

            _buildRig();
            _buildTank();
            _buildBomb("bomb");
            _buildSquid();
            _tierProps(lod.tier);

            setColor(color);
            setWeapon(weapon ?? "shooter");
            poseNeutral(P);
        }

        Foot mkFoot(int i) => new Foot { i = i, side = i == 0 ? 1 : -1, name = i == 0 ? "L" : "R" };

        // ---------------------------------------------------------------------------------------------------------
        void _buildRig()
        {
            var hair = CharacterGeo.getHairStyle(style, "game");
            hairMeta = hair.meta;
            rest = CharacterGeo.getRestPositions(style);
            var names = CharacterGeo.BONE_NAMES;
            bones = new Dictionary<string, Transform>();
            boneList = new Transform[names.Length];
            for (int i = 0; i < names.Length; i++) { var b = new GameObject(names[i]).transform; bones[names[i]] = b; boneList[i] = b; }
            for (int i = 0; i < names.Length; i++)
            {
                string nm = names[i], p = CharacterGeo.BONE_PARENT[nm]; var b = bones[nm];
                if (p != null) { b.SetParent(bones[p], false); b.localPosition = rest[nm] - rest[p]; }
                else { b.SetParent(kid, false); b.localPosition = rest[nm]; }
            }
            // body meshes live in LOD tiers: hero / game / far sets, each built on first use and all bound to this one
            // skeleton; _updateLod shows one (or two, dither cross-fading). Starts on the game tier.
            _setTier(T_GAME);
            // limb constants for IK (rest directions / lengths)
            Limb lim(string up, string lo, string end, Vector3 h0)
            {
                Transform L = bones[lo], E = bones[end];
                float a = L.localPosition.magnitude, b = E.localPosition.magnitude;
                Vector3 ru = L.localPosition.normalized, rf = E.localPosition.normalized;
                Matrix4x4 mk0(Vector3 r)
                {
                    Vector3 h = (h0 - r * Vector3.Dot(h0, r)).normalized; Vector3 c = Vector3.Cross(r, h);
                    var m = Matrix4x4.identity; m.SetColumn(0, r); m.SetColumn(1, h); m.SetColumn(2, c);
                    return m.transpose;
                }
                return new Limb { up = bones[up], lo = L, end = E, a = a, b = b, Mu0T = mk0(ru), Mf0T = mk0(rf) };
            }
            limbL_arm = lim("uArmL", "fArmL", "handL", new Vector3(-1, 0, 0));
            limbR_arm = lim("uArmR", "fArmR", "handR", new Vector3(-1, 0, 0));
            limbL_leg = lim("thighL", "shinL", "footL", new Vector3(1, 0, 0));
            limbR_leg = lim("thighR", "shinR", "footR", new Vector3(1, 0, 0));
            legReach = (limbL_leg.a + limbL_leg.b) * 0.985f;
            faceRestBrowL = bones["browL"].localPosition; faceRestBrowR = bones["browR"].localPosition;
            // hair: bone refs + per-strand "into the head" direction (head-bone space) so the springs never swing through it
            int HM = CharacterGeo.HAIR_MAX, HS = CharacterGeo.HAIR_SEGS;
            hairBones = new Transform[HM * HS];
            for (int s = 0; s < HM; s++) for (int k = 0; k < HS; k++) hairBones[s * HS + k] = bones[$"hair{s}_{k}"];
            hairIn = new float[HM * 3];
            Vector3 hc = rest["head"] + HEAD_CTR;
            for (int s = 0; s < hairMeta.Length; s++)
            {
                if (!rest.TryGetValue($"hair{s}_0", out var r0)) continue;
                Vector3 d = (hc - r0).normalized;
                hairIn[s * 3] = d.x; hairIn[s * 3 + 1] = d.y; hairIn[s * 3 + 2] = d.z;
            }
            Transform opt(string nm) => bones.TryGetValue(nm, out var b) ? b : null;
            xb = new XB
            {
                jaw = opt("jaw"), tank = opt("tank"), hemF = opt("hemF"), hemB = opt("hemB"),
                toeL = opt("toeL"), toeR = opt("toeR"), earL = opt("earL"), earR = opt("earR"), cheekL = opt("cheekL"), cheekR = opt("cheekR"),
            };
            cheekRest[0] = xb.cheekL != null ? xb.cheekL.localPosition : (Vector3?)null;
            cheekRest[1] = xb.cheekR != null ? xb.cheekR.localPosition : (Vector3?)null;
            // articulated hands: rest = power grip, curl about local Z (sign flips per side)
            for (int sd = 0; sd < 2; sd++)
            {
                string sn = sd == 0 ? "L" : "R";
                var t1 = opt($"hand{sn}_thumb1"); var t2 = opt($"hand{sn}_thumb2");
                var f1 = new List<Transform>(); var f2 = new List<Transform>();
                foreach (var fn in new[] { "index", "middle", "ring", "pinky" }) { var b1 = opt($"hand{sn}_{fn}1"); var b2 = opt($"hand{sn}_{fn}2"); if (b1 != null && b2 != null) { f1.Add(b1); f2.Add(b2); } }
                fing[sd] = t1 != null && t2 != null && f1.Count == 4 ? new Fing { t1 = t1, t2 = t2, f1 = f1.ToArray(), f2 = f2.ToArray() } : null;
            }
            // tentacle tips: one more spring stage after hair{s}_2
            hairTips = new Transform[HM];
            for (int s = 0; s < HM; s++) hairTips[s] = opt($"hairTip{s}");
            // per segment (+ tip): its own axis (rest direction to the next joint) and two bend axes for the idle life:
            // A1 = coil in/out of the head (curl plane), A2 = sideways
            int NS = HM * (HS + 1);
            hairAx = new float[NS * 3]; hairA1 = new float[NS * 3]; hairA2 = new float[NS * 3];
            hairPh = new float[HM];
            for (int s = 0; s < hairMeta.Length; s++)
            {
                hairPh[s] = rng() * TAU;
                for (int k = 0; k <= HS; k++)
                {
                    string an = k < HS ? $"hair{s}_{k}" : $"hairTip{s}";
                    if (!rest.TryGetValue(an, out var a)) continue;
                    Vector3 v1;
                    if (k < HS - 1) v1 = rest[$"hair{s}_{k + 1}"] - a;
                    else if (k == HS - 1) v1 = rest[$"hairTip{s}"] - a;
                    else { int pj = (s * (HS + 1) + k - 1) * 3; v1 = new Vector3(hairAx[pj], hairAx[pj + 1], hairAx[pj + 2]); }
                    if (v1.sqrMagnitude < 1e-10f) v1 = new Vector3(0, -1, 0);
                    v1.Normalize();
                    Vector3 v2 = (a - hc).normalized;                       // out of the head
                    Vector3 v3 = Vector3.Cross(v1, v2); if (v3.sqrMagnitude < 1e-8f) v3 = new Vector3(1, 0, 0); v3.Normalize();
                    Vector3 v4 = Vector3.Cross(v1, v3).normalized;
                    int j = (s * (HS + 1) + k) * 3;
                    hairAx[j] = v1.x; hairAx[j + 1] = v1.y; hairAx[j + 2] = v1.z;
                    hairA1[j] = v3.x; hairA1[j + 1] = v3.y; hairA1[j + 2] = v3.z;
                    hairA2[j] = v4.x; hairA2[j + 1] = v4.y; hairA2[j + 2] = v4.z;
                }
            }
        }

        // the sub prop held in the left hand while a throw is aimed (any sub kind)
        void _buildBomb(string kind)
        {
            bomb = WeaponModel.subInstance(kind, bones["handL"], mats);
            bomb.group.gameObject.SetActive(false);
            bomb.setColor(color);
        }
        public void setSub(string kind)
        {
            if (bomb != null && bomb.kind == kind) return;
            if (bomb != null) Ctx.destroy(bomb.group.gameObject);
            _buildBomb(kind);
        }

        void _buildTank()
        {
            var T = CharacterGeo.getKidShared("game").tank;
            var g = ThreeSpace.create("tank", xb.tank ?? bones["chest"]).transform;
            g.localPosition = T.offset; g.localRotation = qXYZ(T.tilt, 0, 0);
            if (xb.tank != null) g.localPosition -= xb.tank.localPosition;
            var fillGo = ThreeSpace.create("fill", g); fillGo.transform.localPosition = new Vector3(0, T.fillBottom, 0);
            var glassGo = ThreeSpace.create("glass", g);
            tank = new TankParts
            {
                group = g, h = T.fillHeight, bottom = T.fillBottom, tilt = T.tilt,
                fillF = fillGo.AddComponent<MeshFilter>(), fill = fillGo.AddComponent<MeshRenderer>(),
                glassF = glassGo.AddComponent<MeshFilter>(), glass = glassGo.AddComponent<MeshRenderer>(),
            };
            tank.fill.sharedMaterial = mats.fill; tank.fill.shadowCastingMode = ShadowCastingMode.Off;
            tank.glass.sharedMaterial = CharacterMats.glass; tank.glass.shadowCastingMode = ShadowCastingMode.Off;
        }

        void _buildSquid()
        {
            var pivot = ThreeSpace.create("pivot", squidRoot).transform;
            MeshRenderer part(string nm, Material m, out MeshFilter f, bool shadow)
            {
                var g = ThreeSpace.create(nm, pivot);
                f = g.AddComponent<MeshFilter>();
                var r = g.AddComponent<MeshRenderer>(); r.sharedMaterial = m;
                r.shadowCastingMode = shadow ? ShadowCastingMode.On : ShadowCastingMode.Off;
                return r;
            }
            squid = new SquidParts { pivot = pivot };
            squid.body = part("body", mats.squid, out squid.bodyF, true);
            squid.ghost = part("ghost", mats.squidGhost, out squid.ghostF, false); squid.ghost.enabled = false;
            squid.dark = part("dark", CharacterMats.dark, out squid.darkF, true);
            squid.eyes = part("eyes", mats.eye, out squid.eyesF, false);
            squidRoot.gameObject.SetActive(false);
        }

        // ---------------------------------------------------------------------------------------------------------
        // LOD tiers. A tier = the kid's skinned body parts at one level of detail, all on the same skeleton and the same
        // (per-kid) materials. The far tier is decimated by the baker.
        // ---------------------------------------------------------------------------------------------------------
        List<TierPart> _tierParts(int t)
        {
            string tn = CharacterGeo.TIERS[t];
            var K = CharacterGeo.getKidShared(tn);
            var H = CharacterGeo.getHairStyle(style, tn);
            var parts = new List<TierPart>
            {
                new TierPart { key = "skin", geo = K.skin, mat = "skin", shadow = true },
                new TierPart { key = "cloth", geo = K.cloth, mat = "cloth", shadow = true },
                new TierPart { key = "hair", geo = H.geo, mat = "hair", shadow = true },
                new TierPart { key = "eyes", geo = K.eyes, mat = "eye", shadow = false },
            };
            if (t == T_FAR) foreach (var p in parts) if (p.key != "skin" && p.key != "cloth") p.shadow = false;   // far: only the body's bulk casts
            return parts;
        }

        /** The body meshes of tier t (built on first use, hidden until shown). */
        TierSet _tierSet(int t)
        {
            if (lodSets[t] != null) return lodSets[t];
            var S = new TierSet { t = t };
            foreach (var p in _tierParts(t))
            {
                var g = ThreeSpace.create("kid:" + p.key + ":" + CharacterGeo.TIERS[t], kid);
                var m = g.AddComponent<SkinnedMeshRenderer>();
                m.sharedMesh = p.geo;
                m.bones = boneList;
                m.rootBone = kid;
                m.localBounds = new Bounds(new Vector3(0, 0.75f, 0), Vector3.one * 2.6f);
                m.sharedMaterial = mats.get(p.mat);
                m.shadowCastingMode = p.shadow ? ShadowCastingMode.On : ShadowCastingMode.Off;
                m.receiveShadows = true;
                m.updateWhenOffscreen = false;
                m.skinnedMotionVectors = false;
                m.sortingOrder = p.order;
                m.enabled = false;
                S.meshes[p.key] = m; S.list.Add(m); S.matKeys.Add(p.mat); S.shadows.Add(p.shadow);
            }
            lodSets[t] = S;
            return S;
        }

        /** Show tier t now (no fade): first frame, menus, forced tiers. */
        void _setTier(int t)
        {
            var S = _tierSet(t);
            for (int i = 0; i < 3; i++)
            {
                var X = lodSets[i]; if (X == null) continue;
                for (int j = 0; j < X.list.Count; j++)
                {
                    var m = X.list[j];
                    m.enabled = i == t; m.sharedMaterial = mats.get(X.matKeys[j]);
                    m.shadowCastingMode = X.shadows[j] ? ShadowCastingMode.On : ShadowCastingMode.Off;
                }
            }
            lod.tier = t; lod.to = -1; lod.f = 0; mats.setLodFade(0, 0);
            meshes = S.meshes;
            _tierProps(t);
        }

        /** Rigid props follow the tier too (instant swap — small on screen): squid form, tank glass + fill. */
        void _tierProps(int t)
        {
            if (squid == null || tank == null) return;
            var K = CharacterGeo.getKidShared(CharacterGeo.TIERS[Mathf.Max(0, t)]);
            squid.bodyF.sharedMesh = K.squid.body; squid.ghostF.sharedMesh = K.squid.body;
            squid.darkF.sharedMesh = K.squid.dark; squid.eyesF.sharedMesh = K.squid.eyes;
            tank.glassF.sharedMesh = K.tank.glass; tank.fillF.sharedMesh = K.tank.fill;
            tank.glass.enabled = t != T_FAR;   // a 14 %-opaque shell on a 60 px kid: the ink fill alone reads the same
        }

        /** Projected screen height (CSS px) of the kid for the game camera. 0 = unknown yet. */
        float _screenPx()
        {
            var cam = G.rig?.camera;
            if (!inWorld || cam == null) return 0;
            float e5 = 1 / Mathf.Tan(cam.fov * 0.5f * Mathf.Deg2Rad);
            float dpr = Application.isMobilePlatform && Screen.dpi > 0 ? Mathf.Max(1, Screen.dpi / 160f) : 1;
            float vh = Screen.height / dpr;
            Vector3 k = root.localPosition + new Vector3(0, 0.66f, 0);
            return KID_H * e5 / Mathf.Max(0.2f, Vector3.Distance(k, cam.position)) * vh * 0.5f;
        }

        /** Pick the tier for this frame; fade between tiers with the screen-door dither (0.3 s) once the kid is on screen. */
        void _updateLod(float dt)
        {
            var L = lod;
            string q = G.settings?.quality ?? "high";
            if (L.to >= 0)
            {
                L.f = Mathf.Min(1, L.f + dt / FADE_S);
                mats.setLodFade(Mathf.Max(1e-4f, L.f), L.to);
                if (L.f >= 0.5f && !L.props) { L.props = true; _tierProps(L.to); }
                if (L.f >= 1) _setTier(L.to);
                return;
            }
            var Q = LOD_QUALITY.TryGetValue(q, out var qq) ? qq : LOD_QUALITY["high"];
            int want;
            float px = L.px = _screenPx();
            if (L.force >= 0) want = L.force;
            else if (!inWorld) want = Q.menu;
            else if (px <= 0) want = L.tier;
            else want = pickTier(px, L.tier, Q, isLocal ? Q.heroLocal : Q.hero);
            if (want == L.tier) return;
            if (!visible || !inWorld || dt <= 0 || L.force >= 0) { _setTier(want); return; }
            _startFade(want);
        }

        /** Cross-fade from the current tier to t: outgoing parts keep the IGN ≥ f pixels, incoming the rest; 0.3 s. */
        void _startFade(int t)
        {
            var L = lod;
            if (L.to >= 0 || t == L.tier) return;
            var A = lodSets[L.tier]; var B = _tierSet(t);
            for (int j = 0; j < A.list.Count; j++) { A.list[j].sharedMaterial = mats.dither(A.matKeys[j], 0); A.list[j].shadowCastingMode = ShadowCastingMode.Off; }
            for (int j = 0; j < B.list.Count; j++)
            {
                B.list[j].sharedMaterial = mats.dither(B.matKeys[j], 1);
                B.list[j].shadowCastingMode = B.shadows[j] ? ShadowCastingMode.On : ShadowCastingMode.Off;
                B.list[j].enabled = true;
            }
            L.to = t; L.f = 0; L.props = false; mats.setLodFade(1e-4f, t);
        }

        /** Force a tier ('hero' | 'game' | 'far'), or null for automatic. Labs / showcase portraits / audits. */
        public void setLod(string t)
        {
            int i = t == null ? -1 : Array.IndexOf(CharacterGeo.TIERS, t);
            lod.force = i;
            if (lod.force >= 0) _setTier(lod.force);
        }
        public string lodTier => CharacterGeo.TIERS[lod.to >= 0 ? lod.to : lod.tier];

        // ---------------------------------------------------------------------------------------------------------
        // Public API
        // ---------------------------------------------------------------------------------------------------------
        public void setColor(Color c)
        {
            color = c;
            mats.setTeam(c);
            bomb?.setColor(c);
            foreach (var w in weapons.Values) for (var x = w; x != null; x = x.left) x.setColor(c);
        }

        // kind = weapon model; the pose family (animKind) comes from ANIM_OF — new weapons borrow an existing family's animation
        public void setWeapon(string kind)
        {
            if (!HOLD.ContainsKey(ANIM_OF.TryGetValue(kind, out var af) ? af : kind)) kind = "shooter";
            if (kind == weaponKind) return;
            if (weaponKind != null && weapons.TryGetValue(weaponKind, out var old))
            {
                old.pivot.SetParent(null, false); old.pivot.gameObject.SetActive(false);
                if (old.left != null) { old.left.pivot.SetParent(null, false); old.left.pivot.gameObject.SetActive(false); }
                if (old.pivotL != null) { old.pivotL.SetParent(null, false); old.pivotL.gameObject.SetActive(false); }
            }
            var w = _getWeapon(kind);
            w.pivot.SetParent(bones["handR"], false); w.pivot.gameObject.SetActive(!weaponHidden);
            if (w.left != null) { w.left.pivot.SetParent(bones["handL"], false); w.left.pivot.gameObject.SetActive(true); }
            if (w.pivotL != null) { w.pivotL.SetParent(bones["handL"], false); w.pivotL.gameObject.SetActive(true); }
            weaponKind = kind; animKind = ANIM_OF.TryGetValue(kind, out var ak) ? ak : kind; weapon = w;
            hold = HOLD.TryGetValue(kind, out var hk2) ? hk2 : HOLD[animKind];   // a kind may bring its own hold data (bow)
            dual = w.left != null;
        }

        WeaponModel.Instance _getWeapon(string kind)
        {
            if (weapons.TryGetValue(kind, out var w)) return w;
            w = WeaponModel.instance(kind, mats, false);
            if (w.def.dual && w.def.hasInHandL) w.left = WeaponModel.instance(kind, mats, true);
            if (w.def.mirrorDual) WeaponModel.addMirror(w, mats);
            w.setColor(color);
            if (w.left != null) w.left.setColor(color);
            weapons[kind] = w;
            return w;
        }

        public void trigger(string name, float arg = 1, Vector2? dir = null)
        {
            switch (name)
            {
                case "shoot":
                {
                    if (animKind == "roller") { tr[T_FLICK] = 0; break; }
                    if (animKind == "slosher") { if (tr[T_SLOSH] > 0.3f) tr[T_SLOSH] = 0; lastShot = 0; break; }
                    int hand = arg >= 2 ? 1 : 0;   // dualies alternate hands: arg 2 = the left pistol
                    if (dual && hand == 1) { tr[T_SHOOTL] = 0; _recoil(1, 1); sp[S_GRIPL + 1] += 2.2f; }
                    else { tr[T_SHOOT] = 0; _recoil(1, 0); sp[S_GRIP + 1] += 2.2f; if (!dual) sp[S_GRIPL + 1] += 1.2f; }
                    lastShot = 0;
                    break;
                }
                case "slosh": tr[T_SLOSH] = 0; lastShot = 0; break;
                // locker / showcase one-shots (played right after a look swap)
                case "admire": tr[T_ADMIRE] = 0; break;
                case "hairflip": tr[T_FLIP] = 0; break;
                case "wink": tr[T_WINK] = 0; break;
                case "dodge":
                {
                    // dualies roll: dir = unit roll direction in root space (+z forward, +x = the character's left), arg = duration
                    float x = 0, z = 1, d = 0.3f;
                    if (dir.HasValue) { x = dir.Value.x; z = dir.Value.y; d = clamp(arg > 0 ? arg : 0.3f, 0.15f, 0.8f); }
                    float l = Mathf.Sqrt(x * x + z * z); if (l > 1e-4f) { x /= l; z /= l; } else { x = 0; z = 1; }
                    tr[T_DODGE] = 0; dodgeX = x; dodgeZ = z; dodgeDur = d; lockW = Mathf.Max(lockW, 0.001f);
                    sp[S_SQ + 1] -= 1.6f; sp[S_TANKL + 1] += 1.5f; _hairKick(-x * 2, 1.2f, -z * 2);
                    break;
                }
                // roller-family flick / bucket heave / brush swipe (arg = swipe side ±1, alternating)
                case "flick": tr[T_FLICK] = 0; lastShot = 0; if (arg != 0) swipeSide = arg; sp[S_GRIP + 1] += 3; sp[S_GRIPL + 1] += 3; break;
                // Brine Cutlass: a quick cut (arg = side ±1) / the charged overhead cut
                case "blade_slash": blT = 0; blSide = arg != 0 ? arg : 1; blHeavy = false; lastShot = 0; sp[S_GRIP + 1] += 2.4f; break;
                case "blade_heavy": blT = 0; blHeavy = true; lastShot = 0; sp[S_SQ + 1] -= 1.2f; sp[S_GRIP + 1] += 3.2f; break;
                case "stream": lastShot = 0; break;   // spinner burst in progress: keep the aim pose and the barrels spinning
                // twins dodge roll: dir = world-space (x, z) direction; one forward tumble of the model (see _updateDodge)
                case "roll":
                    dodge = new Dodge { t = 0, dir = dir.HasValue ? new Vector3(dir.Value.x, 0, dir.Value.y) : new Vector3(0, 0, 1), dur = arg > 0 && arg < 2 ? arg : 0.3f };
                    break;
                case "throw": tr[T_THROW] = 0; bombHeld = false; break;
                case "land":
                {
                    float a = clamp((arg - 2.5f) / 13, 0.12f, 1);
                    landAmp = tr[T_LAND] < 0.25f ? Mathf.Max(landAmp, a) : a; tr[T_LAND] = 0;
                    sp[S_PELY + 1] -= 3.0f * a; sp[S_SQ + 1] -= 3.6f * a; sp[S_LEANP + 1] += 2.2f * a; sp[S_HEADP + 1] += 3.5f * a;
                    sp[S_ARML + 1] -= 4 * a; sp[S_ARMR + 1] -= 4 * a; sp[S_WPY + 1] -= 0.9f * a; sp[S_WRX + 1] += 4 * a; sp[S_TANKL + 1] -= 3 * a;
                    _hairKick(0, -3.2f * a, 0);
                    sp[S_EARL + 1] -= 5 * a; sp[S_EARR + 1] -= 5 * a;
                    if (a > 0.55f) _blink();
                    break;
                }
                case "jump":
                {
                    tr[T_JUMP] = 0; sp[S_SQ + 1] += 2.6f; sp[S_TANKL + 1] += 2; _hairKick(0, 2.4f, 0);
                    if (kidForm && dance == null && rng() < 0.6f) _mxPlay(X_HUP, 0.6f, 0.05f, 0.12f, 0.3f);
                    // a running jump leaps off the planted foot: the leg that is swinging (or furthest behind) drives up in front
                    var F = feet;
                    jumpRun = kidForm ? sstep(1.5f, 4.5f, gs) : 0;
                    jumpLead = F[0].sw && !F[1].sw ? 0 : F[1].sw && !F[0].sw ? 1 : (F[0].su > F[1].su ? 0 : 1);
                    break;
                }
                case "hit":
                {
                    tr[T_HIT] = 0;
                    float hx = 0, hz = 1, amp = 1;
                    if (dir.HasValue)
                    {
                        hx = dir.Value.x; hz = dir.Value.y; float l = Mathf.Sqrt(hx * hx + hz * hz);
                        if (l > 1e-4f) { hx /= l; hz /= l; } else hz = 1;
                        amp = clamp(arg, 0.3f, 1.6f);
                    }
                    else { hx = (rng() - 0.5f) * 1.2f; hz = 1; amp = clamp(arg, 0.3f, 1.4f); }
                    hitX = hx; hitZ = hz; hitAmp = amp;
                    hitAcc += amp;
                    // dir = unit direction toward the attacker in root space (+z forward, +x = the character's left).
                    // Torso knocked away from it: pitch back from frontal hits, roll away from side hits, twist toward the struck side.
                    sp[S_HITP + 1] -= hz * 6.5f * amp; sp[S_HITR + 1] += hx * 6.5f * amp; sp[S_HITY + 1] += hx * 5 * amp; sp[S_PELY + 1] -= 0.5f * amp;
                    sp[S_HEADP + 1] -= hz * 7.5f * amp; sp[S_HEADR + 1] += hx * 7 * amp;
                    sp[S_CLAV + 1] += 3.5f * amp; sp[S_WRX + 1] -= 2 * amp;
                    _hairKick(hx * 1.5f, 1.2f, -hz * 1.8f);
                    sp[S_EARL + 1] += (4 + 3 * hx) * amp; sp[S_EARR + 1] += (4 - 3 * hx) * amp;
                    _blink(amp > 0.7f);
                    if (amp > 0.8f && kidForm) _mxPlay(X_OOF, 0.35f + 0.3f * amp, 0.04f, 0.1f, 0.45f);
                    sp[S_GRIP + 1] += 3 * amp;
                    if (hitAcc > 2.6f && tr[T_STAG] > 0.9f) { tr[T_STAG] = 0; hitAcc = 0; stepOfsZ = -0.16f * hz; stepOfsX = -0.1f * hx; sp[S_STAG + 1] -= 2.5f; }
                    if (form != "kid") { sp[S_SQP + 1] += 5 * amp; sp[S_SQY + 1] -= 3 * amp; }
                    break;
                }
                case "special_leap": tr[T_LEAP] = 0; tr[T_SLAM] = 99; tr[T_IMPACT] = 99; slamGround = false; leapEnd = -1; _hairKick(0, -2, 0); break;
                case "special_slam": tr[T_SLAM] = 0; leapEnd = tr[T_LEAP] < 1.9f ? tr[T_LEAP] : -1; tr[T_IMPACT] = 99; slamGround = false; break;
                case "spawn":
                    tr[T_SPAWN] = 0; form = "kid"; formPrev = "kid"; formT = 99; kidScale = 1; sqScale = 0;
                    feetValid = false; replant = true; headInit = false; rootInit = false;
                    break;
                case "charge_release":
                    tr[T_REL] = 0; lastRelease = 0; lastShot = 0; chargeFlash = 1; _recoil(0.5f + 0.7f * charge); sp[S_GRIP + 1] += 3; sp[S_GRIPL + 1] += 2;
                    if (charge > 0.8f) _mxPlay(X_EFFORTP, 0.5f, 0.03f, 0.08f, 0.3f);
                    break;
            }
        }

        public void setDance(string nm)
        {
            if (nm == dance) return;
            if (dance != null && nm != null) { prevDance = dance; prevDanceT = danceT; danceFade = 0; }
            dance = nm; danceT = 0;
            int nv = nm != null && DANCE_VARIANTS.TryGetValue(nm, out var v) ? v : 1;
            danceVar = (int)((seed >> 5) % (uint)nv);
            // the moment it lands: a burst of joy (win) / a sinking breath out (lose) on top of the dance's own face
            if (nm == "victory") { _mxPlay(X_JOY, 0.7f, 0.08f, 0.5f, 0.8f, 0, E_BLUSH); _blinkStart(1, false); }
            else if (nm == "defeat") { _mxPlay(X_SIGH, 0.9f, 0.3f, 0.8f, 1.2f); sigh = 1; }
        }

        public void setHurt(float amount, Color enemy)
        {
            hurt = clamp(amount, 0, 1);
            enemyColor = enemy;
            mats.setHurt(enemyColor, hurt);
        }

        public void setVisible(bool v)
        {
            visible = v; go.SetActive(v);
            if (!visible) { feetValid = false; rootInit = false; }
        }

        public Vector3 getMuzzle()
        {
            if (form != "kid" || weapon == null) return getHeadPosition();
            return ThreeSpace.toThree(weapon.muzzle.position);
        }

        /** World muzzle of one hand's weapon: hand 0 = the main (right) one, hand 1 = the left pistol when dual wielding. */
        public Vector3 getMuzzleHand(int hand = 0)
        {
            if (hand == 1 && form == "kid" && weapon != null && weapon.left != null) return ThreeSpace.toThree(weapon.left.muzzle.position);
            return getMuzzle();
        }

        bool ICharacter.dual => dual;

        /** 0..1 — how far the held weapon is into its aim pose (1 = up and aimed; rollers never "aim"). */
        public float aimReady() => form != "kid" || weapon == null || animKind == "roller" ? 1 : wAim;

        /** Three-space muzzle of the full aim pose at `pitch` (where the gun springs to when the trigger is pulled). */
        public bool getAimMuzzle(float pitch, out Vector3 o)
        {
            o = default;
            if (form != "kid" || weapon == null || animKind == "roller") return false;
            var a = hold.aim;
            float ap = clamp(pitch, -1.0f, 1.15f), aimPose = clamp(ap, -0.8f, 1.0f);
            Vector3 v1 = axisAngle(new Vector3(a.p[0], a.p[1], a.p[2]), XAX, -aimPose) + AIM_PIVOT;
            Quaternion q1 = qYXZ(a.r[0] - ap, a.r[1], a.r[2]);
            Vector3 local = q1 * weapon.def.muzzle + v1;
            o = ThreeSpace.toThree(kid.TransformPoint(local));
            return true;
        }

        /** Three-space position of the head centre (for name tags, cameras). */
        public Vector3 getHeadPosition()
        {
            if (form != "kid") return ThreeSpace.toThree(squid.pivot.position);
            return ThreeSpace.toThree(bones["head"].TransformPoint(HEAD_CTR));
        }

        public void dispose()
        {
            LIVE.Remove(this);
            foreach (var w in weapons.Values) for (var x = w; x != null; x = x.left) if (x.pivot != null) Ctx.destroy(x.pivot.gameObject);   // (scene teardown may have destroyed them first)
            Ctx.destroy(go);
            mats.dispose();
        }

        // ---------------------------------------------------------------------------------------------------------
        // internal event helpers
        // ---------------------------------------------------------------------------------------------------------
        void _recoil(float k, int hand = 0)
        {
            var rc = hold.rc; float w = TAU * rc.hz;
            if (hand == 1)
            {
                sp[S_RCP2 + 1] += rc.kick * w * 1.35f * k; sp[S_RCZ2 + 1] += rc.back * w * 1.35f * k;
                sp[S_RCY2 + 1] += (rng() - 0.5f) * 2 * rc.jit * w * k;
            }
            else
            {
                sp[S_RCP + 1] += rc.kick * w * 1.35f * k;
                sp[S_RCZ + 1] += rc.back * w * 1.35f * k;
                sp[S_RCY + 1] += (rng() - 0.5f) * 2 * rc.jit * w * k;
                sp[S_RCR + 1] += (rng() - 0.5f) * 2 * rc.jit * w * k;
            }
            sp[S_TANKX + 1] += (rng() - 0.5f) * 0.4f * k; sp[S_TANKL + 1] -= 0.4f * k;
        }
        void _hairKick(float x, float y, float z) { for (int i = 0; i < hv.Length; i += 3) { hv[i] += z * 0.6f + x * 0.3f; hv[i + 1] += x * 0.4f; hv[i + 2] += y * 0.5f - x * 0.2f; } }
        void _blink(bool hard = false) => _blinkStart(1, hard);

        // ---------------------------------------------------------------------------------------------------------
        // Update
        // ---------------------------------------------------------------------------------------------------------
        static readonly AnimState EMPTY_STATE = new AnimState();
        static float rootYaw(Transform r) { var q = r.localRotation; return 2 * Mathf.Atan2(q.y, q.w); }

        public void update(float dt, AnimState s)
        {
            dt = clamp(dt, 0, 0.1f);
            s ??= EMPTY_STATE;
            _dt = dt;
            t += dt;
            for (int i = 0; i < TN; i++) tr[i] += dt;
            lastShot += dt; lastRelease += dt; blT += dt;
            if (dance != null) danceT += dt;
            prevDanceT += dt; danceFade = Mathf.Min(1, danceFade + dt / 0.45f);
            inWorld = G.physics != null && root.parent == ThreeSpace.root;
            phys = inWorld ? G.physics : null;

            // ---- inputs ----
            string f = s.form ?? "kid";
            grounded = s.grounded;
            if (f != form)
            {
                bool k0 = form == "kid", k1 = f == "kid";
                if (k0 != k1) _formEnter(f); else form = f;   // squid ⇄ swim ⇄ climb: same body, no pop
                if (f == "kid") feetValid = false;
            }
            formT += dt;
            kidForm = form == "kid";

            _trackRoot(dt, s);
            _updateStates(dt, s);
            _updateFormScales(dt);

            // an external head turn applied after our last update (the showcase lobby glance): yaw of (now · ours⁻¹)
            if (_headSet && kidForm)
            {
                var q1 = bones["head"].localRotation * Quaternion.Inverse(_headQW);
                float g = 2 * Mathf.Atan2(q1.y, q1.w);
                extGl = Mathf.Abs(g) < 1.5f ? g : 0;
            }
            else extGl = 0;
            // life detail: 2 full · 1 small on screen (game tier < 150 px) · 0 far tier (no face, fingers, jiggle, breathing;
            // hair at half rate) — none of it is readable at that size
            int lt = lod.to >= 0 ? Mathf.Min(lod.tier, lod.to) : lod.tier;
            lifeLv = !inWorld || lt == T_HERO ? 2 : lt == T_FAR ? 0 : lod.px > 0 && lod.px < 150 ? 1 : 2;
            // hidden (not drawn): keep the clocks and states, skip the pose; the feet re-plant and the hair re-inits on return
            bool shown = go.activeInHierarchy;
            if (kidScale > 0.001f && shown)
            {
                _updateFeet(dt, s);
                _buildPose(dt, s);
                _applyPose(dt, s);
            }
            else { feetValid = false; headInit = false; _headSet = false; }
            if (shown) _updateSquid(dt, s); else sqInit = false;
            _updateMaterials(dt, s);
            _updateLod(dt);
        }

        // Dodge roll (twin pistols): one forward tumble about the body centre toward the roll direction, ~0.3 s.
        void _updateDodge(float dt)
        {
            if (dodge == null) return;
            var R = dodge; R.t += dt;
            float u = R.t / (R.dur > 0 ? R.dur : 0.3f);
            if (u >= 1) { dodge = null; model.localRotation = Quaternion.identity; model.localPosition = Vector3.zero; return; }
            // roll direction into root space (the engine sets the root's yaw to the facing)
            float ry = rootYaw(root), c = Mathf.Cos(-ry), sn = Mathf.Sin(-ry);
            float lx = R.dir.x * c + R.dir.z * sn, lz = -R.dir.x * sn + R.dir.z * c;
            Vector3 axis = new Vector3(lz, 0, -lx).normalized;                       // tumble axis: up × direction
            float e = u < 0.5f ? 2 * u * u : 1 - 2 * (1 - u) * (1 - u);
            var q = Quaternion.AngleAxis(TAU * e * Mathf.Rad2Deg, axis);
            model.localRotation = q;
            Vector3 C = new Vector3(0, 0.55f, 0);
            Vector3 p = C - q * C; p.y -= Mathf.Sin(Mathf.PI * u) * 0.28f;
            model.localPosition = p;
        }

        // Root motion → world velocity/acceleration (+ kid-space versions), turn rate, ground distance while airborne.
        void _trackRoot(float dt, AnimState s)
        {
            Vector3 r = root.localPosition;
            float y = rootYaw(root);
            if (!rootInit || (r - rp).sqrMagnitude > 9)
            {
                rp = r; rv = Vector3.zero; ra = Vector3.zero; prevYaw = y; rootInit = true; feetValid = false; yawRate = 0;
            }
            yaw = y;
            if (dt > 0)
            {
                Vector3 v1 = (r - rp) / dt;
                Vector3 v2 = rv;
                rv = Vector3.Lerp(rv, v1, 1 - Mathf.Exp(-dt * 32));
                Vector3 v3 = (rv - v2) / dt;
                ra = Vector3.Lerp(ra, v3, 1 - Mathf.Exp(-dt * 16));
                yawRate = damp(yawRate, clamp(s.turnRate, -14, 14), 30, dt);
            }
            rp = r; prevYaw = y;
            float c = Mathf.Cos(y), sn = Mathf.Sin(y);
            kvx = rv.x * c - rv.z * sn; kvz = rv.x * sn + rv.z * c;
            kax = ra.x * c - ra.z * sn; kaz = ra.x * sn + ra.z * c;
            hs = Mathf.Sqrt(kvx * kvx + kvz * kvz);
            // treadmill: the engine says we move but the root stays put (lab / previews) → the ground slides under us instead
            float sv = s.speed;
            Vector2 lm = s.localMove;
            float lml = lm.magnitude;
            tread = hs < 0.12f && sv > 0.4f && lml > 0.05f && grounded;
            if (tread)
            {
                float kx = (-lm.x / lml) * sv, kz = (lm.y / lml) * sv; // kid space (+x left)
                tvx = kx * c + kz * sn; tvz = -kx * sn + kz * c;
                gvx = tvx; gvz = tvz; kgx = kx; kgz = kz;
            }
            else { tvx = 0; tvz = 0; gvx = rv.x; gvz = rv.z; kgx = kvx; kgz = kvz; }
            gv = Mathf.Sqrt(gvx * gvx + gvz * gvz);
            gs = damp(gs, grounded ? gv : gs, gv > gs ? 16 : 7, dt);
            if (gv > 0.35f)
            {
                float il = 1 / gv;
                mdx = damp(mdx, kgx * il, 10, dt); mdz = damp(mdz, kgz * il, 10, dt);
                float ml = Mathf.Sqrt(mdx * mdx + mdz * mdz); if (ml == 0) ml = 1; mdx /= ml; mdz /= ml;
            }
            vyS = damp(vyS, s.vy, 14, dt);
            airT = grounded ? 0 : airT + dt;
            // distance to the ground below while airborne (fall reach + landing anticipation)
            if (!grounded && phys != null)
            {
                var h = phys.raycast(new Vector3(r.x, r.y + 0.3f, r.z), DOWN, 4, _gHit, false);
                gnd = h.hit ? Mathf.Max(0, h.dist - 0.3f) : 9;
            }
            else gnd = grounded ? 0 : 9;
        }
        readonly Hit _gHit = new Hit();

        // Blend weights and slow state (aiming, rolling, air, exertion, tiredness, stance presets, hip twist).
        void _updateStates(float dt, AnimState s)
        {
            bool kd = kidForm, dn = dance != null; var H = hold;
            float ch = s.charge;
            bool sub = s.subAim;
            wSub = damp(wSub, sub && kd && !dn ? 1 : 0, sub ? 14 : 9, dt);
            if (sub && !_subPrev && kd) { bombHeld = true; bombT = 0; }
            if (!sub && wSub < 0.3f) bombHeld = false;
            _subPrev = sub; bombT += dt;
            // dual wield: the left pistol makes way for the bomb (held + throw), then pops back into the fist
            bombSwap = damp(bombSwap, dual && kd && (bombHeld || tr[T_THROW] < 0.32f) ? 1 : 0, 16, dt);
            // dualies: roll → locked turret stance while the runner's lockT runs
            {
                var R = _runner(); float dk = tr[T_DODGE] / dodgeDur;
                bool lk = kd && !dn && dual && ((R != null ? R.lockT > 0 : tr[T_DODGE] < dodgeDur + 0.5f) || (dk > 0.55f && dk < 1));
                lockW = damp(lockW, lk ? 1 : 0, lk ? 18 : 7, dt);
            }
            bool aiming = kd && !dn && animKind != "roller" && (s.firing || ch > 0.01f || lastShot < 0.5f || lastRelease < 0.35f || lockW > 0.5f);
            wAim = damp(wAim, aiming ? 1 : 0, aiming ? 22 : 4.5f, dt);
            bool rolling = kd && !dn && s.rolling && animKind == "roller" && tr[T_FLICK] > 0.6f;
            wRoll = damp(wRoll, rolling ? 1 : 0, rolling ? 11 : 6, dt);
            wAir = damp(wAir, grounded ? 0 : 1, grounded ? 24 : 12, dt);
            wDance = damp(wDance, dn ? 1 : 0, 5, dt);
            charge = damp(charge, ch, 25, dt);
            fullT = ch >= 0.995f ? fullT + dt : 0;
            chargeFlash = Mathf.Max(0, chargeFlash - dt * 3.5f);
            wGlow = damp(wGlow, s.special >= 0.999f ? 1 : 0, 6, dt);
            wLow = damp(wLow, s.lowInk ? 1 : 0, 8, dt);
            inkS = damp(inkS, clamp(s.ink, 0, 1), 8, dt);
            float hp = s.hp; if (hp > 1.001f) hp /= PLAYER.hp;
            wTired = damp(wTired, sstep(0.45f, 0.12f, hp), 3, dt);
            wGoo = damp(wGoo, s.inEnemyInk && grounded && kd ? 1 : 0, 6, dt);
            hitAcc = Mathf.Max(0, hitAcc - dt * 1.4f);
            stepOfsX = damp(stepOfsX, 0, 2.2f, dt); stepOfsZ = damp(stepOfsZ, 0, 2.2f, dt);
            // exertion: builds while sprinting, decays at rest (drives breathing rate/amplitude)
            exert = clamp(exert + (gs > 3.5f ? dt * 0.12f : -dt * 0.06f) + (tr[T_LAND] < dt * 1.5f ? 0.05f * landAmp : 0), 0, 1);
            brPh += dt * lerp(0.26f, 0.72f, Mathf.Max(exert, wTired * 0.8f)) * brRate * (sigh > 0 ? 0.62f : 1);
            // gait params from the smoothed speed
            float v = moving ? Mathf.Max(gv, 0.6f) : gs;   // gait params follow the real ground speed (no lag)
            float rw = runW = sstep(1.7f, 4.3f, v);
            duty = lerp(0.62f, 0.3f, rw) + 0.06f * wGoo;
            float half = lerp(0.19f, 0.265f, sstep(0.4f, 5, v)) * (1 - 0.18f * wGoo);
            cad = clamp(Mathf.Max(v, 0.6f) * duty / (2 * half), 1.1f, 4.4f) * (1 - 0.1f * wGoo);
            liftH = lerp(0.05f, 0.2f, sstep(1.2f, 5.5f, v)) * (1 + 0.9f * wGoo);
            gaitW = damp(gaitW, moving ? 1 : 0, moving ? 7 : 4.5f, dt);
            // hips twist toward the travel direction when strafing (legs run "diagonal"), upper body counter-rotates
            float tw = 0;
            if (moving)
            {
                tw = Mathf.Atan2(mdx, Mathf.Abs(mdz) + 0.3f) * 0.78f;
                if (mdz < -0.25f) tw = -tw * 0.8f;
                tw = clamp(tw, -0.8f, 0.8f) * sstep(0.5f, 2.2f, v);
            }
            hipTwist = damp(hipTwist, tw, 7, dt);
            // stance preset (feet targets when standing): idle, or the weapon's aim / roll stance
            float aimSt = Mathf.Max(wAim, wRoll) * (1 - gaitW);
            var st = stance; var A = H.stance; float lkw = lockW;
            // idle clock (fidgets + weight shifts)
            bool idleNow = kd && !dn && !moving && grounded && wAim < 0.05f && wRoll < 0.05f && tr[T_LAND] > 0.5f && tr[T_SPAWN] > 1.2f;
            // idle shuffles: every so often one foot wants to sit a little wider / narrower / turned
            var SV = stVar;
            if (idleNow)
            {
                if (idleT > 2.5f) shufT -= dt;
                if (shufT <= 0)
                {
                    shufT = 5 + rng() * 8;
                    int o = rng() < 0.5f ? 0 : 3; float sd = o != 0 ? -1 : 1; var M = SHUFFLES[(int)(rng() * SHUFFLES.Length)]; float j = 0.88f + 0.24f * rng();
                    SV[o] = sd * M[0] * j; SV[o + 1] = M[1] * j; SV[o + 2] = sd * (M[2] + (rng() - 0.5f) * 0.15f);
                    int q = 3 - o; SV[q] *= 0.3f; SV[q + 1] *= 0.3f; SV[q + 2] *= 0.3f;
                }
            }
            else if (moving || wAim > 0.2f || dn) { for (int i = 0; i < 6; i++) SV[i] = 0; shufT = Mathf.Max(shufT, 3); }
            for (int i = 0; i < 6; i++) st[i] = damp(st[i], lerp(STANCE_IDLE[i] + SV[i], lerp(A[i], STANCE_LOCK[i], lkw), Mathf.Max(aimSt, lkw)), lkw > 0.5f ? 16 : 9, dt);
            idleT = idleNow ? idleT + dt : 0;
            if (fidget >= 0) { fidgetT += dt; if (fidgetT > FIDGET_LEN[fidget] || !idleNow) { fidget = -1; nextFidget = 4 + rng() * 5; idleT = Mathf.Min(idleT, 1.5f); } }
            else if (idleNow && idleT > nextFidget) _startFidget();
            shiftT -= dt;
            if (shiftT <= 0) { shiftTgt = -shiftTgt; shiftT = 3.2f + rng() * 4; }
        }

        void _startFidget()
        {
            int id = (int)(rng() * FIDGETS.Length);
            if (id == lastFidget) id = (id + 1 + (int)(rng() * 3)) % FIDGETS.Length;
            // a pistol in each fist: no free hand for the goggles / tank taps
            if ((dual || weapon?.pivotL != null) && (FIDGETS[id] == "goggles" || FIDGETS[id] == "tank")) id = Array.IndexOf(FIDGETS, rng() < 0.5f ? "twirl" : "look");
            fidget = id; lastFidget = id; fidgetT = 0;
        }

        // Kid ⇄ squid as one continuous elastic gesture (the ink splash itself is FX):
        //   kid → squid: the kid dips (anticipation), flattens into a puddle of ink and is gone; the squid rises out of that
        //                puddle, stretches tall past rest and wobbles to a stop.
        //   squid → kid: the squid crouches, shoots up thin and pops; the kid springs out of it tall and thin, lands in a
        //                squash and wobbles to rest. Rapid toggles start the new gesture where the current shapes are.
        void _updateFormScales(float dt)
        {
            bool toKid = form == "kid", fromKid = formPrev == "kid";
            float ft = formT;
            float kU = toKid ? 1 : 0, kY = 1, kXZ = 1, sU = toKid ? 0 : 1, sY = 1, sXZ = 1, lift = 0;
            if (toKid && !fromKid && ft < 0.45f)
            {
                sU = ft < 0.06f ? 1 : 1 - easeIn((ft - 0.06f) / 0.035f);
                sY = kc(ft, K_EM_ST, K_EM_SY); sXZ = kc(ft, K_EM_ST, K_EM_SX);
                kU = ft < 0.045f ? 0 : easeOut((ft - 0.045f) / 0.055f);
                kY = kc(ft, K_EM_KT, K_EM_KY); kXZ = kc(ft, K_EM_KT, K_EM_KX);
                lift = (formPrev == "swim" ? -0.3f : formPrev == "climb" ? -0.05f : -0.12f) * (1 - easeOut((ft - 0.045f) / 0.1f));
            }
            else if (!toKid && fromKid && ft < 0.45f)
            {
                kY = kc(ft, K_DV_KT, K_DV_KY); kXZ = kc(ft, K_DV_KT, K_DV_KX);
                kU = ft < 0.07f ? 1 : 1 - easeIn((ft - 0.07f) / 0.03f);
                sU = ft < 0.045f ? 0 : easeOut((ft - 0.045f) / 0.035f);
                sY = kc(ft, K_DV_ST, K_DV_SY); sXZ = kc(ft, K_DV_ST, K_DV_SX);
            }
            kidScale = kU; sqScale = sU; kidSY = kY; kidSXZ = kXZ; sqSY = sY; sqSXZ = sXZ; kidLift = lift;
            kidPop = toKid ? kU : 0;
            kid.gameObject.SetActive(kU > 0.001f);
            squidRoot.gameObject.SetActive(sU > 0.001f);
        }

        /** A form change arriving mid-gesture: enter the new timeline at the point that matches what is on screen now. */
        void _formEnter(string newForm)
        {
            float wasKidT = formT; string prev = form;
            formPrev = prev; form = newForm; formT = 0; tr[T_FORM] = 0;
            if (wasKidT < 0.1f)
            {
                if (newForm == "kid" && prev != "kid") formT = clamp(0.06f + 0.035f * (1 - sqScale), 0, 0.095f) * (sqScale < 0.999f ? 1 : 0);
                else if (newForm != "kid" && prev == "kid") formT = kidScale < 0.999f ? clamp(0.07f + 0.03f * (1 - kidScale), 0, 0.1f) : 0;
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Stepping: world-locked plants, phase-driven gait, settle / catch-up steps
        // ---------------------------------------------------------------------------------------------------------
        float _ground(float x, float z, ref Vector3 n)
        {
            float ry = root.localPosition.y;
            if (phys != null)
            {
                var h = phys.raycast(new Vector3(x, ry + 0.55f, z), DOWN, 1.25f, _gHit, false);
                if (h.hit && h.normal.y > 0.55f)
                {
                    float y = h.point.y;
                    if (y > ry - 0.55f && y < ry + 0.52f) { n = h.normal; return y; }
                }
            }
            n = Vector3.up;
            return ry;
        }
        float _ground(float x, float z) { Vector3 dummy = default; return _ground(x, z, ref dummy); }

        /** Ideal standing plant (world) for a foot under the current body; returns the foot's world yaw. */
        float _idealFoot(Foot f, out Vector3 o)
        {
            var st = stance; Vector3 R = root.localPosition;
            float kx = (f.side > 0 ? st[0] : st[3]) + stepOfsX, kz = (f.side > 0 ? st[1] : st[4]) + stepOfsZ;
            float c = Mathf.Cos(yaw), sn = Mathf.Sin(yaw);
            o = new Vector3(R.x + kx * c + kz * sn, R.y, R.z - kx * sn + kz * c);
            return yaw + (f.side > 0 ? st[2] : st[5]);
        }

        /** Predicted gait landing (world) for a swing that touches down in tRem seconds. */
        float _gaitTarget(Foot f, float tRem, out Vector3 o)
        {
            Vector3 R = root.localPosition;
            float stHalf = (duty / cad) * 0.5f;
            float yawP = yaw + yawRate * Mathf.Min(tRem, 0.25f) * 0.8f;
            float hy = yawP + hipTwist;
            float w = lerp(Mathf.Abs(stance[f.side > 0 ? 0 : 3]), 0.068f, runW) * f.side;
            float c = Mathf.Cos(hy), sn = Mathf.Sin(hy);
            float ta = Mathf.Min(tRem, 0.15f), ax = tread ? 0 : clamp(ra.x, -40, 40), az = tread ? 0 : clamp(ra.z, -40, 40);
            float px = R.x + gvx * tRem + 0.5f * ax * ta * ta, pz = R.z + gvz * tRem + 0.5f * az * ta * ta;
            float dx = gvx * stHalf + w * c, dz = gvz * stHalf - w * sn;
            float dl = Mathf.Sqrt(dx * dx + dz * dz); const float mx = 0.3f;
            if (dl > mx) { dx *= mx / dl; dz *= mx / dl; }
            o = new Vector3(px + dx, 0, pz + dz);
            o.y = _ground(o.x, o.z, ref f.tn);
            // foot points along the hips (toe-out grows a little when walking)
            return hy + f.side * lerp(0.1f, 0.04f, runW);
        }

        void _liftOff(Foot f, int mode)
        {
            f.planted = false; f.sw = true; f.mode = mode; f.su = 0;
            f.from = f.pw; f.fromYaw = f.yaw; f.to = f.pw; f.toYaw = f.yaw;
            float fwd = clamp(mdz * Mathf.Cos(hipTwist) + mdx * Mathf.Sin(hipTwist), -1, 1);
            f.lift = liftH * (mode == M_CATCH ? 0.7f : 1);
            f.toe = lerp(0.3f, 0.95f, runW) * (fwd >= 0 ? fwd : fwd * 0.55f);
            f.land = lerp(0.3f, 0.1f, runW) * (fwd >= 0 ? fwd : fwd * 0.6f);
            f.dur = mode == M_CATCH ? 0.13f : (1 - duty) / cad;
        }

        void _touchDown(Foot f, float loud)
        {
            f.pw = f.to; f.n = f.tn; f.yaw = f.toYaw;
            f.planted = true; f.sw = false; f.stU = 0; f.su = 0; f.stT = 0;
            // off-beat touchdown (first step, catch-up): re-sync the gait clock to this foot so the other one follows in rhythm
            if (moving && f.mode == M_CATCH)
            {
                phase = -f.i * 0.5f; f.inSt = true;
                var o = feet[1 - f.i]; o.inSt = 0.5f < duty;
            }
            settleCd = 0.045f;
            if (onEvent != null && kidForm && visible && kidScale > 0.5f)
            {
                _fsData.pos = f.pw; _fsData.kind = f.name; _fsData.speed = loud * Mathf.Max(gv, 0.6f);
                onEvent("footstep", _fsData);
            }
        }

        void _startSettle(Foot f, float err)
        {
            f.planted = false; f.sw = true; f.mode = M_SETTLE; f.su = 0;
            f.from = f.pw; f.fromYaw = f.yaw;
            f.toYaw = _idealFoot(f, out f.to); f.to.y = _ground(f.to.x, f.to.z, ref f.tn);
            f.dur = clamp(0.15f + err * 0.42f, 0.15f, 0.3f);
            f.lift = clamp(0.028f + err * 0.22f, 0.03f, 0.085f) + Mathf.Max(0, f.to.y - f.from.y);
            f.toe = 0.25f; f.land = 0.12f;
        }

        float _footErr(Foot f)
        {
            float yawI = _idealFoot(f, out var v6);
            return Mathf.Sqrt((f.pw.x - v6.x) * (f.pw.x - v6.x) + (f.pw.z - v6.z) * (f.pw.z - v6.z)) + 0.11f * Mathf.Abs(wrapA(f.yaw - yawI));
        }

        void _updateFeet(float dt, AnimState s)
        {
            var F = feet; Vector3 R = root.localPosition;
            bool plantOK = kidForm && grounded && dance == null && tr[T_LEAP] > 1.9f && tr[T_SLAM] > 1.4f && tr[T_DODGE] > dodgeDur * 0.86f;
            // treadmill: the ground (and everything planted on it) slides back under a stationary root
            if (tread) for (int i = 0; i < 2; i++) { var f = F[i]; Vector3 d = new Vector3(tvx * dt, 0, tvz * dt); f.pw -= d; f.from -= d; f.disp -= d; }
            if (!plantOK)
            {
                moving = false; replant = true;
                for (int i = 0; i < 2; i++) F[i].sw = false;
                plantW = damp(plantW, 0, 30, dt);
                _footPose(F[0]); _footPose(F[1]);
                return;
            }
            plantW = damp(plantW, 1, 14, dt);
            if (replant || !feetValid)
            {
                for (int i = 0; i < 2; i++)
                {
                    var f = F[i];
                    float fy;
                    if (feetValid && f.dispOK) { f.pw = f.disp; fy = f.dispYaw; } else fy = _idealFoot(f, out f.pw);
                    f.pw.y = _ground(f.pw.x, f.pw.z, ref f.n);
                    f.yaw = fy; f.planted = true; f.sw = false; f.su = 0; f.stU = 0.5f; f.inSt = true;
                }
                if (!feetValid) plantW = 1;
                replant = false; feetValid = true; settleCd = 0.06f; moving = false;
            }
            bool was = moving;
            moving = was ? gv > 0.3f : gv > 0.62f;
            if (moving)
            {
                if (!was)
                {
                    // start: the foot most "behind" the travel direction takes a quick, short first step while the other
                    // pushes off; the gait clock re-syncs to that first touchdown (see _touchDown)
                    float gl = gv > 0 ? gv : 1;
                    float dx = gvx / gl, dz = gvz / gl;
                    float bL = (F[0].pw.x - R.x) * dx + (F[0].pw.z - R.z) * dz, bR = (F[1].pw.x - R.x) * dx + (F[1].pw.z - R.z) * dz;
                    int first = bL <= bR ? 0 : 1;
                    phase = duty + 1e-3f - first * 0.5f;
                    if (!F[first].sw) { _liftOff(F[first], M_CATCH); F[first].dur = 0.15f; F[first].lift *= 0.8f; }
                    F[first].inSt = false; F[1 - first].inSt = frac(phase + (1 - first) * 0.5f) < duty;
                }
                phase += cad * dt;
                for (int i = 0; i < 2; i++)
                {
                    var f = F[i]; var o = F[1 - i];
                    float p = frac(phase + i * 0.5f);
                    bool inSt = p < duty;
                    bool wasSt = f.inSt; f.inSt = inSt;
                    if (f.planted)
                    {
                        f.stU = inSt ? p / duty : 1;
                        // lift on the stance→swing edge; re-sync when the clock says "swing" while the other foot carries the
                        // body; catch up when the body has run away from this foot
                        float hx = f.pw.x - R.x, hz = f.pw.z - R.z;
                        bool far = hx * hx + hz * hz > 0.37f * 0.37f;
                        if (wasSt && !inSt) _liftOff(f, M_GAIT);
                        else if (!inSt && o.planted && f.stT > 0.06f) { _liftOff(f, M_CATCH); f.dur = clamp((1 - p) / cad, 0.09f, 0.24f); }
                        else if (far) _liftOff(f, M_CATCH);
                        f.stT += dt;
                    }
                    if (f.sw)
                    {
                        if (f.mode == M_GAIT)
                        {
                            float u = inSt ? 1 : (p - duty) / (1 - duty);
                            f.su = Mathf.Max(f.su, Mathf.Min(1, u));
                            float tRem = inSt ? 0 : (1 - p) / cad;
                            float yawT = _gaitTarget(f, tRem, out var v1);
                            float k = f.su > 0.82f ? 1 : 1 - Mathf.Exp(-dt * 28);
                            f.to = Vector3.Lerp(f.to, v1, k); f.toYaw = yawT;
                            if (f.su >= 1) _touchDown(f, 1);
                        }
                        else
                        {
                            f.su = Mathf.Min(1, f.su + dt / f.dur);
                            f.toYaw = _gaitTarget(f, (1 - f.su) * f.dur, out f.to);
                            if (f.su >= 1) _touchDown(f, 0.8f);
                        }
                    }
                }
            }
            else
            {
                // standing: finish any step in flight onto the stance, then settle the worst foot (stops, turns in place)
                int swinging = 0;
                for (int i = 0; i < 2; i++)
                {
                    var f = F[i];
                    if (!f.sw) continue;
                    if (f.mode != M_SETTLE)
                    {
                        float rem = clamp((1 - f.su) * (f.mode == M_GAIT ? (1 - duty) / Mathf.Max(cad, 1.5f) : f.dur), 0.09f, 0.22f);
                        f.mode = M_SETTLE; f.dur = rem / Mathf.Max(0.05f, 1 - f.su);
                        f.lift = Mathf.Max(f.lift * 0.8f, 0.04f);
                    }
                    f.su = Mathf.Min(1, f.su + dt / f.dur);
                    f.toYaw = _idealFoot(f, out f.to); f.to.y = _ground(f.to.x, f.to.z, ref f.tn);
                    if (f.su >= 1) _touchDown(f, 0.45f); else swinging++;
                }
                settleCd -= dt;
                if (swinging == 0 && settleCd <= 0)
                {
                    float eL = _footErr(F[0]), eR = _footErr(F[1]);
                    int b = eL >= eR ? 0 : 1; float e = Mathf.Max(eL, eR);
                    if (e > 0.068f) _startSettle(F[b], e);
                }
            }
            _footPose(F[0]); _footPose(F[1]);
            // how far the planted feet are turned relative to the body (drives the hip counter-twist when turning in place)
            float tL = wrapA(F[0].cyaw - (yaw + stance[2])), tR = wrapA(F[1].cyaw - (yaw + stance[5]));
            footTwist = damp(footTwist, clamp((tL + tR) * 0.5f, -1.2f, 1.2f), 20, dt);
        }

        /** Current world contact, yaw and pitch of a foot (planted or mid-swing). */
        void _footPose(Foot f)
        {
            if (f.planted || !f.sw)
            {
                f.cw = f.pw; f.cyaw = f.yaw; f.cn = f.n;
                if (moving)
                {
                    float st = f.stU;
                    f.pitch = -f.land * (1 - sstep(0, 0.28f, st)) + f.toe * sstep(0.42f, 1, st);
                }
                else f.pitch = damp(f.pitch, 0, 12, 1 / 60f);
                return;
            }
            float u = f.su;
            float e = lerp(u, mj(u), 0.8f);
            f.cw.x = lerp(f.from.x, f.to.x, e); f.cw.z = lerp(f.from.z, f.to.z, e);
            float rise = Mathf.Max(0, f.to.y - f.from.y);
            float gy = lerp(f.from.y, f.to.y, sstep(0.15f, 0.7f, u));
            float peak = lerp(0.5f, 0.4f, runW);
            float lc = u < peak ? Mathf.Sin((u / peak) * Mathf.PI * 0.5f) : Mathf.Cos(((u - peak) / (1 - peak)) * Mathf.PI * 0.5f);
            f.cw.y = gy + (f.lift + rise * 0.6f) * Mathf.Pow(Mathf.Max(0, lc), 1.15f);
            f.cyaw = f.fromYaw + wrapA(f.toYaw - f.fromYaw) * e;
            f.cn = Vector3.Lerp(f.n, f.tn, e);
            f.pitch = f.toe * (1 - sstep(0, 0.5f, u)) - f.land * sstep(0.55f, 0.96f, u) + 0.12f * Mathf.Sin(Mathf.PI * u) * runW;
        }

        /** The actor's WeaponRunner (read-only: charging / lockT …). */
        WeaponRunner _runner() => _owner()?.weaponRunner;

        /** The actor that owns this character (read-only: team, sub-weapon aim). */
        Actor _owner()
        {
            if (actor != null && actor.character == this) return actor;
            if (!inWorld) return null;
            if (_ownT > t) return null;
            _ownT = t + 1; actor = null;
            foreach (var a in G.actors) if (a.character == this) { actor = a; break; }
            return actor;
        }
    }
}
