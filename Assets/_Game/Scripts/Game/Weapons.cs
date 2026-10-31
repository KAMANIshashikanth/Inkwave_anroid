// Port of src/game/weapons.js — per-actor WeaponRunner (fire logic for shooter / roller / charger / blaster / bucket /
// spinner / twins / brush + the bomb sub) and the global Projectiles system (ink shots, blaster blobs, roller drops,
// bombs, storm clouds, charger beams + laser sights, the bomb arc preview).
//
// Accuracy: every shot leaves the muzzle aimed at the crosshair's world point and shooter shots get a ballistic
// launch-pitch correction (same integrator as the flight) so, inside the weapon's range, they land on the crosshair
// instead of dropping under it; spread is a cone around that corrected line (shooter: first-shot accurate, blooms with
// sustained fire, recovers when you let go). Hit tests use the victim's visual (smoothed) body.
//
// Not ported: upstream's legacy dualies / slosher / splatling (config.js WEAPON_SUCCESSOR maps them to twins / bucket /
// spinner and no menu offers them), the online ghosts (src/net) and the Boss Battle hooks (M7). Kit weapons
// (kits/*.js) plug in through Kits.MAIN; the subs.js / specials.js hooks are marked where those modules attach.
using System.Collections.Generic;
using Inkwave.World;
using UnityEngine;
using UnityEngine.Rendering;
using static Inkwave.Ctx;

namespace Inkwave
{
    public static partial class G
    {
        public static Projectiles projectiles;
    }

    public class WeaponInput { public bool fire, firePressed, sub, subReleased; }

    public static class Weapons
    {
        /** the weapon kinds WeaponRunner fires itself (the rest are kits: Kits.MAIN) */
        public static readonly HashSet<string> BUILTIN = new HashSet<string> { "shooter", "roller", "charger", "blaster", "bucket", "spinner", "twins", "brush" };
    }

    // ---------------------------------------------------------------------------------------------- per-actor runner
    public class WeaponRunner
    {
        public readonly Actor a;
        public float cooldown, charge, flick = -1, firingT, emptyCd, bloom, spread, rollT, chargeT, flickRecover, rumbleT, rollDist;
        public bool charging, rolling, aimingSub, chargeDinged;
        public readonly Dictionary<object, float> rollHits = new Dictionary<object, float>();
        public AudioLoop chargeLoop; AudioLoop rollLoop;
        Vector3 lastRollPos;
        // spinner: charged burst; twins: dodge roll + planted stance; brush/bucket reuse flick (swing) + rolling (brushing)
        public float burstT, burstPower, burstDur, streamCd;
        public float dodgeT, rollLock, sinceRoll = 9;
        public int rollsUsed;
        public bool rollQueued, turret;
        public Vector3 dodgeDir;
        public float twinSide = 1, swipeSide = 1;
        // per-hand shot clocks ([right, left] s since that hand fired)
        public readonly float[] sinceHand = { 99, 99 };
        public float lockT;   // upstream dualies' post-roll lock (not ported: always 0; the character reads it)
        public object kit;    // kit weapons (kits/*.js) keep their per-runner state here (BrollyState, KitState …)
        public object subKit; // a kit sub's hold state (shaker.js runner.shaker: ShakerState)
        public int streamN;

        public WeaponRunner(Actor actor) { a = actor; reset(); }

        MainKit K => Kits.main(a.weapon?.kind);

        public void reset()
        {
            cooldown = 0; charge = 0; charging = false; rolling = false;
            flick = -1; firingT = 0; emptyCd = 0; aimingSub = false;
            bloom = 0; spread = 0; rollT = 0; chargeT = 0; flickRecover = 0; rumbleT = 0;
            rollDist = 0; rollHits.Clear(); chargeLoop?.stop(0.05f); chargeLoop = null; chargeDinged = false;
            rollLoop?.stop(0.1f); rollLoop = null;
            burstT = 0; burstPower = 0; burstDur = 0; streamCd = 0;
            dodgeT = 0; rollsUsed = 0; rollLock = 0; sinceRoll = 9; rollQueued = false;
            turret = false; twinSide = 1; swipeSide = 1;
            sinceHand[0] = sinceHand[1] = 99;
            lockT = 0;
            kit = null;
            K?.reset(this);
        }
        public void onDeath() => reset();
        public bool busy() => charging || flick >= 0 || burstT > 0 || dodgeT > 0 || lockT > 0 || (K?.busy(this) ?? false);
        public bool firingPose() => firingT > 0 || charging || flick >= 0 || rolling || burstT > 0 || turret || lockT > 0 || (K?.firingPose(this) ?? false);

        public float moveSpeed()
        {
            var w = a.weapon;
            { float v = K?.moveSpeed(this, w) ?? 0; if (v != 0) return v; }
            switch (w.kind)
            {
                case "brush":   // brushing is faster than running; swipes barely slow you
                    if (rolling) return lerp(w.moveSpeedFiring, w.brushSpeed, smoothstep(0, 0.35f, rollT));
                    return firingT > 0 ? w.moveSpeedFiring : PLAYER.runSpeed;
                case "bucket":
                    if (flick >= 0) return w.moveSpeedFiring * 0.7f;
                    return firingT > 0 ? w.moveSpeedFiring : PLAYER.runSpeed;
                case "spinner":
                    if (burstT > 0) return w.moveSpeedFiring;
                    if (charging) return lerp(PLAYER.runSpeed * 0.75f, w.moveSpeedCharging, Mathf.Min(1, charge * 2));
                    return PLAYER.runSpeed;
                case "twins":
                    return firingT > 0 ? w.moveSpeedFiring : PLAYER.runSpeed;
            }
            if (lockT > 0) return 0;
            // roller: the drum has weight — rolling speed builds up over ~0.45 s; the flick wind-up plants you
            if (rolling) return lerp(w.rollSpeed * 0.5f, w.rollSpeed, smoothstep(0, 0.45f, rollT));
            if (flick >= 0) return lerp(w.moveSpeedFiring, w.moveSpeedFiring * 0.45f, clamp(flick / w.flickWindup, 0, 1));
            if (flickRecover > 0) return lerp(PLAYER.runSpeed, w.moveSpeedFiring * 0.6f, flickRecover / 0.18f);
            if (charging) return lerp(PLAYER.runSpeed * 0.7f, w.moveSpeedFiring, Mathf.Min(1, charge * 3));
            if (firingT > 0) return w.moveSpeedFiring;
            return PLAYER.runSpeed;
        }

        // current shot cone half-angle in degrees (the HUD crosshair uses this)
        public float spreadDeg(WeaponDef w)
        {
            { var v = Kits.main(w.kind)?.spreadDeg(this, w); if (v.HasValue) return v.Value; }
            if (w.kind == "shooter")
            {
                float bas = a.grounded ? w.spreadGround : w.spreadAir;
                return bas * lerp(0.45f, 1, bloom);   // (spreadFirst ?? 0.45)
            }
            if (w.kind == "blaster") return a.grounded ? 1.2f : (w.spreadAir > 0 ? w.spreadAir : 4);   // (spread ?? 1.2)
            if (w.kind == "twins") return turret ? w.turretSpread : (a.grounded ? w.spreadGround : w.spreadAir) * lerp(0.5f, 1, bloom);
            if (w.kind == "spinner") return a.grounded ? w.spreadGround : w.spreadAir;
            return 0;
        }

        public void update(float dt, WeaponInput inp)
        {
            var w = a.weapon;
            cooldown -= dt; emptyCd -= dt; rumbleT -= dt;
            firingT = Mathf.Max(0, firingT - dt);
            flickRecover = Mathf.Max(0, flickRecover - dt);
            // spread bloom recovers when the trigger is released (and slowly while still firing between shots)
            if (!inp.fire) bloom = Mathf.Max(0, bloom - dt / 0.28f);   // (bloomRecover ?? 0.28)
            spread = spreadDeg(w);
            sinceHand[0] += dt; sinceHand[1] += dt;
            switch (w.kind)
            {
                case "shooter": case "blaster": auto(dt, inp, w); break;
                case "charger": charger(dt, inp, w); break;
                case "roller": roller(dt, inp, w); break;
                case "bucket": bucket(dt, inp, w); break;
                case "spinner": spinner(dt, inp, w); break;
                case "twins": twins(dt, inp, w); break;
                case "brush": brush(dt, inp, w); break;
                default:
                    var k = K;
                    if (k != null) k.update(this, dt, inp, w);   // kit weapons (kits/*.js)
                    else auto(dt, inp, Config.WEAPONS["shooter"]);   // a kit not ported yet fires as the Spritzer
                    break;
            }
            // ---- sub weapon (the actor's own: the Splat Bomb here, everything else via G.subs). (Bomb Barrage — its bomb, no
            // ink, as fast as its throw gap allows — arrives with specials.js.)
            var sub = a.sub ?? Config.SUBS["bomb"];
            var SK = Kits.sub(sub.kind);
            if (aimingSub && SK != null) SK.hold(this, dt, inp, sub);   // charge-up subs
            if (inp.sub && !aimingSub)
            {
                aimingSub = true;
                if (a.ink < sub.inkCost && a.isLocal) { G.audio?.play("low_ink"); emit("lowink", new Ev { actor = a, need = sub.inkCost }); }
            }
            if (aimingSub) a.fireFacing = 0.3f;
            if (inp.subReleased && aimingSub)
            {
                aimingSub = false;
                // "Can't use": a kit sub that only allows one out at a time (or similar) refuses the throw, ink untouched
                bool refused = SK != null && SK.blocked(a, sub);
                if (refused) emit("sub:cantuse", new Ev { actor = a, kind = sub.kind });
                if (!refused && a.ink >= sub.inkCost)
                {
                    a.ink -= sub.inkCost;
                    a.lastFire = 0;
                    a.character.trigger("throw");
                    if (sub.kind == "bomb") G.projectiles.throwBomb(a); else G.subs.use(a, sub);
                    Actor.rumble(a, 0.08f, 0.22f, 70);
                }
            }
            if (!inp.sub && !inp.subReleased) aimingSub = false;
        }

        Vector3? sndPos => a.isLocal ? (Vector3?)null : a.pos;

        public void empty()
        {
            if (emptyCd > 0) return;
            emptyCd = 0.45f;
            if (a.isLocal) { G.audio?.play("empty_click"); emit("lowink", new Ev { actor = a }); }
        }

        void auto(float dt, WeaponInput inp, WeaponDef w)
        {
            if (!inp.fire) { if (cooldown < 0) cooldown = 0; return; }
            firingT = 0.35f;
            a.fireFacing = 0.5f;
            int guard = 0;
            while (cooldown <= 0 && guard++ < 3)
            {
                if (a.ink < w.inkPerShot) { empty(); cooldown += w.fireInterval; break; }
                a.ink -= w.inkPerShot;
                a.lastFire = 0;
                spread = spreadDeg(w);
                if (w.kind == "blaster") G.projectiles.fireBlaster(a, w, spread);
                else G.projectiles.fireShooter(a, w, spread);
                bloom = Mathf.Min(1, bloom + 0.3f);   // (bloomPerShot ?? 0.3)
                a.character.trigger("shoot");
                cooldown += w.fireInterval;
            }
        }

        void charger(float dt, WeaponInput inp, WeaponDef w)
        {
            if (inp.fire && cooldown <= 0)
            {
                if (!charging)
                {
                    if (a.ink < w.inkFull * 0.2f) { empty(); return; }
                    charging = true; charge = 0; chargeT = 0; chargeDinged = false;
                    if (a.isLocal || a.nearCamera()) chargeLoop = G.audio?.loop("charger_charge", new AudioOpts { pos = sndPos, volume = a.isLocal ? 0.55f : 0.35f, pitch = 1 });
                }
                float maxCharge = clamp(a.ink / w.inkFull, 0, 1);
                // charge builds on a gentle S-curve (quick first 20 % so taps are useful, a committed middle, a crisp top-off)
                chargeT = Mathf.Min(1, chargeT + dt / w.chargeTime);
                float t = chargeT, curve = t < 0.2f ? t * 1.25f : 0.25f + (t - 0.2f) * 0.9375f;
                charge = Mathf.Min(maxCharge, curve);
                a.fireFacing = 0.4f;
                chargeLoop?.set(pitch: 1 + charge * 1.5f, pos: sndPos);
                if (charge >= 1 && !chargeDinged)
                {
                    chargeDinged = true;
                    if (a.isLocal) G.audio?.play("charger_full", new AudioOpts { volume = 0.7f });
                    Actor.rumble(a, 0.05f, 0.3f, 60);
                }
            }
            else if (charging)
            {
                charging = false;
                chargeLoop?.stop(0.05f); chargeLoop = null;
                float c = Mathf.Max(0.12f, charge);
                a.ink = Mathf.Max(0, a.ink - w.inkFull * c);
                a.lastFire = 0;
                G.projectiles.fireCharger(a, w, c);
                a.character.trigger("charge_release");
                charge = 0; chargeT = 0;
                firingT = 0.35f;
                cooldown = 0.28f;
            }
        }

