// Port of src/game/character.js — the life layers: facial expression tables, game-bus reactions, attention (head look
// + gaze point), breathing, micro-expressions, blinks, expression mixing and the dances / showcase poses.
using UnityEngine;
using static Inkwave.Ctx;

namespace Inkwave
{
    public partial class Character
    {
        // facial expressions: [BROW, BROWY, EYE, MCURVE, MWIDTH, MOPEN, MTILT, SQUINT] deltas from neutral
        static readonly float[] X_FOCUS = { -0.38f, -0.25f, -0.08f, -0.5f, -0.18f, 0, 0, 0.25f };
        static readonly float[] X_GRIN = { -0.2f, 0, -0.05f, 0.3f, 0.12f, 0.22f, 0, 0.2f };
        static readonly float[] X_EFFORT = { -0.62f, -0.35f, -0.22f, -0.35f, 0.22f, 0.14f, 0, 0.45f };
        static readonly float[] X_WINCE = { 0.85f, 0.1f, -0.62f, -1.9f, -0.1f, 0.42f, 0.18f, 0.7f };
        static readonly float[] X_WORRY = { 0.62f, 0.45f, 0.08f, -1.1f, -0.28f, 0.1f, 0, 0 };
        static readonly float[] X_DETERM = { -0.45f, -0.1f, -0.06f, 0.2f, 0.05f, 0.05f, 0.16f, 0.2f };
        static readonly float[] X_SURPRISE = { 0.25f, 0.9f, 0.18f, -0.75f, -0.42f, 0.55f, 0, 0 };
        static readonly float[] X_TIRED = { 0.5f, -0.1f, -0.2f, -0.9f, -0.2f, 0.22f, 0, 0.1f };
        static readonly float[] X_JOY = { 0.1f, 0.7f, -0.12f, 0.25f, 0.12f, 0.72f, 0, 0.35f };
        static readonly float[] X_POUT = { 0.75f, -0.3f, -0.45f, -1.65f, -0.35f, 0, 0.12f, 0.2f };
        // micro-expressions (short impulses from events / idle flickers, see _mxPlay): same channel order
        static readonly float[] X_BROWFLASH = { -0.1f, 0.85f, 0.1f, 0.25f, 0.05f, 0.04f, 0, 0 };         // eye contact / attention: quick brow lift
        static readonly float[] X_GLOAT = { -0.35f, 0.4f, -0.1f, 1.0f, 0.18f, 0.28f, 0.32f, 0.35f };       // splatted someone: lopsided grin
        static readonly float[] X_OOF = { 0.7f, 0.35f, 0.1f, -1.3f, -0.3f, 0.55f, -0.1f, 0.35f };          // big hit / an ally went down
        static readonly float[] X_LIPPRESS = { -0.12f, -0.08f, 0, -0.45f, -0.32f, 0, 0, 0.06f };          // idle: lips pressed, thinking
        static readonly float[] X_SMIRK = { -0.08f, 0.12f, -0.04f, 0.35f, 0.06f, 0, 0.42f, 0.12f };       // idle: half smile (MTILT sign = side)
        static readonly float[] X_HMM = { 0.2f, 0.15f, 0, -0.25f, -0.22f, 0.05f, -0.2f, 0 };              // idle: mouth pulled to one side
        static readonly float[] X_SOFTSMILE = { -0.05f, 0.18f, -0.06f, 0.45f, 0.1f, 0.06f, 0, 0.22f };    // at the viewer: warm little smile
        static readonly float[] X_HUP = { -0.3f, 0.3f, 0.06f, 0.1f, 0.06f, 0.3f, 0, 0.1f };               // jump: "hup!"
        static readonly float[] X_EFFORTP = { -0.5f, -0.2f, -0.12f, -0.3f, 0.2f, 0.2f, 0, 0.4f };         // heave / release: clenched effort
        static readonly float[] X_SIGH = { 0.3f, 0.1f, -0.35f, -0.2f, -0.1f, 0.22f, 0, 0 };               // deep breath out
        static readonly float[] E_GLOAT = { 0.55f, 0, 0.2f }, E_HMM = { 0, 0.45f, 0 }, E_BLUSH = { 0, 0, 0.45f };   // extras: [sneer, pucker, blush]
        static int[] _xch;
        static int[] XCH => _xch ??= new[] { BROW, BROWY, EYE, MCURVE, MWIDTH, MOPEN, MTILT, SQUINT };
        static readonly Vector3 EYE_MID = new Vector3(0, 0.188f, 0.15f);   // between the eyes, head-bone space

        /** Lid closure of a blink `b` (close c · hold h · open o, seconds) at time t: accelerating close, quick-start open. */
        static float blinkCurve(Blink b, float t)
        {
            if (t <= 0) return 0;
            if (t < b.c) { float u = t / b.c; return u * u; }
            t -= b.c; if (t < b.h) return 1;
            t -= b.h; if (t < b.o) return Mathf.Pow(1 - t / b.o, 2.2f);
            return 0;
        }

        // game-bus reactions (one subscription for all kids): splatting someone → a lopsided grin; an ally going down
        // nearby → a wince. Pure face acting — no gameplay coupling.
        static System.Action _busOff;
        public static void hookBus()
        {
            _busOff?.Invoke();
            _busOff = on("splatted", e =>
            {
                var att = e.attacker as Actor; var vic = e.victim as Actor;
                var k = att?.character as Character;
                if (k != null && vic != null && k != vic.character && k.kidForm) { k._mxPlay(X_GLOAT, 0.95f, 0.14f, 0.7f + k.rng() * 0.4f, 0.6f, 0.35f, E_GLOAT); k.sp[S_EARL + 1] += 3; k.sp[S_EARR + 1] += 3; }
                if (vic?.character is not Character vc) return;
                foreach (var c in LIVE)
                {
                    var a = c.actor;
                    if (a == null || a == vic || a.team != vic.team || !c.inWorld || !c.kidForm) continue;
                    if ((c.root.localPosition - vc.root.localPosition).sqrMagnitude < 14 * 14) c._mxPlay(X_OOF, 0.4f, 0.1f, 0.35f, 0.6f);
                }
            });
        }
        static void addExpr(float[] P, float[] X, float w) { if (w <= 0.001f) return; var xc = XCH; for (int i = 0; i < 8; i++) P[xc[i]] += X[i] * w; }

