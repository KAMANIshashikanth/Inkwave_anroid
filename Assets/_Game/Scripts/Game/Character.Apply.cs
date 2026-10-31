// Port of src/game/character.js — pose application: kid transform → pelvis reach → torso FK → head → leg IK → arm IK
// → face → hair → tank → jiggle → fingers; plus the squid form and the per-frame material inputs.
using UnityEngine;
using static Inkwave.Ctx;

namespace Inkwave
{
    public partial class Character
    {
        // three's bone.rotation.set(x, y, z) for the two Euler orders the rig uses
        static void rotYXZ(Transform b, float x, float y, float z) => b.localRotation = qYXZ(x, y, z);
        static void rotXYZ(Transform b, float x, float y, float z) => b.localRotation = qXYZ(x, y, z);

        Vector3 _fL, _fR; Quaternion _fLq = Quaternion.identity, _fRq = Quaternion.identity;
        Vector3? _tankRest;

        void _applyPose(float dt, AnimState s)
        {
            var B = bones; Vector3 R = root.localPosition;
            // ---- hand-held splat bomb: appears (pop) when the sub is aimed, leaves the hand on 'throw'
            if (bomb != null)
            {
                bool on = bombHeld && kidForm && wSub > 0.2f;
                bomb.group.gameObject.SetActive(on && !subPropHidden);   // specials that hold their own prop hide it
                if (on) bomb.group.localScale = Vector3.one * Mathf.Max(0.05f, backOut(clamp(bombT / 0.14f, 0, 1), 2.6f));
            }
            // ---- kid group transform: squash/stretch, model offsets, rotation about the hips, form-change pop
            float sq = kidScale;
            float sqY = P[SQY] * kidSY * sq, sqX = P[SQXZ] * kidSXZ * sq;
            sqX = Mathf.Max(1e-3f, sqX); sqY = Mathf.Max(1e-3f, sqY);
            kid.localScale = new Vector3(sqX, sqY, sqX);
            Quaternion kq = qYXZ(P[MODELR], P[MODELR + 1], P[MODELR + 2]);
            // dodge roll tumble: the tucked body turns about (up × roll direction) through its centre, lowered to the ground
            if (tumble != 0) kq = Quaternion.AngleAxis(tumble * Mathf.Rad2Deg, new Vector3(tumbleX, 0, tumbleZ)) * kq;
            kid.localRotation = kq;
            Vector3 v1 = new Vector3(0, tumble != 0 ? 0.56f : 0.62f, 0), v2 = kq * v1;
            Vector3 kpos = new Vector3(P[MODEL], P[MODEL + 1] - tumbleDrop, P[MODEL + 2]) + v1 - v2;
            kpos.y += kidLift;
            kid.localPosition = kpos;
            // inverse kid transform (root space → kid space)
            Quaternion q6 = Quaternion.Inverse(kq);
            float isx = 1 / sqX, isy = 1 / sqY;

            // ---- feet targets in kid space (planted world feet blended with pose feet)
            float c = Mathf.Cos(yaw), sn = Mathf.Sin(yaw);
            float plant = plantW;
            for (int i = 0; i < 2; i++)
            {
                var f = feet[i];
                int FO = i == 0 ? FOOTL : FOOTR, FR = i == 0 ? FOOTLR : FOOTRR;
                float wp = (i == 0 ? P[WPL] : P[WPR]) * plant * (feetValid ? 1 : 0);
                // pose target
                Quaternion q1 = qYXZ(P[FR], P[FR + 1], P[FR + 2]);
                Vector3 v3 = new Vector3(P[FO], P[FO + 1], P[FO + 2]);
                if (wp > 0.001f)
                {
                    // world contact → root space
                    float dx = f.cw.x - R.x, dy = f.cw.y - R.y, dz = f.cw.z - R.z;
                    Vector3 v4 = new Vector3(dx * c - dz * sn, dy, dx * sn + dz * c);
                    float fy = f.cyaw - yaw;
                    float pitch = f.pitch;
                    if (!moving && i == (shiftS > 0 ? 1 : 0)) pitch += 0.1f * Mathf.Abs(shiftS) * (1 - gaitW);
                    float tip = Mathf.Max(P[TIPTOE], _toeUp);
                    if (tip > 0.001f) pitch += 0.55f * tip;
                    // ground normal in root space → foot orientation = align(up→n) · yaw · pitch
                    Vector3 v5 = new Vector3(f.cn.x * c - f.cn.z * sn, f.cn.y, f.cn.x * sn + f.cn.z * c);
                    Quaternion q2 = Quaternion.FromToRotation(UP, v5) * Quaternion.AngleAxis(fy * Mathf.Rad2Deg, YAX);
                    // ankle above the contact, rolling about the ball (heel up) or the heel (toes up)
                    float az, ay;
                    if (pitch >= 0) { ay = ANKLE_H * Mathf.Cos(pitch) + BALL_Z * Mathf.Sin(pitch); az = BALL_Z + ANKLE_H * Mathf.Sin(pitch) - BALL_Z * Mathf.Cos(pitch); }
                    else { ay = ANKLE_H * Mathf.Cos(pitch) - HEEL_Z * Mathf.Sin(pitch); az = -HEEL_Z + ANKLE_H * Mathf.Sin(pitch) + HEEL_Z * Mathf.Cos(pitch); }
                    Vector3 v6 = q2 * new Vector3(0, ay, az) + v4;
                    q2 = q2 * Quaternion.AngleAxis(pitch * Mathf.Rad2Deg, XAX);
                    // root space → kid space
                    v6 = q6 * (v6 - kpos); v6.x *= isx; v6.y *= isy; v6.z *= isx;
                    q2 = q6 * q2;
                    v3 = Vector3.Lerp(v3, v6, wp); q1 = Quaternion.Slerp(q1, q2, wp);
                }
                if (i == 0) { _fL = v3; _fLq = q1; } else { _fR = v3; _fRq = q1; }
            }
            _toeUp = 0;

            // ---- pelvis: pose offsets, then drop just enough that both ankles are reachable
            Vector3 hipsP = rest["hips"] + new Vector3(P[HIPS_P], P[HIPS_P + 1], P[HIPS_P + 2]);
            Quaternion hipsQ = qYXZ(P[HIPS], P[HIPS + 1], P[HIPS + 2]);
            {
                float drop = 0;
                for (int i = 0; i < 2; i++)
                {
                    var leg = i == 0 ? limbL_leg : limbR_leg; Vector3 ft = i == 0 ? _fL : _fR;
                    // only feet carrying weight pull the pelvis down; a swinging foot just reaches (clamped below)
                    var f = feet[i];
                    if (plantW > 0.5f && (i == 0 ? P[WPL] : P[WPR]) > 0.5f && f.sw && f.su > 0.02f && f.su < 0.9f) continue;
                    Vector3 v4 = hipsQ * leg.up.localPosition + hipsP;
                    float hx0 = ft.x - v4.x, hz0 = ft.z - v4.z;
                    float h2 = hx0 * hx0 + hz0 * hz0;
                    float vmax = Mathf.Sqrt(Mathf.Max(0, legReach * legReach - h2));
                    float need = v4.y - (ft.y + vmax);
                    if (need > drop) drop = need;
                }
                drop = Mathf.Min(drop, 0.26f);
                hipDrop = drop > hipDrop ? damp(hipDrop, drop, 40, dt) : damp(hipDrop, drop, 16, dt);
                hipsP.y -= Mathf.Max(drop * 0.85f, hipDrop);
                // keep swing-foot targets inside the leg's reach (a soft knee, never a locked, popping leg)
                for (int i = 0; i < 2; i++)
                {
                    var leg = i == 0 ? limbL_leg : limbR_leg;
                    Vector3 v4 = hipsQ * leg.up.localPosition + hipsP;
                    Vector3 ft = i == 0 ? _fL : _fR;
                    Vector3 v5 = ft - v4; float dl = v5.magnitude, mxr = legReach * 0.97f;
                    if (dl > mxr) { ft = v4 + v5 * (mxr / dl); if (i == 0) _fL = ft; else _fR = ft; }
                }
            }
            B["hips"].localPosition = hipsP; B["hips"].localRotation = hipsQ;

            // ---- torso FK
            rotYXZ(B["spine"], P[SPINE], P[SPINE + 1], P[SPINE + 2]);
            rotYXZ(B["chest"], P[CHEST], P[CHEST + 1], P[CHEST + 2]);
            rotYXZ(B["neck"], P[NECK], P[NECK + 1], P[NECK + 2]);
            clavLx = P[CLAVL]; clavLy = P[CLAVL + 1]; clavLz = P[CLAVL + 2];
            rotYXZ(B["clavL"], clavLx, clavLy, clavLz);
            rotYXZ(B["clavR"], P[CLAVR], P[CLAVR + 1], P[CLAVR + 2]);
            rotXYZ(B["uArmL"], P[UARML], P[UARML + 1], P[UARML + 2]);
            rotXYZ(B["uArmR"], P[UARMR], P[UARMR + 1], P[UARMR + 2]);
            rotXYZ(B["fArmL"], P[FARML], P[FARML + 1], P[FARML + 2]);
            rotXYZ(B["fArmR"], P[FARMR], P[FARMR + 1], P[FARMR + 2]);
            rotXYZ(B["handL"], P[HANDL], P[HANDL + 1], P[HANDL + 2]);
            rotXYZ(B["handR"], P[HANDR], P[HANDR + 1], P[HANDR + 2]);

            // ---- head: stabilised look (kid-space yaw/pitch) blended with the FK head
            {
                Quaternion q1 = qYXZ(P[HEAD], P[HEAD + 1], P[HEAD + 2]);
                if (P[STAB] > 0.001f)
                {
                    _kidXform(B["neck"], out _, out var q2);
                    float tp = P[HIPS] + P[SPINE] + P[CHEST] + P[NECK], trl = P[HIPS + 2] + P[SPINE + 2] + P[CHEST + 2];
                    Quaternion q3 = qYXZ(-P[HLP] + P[HEAD] * 0.5f + tp * 0.4f, P[HLY], P[HEAD + 2] + trl * 0.35f);
                    q2 = Quaternion.Inverse(q2) * q3;
                    q1 = Quaternion.Slerp(q1, q2, P[STAB]);
                }
                else if (Mathf.Abs(P[HLY]) + Mathf.Abs(P[HLP]) > 1e-4f) q1 = qYXZ(-P[HLP], P[HLY], 0) * q1;
                B["head"].localRotation = q1;
            }

            // ---- legs: IK to the targets, knees toward the feet
            float hyw = P[HIPS + 1];
            for (int i = 0; i < 2; i++)
            {
                var leg = i == 0 ? limbL_leg : limbR_leg; float sd = i == 0 ? 1 : -1;
                Vector3 ft = i == 0 ? _fL : _fR; Quaternion fq = i == 0 ? _fLq : _fRq;
                Vector3 fwd = fq * new Vector3(0, 0, 1); // foot forward
                int kn = i == 0 ? KNEEL : KNEER;
                Vector3 pole = new Vector3(fwd.x * 0.7f + Mathf.Sin(hyw) * 0.3f + 0.1f * sd + P[kn], 0.05f + P[kn + 1], fwd.z * 0.7f + Mathf.Cos(hyw) * 0.3f + P[kn + 2]);
                _solveLimb(leg, ft, pole, fq, 1, sd > 0 ? 2 : 3);
            }

            // ---- weapon parts first (WeaponModel.animate) so the hands ride this frame's pump / trigger
            var w = weapon; var d = w.def;
            _animWeapon(dt, s, w);
            // ---- weapon anchor → right arm IK
            Quaternion aQ = qYXZ(P[ANCR], P[ANCR + 1], P[ANCR + 2]);
            Vector3 aP = new Vector3(P[ANC], P[ANC + 1], P[ANC + 2]);
            // follow the chest (translation / rotation weights) so the weapon rides with the torso
            bool fol = P[AFOLT] > 0.001f || P[AFOLR] > 0.001f;
            Vector3 cP = default; Quaternion cQ = Quaternion.identity;
            if (fol)
            {
                _kidXform(B["chest"], out cP, out cQ);
                Quaternion q2 = Quaternion.Slerp(Quaternion.identity, cQ, P[AFOLR]);
                Vector3 t1 = q2 * (aP - rest["chest"]) + cP;
                aP = Vector3.Lerp(aP, t1, P[AFOLT]);
                aQ = q2 * aQ;
            }
            // sway (lags body acceleration / turning) — damped when aiming so the barrel stays true; none in the lock
            float swW = lerp(1, 0.3f, wAim) * (1 - lockW);
            float sx = spr(sp, S_WPX, clamp(-kax * 0.0011f, -0.035f, 0.035f), 3.0f, 0.34f, dt);
            float sy = spr(sp, S_WPY, clamp(-vyS * 0.002f, -0.03f, 0.03f), 3.4f, 0.34f, dt);
            float sz = spr(sp, S_WPZ, clamp(-kaz * 0.0011f, -0.035f, 0.035f), 3.0f, 0.34f, dt);
            float rx = spr(sp, S_WRX, clamp(vyS * 0.01f, -0.12f, 0.12f), 2.6f, 0.32f, dt);
            float ry = spr(sp, S_WRY, clamp(-yawRate * 0.035f, -0.22f, 0.22f), 2.6f, 0.38f, dt);
            aP.x += sx * swW; aP.y += sy * swW; aP.z += sz * swW;
            aQ = qYXZ(rx * swW, ry * swW, 0) * aQ;
            // recoil: kick back along the barrel + muzzle climb + jitter
            aP += aQ * new Vector3(0, 0, -rcZ);
            aQ = aQ * qYXZ(-rcP, sp[S_RCY], sp[S_RCR]);
            {
                Quaternion q2 = aQ * d.handR.quat;
                Vector3 pT = aQ * d.handR.pos + aP;
                Vector3 pN = new Vector3(P[POLER], P[POLER + 1], P[POLER + 2]);
                if (P[IKR] > 0.001f) _solveLimb(limbR_arm, pT, pN, q2, P[IKR], 1);
            }
            rotXYZ(w.pivot, P[SPIN], 0, 0);
            if (dual)
            {
                // ---- dual wield: the left fist holds its own pistol at the mirrored anchor (own sway mirror + own recoil)
                var wl = w.left;
                aQ = qYXZ(P[ANLR], P[ANLR + 1], P[ANLR + 2]);
                aP = new Vector3(P[ANL], P[ANL + 1], P[ANL + 2]);
                if (fol)
                {
                    Quaternion q2 = Quaternion.Slerp(Quaternion.identity, cQ, P[AFOLR]);
                    Vector3 t1 = q2 * (aP - rest["chest"]) + cP;
                    aP = Vector3.Lerp(aP, t1, P[AFOLT]);
                    aQ = q2 * aQ;
                }
                aP.x += sx * swW; aP.y += sy * swW; aP.z += sz * swW;
                aQ = qYXZ(rx * swW, ry * swW, 0) * aQ;
                aP += aQ * new Vector3(0, 0, -rcZ2);
                aQ = aQ * qYXZ(-rcP2, -sp[S_RCY2], 0);
                Quaternion qe = aQ * d.handL.quat;
                Vector3 pT = aQ * d.handL.pos + aP;
                if (P[LTW] > 0.001f) pT = Vector3.Lerp(pT, new Vector3(P[LTGT], P[LTGT + 1], P[LTGT + 2]), P[LTW]);
                Vector3 pN = new Vector3(P[POLEL], P[POLEL + 1], P[POLEL + 2]);
                float wgt = clamp(Mathf.Max(P[IKL], P[LTW]), 0, 1) * (1 - bombSwap);
                if (wgt > 0.001f) _solveLimb(limbL_arm, pT, pN, P[LTW] > 0.5f ? (Quaternion?)null : qe, wgt, 0);
                rotXYZ(wl.pivot, -P[SPIN], 0, 0);
                // the splat bomb needs the left fist: the pistol shrinks away while it is held and pops back after the throw
                float k = 1 - bombSwap;
                wl.pivot.localScale = Vector3.one * Mathf.Max(0.001f, k < 1 ? backOut(k, 2) : 1);
                wl.pivot.gameObject.SetActive(k > 0.01f && !weaponHidden);
            }
            else if (P[IKL] > 0.001f || P[LTW] > 0.001f)
            {
                // left arm → foregrip, an explicit target (hip / visor / tank), or free FK
                _kidXform(B["handR"], out var v3, out var q3);
                Vector3 v4 = q3 * w.pivot.localPosition + v3; Quaternion q4 = q3 * w.pivot.localRotation;
                Vector3 v5 = q4 * w.off.localPosition + v4; Quaternion q5 = q4 * w.off.localRotation;
                Vector3 pT = d.handL.pos; if (w.pump != 0) pT.z -= 0.036f * w.pump;   // blaster: the support hand racks the pump
                pT = q5 * pT + v5;
                Quaternion q2 = q5 * d.handL.quat;
                if (P[LTW] > 0.001f)
                {
                    pT = Vector3.Lerp(pT, new Vector3(P[LTGT], P[LTGT + 1], P[LTGT + 2]), P[LTW]);
                    float wgt = Mathf.Max(P[IKL], P[LTW]);
                    Vector3 pN = new Vector3(P[POLEL], P[POLEL + 1], P[POLEL + 2]);
                    _solveLimb(limbL_arm, pT, pN, P[IKL] > P[LTW] ? (Quaternion?)q2 : null, wgt, 0);
                }
                else
                {
                    Vector3 pN = new Vector3(P[POLEL], P[POLEL + 1], P[POLEL + 2]);
                    _solveLimb(limbL_arm, pT, pN, q2, P[IKL], 0);
                    // still short of the foregrip (steep aim, fast pitch changes): protract the shoulder toward the grip
                    // and re-solve in the same frame, so the support hand never visibly leaves the gun
                    ikErrPre = ikErr[0];
                    if (P[IKL] > 0.5f && ikErr[0] > 0.0005f)
                    {
                        float cy = clavLy;
                        for (int it = 0; it < 3 && ikErr[0] > 0.0005f && clavLy > cy - 0.55f; it++)
                        {
                            float extra = clamp(ikErr[0] * 12, 0, 0.3f);
                            clavLy -= extra; clavLx -= extra * 0.35f;
                            rotYXZ(B["clavL"], clavLx, clavLy, clavLz);
                            _solveLimb(limbL_arm, pT, pN, q2, P[IKL], 0);
                        }
                    }
                }
            }

            int lv = lifeLv;
            // ---- face
            if (lv > 0) _applyFace(P, dt);
            // ---- hair secondary motion (far: every other frame on the summed step; the chain sub-steps at ≤ 1/60 s)
            if (lv > 0 || !(_hairOdd = !_hairOdd)) { _updateHair(dt + _hairAcc); _hairAcc = 0; } else _hairAcc += dt;
            // ---- tank slosh (ink level wobble + surface tilt within the glass)
            _updateTank(dt);
            // ---- fork weapon extras (the arsenal kinds' drums / barrels are turned by WeaponModel.animate)
            if (w.spin != null)
            {
                // spinner / zooka barrels: spin up with the charge, flat out while the stream fires, wind down after
                float want = lastShot < 0.12f ? 38 : charge * 30;
                spinRate = damp(spinRate, want, want > spinRate ? 6 : 1.6f, dt);
                spinAngle = (spinAngle + spinRate * dt) % TAU;
                rotXYZ(w.spin, 0, 0, spinAngle);
            }
            if (w.pivotL != null) w.pivotL.gameObject.SetActive(!bombHeld && !weaponHidden);   // twins: the left pistol makes way for the sub
            _updateDodge(dt);
            // ---- jiggle bones: toes, tee hem flaps, backpack sway, ears
            if (lv > 0) _applyJiggle(P, dt);
            // ---- hands: grip weapons / the bomb, relax when free, fists and open palms from the pose layers
            if (lv > 0) _applyFingers(P, dt);
            _headQW = B["head"].localRotation; _headSet = true;
            // ---- remember where the feet actually are (world) for seamless replanting after air / dances
            Matrix4x4 km = Matrix4x4.TRS(kid.localPosition, kid.localRotation, kid.localScale);
            for (int i = 0; i < 2; i++)
            {
                var f = feet[i]; var bone = i == 0 ? B["footL"] : B["footR"];
                _kidXform(bone, out var p1, out var q1);
                p1 = km.MultiplyPoint3x4(p1); // root space
                f.disp = new Vector3(R.x + p1.x * c + p1.z * sn, R.y + Mathf.Max(0, p1.y - ANKLE_H), R.z - p1.x * sn + p1.z * c);
                Vector3 fw = kid.localRotation * (q1 * new Vector3(0, 0, 1));
                f.dispYaw = yaw + Mathf.Atan2(fw.x, fw.z); f.dispOK = true;
            }
        }
        float clavLx, clavLy, clavLz;