        void roller(float dt, WeaponInput inp, WeaponDef w)
        {
            // flick wind-up → release
            if (flick >= 0)
            {
                flick += dt;
                a.fireFacing = 0.4f;
                if (flick >= w.flickWindup)
                {
                    flick = -1;
                    G.projectiles.fireFlick(a, w);
                    cooldown = w.flickInterval - w.flickWindup;
                    firingT = 0.25f;
                    flickRecover = 0.18f;
                }
                return;
            }
            if (inp.firePressed && cooldown <= 0)
            {
                if (a.ink < w.flickInk) empty();
                else
                {
                    a.ink -= w.flickInk; a.lastFire = 0;
                    flick = 0;
                    a.character.trigger("flick", 0);
                    if (a.isLocal || a.nearCamera()) G.audio?.play("roller_flick", new AudioOpts { pos = sndPos, volume = 0.8f });
                    return;
                }
            }
            bool canRoll = inp.fire && a.grounded && a.ink > 0.5f && cooldown <= 0.25f;
            rollT = canRoll ? rollT + dt : 0;
            if (canRoll != rolling)
            {
                rolling = canRoll;
                if (canRoll) { lastRollPos = a.pos; rollDist = 0; }
                if (canRoll && (a.isLocal || a.nearCamera())) rollLoop = G.audio?.loop("roll", new AudioOpts { pos = sndPos, volume = 0 });
                if (!canRoll) { rollLoop?.stop(0.12f); rollLoop = null; }
            }
            if (inp.fire && a.ink <= 0.5f) empty();
            if (!rolling) return;
            a.lastFire = 0;
            float hs = Mathf.Sqrt(a.vel.x * a.vel.x + a.vel.z * a.vel.z);
            rollLoop?.set(clamp(hs / w.rollSpeed, 0, 1) * (a.isLocal ? 0.7f : 0.45f), 0.6f + clamp(hs / w.rollSpeed, 0, 1), sndPos);
            float moved = Vector3.Distance(a.pos, lastRollPos);
            // roll damage in front of the drum
            float fx = Mathf.Sin(a.yaw), fz = Mathf.Cos(a.yaw);
            foreach (var e in G.actors)
            {
                if (e.team == a.team || !e.alive) continue;
                float dx = e.pos.x - a.pos.x, dz = e.pos.z - a.pos.z, dy = e.pos.y - a.pos.y;
                float fwd = dx * fx + dz * fz, lat = Mathf.Abs(dx * fz - dz * fx);
                if (fwd > -0.2f && fwd < 1.35f && lat < w.rollWidth / 2 + 0.35f && Mathf.Abs(dy) < 1.2f && hs > 1.0f)
                {
                    float last = rollHits.TryGetValue(e, out var l) ? l : -9;
                    if (G.time - last > 0.5f) { rollHits[e] = G.time; G.projectiles.applyHit(a, e, w.rollDamage, "roller"); }
                }
            }
            // (Boss Battle: the drum crushes into HULLBREAKER's claws / belly — M7)
            if (moved < 0.28f) return;
            lastRollPos = a.pos;
            a.ink = Mathf.Max(0, a.ink - w.rollInkPerMeter * moved);
            // paint a stripe across the drum: kind 'roll' + the roll direction → paint lays one straight-edged band segment
            // per splat (identical on the CPU turf grid) instead of round blobs, so rolled turf reads as a clean stripe
            float area = 0;
            float rx = fz, rz = -fx;   // right-ish perpendicular
            var fwdV = new Vector3(fx, 0, fz);
            for (int i = -1; i <= 1; i++)
            {
                float off = i * w.rollWidth * 0.33f;
                var v = new Vector3(a.pos.x + fx * 0.75f + rx * off, a.pos.y + 0.35f, a.pos.z + fz * 0.75f + rz * off);
                area += G.paint.splat(v, 0.62f, a.team, new SplatOpts { seed = random(), kind = "roll", stretch = fwdV });
            }
            a.addTurf(area);
            emit("weapon:impact", new Ev { pos = new Vector3(a.pos.x + fx * 0.75f, a.pos.y + 0.02f, a.pos.z + fz * 0.75f), normal = a.groundN, team = a.team, kind = "roll", radius = w.rollWidth / 2 });
            if (rumbleT <= 0) { rumbleT = 0.12f; Actor.rumble(a, 0.04f, clamp(hs / w.rollSpeed, 0, 1) * 0.14f, 110); }
        }

        void bucket(float dt, WeaponInput inp, WeaponDef w)
        {
            if (flick >= 0)
            {
                flick += dt; a.fireFacing = 0.45f;
                if (flick >= w.windup) { flick = -1; G.projectiles.fireSlosh(a, w); cooldown = w.fireInterval - w.windup; firingT = 0.35f; }
                return;
            }
            if (!(inp.fire || inp.firePressed) || cooldown > 0) return;
            if (a.ink < w.inkPerShot) { empty(); return; }
            a.ink -= w.inkPerShot; a.lastFire = 0;
            flick = 0;
            a.character.trigger("flick", 0);   // two-handed underarm throw
            if (a.isLocal || a.nearCamera()) G.audio?.play("roller_flick", new AudioOpts { pos = sndPos, volume = 0.7f, pitch = 0.8f });
        }

        // spinner: hold to spin up (like a charger), release to fire a stream whose length and reach follow the charge
        void spinner(float dt, WeaponInput inp, WeaponDef w)
        {
            if (burstT > 0)
            {
                burstT -= dt; streamCd -= dt; firingT = 0.2f; a.fireFacing = 0.5f; a.lastFire = 0;
                int guard = 0;
                while (streamCd <= 0 && guard++ < 4) { G.projectiles.fireStream(a, w, burstPower, spreadDeg(w)); streamCd += w.fireInterval; a.character.trigger("stream"); }
                chargeLoop?.set(pitch: 1.5f + 0.06f * Mathf.Sin(G.time * 31), pos: sndPos);
                if (burstT <= 0)
                {
                    burstT = 0; cooldown = 0.2f; chargeLoop?.stop(0.12f); chargeLoop = null;
                    if (a.isLocal || a.nearCamera()) G.audio?.play("splatling_wind", new AudioOpts { pos = sndPos, volume = a.isLocal ? 0.6f : 0.42f });
                }
                return;
            }
            if (inp.fire && cooldown <= 0)
            {
                if (!charging)
                {
                    if (a.ink < w.inkFull * 0.15f) { empty(); return; }
                    charging = true; charge = 0; chargeT = 0; chargeDinged = false;
                    if (a.isLocal || a.nearCamera()) chargeLoop = G.audio?.loop("splatling_spin", new AudioOpts { pos = sndPos, volume = a.isLocal ? 0.6f : 0.4f, pitch = 0.6f });
                }
                chargeT = Mathf.Min(1, chargeT + dt / w.chargeTime);
                charge = Mathf.Min(clamp(a.ink / w.inkFull, 0, 1), chargeT);
                a.fireFacing = 0.4f;
                chargeLoop?.set(pitch: 0.6f + 0.85f * charge, pos: sndPos);
                if (charge >= 1 && !chargeDinged)
                {
                    chargeDinged = true;
                    if (a.isLocal) G.audio?.play("splatling_ready", new AudioOpts { volume = 0.7f });
                    Actor.rumble(a, 0.05f, 0.28f, 60);
                }
            }
            else if (charging)
            {
                charging = false;
                float c = Mathf.Max(0.15f, charge);
                a.ink = Mathf.Max(0, a.ink - w.inkFull * c);
                burstPower = c; burstT = lerp(w.burstMin, w.burstMax, c); burstDur = burstT; streamCd = 0;
                charge = 0; chargeT = 0;
                a.character.trigger("charge_release");
            }
        }

        // twins: rapid alternating fire; after a dodge roll, holding fire plants you with both guns together (faster, tighter)
        void twins(float dt, WeaponInput inp, WeaponDef w)
        {
            rollLock = Mathf.Max(0, rollLock - dt); sinceRoll += dt;
            if (rollLock <= 0 && sinceRoll > w.rollReset && dodgeT <= 0) rollsUsed = 0;
            if (turret && (!inp.fire || Mathf.Sqrt(a.intent.move.x * a.intent.move.x + a.intent.move.z * a.intent.move.z) > 0.3f)) turret = false;   // walk = back to dual mode
            if (dodgeT > 0)
            {
                dodgeT -= dt;
                if (dodgeT <= 0)
                {
                    dodgeT = 0; turret = inp.fire;
                    // a second roll tapped during this one chains straight on (if fire + a direction are still held)
                    if (rollQueued) { rollQueued = false; if (twinDodge(a.intent)) return; }
                }
                return;
            }
            if (!inp.fire) { if (cooldown < 0) cooldown = 0; return; }
            firingT = 0.35f; a.fireFacing = 0.5f;
            int guard = 0;
            while (cooldown <= 0 && guard++ < 3)
            {
                if (a.ink < w.inkPerShot) { empty(); cooldown += w.fireInterval; break; }
                a.ink -= w.inkPerShot; a.lastFire = 0;
                twinSide = -twinSide;
                G.projectiles.fireTwin(a, w, spreadDeg(w), turret ? 0 : twinSide);
                bloom = Mathf.Min(1, bloom + 0.25f);
                // side +1 = the left gun, -1 = the right (planted mode keeps alternating the animation between both guns)
                int hand = twinSide > 0 ? 1 : 0;
                sinceHand[hand] = 0;
                a.character.trigger("shoot", hand == 1 ? 2 : 1);
                cooldown += turret ? w.turretInterval : w.fireInterval;
            }
        }

        // Jump pressed while grounded (actor.js): roll instead of jumping? (twins: needs fire held + a move direction).
        // Returns true if a roll started.
        public bool tryDodge(Intent intent)
        {
            if (a.weapon.kind == "twins") return intent != null && twinDodge(intent);
            return false;
        }

        // twins: jump pressed while firing and moving → roll instead of jumping. Returns true if a roll started.
        bool twinDodge(Intent intent)
        {
            var w = a.weapon;
            // jump tapped mid-roll with a roll left in the chain: queue it (and swallow the press, so it never turns into a jump)
            if (w.kind == "twins" && dodgeT > 0 && intent.fire && rollLock <= 0 && rollsUsed > 0 && rollsUsed < w.rollCharges) { rollQueued = true; return true; }
            if (w.kind != "twins" || !intent.fire || dodgeT > 0 || rollLock > 0 || !a.grounded || a.form != "kid") return false;
            float mh = Mathf.Sqrt(intent.move.x * intent.move.x + intent.move.z * intent.move.z);
            if (mh < 0.3f || a.ink < w.rollInk) return false;
            a.ink -= w.rollInk;
            dodgeDir = new Vector3(intent.move.x / mh, 0, intent.move.z / mh);
            dodgeT = w.rollTime; turret = false; sinceRoll = 0;
            // two rolls in a row, then a lockout before the next
            if (++rollsUsed >= w.rollCharges) { rollLock = w.rollLockout; rollsUsed = 0; }
            a.character.trigger("roll", 0, new Vector2(dodgeDir.x, dodgeDir.z));
            if (a.isLocal || a.nearCamera()) G.audio?.play("roll", new AudioOpts { pos = sndPos, volume = 0.7f, pitch = 1.6f });
            G.fx?.burst(new Vector3(a.pos.x, a.pos.y + 0.1f, a.pos.z), Vector3.up, a.color, new FxOpts { count = 8, speed = 3, size = 0.07f });
            emit("actor:dodge", new Ev { actor = a, dir = dodgeDir });
            emit("weapon:dodge", new Ev { actor = a, pos = a.pos, dir = dodgeDir });   // upstream's dodge splash FX
            Actor.rumble(a, 0.2f, 0.25f, 90);
            return true;
        }

        // twins: horizontal velocity while rolling (fast start, eases out)
        public bool dodgeVelocity(out Vector3 v)
        {
            v = default;
            if (dodgeT <= 0) return false;
            var w = a.weapon;
            float u = 1 - dodgeT / w.rollTime;       // 0 → 1 over the roll
            float v0 = w.rollDist / (0.7f * w.rollTime);
            v = dodgeDir * (v0 * (1 - 0.6f * u));
            return true;
        }

        // rolls left in the chain (HUD): twins (lockout → 0)
        public float rollsLeft() => rollLock > 0 ? 0 : a.weapon.rollCharges - rollsUsed;

        // cut any dodge roll short (a special taking over the body or the trigger mid-roll must not leave the roll
        // velocity running while the runner isn't being updated)
        public void endDodge() { dodgeT = 0; turret = false; lockT = 0; }