        // Attention (what the kid looks at) → a root-space gaze point for the eyes (_applyFace saccades to it) + head
        // yaw/pitch targets for the stabilised head (springs: the eyes lead, the head follows with a slight overshoot).
        // In a match: the aim / nearby actors (enemies first) / the attacker right after a hit / into turns and the
        // travel direction / idle glances. Out of one (showcase, locker, lobby, podium): see _attendMenu.
        void _poseLook(float dt, AnimState s)
        {
            var A = att;
            float ty = 0, tp = 0;
            if (!inWorld) _attendMenu(dt);
            else
            {
                // pick something interesting to look at every so often (enemies > allies > travel direction > idle glances)
                lookT -= dt;
                if (lookT <= 0)
                {
                    lookT = 0.9f + rng() * 2.2f;
                    lookActor = null;
                    if (G.actors.Count > 0) _pickLook();
                    glanceYaw = (rng() - 0.5f) * (idleT > 1 ? 1.1f : 0.4f);
                    glancePitch = (rng() - 0.5f) * 0.25f;
                }
                bool actorGaze = false;
                if (wAim > 0.5f) { ty = 0; tp = aimP * 0.85f; }
                else
                {
                    var la = lookActor;
                    if (la != null && la.alive)
                    {
                        Vector3 R = root.localPosition;
                        float dx = la.pos.x - R.x, dz = la.pos.z - R.z, dy = (la.pos.y + (la.form == "squid" ? 0.3f : 1.15f)) - (R.y + 1.2f);
                        float c = Mathf.Cos(yaw), sn = Mathf.Sin(yaw);
                        float kx = dx * c - dz * sn, kz = dx * sn + dz * c;
                        ty = Mathf.Atan2(kx, kz); tp = Mathf.Atan2(dy, Mathf.Sqrt(kx * kx + kz * kz));
                        if (Mathf.Abs(ty) > 1.9f) { ty = glanceYaw; tp = glancePitch; }
                        else { A.p = new Vector3(kx, dy + 1.2f, kz); actorGaze = true; }
                    }
                    else { ty = glanceYaw * (1 - gaitW * 0.7f); tp = glancePitch; }
                    // look into turns / along the travel direction when moving
                    ty = lerp(ty, clamp(Mathf.Atan2(mdx, Mathf.Max(mdz, 0.2f)) * 0.5f, -0.6f, 0.6f), gaitW * 0.6f);
                    ty += clamp(yawRate * 0.1f, -0.35f, 0.35f) * gaitW;
                    tp = lerp(tp, aimP * 0.6f, 0.5f);
                    if (gaitW > 0.5f) actorGaze = false;
                }
                // hit: a flinching glance toward whoever it came from (eyes snap there, the head turns partway)
                float hg = tr[T_HIT] < 1.1f ? win(tr[T_HIT], 0.02f, 0.08f, 0.55f, 1.1f) * clamp(hitAmp, 0.5f, 1) : 0;
                if (hg > 0.01f) { ty = lerp(ty, clamp(Mathf.Atan2(hitX, hitZ), -1.3f, 1.3f), 0.65f * hg); tp = lerp(tp, 0.05f, hg); actorGaze = false; }
                ty = clamp(ty, -1.1f, 1.1f); tp = clamp(tp, -0.6f, 0.55f);
                // gaze point: the actor's head, else 8 m out along (ty, tp) from the head (root space)
                if (!actorGaze) { float cp = Mathf.Cos(tp); A.p = new Vector3(Mathf.Sin(ty) * cp * 8, 1.2f + Mathf.Sin(tp) * 8, Mathf.Cos(ty) * cp * 8); }
                A.on = true; A.kind = K_NONE;
            }
            // stabilised head: springs toward (ty, tp) — in menus the head is turned post-dance in _lifePost instead
            float hy = spr(sp, S_HLY, clamp(ty, -0.85f, 0.85f), 2.4f, 0.62f, dt);
            float hpp = spr(sp, S_HLP, clamp(tp, -0.5f, 0.45f), 2.6f, 0.62f, dt);
            P[HLY] += hy; P[HLP] += hpp;
            P[NECK + 1] += hy * 0.3f; P[NECK] -= hpp * 0.2f;
            // pose-driven eye direction (used when there is no gaze point, e.g. scripted dance looks)
            P[LOOKX] += clamp((ty - hy) * 0.9f, -0.36f, 0.36f);
            P[LOOKY] += clamp((tp - hpp) * 0.8f + 0.02f, -0.3f, 0.3f);
            // head reactions (hits / landings) ride on top of the stabilised look
            float hp0 = spr(sp, S_HEADP, 0, 3.2f, 0.4f, dt), hr = spr(sp, S_HEADR, 0, 3.2f, 0.4f, dt);
            P[HEAD] += hp0 * 0.5f; P[HEAD + 2] += hr * 0.4f; P[HLP] -= hp0 * 0.4f;
            // turn / lean compensation: the head rolls less than the body (stabiliser), a little into the turn
            P[HEAD + 2] -= clamp(yawRate * gs * 0.006f, -0.12f, 0.12f);
        }

        // Out of a match the kid mostly looks at the viewer (the camera it is drawn with), with glances at a neighbour
        // and idle glances away. Dances bias it (victory: the viewer; defeat: mostly away).
        bool _camOK; Vector3 _camPos;   // menus (M6): the camera the kid is drawn with, three space
        Vector3 rootLocal(Vector3 p) => Quaternion.Inverse(root.localRotation) * (p - root.localPosition);
        void _attendMenu(float dt)
        {
            var A = att;
            A.t -= dt;
            if (A.t <= 0)
            {
                string d = dance; float r = rng();
                float viewer = d == "defeat" ? 0.15f : d == "victory" ? 0.85f : 0.6f;
                if (!_camOK) viewer = 0;
                var nb = _neighbour();
                A.prev = A.kind;
                if (r < viewer) { A.kind = K_VIEWER; A.t = 2.2f + rng() * 3.2f; }
                else if (nb != null && r < viewer + 0.24f) { A.kind = K_NEIGHBOUR; A.who = nb; A.t = 0.9f + rng() * 1.7f; }
                else { A.kind = K_GLANCE; A.gy = (rng() - 0.5f) * 1.1f; A.gp = d == "defeat" ? -0.35f - rng() * 0.2f : -0.04f - rng() * 0.3f; A.t = 0.6f + rng() * 1.1f; }
                // eye contact: a little brow flash + warm smile now and then
                if (A.kind == K_VIEWER && A.prev != K_VIEWER && d != "defeat")
                {
                    if (rng() < 0.45f) _mxPlay(X_BROWFLASH, 0.6f, 0.07f, 0.12f, 0.35f);
                    if (rng() < 0.5f) _mxPlay(X_SOFTSMILE, 0.8f, 0.25f, 1.2f + rng(), 0.8f, 0, E_BLUSH);
                }
            }
            A.on = true;
            if (A.kind == K_VIEWER && _camOK) A.p = rootLocal(_camPos);
            else if (A.kind == K_NEIGHBOUR && A.who != null && A.who.visible && A.who.root.parent != null && A.who.kidForm) A.p = rootLocal(A.who.getHeadPosition());
            else if (A.kind == K_GLANCE) { float cp = Mathf.Cos(A.gp); A.p = new Vector3(Mathf.Sin(A.gy) * cp * 4, 1.15f + Mathf.Sin(A.gp) * 4, Mathf.Cos(A.gy) * cp * 4); }
            else A.on = false;
            // the showcase is turning our head toward someone: the eyes lead that way instead
            if (Mathf.Abs(extGl) > 0.06f) A.on = false;
        }

        /** Nearest other visible kid sharing our parent (≤ 4 m) — the lobby line-up / podium neighbour to glance at. */
        Character _neighbour()
        {
            var par = root.parent; if (par == null) return null;
            Character best = null; float bd = 16;
            foreach (var c in LIVE)
            {
                if (c == this || c.root.parent != par || !c.visible || !c.go.activeSelf) continue;
                float d = (c.root.localPosition - root.localPosition).sqrMagnitude;
                if (d < bd && d > 0.04f) { bd = d; best = c; }
            }
            return best;
        }