        // Gaze: eye yaw/pitch in head space. Big changes of the wanted direction fire a saccade (minimum-jerk,
        // main-sequence duration ≈ 22 ms + 100 ms/rad, big ones undershoot and land with a correction, may trigger a
        // blink); small ones are tracked at once (VOR) with fixation micro-saccades on top.
        void _gazeTick(float dt, float dx, float dy)
        {
            var g = gz;
            dx = clamp(dx, -0.62f, 0.62f); dy = clamp(dy, -0.42f, 0.38f);
            if (g.st >= 0)
            {
                g.st += dt; float u = Mathf.Min(1, g.st / g.sd), k = mj(u);
                g.x = lerp(g.x0, g.x1, k); g.y = lerp(g.y0, g.y1, k);
                if (u >= 1) { g.st = -1; g.fx = g.x1; g.fy = g.y1; }
                return;
            }
            float ex = dx - g.fx, ey = dy - g.fy, amp = Mathf.Sqrt(ex * ex + ey * ey);
            if (amp > 0.075f && dt > 0)
            {
                g.x0 = g.x; g.y0 = g.y;
                float us = amp > 0.25f ? 0.9f + 0.05f * rng() : 1;
                g.x1 = g.fx + ex * us; g.y1 = g.fy + ey * us; g.sd = 0.022f + 0.1f * amp; g.st = 0;
                if (amp > 0.3f && kidForm && bl.t < 0 && bl.next > 0.8f && rng() < amp * 0.3f) _blinkStart(0.85f + 0.15f * rng(), false);
                if (amp > 0.4f && rng() < 0.3f) _mxPlay(X_BROWFLASH, 0.3f, 0.06f, 0.1f, 0.3f);
                return;
            }
            g.fx = dx; g.fy = dy;
            g.mT -= dt;
            if (g.mT <= 0) { g.mT = 0.28f + rng() * 1.1f; g.mx = (rng() - 0.5f) * 0.07f; g.my = (rng() - 0.5f) * 0.045f; }
            g.x = damp(g.x, g.fx + g.mx, 45, dt); g.y = damp(g.y, g.fy + g.my, 45, dt);
        }