        // brush: hold fire while running to brush a thin fast trail (light contact damage, never a one-hit splat);
        // tap (or hold while standing) to swipe side to side, flicking a spray of small globs
        void brush(float dt, WeaponInput inp, WeaponDef w)
        {
            if (flick >= 0)
            {
                flick += dt; a.fireFacing = 0.4f;
                if (flick >= 0.04f) { flick = -1; G.projectiles.fireSwipe(a, w, swipeSide); cooldown = w.swipeInterval; firingT = 0.25f; }
                return;
            }
            float hs = Mathf.Sqrt(a.vel.x * a.vel.x + a.vel.z * a.vel.z);
            bool moving = a.grounded && hs > 2.0f && Mathf.Sqrt(a.intent.move.x * a.intent.move.x + a.intent.move.z * a.intent.move.z) > 0.3f;
            if ((inp.firePressed || (inp.fire && !moving)) && cooldown <= 0)
            {
                if (a.ink < w.swipeInk) empty();
                else
                {
                    a.ink -= w.swipeInk; a.lastFire = 0; flick = 0;
                    swipeSide = -swipeSide;
                    a.character.trigger("flick", swipeSide);   // sideways swipe, alternating direction
                    if (a.isLocal || a.nearCamera()) G.audio?.play("roller_flick", new AudioOpts { pos = sndPos, volume = 0.5f, pitch = 1.7f });
                    if (rolling) { rolling = false; rollLoop?.stop(0.08f); rollLoop = null; }
                    return;
                }
            }
            bool brushing = inp.fire && moving && a.ink > 0.3f;
            rollT = brushing ? rollT + dt : 0;
            if (brushing != rolling)
            {
                rolling = brushing;
                if (brushing) { lastRollPos = a.pos; rollHits.Clear(); }
                if (brushing && (a.isLocal || a.nearCamera())) rollLoop = G.audio?.loop("roll", new AudioOpts { pos = sndPos, volume = 0 });
                if (!brushing) { rollLoop?.stop(0.1f); rollLoop = null; }
            }
            if (!rolling) return;
            a.lastFire = 0;
            rollLoop?.set(clamp(hs / w.brushSpeed, 0, 1) * (a.isLocal ? 0.45f : 0.3f), 1.3f + clamp(hs / w.brushSpeed, 0, 1) * 0.8f, sndPos);
            float fx = Mathf.Sin(a.yaw), fz = Mathf.Cos(a.yaw);
            foreach (var e in G.actors)
            {
                if (e.team == a.team || !e.alive) continue;
                float dx = e.pos.x - a.pos.x, dz = e.pos.z - a.pos.z, dy = e.pos.y - a.pos.y;
                float fwd = dx * fx + dz * fz, lat = Mathf.Abs(dx * fz - dz * fx);
                if (fwd > -0.2f && fwd < 1.2f && lat < w.brushWidth / 2 + 0.3f && Mathf.Abs(dy) < 1.2f)
                {
                    float last = rollHits.TryGetValue(e, out var l) ? l : -9;
                    if (G.time - last > w.brushHitCd) { rollHits[e] = G.time; G.projectiles.applyHit(a, e, w.brushDamage, "brush"); }
                }
            }
            // (Boss Battle: the bristles drag across HULLBREAKER's claws / belly — M7)
            float moved = Vector3.Distance(a.pos, lastRollPos);
            if (moved < 0.22f) return;
            lastRollPos = a.pos;
            a.ink = Mathf.Max(0, a.ink - w.brushInkPerMeter * moved);
            var v = new Vector3(a.pos.x + fx * 0.65f, a.pos.y + 0.35f, a.pos.z + fz * 0.65f);
            a.addTurf(G.paint.splat(v, w.brushWidth * 0.45f, a.team, new SplatOpts { seed = random(), stretch = new Vector3(fx, 0, fz), stretchAmt = 0.5f }));
            emit("weapon:impact", new Ev { pos = new Vector3(v.x, a.pos.y + 0.02f, v.z), normal = a.groundN, team = a.team, kind = "roll", radius = w.brushWidth / 2 });
            if (rumbleT <= 0) { rumbleT = 0.12f; Actor.rumble(a, 0.02f, clamp(hs / w.brushSpeed, 0, 1) * 0.08f, 90); }
        }
    }

    // ---------------------------------------------------------------------------------------------- projectiles
    public class Projectile
    {
        public string type, wid, weaponId;
        public Actor owner; public int team;
        public Vector3 pos, prev, vel, start;
        public float age, life, straight, radius, damage, dmgFar, size, trail, trailEvery, trailRadius, grav, drag, seed, delay;
        public bool sp, spSet;
        // bucket: one throw (volley) lands one hit per victim; full damage at any range, less on targets below you
        public HashSet<Actor> volley;
        public float belowFalloff, belowFalloffMax;
        public BurstSpec burst;   // fireCustom blasts: their own mid-air burst
        // look (visual only): head radius, tail length (base + speed gain, in radii), wobble, nose flatten, satellites
        public float vis, tail0, tailK, wob, wobF, nose;
        public int sats;
        public int _bid;   // kit-scored shots (Canopy Brolly pellets): the kit's id; negated once scored / blocked
    }

    /** a blast's mid-air burst (specials' guns; the Popper Blaster's numbers otherwise). paint < 0 = radius × 0.6 */
    public class BurstSpec { public float radius, splashRadius, dmgMax, dmgMin, paint = -1; }

    /** stream-round look (visual only; hit size stays in 'size') */
    public struct Look
    {
        public float vis, tail0, tailK, wob, wobF, nose; public int sats;
        public Look(float vis, float tail0, float tailK, float wob, float wobF, float nose, int sats) { this.vis = vis; this.tail0 = tail0; this.tailK = tailK; this.wob = wob; this.wobF = wobF; this.nose = nose; this.sats = sats; }
    }

    /** Projectiles.fireCustom options (specials' guns); unset = the shooter / blaster default */
    public class FireOpts
    {
        public string type = "shot", weaponId;
        public float speed, damage, range = 20;
        public float? straight, radius, size, trailEvery, trailRadius, grav, drag, life;
        public BurstSpec burst;
        public Look? look;
    }

    public class Bomb
    {
        public string kind; public Actor owner; public int team;
        public Transform mesh, body; public Material mat;
        public Vector3 pos, vel, spin, dir; public float fuse = -1, age, beepT, rotX, rotZ; public bool sp;
    }

    public class Projectiles
    {
        const float SIM_DT = 1 / 60f;
        const float DEG = Mathf.PI / 180;
        static readonly Vector3 UP = new Vector3(0, 1, 0), DOWN = new Vector3(0, -1, 0), ZAX = new Vector3(0, 0, 1);
        const int MAX_BLOBS = 700;
        // stream-round looks: spinner stream a touch finer, twin pistols snappier (planted mode: longer, faster tails)
        static readonly Look LOOK_SPIN = new Look(0.08f, 0.9f, 1.6f, 0.025f, 30, 0.35f, 2);
        static readonly Look LOOK_TWIN = new Look(0.088f, 0.8f, 1.3f, 0.03f, 28, 0.3f, 2);
        static readonly Look LOOK_TWIN_PLANT = new Look(0.086f, 0.9f, 1.5f, 0.025f, 30, 0.35f, 2);
        // satellite droplets trailing each projectile (fractions of the head radius), thinning out down the string
        static readonly float[] SAT_SIZE = { 0.46f, 0.33f, 0.24f, 0.17f };

        public readonly List<Projectile> list = new List<Projectile>();
        readonly Stack<Projectile> pool = new Stack<Projectile>();
        public readonly List<Bomb> bombs = new List<Bomb>();
        readonly List<Cloud> clouds = new List<Cloud>();
        readonly List<Beam> beams = new List<Beam>();
        readonly Stack<Beam> beamPool = new Stack<Beam>();
        readonly Dictionary<Actor, MaterialPropertyBlock> sights = new Dictionary<Actor, MaterialPropertyBlock>();
        readonly Hit _hit = new Hit(), _hit2 = new Hit();
        readonly Mesh blobMesh, bombMesh, bombCapMesh, cloudMesh, ribbonMesh;
        readonly Material blobMat, ribbonMat, bombCapMat;
        // glossy ink teardrops: instanced in chunks (per-instance colour + shape ride in property-block arrays, which must
        // fit a 16 KB constant buffer on mobile GPUs)
        const int CHUNK = 100;
        readonly Matrix4x4[][] blobM;
        readonly Vector4[][] blobC, blobS;
        readonly MaterialPropertyBlock[] blobP;
        static readonly int ID_IColor = Shader.PropertyToID("_IColor"), ID_IShape = Shader.PropertyToID("_IShape");
        static readonly int ID_Color = Shader.PropertyToID("_Color"), ID_T = Shader.PropertyToID("_T"), ID_Life = Shader.PropertyToID("_Life"),
            ID_Len = Shader.PropertyToID("_Len"), ID_Charge = Shader.PropertyToID("_Charge"), ID_Seed = Shader.PropertyToID("_Seed"),
            ID_Mode = Shader.PropertyToID("_Mode"), ID_Width = Shader.PropertyToID("_Width");
        // bomb arc preview for the local player
        readonly LineRenderer arcLine;
        readonly Transform arcRing;
        /** the throw arc's landing ring while it shows (null when hidden): kit subs draw around it (the Waddle's preview) */
        public Transform arcRingShown => arcRing != null && arcRing.gameObject.activeSelf ? arcRing : null;
        readonly Material arcMat;
        const int ARC_N = 64;
        readonly Vector3[] arcPts = new Vector3[ARC_N];

        class Cloud
        {
            public Actor owner; public int team; public Transform group; public Transform[] puffs; public float[] bob;
            public float t, dur, rainT, groundY; public Vector3 dir; public AudioLoop loop; public Material baseM, topM;
        }
        // charger beam: tracer front races out, white-hot core snaps off, the ink sheath thins and breaks into dashes
        class Beam { public Vector3 m, dir; public float t, life, th, len, charge, seed; public Color col; public readonly MaterialPropertyBlock mpb = new MaterialPropertyBlock(); }

        public Projectiles()
        {
            blobMesh = Geo.sphere(1, 14, 12).rotateX(Mathf.PI / 2).toMesh("blob");
            blobMat = new Material(Shader.Find("Inkwave/Fx/InkBlob")) { name = "iw-blob", enableInstancing = true };
            int nc = (MAX_BLOBS + CHUNK - 1) / CHUNK;
            blobM = new Matrix4x4[nc][]; blobC = new Vector4[nc][]; blobS = new Vector4[nc][]; blobP = new MaterialPropertyBlock[nc];
            for (int c = 0; c < nc; c++) { blobM[c] = new Matrix4x4[CHUNK]; blobC[c] = new Vector4[CHUNK]; blobS[c] = new Vector4[CHUNK]; blobP[c] = new MaterialPropertyBlock(); }
            // bombs
            bombMesh = Geo.sphere(0.2f, 20, 14).toMesh("bomb");
            bombCapMesh = Geo.cylinder(0.07f, 0.09f, 0.12f, 12).toMesh("bombCap");
            bombCapMat = CharacterMats.matte(new Color(0.165f, 0.165f, 0.188f).linear, Color.black, 0, 0.4f, 0.6f);   // 0x2a2a30
            // charger beams + laser sights: camera-facing ribbons (x ∈ {-1, 1} across, z 0..1 along +Z)
            ribbonMesh = ribbonGeometry(16);
            ribbonMat = new Material(Shader.Find("Inkwave/Fx/Ribbon")) { name = "iw-ribbon" };
            // storm cloud puffs
            cloudMesh = Geo.sphere(1, 20, 14).toMesh("cloudPuff");
            refreshColors();
            var arc = ThreeSpace.create("bombArc");
            arcLine = arc.AddComponent<LineRenderer>();
            arcLine.useWorldSpace = true;
            arcLine.positionCount = ARC_N;
            arcLine.widthMultiplier = 0.06f;
            arcMat = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { color = new Color(1, 1, 1, 0.95f) };
            arcMat.SetFloat("_Cull", 0);   // the landing ring is seen from either side
            arcLine.material = arcMat;
            arcLine.shadowCastingMode = ShadowCastingMode.Off;
            arcLine.enabled = false;
            var ring = ThreeSpace.create("bombArcRing");
            ring.AddComponent<MeshFilter>().sharedMesh = ringGeometry(0.55f, 0.75f, 40);
            ring.AddComponent<MeshRenderer>().sharedMaterial = arcMat;
            arcRing = ring.transform;
            ring.SetActive(false);
        }

        static Mesh ribbonGeometry(int segs)
        {
            var pos = new List<Vector3>();
            var idx = new List<int>();
            for (int i = 0; i <= segs; i++)
            {
                float z = (float)i / segs;
                pos.Add(new Vector3(-1, 0, z)); pos.Add(new Vector3(1, 0, z));
                if (i < segs) { int a = i * 2; idx.AddRange(new[] { a, a + 2, a + 1, a + 1, a + 2, a + 3 }); }
            }
            var m = new Mesh { name = "ribbon" };
            m.SetVertices(pos); m.SetTriangles(idx, 0);
            // the vertex shader widens the strip toward the camera: never cull it on its flat bounds
            m.bounds = new Bounds(new Vector3(0, 0, 0.5f), new Vector3(40, 40, 1.5f));
            return m;
        }