        // Post-dance life layer (runs after every pose layer incl. dances, so menus get it too): micro-expression impulses
        // + idle flickers, the menu head turn toward the attention target, pupils.
        int _mxLast = -1;
        void _lifePost(float dt)
        {
            var A = att;
            if (lifeLv == 0) return;
            _breathe(P);
            // ---- head follows the gaze partway out of a match (the eyes do the rest); a showcase glance owns the head
            float wy = 0, wp = 0;
            if (!inWorld && A.on && kidForm && tr[T_SPAWN] > 1.4f)
            {
                string d = dance;
                float hw = (d == "victory" ? 0.3f : d == "defeat" ? 0.25f : d != null ? 0.55f : 0.75f) * (1 - clamp(Mathf.Abs(extGl) * 8, 0, 1)) * (1 - wAir);
                float curY = P[MODELR + 1] + P[HIPS + 1] + P[SPINE + 1] + P[CHEST + 1] + P[NECK + 1] + P[HEAD + 1] + P[HLY];
                float curP = P[HLP] - (P[NECK] + P[HEAD] + 0.5f * (P[CHEST] + P[SPINE]));
                float yT = Mathf.Atan2(A.p.x, A.p.z), pT = Mathf.Atan2(A.p.y - 1.2f, Mathf.Max(0.3f, Mathf.Sqrt(A.p.x * A.p.x + A.p.z * A.p.z)));
                wy = clamp(wrapA(yT - curY), -0.85f, 0.85f) * hw; wp = clamp(pT - curP, -0.35f, 0.35f) * hw * 0.6f;
            }
            float ay = spr(sp, S_ATTY, wy, 1.5f, 0.78f, dt), ap = spr(sp, S_ATTP, wp, 1.6f, 0.8f, dt);
            P[HLY] += ay; P[HLP] += ap; P[NECK + 1] += ay * 0.25f;
            // ---- idle micro-expressions: a lip press, a half smile, one brow up, a "hmm" — never twice the same in a row
            bool calm = kidForm && !moving && wAim < 0.2f && tr[T_HIT] > 1.5f && (dance == null || dance == "menu_idle" || dance == "locker_idle" || dance == "lobby_pose");
            mxT -= dt;
            if (mxT <= 0 && lifeLv < 2) mxT = 1 + rng() * 2;   // small on screen: no idle flickers
            else if (mxT <= 0)
            {
                mxT = (inWorld ? 4 : 2.6f) + rng() * 4;
                if (calm)
                {
                    int k = (int)(rng() * 5); if (k == _mxLast) k = (k + 1) % 5; _mxLast = k;
                    float sd = rng() < 0.5f ? 1 : -1, hold0 = 0.35f + rng() * 0.9f;
                    if (k == 0) _mxPlay(X_LIPPRESS, 0.7f, 0.14f, hold0, 0.4f);
                    else if (k == 1) _mxPlay(X_SMIRK, 0.8f * sd, 0.18f, hold0, 0.5f);
                    else if (k == 2) _mxPlay(X_BROWFLASH, 0.25f, 0.2f, hold0, 0.5f, 0.55f * sd);
                    else if (k == 3) _mxPlay(X_HMM, 0.7f * sd, 0.16f, hold0, 0.45f, 0, E_HMM);
                    else _mxPlay(X_SOFTSMILE, 0.6f, 0.3f, hold0 + 0.6f, 0.7f);
                }
            }
            _mxTick(P, dt);
            // ---- blush: warm flush after exertion, in victory, on locker compliments
            P[BLUSH] += 0.4f * exert + 0.2f * wTired + (dance == "victory" ? 0.5f : 0) + 0.45f * Mathf.Max(win(tr[T_ADMIRE], 0, 0.3f, 1.2f, 1.6f), win(tr[T_WINK], 0, 0.1f, 0.5f, 0.8f));
            // ---- pupils: wide when excited / meeting the viewer's eyes, narrower when focused on a target
            P[PUPIL] = clamp(P[PUPIL] + 0.22f * xw[8] + 0.2f * wGlow - 0.16f * wAim + (A.kind == K_VIEWER && !inWorld ? 0.12f : 0) + (dance == "victory" ? 0.2f : 0), 0, 1);
        }

        // Breathing: inhale (~40 %) → exhale (~45 %) → a short pause; each breath a little different in depth and length;
        // now and then a deep sigh when idle. The rib cage lifts and opens, the head counters so it stays level.
        void _breathe(float[] P)
        {
            float bu = frac(brPh), bn = Mathf.Floor(brPh);
            if (bn != brN)
            {
                brN = bn; brAmp = 0.8f + 0.4f * rng(); brRate = 0.88f + 0.24f * rng();
                if (sigh > 0) sigh = 0;
                else if (idleT > 3 || (!inWorld && wDance > 0.5f && dance != "victory")) { sighT -= 1; if (sighT <= 0) { sighT = 5 + rng() * 6; sigh = 1; } }
            }
            float bb = bu < 0.4f ? ease(bu / 0.4f) : bu < 0.85f ? 1 - ease((bu - 0.4f) / 0.45f) : 0;   // 0 empty … 1 full
            float idleW = (1 - gaitW) * (1 - wAir);
            float brA = lerp(1, 2.3f, Mathf.Max(exert, wTired)) * brAmp * (sigh > 0 ? 2.3f : 1) * (kidForm ? 1 : 0);
            float br = (bb * 2 - 1) * brA;
            P[CHEST] -= 0.034f * br; P[SPINE] -= 0.012f * br;
            P[CLAVL + 2] += 0.032f * br; P[CLAVR + 2] -= 0.032f * br; P[CLAVL] -= 0.012f * br; P[CLAVR] -= 0.012f * br;
            P[UARML + 2] += 0.008f * br; P[UARMR + 2] -= 0.008f * br;
            P[HIPS_P + 1] -= 0.003f * br * idleW;
            P[HEAD] += 0.016f * br; P[NECK] += 0.004f * br;
            P[ANC + 1] += 0.003f * br * wDance;
            P[BREATH] = bb;
            if (sigh > 0 && bu > 0.4f)
            {
                float k = win(bu, 0.4f, 0.5f, 0.75f, 0.95f);
                P[MOPEN] = Mathf.Max(P[MOPEN], 0.2f * k); P[EYE] -= 0.25f * k; P[BROW] += 0.2f * k; P[MCURVE] -= 0.2f * k;
                P[CLAVL + 2] -= 0.03f * k; P[CLAVR + 2] += 0.03f * k;
            }
        }

        /** Start a micro-expression impulse: X = expression vector, w = weight (negative flips MTILT: the other side),
         *  a/h/d = attack/hold/decay (s), bras = brow asymmetry (+ left brow up). Reuses the weakest of 6 slots. */
        void _mxPlay(float[] X, float w, float a = 0.1f, float h = 0.2f, float d = 0.4f, float bras = 0, float[] E = null)
        {
            Mx best = mx[0]; float bw = float.PositiveInfinity;
            foreach (var m in mx) { float r = m.t > m.a + m.h + m.d ? -1 : m.w * m.w; if (r < bw) { bw = r; best = m; } }
            best.X = X; best.w = w; best.t = 0; best.a = a; best.h = h; best.d = d; best.bras = bras; best.E = E;
        }
        void _mxTick(float[] P, float dt)
        {
            var xc = XCH;
            foreach (var m in mx)
            {
                if (m.X == null) continue;
                m.t += dt;
                float e = m.a + m.h + m.d;
                if (m.t >= e) { m.X = null; continue; }
                float k = m.t < m.a ? ease(m.t / m.a) : m.t < m.a + m.h ? 1 : 1 - ease((m.t - m.a - m.h) / m.d);
                float w = Mathf.Abs(m.w) * k, sg = m.w < 0 ? -1 : 1;
                for (int i = 0; i < 8; i++) P[xc[i]] += m.X[i] * w * (xc[i] == MTILT ? sg : 1);
                P[BRAS] += m.bras * k;
                if (m.E != null) { P[SNEER] += m.E[0] * w; P[PUCKER] += m.E[1] * w; P[BLUSH] += m.E[2] * w; }   // [sneer, pucker, blush]
            }
        }

