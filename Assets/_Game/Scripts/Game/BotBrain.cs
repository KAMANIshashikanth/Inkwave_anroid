// Port of src/game/bots.js — bot brain: picks turf to claim, paths there (swimming through its own ink), paints on the
// move, spots and fights enemies with human-ish reaction time and aim error, refills ink, throws bombs and uses
// specials. Motion: aim is a critically-damped spring with a turn-rate cap and a smoothly wandering error (plus an
// acquisition over/undershoot that settles), shots follow the bot's *actual* aim ray, the move command slews its
// heading (no twitch at waypoint switches / strafe flips), strafes ease, bots dodge-hop when hit, swim in to close
// distance and retreat through own ink to heal when they're losing a duel.
//
// M1 scope: Turf War play. The Zone Control team plan, Boss Battle, the device/canopy "threats" layer, kit weapons and
// the special-driving switch (zooka, stamp, crab …) arrive with M3 / M7 — their call sites are marked.
using System.Collections.Generic;
using System.Linq;
using Inkwave.World;
using UnityEngine;
using static Inkwave.Ctx;

namespace Inkwave
{
    public static partial class G
    {
        public static NavGraph nav;
    }

    public class BotBrain
    {
        // weapon families the brain treats alike: close-range pushers, and charge-then-release weapons
        public static readonly HashSet<string> MELEE = new HashSet<string> { "roller", "brush" };
        public static readonly HashSet<string> CHARGES = new HashSet<string> { "charger", "spinner", "splatling" };
        /** sub kinds that are thrown as a lob (bots.js THROWN): lobbed over a shield */
        public static readonly HashSet<string> THROWN = new HashSet<string> { "bomb", "sticky", "burst", "mist", "seeker" };
        public static readonly HashSet<string> LONG = new HashSet<string> { "charger", "spinner", "splatling" };
        static float wander(float x) => Mathf.Sin(x) * 0.6f + Mathf.Sin(x * 2.27f + 1.3f) * 0.4f;

        readonly Actor a;
        public Actor actor => a;
        public DifficultyDef diff;
        public object kitMem;   // a kit weapon's own bot memory (brain._brolly …: Kits.main(kind).bot hooks)
        public List<int> path; public int pi, goal = -1;
        public float repath; float goalTimer;
        public Actor target;
        public float seeTimer, react; float lostTimer, stuck, jumpCd, bestD; public float noProg;
        public string mode;
        float sweep;
        public float aimYaw, aimPitch;
        public float bombCd;
        float strafe, strafeT, think, refillUntil, chargeRelease, aimYawV, aimPitchV, acqT, acqSignY, acqSignP;
        public float paintPause;
        float ph1, ph2, t, strafeS, strafeAmp, mvYaw, mvMag, dodgeCd; public float retreatT;
        bool _firing, _wasDead, _bombAim, _releaseBomb, _needJump, _skipped, _wasPlanted;
        float plantT;   // twins: how long to stay planted in rapid mode after a roll
        int strikes; float strikeT, wiggleT, wiggleYaw, airStill, dispT, moveAcc;
        Vector3 snap;
        float paintYawOff, paintScanT, goalCheckT, climbT, noClimbUntil;
        Aim _climbAim;
        public bool climbAiming => _climbAim != null;   // driving a climb edge (bots.js brain._climbAim)
        NavNode navBack; float navBackT;
        float _laT; int _laPi = -1; public int _laTi; public List<int> _laPath;
        float _ledgeT = -9; int _ledgeN;
        int _waterSide = 1;
        readonly RegionStats _stats = new RegionStats();
        readonly Hit _walkHit = new Hit(), _wh = new Hit();
        // enemy shields (kit canopies): the threat scan's list, flanking a held one, steering round a launched one
        readonly List<ShieldInfo> _shl = new List<ShieldInfo>();
        float thrScanT = random() * 0.25f, flankT, canT; public float blockT; int flankSide, canSide; ShieldInfo canRef; bool _canBroke, thrPulse;
        readonly Aim _thrAim = new Aim();
        // enemy devices hunting a player (kit subs: Waddle Bombs, Tide Torpedoes): the one being dealt with, the best
        // candidate from the last scan, and when each one noticed may be acted on (after our reaction time)
        public ThreatInfo thr; ThreatInfo thrCand; string thrAct; int thrActs, thrSide;
        float thrEvT, thrEvYaw, thrAcqT, thrLosT, thrSignY, thrSignP; bool thrLos, thrFiring;
        readonly Dictionary<ThreatInfo, float> _thrMem = new Dictionary<ThreatInfo, float>();
        static readonly List<ThreatInfo> _thrList = new List<ThreatInfo>();
        /** Counter-play vs enemy devices (bots.js THREAT_STATS): what the bots did (engagements, not frames) — tests read it */
        public static class THREAT_STATS { public static int noticed, shoot, evade, swim, sidestep, gone; }

        class Aim { public float yaw, pitch, dist; }

        public BotBrain(Actor actor, string difficulty = "normal")
        {
            a = actor;
            setDifficulty(difficulty);
            reset();
        }

        public void setDifficulty(string d) => diff = Config.DIFFICULTY.TryGetValue(d ?? "normal", out var x) ? x : Config.DIFFICULTY["normal"];

        public void reset()
        {
            path = null; pi = 0; goal = -1; repath = 0; goalTimer = 0;
            target = null; seeTimer = 0; react = 0; lostTimer = 0;
            stuck = 0; jumpCd = 0; bestD = float.PositiveInfinity; noProg = 0;
            mode = "paint";
            sweep = random() * 10;
            aimYaw = a.yaw; aimPitch = 0;
            strafe = random() < 0.5f ? 1 : -1; strafeT = 0;
            bombCd = 3 + random() * 4;
            think = random() * 0.2f;
            refillUntil = 0;
            chargeRelease = 0.95f + random() * 0.05f;
            paintPause = 0;
            aimYawV = 0; aimPitchV = 0;
            acqT = 9; acqSignY = 0; acqSignP = 0;
            ph1 = random() * 20; ph2 = random() * 20; t = random() * 10;
            strafeS = 0; strafeAmp = 1;
            mvYaw = a.yaw; mvMag = 0;
            dodgeCd = 1 + random() * 2;
            retreatT = 0; _firing = false;
            strikes = 0; strikeT = 0; wiggleT = 0; wiggleYaw = 0; airStill = 0;
            dispT = 0; moveAcc = 0; snap = a.pos; paintYawOff = 0; paintScanT = 0; goalCheckT = 0;
            climbT = 0; noClimbUntil = 0; _climbAim = null;
            navBack = null; navBackT = 0;
        }

        void idle() { var it = a.intent; it.move = Vector3.zero; it.fire = it.squid = it.sub = it.jump = it.special = false; mvMag = 0; }