        // RingGeometry(inner, outer, segs).rotateX(-π/2): a flat annulus in XZ
        static Mesh ringGeometry(float inner, float outer, int segs)
        {
            var pos = new List<Vector3>();
            var idx = new List<int>();
            for (int i = 0; i <= segs; i++)
            {
                float t = (float)i / segs * Mathf.PI * 2;
                float c = Mathf.Cos(t), s = Mathf.Sin(t);
                pos.Add(new Vector3(c * inner, 0, -s * inner)); pos.Add(new Vector3(c * outer, 0, -s * outer));
                if (i < segs) { int a = i * 2; idx.AddRange(new[] { a, a + 1, a + 2, a + 1, a + 3, a + 2 }); }
            }
            var m = new Mesh { name = "arcRing" };
            m.SetVertices(pos); m.SetTriangles(idx, 0); m.RecalculateNormals();
            return m;
        }

        public void refreshColors()
        {
            foreach (var b in bombs) setBombColor(b);
        }

        public void clear()
        {
            foreach (var p in list) pool.Push(p);
            list.Clear();
            foreach (var b in bombs) disposeBomb(b);
            bombs.Clear();
            foreach (var c in clouds) disposeCloud(c);
            clouds.Clear();
            foreach (var b in beams) beamPool.Push(b);
            beams.Clear();
            sights.Clear();
            foreach (var k in Kits.MAIN.Values) k.clear();
        }

        // a shot knocked out of the air (Mega Stamp guard): gone, no impact, no paint
        public void removeShot(Projectile p) { int i = list.IndexOf(p); if (i < 0) return; list[i] = list[list.Count - 1]; list.RemoveAt(list.Count - 1); pool.Push(p); }
        // a bomb smashed before it goes off: gone, no blast
        public void defuseBomb(Bomb b) { int i = bombs.IndexOf(b); if (i < 0) return; disposeBomb(b); bombs.RemoveAt(i); }
        // turf from a projectile / bomb: counts for the owner, but ink from a special never charges the special meter
        static void credit(bool sp, Actor owner, float area) { if (sp) owner.addTurfNoSpecial(area); else owner.addTurf(area); }

        Projectile newP()
        {
            var p = pool.Count > 0 ? pool.Pop() : new Projectile();
            // pooled: clear the per-kind optional fields so nothing leaks from the projectile's previous life
            p.volley = null; p.belowFalloff = 0; p.belowFalloffMax = 0; p.burst = null; p.weaponId = null;
            p.delay = 0; p.wid = null; p.dmgFar = 0; p.spSet = false; p.sp = false; p._bid = 0;
            // look (visual only; draw falls back to size / defaults for anything left unset)
            p.vis = 0; p.tail0 = 1; p.tailK = 1.2f; p.wob = 0.04f; p.wobF = 0; p.nose = 0; p.sats = 0;
            return p;
        }
        static void look(Projectile p, Look l) { p.vis = l.vis; p.tail0 = l.tail0; p.tailK = l.tailK; p.wob = l.wob; p.wobF = l.wobF; p.nose = l.nose; p.sats = l.sats; }
        void push(Projectile p) => list.Add(p);
        // a kit filling in a core projectile itself (blade.js P._new / P._push: the Cutlass's crescent drops)
        public Projectile _new() => newP();
        public void _push(Projectile p) => push(p);

        // feet of the victim's *visual* body (the smoothed root), so what you see is what you hit
        static Vector3 hitBase(Actor e) => new Vector3(e.pos.x, e.pos.y + e.smoothY, e.pos.z);

        // Where the muzzle is — or, while the gun is still coming up to the aim pose (the first shot of a burst fires on
        // the same frame the trigger is pulled), where it is about to be: shots never leave from the hip.
        public Vector3 muzzle(Actor a)
        {
            var ch = a.character;
            Vector3 o = ch.getMuzzle();
            float w = ch.aimReady();
            if (w < 0.98f && ch.getAimMuzzle(a.aimPitch, out var aimM)) o = Vector3.Lerp(o, aimM, 1 - w);
            Vector3 chest = a.pos + Vector3.up * (a.form == "squid" ? 0.4f : 1.05f);
            if (!float.IsFinite(o.x) || (o - chest).sqrMagnitude > 2.5f || !G.physics.los(chest, o)) o = chest + a.aimDir * 0.3f;
            return o;
        }

        // direction from muzzle toward the actor's aim point (falls back to aimDir)
        public static Vector3 aimFrom(Actor a, Vector3 from)
        {
            Vector3 o = a.aimPoint - from;
            float d = o.magnitude;
            if (d < 2.0f || Vector3.Dot(o, a.aimDir) < 0) return a.aimDir;
            return o / d;
        }

        public static Vector3 spreadDir(Vector3 dir, float deg)
        {
            if (deg <= 0) return dir;
            float r = deg * DEG * Mathf.Sqrt(random());
            float t = random() * Mathf.PI * 2;
            // random perpendicular
            Vector3 v2 = new Vector3(-dir.z, 0, dir.x); if (v2.sqrMagnitude < 1e-4f) v2 = new Vector3(1, 0, 0); v2.Normalize();
            Vector3 v3 = Vector3.Cross(dir, v2);
            return (dir + v2 * (Mathf.Cos(t) * Mathf.Tan(r)) + v3 * (Mathf.Sin(t) * Mathf.Tan(r) * 0.55f)).normalized;
        }

        // Raise/lower the launch direction so a gravity shot (straight phase, then gravity + drag — the exact integrator
        // update() uses) passes through `target`. Secant iterations on the launch pitch; no-op beyond maxDist.
        public static Vector3 ballistic(Vector3 from, Vector3 dir, Vector3 target, float speed, float straight, float grav, float drag, float maxDist)
        {
            float hx = target.x - from.x, hz = target.z - from.z, hd = Mathf.Sqrt(hx * hx + hz * hz);
            if (hd < 1.5f || hd > maxDist || grav == 0) return dir;
            float dy = target.y - from.y;
            float hdir = Mathf.Sqrt(dir.x * dir.x + dir.z * dir.z);
            if (hdir < 1e-4f) return dir;
            float sim(float pitch)
            {
                float vh = Mathf.Cos(pitch) * speed, vy = Mathf.Sin(pitch) * speed, x = 0, y = 0, age = 0;
                for (int i = 0; i < 90; i++)
                {
                    age += SIM_DT;
                    float px = x, py = y;
                    if (age > straight) { vy -= grav * SIM_DT; float k = 1 - drag * SIM_DT; vh *= k; vy *= k; }
                    x += vh * SIM_DT; y += vy * SIM_DT;
                    if (x >= hd) { float f = (hd - px) / Mathf.Max(1e-6f, x - px); return py + (y - py) * f; }
                    if (vh < 0.5f) break;
                }
                return -1e3f;
            }
            float p0 = Mathf.Atan2(dir.y, hdir), e0 = sim(p0) - dy;
            if (Mathf.Abs(e0) < 0.01f) return dir;
            float p1 = p0 - Mathf.Atan2(e0, hd), e1 = sim(p1) - dy;
            for (int it = 0; it < 4 && Mathf.Abs(e1) > 0.005f; it++)
            {
                float d = e1 - e0; if (Mathf.Abs(d) < 1e-6f) break;
                float p2 = p1 - e1 * (p1 - p0) / d;
                p0 = p1; e0 = e1; p1 = clamp(p2, -1.2f, 1.2f); e1 = sim(p1) - dy;
            }
            if (Mathf.Abs(e1) > 0.25f || Mathf.Abs(p1 - Mathf.Atan2(dir.y, hdir)) > 0.35f) return dir;   // unreachable: leave it
            float cp = Mathf.Cos(p1);
            return new Vector3((dir.x / hdir) * cp, Mathf.Sin(p1), (dir.z / hdir) * cp);
        }

        // Lob solver (bucket): the lowest launch pitch whose arc passes through `target`, found by sweeping pitch with the
        // flight integrator update() uses (gravity + drag from launch) and bisecting the first crossing. Keeps dir's heading;
        // leaves dir alone (returns false) when the target is out of reach.
        static bool lobAim(Vector3 from, ref Vector3 dir, Vector3 target, float speed, float grav, float drag, float maxDist)
        {
            float hd = Mathf.Sqrt((target.x - from.x) * (target.x - from.x) + (target.z - from.z) * (target.z - from.z));
            if (hd < 1 || hd > maxDist) return false;
            float dy = target.y - from.y;
            float f(float pitch)
            {
                float vh = Mathf.Cos(pitch) * speed, vy = Mathf.Sin(pitch) * speed, x = 0, y = 0;
                for (int i = 0; i < 180; i++)
                {
                    float px = x, py = y;
                    vy -= grav * SIM_DT; float k = 1 - drag * SIM_DT; vh *= k; vy *= k;
                    x += vh * SIM_DT; y += vy * SIM_DT;
                    if (x >= hd) return py + (y - py) * ((hd - px) / Mathf.Max(1e-6f, x - px)) - dy;
                    if (y < dy - 8) break;
                }
                return -1e3f;
            }
            float fp = f(-0.6f);
            for (float p = -0.58f; p <= 1.2f; p += 0.02f)
            {
                float fv = f(p);
                if (fp < 0 && fv >= 0)
                {
                    float lo = p - 0.02f, hi = p;
                    for (int k = 0; k < 10; k++) { float mid = (lo + hi) / 2; if (f(mid) >= 0) hi = mid; else lo = mid; }
                    float hdir = Mathf.Sqrt(dir.x * dir.x + dir.z * dir.z); if (hdir == 0) hdir = 1;
                    float cp = Mathf.Cos(hi);
                    dir = new Vector3((dir.x / hdir) * cp, Mathf.Sin(hi), (dir.z / hdir) * cp);
                    return true;
                }
                fp = fv;
            }
            return false;
        }

        public void fireShooter(Actor a, WeaponDef w, float spreadDeg)
        {
            Vector3 m = muzzle(a);
            Vector3 dir = aimFrom(a, m);
            dir = ballistic(m, dir, a.aimPoint, w.projSpeed, w.straightTime, 28, 0.8f, w.range);
            dir = spreadDir(dir, spreadDeg);
            var p = newP();
            // trail starts ~2.5 m out so shots never drip on the shooter's own feet
            p.type = "shot"; p.owner = a; p.team = a.team; p.age = 0; p.life = 1.2f; p.straight = w.straightTime; p.radius = w.impactRadius;
            p.damage = w.damage; p.size = 0.15f; p.trail = -(2.5f - w.trailEvery); p.trailEvery = w.trailEvery; p.trailRadius = w.trailRadius;
            p.grav = 28; p.drag = 0.8f; p.seed = random();
            look(p, new Look(0.1f + random() * 0.012f, 0.8f, 1.3f, 0.035f, 26, 0.3f, 3));
            p.pos = m; p.prev = m; p.start = m;
            p.vel = dir * w.projSpeed;
            push(p);
            if (a.isLocal || a.nearCamera())
            {
                G.audio?.play("shoot_shooter", new AudioOpts { pos = a.isLocal ? (Vector3?)null : m, volume = a.isLocal ? 0.55f : 0.4f });
                G.fx?.muzzle(m, dir, a.color, "shooter");
            }
            emit("weapon:fire", new Ev { actor = a, id = w.id, muzzle = m, dir = dir });
            var wr = a.weaponRunner;
            if (wr.rumbleT <= 0) { wr.rumbleT = 0.09f; Actor.rumble(a, 0.02f, 0.1f, 40); }
        }

        // Left-hand muzzle for dual wield: the rig's own left pistol, else the right muzzle mirrored across the kid's
        // midline (actor frame) — the pistols are held symmetrically.
        Vector3 muzzleHand(Actor a, int hand)
        {
            var ch = a.character;
            Vector3 chest = new Vector3(a.pos.x, a.pos.y + 1.05f, a.pos.z);
            if (hand == 1)
            {
                Vector3 o = ch.getMuzzleHand(1);
                if (float.IsFinite(o.x) && (o - chest).sqrMagnitude < 2.5f) return o;
            }
            Vector3 m = muzzle(a);
            if (hand == 0) return m;
            float cy = Mathf.Cos(a.yaw), sy = Mathf.Sin(a.yaw), dx = m.x - a.pos.x, dz = m.z - a.pos.z;
            float lx = -(dx * cy - dz * sy), lz = dx * sy + dz * cy;
            m.x = a.pos.x + lx * cy + lz * sy; m.z = a.pos.z - lx * sy + lz * cy;
            if (!G.physics.los(chest, m)) m = chest + a.aimDir * 0.3f;
            return m;
        }