        void _applyFace(float[] P, float dt)
        {
            var B = bones; var F = face; var A = att;
            // ---- gaze: exact eye-in-head direction to the attention point (real head orientation: dances, lobby turns)
            float ex = P[LOOKX] / 0.9f, ey = (P[LOOKY] - 0.02f) / 0.8f;
            if (A.on)
            {
                Vector3 v1 = Matrix4x4.TRS(kid.localPosition, kid.localRotation, kid.localScale).inverse.MultiplyPoint3x4(A.p);
                _kidXform(B["head"], out var v2, out var q1);
                Vector3 v3 = q1 * EYE_MID + v2;
                v1 = Quaternion.Inverse(q1) * (v1 - v3);
                float d = v1.magnitude;
                if (d > 0.05f) { ex = Mathf.Atan2(v1.x, v1.z); ey = Mathf.Atan2(v1.y, Mathf.Sqrt(v1.x * v1.x + v1.z * v1.z)); A.dist = d; }
            }
            // the showcase turned the head toward a neighbour after our last update: the eyes carry the rest of the way
            if (Mathf.Abs(extGl) > 0.06f) { ex = extGl * 0.62f; ey = -0.03f; }
            _gazeTick(dt, ex, ey);
            float gzx = gz.x, gzy = gz.y;
            F.verge = Mathf.Atan2(0.062f, Mathf.Max(0.3f, A.dist));   // half the convergence angle on the looked-at point
            // ---- lids: blink (per eye) + wink + squint; the upper lid rides the gaze (looking down lowers it, up lifts it)
            float sqz = clamp(P[SQUINT] + 0.35f * bl.squeeze * blinkK, 0, 1);
            float follow = clamp(-gzy * 0.75f, 0, 0.3f) - clamp(gzy * 0.3f, 0, 0.1f);
            float bas = clamp(P[EYE] - 0.22f * sqz - follow + 0.04f * (P[PUPIL] - 0.5f), 0.07f, 1.25f);
            float open = Mathf.Max(0.07f, bas * (1 - 0.94f * blinkL));
            float openR = Mathf.Max(0.07f, bas * (1 - 0.94f * blinkR) * (1 - 0.93f * clamp(P[WINK], 0, 1)));
            B["eyeL"].localScale = new Vector3(1 + 0.06f * (1 - open), open, 1);
            B["eyeR"].localScale = new Vector3(1 + 0.06f * (1 - openR), openR, 1);
            // ---- brows (+ asymmetry channel), mouth decal, gaze uniform
            float bra = clamp(P[BRAS], -1, 1);
            rotXYZ(B["browL"], 0, 0, -P[BROW] * 0.42f - 0.12f * bra); rotXYZ(B["browR"], 0, 0, P[BROW] * 0.42f - 0.12f * bra);
            var bL = B["browL"].localPosition; bL.y = faceRestBrowL.y + (P[BROWY] + 0.6f * bra) * 0.008f - 0.004f * sqz + (1 - open) * -0.004f; B["browL"].localPosition = bL;
            var bR = B["browR"].localPosition; bR.y = faceRestBrowR.y + (P[BROWY] - 0.6f * bra) * 0.008f - 0.004f * sqz + (1 - openR) * -0.004f; B["browR"].localPosition = bR;
            float mo = clamp(P[MOPEN], 0, 1);
            F.smile = clamp((P[MCURVE] - 0.3f) / 0.6f, 0, 1);
            var mouth = new Vector4(clamp(P[MCURVE], -1.2f, 1.2f), clamp(P[MWIDTH], 0.2f, 1.4f), mo, P[MTILT]);
            float lx = clamp(gzx * 0.9f, -0.4f, 0.4f) * 0.55f, ly = clamp(gzy * 0.8f + 0.02f, -0.4f, 0.4f) * 0.5f;
            // face hooks (character-mats.js): socketed eyeballs turn by uLook·1.25 + uGaze — top that up so the balls
            // really point at the target (90 %), converging on near targets; lower lids rise with squint / smiles;
            // pupils; mouth extras (sneer, pucker, blush)
            float vg = F.verge * (A.on ? 1 : 0), ex2 = 0.9f * gzx - lx * 1.25f, ey2 = 0.9f * gzy - ly * 1.25f;
            float lo = clamp(0.4f * sqz + 0.22f * F.smile, 0, 0.6f);
            mats.setFace(mouth, new Vector2(lx, ly), new Vector4(ex2 - vg, ey2, ex2 + vg, ey2), new Vector4(clamp((1 - open) / 0.93f, 0, 1), clamp((1 - openR) / 0.93f, 0, 1), lo, lo), P[PUPIL],
                new Vector4(F.smile, clamp(P[SNEER], 0, 1), clamp(P[PUCKER], 0, 1), clamp(P[BLUSH], 0, 1)));
            // ---- face channels: what the face rig / shaders can hook
            F.blinkL = blinkL; F.blinkR = Mathf.Max(blinkR, clamp(P[WINK], 0, 1)); F.lidL = 1 - open / Mathf.Max(0.07f, P[EYE]); F.lidR = 1 - openR / Mathf.Max(0.07f, P[EYE]);
            F.squintL = F.squintR = sqz; F.gazeX = gzx; F.gazeY = gzy; F.pupil = P[PUPIL];
            F.browInL = F.browInR = clamp(P[BROWY] * 0.6f + P[BROW] * 0.5f, -1, 1); F.browOutL = clamp(P[BROWY] + 0.6f * bra, -1, 1); F.browOutR = clamp(P[BROWY] - 0.6f * bra, -1, 1);
            F.furrow = clamp(-P[BROW], 0, 1); F.curve = P[MCURVE]; F.width = P[MWIDTH]; F.open = mo; F.tilt = P[MTILT]; F.jaw = mo;
            F.breath = P[BREATH]; F.sneer = P[SNEER]; F.pucker = P[PUCKER];
            // optional rig bones
            if (xb.jaw != null) rotXYZ(xb.jaw, mo * 0.35f, 0, 0);
            if (xb.cheekL != null || xb.cheekR != null)
            {
                float mc = P[MCURVE];
                float smile = clamp(clamp((mc - 0.3f) / 0.6f, 0, 1) * (1 - 0.4f * mo) + (mc > 0.3f ? clamp(mo - 0.35f, 0, 1) * 0.9f : 0), 0, 1);
                float pout = clamp((-mc - 1.2f) / 0.4f, 0, 1);
                float puff = clamp(Mathf.Max(smile, pout * 0.85f) + 0.3f * sqz, 0, 1);
                for (int ci = 0; ci < 2; ci++)
                {
                    var b = ci == 0 ? xb.cheekL : xb.cheekR; var r = cheekRest[ci]; if (b == null || r == null) continue;
                    b.localPosition = new Vector3(r.Value.x, r.Value.y + 0.0035f * smile + 0.001f * pout, r.Value.z); b.localScale = Vector3.one * (1 + 0.06f * puff);
                }
            }
        }