        public void update(float dt)
        {
            var it = a.intent;
            if (!a.alive) { idle(); path = null; target = null; _wasDead = true; navBack = null; navBackT = 0; return; }
            if (_wasDead && G.match != null && G.match.playing())
            {
                // just respawned: face the way the body faces, then sometimes super jump to the teammate furthest up the field
                _wasDead = false;
                aimYaw = a.yaw; aimPitch = 0; aimYawV = 0; aimPitchV = 0;
                if (random() < 0.5f)
                {
                    var enemyPad = G.level.spawnPads[1 - a.team];
                    Actor best = null; float bd = float.PositiveInfinity;
                    foreach (var o in G.actors)
                    {
                        if (o == a || o.team != a.team || !o.alive || o.superJumpState != null) continue;
                        float d = Vector3.Distance(o.pos, enemyPad);
                        if (d < bd && Vector3.Distance(o.pos, a.pos) > 18) { bd = d; best = o; }
                    }
                    // a team jump beacon further up the field beats a teammate
                    SubItem bestB = null;
                    foreach (var b in G.subs.beaconsFor(a.team, _beacons))
                    {
                        float d = Vector3.Distance(b.pos, enemyPad);
                        if (d < bd - 2 && Vector3.Distance(b.pos, a.pos) > 18) { bd = d; bestB = b; }
                    }
                    bool ok = bestB != null ? G.subs.jumpToBeacon(a, bestB) : best != null && a.superJump(best);
                    if (ok) { path = null; goalTimer = 0; }
                }
            }
            if (a.superJumpState != null) { idle(); return; }
            if (G.match == null || !G.match.playing()) { idle(); return; }
            think -= dt; jumpCd -= dt; bombCd -= dt; strafeT -= dt; paintPause -= dt; dodgeCd -= dt;
            acqT += dt; t += dt;

            // ---------------- perception
            if (think <= 0)
            {
                think = 0.15f + random() * 0.1f;
                perceive();
            }
            if (target != null && !target.alive) target = null;

            // ---------------- mode selection (retreat = break line of sight and heal in own ink when losing a duel)
            float inkFrac = a.ink / PLAYER.inkMax;
            float hpFrac = a.hp / PLAYER.hp;
            var w = a.weapon;
            if (mode == "retreat")
            {
                retreatT -= dt;
                if (hpFrac > 0.85f || retreatT <= 0 || (target == null && hpFrac > 0.6f)) { mode = "paint"; path = null; goalTimer = 0; }
            }
            else if (target != null && seeTimer > 0 && ((hpFrac < 0.34f && !MELEE.Contains(w.kind) && a.lastDamage < 0.8f) || hpFrac < 0.2f) && random() < 0.6f * dt * 60 * diff.fireDiscipline
                && !(Kits.main(w.kind)?.bot?.stayIn?.Invoke(this) ?? false))   // (a kit may veto: e.g. the Cutlass finishing a fight at blade range)
            {
                mode = "retreat"; retreatT = 2.2f + random() * 1.4f; repath = 0; pickRetreat();
            }
            if (mode != "refill" && mode != "retreat" && inkFrac < 0.12f && !(target != null && seeTimer > 0 && !MELEE.Contains(w.kind) && inkFrac > 0.05f))
            {
                mode = "refill"; refillUntil = 0.85f + random() * 0.1f;
            }
            if (mode == "refill" && inkFrac >= refillUntil) mode = "paint";
            if (mode != "refill" && mode != "retreat") mode = target != null ? "fight" : "paint";

            // ---------------- navigation goal
            goalTimer -= dt; repath -= dt;
            if (mode == "fight" && target != null) { if (repath <= 0) pathTo(target.pos, 0.6f); }
            else if (mode == "refill") { if (repath <= 0 || path == null) pickRefill(); }
            else if (mode == "retreat") { if (repath <= 0 || path == null) pickRetreat(); }
            else
            {
                // our team has already covered the goal area: move on instead of walking over our own ink
                goalCheckT -= dt;
                if (goalCheckT <= 0 && goal >= 0 && path != null)
                {
                    goalCheckT = 1;
                    var g = G.nav.nodes[goal]; var st = G.paint.regionStats(g.x, g.y, g.z, 3, a.team, _stats);
                    if (st.n > 0 && st.own > 0.85f) goalTimer = 0;
                }
                if (goalTimer <= 0 || path == null || pi >= path.Count) pickPaintGoal();
            }

            // ---------------- steering along the path
            Vector3 move = steer(dt);
            unstick(dt, ref move);
            if (path == null && wiggleT <= 0) backOnNav(dt, ref move);
            bool wantMove = move.sqrMagnitude > 0.01f;

            // ---------------- actions
            it.fire = false; it.sub = false; it.special = false; it.squid = false; it.jump = false;
            float wantYaw = wantMove ? Mathf.Atan2(move.x, move.z) : a.yaw;
            float wantPitch = -0.1f;
            bool enemyVisible = target != null && seeTimer > 0;
            float idealYaw = 0, idealPitch = 0, aimDist = 6, fightDist = 0;

            if ((mode == "fight" || mode == "retreat") && target != null)
            {
                var tg = target;
                float dx = tg.pos.x - a.pos.x, dz = tg.pos.z - a.pos.z;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                fightDist = dist;
                float range = rangeOf();
                // lead the target by the projectile's time to arrive
                float spd = w.projSpeed > 0 ? w.projSpeed : w.speedMax > 0 ? w.speedMax : w.throwSpeed > 0 ? w.throwSpeed : 30;
                float lead = w.kind == "charger" ? 0 : dist / spd;
                Vector3 aimAt = new Vector3(tg.pos.x + tg.vel.x * lead, tg.pos.y + tg.smoothY + (tg.form == "squid" ? 0.3f : 0.85f), tg.pos.z + tg.vel.z * lead);
                Vector3 rel = aimAt - new Vector3(a.pos.x, a.pos.y + 1.1f, a.pos.z);
                idealYaw = Mathf.Atan2(rel.x, rel.z);
                idealPitch = Mathf.Atan2(rel.y, Mathf.Sqrt(rel.x * rel.x + rel.z * rel.z));
                aimDist = rel.magnitude;
                // human aim error: a slow wander plus an acquisition error that settles over the reaction time
                float e = diff.aimError;
                float acq = Mathf.Exp(-acqT / Mathf.Max(0.12f, diff.reaction * 0.9f));
                wantYaw = idealYaw + e * (0.75f * wander(t * 1.7f + ph1) + 2.4f * acq * acqSignY);
                wantPitch = idealPitch + e * 0.6f * (0.75f * wander(t * 2.1f + ph2) + 1.6f * acq * acqSignP);
                if (mode == "fight")
                {
                    // movement in combat: keep preferred distance + eased strafing (+ swim in to close distance)
                    float pref = w.kind == "charger" ? range * 0.8f : MELEE.Contains(w.kind) ? 0.5f : range * 0.7f;
                    if (strafeT <= 0) { strafeT = 0.6f + random() * 1.2f; strafe = random() < 0.5f ? -1 : 1; strafeAmp = 0.5f + random() * 0.5f; }
                    strafeS += (strafe * strafeAmp - strafeS) * (1 - Mathf.Exp(-5 * dt));
                    float nx = dx / Mathf.Max(dist, 0.01f), nz = dz / Mathf.Max(dist, 0.01f);
                    float mvx = 0, mvz = 0;
                    if (dist > pref + 1.2f && wantMove) { mvx = move.x; mvz = move.z; }
                    else if (dist < pref - 1.5f && !MELEE.Contains(w.kind)) { mvx = -nx; mvz = -nz; }
                    if (!(CHARGES.Contains(w.kind) && a.weaponRunner.charging)) { mvx += -nz * strafeS * 0.9f; mvz += nx * strafeS * 0.9f; }
                    if (MELEE.Contains(w.kind) && dist < 7) { mvx = nx; mvz = nz; }
                    // twin pistols: after a roll, stand planted for a moment in rapid mode (moving would drop back to dual mode)
                    if (w.kind == "twins")
                    {
                        bool planted = a.weaponRunner.turret;
                        if (planted && !_wasPlanted) plantT = 0.7f + random() * 0.7f;
                        _wasPlanted = planted;
                        plantT -= dt;
                        if (planted && plantT > 0) { mvx = 0; mvz = 0; }
                    }
                    // (an enemy on a Mega Stamp: step out of its line — with specials.js)
                    float l = Mathf.Sqrt(mvx * mvx + mvz * mvz);
                    move = l > 0.01f ? new Vector3(mvx / l, 0, mvz / l) : Vector3.zero;
                    // fire only when the *actual* aim is on the body (shots follow the visible aim, not the target)
                    float offY = angleDiff(aimYaw, idealYaw), offP = aimPitch - idealPitch;
                    float off = Mathf.Sqrt(offY * offY + offP * offP);
                    float tol = Mathf.Max(0.05f, Mathf.Atan2(0.55f, dist)) * (_firing ? 2.4f : 1.5f);
                    bool aimed = off < tol;
                    _firing = false;
                    var BK = Kits.main(w.kind)?.bot;
                    if (enemyVisible && react <= 0 && aimed && inkFrac > 0.02f)
                    {
                        var wr = a.weaponRunner;
                        if (BK?.fight != null)
                        {
                            // kit weapons decide their own trigger (and may steer `move`)
                            var c = kctx(w, dist, range, dt, move, tg, inkFrac); it.fire = BK.fight(this, c); move = c.move;
                        }
                        else if (w.kind == "spinner")
                        {
                            // spin up to the release point, let go, and let the stream run (aim keeps tracking during the burst)
                            it.fire = wr.burstT <= 0 && !(wr.charging && wr.charge >= chargeRelease);
                        }
                        else if (w.kind == "brush") it.fire = dist < 4.5f;
                        else if (w.kind == "charger")
                        {
                            it.fire = !(wr.charging && wr.charge >= chargeRelease);
                            if (wr.charging) move *= 0.3f;
                        }
                        else if (w.kind == "roller") it.fire = dist < 5.5f || (wr.rolling && dist < 8);
                        else it.fire = dist < range * 1.08f;
                        _firing = it.fire;
                        if (bombCd <= 0 && fightSub(dist)) { it.sub = true; _bombAim = true; }
                    }
                    else if (CHARGES.Contains(w.kind) && a.weaponRunner.charging && !enemyVisible)
                    {
                        it.fire = true;   // keep charge while target briefly hidden
                    }
                    // out of range with own ink underfoot: swim in (fast, hard to hit) instead of walking
                    if (!it.fire && !a.weaponRunner.charging && dist > range * 1.15f && a.groundTeam == 1) it.squid = true;
                    if (a.lastDamage < 0.25f && dodgeCd <= 0 && a.grounded && !CHARGES.Contains(w.kind) && random() < (w.kind == "twins" ? 0.6f : 0.3f)
                        && !nearWater(a, w.kind == "twins" ? w.rollDist + 0.4f : 1.6f))
                    {
                        // dodge: a strafe-hop right after taking a hit (twins: a dodge roll, since they're firing and moving)
                        it.jump = true; dodgeCd = w.kind == "twins" ? 0.9f + random() : 2 + random() * 2.5f;
                    }
                    // an enemy canopy between us and the target: flank round it, save the shots, lob a sub over it
                    if (enemyVisible) shieldFight(tg, dist, ref move, it, dt); else blockT = 0;
                    // kit weapons with their own fight tactics (bot.tactics, e.g. the Cutlass: flank, pre-charge, swim in, strafe):
                    // every fight frame, aimed or not, after the defaults above — may override move, fire, squid and jump
                    if (BK?.tactics != null)
                    {
                        var c = kctx(w, dist, range, dt, move, tg, inkFrac); c.visible = enemyVisible; c.aimed = aimed; c.canFire = enemyVisible && react <= 0 && aimed && inkFrac > 0.02f;
                        BK.tactics(this, c); move = c.move;
                    }
                    // special
                    if (a.specialReady() && wantSpecial("fight", dist, enemyVisible)) it.special = true;
                }
                else
                {
                    // retreat: swim away through own ink, keep eyes on the threat
                    it.squid = true;
                    // (kits with their own tactics run the retreat too — ctx.retreat — e.g. the Cutlass runs where there's no ink)
                    var RK = Kits.main(w.kind)?.bot;
                    if (RK?.tactics != null) { var c = kctx(w, dist, range, dt, move, tg, inkFrac); c.visible = enemyVisible; c.retreat = true; RK.tactics(this, c); move = c.move; }
                }
            }
            else if (mode == "paint")
            {
                // paint the most valuable ground in reach (unclaimed, and enemy ink even more), with a sweeping aim around it
                sweep += dt * (w.kind == "charger" ? 0.8f : 2.1f);
                paintScanT -= dt;
                if (paintScanT <= 0 && !MELEE.Contains(w.kind))
                {
                    paintScanT = 0.35f + random() * 0.15f;
                    float reach = w.kind == "charger" || w.kind == "spinner" || w.kind == "splatling" ? 8.5f : w.kind == "bucket" || w.kind == "slosher" ? 7 : w.kind == "blaster" ? 5 : 4.5f;
                    float bestOff = 0, bestV = -1;
                    foreach (float off in new[] { 0, -0.6f, 0.6f, -1.2f, 1.2f })
                    {
                        float yw = (wantMove ? Mathf.Atan2(move.x, move.z) : aimYaw) + off;
                        var st = G.paint.regionStats(a.pos.x + Mathf.Sin(yw) * reach, a.pos.y, a.pos.z + Mathf.Cos(yw) * reach, 2.2f, a.team, _stats);
                        float v = st.n > 0 ? st.empty + st.enemy * 1.4f - Mathf.Abs(off) * 0.12f : -1;
                        if (v > bestV) { bestV = v; bestOff = off; }
                    }
                    paintYawOff = bestOff;
                }
                float sweepAmt = MELEE.Contains(w.kind) ? 0 : 0.35f;
                wantYaw += (MELEE.Contains(w.kind) ? 0 : paintYawOff) + Mathf.Sin(sweep) * sweepAmt;
                var PK = Kits.main(w.kind)?.bot;
                wantPitch = PK?.paintPitch != null ? PK.paintPitch.Value : w.kind == "charger" ? -0.12f : w.kind == "spinner" || w.kind == "slosher" ? -0.16f : w.kind == "blaster" ? -0.28f : w.kind == "splatling" ? -0.3f
                    : w.kind == "bucket" ? -0.5f : -0.42f;
                var ahead = G.paint.regionStats(a.pos.x + Mathf.Sin(wantYaw) * 4, a.pos.y, a.pos.z + Mathf.Cos(wantYaw) * 4, 3, a.team, _stats);
                bool needPaint = ahead.n == 0 || ahead.own < 0.75f;
                if (PK?.paint != null)
                {
                    var c = kctx(w, 0, rangeOf(), dt, move, null, inkFrac); c.needPaint = needPaint; c.wantMove = wantMove;
                    it.fire = PK.paint(this, c); move = c.move;
                }
                else if (MELEE.Contains(w.kind)) it.fire = inkFrac > 0.08f && (needPaint || random() < 0.02f) && wantMove;
                else if (w.kind == "spinner")
                {
                    // spin to ~60 %, release, let the stream paint, breather
                    var wr = a.weaponRunner;
                    if (wr.burstT > 0) it.fire = false;
                    else if (wr.charging) { it.fire = wr.charge < 0.6f; if (!it.fire) paintPause = 0.25f + random() * 0.3f; }
                    else it.fire = needPaint && inkFrac > 0.3f && paintPause <= 0;
                }
                else if (w.kind == "charger")
                {
                    // charge to ~70 % and release a paint line, then a short breather before the next one
                    if (a.weaponRunner.charging)
                    {
                        it.fire = a.weaponRunner.charge < 0.7f;
                        if (!it.fire) paintPause = 0.3f + random() * 0.35f;
                    }
                    else it.fire = needPaint && inkFrac > 0.3f && paintPause <= 0;
                }
                else it.fire = needPaint && inkFrac > 0.18f;
                // travel as a squid through own ink when not painting
                if (!it.fire && pathRemaining() > 5 && a.groundTeam == 1) it.squid = true;
                if (bombCd <= 0 && paintSub()) { it.sub = true; _bombAim = true; }
                if (a.specialReady() && wantSpecial("paint", 0, false)) it.special = true;
            }
            else if (mode == "refill")
            {
                it.squid = a.groundTeam == 1 || pathRemaining() > 2;
                if (a.groundTeam != 1 && pathRemaining() < 1.5f && inkFrac > 0.03f)
                {
                    // no ink here: paint a puddle to swim in
                    it.squid = false; it.fire = true;
                    wantPitch = -1.0f;
                }
            }
            // ---------------- running specials
            specialCtl(dt, it, ref move, fightDist, enemyVisible);
            // ---------------- enemy devices after us (with the kit subs); enemy launched canopies in the way: steer round the
            // nearer edge (or break one we're boxed in by)
            var thrAim = threatCtl(dt, it, ref move);
            var canAim = canopyCtl(dt, it, ref move, thrAim == null);
            if (thrAim == null && canAim != null) thrAim = canAim;
            if (thrAim != null) { wantYaw = thrAim.yaw; wantPitch = thrAim.pitch; }
            // ---------------- wall climb (nav 'climb' edge): ink the wall column up to the top, then swim up it
            _climbAim = climb(dt, ref move, it);
            if (_climbAim != null) { wantYaw = _climbAim.yaw; wantPitch = _climbAim.pitch; }
            tail(dt, move, wantYaw, wantPitch, aimDist, wantMove, thrAim, enemyVisible);
        }