        public void fireBlaster(Actor a, WeaponDef w, float spreadDeg)
        {
            Vector3 m = muzzle(a);
            Vector3 dir = aimFrom(a, m);
            dir = spreadDir(dir, spreadDeg);
            var p = newP();
            p.type = "blast"; p.owner = a; p.team = a.team; p.age = 0; p.life = w.range / w.projSpeed; p.straight = 99; p.radius = w.impactRadius;
            p.damage = w.directDamage; p.size = 0.26f; p.trail = -1.5f; p.trailEvery = 2.2f; p.trailRadius = 0.45f;
            p.grav = 0; p.drag = 0; p.seed = random();
            look(p, new Look(0.2f, 0.5f, 0.9f, 0.085f, 17, 0.15f, 4));
            p.pos = m; p.prev = m; p.start = m;
            p.vel = dir * w.projSpeed;
            push(p);
            if (a.isLocal || a.nearCamera())
            {
                G.audio?.play("shoot_blaster", new AudioOpts { pos = a.isLocal ? (Vector3?)null : m, volume = a.isLocal ? 0.7f : 0.5f });
                // the pump rack: clacks land on the pump animation's back/front stops (character.js, +0.29 s / +0.46 s)
                G.audio?.play("blaster_pump", new AudioOpts { pos = a.isLocal ? (Vector3?)null : m, volume = a.isLocal ? 0.55f : 0.4f, delay = 0.27f });
            }
            if (a.isLocal) emit("recoil", new Ev { amount = 0.012f });   // one clean pitch kick; no trauma shake for your own gun
            emit("weapon:fire", new Ev { actor = a, id = w.id, muzzle = m, dir = dir });
            Actor.rumble(a, 0.28f, 0.4f, 95);
        }

        // Specials' guns: a shooter-style shot or a blaster-style blast from an explicit muzzle + direction.
        public Projectile fireCustom(Actor a, Vector3 from, Vector3 dir, FireOpts o)
        {
            var p = newP();
            bool blast = o.type == "blast";
            float size = o.size ?? (blast ? 0.26f : 0.14f);
            p.type = blast ? "blast" : "shot"; p.owner = a; p.team = a.team; p.age = 0;
            p.life = o.life ?? (o.range / o.speed + (blast ? 0 : 0.6f)); p.straight = o.straight ?? (blast ? 99 : 0.15f);
            p.radius = o.radius ?? 0.8f; p.damage = o.damage; p.size = size; p.trail = -1.5f; p.trailEvery = o.trailEvery ?? 1.2f; p.trailRadius = o.trailRadius ?? 0.42f;
            p.grav = o.grav ?? (blast ? 0 : 28); p.drag = o.drag ?? (blast ? 0 : 0.8f); p.seed = random(); p.burst = o.burst; p.weaponId = o.weaponId;
            look(p, o.look ?? (blast ? new Look(size * 0.77f, 0.5f, 0.9f, 0.085f, 17, 0.15f, 3) : new Look(size * 0.68f, 0.8f, 1.3f, 0.035f, 26, 0.3f, 2)));
            p.pos = from; p.prev = from; p.start = from;
            p.vel = dir.normalized * o.speed;
            push(p);
            return p;
        }

        public void fireFlick(Actor a, WeaponDef w)
        {
            Vector3 m = a.pos; m.y += 1.0f;
            float fx = Mathf.Sin(a.yaw), fz = Mathf.Cos(a.yaw);
            float up = clamp(a.aimPitch, -0.2f, 0.5f) + 0.32f;
            for (int i = 0; i < w.flickDrops; i++)
            {
                float t = (i / (w.flickDrops - 1)) * 2 - 1;
                float ang = a.yaw + t * w.flickSpreadDeg * DEG * 0.5f + (random() - 0.5f) * 0.05f;
                float sp = w.flickSpeed * (0.82f + 0.28f * (1 - Mathf.Abs(t)) + random() * 0.08f);
                var p = newP();
                // big globs in the middle of the sheet, smaller beads toward the edges (visual only: the hit size is unchanged)
                float mid = 1 - Mathf.Abs(t);
                p.type = "drop"; p.owner = a; p.team = a.team; p.age = 0; p.life = 1.4f; p.straight = 0; p.radius = 0.85f + random() * 0.3f;
                p.damage = w.flickDamageNear; p.dmgFar = w.flickDamageFar; p.size = 0.15f; p.trail = 0; p.trailEvery = 1.8f; p.trailRadius = 0.45f;
                p.grav = 26; p.drag = 0.4f; p.seed = random();
                look(p, new Look(0.1f + 0.085f * mid + random() * 0.03f, 0.4f, 1.0f, 0.1f, 19, 0, mid > 0.45f ? 2 : 1));
                p.pos = new Vector3(m.x + fx * 0.6f, m.y + 0.3f, m.z + fz * 0.6f); p.prev = p.pos; p.start = p.pos;
                float cu = Mathf.Cos(up + (random() - 0.5f) * 0.12f);
                p.vel = new Vector3(Mathf.Sin(ang) * cu * sp, Mathf.Sin(up) * sp, Mathf.Cos(ang) * cu * sp);
                push(p);
            }
            if (a.isLocal) emit("recoil", new Ev { amount = 0.007f });
            emit("weapon:fire", new Ev { actor = a, id = w.id, muzzle = new Vector3(m.x + fx * 0.6f, m.y + 0.3f, m.z + fz * 0.6f), dir = new Vector3(fx, Mathf.Sin(up), fz).normalized });
            Actor.rumble(a, 0.3f, 0.32f, 110);
        }

        public void fireCharger(Actor a, WeaponDef w, float charge)
        {
            Vector3 m = muzzle(a);
            Vector3 dir = aimFrom(a, m);
            float range = lerp(w.rangeMin, w.rangeMax, charge);
            float dmg = charge >= 0.999f ? w.damageMax : lerp(w.damageMin, w.damageMax * 0.62f, charge);
            var hit = G.physics.raycast(m, dir, range, _hit, true);
            bool wallHit = hit.hit;
            float len = wallHit ? hit.dist : range;
            // enemy canopies and ink curtains stop the beam (specials.js rayHit — bubbles, tanks — with that module)
            { float cut = G.subs.blockRay(m, dir, len, a.team, dmg); if (cut < len) { len = cut; wallHit = false; } }
            // first enemy along the beam
            Actor victim = null; float victimD = 0;
            foreach (var e in G.actors)
            {
                if (e.team == a.team || !e.alive) continue;
                var (dist, t) = Physics.segmentCapsuleDist(m, m + dir * len, hitBase(e), PLAYER.radius + 0.12f, e.form == "squid" ? PLAYER.squidHeight : PLAYER.height);
                if (dist < PLAYER.radius + 0.14f)
                {
                    float d = t * len;
                    if (victim == null || d < victimD) { victim = e; victimD = d; }
                }
            }
            if (victim != null) { len = victimD; applyHit(a, victim, dmg, "charger"); }
            // paint along the line (projected to the ground)
            float area = 0;
            float step = w.lineSplatEvery;
            for (float s = 1.2f; s < len - 0.3f; s += step)
            {
                var g = G.physics.raycast(m + dir * s, DOWN, 3.5f, _hit2, true);
                if (g.hit) area += G.paint.splat(g.point + g.normal * 0.1f, w.lineRadius * (0.8f + charge * 0.4f), a.team, new SplatOpts { seed = random(), stretch = dir, stretchAmt = 1.2f });
            }
            if (wallHit && victim == null)
            {
                area += G.paint.splat(hit.point + hit.normal * 0.12f, w.impactRadius * (0.6f + 0.4f * charge), a.team, new SplatOpts { seed = random(), stretch = dir, stretchAmt = 0.6f });
                G.fx?.burst(hit.point, hit.normal, a.color, new FxOpts { count = 10, speed = 4, size = 0.09f, paint = false });
                if (a.isLocal || a.nearCamera()) G.audio?.play("ink_hit_wall", new AudioOpts { pos = hit.point, volume = 0.6f });
            }
            Vector3 end = m + dir * len;
            emit("weapon:fire", new Ev { actor = a, id = w.id, muzzle = m, dir = dir, charge = charge, len = len });
            emit("weapon:impact", new Ev { pos = end, normal = wallHit && victim == null ? hit.normal : -dir, team = a.team, kind = "charger", radius = w.impactRadius * (0.6f + 0.4f * charge) });
            a.addTurf(area);
            addBeam(m, dir, len, charge, a.color);
            if (a.isLocal || a.nearCamera())
                G.audio?.play("shoot_charger", new AudioOpts { pos = a.isLocal ? (Vector3?)null : m, volume = a.isLocal ? 0.8f : 0.6f, pitch = 1.08f - 0.16f * charge });
            if (a.isLocal) emit("recoil", new Ev { amount = 0.005f + charge * 0.013f });
            Actor.rumble(a, 0.12f + charge * 0.45f, 0.2f + charge * 0.35f, 80 + charge * 90);
        }

        void addBeam(Vector3 m, Vector3 dir, float len, float charge, Color col)
        {
            var b = beamPool.Count > 0 ? beamPool.Pop() : new Beam();
            b.m = m; b.dir = dir; b.len = len; b.charge = charge; b.col = col;
            b.th = 0.035f + charge * 0.05f;
            b.t = 0; b.life = 0.3f + 0.1f * charge; b.seed = random() * 100;
            beams.Add(b);
        }

        // bucket: a volley of blobs thrown along the aim, lobbed upward so it clears cover; only the first blob of a volley
        // to reach an enemy hurts them (the rest still paint)
        public void fireSlosh(Actor a, WeaponDef w)
        {
            Vector3 m = muzzle(a);
            Vector3 dir = aimFrom(a, m);
            // default: the aim lifted by the lob (sails over cover when you aim above it). With the crosshair on something in
            // reach, bend the arc so the lead blob lands on it (the solver keeps the default arc if that's out of reach).
            float hd0 = Mathf.Sqrt(dir.x * dir.x + dir.z * dir.z); if (hd0 == 0) hd0 = 1;
            float p0 = Mathf.Atan2(dir.y, hd0) + w.lob;
            dir = new Vector3((dir.x / hd0) * Mathf.Cos(p0), Mathf.Sin(p0), (dir.z / hd0) * Mathf.Cos(p0));
            lobAim(m, ref dir, a.aimPoint, w.throwSpeed, w.gravity, w.drag, w.reach);
            float hd = Mathf.Sqrt(dir.x * dir.x + dir.z * dir.z); if (hd == 0) hd = 1;
            float pitch = Mathf.Atan2(dir.y, hd);
            var volley = new HashSet<Actor>();
            for (int i = 0; i < w.blobs; i++)
            {
                float sp = w.throwSpeed * (1 - i * 0.075f), side = (random() - 0.5f) * 0.05f, pi = pitch + (random() - 0.5f) * 0.04f;
                float yaw = Mathf.Atan2(dir.x, dir.z) + side, cp = Mathf.Cos(pi);
                var p = newP();
                float size = 0.22f - i * 0.02f;
                p.type = "slosh"; p.owner = a; p.team = a.team; p.age = 0; p.life = 2.2f; p.straight = 0; p.radius = w.impactRadius * (1 - i * 0.08f);
                p.damage = w.damage; p.belowFalloff = w.belowFalloff; p.belowFalloffMax = w.belowFalloffMax; p.size = size; p.trail = 0; p.trailEvery = 1.6f; p.trailRadius = 0.42f;
                p.grav = w.gravity; p.drag = w.drag; p.seed = random(); p.volley = volley;
                look(p, new Look(size * 0.86f, 0.7f, 1.5f, 0.12f, 15, 0.1f, i < 2 ? 2 : 1));
                p.pos = m - dir * (i * 0.12f); p.prev = p.pos; p.start = p.pos;
                p.vel = new Vector3(Mathf.Sin(yaw) * cp * sp, Mathf.Sin(pi) * sp, Mathf.Cos(yaw) * cp * sp);
                push(p);
            }
            if (a.isLocal || a.nearCamera()) G.audio?.play("bomb_throw", new AudioOpts { pos = a.isLocal ? (Vector3?)null : m, volume = a.isLocal ? 0.7f : 0.5f, pitch = 0.8f });
            if (a.isLocal) emit("recoil", new Ev { amount = 0.008f });
            emit("weapon:fire", new Ev { actor = a, id = w.id, muzzle = m, dir = dir });
            Actor.rumble(a, 0.2f, 0.25f, 90);
        }

        // spinner stream: small fast shots; reach and speed follow the burst's charge
        public void fireStream(Actor a, WeaponDef w, float power, float spreadDeg)
        {
            Vector3 m = muzzle(a);
            Vector3 dir = aimFrom(a, m);
            float speed = lerp(w.speedMin, w.speedMax, power), straight = lerp(w.straightMin, w.straightMax, power), range = lerp(w.rangeMin, w.rangeMax, power);
            dir = ballistic(m, dir, a.aimPoint, speed, straight, 28, 0.8f, range);
            dir = spreadDir(dir, spreadDeg);
            var p = newP();
            p.type = "shot"; p.owner = a; p.team = a.team; p.age = 0; p.life = 1.3f; p.straight = straight; p.radius = w.impactRadius;
            p.damage = w.damage; p.size = 0.12f; p.trail = -(2.2f - w.trailEvery); p.trailEvery = w.trailEvery; p.trailRadius = w.trailRadius;
            p.grav = 28; p.drag = 0.8f; p.seed = random();
            look(p, LOOK_SPIN);
            p.pos = m; p.prev = m; p.start = m;
            p.vel = dir * speed;
            push(p);
            var wr = a.weaponRunner;
            if ((a.isLocal || a.nearCamera()) && ++wr.streamN % 2 == 0)
            {
                G.audio?.play("shoot_shooter", new AudioOpts { pos = a.isLocal ? (Vector3?)null : m, volume = a.isLocal ? 0.4f : 0.28f, pitch = 1.35f });
                G.fx?.muzzle(m, dir, a.color, "shooter");
            }
            emit("weapon:fire", new Ev { actor = a, id = w.id, muzzle = m, dir = dir });
            if (wr.rumbleT <= 0) { wr.rumbleT = 0.07f; Actor.rumble(a, 0.03f, 0.12f, 40); }
        }