        void _applyJiggle(float[] P, float dt)
        {
            // toes stay flat on the ground while the heel peels up (planted feet; posed feet only when they touch the ground)
            for (int i = 0; i < 2; i++)
            {
                var toe = i == 0 ? xb.toeL : xb.toeR; if (toe == null) continue;
                var f = feet[i];
                float wp = (i == 0 ? P[WPL] : P[WPR]) * plantW * (feetValid ? 1 : 0);
                float onGnd = f.planted || !f.sw ? 1 : Mathf.Max(0, 1 - f.su * 5);
                int FO = i == 0 ? FOOTL : FOOTR, FR = i == 0 ? FOOTLR : FOOTRR;
                float posed = P[FR] * sstep(0.05f, 0.005f, P[FO + 1] - ANKLE_H);
                rotXYZ(toe, -clamp(lerp(posed, f.pitch * onGnd, wp), 0, 0.5f), 0, 0);
            }
            // tee hem: trails back with speed / acceleration, swings forward when braking, flares when falling, sways sideways
            if (xb.hemF != null || xb.hemB != null)
            {
                float fl = spr(sp, S_HEMP, clamp(kaz * 0.005f + gs * 0.022f * gaitW, -0.3f, 0.3f), 3.0f, 0.2f, dt);
                float lift = spr(sp, S_HEMV, clamp(-vyS * 0.018f, -0.1f, 0.25f), 3.4f, 0.25f, dt);
                float lat = spr(sp, S_HEMR, clamp(-kax * 0.005f, -0.15f, 0.15f), 3.0f, 0.25f, dt);
                float flut = 0.05f * Mathf.Sin(TAU * 2 * phase + 0.7f) * gaitW * runW;
                if (xb.hemF != null) rotXYZ(xb.hemF, clamp(Mathf.Min(0, fl) - Mathf.Max(0, lift) - Mathf.Max(0, flut), -0.25f, 0), 0, lat);
                if (xb.hemB != null) rotXYZ(xb.hemB, clamp(Mathf.Max(0, fl) + Mathf.Max(0, lift) + Mathf.Max(0, -flut), 0, 0.25f), 0, lat);
            }
            // backpack: hangs from the straps — bottom swings back on acceleration, sways sideways, bounces with the stride
            if (xb.tank != null)
            {
                _tankRest ??= xb.tank.localPosition;
                float bob = gaitW * runW * Mathf.Cos(TAU * 2 * (phase - duty * 0.5f));
                float ty = spr(sp, S_TKY, clamp(-headAcc.y * 0.001f, -0.012f, 0.012f), 4.5f, 0.22f, dt);
                float rx = spr(sp, S_TKX, clamp(kaz * 0.0022f + 0.035f * bob - vyS * 0.004f, -0.12f, 0.12f), 3.0f, 0.26f, dt);
                float rz = spr(sp, S_TKZ, clamp(-kax * 0.002f + 0.02f * Mathf.Sin(TAU * phase) * gaitW, -0.1f, 0.1f), 3.0f, 0.26f, dt);
                var tr0 = _tankRest.Value;
                xb.tank.localPosition = new Vector3(tr0.x, tr0.y + ty, tr0.z);
                rotXYZ(xb.tank, rx, 0, rz);
            }
            // ears: perk / droop with the mood, trail head turns, flick on hits and landings, twitch now and then
            if (xb.earL != null || xb.earR != null)
            {
                earT -= dt;
                if (earT <= 0) { earT = 3 + rng() * 6; int e0 = rng() < 0.5f ? S_EARL : S_EARR; sp[e0 + 1] += rng() < 0.7f ? 5 : -4; }
                // left-ear convention: + perk, − droop. Under a snapback / bucket brim the ears tuck down
                int hat = style.hat; float hd = hat == 3 ? -0.35f : hat == 1 ? -0.3f : 0;
                float e = clamp(P[EARS], -1, 1), bse = hd + (e >= 0 ? (hd != 0 ? 0.04f : 0.2f) * e : 0.3f * e);
                sp[S_EARL + 1] += (headRY * 1.2f - headRZ * 1.6f) * 60 * dt; sp[S_EARR + 1] += (-headRY * 1.2f - headRZ * 1.6f) * 60 * dt;
                float aL = spr(sp, S_EARL, bse, 3.6f, 0.2f, dt), aR = spr(sp, S_EARR, bse, 3.6f, 0.2f, dt);
                float eHi = hd != 0 ? hd + 0.08f : 0.35f, eLo = hd != 0 ? hd - 0.3f : -0.45f;
                if (xb.earL != null) rotXYZ(xb.earL, 0, 0, clamp(aL, eLo, eHi));
                if (xb.earR != null) rotXYZ(xb.earR, 0, 0, -clamp(aR, eLo, eHi));
            }
        }