        void _pickLook()
        {
            var me = _owner();
            Vector3 R = root.localPosition;
            float c = Mathf.Cos(yaw), sn = Mathf.Sin(yaw);
            Actor best = null; float bestS = 0;
            foreach (var a in G.actors)
            {
                if (a == me || !a.alive) continue;
                float dx = a.pos.x - R.x, dz = a.pos.z - R.z, d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d < 0.5f || d > 18) continue;
                float fz = (dx * sn + dz * c) / d;
                if (fz < -0.15f) continue;
                bool enemy = me != null && a.team != me.team;
                float sc = (enemy ? 2.2f : 1) * (0.6f + fz) / (1 + d * 0.25f) * (0.7f + rng() * 0.6f);
                if (sc > bestS) { bestS = sc; best = a; }
            }
            if (best != null && rng() < 0.85f) lookActor = best;
        }

        // Blinks: one clock, two lids. A blink = fast accelerating close (≈75 ms), a short hold, a slower decelerating
        // open (≈150 ms); the right lid trails by a few ms; some are partial; ~14 % come as doubles. Spontaneous
        // intervals are skewed-random around a state mean (focus/aim/charging suppress, exertion/tiredness raise);
        // saccades, hits and hard landings trigger their own (_blinkStart).
        float _turnBl;
        void _blinkTick(float dt)
        {
            var b = bl;
            b.next -= dt;
            // a big fast head / body turn (camera flick, snap turn) takes a blink with it, at most one per second
            _turnBl -= dt;
            if (Mathf.Abs(yawRate) > 5.5f && _turnBl <= 0 && b.t < 0 && kidForm) { _turnBl = 1.1f; if (rng() < 0.7f) _blinkStart(0.9f + 0.1f * rng(), false); }
            if (b.t < 0 && b.next <= 0) _blinkStart(rng() < 0.18f ? 0.55f + rng() * 0.3f : 1, false);
            float L = 0, R = 0;
            if (b.t >= 0)
            {
                b.t += dt;
                L = blinkCurve(b, b.t - (b.lag < 0 ? -b.lag : 0)) * b.amp; R = blinkCurve(b, b.t - (b.lag > 0 ? b.lag : 0)) * b.amp;
                if (b.t > b.c + b.h + b.o + Mathf.Abs(b.lag))
                {
                    b.t = -1; b.squeeze = 0;
                    if (b.dbl) { b.dbl = false; b.next = 0.05f + rng() * 0.05f; }
                    else
                    {
                        // ≈ 12–15 blinks/min at rest incl. gaze-evoked ones, far fewer when focused
                        float mean = inWorld ? 6.4f : 6.0f;
                        if (wAim > 0.5f) mean = 7.5f;
                        if (weaponKind == "charger" && charge > 0.3f) mean = 11;
                        mean = lerp(mean, 2.8f, Mathf.Max(exert, wTired));
                        float r = rng();
                        b.next = mean * (0.3f + 1.35f * Mathf.Pow(r, 1.7f));
                    }
                }
            }
            blinkL = L; blinkR = R; blinkK = Mathf.Max(L, R);
        }
        /** Start a blink now (amp 0..1; hard = a squeezed, held blink for hits). */
        void _blinkStart(float amp = 1, bool hard = false)
        {
            var b = bl;
            if (b.t >= 0 && b.t < b.c) return;    // already closing
            b.t = 0; b.amp = amp;
            b.c = 0.06f + rng() * 0.025f; b.h = hard ? 0.08f + rng() * 0.06f : 0.008f + rng() * 0.03f; b.o = 0.14f + rng() * 0.06f + (hard ? 0.05f : 0);
            b.lag = (rng() - 0.5f) * 0.024f; b.squeeze = hard ? 1 : 0;
            b.dbl = !hard && amp > 0.9f && rng() < 0.14f;
            b.next = 99;
        }

        // Facial animation: expression mixing from state (gameplay), mouth breathing.
        void _poseFace(float dt, AnimState s)
        {
            if (lifeLv == 0) { _effort = 0; blinkL = blinkR = blinkK = 0; return; }
            _blinkTick(dt);
            // expression weights (smoothed)
            float eff = _effort; _effort = 0;
            float hitK = tr[T_HIT] < 0.7f ? pulse(tr[T_HIT], 0.03f, 4.5f) * clamp(hitAmp, 0.5f, 1.2f) : 0;
            float firing = lastShot < 0.25f ? 1 : 0;
            float tgt0 = wAim * (1 - firing * 0.5f) * (animKind == "charger" ? 1 : 0.8f);
            float tgt1 = firing * (animKind == "shooter" ? 1 : 0.4f);
            float tgt2 = Mathf.Max(eff, animKind == "charger" ? charge * wAim : 0);
            float tgt3 = hitK;
            float tgt4 = wLow * (1 - hitK);
            float tgt5 = wGlow * (1 - wLow) * 0.9f;
            float tgt6 = tr[T_SPAWN] < 1.3f ? win(tr[T_SPAWN], 0, 0.05f, 0.5f, 0.9f) : 0;
            float tgt7 = Mathf.Max(wTired, wGoo * 0.7f);
            float tgt8 = tr[T_SPAWN] < 1.6f ? win(tr[T_SPAWN], 0.75f, 0.9f, 1.2f, 1.6f) : 0;
            const float rate = 12;
            xw[0] = damp(xw[0], tgt0, rate, dt); xw[1] = damp(xw[1], tgt1, rate, dt); xw[2] = damp(xw[2], tgt2, rate * 1.5f, dt);
            xw[3] = Mathf.Max(tgt3, damp(xw[3], tgt3, 10, dt)); xw[4] = damp(xw[4], tgt4, 6, dt); xw[5] = damp(xw[5], tgt5, 5, dt);
            xw[6] = damp(xw[6], tgt6, 20, dt); xw[7] = damp(xw[7], tgt7, 4, dt); xw[8] = damp(xw[8], tgt8, 8, dt);
            addExpr(P, X_FOCUS, xw[0] * (1 - xw[3])); addExpr(P, X_GRIN, xw[1] * (1 - xw[3])); addExpr(P, X_EFFORT, xw[2] * (1 - xw[3]));
            addExpr(P, X_WINCE, xw[3]); addExpr(P, X_WORRY, xw[4]); addExpr(P, X_DETERM, xw[5] * (1 - xw[0] * 0.5f));
            addExpr(P, X_SURPRISE, xw[6]); addExpr(P, X_TIRED, xw[7] * (1 - xw[3])); addExpr(P, X_JOY, xw[8] * 0.6f);
            P[EARS] += 0.25f * xw[0] + 0.1f * xw[1] + 0.15f * xw[2] - 0.6f * xw[3] - 0.9f * xw[4] + 0.5f * xw[5] + 0.8f * xw[6] - 0.8f * xw[7] + 0.8f * xw[8] - 0.3f * runW * gaitW;
            P[HANDPL] = Mathf.Min(2, P[HANDPL] + 0.9f * hitK);
            // breathing through the mouth when exerted, open-mouthed gasps on big air
            P[MOPEN] = Mathf.Max(P[MOPEN], (0.08f + 0.14f * Mathf.Max(exert, wTired)) * (0.5f + 0.5f * Mathf.Sin(TAU * brPh)) * Mathf.Max(exert, wTired));
            if (runW * gaitW > 0.5f) P[MOPEN] = Mathf.Max(P[MOPEN], 0.12f * runW);
        }