        // twin pistols: side = -1 / 1 alternates left and right guns (0 = both guns together after a roll)
        public void fireTwin(Actor a, WeaponDef w, float spreadDeg, float side)
        {
            Vector3 m;
            // a dual-wield rig (a real pistol in each fist) fires each side from its own gun; otherwise offset the one muzzle
            if (side != 0 && a.character.dual) m = muzzleHand(a, side > 0 ? 1 : 0);
            else
            {
                m = muzzle(a);
                if (side != 0)
                {
                    // half way between the original twin offset (the gun's muzzle ± 17 cm) and the dualies' spacing (each shot
                    // from its own pistol: the left one is the right muzzle mirrored across the kid's midline)
                    Vector3 r = new Vector3(a.aimDir.z, 0, -a.aimDir.x).normalized;
                    float cy = Mathf.Cos(a.yaw), sy = Mathf.Sin(a.yaw), dx = m.x - a.pos.x, dz = m.z - a.pos.z;
                    float lx = dx * cy - dz * sy, lz = dx * sy + dz * cy;
                    var mir = new Vector3(a.pos.x - lx * cy + lz * sy, m.y, a.pos.z + lx * sy + lz * cy);   // mirrored (left pistol) muzzle
                    if (side > 0) m = (m + r * 0.17f + mir) * 0.5f;
                    else m += r * -0.085f;
                    Vector3 chest = new Vector3(a.pos.x, a.pos.y + 1.05f, a.pos.z);
                    if (!G.physics.los(chest, m)) m = chest + a.aimDir * 0.3f;
                }
            }
            Vector3 dir = aimFrom(a, m);
            // side 0 = planted rapid mode: faster, straighter shots with more reach
            float speed = side != 0 ? w.projSpeed : w.turretProjSpeed, straight = side != 0 ? w.straightTime : w.turretStraight;
            dir = ballistic(m, dir, a.aimPoint, speed, straight, 28, 0.8f, side != 0 ? w.range : w.turretRange);
            dir = spreadDir(dir, spreadDeg);
            var p = newP();
            p.type = "shot"; p.owner = a; p.team = a.team; p.age = 0; p.life = 1.2f; p.straight = straight; p.radius = w.impactRadius;
            p.damage = w.damage; p.size = 0.13f; p.trail = -(2.4f - w.trailEvery); p.trailEvery = w.trailEvery; p.trailRadius = w.trailRadius;
            p.grav = 28; p.drag = 0.8f; p.seed = random();
            look(p, side != 0 ? LOOK_TWIN : LOOK_TWIN_PLANT);
            p.pos = m; p.prev = m; p.start = m;
            p.vel = dir * speed;
            push(p);
            if (a.isLocal || a.nearCamera())
            {
                G.audio?.play("shoot_shooter", new AudioOpts { pos = a.isLocal ? (Vector3?)null : m, volume = a.isLocal ? 0.45f : 0.32f, pitch = 1.12f + random() * 0.06f });
                G.fx?.muzzle(m, dir, a.color, "shooter");
            }
            emit("weapon:fire", new Ev { actor = a, id = w.id, muzzle = m, dir = dir, hand = side > 0 ? 1 : 0 });
            var wr = a.weaponRunner;
            if (wr.rumbleT <= 0) { wr.rumbleT = 0.08f; Actor.rumble(a, 0.02f, 0.09f, 35); }
        }

        // brush swipe: globs flung off the brush head as it sweeps sideways low across the front — each leaves from its point
        // on the arc, flying outward and along the sweep, low and flat (side +1 sweeps right → left)
        public void fireSwipe(Actor a, WeaponDef w, float side)
        {
            float fx = Mathf.Sin(a.yaw), fz = Mathf.Cos(a.yaw);
            float up = clamp(a.aimPitch, -0.3f, 0.3f) * 0.5f + 0.08f;
            float arc = w.swipeSpreadDeg * DEG * 1.6f;
            for (int i = 0; i < w.swipeDrops; i++)
            {
                float t = i / Mathf.Max(1, w.swipeDrops - 1);                    // 0 → 1 along the sweep
                float ang = a.yaw - side * (t - 0.5f) * arc;
                float ox = Mathf.Sin(ang), oz = Mathf.Cos(ang);                    // outward from the body
                float tx = -oz * side, tz = ox * side;                             // along the sweep
                float sp = w.swipeSpeed * (0.8f + random() * 0.3f);
                var p = newP();
                p.type = "drop"; p.owner = a; p.team = a.team; p.age = 0; p.life = 1.0f; p.straight = 0; p.radius = 0.55f + random() * 0.2f;
                p.damage = w.swipeDamageNear; p.dmgFar = w.swipeDamageFar; p.size = 0.1f; p.trail = 0; p.trailEvery = 2.2f; p.trailRadius = 0.32f;
                p.grav = 26; p.drag = 0.6f; p.seed = random();
                look(p, new Look(0.07f + random() * 0.025f, 0.4f, 1.0f, 0.1f, 19, 0, 1));
                p.pos = new Vector3(a.pos.x + ox * 0.85f, a.pos.y + 0.45f, a.pos.z + oz * 0.85f); p.prev = p.pos; p.start = p.pos;
                float hx = ox * 0.8f + tx * 0.45f + fx * 0.25f, hz = oz * 0.8f + tz * 0.45f + fz * 0.25f, hl = Mathf.Sqrt(hx * hx + hz * hz);
                float cu = Mathf.Cos(up + (random() - 0.5f) * 0.08f);
                p.vel = new Vector3((hx / hl) * cu * sp, Mathf.Sin(up) * sp, (hz / hl) * cu * sp);
                push(p);
            }
            emit("weapon:fire", new Ev { actor = a, id = w.id, muzzle = new Vector3(a.pos.x + fx * 0.85f, a.pos.y + 0.45f, a.pos.z + fz * 0.85f), dir = new Vector3(fx, Mathf.Sin(up), fz).normalized });
            Actor.rumble(a, 0.08f, 0.14f, 50);
        }

        // a loose ink drop (sprinkler spray): owner's team, damage with no falloff
        public void spawnDrop(Actor owner, Vector3 pos, Vector3 v, float size = 0.08f, float radius = 0.5f, float damage = 8, string weaponId = null)
        {
            var p = newP();
            p.type = "drop"; p.owner = owner; p.team = owner.team; p.age = 0; p.life = 1.2f; p.straight = 0; p.radius = radius;
            p.damage = damage; p.dmgFar = damage; p.size = size; p.trail = 0; p.trailEvery = 0; p.trailRadius = 0.3f;
            p.grav = 22; p.drag = 0.4f; p.seed = random();
            look(p, new Look(size * (0.9f + random() * 0.2f), 0.4f, 1.0f, 0.09f, 20, 0, 1));
            p.weaponId = weaponId;   // what the hit is credited to (the splat screen): e.g. 'sprinkler'
            p.pos = pos; p.prev = pos; p.start = pos;
            p.vel = v;
            push(p);
        }

        // ---- bombs
        void setBombColor(Bomb b)
        {
            Color c = G.teamColors[b.team];
            b.mat.SetColor("_BaseColor", c);
            CharacterMats.setLamp(b.mat, c.linear, 0);
        }

        public Vector3 throwVelocity(Actor a, float speed)
        {
            float pitch = clamp(a.aimPitch + 0.28f, -0.3f, 1.1f);
            float cp = Mathf.Cos(pitch);
            return new Vector3(Mathf.Sin(a.aimYaw) * cp * speed + a.vel.x * 0.4f, Mathf.Sin(pitch) * speed + 1.5f, Mathf.Cos(a.aimYaw) * cp * speed + a.vel.z * 0.4f);
        }

        Bomb makeBomb(string kind, Actor a, float y, float scale, bool cap)
        {
            var group = ThreeSpace.create(kind);
            var body = new GameObject("body");
            body.transform.SetParent(group.transform, false);
            body.transform.localScale = Vector3.one * scale;
            body.AddComponent<MeshFilter>().sharedMesh = bombMesh;
            var mat = CharacterMats.lampMaterial(G.teamColors[a.team], 0.25f);
            body.AddComponent<MeshRenderer>().sharedMaterial = mat;
            if (cap)
            {
                var c = new GameObject("cap");
                c.transform.SetParent(group.transform, false);
                c.transform.localPosition = new Vector3(0, 0.2f, 0);
                c.AddComponent<MeshFilter>().sharedMesh = bombCapMesh;
                c.AddComponent<MeshRenderer>().sharedMaterial = bombCapMat;
            }
            Vector3 p = a.pos; p.y += y;
            group.transform.localPosition = p;
            var b = new Bomb { kind = kind, owner = a, team = a.team, mesh = group.transform, body = body.transform, mat = mat, pos = p, fuse = -1, age = 0, beepT = 0 };
            setBombColor(b);
            return b;
        }
        void disposeBomb(Bomb b) { Ctx.destroy(b.mesh.gameObject); Ctx.destroy(b.mat); }

        public void throwBomb(Actor a)
        {
            var s = Config.SUBS["bomb"];
            var b = makeBomb("bomb", a, 1.35f, 1, true);
            b.vel = throwVelocity(a, s.throwSpeed);
            b.spin = new Vector3(random() * 8, random() * 8, 0);
            b.sp = a.specialActive != null;
            bombs.Add(b);
            if (a.isLocal || a.nearCamera()) G.audio?.play("bomb_throw", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = 0.7f });
            emit("bomb:throw", new Ev { actor = a, pos = b.pos, team = a.team, radius = s.radius });
        }

        public void throwStorm(Actor a)
        {
            var sp = Config.SPECIALS["storm"];
            var b = makeBomb("storm", a, 1.45f, 1.25f, false);
            b.vel = throwVelocity(a, sp.throwSpeed);
            b.spin = new Vector3(4, 6, 0);
            b.dir = new Vector3(b.vel.x, 0, b.vel.z).normalized;
            bombs.Add(b);
        }

        void explodeBomb(Bomb b)
        {
            var s = Config.SUBS["bomb"];
            var c = b.pos;
            float area = G.paint.splat(new Vector3(c.x, c.y + 0.2f, c.z), s.paintRadius, b.team, new SplatOpts { seed = random() });
            for (int i = 0; i < 5; i++)
            {
                float an = random() * Mathf.PI * 2, r = s.paintRadius * (0.6f + random() * 0.4f);
                area += G.paint.splat(new Vector3(c.x + Mathf.Cos(an) * r, c.y + 0.5f, c.z + Mathf.Sin(an) * r), 0.7f + random() * 0.5f, b.team, new SplatOpts { seed = random() });
            }
            credit(b.sp, b.owner, area);
            G.subs.damageArea(c, s.radius, 60, b.team);
            G.fx?.explosion(c, G.teamColors[b.team], s.radius);
            G.audio?.play("bomb_explode", new AudioOpts { pos = c });
            emit("shake", new Ev { pos = c, amount = 0.6f });
            emit("bomb:explode", new Ev { actor = b.owner, pos = c, team = b.team, radius = s.radius });
            var loc = G.local;
            if (loc != null && loc.alive) { float d = Vector3.Distance(loc.pos, c); if (d < 14) Actor.rumble(loc, clamp(1 - d / 14, 0, 1) * 0.6f, clamp(1 - d / 14, 0, 1) * 0.5f, 160); }
            foreach (var e in G.actors)
            {
                if (e.team == b.team || !e.alive) continue;
                Vector3 ep = e.pos + Vector3.up * 0.7f;
                float d = Vector3.Distance(ep, c);
                if (d > s.radius) continue;
                if (!G.physics.los(new Vector3(c.x, c.y + 0.3f, c.z), ep)) continue;
                float k = 1 - clamp((d - 0.8f) / (s.radius - 0.8f), 0, 1);
                applyHit(b.owner, e, lerp(s.damageMin, s.damageMax, k * k), "bomb");
            }
        }