        // Articulated hands. h: −1 fist · 0 grip (rest) · 1 relaxed · 2 open. Curl is about local Z (left + opens, right
        // mirrored); spread about local X (same sign both sides). A gripped weapon or the bomb forces 0.
        void _applyFingers(float[] P, float dt)
        {
            bool bombOn = bomb != null && bomb.group.gameObject.activeSelf;
            float gL = Mathf.Max(clamp(P[IKL], 0, 1) * (1 - clamp(P[LTW], 0, 1)), bombOn ? 1 : 0);
            // micro-grip: fingers squeeze on every shot / hit and settle back (spring); an idle re-grip now and then
            gripT -= dt;
            if (gripT <= 0)
            {
                gripT = 4.5f + rng() * 6;
                if (lifeLv == 2 && kidForm && dance == null && wAim < 0.2f && gaitW < 0.3f) { sp[S_GRIP + 1] -= 3.2f; if (gL > 0.5f && rng() < 0.5f) sp[S_GRIPL + 1] -= 2.6f; }
            }
            float sqR = spr(sp, S_GRIP, 0, 5.2f, 0.42f, dt), sqL = spr(sp, S_GRIPL, 0, 5.2f, 0.42f, dt);
            handS[0] = damp(handS[0], clamp(lerp(P[HANDPL], 0, gL), -1, 2), 20, dt);
            handS[1] = damp(handS[1], clamp(P[HANDPR], -1, 2), 20, dt);
            for (int sd = 0; sd < 2; sd++)
            {
                var F = fing[sd]; if (F == null) continue;
                float side = sd == 0 ? 1 : -1, h = handS[sd];
                float gq = clamp((sd == 0 ? sqL : sqR) * 0.05f, -0.2f, 0.14f) * clamp(1 - Mathf.Abs(h), 0, 1);   // + tighter, − looser
                float f1 = hk(h, -0.35f, 0, 0.45f, 0.9f) - gq, f2 = hk(h, -0.5f, 0, 0.35f, 0.75f) - gq * 1.3f;
                float relaxW = clamp(h, 0, 1) * clamp(2 - h, 0, 1), openW = clamp(h - 1, 0, 1);
                float life = 0.025f * Mathf.Sin(t * 0.9f + sd * 2.1f) * relaxW;
                // trigger finger: the right index squeezes with the weapon's trigger blade (only while gripping)
                float trig = sd == 1 && weapon != null ? weapon.trig * clamp(1 - Mathf.Abs(h), 0, 1) : 0;
                for (int k = 0; k < 4; k++)
                {
                    float casc = (k - 1.5f) * 0.07f * relaxW; // the pinky side curls a little more than the index side
                    float sq = k == 0 ? trig : 0;
                    rotXYZ(F.f1[k], FINGER_SPREAD[k] * (openW + 0.35f * relaxW), 0, side * (f1 - casc + life - 0.3f * sq));
                    rotXYZ(F.f2[k], 0, 0, side * (f2 - casc * 0.8f + life * 0.5f - 0.35f * sq));
                }
                rotXYZ(F.t1, hk(h, 0, 0, 0.35f, 0.55f), side * hk(h, -0.1f, 0, 0, 0), side * hk(h, 0.12f, 0, 0, 0));
                rotXYZ(F.t2, 0, 0, side * hk(h, 0.3f, 0, -0.25f, -0.5f));
            }
        }

        void _updateTank(float dt)
        {
            var T = tank;
            // slosh driven by body acceleration (kid space), lagging and ringing like a liquid
            float tx = spr(sp, S_TANKX, clamp(kax * 0.004f, -0.1f, 0.1f), 1.6f, 0.14f, dt);
            float tz = spr(sp, S_TANKZ, clamp(-kaz * 0.004f, -0.1f, 0.1f), 1.6f, 0.14f, dt);
            float tl = spr(sp, S_TANKL, 0, 2.4f, 0.18f, dt);
            slosh = Mathf.Sqrt(tx * tx + tz * tz);
            var f = T.fill.transform;
            rotXYZ(f, clamp(tz, -0.06f, 0.06f), 0, clamp(tx, -0.06f, 0.06f));
            float lvl = Mathf.Max(0.004f, inkS * (1 + clamp(tl * 0.06f, -0.08f, 0.08f)));
            f.localScale = new Vector3(1 - slosh * 0.03f, lvl * T.h, 1 - slosh * 0.03f);
            T.fill.enabled = inkS > 0.005f;
        }

        /** kid-space transform of a bone (walks up to the kid; ignores bone scale). */
        void _kidXform(Transform bone, out Vector3 pos, out Quaternion quat)
        {
            pos = Vector3.zero; quat = Quaternion.identity;
            var o = bone;
            while (o != null && o != kid) { pos = o.localRotation * pos + o.localPosition; quat = o.localRotation * quat; o = o.parent; }
        }