        // ---------------------------------------------------------------------------------------------------------
        // Dances / showcase poses (kid-space feet; variants picked per character)
        // ---------------------------------------------------------------------------------------------------------
        void _poseDance(float[] D, string nm, float tt, float dt)
        {
            D[WPL] = 0; D[WPR] = 0; D[STAB] = 0; D[AFOLT] = 0; D[AFOLR] = 0;
            if (nm == "victory")
            {
                if (danceVar == 0) _dVictoryPump(D, tt);
                else if (danceVar == 1) _dVictoryFlourish(D, tt);
                else _dVictoryHops(D, tt);
            }
            else if (nm == "defeat")
            {
                if (danceVar == 0) _dDefeatSlump(D, tt);
                else if (danceVar == 1) _dDefeatSulk(D, tt);
                else _dDefeatKick(D, tt);
            }
            else if (nm == "menu_idle") _dMenuIdle(D, tt);
            else if (nm == "lobby_pose") _dLobby(D, tt);
            else if (nm == "locker_idle") _dLocker(D, tt);
        }

        static float fmod(float a, float b) => a - b * Mathf.Floor(a / b);

        // Victory A: bouncing fist pumps → hop-spin → hero pose + wink (8 beats @ 126 bpm)
        void _dVictoryPump(float[] D, float tt)
        {
            var H = hold;
            float b = fmod(tt * 2.1f, 8), bf = frac(b);
            float pump = Mathf.Pow(Mathf.Abs(Mathf.Sin(Mathf.PI * b)), 0.6f);
            float hit = Mathf.Exp(-bf * 7);
            D[HIPS_P + 1] = -0.055f + 0.045f * pump;
            D[SPINE] = 0.05f - 0.05f * pump; D[CHEST] = -0.08f * pump;
            D[HEAD] = 0.12f * hit - 0.1f; D[HEAD + 2] = 0.1f * Mathf.Sin(Mathf.PI * b * 0.5f);
            D[SQY] = 1 - 0.05f * hit; D[SQXZ] = 1 + 0.03f * hit;
            setAnc(D, H.raise);
            D[IKL] = 0; D[POLER] = -0.8f; D[POLER + 1] = 0.1f; D[POLER + 2] = -0.5f;
            setE(D, FOOTL, 0.11f, ANKLE_H, 0.01f); setE(D, FOOTR, -0.11f, ANKLE_H, -0.01f); D[FOOTLR + 1] = 0.2f; D[FOOTRR + 1] = -0.2f;
            D[FOOTLR] = 0.25f * pump * (b < 4 ? 1 : 0);
            if (b < 4)
            {
                D[UARML] = -2.6f - 0.35f * pump; D[UARML + 2] = 0.35f; D[FARML] = -0.2f - 1.3f * (1 - pump); D[HANDL] = 0; D[HANDPL] = -1;
                D[ANC + 1] += 0.05f * pump;
                D[CHEST + 1] = 0.12f * Mathf.Sin(Mathf.PI * b);
                // anticipation crouch before the spin
                float pre = win(b, 3.4f, 3.8f, 3.9f, 4.0f);
                D[HIPS_P + 1] -= 0.07f * pre; D[SPINE] += 0.15f * pre; D[UARML] = lerp(D[UARML], -0.6f, pre);
            }
            else if (b < 6)
            {
                float k = (b - 4) / 2;
                D[MODELR + 1] = wrapA(TAU * ease(k));
                float air = Mathf.Sin(Mathf.PI * clamp(k * 1.15f, 0, 1));
                D[MODEL + 1] = 0.2f * air;
                D[FOOTL + 1] += 0.14f * air; D[FOOTR + 1] += 0.14f * air; D[FOOTLR] = 0.45f * air; D[FOOTRR] = 0.45f * air;
                D[UARML] = -1.4f; D[UARML + 2] = 1.1f; D[FARML] = -0.6f; D[HANDPL] = 1.8f;
                D[HEAD + 1] = -0.3f * Mathf.Sin(TAU * k);
                float land = k > 0.87f ? Mathf.Exp(-(k - 0.87f) * 18) : 0;
                D[SQY] *= 1 - 0.1f * land; D[HIPS_P + 1] -= 0.06f * land;
            }
            else
            {
                float k = backOut((b - 6) / 0.4f, 2);
                D[FOOTL] = lerp(0.11f, 0.16f, k); D[FOOTR] = lerp(-0.11f, -0.16f, k); D[FOOTLR + 1] = 0.3f; D[FOOTRR + 1] = -0.3f;
                D[HIPS_P + 1] = -0.07f; D[HIPS + 2] = 0.06f * k; D[CHEST] = -0.12f;
                D[UARML] = -2.0f; D[UARML + 2] = 1.0f * k; D[FARML] = -0.9f; D[HANDL + 2] = 0.3f; D[HANDPL] = 1.9f;
                D[HEAD + 2] = -0.18f * k; D[HEAD] = -0.1f;
                D[WINK] = ease((b - 6.3f) / 0.12f) * (1 - ease((b - 7.6f) / 0.15f));
                setAnc(D, HOLD_HERO);
            }
            D[MOPEN] = 0.75f; D[MCURVE] = 1; D[BROW] = -0.1f; D[BROWY] = 0.8f; D[EYE] = 0.92f; D[LOOKY] = 0.1f; D[EARS] = 0.9f + 0.1f * hit;
        }