        // bomb release, the aim spring, the smoothed move command, edge guard, stuck recovery
        void tail(float dt, Vector3 move, float wantYaw, float wantPitch, float aimDist, bool wantMove, Aim thrAim, bool enemyVisible)
        {
            var it = a.intent; var w = a.weapon;
            // throws and placements only happen in humanoid form: stay upright for the press and the release
            if (_bombAim) { it.sub = true; it.squid = false; _bombAim = false; _releaseBomb = true; }
            else if (_releaseBomb) { it.sub = false; it.squid = false; _releaseBomb = false; }

            // ---------------- aim: critically-damped spring with a turn-rate cap (flicks accelerate and settle; no twitch)
            bool fighting = mode == "fight";
            bool snappy = fighting || thrAim != null;   // duelling, or drawing a bead on a device / canopy
            float om = snappy ? diff.aimOmega : 8;
            float maxRate = snappy ? diff.aimTurn : 6;
            wantPitch = clamp(wantPitch, -1.1f, 1.0f);
            aimYawV += (om * om * angleDiff(aimYaw, wantYaw) - 2 * om * aimYawV) * dt;
            aimYawV = clamp(aimYawV, -maxRate, maxRate);
            aimYaw += aimYawV * dt;
            if (aimYaw > Mathf.PI) aimYaw -= Mathf.PI * 2; else if (aimYaw < -Mathf.PI) aimYaw += Mathf.PI * 2;
            aimPitchV += (om * om * (wantPitch - aimPitch) - 2 * om * aimPitchV) * dt;
            aimPitchV = clamp(aimPitchV, -maxRate * 0.7f, maxRate * 0.7f);
            aimPitch = clamp(aimPitch + aimPitchV * dt, -1.1f, 1.0f);
            a.aimYaw = aimYaw; a.aimPitch = aimPitch;
            // shots go where the bot is actually aiming (its eye ray at the target's distance), never straight to the target
            {
                float cp = Mathf.Cos(aimPitch);
                float d = _climbAim != null ? _climbAim.dist : thrAim != null ? thrAim.dist : fighting && target != null ? aimDist : mode == "refill" ? 1.6f : 6;
                a.aimPoint = new Vector3(a.pos.x + Mathf.Sin(aimYaw) * cp * d, a.pos.y + 1.1f + Mathf.Sin(aimPitch) * d, a.pos.z + Mathf.Cos(aimYaw) * cp * d);
                if (!fighting && thrAim == null) { float gy = a.pos.y; if (a.aimPoint.y < gy) a.aimPoint.y = gy; }
            }

            // ---------------- never walk, strafe or swim off into the sea; stay a kid over grates spanning water
            avoidWater(ref move);
            if (it.squid && squidWouldDrop(move)) it.squid = false;

            // ---------------- smooth the move command: heading slews (no twitch at waypoint switches / strafe flips)
            float ml = Mathf.Min(1, move.magnitude);
            if (ml > 0.01f)
            {
                float des = Mathf.Atan2(move.x, move.z);
                float d = angleDiff(mvYaw, des);
                if (mvMag < 0.05f) mvYaw = des;
                else if (Mathf.Abs(d) > 2.1f) { mvYaw = des; mvMag *= 0.35f; }      // reversal: let the body plant and reverse
                else mvYaw += clamp(d, -11 * dt, 11 * dt);
            }
            mvMag += (ml - mvMag) * (1 - Mathf.Exp(-14 * dt));
            it.move = new Vector3(Mathf.Sin(mvYaw) * mvMag, 0, Mathf.Cos(mvYaw) * mvMag);
            // edge guard (after the smoothing, which can swing the heading past what avoidWater checked)
            if (mvMag > 0.05f && a.grounded && !a.climbing) it.move = edgeGuard(a, it.move);
            // stuck recovery, based on progress toward the current waypoint: hop → skip the waypoint → replan
            bool trying = path != null && wantMove && !(CHARGES.Contains(w.kind) && (a.weaponRunner.charging || a.weaponRunner.burstT > 0));
            if (!trying) noProg = 0;
            if (noProg > 0.7f && jumpCd <= 0 && a.grounded && !nearWater(a, 1.2f)) { it.jump = true; jumpCd = 1.0f; }
            if (noProg > 1.5f && path != null && pi < path.Count - 1 && !_skipped) { pi++; _skipped = true; bestD = float.PositiveInfinity; }
            if (noProg > 2.4f)
            {
                noProg = 0; _skipped = false; path = null; goalTimer = 0; repath = 0;
                // replanning keeps failing here: shake loose in a random direction and let the next goal come from elsewhere
                if (++strikes >= 2) { strikes = 0; wiggle(0.9f); }
                strikeT = 8;
            }
            if (noProg == 0) _skipped = false;
            stuck = noProg;
            if (_needJump && jumpCd <= 0 && a.grounded) { it.jump = true; jumpCd = 0.6f; _needJump = false; }
            // displacement watchdog: asking to move for 1.5 s but covering < 0.4 m means something the waypoint logic can't
            // see is holding us. Duels are exempt: strafing back and forth is meant to stay put.
            dispT += dt; moveAcc += mvMag * dt;
            if (dispT >= 1.5f)
            {
                float moved = Mathf.Sqrt((a.pos.x - snap.x) * (a.pos.x - snap.x) + (a.pos.z - snap.z) * (a.pos.z - snap.z));
                if (moveAcc / dispT > 0.45f && moved < 0.4f && !(mode == "fight" && enemyVisible) && a.grounded && _climbAim == null && !a.climbing)
                {
                    if (++strikes >= 2) { strikes = 0; goalTimer = 0; }
                    strikeT = 8;
                    wiggle(0.7f);
                }
                snap = a.pos; dispT = 0; moveAcc = 0;
            }
        }

        NavEdge climbEdge()
        {
            if (path == null || pi <= 0 || pi >= path.Count) return null;
            var e = G.nav.edge(path[pi - 1], path[pi]);
            return e != null && e.type == "climb" ? e : null;
        }

        // Drives a climb edge once we're at its wall. Returns the aim to hold or null.
        Aim climb(float dt, ref Vector3 move, Intent it)
        {
            var e = climbEdge();
            if (e == null) { climbT = 0; return null; }
            var top = G.nav.nodes[path[pi]];
            if (a.grounded && a.pos.y > top.y - 0.4f) { climbT = 0; return null; }       // on top: normal steering finishes
            float wx = e.wallP.x, wz = e.wallP.z, nx = e.wallN.x, nz = e.wallN.y;
            float dist = (a.pos.x - wx) * nx + (a.pos.z - wz) * nz;                       // distance out from the wall
            if (!a.climbing && (dist > 2.4f || Mathf.Sqrt((a.pos.x - wx) * (a.pos.x - wx) + (a.pos.z - wz) * (a.pos.z - wz)) > 2.8f)) return null;   // still walking up to it
            climbT += dt;
            if (climbT > 7)
            {                                                                           // not working: plan around climbs for a while
                climbT = 0; noClimbUntil = t + 12; path = null; repath = 0; goalTimer = 0;
                return null;
            }
            float yaw = Mathf.Atan2(-nx, -nz);
            Aim swim(ref Vector3 mv) { it.squid = true; it.fire = false; mv = new Vector3(-nx, 0, -nz); return new Aim { yaw = yaw, pitch = 0.5f, dist = 2 }; }
            if (a.climbing) return swim(ref move);
            float? gap = wallInkGap(wx, wz, nx, nz, a.pos.y, e.topY);
            if (gap == null) return swim(ref move);                                       // column is ours: swim up
            if (a.ink < PLAYER.inkMax * 0.05f) return null;                               // out of ink: refill logic takes over
            // stand ~1–1.6 m off the wall and paint the lowest un-inked strip, working upward
            if (dist < 0.9f) move = new Vector3(nx * 0.6f, 0, nz * 0.6f); else if (dist > 1.6f) move = new Vector3(-nx * 0.6f, 0, -nz * 0.6f); else move = Vector3.zero;
            it.squid = false;
            var w = a.weapon;
            if (CHARGES.Contains(w.kind)) it.fire = a.weaponRunner.burstT <= 0 && !(a.weaponRunner.charging && a.weaponRunner.charge >= 0.45f);
            else it.fire = true;
            float hd = Mathf.Max(0.5f, dist), dy = gap.Value + 0.3f - (a.pos.y + 1.1f);
            return new Aim { yaw = yaw, pitch = Mathf.Atan2(dy, hd), dist = Mathf.Sqrt(hd * hd + dy * dy) };
        }

        // Lowest height on the wall column in front of the climb spot that isn't our ink yet (null = inked to the top).
        float? wallInkGap(float wx, float wz, float nx, float nz, float y0, float topY)
        {
            var d = new Vector3(-nx, 0, -nz);
            for (float y = y0 + 0.3f; y < topY - 0.1f; y += 0.45f)
            {
                var o = new Vector3(wx + nx * 0.6f, y, wz + nz * 0.6f);
                if (!G.physics.raycast(o, d, 1.2f, _wh, true).hit) return null;            // wall ended below the top
                if (_wh.face < 0 || G.paint.sample(_wh.face, _wh.u, _wh.v) - 1 != a.team) return y;
            }
            return null;
        }

        // Remove any heading that would put us over open water: try the nearest safe heading, else stand still.
        void avoidWater(ref Vector3 move)
        {
            float l = Mathf.Sqrt(move.x * move.x + move.z * move.z);
            if (l < 0.05f) return;
            var L = G.level; float yaw = Mathf.Atan2(move.x, move.z);
            bool safe(float yw)
            {
                foreach (float d in new[] { 0.7f, 1.3f }) if (float.IsNegativeInfinity(L.groundHeight(a.pos.x + Mathf.Sin(yw) * d, a.pos.z + Mathf.Cos(yw) * d, 50))) return false;
                return true;
            }
            if (safe(yaw)) return;
            foreach (float off in new[] { 0.5f, -0.5f, 1.0f, -1.0f, 1.6f, -1.6f, 2.2f, -2.2f })
            {
                float yw = yaw + _waterSide * off;
                if (safe(yw)) { _waterSide = (int)Mathf.Sign(off) * _waterSide; move = new Vector3(Mathf.Sin(yw) * l, 0, Mathf.Cos(yw) * l); return; }
            }
            move = Vector3.zero;
        }