        /** Analytic two-bone IK in kid space. target = end-bone origin, pole = bend direction, endQuat = kid-space end orientation. */
        void _solveLimb(Limb L, Vector3 target, Vector3 pole, Quaternion? endQuat, float weight, int errSlot)
        {
            _kidXform(L.up.parent, out var sP, out var sQp);
            Vector3 pA = sQp * L.up.localPosition + sP;
            float a = L.a, b = L.b;
            Vector3 pD = target - pA;
            float dist = pD.magnitude;
            float dmin = Mathf.Abs(a - b) + 1e-3f, dmax = (a + b) * 0.9995f;
            ikErr[errSlot] = Mathf.Max(0, dist - dmax);
            dist = clamp(dist, dmin, dmax);
            pD.Normalize();
            float cosA = clamp((a * a + dist * dist - b * b) / (2 * a * dist), -1, 1), sinA = Mathf.Sqrt(1 - cosA * cosA);
            Vector3 pN = pole - pD * Vector3.Dot(pole, pD);
            if (pN.sqrMagnitude < 1e-8f) pN = new Vector3(0, 0, 1) - pD * pD.z;
            pN.Normalize();
            Vector3 pE = pA + pD * (a * cosA) + pN * (a * sinA);
            Vector3 sT = pA + pD * dist;
            Vector3 pH = Vector3.Cross(pN, pD).normalized;
            Vector3 by = (pE - pA).normalized;
            Vector3 bz = Vector3.Cross(by, pH);
            Quaternion sQa = basis(by, pH, bz, L.Mu0T);
            Quaternion sQb = Quaternion.Inverse(sQp) * sQa;
            L.up.localRotation = Quaternion.Slerp(L.up.localRotation, sQb, weight);
            by = (sT - pE).normalized;
            bz = Vector3.Cross(by, pH);
            sQa = basis(by, pH, bz, L.Mf0T);
            sQp = sQp * L.up.localRotation;
            sQb = Quaternion.Inverse(sQp) * sQa;
            L.lo.localRotation = Quaternion.Slerp(L.lo.localRotation, sQb, weight);
            if (endQuat.HasValue)
            {
                sQp = sQp * L.lo.localRotation;
                sQb = Quaternion.Inverse(sQp) * endQuat.Value;
                L.end.localRotation = Quaternion.Slerp(L.end.localRotation, sQb, weight);
            }
        }
        static Quaternion basis(Vector3 x, Vector3 y, Vector3 z, Matrix4x4 M)
        {
            var m = Matrix4x4.identity; m.SetColumn(0, x); m.SetColumn(1, y); m.SetColumn(2, z);
            return (m * M).rotation;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Hair: per-strand spring chains driven by head inertia (linear + angular), drag and gravity; the "into the
        // head" component of every bend is removed so strands never swing through the skull.
        // ---------------------------------------------------------------------------------------------------------
        void _updateHair(float dt)
        {
            var B = bones;
            // kid world matrix in three space (root · model · kid)
            Matrix4x4 kidW = Matrix4x4.TRS(root.localPosition, root.localRotation, Vector3.one)
                           * Matrix4x4.TRS(model.localPosition, model.localRotation, Vector3.one)
                           * Matrix4x4.TRS(kid.localPosition, kid.localRotation, kid.localScale);
            _kidXform(B["head"], out var v2, out var q1);
            Vector3 v1 = kidW.MultiplyPoint3x4(q1 * HEAD_CTR + v2); // head centre, world
            Quaternion q2 = root.localRotation * model.localRotation * kid.localRotation * q1; // head world quat
            if (!headInit || dt <= 0)
            {
                headPrevPos = v1; headPrevVel = Vector3.zero; headVel = Vector3.zero; headPrevQuat = q2; headInit = true;
                if (dt <= 0) return;
            }
            Vector3 v3 = (v1 - headPrevPos) / dt;
            if (v3.sqrMagnitude > 900) v3 = v3.normalized * 30;
            Vector3 v4 = (v3 - headPrevVel) / dt;
            if (v4.magnitude > 34) v4 = v4.normalized * 34; // instant engine accelerations would fling the strands flat
            headAcc = Vector3.Lerp(headAcc, v4, 1 - Mathf.Exp(-dt * 24));
            headVel = Vector3.Lerp(headVel, v3, 1 - Mathf.Exp(-dt * 12));
            headPrevPos = v1; headPrevVel = v3;
            // apparent gravity + air drag, in head space (delta vs. rest)
            const float g = 9.8f;
            Vector3 v5 = new Vector3(0, -g, 0) - headAcc + headVel * -0.55f;
            v5 = Quaternion.Inverse(q2) * v5;
            v5.y += g;
            // head rotation delta (local)
            Quaternion q5 = Quaternion.Inverse(headPrevQuat) * q2;
            if (q5.w < 0) { q5.x = -q5.x; q5.y = -q5.y; q5.z = -q5.z; q5.w = -q5.w; }
            float sAng = 2 * Mathf.Acos(clamp(q5.w, -1, 1));
            float snq = Mathf.Sqrt(Mathf.Max(1e-12f, 1 - q5.w * q5.w));
            float rx = (q5.x / snq) * sAng, ry = (q5.y / snq) * sAng, rz = (q5.z / snq) * sAng;
            headRY = clamp(ry, -0.2f, 0.2f); headRZ = clamp(rz, -0.2f, 0.2f);
            headPrevQuat = q2;
            int steps = Mathf.Min(4, Mathf.Max(1, Mathf.CeilToInt(dt * 60 - 0.25f))); float h = dt / steps;
            const float GAIN = 0.5f, GAIN1 = 0.3f, GAIN2 = 0.2f;
            // heavy gummy strands: ~20 % lower frequency, a touch more damping, more lag behind head turns,
            // less fling from linear acceleration, a heavier club tip that still whips through
            const float HK = 0.62f, HZ = 0.3f, HINE = 1.15f, HGK = 0.85f, HCL = 0.68f, HBR = 0.6f;
            // idle life: a slow wave travelling down each strand + the curled tips coiling / uncoiling
            float idleL = (1 - gaitW) * (1 - wAir) * (kidForm ? 1 : 0), wv = TAU * 0.42f * t;
            var AX = hairAx; var A1 = hairA1; var A2 = hairA2;
            var hin = hairIn;
            int HS = CharacterGeo.HAIR_SEGS;
            for (int si = 0; si < hairMeta.Length; si++)
            {
                var m = hairMeta[si];
                float ux = m.dir.x, uy = m.dir.y, uz = m.dir.z;
                float tx = uy * v5.z - uz * v5.y, ty = uz * v5.x - ux * v5.z, tz = ux * v5.y - uy * v5.x;
                float gk = (m.G * 0.075f * HGK) * clamp(m.len / 0.22f, 0.4f, 1.6f);
                tx *= gk; ty *= gk * 0.3f; tz *= gk;
                float tl = Mathf.Sqrt(tx * tx + ty * ty + tz * tz); if (tl > 0.62f) { tx *= 0.62f / tl; ty *= 0.62f / tl; tz *= 0.62f / tl; }
                // collision-free: remove the bend component that would move the strand into the head (w = dir × in)
                float ix = hin[si * 3], iy = hin[si * 3 + 1], iz = hin[si * 3 + 2];
                float wx = uy * iz - uz * iy, wy = uz * ix - ux * iz, wz = ux * iy - uy * ix;
                float ww = wx * wx + wy * wy + wz * wz;
                float breeze = HBR * (0.03f * Mathf.Sin(t * 1.7f + si * 1.3f) + 0.012f * Mathf.Sin(t * 4.3f + si * 2.1f));
                for (int k = 0; k < HS; k++)
                {
                    int i = (si * HS + k) * 3;
                    float gain = k == 0 ? GAIN : k == 1 ? GAIN1 : GAIN2;
                    float ine = (k == 0 ? 0.55f : k == 1 ? 0.3f : 0.15f) * clamp(1.2f - m.K * 0.3f, 0.3f, 1) * HINE;
                    hx[i] -= rx * ine; hx[i + 1] -= ry * ine * 0.5f; hx[i + 2] -= rz * ine;
                    float K = 170 * HK * m.K * (k == 0 ? 1 : k == 1 ? 0.75f : 0.55f) / clamp(m.len / 0.22f, 0.6f, 1.6f); float D = 2 * HZ * Mathf.Sqrt(K);
                    float gx = tx * gain + breeze * 0.3f, gy = ty * gain, gz2 = tz * gain + breeze * 0.5f;
                    int j = (si * (HS + 1) + k) * 3;
                    if (idleL > 0.01f)
                    {
                        float w0 = idleL * 0.03f * Mathf.Sin(wv - 0.6f * k + hairPh[si]) * clamp(1.6f - m.K * 0.4f, 0.3f, 1);
                        gx += (A2[j] * 0.8f + A1[j] * 0.35f) * w0; gy += (A2[j + 1] * 0.8f + A1[j + 1] * 0.35f) * w0; gz2 += (A2[j + 2] * 0.8f + A1[j + 2] * 0.35f) * w0;
                    }
                    for (int n = 0; n < steps; n++)
                    {
                        hv[i] += (K * (gx - hx[i]) - D * hv[i]) * h;
                        hv[i + 1] += (K * (gy - hx[i + 1]) - D * hv[i + 1]) * h;
                        hv[i + 2] += (K * (gz2 - hx[i + 2]) - D * hv[i + 2]) * h;
                        hx[i] += hv[i] * h; hx[i + 1] += hv[i + 1] * h; hx[i + 2] += hv[i + 2] * h;
                    }
                    if (ww > 1e-6f)
                    {
                        float into = (hx[i] * wx + hx[i + 1] * wy + hx[i + 2] * wz) / ww;
                        if (into > 0.05f)
                        {
                            float e = into - 0.05f; hx[i] -= wx * e; hx[i + 1] -= wy * e; hx[i + 2] -= wz * e;
                            float vi = (hv[i] * wx + hv[i + 1] * wy + hv[i + 2] * wz) / ww; if (vi > 0) { hv[i] -= wx * vi; hv[i + 1] -= wy * vi; hv[i + 2] -= wz * vi; }
                        }
                    }
                    hx[i] = clamp(hx[i], -HCL, HCL); hx[i + 1] = clamp(hx[i + 1], -HCL, HCL); hx[i + 2] = clamp(hx[i + 2], -HCL, HCL);
                    // no candy-wrapper: project the twist (rotation about the segment's own axis) out, keep ±0.12 rad of it
                    {
                        float ax = AX[j], ay = AX[j + 1], az = AX[j + 2];
                        float tw = hx[i] * ax + hx[i + 1] * ay + hx[i + 2] * az, exs = tw - clamp(tw, -0.12f, 0.12f);
                        hx[i] -= ax * exs; hx[i + 1] -= ay * exs; hx[i + 2] -= az * exs;
                        float tv = hv[i] * ax + hv[i + 1] * ay + hv[i + 2] * az; hv[i] -= ax * tv; hv[i + 1] -= ay * tv; hv[i + 2] -= az * tv;
                    }
                    var bone = hairBones[si * HS + k];
                    float bx = hx[i], byy = hx[i + 1], bzz = hx[i + 2];
                    float ang = Mathf.Sqrt(bx * bx + byy * byy + bzz * bzz);
                    bone.localRotation = ang > 1e-6f ? Quaternion.AngleAxis(ang * Mathf.Rad2Deg, new Vector3(bx / ang, byy / ang, bzz / ang)) : Quaternion.identity;
                }
                // club-shaped tip: one more, floppier stage that keeps bending the way the last segment bends
                var tip = hairTips[si];
                if (tip != null)
                {
                    int j = si * 3, i2 = (si * HS + 2) * 3; var px = tipX; var pv = tipV;
                    px[j] -= rx * 0.12f; px[j + 1] -= ry * 0.06f; px[j + 2] -= rz * 0.12f;
                    float K = 95 * 0.7f * m.K / clamp(m.len / 0.22f, 0.6f, 1.6f), D = 2 * 0.24f * Mathf.Sqrt(K);
                    int jt = (si * (HS + 1) + HS) * 3; float coil = idleL * 0.08f * Mathf.Sin(wv * 0.95f + hairPh[si] * 1.7f - 1.8f);
                    float gx = hx[i2] * 0.45f + tx * 0.12f + breeze * 0.25f + A1[jt] * coil, gy = hx[i2 + 1] * 0.3f + ty * 0.1f + A1[jt + 1] * coil, gz2 = hx[i2 + 2] * 0.45f + tz * 0.12f + breeze * 0.35f + A1[jt + 2] * coil;
                    for (int n = 0; n < steps; n++)
                    {
                        pv[j] += (K * (gx - px[j]) - D * pv[j]) * h; pv[j + 1] += (K * (gy - px[j + 1]) - D * pv[j + 1]) * h; pv[j + 2] += (K * (gz2 - px[j + 2]) - D * pv[j + 2]) * h;
                        px[j] += pv[j] * h; px[j + 1] += pv[j + 1] * h; px[j + 2] += pv[j + 2] * h;
                    }
                    px[j] = clamp(px[j], -0.4f, 0.4f); px[j + 1] = clamp(px[j + 1], -0.4f, 0.4f); px[j + 2] = clamp(px[j + 2], -0.4f, 0.4f);
                    { float ax = AX[jt], ay = AX[jt + 1], az = AX[jt + 2], tw = px[j] * ax + px[j + 1] * ay + px[j + 2] * az, exs = tw - clamp(tw, -0.12f, 0.12f); px[j] -= ax * exs; px[j + 1] -= ay * exs; px[j + 2] -= az * exs; }
                    float ang = Mathf.Sqrt(px[j] * px[j] + px[j + 1] * px[j + 1] + px[j + 2] * px[j + 2]);
                    tip.localRotation = ang > 1e-6f ? Quaternion.AngleAxis(ang * Mathf.Rad2Deg, new Vector3(px[j] / ang, px[j + 1] / ang, px[j + 2] / ang)) : Quaternion.identity;
                }
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Squid form: transform pops, dry hops with anticipation, dolphin arcs, swim undulation, climb wiggle, blinks.
        // ---------------------------------------------------------------------------------------------------------
        void _updateSquid(float dt, AnimState s)
        {
            var sq = squid;
            if (sqScale <= 0.001f) { sqInit = false; return; }
            string fm = form == "kid" ? (formPrev == "kid" ? "squid" : formPrev) : form;
            float v = hs;
            mats.setTime(t);
            Vector3 p; Quaternion q;
            float wigAmp = 0.012f, wigFreq = 9;
            bool local = false;
            float sy = 1, sxz = 1;
            bool airborne = !grounded;
            if (fm == "climb" && s.wallNormal != Vector3.zero)
            {
                // belly (+Z) into the wall, mantle (+Y) up the wall, wiggling side to side as it climbs
                Vector3 n = s.wallNormal.normalized;
                Vector3 by = UP - n * Vector3.Dot(UP, n);
                if (by.sqrMagnitude < 1e-4f) by = new Vector3(0, 0, 1);
                by.Normalize(); Vector3 bz = -n; Vector3 bx = Vector3.Cross(by, bz).normalized;
                var m1 = Matrix4x4.identity; m1.SetColumn(0, bx); m1.SetColumn(1, by); m1.SetColumn(2, bz);
                Quaternion q2 = m1.rotation;
                float climbV = Mathf.Min(1, Mathf.Abs(vyS) / 5 + v / 6);
                q2 = Quaternion.AngleAxis(Mathf.Sin(t * 11) * 0.16f * climbV * Mathf.Rad2Deg, bz) * q2;
                Quaternion q3 = root.localRotation * model.localRotation;     // model world rotation (three space)
                q = Quaternion.Inverse(q3) * q2;
                Vector3 v5 = root.localPosition + UP * (0.26f + 0.025f * Mathf.Sin(t * 14) * climbV) + n * (-climbInset - 0.07f);
                v5 += bx * (0.022f * Mathf.Sin(t * 5.5f) * climbV);
                p = Quaternion.Inverse(model.localRotation) * (Quaternion.Inverse(root.localRotation) * (v5 - root.localPosition) - model.localPosition);
                sy = 1 + 0.1f * climbV + 0.05f * Mathf.Sin(t * 14) * climbV; sxz = 1 / Mathf.Sqrt(sy);
                wigAmp = 0.014f + 0.022f * climbV; wigFreq = 10 + 8 * climbV;
                local = true;
            }
            else if (fm == "swim" && !airborne)
            {
                if (v > 0.3f) sqYaw = dampAngleC(sqYaw, Mathf.Atan2(mdx, mdz), 10, dt);
                float sv = Mathf.Min(1, v / 11);
                float und = Mathf.Sin(t * lerp(5, 14, sv));
                sqRoll = damp(sqRoll, clamp(-yawRate * 0.08f, -0.5f, 0.5f), 6, dt);
                q = qYXZ(Mathf.PI / 2 + 0.06f * und * sv, sqYaw + 0.1f * Mathf.Sin(t * lerp(4, 11, sv) + 1) * sv, sqRoll + und * 0.12f * Mathf.Min(1, v / 4));
                p = new Vector3(0, -0.085f + 0.012f * Mathf.Sin(t * 5), 0);
                p += q * new Vector3(0, -0.12f, 0);
                sy = 1 + 0.2f * sv + 0.03f * und * sv; sxz = 1 / Mathf.Sqrt(sy);
                wigAmp = 0.018f + 0.022f * sv; wigFreq = 12 + 8 * sv;   // the travelling arm wave reads well up to ≈ 0.04
                local = true;
            }
            else if (airborne && (hs > 3 || fm == "swim" || formPrev == "climb"))
            {
                // dolphin arc: mantle follows the flight path (nose up rising, nose down falling), stretched along it
                float pit = Mathf.Atan2(vyS, Mathf.Max(hs, 0.5f));
                if (v > 0.3f) sqYaw = dampAngleC(sqYaw, Mathf.Atan2(mdx, mdz), 8, dt);
                q = qYXZ(Mathf.PI / 2 - pit, sqYaw, 0);
                p = new Vector3(0, 0.22f, 0);
                float sv = Mathf.Min(1, Mathf.Sqrt(hs * hs + vyS * vyS) / 10);
                sy = 1 + 0.22f * sv; sxz = 1 / Mathf.Sqrt(sy);
                wigAmp = 0.03f; wigFreq = 16;
            }
            else
            {
                // dry squid: hops (anticipation squash → stretch in the air → splat landing), or an idle bob
                float hop = 0, tilt = 0;
                if (!airborne && v > 0.25f)
                {
                    hopPhase += dt * (v / 0.72f);
                    float hp = frac(hopPhase);
                    float k = Mathf.Min(1, v / 1.5f);
                    float airU = clamp((hp - 0.14f) / 0.72f, 0, 1);
                    hop = 0.14f * Mathf.Sin(Mathf.PI * airU) * k;
                    tilt = (0.3f * Mathf.Cos(Mathf.PI * airU) - 0.1f) * k;
                    float anti = hp < 0.14f ? Mathf.Sin(Mathf.PI * hp / 0.14f) : 0;
                    float land = hp > 0.86f ? Mathf.Sin(Mathf.PI * (hp - 0.86f) / 0.14f) : 0;
                    sy = 1 - 0.2f * anti * k + 0.16f * Mathf.Sin(Mathf.PI * airU) * k - 0.16f * land * k;
                    if (v > 0.3f) sqYaw = dampAngleC(sqYaw, Mathf.Atan2(mdx, mdz), 10, dt);
                }
                else if (airborne)
                {
                    hopPhase = 0;
                    sy = 1 + clamp(Mathf.Abs(vyS) * 0.022f, 0, 0.24f);
                    tilt = clamp(-vyS * 0.03f, -0.3f, 0.3f);
                }
                else
                {
                    hopPhase = 0;
                    sy = 1 + 0.035f * Mathf.Sin(t * 3.1f) + 0.012f * Mathf.Sin(t * 7.3f);
                    sqYaw = dampAngleC(sqYaw, 0, 3, dt);
                }
                float sqy = spr(sp, S_SQY, 0, 5, 0.3f, dt), sqp = spr(sp, S_SQP, 0, 4, 0.35f, dt);
                sy *= 1 + clamp(sqy, -0.3f, 0.3f);
                tilt += sqp * 0.3f;
                q = qYXZ(tilt, sqYaw, 0.05f * Mathf.Sin(t * 2.3f));
                p = new Vector3(0, 0.165f + hop, 0);
                sxz = 1 / Mathf.Sqrt(sy);
                wigAmp = 0.014f + 0.014f * Mathf.Min(1, v); wigFreq = 7 + 6 * Mathf.Min(1, v);
            }
            // transform gesture (see _updateFormScales): puddle → stretch → wobble on the way in, crouch → shoot up on the way out
            sy *= sqSY; sxz *= sqSXZ;
            sq.pivot.localScale = new Vector3(sxz, sy, sxz);
            if (!sqInit) { sqPos = p; sqQuat = q; sqInit = true; }
            float kk = 1 - Mathf.Exp(-dt * 26);
            sqPos = Vector3.Lerp(sqPos, p, kk); sqQuat = Quaternion.Slerp(sqQuat, q, kk);
            sq.pivot.localPosition = sqPos; sq.pivot.localRotation = sqQuat;
            float pop = sqScale;
            squidRoot.localScale = Vector3.one * Mathf.Max(0.001f, pop);
            squidRoot.localPosition = Vector3.zero;
            mats.setWig(new Vector3(wigAmp, wigFreq, clamp(v / 11, 0, 1)));
            // blinks (squash the eyes about their centre)
            float bk = blinkK;
            float ey = Mathf.Max(0.08f, 1 - 0.92f * bk);
            sq.eyes.transform.localScale = new Vector3(1, ey, 1); sq.eyes.transform.localPosition = new Vector3(0, 0.09f * (1 - ey), 0);
            bool ghost = local && isLocal;
            sq.ghost.enabled = ghost;
            sq.eyes.enabled = !(local && !isLocal);
            sq.dark.enabled = sq.eyes.enabled;
            if (!kidForm) _blinkTick(dt);
        }

        // ---------------------------------------------------------------------------------------------------------
        void _updateMaterials(float dt, AnimState s)
        {
            mats.updateRim(inWorld);
            float fl = s.invuln ? 0.22f * (0.5f + 0.5f * Mathf.Sin(t * TAU * 6)) : 0;
            float pul = 0.5f + 0.5f * Mathf.Sin(t * TAU * 1.6f);
            float gl = wGlow * (0.35f + 0.45f * pul);
            float blink = wLow * (0.5f + 0.5f * Mathf.Sin(t * TAU * 3.2f));
            float fillE = 0.12f + 0.9f * blink + 0.3f * wGlow * pul;
            if (!kid.gameObject.activeSelf)
            {
                var f = tank.fill.transform; var sc = f.localScale; sc.y = Mathf.Max(0.004f, inkS) * tank.h; f.localScale = sc;
                tank.fill.enabled = inkS > 0.005f;
            }
            float ch = animKind == "charger" ? charge : 0;
            float full = ch >= 0.995f ? 0.5f + 0.5f * Mathf.Sin(t * TAU * 8) : 0;
            float glowI = 0.15f + 2.6f * ch * ch + 1.5f * full + 3 * chargeFlash;
            mats.setFrame(fl, gl, fillE, glowI);
        }
    }
}