        void spawnCloud(Bomb b)
        {
            var sp = Config.SPECIALS["storm"];
            var g = G.physics.raycast(new Vector3(b.pos.x, b.pos.y + 0.5f, b.pos.z), DOWN, 12, _hit);
            float groundY = g.hit ? g.point.y : b.pos.y;
            var group = ThreeSpace.create("stormCloud");
            Color col = G.teamColors[b.team].linear;
            // a soft cumulus: a flattened ring of big puffs with smaller, paler puffs piled on top; tinted underside
            var baseM = CharacterMats.matte(Color.Lerp(col, Color.white, 0.12f), col, 0.16f, 0.95f, 0);
            var topM = CharacterMats.matte(Color.Lerp(col, Color.white, 0.55f), col, 0.08f, 0.95f, 0);
            var puffs = new List<Transform>(); var bob = new List<float>();
            void puff(float x, float y, float z, float r, Material m)
            {
                var p = new GameObject("puff");
                p.transform.SetParent(group.transform, false);
                p.transform.localPosition = new Vector3(x, y, z); p.transform.localScale = new Vector3(r, r * 0.68f, r);
                p.AddComponent<MeshFilter>().sharedMesh = cloudMesh;
                p.AddComponent<MeshRenderer>().sharedMaterial = m;
                puffs.Add(p.transform); bob.Add(random() * 6.28f);
            }
            puff(0, 0, 0, 1.9f, baseM);
            for (int i = 0; i < 8; i++)
            {
                float an = (i / 8f) * Mathf.PI * 2 + random() * 0.3f, r = 1.7f + random() * 0.6f;
                puff(Mathf.Cos(an) * r, -0.1f + random() * 0.2f, Mathf.Sin(an) * r, 1.05f + random() * 0.45f, baseM);
            }
            for (int i = 0; i < 5; i++)
            {
                float an = (i / 5f) * Mathf.PI * 2 + 0.4f, r = 0.6f + random() * 0.8f;
                puff(Mathf.Cos(an) * r, 0.75f + random() * 0.3f, Mathf.Sin(an) * r, 0.9f + random() * 0.4f, topM);
            }
            puff(0, 1.15f, 0, 1.0f, topM);
            Vector3 gp = new Vector3(b.pos.x, groundY + 4.6f, b.pos.z);
            group.transform.localPosition = gp;
            group.transform.localScale = Vector3.one * 0.01f;
            var loop = G.audio?.loop("storm_rain", new AudioOpts { pos = gp, volume = 0.6f });
            clouds.Add(new Cloud { owner = b.owner, team = b.team, group = group.transform, puffs = puffs.ToArray(), bob = bob.ToArray(), t = 0, dur = sp.duration, dir = b.dir, rainT = 0, loop = loop, groundY = groundY, baseM = baseM, topM = topM });
            G.audio?.play("storm_thunder", new AudioOpts { pos = gp });
            emit("storm:start", new Ev { pos = gp, team = b.team, actor = b.owner, radius = sp.radius });
        }
        void disposeCloud(Cloud c) { c.loop?.stop(0.3f); Ctx.destroy(c.group.gameObject); Ctx.destroy(c.baseM); Ctx.destroy(c.topM); }

        // ---- damage routing
        public void applyHit(Actor attacker, Actor victim, float dmg, string weaponId)
        {
            if (!victim.alive || victim.team == attacker.team) return;
            bool killed = victim.damage(dmg, attacker, weaponId);
            emit("hit", new Ev { attacker = attacker, victim = victim, damage = dmg, killed = killed, weaponId = weaponId });
            // ink smacking the body, at the body (heavier + lower for big hits); the UI tick / kill sting are main.js's
            if (G.audio != null && (attacker.isLocal || victim.isLocal || victim.nearCamera()))
            {
                Vector3 vh = victim.pos + Vector3.up * (victim.form == "squid" ? 0.3f : 0.9f);
                G.audio.play("ink_hit_body", new AudioOpts { pos = vh, volume = (victim.isLocal ? 0.3f : 0.4f) + Mathf.Min(0.45f, dmg / 260), pitch = dmg >= 60 ? 0.8f : 1.05f });
            }
            if (attacker.isLocal) Actor.rumble(attacker, killed ? 0.35f : 0.06f, killed ? 0.4f : 0.16f, killed ? 150 : 45);
        }