        // "Wet" = open sea under (x, z): no deck at all below y + 0.6 — exactly the rule that splats an actor
        static bool wet(float x, float z, float y) => float.IsNegativeInfinity(G.level.groundHeight(x, z, y + 0.6f));
        // ground all the way along a straight walk (samples every 0.45 m)
        public static bool dryLine(float x0, float y0, float z0, float x1, float z1)
        {
            float d = Mathf.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0)); int n = Mathf.CeilToInt(d / 0.45f);
            for (int i = 1; i <= n; i++) { float tt = (float)i / n; if (wet(x0 + (x1 - x0) * tt, z0 + (z1 - z0) * tt, y0)) return false; }
            return true;
        }
        // open water anywhere on a ring of radius r around the actor
        static bool nearWater(Actor a, float r)
        {
            for (int k = 0; k < 8; k++) { float tt = (k / 8f) * Mathf.PI * 2; if (wet(a.pos.x + Mathf.Cos(tt) * r, a.pos.z + Mathf.Sin(tt) * r, a.pos.y)) return true; }
            return false;
        }
        // final move command: if the ground a stopping distance ahead is water, slide along the edge or stop
        static Vector3 edgeGuard(Actor a, Vector3 mv)
        {
            float m = Mathf.Sqrt(mv.x * mv.x + mv.z * mv.z); if (m < 1e-4f) return mv;
            float dx = mv.x / m, dz = mv.z / m;
            float look = 0.6f + Mathf.Sqrt(a.vel.x * a.vel.x + a.vel.z * a.vel.z) * 0.17f;
            float px = a.pos.x, py = a.pos.y, pz = a.pos.z;
            bool bad(float ux, float uz) => wet(px + ux * 0.45f, pz + uz * 0.45f, py) || wet(px + ux * look, pz + uz * look, py);
            if (!bad(dx, dz)) return mv;
            foreach (float ang in new[] { 0.8f, -0.8f, 1.45f, -1.45f })
            {
                float c = Mathf.Cos(ang), s = Mathf.Sin(ang), nx = dx * c + dz * s, nz = -dx * s + dz * c;
                if (!bad(nx, nz)) return new Vector3(nx * m, 0, nz * m);
            }
            return Vector3.zero;
        }
        // squid form drops through grates: is there only water under us (or just ahead) once grates don't count?
        bool squidWouldDrop(Vector3 move)
        {
            var L = G.level;
            if (float.IsNegativeInfinity(L.groundHeight(a.pos.x, a.pos.z, 50, true))) return true;
            float l = Mathf.Sqrt(move.x * move.x + move.z * move.z);
            return l > 0.05f && float.IsNegativeInfinity(L.groundHeight(a.pos.x + (move.x / l) * 1.2f, a.pos.z + (move.z / l) * 1.2f, 50, true));
        }

        // Low on health mid-duel: head for own ink away from the threat (swim = heal + hard to spot), then come back.
        public void pickRetreat()
        {
            var tg = target;
            Vector3? bestP = null; float bs = float.NegativeInfinity;
            for (int i = 0; i < 16; i++)
            {
                float ang = random() * Mathf.PI * 2, r = 3 + random() * 8;
                Vector3 v = new Vector3(a.pos.x + Mathf.Cos(ang) * r, a.pos.y, a.pos.z + Mathf.Sin(ang) * r);
                var st = G.paint.regionStats(v.x, v.y, v.z, 1.4f, a.team, _stats);
                if (st.n == 0) continue;
                float away = tg != null ? Mathf.Sqrt((v.x - tg.pos.x) * (v.x - tg.pos.x) + (v.z - tg.pos.z) * (v.z - tg.pos.z)) - Mathf.Sqrt((a.pos.x - tg.pos.x) * (a.pos.x - tg.pos.x) + (a.pos.z - tg.pos.z) * (a.pos.z - tg.pos.z)) : 0;
                float score = st.own * 6 + away * 0.8f - r * 0.15f + (tg != null && !G.physics.los(v + Vector3.up, tg.pos + Vector3.up) ? 4 : 0);
                if (score > bs) { bs = score; bestP = v; }
            }
            if (bestP.HasValue) pathTo(bestP.Value, 0.5f); else path = null;
            repath = 1.0f;
        }

        // when to fire off the special (fight: distance to the target; paint: only the turf-claiming ones). A special not
        // ported yet plays as Tidal Slam, so it is used like one.
        static readonly HashSet<string> TURF_SPECIALS = new HashSet<string> { "storm", "strike", "booyah", "barrage", "sonar", "kraken", "crab", "zooka", "blower", "stamp" };
        bool wantSpecial(string m, float dist, bool vis)
        {
            string id = SpecialSystem.kindOf(SpecialSystem.resolve(a.specialId));
            float r = random();
            if (m == "fight")
            {
                switch (id)
                {
                    case "slam": return dist < 4.5f;
                    case "storm": return dist < 16;
                    case "barrage": return dist < 14;
                    case "bubbler": return dist < 12 && (a.lastDamage < 0.6f || r < 0.02f);
                    case "sonar": return r < 0.02f;
                    case "strike": return dist > 14 && r < 0.01f;
                    case "zooka": return vis && dist > 7 && dist < 36;
                    case "wail": return vis && dist > 5 && dist < 40;
                    case "kraken": return dist < 10;
                    case "blower": return dist < 14;
                    case "jetpack": return dist > 5 && dist < 22;
                    case "stamp": return dist < 9;
                    case "booyah": return dist > 9 && dist < 26 && r < 0.03f;
                    case "zipcaster": return dist < 16;
                    case "crab": return dist < 20;
                }
                return false;
            }
            if (!TURF_SPECIALS.Contains(id) || r > 0.012f) return false;
            var st = G.paint.regionStats(a.pos.x, a.pos.y, a.pos.z, 6, a.team, _stats);
            return st.own < 0.55f;
        }

        // driving a running special: most reuse the normal fight aim; these set the trigger / sub / movement they need
        // (the kinds still to port add their cases, the Cheer Orb cheering and the Mega Stamp guard with them)
        float barrageCd, blowT;
        void specialCtl(float dt, Intent it, ref Vector3 move, float dist, bool vis)
        {
            var s = a.specialActive;
            if (s == null) return;
            bool fighting = mode == "fight" && target != null;
            switch (s.kind ?? s.id)
            {
                case "barrage":
                    barrageCd -= dt;
                    if (barrageCd <= 0 && (!fighting || dist < 16)) { it.sub = true; _bombAim = true; barrageCd = 0.35f + random() * 0.3f; }
                    break;
                case "zooka":
                    it.fire = fighting ? vis && dist < 42 : random() < 0.06f;
                    it.squid = false;
                    break;
                case "blower":
                    blowT += dt;
                    it.fire = blowT < 0.9f;
                    if (blowT > 1.4f) blowT = 0;
                    it.squid = false;
                    break;
            }
        }

        // Mid-duel sub use (enemy in view at `dist`). Sets the cooldown and returns true to throw this frame.
        bool fightSub(float dist)
        {
            var sub = a.sub ?? Config.SUBS["bomb"]; string k = sub.kind;
            if (a.ink < sub.inkCost + 8 || k == "beacon") return false;
            { var SK = Kits.sub(k)?.bot; if (SK?.fight != null) return SK.fight(this, dist); }   // kit subs decide for themselves
            bool roll = random() < 0.02f * (1 + diff.fireDiscipline);
            bool go;
            switch (k)
            {
                case "burst": go = dist > 3 && dist < 12 && random() < 0.04f; break;                // cheap: often
                case "scan": go = dist > 8 && dist < 18 && roll; break;
                case "curtain": go = dist > 4 && dist < 14 && a.lastDamage < 0.6f && random() < 0.05f; break;  // cover when hit
                case "sprinkler": go = false; break;
                case "mine": go = dist < 10 && a.lastDamage < 1.5f && random() < 0.05f; break;     // a trap at your feet while under fire
                case "seeker": go = dist > 5 && dist < 15 && roll; break;
                default: go = dist > 4 && dist < 14 && roll; break;                                 // bomb, sticky, mist
            }
            if (go) bombCd = k == "burst" ? 1.5f + random() * 2 : 5 + random() * 6;
            return go;
        }
        // Sub use while painting: sprinklers onto open turf ahead, mines around the contested middle, beacons up front.
        readonly List<SubItem> _beacons = new List<SubItem>();
        bool paintSub()
        {
            var sub = a.sub ?? Config.SUBS["bomb"]; string k = sub.kind;
            { var SK = Kits.sub(k)?.bot; if (SK?.paint != null) return a.ink >= sub.inkCost + 4 && SK.paint(this); }
            if (a.ink < sub.inkCost + (sub.placed ? 4 : 20) || random() > (sub.placed ? 0.04f : 0.01f)) return false;
            var pads = G.level.spawnPads; float total = Vector3.Distance(pads[0], pads[1]);
            var ep = pads[1 - a.team];
            float progress = 1 - Mathf.Sqrt((a.pos.x - ep.x) * (a.pos.x - ep.x) + (a.pos.z - ep.z) * (a.pos.z - ep.z)) / total;
            bool go = false;
            if (k == "sprinkler")
            {
                var st = G.paint.regionStats(a.pos.x + Mathf.Sin(a.aimYaw) * 5, a.pos.y, a.pos.z + Mathf.Cos(a.aimYaw) * 5, 3, a.team, _stats);
                go = st.n > 0 && st.own < 0.4f;
            }
            else if (k == "mine") go = progress > 0.3f && progress < 0.7f;
            else if (k == "beacon")
            {
                go = progress > 0.4f;
                if (go) foreach (var b in G.subs.beaconsFor(a.team, _beacons)) if (Vector3.Distance(b.pos, a.pos) < 12) { go = false; break; }
            }
            if (go) bombCd = 8 + random() * 8;
            return go;
        }

        float rangeOf()
        {
            var w = a.weapon;
            return Config.weaponRange(w) * (CHARGES.Contains(w.kind) ? 0.9f : 1);
        }

        // what a kit weapon's bot hook sees (bots.js { a, w, dist, range, dt, it, move, target, … })
        BotCtx kctx(WeaponDef w, float dist, float range, float dt, Vector3 move, Actor tg, float inkFrac)
            => new BotCtx { a = a, w = w, dist = dist, range = range, dt = dt, it = a.intent, move = move, target = tg, inkFrac = inkFrac };

        // ============================================================================================ threats
        // Enemy devices that hunt a player — Waddle Bombs (walk after a foe along the nav graph at 4 m/s, give up after ~9 s /
        // 26 m) and Tide Torpedoes (lock on in mid-air, hover, then home in gently) — and enemy Brolly canopies (a held one
        // blocks shots; a launched one is a sliding wall that blocks players too). The kits list them through
        // Kits.SUB[k].threats(out) / Kits.MAIN[k].shields(out), scanned a few times a second.
        // A bot notices a device hunting it after its reaction time, then shoots it down when it has a line on it and the
        // reach (Waddle 30 hp, Torpedo 20; melee kits flick / swipe / cut / punch at one in their window), else evades: kites a
        // Waddle (a running kid outruns it), swims off through its own ink, steps out of a hovering Torpedo's sight,
        // sidesteps + hops a launched one late. A duel with an enemy player close by keeps priority unless the device is
        // about to arrive (the bot edges away from it meanwhile); far-off devices, or ones after someone else, are left alone.
        // Launched canopies across our way: steer round the nearer edge, or shoot apart one that boxes us in. A shield between
        // us and our target: flank round it, hold the shots unless it's nearly broken, lob a sub over it.
        void threatScan()
        {
            var L = _thrList;
            L.Clear(); _shl.Clear();
            foreach (var k in Kits.SUB.Values) k.threats(L);
            foreach (var k in Kits.MAIN.Values) k.shields(_shl);
            for (int i = _shl.Count - 1; i >= 0; i--) if (_shl[i].team == a.team) { _shl[i] = _shl[_shl.Count - 1]; _shl.RemoveAt(_shl.Count - 1); }
            ThreatInfo best = null; float bs = 0.2f;
            foreach (var d in L)
            {
                if (d.team == a.team || !d.live) continue;
                float s = threatScore(d);
                if (s > bs) { bs = s; best = d; }
            }
            L.Clear();
            var dead = new List<ThreatInfo>();
            foreach (var d in _thrMem.Keys) if (!d.live) dead.Add(d);
            foreach (var d in dead) _thrMem.Remove(d);
            // a new one is acted on only after our reaction time (it beeps / shows its lock ring: no sight line needed)
            if (best != null && !_thrMem.ContainsKey(best)) { _thrMem[best] = G.time + diff.reaction * (0.8f + random() * 0.7f) + 0.05f; THREAT_STATS.noticed++; }
            thrCand = best;
        }
        // how much a device matters to us right now: 0 = not at all; ~1.5+ outranks a duel in weapon range, 2.4+ one up close
        float threatScore(ThreatInfo d)
        {
            Vector3 dp = d.pos; float dx = dp.x - a.pos.x, dz = dp.z - a.pos.z, dh = Mathf.Sqrt(dx * dx + dz * dz);
            float dy = dp.y + d.aimY - (a.pos.y + 0.8f);
            if (dh > 18 || Mathf.Abs(dy) > 6) return 0;
            string st = d.state; bool mine = d.target == a;
            if (st == "fly")
            {
                // still in the air after the throw: a human sees it coming, so it's noticed now (the reaction time runs during its
                // flight) — a walker coming down within its sensing circle of us (it'll lock on), a flyer heading our way
                Vector3 v = d.vel;
                if (d.ground)
                {
                    float tt = (v.y + Mathf.Sqrt(Mathf.Max(0, v.y * v.y + 48 * (dp.y - a.pos.y)))) / 24;
                    float lx = dp.x + v.x * tt - a.pos.x, lz = dp.z + v.z * tt - a.pos.z;
                    return Mathf.Sqrt(lx * lx + lz * lz) < (d.senseRadius > 0 ? d.senseRadius : 7) + 1 ? 0.6f : 0;
                }
                return dh < (d.lockRange > 0 ? d.lockRange : 6.5f) + 3 && dx * v.x + dz * v.z < 0 ? 0.5f : 0;
            }
            if (!d.ground)
            {
                // flyers (Torpedo): hovering on us before its launch, or darting at us (or past us close enough to catch the burst)
                if (st == "unfold") return mine ? 3 : dh < d.radius ? 0.8f : 0;
                if (st != "launch") return 0;
                Vector3 v = d.vel; float sp = Mathf.Max(2, v.magnitude), d3 = Mathf.Sqrt(dh * dh + dy * dy);
                if (mine) return 4 / (0.25f + d3 / Mathf.Max(6, sp));   // (it's only just pushing off: it'll be quick)
                float tca = -(dx * v.x + dy * v.y + dz * v.z) / (sp * sp);
                if (tca > 0 && tca < 1.2f) { float mx = -dx - v.x * tca, my = -dy - v.y * tca, mz = -dz - v.z * tca; if (mx * mx + my * my + mz * mz < 1.6f) return 2; }
                return d3 < d.radius ? 0.8f : 0;
            }
            // walkers (Waddle): sitting on its sensing circle with us (nearly) inside, or walking after us / our way
            if (st == "sense") return dh < (d.senseRadius > 0 ? d.senseRadius : 7) + 0.6f ? 0.7f : 0;
            if (!d.locked) return 0;
            float eta = Mathf.Max(0, dh - (d.trigger > 0 ? d.trigger : 1.2f)) / (d.speed > 0 ? d.speed : 4);
            if (mine) return (4 / (0.5f + eta)) * (!float.IsNaN(d.left) && d.left < eta * 0.8f ? 0.3f : 1);   // (gives up before it gets here)
            if (dh < d.radius + 0.8f) return 1.7f;                                   // after a teammate, right by us: its blast gets us too
            Vector3 vv = d.vel; float vl = Mathf.Sqrt(vv.x * vv.x + vv.z * vv.z);
            if (dh < 9 && vl > 0.5f && -(dx * vv.x + dz * vv.z) / (dh * vl) > 0.8f) return 1.2f / (0.5f + eta);
            return 0;
        }
        // per frame, after the mode's own actions: deal with the device we've noticed (aim + trigger + move). Returns the aim
        // to hold (_thrAim, shared) or null to leave the frame as it is.
        Aim threatCtl(float dt, Intent it, ref Vector3 move)
        {
            if ((thrScanT -= dt) <= 0) { thrScanT = 0.2f + random() * 0.1f; threatScan(); }
            var d = thr;
            if (d != null && !d.live) { thrEnd(true); d = null; }
            var c = thrCand;
            if (c != null && c != d && c.live && G.time >= (_thrMem.TryGetValue(c, out var at) ? at : float.PositiveInfinity) && (d == null || threatScore(c) > threatScore(d) * 1.3f))
            {
                d = thr = c; thrAct = null; thrActs = 0; thrSide = 0; thrEvT = 0; thrAcqT = 0; thrLosT = 0;
                thrSignY = (random() < 0.5f ? -1 : 1) * (0.5f + random() * 0.5f); thrSignP = (random() - 0.5f) * 1.2f;
            }
            // a special that owns the body (a ride / transformation) plays out; others only get our footwork — the special
            // keeps the trigger (the riding specials arrive with specials.js)
            var sp = a.specialActive;
            if (d == null || (sp != null && sp.body) || a.climbing || _climbAim != null || a.superJumpState != null) return null;
            bool moveOnly = sp != null;
            float score = threatScore(d);
            if (score < 0.15f) { thrEnd(false); return null; }   // lost interest in us / walked off after someone else
            thrAcqT += dt;
            var w = a.weapon; string kind = w.kind; var wr = a.weaponRunner; var KB = Kits.main(kind)?.bot; bool melee = MELEE.Contains(kind) || (KB != null && KB.melee);
            // eye → the device's hit centre, led by the shot's flight time
            Vector3 dp = d.pos;
            float ex = a.pos.x, ey = a.pos.y + 1.1f, ez = a.pos.z, hx = dp.x, hy = dp.y + d.aimY, hz = dp.z;
            float dx = hx - ex, dy = hy - ey, dz = hz - ez;
            float dh = Mathf.Sqrt(dx * dx + dz * dz), d3 = Mathf.Sqrt(dh * dh + dy * dy); Vector3 v = d.vel;
            float spd = w.projSpeed > 0 ? w.projSpeed : w.speedMax > 0 ? w.speedMax : w.throwSpeed > 0 ? w.throwSpeed : 30;
            float lt = kind == "charger" ? 0 : Mathf.Min(0.5f, d3 / spd);
            dx += v.x * lt; dy += v.y * lt; dz += v.z * lt;
            // (a roller's flick at something on the ground: aim low — the flick's arc bottoms out there and lands 5–6.6 m out)
            float idealYaw = Mathf.Atan2(dx, dz), idealPitch = kind == "roller" && d.ground ? -0.3f : Mathf.Atan2(dy, Mathf.Sqrt(dx * dx + dz * dz));
            if ((thrLosT -= dt) <= 0) { thrLosT = 0.1f; thrLos = G.physics.los(new Vector3(ex, ey + 0.1f, ez), new Vector3(hx, hy + 0.05f, hz)); }
            // can we pop it? a line on it, in reach (melee: the window its flick / swipe / cut / punch lands in), ink to shoot
            float lo = 0, hi = rangeOf() * (d.ground ? 0.95f : 1);
            if (melee) meleeWindow(w, d, out lo, out hi);
            bool inkOk = a.ink > Mathf.Max(2.5f, Mathf.Max(w.inkPerShot, w.inkFull * 0.25f));
            bool canShoot = !moveOnly && d.shootable && thrLos && d3 >= lo && d3 <= hi && inkOk;
            float trig = d.trigger > 0 ? d.trigger : 1.2f;
            // a walker coming at us from just out of reach: stand and let it walk into it (running only buys time)
            bool waitIn = !canShoot && !moveOnly && d.ground && d.shootable && thrLos && inkOk && d3 > hi && d3 < hi + 4 && hi - trig > 2.5f;
            bool mine = d.target == a; float ttl = !d.ground && d.state == "launch" ? d3 / Mathf.Max(2, v.magnitude) : 9;
            float eta = d.ground ? Mathf.Max(0, dh - trig) / (d.speed > 0 ? d.speed : 4) : ttl;
            // a duel with an enemy player close by outranks a device that isn't about to arrive
            var T = target;
            if (T != null && mode == "fight" && seeTimer > 0)
            {
                float td = Mathf.Sqrt((T.pos.x - a.pos.x) * (T.pos.x - a.pos.x) + (T.pos.z - a.pos.z) * (T.pos.z - a.pos.z));
                float pd = td < 4 ? 2.4f : td < rangeOf() * 0.9f || td < Config.weaponRange(T.weapon) * 0.9f ? 1.5f : 0.5f;
                if (score < pd) { if (mine || dh < 4) thrDrift(d, ref move, 0.6f); return null; }
            }
            // far off / out of reach and not about to arrive: carry on, edging away from it
            bool urgent = d.ground ? eta < 2.2f || dh < (d.radius > 0 ? d.radius : 3) + 0.5f : d.state == "launch" || d.state == "unfold";
            if (!canShoot && !waitIn && !urgent) { thrDrift(d, ref move, 0.5f); return null; }
            string act = canShoot || waitIn ? "shoot" : "evade"; int bit = act == "shoot" ? 1 : 2;
            if ((thrActs & bit) == 0) { thrActs |= bit; if (bit == 1) THREAT_STATS.shoot++; else THREAT_STATS.evade++; }
            thrAct = act;
            bool dodge = false;
            if (canShoot || waitIn)
            {
                it.squid = false;
                a.fireFacing = Mathf.Max(a.fireFacing, 0.25f);   // square up to it (a flick / swipe leaves along the body)
                float rx = a.pos.x - dp.x, rz = a.pos.z - dp.z, rl = Mathf.Sqrt(rx * rx + rz * rz); if (rl == 0) rl = 1;
                // too late to count on the shot (a charge weapon a little sooner): sidestep its line, still firing if on it
                dodge = !d.ground && d.state == "launch" && ttl < (CHARGES.Contains(kind) ? 0.8f : 0.45f);
                if (dodge) thrSidestep(d, ref move, it, ttl < 0.3f);
                else if (waitIn || (d.ground && kind == "roller")) move = Vector3.zero;                    // (a flick lands a fixed way out)
                else if (d.ground && melee) move = new Vector3((rx / rl) * 0.35f, 0, (rz / rl) * 0.35f);     // let it walk into the swing
                else if (d.ground && dh < 4.5f) move = new Vector3(rx / rl, 0, rz / rl);                    // kite while shooting
                else move *= 0.25f;                                                                         // plant a moment for the shot
                if (CHARGES.Contains(kind) && wr.charging && !dodge) move *= 0.4f;
            }
            else thrEvade(d, dt, ref move, it, ttl);
            noProg = 0; bestD = float.PositiveInfinity;   // (off the route on purpose: not "stuck")
            // aim: the usual human error, a little tighter (it's small and we're looking straight at it)
            float e = diff.aimError * 0.7f, acq = Mathf.Exp(-thrAcqT / Mathf.Max(0.12f, diff.reaction * 0.9f));
            _thrAim.yaw = idealYaw + e * (0.7f * wander(t * 1.7f + ph1) + 2.2f * acq * thrSignY);
            _thrAim.pitch = idealPitch + e * 0.6f * (0.7f * wander(t * 2.1f + ph2) + 1.5f * acq * thrSignP);
            _thrAim.dist = Mathf.Max(1, d3);
            // trigger only with the actual aim on it (shots follow the bot's aim ray)
            float offY = angleDiff(aimYaw, idealYaw), offP = aimPitch - idealPitch, off = Mathf.Sqrt(offY * offY + offP * offP);
            float tol = Mathf.Max(0.035f, Mathf.Atan2(d.ground ? 0.24f : 0.28f, d3)) * (thrFiring ? 2.2f : 1.3f);
            if (moveOnly) { it.squid = false; return null; }   // (the special keeps its aim and trigger; it's used in kid form)
            it.fire = canShoot ? devTrigger(d3, off < tol, off < tol * 4, lo, hi) : CHARGES.Contains(kind) && wr.charging && !it.squid;
            if (dodge && CHARGES.Contains(kind) && wr.charging && wr.charge >= 0.12f) it.fire = false;   // let the charge go (roughly at it) and run
            thrFiring = it.fire && off < tol;
            if (it.fire) it.squid = false;
            it.sub = false; _bombAim = false;
            return _thrAim;
        }
        void thrEnd(bool gone)
        {
            if (gone && thrActs != 0) THREAT_STATS.gone++;
            bool acted = thrActs != 0;
            thr = null; thrAct = null; thrActs = 0; thrFiring = false;
            // back to what we were doing: re-plan the route from wherever the dodge left us
            if (acted && G.nav != null)
            {
                if (mode == "paint" && goal >= 0 && path != null) { var n = G.nav.nodes[goal]; pathTo(new Vector3(n.x, n.y, n.z), 0.3f); }
                else repath = 0;
            }
        }
        // keep doing what we're doing, but edge away from it
        void thrDrift(ThreatInfo d, ref Vector3 move, float k)
        {
            Vector3 dp = d.pos; float rx = a.pos.x - dp.x, rz = a.pos.z - dp.z, rl = Mathf.Sqrt(rx * rx + rz * rz); if (rl == 0) rl = 1;
            float x = move.x + (rx / rl) * k, z = move.z + (rz / rl) * k, l = Mathf.Sqrt(x * x + z * z);
            move = l > 1 ? new Vector3(x / l, 0, z / l) : new Vector3(x, 0, z);
        }
        // can't pop it: run (a kid outruns a Waddle; out of a hovering Torpedo's sight), swim through our own ink, or — a
        // Torpedo about to arrive — sidestep its line and hop
        void thrEvade(ThreatInfo d, float dt, ref Vector3 move, Intent it, float ttl)
        {
            if (!d.ground && d.state == "launch" && ttl < 0.55f) { thrSidestep(d, ref move, it, ttl < 0.35f); return; }
            thrEvT -= dt;
            if (thrEvT <= 0) { thrEvT = 0.45f + random() * 0.3f; thrEvYaw = pickEvade(d); }
            move = new Vector3(Mathf.Sin(thrEvYaw), 0, Mathf.Cos(thrEvYaw));
            bool swim = a.groundTeam == 1 && !squidWouldDrop(move);
            if (swim && (thrActs & 4) == 0) { thrActs |= 4; THREAT_STATS.swim++; }
            it.squid = swim;
            // running from a walker we can see: back off facing it, ready to shoot the moment it's in reach
            if (!swim && d.ground && thrLos) a.fireFacing = Mathf.Max(a.fireFacing, 0.25f);
        }
        void thrSidestep(ThreatInfo d, ref Vector3 move, Intent it, bool hop)
        {
            Vector3 v = d.vel, dp = d.pos; float vl = Mathf.Sqrt(v.x * v.x + v.z * v.z), rx = a.pos.x - dp.x, rz = a.pos.z - dp.z;
            float px, pz;
            if (vl > 0.3f) { px = -v.z / vl; pz = v.x / vl; } else { float rl = Mathf.Sqrt(rx * rx + rz * rz); if (rl == 0) rl = 1; px = -rz / rl; pz = rx / rl; }
            if (thrSide == 0)
            {
                // step to the side of its line we're already on, unless that's a wall or the sea
                bool ok(int sg) => dryLine(a.pos.x, a.pos.y, a.pos.z, a.pos.x + px * sg * 2, a.pos.z + pz * sg * 2) && fatLos(a.pos.x, a.pos.y, a.pos.z, a.pos.x + px * sg * 2, a.pos.y, a.pos.z + pz * sg * 2);
                int s = rx * px + rz * pz >= 0 ? 1 : -1;
                if (!ok(s) && ok(-s)) s = -s;
                thrSide = s;
                if ((thrActs & 8) == 0) { thrActs |= 8; THREAT_STATS.sidestep++; }
            }
            move = new Vector3(px * thrSide, 0, pz * thrSide);
            it.squid = false;
            if (hop && a.grounded && jumpCd <= 0 && !nearWater(a, 1.4f)) { it.jump = true; jumpCd = 0.9f; }
        }
        // a heading to run from it: away (flyers: across its line, out of its sight), clear of walls and water, onto our ink
        static readonly float[] EVADE_OFF = { 0, 0.5f, -0.5f, 1.0f, -1.0f, 1.5f, -1.5f, 2.1f, -2.1f };
        float pickEvade(ThreatInfo d)
        {
            Vector3 dp = d.pos; float x0 = a.pos.x, y0 = a.pos.y, z0 = a.pos.z, bas = Mathf.Atan2(x0 - dp.x, z0 - dp.z);
            var wp = path != null && pi < path.Count ? G.nav.nodes[path[pi]] : null;
            float best = bas, bs = float.NegativeInfinity;
            foreach (float off in EVADE_OFF)
            {
                float yw = bas + off, sx = Mathf.Sin(yw), sz = Mathf.Cos(yw), px = x0 + sx * 3.2f, pz = z0 + sz * 3.2f;
                if (G.nav.nearest(new Vector3(px, y0, pz), 1.0f) < 0) continue;   // never run off the walkable graph (no way back)
                if (!dryLine(x0, y0, z0, px, pz) || !fatLos(x0, y0, z0, px, y0, pz)) continue;
                float sc = Mathf.Cos(off) * (d.ground ? 2 : 1) + (d.ground ? 0 : Mathf.Abs(Mathf.Sin(off)) * 1.2f);
                var st = G.paint.regionStats(px, y0, pz, 1.3f, a.team, _stats);
                if (st.n > 0) sc += st.own * 1.5f;
                if (wp != null) { float wx = wp.x - x0, wz = wp.z - z0, wl = Mathf.Sqrt(wx * wx + wz * wz); if (wl == 0) wl = 1; sc += (0.4f * (wx * sx + wz * sz)) / wl; }
                if (!d.ground && !G.physics.los(dp, new Vector3(px, y0 + 1, pz))) sc += 2;
                sc += random() * 0.3f;
                if (sc > bs) { bs = sc; best = yw; }
            }
            return best;
        }
        // melee kits vs a device: the distance window their attack pops it in (a roller's flick arcs over anything close;
        // a brush's swipe globs fly low and short; the Cutlass's cut throws droplets; a punch flies straight)
        void meleeWindow(WeaponDef w, ThreatInfo d, out float lo, out float hi)
        {
            switch (w.kind)
            {
                // (ground, aimed low: the flick comes down 5–6.6 m out ~0.6 s after the press — a walker is ~2.3 m closer by then)
                case "roller": if (!d.ground) { lo = 1.5f; hi = 6; } else if (d.state == "walk") { lo = 7.3f; hi = 9; } else { lo = 5; hi = 6.6f; } return;
                case "brush": if (!d.ground) { lo = 0.8f; hi = 4.5f; } else if (d.state == "walk") { lo = 99; hi = 99; } else { lo = 0.8f; hi = 2.8f; } return;   // (a walker inside 2.8 m is too close to call: run)
                case "blade": lo = 0; hi = d.ground ? 4 : 5.5f; return;
                default: lo = 0; hi = Config.weaponRange(w) * 0.95f; return;
            }
        }

        // the trigger for a shot at a device dist m away (aimed: on it now; roughly: swinging onto it)
        bool devTrigger(float dist, bool aimed, bool roughly, float lo, float hi)
        {
            var w = a.weapon; var wr = a.weaponRunner; string k = w.kind; var KB = Kits.main(k)?.bot; bool inWin = dist >= lo && dist <= hi;
            if (MELEE.Contains(k) || (KB != null && KB.melee))
            {
                // press, release, press… (a roller's flick / a brush's swipe goes where the body faces: wait until it's squared up)
                bool faced = (k != "roller" && k != "brush") || Mathf.Abs(angleDiff(a.yaw, aimYaw)) < 0.3f;
                thrPulse = aimed && faced && inWin && !thrPulse; return thrPulse;
            }
            if (CHARGES.Contains(k) || (KB != null && KB.charges))
            {
                if (wr.burstT > 0) return false;                                     // a spinner stream runs on by itself
                // a short charge does it (a charger tap is 40+, bow ring 1 ~30, a short spinner burst)
                float rel = k == "charger" ? 0.12f : k == "spinner" ? 0.3f : k == "splatling" ? 0.35f : (w.ring1 > 0 ? w.ring1 : 0.4f) + 0.06f;
                if (wr.charging) return !(aimed && wr.charge >= rel);
                return roughly && inWin && !(wr.cooldown > 0);
            }
            return aimed && inWin;
        }

        // In a fight: an enemy shield (their held canopy, or a launched one) between us and the target soaks every shot —
        // flank round to the side it doesn't cover (sticky side, flipped off walls / water), save the shots unless it's nearly
        // broken or we can't get round, and lob a sub over it.
        void shieldFight(Actor t, float dist, ref Vector3 move, Intent it, float dt)
        {
            if (_shl.Count == 0) { blockT = 0; return; }
            float ex = a.pos.x, ey = a.pos.y + 1.1f, ez = a.pos.z;
            float tx = t.pos.x, ty = t.pos.y + t.smoothY + (t.form == "squid" ? 0.3f : 0.85f), tz = t.pos.z;
            ShieldInfo blk = null;
            foreach (var s in _shl)
            {
                if (!s.live) continue;
                Vector3 C = s.C, N = s.N;
                float p0 = (ex - C.x) * N.x + (ey - C.y) * N.y + (ez - C.z) * N.z, p1 = (tx - C.x) * N.x + (ty - C.y) * N.y + (tz - C.z) * N.z;
                if ((p0 > 0) == (p1 > 0) || p0 == p1) continue;
                float kk = p0 / (p0 - p1), qx = ex + (tx - ex) * kk - C.x, qy = ey + (ty - ey) * kk - C.y, qz = ez + (tz - ez) * kk - C.z;
                if (qx * qx + qy * qy + qz * qz > (s.R + 0.12f) * (s.R + 0.12f)) continue;
                blk = s; break;
            }
            if (blk == null) { if (blockT > 0) { blockT = Mathf.Max(0, blockT - dt * 3); if (blockT == 0) flankSide = 0; } return; }
            blockT += dt;
            // flank: across our line to the target, toward the side of the shield we're already off-centre on
            Vector3 BN = blk.N, BC = blk.C;
            float sx = -BN.z, sz = BN.x, hl = Mathf.Max(0.3f, Mathf.Sqrt((tx - ex) * (tx - ex) + (tz - ez) * (tz - ez))), nx = (tx - ex) / hl, nz = (tz - ez) / hl;
            if (flankSide == 0) { flankSide = (ex - BC.x) * sx + (ez - BC.z) * sz >= 0 ? 1 : -1; flankT = 0; }
            float lx = -nz, lz = nx;
            if ((lx * sx + lz * sz) * flankSide < 0) { lx = -lx; lz = -lz; }
            if ((flankT -= dt) <= 0)
            {
                flankT = 0.5f;
                bool ok = dryLine(ex, a.pos.y, ez, ex + lx * 1.8f, ez + lz * 1.8f) && fatLos(ex, a.pos.y, ez, ex + lx * 1.8f, a.pos.y, ez + lz * 1.8f);
                if (!ok) { flankSide = -flankSide; lx = -lx; lz = -lz; }
            }
            float inward = dist > 6 ? 0.45f : dist < 2.5f ? -0.35f : 0.1f, mx = lx + nx * inward, mz = lz + nz * inward, ml = Mathf.Sqrt(mx * mx + mz * mz);
            if (ml == 0) ml = 1;
            move = new Vector3(mx / ml, 0, mz / ml);
            // shots: only into a shield we'd break in about a second (a fast shooter on a worn one), or when we can't get round
            // it; charge weapons keep their charge for the opening
            var w = a.weapon; var wr = a.weaponRunner;
            float dps = w.damage > 0 && w.fireInterval > 0 && !CHARGES.Contains(w.kind) ? w.damage / w.fireInterval : 120;
            bool through = blk.hp <= dps * 1.3f || blockT > 2.5f || (!blk.held && blk.left < 0.5f) || (canRef == blk && canSide == 2);
            if (!through && blockT > 0.15f)
            {
                if (CHARGES.Contains(w.kind) || (Kits.main(w.kind)?.bot?.charges ?? false)) it.fire = it.fire || wr.charging;
                else if (it.fire) it.fire = false;
            }
            // a thrown sub goes over / round it
            var sub = a.sub ?? Config.SUBS["bomb"];
            if (blockT > 0.5f && bombCd <= 0 && THROWN.Contains(sub.kind) && a.ink >= sub.inkCost + 8 && dist > 3 && dist < 13 && !(Kits.sub(sub.kind)?.blocked(a, sub) ?? false) && random() < dt * 2.5f)
            {
                it.sub = true; _bombAim = true; bombCd = 5 + random() * 4;
            }
        }

        // Enemy launched canopies (sliding walls that hold enemy players back) across our way: steer round the nearer edge
        // (then across behind it); boxed in (walls / water on both sides) or still not round after 4 s → shoot it apart.
        // Returns the aim to hold while breaking one (only when `free`: no device being dealt with), else null.
        Aim canopyCtl(float dt, Intent it, ref Vector3 move, bool free)
        {
            var S = _shl;
            if (S.Count == 0 || a.climbing || a.superJumpState != null || a.specialActive != null) { canRef = null; return null; }
            float ml = Mathf.Sqrt(move.x * move.x + move.z * move.z);
            ShieldInfo hit = null; float hAlong = 0, hSide = 1, hW = 1;
            foreach (var s in S)
            {
                if (!s.blocksActors || !s.live) continue;
                Vector3 N = s.N, sp = s.pos; float tx = -N.z, tz = N.x, px = a.pos.x - sp.x, pz = a.pos.z - sp.z, dy = a.pos.y - sp.y;
                if (dy < -1.2f || dy > 1.6f) continue;
                float along = px * tx + pz * tz, across = px * N.x + pz * N.z - s.planeOff, W = s.halfW + PLAYER.radius * 0.6f + 0.3f;
                if (Mathf.Abs(across) > 3 || Mathf.Abs(along) > W + 2.5f) continue;
                float side = across >= 0 ? 1 : -1;
                // it's sliding at us (we're in front of it, in its lane): step out of its way even if we're standing still
                bool coming = side > 0 && across < 2.2f && Mathf.Abs(along) < W && s.speed > 0.5f;
                if (!coming)
                {
                    if (ml <= 0.05f) continue;
                    float mx = move.x / ml, mz = move.z / ml, mN = mx * N.x + mz * N.z, mT = mx * tx + mz * tz;
                    if (mN * side > -0.2f) continue;                               // moving along it or away from it
                    float tCross = -across / mN;                                   // metres of travel to its plane
                    if (tCross > 2.5f || Mathf.Abs(along + mT * tCross) > W) continue;   // far yet / we'd clear its edge anyway
                }
                hit = s; hAlong = along; hSide = side; hW = W; break;
            }
            if (hit != null && ml <= 0.05f) ml = 1;
            if (hit == null) { canRef = null; _canBroke = false; return null; }
            if (canRef != hit) { canRef = hit; canSide = 0; canT = 0; _canBroke = false; }
            canT += dt;
            Vector3 HN = hit.N, HP = hit.pos; float htx = -HN.z, htz = HN.x;
            // a spot beside its edge: `off` m out from its plane on our side (negative: past it)
            float edgeX(int sg, float off) => HP.x + HN.x * (hit.planeOff + hSide * off) + htx * sg * (hW + 0.55f);
            float edgeZ(int sg, float off) => HP.z + HN.z * (hit.planeOff + hSide * off) + htz * sg * (hW + 0.55f);
            if (canSide == 0)
            {
                bool ok(int sg)
                {
                    float x1 = edgeX(sg, 0.9f), z1 = edgeZ(sg, 0.9f), x2 = edgeX(sg, -1.4f), z2 = edgeZ(sg, -1.4f), y = a.pos.y;
                    return dryLine(a.pos.x, y, a.pos.z, x1, z1) && fatLos(a.pos.x, y, a.pos.z, x1, y, z1) && dryLine(x1, y, z1, x2, z2) && fatLos(x1, y, z1, x2, y, z2);
                }
                int pref = hAlong >= 0 ? 1 : -1;
                canSide = ok(pref) ? pref : ok(-pref) ? -pref : 2;
            }
            if (canSide != 2 && canT > 4) canSide = 2;
            if (canSide == 2)
            {
                if (!free) return null;
                _canBroke = true;
                Vector3 C = hit.C; float dx = C.x - a.pos.x, dy = C.y - (a.pos.y + 1.1f), dz = C.z - a.pos.z, dh = Mathf.Sqrt(dx * dx + dz * dz);
                _thrAim.yaw = Mathf.Atan2(dx, dz); _thrAim.pitch = Mathf.Atan2(dy, dh); _thrAim.dist = Mathf.Max(1, Mathf.Sqrt(dh * dh + dy * dy));
                float offY = angleDiff(aimYaw, _thrAim.yaw), offP = aimPitch - _thrAim.pitch, off = Mathf.Sqrt(offY * offY + offP * offP);
                bool melee = MELEE.Contains(a.weapon.kind) || (Kits.main(a.weapon.kind)?.bot?.melee ?? false);
                it.fire = a.ink > 3 && devTrigger(_thrAim.dist, off < 0.3f, off < 0.7f, 0, melee ? 3.5f : rangeOf());
                if (it.fire) it.squid = false;
                move = new Vector3(HN.x * hSide * 0.3f, 0, HN.z * hSide * 0.3f);
                noProg = 0; bestD = float.PositiveInfinity;
                return _thrAim;
            }
            _canBroke = false;
            // beside its face → out to the edge; clear of the edge → across behind it
            float o2 = Mathf.Abs(hAlong) < hW + 0.1f ? 0.9f : -1.4f;
            float gx = edgeX(canSide, o2) - a.pos.x, gz = edgeZ(canSide, o2) - a.pos.z, gl = Mathf.Sqrt(gx * gx + gz * gz);
            if (gl > 0.05f) move = new Vector3((gx / gl) * ml, 0, (gz / gl) * ml);
            noProg = 0; bestD = float.PositiveInfinity;
            return null;
        }

        void perceive()
        {
            Vector3 eye = a.pos + Vector3.up * 1.3f;
            Actor best = null; float bd = float.PositiveInfinity;
            float aw = diff.awareness;
            var bias = Kits.main(a.weapon.kind)?.bot?.targetBias;   // kit weapons may weigh targets (e.g. the Cutlass: busy ones)
            foreach (var e in G.actors)
            {
                if (e.team == a.team || !e.alive) continue;
                float d = Vector3.Distance(e.pos, a.pos);
                if (d > aw) continue;
                bool swimming = e.anim.form == "swim";
                float hs = Mathf.Sqrt(e.vel.x * e.vel.x + e.vel.z * e.vel.z);
                if (swimming && d > 3 && !(hs > 7 && d < 9)) continue;
                if (!G.physics.los(eye, e.pos + Vector3.up * (e.form == "squid" ? 0.3f : 1.0f))) continue;
                float score = d - (e == target ? 4 : 0) + (bias != null ? bias(this, e, d) : 0);
                if (score < bd) { bd = score; best = e; }
            }
            if (best != null)
            {
                if (best != target)
                {
                    target = best; react = diff.reaction * (0.7f + random() * 0.6f); repath = 0;
                    // first look lands a little off (over- or under-shoot) and settles — like a human flick
                    acqT = 0; acqSignY = (random() < 0.5f ? -1 : 1) * (0.5f + random() * 0.5f); acqSignP = (random() - 0.5f) * 1.2f;
                }
                seeTimer = 1.2f;
                lostTimer = 0;
            }
            else
            {
                seeTimer -= 0.2f;
                if (target != null)
                {
                    lostTimer += 0.2f;
                    if (lostTimer > 2.5f || Vector3.Distance(target.pos, a.pos) > aw + 6) target = null;
                }
            }
            react -= 0.2f;
        }

        bool pathTo(Vector3 pos, float maxUp = 0.8f)
        {
            var nav = G.nav;
            int s = nav.nearest(a.pos, 1.2f, true);
            // standing at the foot of a step: don't start the route from the ledge above (we can't get up there from here)
            if (s >= 0 && a.grounded && nav.nodes[s].y - a.pos.y > 0.5f) { int s2 = nav.nearest(a.pos, 0.45f, true); if (s2 >= 0) s = s2; }
            int g = nav.nearest(pos, maxUp);
            repath = 0.8f + random() * 0.4f;
            if (s < 0 || g < 0) { path = null; return false; }
            var p = nav.path(s, g, a.team, 6000, t < noClimbUntil);
            if (p == null) { path = null; return false; }
            path = p; pi = Mathf.Min(1, p.Count - 1); goal = g; bestD = float.PositiveInfinity; noProg = 0;
            return true;
        }

        void pickPaintGoal()
        {
            var nav = G.nav;
            int best = -1; float bs = float.NegativeInfinity;
            var enemyPad = G.level.spawnPads[1 - a.team];
            var ownPad = G.level.spawnPads[a.team];
            float total = Vector3.Distance(ownPad, enemyPad);
            var mates = G.actors.Where(o => o != a && o.team == a.team && o.bot != null).ToList();
            for (int i = 0; i < 28; i++)
            {
                int id = nav.validIds[(int)(random() * nav.validIds.Count)];
                var n = nav.nodes[id];
                if (n.zone >= 0 || n.wet == 2) continue;
                float d = Mathf.Sqrt((n.x - a.pos.x) * (n.x - a.pos.x) + (n.z - a.pos.z) * (n.z - a.pos.z));
                if (d > 38) continue;
                // value of the spot and of the patch around it: unclaimed turf counts, enemy ink counts more
                var near = G.paint.regionStats(n.x, n.y, n.z, 3, a.team, _stats);
                if (near.n == 0) continue;
                float vNear = near.empty + near.enemy * 1.4f;
                var wide = G.paint.regionStats(n.x, n.y, n.z, 6.5f, a.team, _stats);
                float value = vNear * 0.55f + (wide.empty + wide.enemy * 1.4f) * 0.45f;
                float progress = 1 - Mathf.Sqrt((n.x - enemyPad.x) * (n.x - enemyPad.x) + (n.z - enemyPad.z) * (n.z - enemyPad.z)) / total;   // 0 at own base → 1 at enemy base
                float score = value * 16 - d * 0.14f + clamp(progress, 0, 0.8f) * 2.5f + random() * 1.5f - (n.wet != 0 ? 1.5f : 0);
                if (CHARGES.Contains(a.weapon.kind)) score += clamp(n.y, 0, 5) * 1.6f;   // long range: high perches see (and paint) more
                if (value < 0.15f) score -= 8;   // already ours: only if nothing better turns up
                foreach (var m in mates) if (m.bot.goal >= 0) { var g = nav.nodes[m.bot.goal]; if (Mathf.Sqrt((g.x - n.x) * (g.x - n.x) + (g.z - n.z) * (g.z - n.z)) < 8) score -= 5; }
                if (score > bs) { bs = score; best = id; }
            }
            goalTimer = 4 + random() * 3;
            if (best < 0) return;
            pathTo(nav.nodes[best].pos, 0.3f);
        }

        void pickRefill()
        {
            // search nearby for own ink
            Vector3? bestP = null; float bd = float.PositiveInfinity;
            for (int i = 0; i < 14; i++)
            {
                float ang = random() * Mathf.PI * 2, r = 1 + random() * 7;
                Vector3 v = new Vector3(a.pos.x + Mathf.Cos(ang) * r, a.pos.y, a.pos.z + Mathf.Sin(ang) * r);
                var st = G.paint.regionStats(v.x, v.y, v.z, 1.2f, a.team, _stats);
                if (st.n > 0 && st.own > 0.6f && r < bd) { bd = r; bestP = v; }
            }
            // nothing close: look further out, and failing that head back toward our own spawn (always our colour)
            for (int i = 0; i < 16 && bestP == null; i++)
            {
                float ang = random() * Mathf.PI * 2, r = 8 + random() * 14;
                Vector3 v = new Vector3(a.pos.x + Mathf.Cos(ang) * r, a.pos.y, a.pos.z + Mathf.Sin(ang) * r);
                var st = G.paint.regionStats(v.x, v.y, v.z, 1.5f, a.team, _stats);
                if (st.n > 0 && st.own > 0.6f) bestP = v;
            }
            if (bestP == null || !pathTo(bestP.Value, 0.4f)) pathTo(G.level.spawnPads[a.team], 1.2f);
            repath = 1.2f;
        }

        // Escape move: steer a fixed random heading for a moment (hopping if on the ground), then re-plan from wherever
        // that leaves us. Used when perched on an edge in mid-air or when repeated re-plans make no progress.
        void wiggle(float tt)
        {
            wiggleT = tt; wiggleYaw = random() * Mathf.PI * 2;
            path = null; goalTimer = 0; repath = 0;
            if (a.grounded) _needJump = true;
        }

        void unstick(float dt, ref Vector3 move)
        {
            strikeT -= dt;
            if (strikeT <= 0) strikes = 0;
            // resting on a ledge corner: not grounded, not falling, not moving → the waypoint logic can't fix that
            bool still = !a.grounded && Mathf.Abs(a.vel.y) < 0.6f && Mathf.Sqrt(a.vel.x * a.vel.x + a.vel.z * a.vel.z) < 0.4f && a.superJumpState == null;
            airStill = still ? airStill + dt : 0;
            if (airStill > 0.4f && wiggleT <= 0) wiggle(0.6f);
            if (wiggleT > 0)
            {
                wiggleT -= dt;
                move = new Vector3(Mathf.Sin(wiggleYaw), 0, Mathf.Cos(wiggleYaw));
                noProg = 0; bestD = float.PositiveInfinity;   // the escape isn't "no progress" toward the waypoint
            }
        }

        // No route, and no nav graph under us: walk to the nearest graph node in sight, then plan again
        void backOnNav(float dt, ref Vector3 move)
        {
            var N = G.nav;
            if (N == null || !a.grounded || a.climbing || a.superJumpState != null) { navBack = null; return; }
            navBackT -= dt;
            if (navBackT <= 0)
            {
                navBackT = 0.6f; navBack = null;
                if (N.nearest(a.pos, 1.2f, true) >= 0) return;                  // on the graph: the normal re-plan works
                var cands = new List<(float d2, NavNode q)>();
                foreach (int id in N.validIds)
                {
                    var q = N.nodes[id]; float d2 = (q.x - a.pos.x) * (q.x - a.pos.x) + (q.z - a.pos.z) * (q.z - a.pos.z);
                    if (d2 < 100 && Mathf.Abs(q.y - a.pos.y) < 1.2f && q.wet == 0) cands.Add((d2, q));
                }
                cands.Sort((x, y) => x.d2.CompareTo(y.d2));
                for (int k = 0; k < Mathf.Min(12, cands.Count); k++)
                {
                    var q = cands[k].q;
                    if (dryLine(a.pos.x, a.pos.y, a.pos.z, q.x, q.z) && fatLos(a.pos.x, a.pos.y, a.pos.z, q.x, q.y, q.z)) { navBack = q; break; }
                }
                if (navBack == null && cands.Count > 0) navBack = cands[0].q;
            }
            var nb = navBack;
            if (nb != null) { float dx = nb.x - a.pos.x, dz = nb.z - a.pos.z, l = Mathf.Sqrt(dx * dx + dz * dz); if (l > 0.3f) move = new Vector3(dx / l, 0, dz / l); }
        }

        public float pathRemaining()
        {
            if (path == null) return 0;
            var n = G.nav.nodes[path[path.Count - 1]];
            return Mathf.Sqrt((n.x - a.pos.x) * (n.x - a.pos.x) + (n.z - a.pos.z) * (n.z - a.pos.z));
        }

        // body-width line of sight at knee height (centre + both shoulders) so bots never cut corners they can't fit past
        public bool fatLos(float ax, float ay, float az, float bx, float by, float bz)
        {
            float dx = bx - ax, dz = bz - az;
            float l = Mathf.Sqrt(dx * dx + dz * dz); if (l == 0) l = 1;
            float px = (-dz / l) * 0.34f, pz = (dx / l) * 0.34f;
            foreach (int o in new[] { 0, 1, -1 })
            {
                Vector3 v = new Vector3(ax + px * o, ay + 0.45f, az + pz * o), v2 = new Vector3(bx + px * o, by + 0.45f, bz + pz * o);
                // a walking line: grates and rails block it (shots see through them, legs don't — so not physics.los)
                Vector3 v3 = v2 - v; float len = v3.magnitude;
                if (len < 1e-4f) continue;
                if (G.physics.raycast(v, v3 / len, len - 0.05f, _walkHit, false).hit) return false;
            }
            return true;
        }

        Vector3 steer(float dt)
        {
            var nav = G.nav;
            Vector3 o = Vector3.zero;
            if (path == null || pi >= path.Count) return o;
            // advance waypoints we've reached (generous vertically when dropping down)
            while (pi < path.Count)
            {
                var n0 = nav.nodes[path[pi]];
                float dx = n0.x - a.pos.x, dz = n0.z - a.pos.z, dy = n0.y - a.pos.y;
                if (dx * dx + dz * dz < 0.6f * 0.6f && dy < 0.9f && dy > -1.8f) { pi++; bestD = float.PositiveInfinity; noProg = 0; }
                else break;
            }
            if (pi >= path.Count) return o;
            var cur = nav.nodes[path[pi]];
            float hd = Mathf.Sqrt((cur.x - a.pos.x) * (cur.x - a.pos.x) + (cur.z - a.pos.z) * (cur.z - a.pos.z));
            // waypoint is above us and we can't get there from here (slipped off a ledge, got pushed): replan now
            string et0 = nav.edgeType(path[Mathf.Max(0, pi - 1)], path[pi]);
            if (a.grounded && cur.y - a.pos.y > 0.9f && hd < 1.2f && et0 != "jump" && et0 != "climb")
            {
                path = null; repath = 0; goalTimer = 0;
                // the replan keeps landing on the same unreachable ledge: shake loose instead of re-planning every frame
                if (t - _ledgeT > 2) _ledgeN = 0;
                _ledgeT = t;
                if (++_ledgeN >= 4) { _ledgeN = 0; wiggle(0.8f); }
                return o;
            }
            // waypoint is below us and we're standing over it (drop edge): keep going the way the path runs to step off
            if (cur.y - a.pos.y < -0.9f && hd < 0.8f && pi > 0)
            {
                var p = nav.nodes[path[pi - 1]];
                o = new Vector3(cur.x - p.x, 0, cur.z - p.z);
                float ll = o.magnitude;
                if (ll > 0.01f) return o / ll;
            }
            // look ahead: aim at the furthest waypoint we can walk to in a straight line on this level (re-chosen every
            // ~0.1 s or when the waypoint advances — the probes are the costly part, the heading still updates every frame)
            int ti = pi;
            _laT -= dt;
            if (_laT > 0 && _laPi == pi && _laPath == path && _laTi < path.Count) ti = _laTi;
            else
            {
                for (int k = pi + 1; k < Mathf.Min(path.Count, pi + 7); k++)
                {
                    var nk = nav.nodes[path[k]];
                    if (Mathf.Abs(nk.y - a.pos.y) > 0.4f) break;
                    if (nav.edgeType(path[k - 1], path[k]) != "walk") break;
                    if (!fatLos(a.pos.x, a.pos.y, a.pos.z, nk.x, nk.y, nk.z)) break;
                    if (!dryLine(a.pos.x, a.pos.y, a.pos.z, nk.x, nk.z)) break;   // never cut a corner across water
                    ti = k;
                }
                _laT = 0.1f; _laPi = pi; _laPath = path; _laTi = ti;
            }
            var n = nav.nodes[path[ti]];
            o = new Vector3(n.x - a.pos.x, 0, n.z - a.pos.z);
            float l = o.magnitude;
            if (l > 0.001f) o /= l;
            // jump edges
            if (pi > 0)
            {
                string et = nav.edgeType(path[pi - 1], path[pi]);
                if (et == "jump" && cur.y - a.pos.y > 0.4f && hd < 1.6f) _needJump = true;
            }
            // separation from teammates (only sideways relative to travel, so it never stalls forward progress)
            foreach (var oa in G.actors)
            {
                if (oa == a || !oa.alive) continue;
                float dx = a.pos.x - oa.pos.x, dz = a.pos.z - oa.pos.z, d2 = dx * dx + dz * dz;
                if (d2 < 1.4f * 1.4f && d2 > 1e-4f)
                {
                    float d = Mathf.Sqrt(d2), k = (1.4f - d) * 0.7f;
                    float side = (dx * -o.z + dz * o.x) >= 0 ? 1 : -1;
                    float ox = o.x, oz = o.z;
                    o.x = ox - oz * side * k; o.z = oz + ox * side * k;
                }
            }
            float l2 = o.magnitude;
            if (l2 > 1) o /= l2;
            // progress tracking toward the current waypoint (used by the stuck recovery)
            if (hd < bestD - 0.2f) { bestD = hd; noProg = 0; } else noProg += dt;
            return o;
        }
    }
}