        // Victory B: anticipation dip → weapon twirl overhead on tiptoe → point it at the camera, hand on hip, wink
        void _dVictoryFlourish(float[] D, float tt)
        {
            var H = hold;
            float c = fmod(tt, 4.6f);
            float dip = win(c, 0, 0.3f, 0.42f, 0.62f), rise = win(c, 0.45f, 0.7f, 1.4f, 1.7f), strike = win(c, 1.45f, 1.62f, 3.9f, 4.45f);
            float bounce = strike * Mathf.Max(0, Mathf.Sin((c - 1.7f) * TAU * 1.05f));
            setE(D, FOOTL, 0.1f, ANKLE_H, 0.03f); setE(D, FOOTR, -0.12f, ANKLE_H, -0.04f); D[FOOTLR + 1] = 0.25f; D[FOOTRR + 1] = -0.35f;
            D[HIPS_P + 1] = -0.03f - 0.08f * dip + 0.03f * rise - 0.04f * strike + 0.015f * bounce;
            D[SPINE] = 0.2f * dip - 0.12f * rise - 0.02f * strike; D[CHEST] = 0.1f * dip - 0.1f * rise;
            D[FOOTLR] = 0.45f * rise; D[FOOTRR] = 0.45f * rise; D[FOOTL + 1] += 0.03f * rise; D[FOOTR + 1] += 0.03f * rise;
            // weapon: pulled back → overhead twirl → thrust forward to the camera
            setAnc(D, H.carry);
            lerpE(D, ANC, -0.16f, 0.8f, -0.02f, dip); lerpE(D, ANCR, 0.9f, 0.3f, 0.2f, dip);
            lerpE(D, ANC, -0.15f, 1.36f, 0.06f, rise); lerpE(D, ANCR, -1.6f, 0.2f, 0, rise);
            D[SPIN] = wrapA(TAU * 2 * ease((c - 0.55f) / 0.85f)) * (NO_TWIRL.Contains(animKind) ? 0 : 1);
            lerpE(D, ANC, -0.1f, 1.02f, 0.32f, strike); lerpE(D, ANCR, -0.12f, -0.05f, 0.25f, strike);
            D[IKL] = 0; D[POLER] = -0.9f; D[POLER + 1] = 0.05f; D[POLER + 2] = -0.3f;
            // free hand: up with the twirl, then on the hip
            D[UARML] = -2.4f * rise - 0.4f * dip; D[UARML + 2] = 0.3f + 0.2f * dip; D[FARML] = -0.3f - 0.8f * dip;
            D[LTW] = strike; setE(D, LTGT, 0.19f, 0.735f, -0.01f); lerpE(D, POLEL, 1, 0.1f, -0.35f, strike);
            D[HANDPL] = lerp(lerp(1, 1.9f, rise), 1.5f, strike); D[EARS] = 0.4f + 0.5f * rise + 0.4f * strike;
            D[HIPS + 2] = 0.08f * strike; D[HIPS_P] = 0.02f * strike; D[HIPS + 1] = 0.12f * strike; D[CHEST + 1] = -0.15f * strike;
            D[HEAD + 2] = -0.16f * strike + 0.04f * bounce; D[HEAD] = -0.06f * strike - 0.1f * rise; D[HEAD + 1] = 0.08f * strike;
            D[WINK] = win(c, 1.75f, 1.85f, 2.4f, 2.55f);
            D[MCURVE] = 1; D[MOPEN] = 0.3f + 0.45f * rise + 0.2f * bounce; D[MTILT] = 0.2f * strike; D[BROW] = -0.2f * strike; D[BROWY] = 0.6f * rise;
            D[LOOKX] = -0.05f; D[LOOKY] = 0.05f + 0.2f * rise; D[EYE] = 1 - 0.1f * strike;
        }

        // Victory C: side-to-side happy hops with alternating arm waves → big V jump (8 beats @ 150 bpm)
        void _dVictoryHops(float[] D, float tt)
        {
            var H = hold;
            float b = fmod(tt * 2.5f, 8), bf = frac(b); int bi = Mathf.FloorToInt(b);
            float sd = bi % 2 != 0 ? -1 : 1;
            setAnc(D, H.raise);
            D[IKL] = 0;
            if (b < 6)
            {
                float hop = Mathf.Sin(Mathf.PI * clamp(bf / 0.62f, 0, 1));
                float squash = bf > 0.62f ? Mathf.Exp(-(bf - 0.62f) * 14) : 0;
                D[MODEL] = lerp(-0.07f * sd, 0.07f * sd, ease(bf / 0.62f)); D[MODEL + 1] = 0.09f * hop;
                D[HIPS + 2] = -0.1f * sd * hop; D[CHEST + 2] = 0.08f * sd * hop; D[HEAD + 2] = 0.12f * sd * hop;
                setE(D, FOOTL, 0.11f, ANKLE_H + 0.06f * hop * (sd > 0 ? 1 : 0.4f), 0); setE(D, FOOTR, -0.11f, ANKLE_H + 0.06f * hop * (sd < 0 ? 1 : 0.4f), 0);
                D[FOOTLR] = 0.4f * hop; D[FOOTRR] = 0.4f * hop; D[FOOTLR + 1] = 0.15f; D[FOOTRR + 1] = -0.15f;
                D[HIPS_P + 1] = -0.04f - 0.06f * squash + 0.01f * hop; D[SQY] = 1 - 0.06f * squash + 0.03f * hop;
                // left arm waves big arcs; weapon arm pumps
                D[UARML] = -2.5f; D[UARML + 2] = 0.3f + 0.45f * Mathf.Sin(Mathf.PI * b); D[FARML] = -0.3f - 0.3f * Mathf.Sin(Mathf.PI * b * 2);
                D[ANC + 1] += 0.06f * hop; D[ANC] += 0.03f * sd; D[HANDPL] = 2; D[EARS] = 0.7f + 0.3f * hop;
            }
            else
            {
                float k = (b - 6) / 2;
                float crouch = win(k, 0, 0.12f, 0.18f, 0.26f), air = Mathf.Sin(Mathf.PI * clamp((k - 0.2f) / 0.62f, 0, 1)), land = k > 0.82f ? Mathf.Exp(-(k - 0.82f) * 16) : 0;
                D[MODEL + 1] = 0.28f * air;
                D[HIPS_P + 1] = -0.03f - 0.1f * crouch - 0.07f * land;
                D[SPINE] = 0.2f * crouch - 0.1f * air; D[SQY] = 1 - 0.08f * crouch + 0.08f * air - 0.08f * land;
                setE(D, FOOTL, 0.12f, ANKLE_H + 0.18f * air, 0.02f * air); setE(D, FOOTR, -0.12f, ANKLE_H + 0.18f * air, 0.02f * air);
                D[FOOTLR] = 0.6f * air; D[FOOTRR] = 0.6f * air;
                D[UARML] = lerp(-0.5f, -2.7f, air); D[UARML + 2] = lerp(0.2f, 0.75f, air); D[FARML] = -0.2f;
                lerpE(D, ANC, -0.18f, 1.4f, 0.05f, air); lerpE(D, ANCR, -1.9f, 0.3f, -0.5f, air);
                D[HEAD] = -0.2f * air; D[MOPEN] = 0.9f * air; D[HANDPL] = 2; D[EARS] = 1;
            }
            D[MOPEN] = Mathf.Max(D[MOPEN], 0.65f); D[MCURVE] = 1; D[BROWY] = 0.9f; D[EYE] = 0.88f; D[LOOKY] = 0.12f;
        }

        // Defeat A: slumped sway, big sigh, head drop
        void _dDefeatSlump(float[] D, float tt)
        {
            float cyc = fmod(tt, 6);
            float sw = Mathf.Sin(tt * TAU * 0.28f);
            float sg = win(cyc, 3.0f, 3.7f, 3.9f, 4.8f);
            float drop = win(cyc, 4.3f, 4.6f, 4.7f, 5.6f);
            D[HIPS_P + 1] = -0.045f - 0.02f * drop; D[HIPS_P] = 0.01f * sw; D[HIPS + 2] = -0.03f * sw;
            D[SPINE] = 0.2f - 0.1f * sg + 0.05f * drop; D[CHEST] = 0.16f - 0.12f * sg + 0.05f * drop; D[NECK] = 0.1f; D[HEAD] = 0.2f - 0.16f * sg + 0.1f * drop + 0.03f * sw;
            D[HEAD + 2] = 0.06f * sw; D[HEAD + 1] = 0.05f * Mathf.Sin(tt * 0.7f);
            D[CLAVL + 2] = -0.12f + 0.2f * sg; D[CLAVR + 2] = 0.12f - 0.2f * sg; D[CLAVL + 1] = -0.1f; D[CLAVR + 1] = 0.1f;
            D[UARML] = 0.1f + 0.03f * sw; D[UARML + 2] = 0.05f; D[FARML] = -0.12f;
            D[UARMR] = 0.1f - 0.03f * sw; D[UARMR + 2] = -0.05f; D[FARMR] = -0.12f;
            D[IKR] = 0; D[IKL] = 0;
            D[FOOTL] = 0.075f; D[FOOTR] = -0.075f; D[FOOTLR + 1] = -0.18f; D[FOOTRR + 1] = 0.18f;
            D[FOOTLR] = 0.12f; D[FOOTL + 2] = 0.02f; D[KNEEL] = -0.2f; D[KNEER] = 0.2f;
            D[MCURVE] = -0.9f; D[MWIDTH] = 0.8f; D[BROW] = 0.75f; D[BROWY] = -0.3f; D[EYE] = 0.45f + 0.25f * sg; D[LOOKY] = -0.4f; D[LOOKX] = 0.1f * sw;
            D[MOPEN] = 0.3f * sg; D[HANDPL] = 1.15f; D[EARS] = -1 + 0.2f * sg;
        }