        // ---- per-frame
        public void update(float dt)
        {
            foreach (var k in Kits.MAIN.Values) k.tick(dt);   // kit weapons' own world objects (kits/*.js)
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var p = list[i];
                // fired while its owner's special was running: its ink never charges the special meter (see credit)
                if (!p.spSet) { p.sp = p.owner != null && p.owner.specialActive != null; p.spSet = true; }
                if (p.delay > 0) { p.delay -= dt; if (p.delay > 0) continue; }   // poured waves: later globs leave a beat later
                if (step(p, dt)) { list[i] = list[list.Count - 1]; list.RemoveAt(list.Count - 1); pool.Push(p); }
            }
            updateBombs(dt);
            updateClouds(dt);
            updateBeams(dt);
            draw();
        }

        // one round, one frame; true = it's done
        bool step(Projectile p, float dt)
        {
            p.age += dt;
            p.prev = p.pos;
            if (p.age > p.straight) p.vel.y -= p.grav * dt;
            if (p.drag != 0) p.vel *= 1 - p.drag * dt * (p.age > p.straight ? 1 : 0);
            p.pos += p.vel * dt;
            bool dead = false;
            // actors
            foreach (var e in G.actors)
            {
                if (e.team == p.team || !e.alive) continue;
                float h = e.form == "squid" ? PLAYER.squidHeight : PLAYER.height, hr = PLAYER.radius;
                if (Mathf.Abs(e.pos.x - p.pos.x) > 3 + hr || Mathf.Abs(e.pos.z - p.pos.z) > 3 + hr) continue;
                var (dist, t) = Physics.segmentCapsuleDist(p.prev, p.pos, hitBase(e), hr, h);
                // generous hitbox: the whole visible body plus the blob's own radius
                if (dist < hr * 0.95f + p.size)
                {
                    if (p.volley != null && p.volley.Contains(e)) continue;   // bucket: this throw already landed on them: pass through
                    p.volley?.Add(e);
                    Vector3 at = Vector3.Lerp(p.prev, p.pos, t);
                    float dmg = p.damage;
                    if (p.type == "drop") dmg = lerp(p.damage, p.dmgFar, clamp(Vector3.Distance(p.start, at) / 7, 0, 1));
                    // bucket: full damage at any range, less only when the target stands below the thrower
                    if (p.belowFalloff != 0) dmg *= 1 - clamp((p.start.y - (e.pos.y + 0.8f) - 0.5f) * p.belowFalloff, 0, p.belowFalloffMax);
                    if (dmg > 0) applyHit(p.owner, e, dmg, p.weaponId ?? p.wid ?? p.type);
                    Vector3 back = -p.vel.normalized;
                    G.fx?.burst(at, back, p.owner.color, new FxOpts { count = 6, speed = 3, size = 0.07f });
                    if (p.type != "blast") emit("weapon:impact", new Ev { pos = at, normal = back, team = p.team, kind = p.type == "drop" ? "drop" : "shot", radius = p.radius * 0.5f, victim = e });
                    if (p.type == "blast") blastBurst(p, at, e);
                    dead = true; break;
                }
            }
            // enemy ink curtains, devices (sprinklers, beacons) and canopies catch shots (specials.js shotHit — bubbles, tanks —
            // with that module)
            if (!dead && G.subs.blockShot(p.prev, p.pos, p.team, p.damage > 0 ? p.damage : 10)) dead = true;
            // world
            if (!dead)
            {
                var hit = G.physics.segment(p.prev, p.pos, _hit, true);
                if (hit.hit) { impact(p, hit); dead = true; }
            }
            // trail drips
            if (!dead && p.trailEvery > 0)
            {
                p.trail += p.vel.magnitude * dt;
                if (p.trail > p.trailEvery)
                {
                    p.trail = 0;
                    var g = G.physics.raycast(p.pos, DOWN, 4, _hit2, true);
                    if (g.hit) credit(p.sp, p.owner, G.paint.splat(g.point + g.normal * 0.1f, p.trailRadius * (0.8f + random() * 0.4f), p.team, new SplatOpts { seed = random() }));
                }
            }
            if (!dead && p.age > p.life)
            {
                if (p.type == "blast") blastBurst(p, p.pos, null);
                dead = true;
            }
            if (!dead && p.pos.y < PLAYER.waterY - 1.8f) dead = true;
            return dead;
        }

        void impact(Projectile p, Hit hit)
        {
            Vector3 at = hit.point + hit.normal * 0.14f;
            Vector3 dir = p.vel.normalized;
            float rad = p.radius * (0.85f + random() * 0.3f);
            float area = G.paint.splat(at, rad, p.team, new SplatOpts { seed = p.seed, stretch = dir, stretchAmt = 0.7f });
            credit(p.sp, p.owner, area);
            if (p.type != "blast") emit("weapon:impact", new Ev { pos = hit.point, normal = hit.normal, team = p.team, kind = p.type == "drop" ? "drop" : "shot", radius = rad });
            bool near = p.owner.isLocal || (G.camPos - hit.point).sqrMagnitude < 22 * 22;
            if (near)
            {
                G.fx?.burst(hit.point, hit.normal, p.owner.color, new FxOpts { count = p.type == "blast" ? 14 : 5, speed = p.type == "blast" ? 5 : 3, size = 0.07f, paint = false });
                if (random() < (p.type == "shot" ? 0.45f : 1)) G.audio?.play(p.type == "blast" ? "splat_big" : "splat_small", new AudioOpts { pos = hit.point, volume = p.type == "shot" ? 0.35f : 0.6f });
            }
            if (p.type == "blast") blastBurst(p, hit.point, null);
        }

        void blastBurst(Projectile p, Vector3 at, Actor direct)
        {
            var bw = Config.WEAPONS["blaster"];
            var b = p.burst;
            float burstRadius = b != null ? b.radius : bw.burstRadius, impactRadius = b != null ? (b.paint >= 0 ? b.paint : b.radius * 0.6f) : bw.impactRadius;
            float splashRadius = b != null ? b.splashRadius : bw.splashRadius, dmgMax = b != null ? b.dmgMax : bw.splashDamageMax, dmgMin = b != null ? b.dmgMin : bw.splashDamageMin;
            Vector3 c = at;
            G.fx?.explosion(c, p.owner.color, burstRadius);
            G.audio?.play("blaster_boom", new AudioOpts { pos = c, volume = 0.7f });
            emit("weapon:impact", new Ev { pos = c, normal = Vector3.up, team = p.team, kind = "blast", radius = burstRadius });
            // paint under the burst
            var g = G.physics.raycast(new Vector3(c.x, c.y + 0.2f, c.z), DOWN, 3.5f, _hit2);
            if (g.hit) credit(p.sp, p.owner, G.paint.splat(g.point + g.normal * 0.1f, impactRadius, p.team, new SplatOpts { seed = random() }));
            foreach (var e in G.actors)
            {
                if (e.team == p.team || !e.alive || e == direct) continue;
                Vector3 ep = e.pos + Vector3.up * 0.7f;
                float d = Vector3.Distance(ep, c);
                if (d > splashRadius) continue;
                if (!G.physics.los(c, ep)) continue;
                applyHit(p.owner, e, lerp(dmgMax, dmgMin, d / splashRadius), p.weaponId ?? "blaster");
            }
            // (specials.js areaHit: bubbles and tanks in the burst — with that module)
        }

        void updateBombs(float dt)
        {
            var bomb = Config.SUBS["bomb"];
            for (int i = bombs.Count - 1; i >= 0; i--)
            {
                var b = bombs[i];
                b.age += dt;
                b.vel.y -= 24 * dt;
                Vector3 from = b.pos;
                b.pos += b.vel * dt;
                var hit = G.physics.segment(from, b.pos, _hit);
                if (hit.hit)
                {
                    if (b.kind == "storm") { spawnCloud(b); disposeBomb(b); bombs.RemoveAt(i); continue; }
                    b.pos = hit.point + hit.normal * 0.21f;
                    float vn = Vector3.Dot(b.vel, hit.normal);
                    b.vel -= hit.normal * (vn * 1.35f);
                    b.vel *= hit.normal.y > 0.6f ? 0.45f : 0.6f;
                    if (hit.normal.y > 0.6f && b.fuse < 0)
                    {
                        b.fuse = bomb.fuse;
                        G.audio?.play("bomb_beep", new AudioOpts { pos = b.pos, volume = 0.6f });
                        emit("bomb:arm", new Ev { actor = b.owner, pos = b.pos, team = b.team, radius = bomb.radius });
                    }
                }
                if (b.kind == "storm" && b.age > 1.1f) { spawnCloud(b); disposeBomb(b); bombs.RemoveAt(i); continue; }
                if (b.fuse >= 0)
                {
                    b.fuse -= dt;
                    b.beepT -= dt;
                    float k = 1 - b.fuse / bomb.fuse;
                    CharacterMats.setLampIntensity(b.mat, (Mathf.Sin(b.age * (10 + k * 30)) * 0.5f + 0.5f) * (0.4f + k * 1.8f));
                    b.mesh.localScale = Vector3.one * (1 + k * 0.35f + Mathf.Sin(b.age * 40) * 0.03f * k);
                    if (b.beepT <= 0)
                    {
                        b.beepT = 0.3f - k * 0.2f;
                        if ((G.camPos - b.pos).sqrMagnitude < 30 * 30) G.audio?.play("bomb_beep", new AudioOpts { pos = b.pos, volume = 0.35f + k * 0.4f, pitch = 1 + k * 0.25f });
                    }
                    if (b.fuse <= 0) { explodeBomb(b); disposeBomb(b); bombs.RemoveAt(i); continue; }
                }
                if (b.pos.y < PLAYER.waterY - 1.8f) { disposeBomb(b); bombs.RemoveAt(i); continue; }
                b.mesh.localPosition = b.pos;
                float sk = b.fuse < 0 ? 1 : 0.2f;
                b.rotX += b.spin.x * dt * sk; b.rotZ += b.spin.y * dt * sk;
                b.mesh.localRotation = ThreeSpace.eulerXYZ(b.rotX, 0, b.rotZ);
            }
        }

        void updateClouds(float dt)
        {
            var sp = Config.SPECIALS["storm"];
            for (int i = clouds.Count - 1; i >= 0; i--)
            {
                var c = clouds[i];
                c.t += dt;
                float grow = clamp(c.t / 0.5f, 0, 1), fade = clamp((c.dur - c.t) / 0.6f, 0, 1);
                float s = (0.3f + 0.7f * (1 - Mathf.Pow(1 - grow, 3))) * (0.2f + 0.8f * fade);
                c.group.localScale = Vector3.one * s;
                Vector3 gp = c.group.localPosition + c.dir * (sp.driftSpeed * dt);
                c.group.localPosition = gp;
                for (int j = 0; j < c.puffs.Length; j++) { var lp = c.puffs[j].localPosition; lp.y += Mathf.Sin(G.time * 1.6f + c.bob[j]) * 0.0025f; c.puffs[j].localPosition = lp; }
                c.loop?.set(0.6f * fade, null, gp);
                if (c.t < c.dur - 0.3f)
                {
                    G.fx?.rain(gp, sp.radius * s, G.teamColors[c.team], dt, false);
                    c.rainT -= dt;
                    while (c.rainT <= 0)
                    {
                        c.rainT += 0.045f;
                        float an = random() * Mathf.PI * 2, r = Mathf.Sqrt(random()) * sp.radius;
                        var g = G.physics.raycast(new Vector3(gp.x + Mathf.Cos(an) * r, gp.y - 0.8f, gp.z + Mathf.Sin(an) * r), DOWN, 12, _hit);
                        if (g.hit) c.owner.addTurfNoSpecial(G.paint.splat(g.point + g.normal * 0.1f, 0.45f + random() * 0.35f, c.team, new SplatOpts { seed = random() }));   // (a special's rain)
                    }
                    foreach (var e in G.actors)
                    {
                        if (e.team == c.team || !e.alive) continue;
                        float dx = e.pos.x - gp.x, dz = e.pos.z - gp.z;
                        if (dx * dx + dz * dz > sp.radius * sp.radius || e.pos.y > gp.y) continue;
                        if (!G.physics.los(new Vector3(e.pos.x, e.pos.y + 1.2f, e.pos.z), new Vector3(e.pos.x, gp.y - 0.6f, e.pos.z))) continue;
                        bool killed = e.damage(sp.dps * dt, c.owner, "storm");
                        if (killed) emit("hit", new Ev { attacker = c.owner, victim = e, damage = 0, killed = true, weaponId = "storm" });
                    }
                }
                if (c.t >= c.dur) { emit("storm:end", new Ev { pos = gp, team = c.team, actor = c.owner }); disposeCloud(c); clouds.RemoveAt(i); }
            }
        }

        void updateBeams(float dt)
        {
            var rootM = ThreeSpace.root.localToWorldMatrix;
            var rp = new RenderParams(ribbonMat) { shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false };
            for (int i = beams.Count - 1; i >= 0; i--)
            {
                var b = beams[i];
                b.t += dt;
                float k = b.t / b.life;
                if (k >= 1) { beamPool.Push(b); beams.RemoveAt(i); continue; }
                var mp = b.mpb;
                var cl = b.col.linear;
                mp.SetVector(ID_Color, new Vector4(cl.r, cl.g, cl.b, 1));
                mp.SetFloat(ID_T, b.t); mp.SetFloat(ID_Life, b.life); mp.SetFloat(ID_Len, b.len); mp.SetFloat(ID_Charge, b.charge); mp.SetFloat(ID_Seed, b.seed);
                mp.SetFloat(ID_Mode, 0);
                mp.SetFloat(ID_Width, b.th * 2.3f * (1 - 0.45f * Mathf.Min(1, k)));   // the streak thins as it dissipates (never swells)
                rp.matProps = mp;
                Graphics.RenderMesh(rp, ribbonMesh, 0, rootM * Matrix4x4.TRS(b.m, Quaternion.FromToRotation(ZAX, b.dir), new Vector3(b.th, b.th, b.len)));
            }
            // laser sights on every charging charger (so you can read where enemy snipers are aiming); the spinner spins up
            // with its own tells (motor whine, weapon meter) — no sight
            foreach (var a in G.actors)
            {
                bool on = a.alive && a.weaponRunner.charging && a.weapon?.kind == "charger";
                if (!on) continue;
                if (!sights.TryGetValue(a, out var mp)) sights[a] = mp = new MaterialPropertyBlock();
                Vector3 m = muzzle(a);
                Vector3 dir = aimFrom(a, m);
                var w = a.weapon;
                float ch = a.weaponRunner.charge;
                float range = lerp(w.rangeMin, w.rangeMax, ch);
                var hit = G.physics.raycast(m, dir, range, _hit);
                float len = hit.hit ? hit.dist : range;
                float th = 0.012f + ch * 0.012f;
                var cl = a.color.linear;
                mp.SetVector(ID_Color, new Vector4(cl.r, cl.g, cl.b, 1));
                mp.SetFloat(ID_Charge, ch); mp.SetFloat(ID_Len, len); mp.SetFloat(ID_T, G.time); mp.SetFloat(ID_Width, 0.014f + ch * 0.02f);
                mp.SetFloat(ID_Mode, 1); mp.SetFloat(ID_Life, 0.34f); mp.SetFloat(ID_Seed, 0);
                rp.matProps = mp;
                Graphics.RenderMesh(rp, ribbonMesh, 0, rootM * Matrix4x4.TRS(m, Quaternion.FromToRotation(ZAX, dir), new Vector3(th, th, len)));
            }
        }

        // The bomb's flight preview while the sub button is held (local player): the same integrator + step as updateBombs
        // (60 Hz semi-implicit Euler), one vertex every 2 steps → exact landing.
        public void updateArc(Actor a, bool show)
        {
            var sub = a?.sub ?? Config.SUBS["bomb"];
            // placed subs have no throw; noArc: kit subs that draw their own aim guide (tracer)
            if (!show || a == null || !a.alive || sub.placed || (Kits.sub(sub.kind)?.noArc ?? false))
            {
                arcLine.enabled = false; arcRing.gameObject.SetActive(false);
                return;
            }
            Vector3 p = a.pos + Vector3.up * 1.35f;
            Vector3 v = throwVelocity(a, sub.throwSpeed);
            int n = 0; bool landed = false;
            arcPts[n++] = ThreeSpace.toUnity(p);
            const int per = 2;
            for (int i = 0; i < (ARC_N - 1) * per; i++)
            {
                Vector3 q = p;
                v.y -= 24 * SIM_DT;
                p += v * SIM_DT;
                var h = G.physics.segment(q, p, _hit);
                if (h.hit)
                {
                    arcPts[n++] = ThreeSpace.toUnity(h.point); landed = true;
                    arcRing.localPosition = h.point + h.normal * 0.03f; arcRing.localRotation = Quaternion.FromToRotation(UP, h.normal);
                    break;
                }
                if ((i + 1) % per == 0) arcPts[n++] = ThreeSpace.toUnity(p);
                if (n >= ARC_N) break;
            }
            arcLine.positionCount = n;
            arcLine.SetPositions(arcPts);
            Color col = (a.ink >= sub.inkCost ? a.color : new Color(0.6f, 0.6f, 0.6f)) * 1.4f; col.a = 0.95f;
            arcMat.color = col;
            arcLine.enabled = true;
            arcRing.gameObject.SetActive(landed);
            float rs = 1 + Mathf.Sin(G.time * 8) * 0.06f;
            arcRing.localScale = new Vector3(rs, 1, rs);
        }

        // Every projectile = a glossy teardrop head (tail length from its speed, liquid wobble, a fat "squirt" pop as it
        // leaves the muzzle) + a string of satellite droplets that sway behind it and close up as it slows. Blaster balls
        // swell and jiggle in the last moments before their mid-air burst.
        void putBlob(int n, Matrix4x4 m, Color linear, Vector4 shape)
        {
            int c = n / CHUNK, j = n % CHUNK;
            blobM[c][j] = m; blobC[c][j] = new Vector4(linear.r, linear.g, linear.b, 1); blobS[c][j] = shape;
        }
        void draw()
        {
            int n = 0;
            Matrix4x4 rootM = ThreeSpace.root.localToWorldMatrix;
            foreach (var p in list)
            {
                if (n >= MAX_BLOBS - 5) break;
                if (p.delay > 0) continue;
                float sp = p.vel.magnitude;
                Vector3 dir = p.vel * (1 / Mathf.Max(sp, 1e-3f));
                Quaternion q = sp > 1e-3f ? Quaternion.FromToRotation(ZAX, dir) : Quaternion.identity;
                float g = Mathf.Min(1, p.age * 20);
                float vis = (p.vis > 0 ? p.vis : p.size) * g * (1 + 0.3f * Mathf.Sin(g * Mathf.PI));
                float tail = p.tail0 + Mathf.Min(p.tailK, sp * 0.04f) * g;
                float wob = p.wob, bright = 1;
                if (p.type == "blast")
                {
                    float k = smoothstep(0.8f, 1, p.age / p.life);
                    vis *= 1 + 0.34f * k; wob *= 1 + 2.4f * k; bright = 1 + 0.9f * k; tail *= 1 - 0.55f * k;
                }
                float ph = p.seed * 40 + p.age * (p.wobF > 0 ? p.wobF : 20);
                Color col = p.owner.color.linear;
                putBlob(n++, rootM * Matrix4x4.TRS(p.pos, q, new Vector3(vis, vis, vis)), col * bright, new Vector4(tail, wob, ph, p.nose));
                // satellites: only once the head has travelled clear of the gun, thinning out down the string
                int ns = Mathf.Min(p.sats, SAT_SIZE.Length);
                if (ns == 0 || sp < 4) continue;
                float trav = Vector3.Distance(p.start, p.pos);
                Vector3 v2 = Mathf.Abs(dir.y) < 0.95f ? new Vector3(-dir.z, 0, dir.x).normalized : new Vector3(1, 0, 0);
                Vector3 v3 = Vector3.Cross(dir, v2);
                float r0 = p.vis > 0 ? p.vis : p.size, spk = 0.55f + 0.45f * Mathf.Min(1, sp / 25), fade = 1 - 0.45f * Mathf.Min(1, p.age / p.life);
                for (int i = 0; i < ns && n < MAX_BLOBS; i++)
                {
                    float back = r0 * (tail + 1.15f + i * 1.8f) * spk;
                    if (trav < back + r0 * 1.6f) break;
                    float sph = p.seed * 31 + i * 2.4f + p.age * 11;
                    float lat = r0 * (0.16f + i * 0.16f);
                    Vector3 at = p.pos - dir * back + v2 * (Mathf.Sin(sph) * lat) + v3 * (Mathf.Cos(sph * 1.3f) * lat);
                    float s = r0 * SAT_SIZE[i] * fade * (1 + 0.14f * Mathf.Sin(sph * 2.1f));
                    putBlob(n++, rootM * Matrix4x4.TRS(at, q, new Vector3(s, s, s)), col, new Vector4(1.3f + 0.25f * spk, 0.05f, sph * 3, 0));
                }
            }
            for (int c = 0; c * CHUNK < n; c++)
            {
                int cnt = Mathf.Min(CHUNK, n - c * CHUNK);
                var mp = blobP[c];
                mp.SetVectorArray(ID_IColor, blobC[c]); mp.SetVectorArray(ID_IShape, blobS[c]);
                Graphics.DrawMeshInstanced(blobMesh, 0, blobMat, blobM[c], cnt, mp, ShadowCastingMode.On, true);
            }
        }
    }
}

namespace Inkwave
{
    // Read-only kit state a kit weapon's runner exposes to character.js (Sponge Mitts: leap charge, leap, landing, cling).
    public class KitState
    {
        public bool mitts, charging, leaping, cling, hasCling, hang;   // hang: the actor holds the kid in place (wall cling)
        public float charge, landT = 99;
        public Vector3 clingN, clingPt;
    }
}