        // Defeat B: two frustrated stomps → turn away, arms crossed, pouting, sneaking a glance back
        void _dDefeatSulk(float[] D, float tt)
        {
            float c = fmod(tt, 6.5f);
            float st1 = win(c, 0.1f, 0.25f, 0.3f, 0.42f), st2 = win(c, 0.55f, 0.7f, 0.75f, 0.87f);
            float stomp = Mathf.Max(st1, st2);
            float imp = (c > 0.42f && c < 0.6f ? Mathf.Exp(-(c - 0.42f) * 16) : 0) + (c > 0.87f && c < 1.05f ? Mathf.Exp(-(c - 0.87f) * 16) : 0);
            float turn = win(c, 1.0f, 1.5f, 5.7f, 6.4f);
            float glance = win(c, 3.2f, 3.45f, 3.9f, 4.15f);
            setE(D, FOOTL, 0.095f, ANKLE_H, 0.01f); setE(D, FOOTR, -0.1f, ANKLE_H + 0.14f * stomp, 0.03f * stomp); D[FOOTLR + 1] = 0.12f; D[FOOTRR + 1] = -0.2f; D[FOOTRR] = -0.2f * stomp;
            D[HIPS_P + 1] = -0.035f - 0.05f * imp - 0.02f * stomp; D[SPINE] = 0.1f + 0.15f * imp; D[CHEST] = 0.05f;
            D[HIPS + 2] = -0.06f * stomp;
            // fists clenched down by the sides while stomping
            D[UARML] = 0.15f; D[UARML + 2] = 0.2f; D[FARML] = -0.5f; D[CLAVL + 2] = 0.12f * stomp; D[CLAVR + 2] = -0.12f * stomp;
            setAnc(D, hold.carry); D[ANC + 1] -= 0.05f * stomp; D[ANCR] += 0.4f;
            // turned away with the free arm across the chest
            D[MODELR + 1] = -0.9f * ease(turn);
            D[LTW] = turn; setE(D, LTGT, -0.06f, 0.9f, 0.13f); lerpE(D, POLEL, 1, -0.3f, 0.2f, turn); D[IKL] = 0;
            lerpE(D, ANC, -0.16f, 0.86f, 0.1f, turn); lerpE(D, ANCR, 1.1f, 0.8f, 0.4f, turn);
            D[CHEST] -= 0.08f * turn; D[HEAD] = -0.12f * turn + 0.2f * imp; D[HEAD + 2] = 0.1f * turn;
            D[HEAD + 1] = 0.8f * glance * turn - 0.15f * turn; D[NECK + 1] = 0.3f * glance * turn;
            D[LOOKX] = 0.35f * glance - 0.2f * turn * (1 - glance);
            D[MCURVE] = -1.4f; D[MWIDTH] = 0.55f; D[MTILT] = 0.15f; D[BROW] = -0.5f + 1.1f * turn * (1 - glance); D[BROWY] = -0.2f;
            D[EYE] = 0.75f - 0.3f * imp; D[SQUINT] = 0.6f * stomp; D[MOPEN] = 0.25f * stomp;
            D[HANDPL] = lerp(-1, 0.6f, ease(turn)); D[EARS] = -0.6f - 0.4f * turn + 0.5f * glance;
        }

        // Defeat C: droop, scuff the ground with a foot, sigh, sniff
        void _dDefeatKick(float[] D, float tt)
        {
            float c = fmod(tt, 5.2f);
            float k1 = win(c, 0.6f, 0.85f, 0.95f, 1.2f), k2 = win(c, 1.4f, 1.65f, 1.75f, 2.05f);
            float scuff = Mathf.Max(k1, k2);
            float sg = win(c, 2.6f, 3.2f, 3.4f, 4.2f), sniff = win(c, 4.4f, 4.5f, 4.55f, 4.75f);
            setE(D, FOOTL, 0.085f, ANKLE_H, 0.0f); D[FOOTLR + 1] = -0.1f; D[FOOTRR + 1] = -0.25f;
            setE(D, FOOTR, -0.08f, ANKLE_H + 0.03f * scuff, -0.02f + 0.12f * Mathf.Sin(Mathf.PI * clamp((c - (k2 > k1 ? 1.4f : 0.6f)) / 0.65f, 0, 1)) * scuff);
            D[FOOTRR] = -0.3f * scuff;
            D[HIPS_P + 1] = -0.05f; D[HIPS_P] = 0.02f; D[HIPS + 2] = 0.05f;
            D[SPINE] = 0.18f - 0.08f * sg; D[CHEST] = 0.13f - 0.1f * sg; D[NECK] = 0.1f; D[HEAD] = 0.2f - 0.16f * sg - 0.2f * sniff;
            D[CLAVL + 2] = -0.1f + 0.18f * sg; D[CLAVR + 2] = 0.1f - 0.18f * sg;
            D[UARML] = 0.05f; D[UARML + 2] = 0.04f; D[FARML] = -0.15f; D[IKL] = 0;
            setAnc(D, hold.carry); D[ANC + 1] -= 0.12f; D[ANCR] += 0.55f; D[ANC + 2] -= 0.04f;
            D[MCURVE] = -1.0f; D[MWIDTH] = 0.7f; D[BROW] = 0.85f; D[BROWY] = -0.2f; D[EYE] = 0.5f + 0.3f * sniff; D[LOOKY] = -0.35f; D[LOOKX] = -0.1f * scuff;
            D[MOPEN] = 0.35f * sg; D[HANDPL] = 1.1f; D[EARS] = -0.9f + 0.3f * sniff;
        }

        // Menu idle: relaxed weight shifts + fidgets + look-arounds (same systems as gameplay idle, feet in kid space)
        void _dMenuIdle(float[] D, float tt)
        {
            var H = hold;
            float cyc = fmod(tt, 8);
            float w = Mathf.Sin(tt * TAU / 8);
            D[HIPS_P] = 0.028f * w; D[HIPS + 2] = -0.05f * w; D[SPINE + 2] = 0.03f * w; D[CHEST + 2] = 0.015f * w;
            D[HIPS_P + 1] = -0.024f - 0.01f * Mathf.Abs(w);
            setE(D, FOOTL, HIPW + 0.005f, ANKLE_H, -0.004f); setE(D, FOOTR, -HIPW - 0.005f, ANKLE_H, -0.004f); D[FOOTLR + 1] = 0.14f; D[FOOTRR + 1] = -0.14f;
            if (w > 0) { D[FOOTRR] = 0.25f * w; D[FOOTR + 2] += 0.03f * w; D[FOOTR] -= 0.01f * w; } else { D[FOOTLR] = -0.25f * w; D[FOOTL + 2] -= 0.03f * w; D[FOOTL] += 0.01f * w; }
            const float br = 0;   // breathing: _breathe (post-dance, same for every pose)
            float yw = kc(cyc, K_MENU_T, K_MENU_V);
            float pitch = -0.08f * Mathf.Sin(cyc * 0.9f);
            D[HEAD + 1] = yw * 0.7f; D[NECK + 1] = yw * 0.3f; D[HEAD] = pitch; D[HEAD + 2] = -0.05f * w;
            float yawL = kc(cyc + 0.25f, K_MENU_T, K_MENU_V);
            D[LOOKX] = clamp(yawL * 0.9f, -0.35f, 0.35f); D[LOOKY] = -pitch * 0.5f;
            setAnc(D, H.carry);
            D[IKL] = H.twoCarry != 0 ? 1 : 0;
            float tw = cyc >= 6.1f && cyc < 7.1f ? (cyc - 6.1f) / 1.0f : -1;
            if (tw >= 0 && !NO_TWIRL.Contains(animKind))
            {
                float k = ease(clamp(tw / 0.8f, 0, 1));
                float lift = Mathf.Sin(Mathf.PI * clamp(tw, 0, 1));
                D[SPIN] = wrapA(TAU * 2 * k); D[IKL] = 0;
                D[ANC + 1] += 0.1f * lift; D[ANC + 2] += 0.08f * lift; D[ANC] -= 0.03f * lift; D[ANCR] -= 0.5f * lift;
                D[MCURVE] = 1; D[MOPEN] = 0.25f * lift;
            }
            else if (tw >= 0)
            {
                float lift = Mathf.Sin(Mathf.PI * clamp(tw, 0, 1));
                D[ANC + 1] += 0.06f * lift; D[ANCR] -= 0.25f * lift;
            }
            D[UARML] = -0.05f + 0.03f * br; D[UARML + 2] = 0.12f; D[FARML] = -0.35f;
            D[MCURVE] = Mathf.Max(D[MCURVE], 0.8f); D[EARS] = 0.15f + 0.2f * Mathf.Abs(yw);
        }

        // Locker: relaxed hand-on-hip weight shifts, looking at the camera (the viewer), a head tilt now and then
        void _dLocker(float[] D, float tt)
        {
            var H = hold;
            float w = Mathf.Sin(tt * TAU / 6.5f), cyc = fmod(tt, 9); const float br = 0;   // breathing: _breathe
            setE(D, FOOTL, 0.12f, ANKLE_H, 0.02f); setE(D, FOOTR, -0.115f, ANKLE_H, -0.02f); D[FOOTLR + 1] = 0.26f; D[FOOTRR + 1] = -0.24f;
            if (w > 0) { D[FOOTRR] = 0.22f * w; D[FOOTR + 2] += 0.02f * w; } else { D[FOOTLR] = -0.22f * w; D[FOOTL + 2] -= 0.02f * w; }
            D[HIPS_P] = 0.03f * w; D[HIPS_P + 1] = -0.035f - 0.012f * Mathf.Abs(w); D[HIPS + 2] = 0.075f * w; D[HIPS + 1] = 0.05f * w;
            D[SPINE + 2] = -0.045f * w; D[CHEST + 2] = -0.03f * w; D[CHEST] = -0.03f - 0.02f * br; D[CLAVL + 2] = 0.02f * br; D[CLAVR + 2] = -0.02f * br;
            float tilt = win(cyc, 3.2f, 3.6f, 4.6f, 5.1f), look = win(cyc, 6.2f, 6.6f, 7.2f, 7.7f);
            D[HEAD + 2] = -0.04f * w - 0.14f * tilt; D[HEAD] = -0.04f + 0.02f * br; D[HEAD + 1] = 0.25f * look;
            D[LOOKX] = 0.28f * look; D[LOOKY] = 0.05f;
            // free hand on the hip, the weapon hand relaxed at the side
            D[LTW] = 1; setE(D, LTGT, 0.17f, 0.745f, -0.005f); D[IKL] = 0;
            D[UARML + 2] = 0.9f; D[FARML] = -1.6f; D[HANDL] = 0.3f; D[HANDL + 2] = 0.6f; D[POLEL] = 1; D[POLEL + 1] = 0.1f; D[POLEL + 2] = -0.35f;
            setAnc(D, H.carry); D[ANC + 1] += 0.005f * br;
            if (H.twoCarry != 0) { D[IKL] = 0; lerpE(D, ANC, -0.19f, 0.74f, 0.12f, 1); }
            D[MCURVE] = 0.95f; D[MTILT] = 0.12f * tilt; D[BROWY] = 0.2f * tilt; D[EYE] = 0.95f; D[HANDPL] = 1.4f; D[EARS] = 0.3f + 0.3f * tilt;
        }

        // Loadout / lobby: confident weapon-presenting stance with breathing and a periodic flourish
        void _dLobby(float[] D, float tt)
        {
            var H = hold;
            const float br = 0;   // breathing: _breathe
            float cyc = fmod(tt, 7);
            float fl = win(cyc, 4.6f, 4.9f, 5.6f, 6.1f);
            setE(D, FOOTL, 0.15f, ANKLE_H, 0.02f); setE(D, FOOTR, -0.15f, ANKLE_H, -0.02f); D[FOOTLR + 1] = 0.3f; D[FOOTRR + 1] = -0.28f;
            D[HIPS_P + 1] = -0.05f - 0.004f * br; D[HIPS_P] = 0.012f; D[HIPS + 2] = -0.05f; D[HIPS + 1] = 0.08f;
            D[SPINE + 1] = -0.05f; D[SPINE + 2] = 0.03f; D[CHEST] = -0.1f - 0.015f * br; D[CHEST + 1] = -0.08f;
            D[HEAD] = -0.1f; D[HEAD + 2] = -0.1f; D[HEAD + 1] = 0.12f; D[NECK + 1] = 0.05f;
            setAnc(D, H.lobby);
            D[ANC + 1] += 0.006f * br;
            D[POLER] = -0.9f; D[POLER + 1] = -0.4f; D[POLER + 2] = -0.1f;
            if (H.lobbyTwo != 0) { D[IKL] = 1; D[LTW] = 0; D[CHEST + 1] = 0.05f; }
            else
            {
                D[LTW] = 1; setE(D, LTGT, 0.17f, 0.745f, -0.005f); D[IKL] = 0;
                D[UARML + 2] = 0.9f; D[FARML] = -1.6f; D[HANDL] = 0.3f; D[HANDL + 2] = 0.6f;
                D[POLEL] = 1; D[POLEL + 1] = 0.1f; D[POLEL + 2] = -0.35f;
            }
            // flourish: a quick re-grip / twirl and a proud chin-up
            if (animKind == "shooter" || animKind == "blaster") { D[SPIN] = wrapA(TAU * ease((cyc - 4.8f) / 0.55f)) * (cyc > 4.8f && cyc < 5.5f ? 1 : 0); D[ANC + 1] += 0.05f * fl; }
            else D[ANCR] -= 0.2f * fl;
            D[HEAD] -= 0.08f * fl; D[HIPS_P + 1] -= 0.015f * fl;
            D[MCURVE] = 1; D[MTILT] = 0.22f; D[BROW] = -0.25f; D[EYE] = 0.92f; D[LOOKX] = -0.12f; D[LOOKY] = 0.04f;
            D[MOPEN] = 0.2f * fl; D[HANDPL] = H.lobbyTwo != 0 ? 0 : 1.5f; D[EARS] = 0.45f + 0.4f * fl;
        }
    }
}
