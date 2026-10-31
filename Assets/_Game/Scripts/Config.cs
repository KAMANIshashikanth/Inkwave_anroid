// Port of src/config.js — shared tuning + content definitions. Every module reads from here; nothing here reads
// anything else. Numbers are copied verbatim from INKWAVE (MIT, © 2026 Jayden Davis).
//
// The JS defs are object literals with whatever fields their kind needs. Here each family is ONE class holding the
// union of those fields (unused ones stay 0), filled with object initialisers so a def reads like its JS original.
// The only name clash in the union is the bow's tiered burstRadius ([ring, full]) vs the blaster's single one: the
// bow's lives in `burstRadiusTier`.
using System.Collections.Generic;
using System.Linq;

namespace Inkwave
{
    public static class Config
    {
        public const string GAME_TITLE = "COLOR SPLASH";
        public const string GAME_SUBTITLE = "Turf Riot";
        public const string VERSION = "0.1.0";

        // Team ink palettes. Team 0 ("Alpha") is always the local player's team; a palette is picked per match.
        public static readonly TeamPalette[] TEAM_PALETTES =
        {
            new TeamPalette("tangerine-cobalt", "#ff8a14", "#2f5bff", "Tangerine", "Cobalt"),
            new TeamPalette("bubblegum-mint", "#ff3f9e", "#18d48c", "Bubblegum", "Mint"),
            new TeamPalette("lemon-grape", "#f2e312", "#8a3cff", "Lemon", "Grape"),
            new TeamPalette("aqua-cherry", "#10d2e6", "#ff4150", "Aqua", "Cherry"),
            new TeamPalette("lime-magenta", "#a6f01a", "#e02cd8", "Lime", "Magenta"),
        };
        // Used instead when settings.colorblind is on (yellow vs blue is safe for all common CVD types).
        public static readonly TeamPalette COLORBLIND_PALETTE = new TeamPalette("cb-yellow-blue", "#ffd21a", "#2a52ff", "Sun", "Sea");

        public static readonly string[] TEAM_NAMES = { "Alpha", "Bravo" };

        // ---- Weapons ----
        // stats.* are 0..1 display bars for the loadout screen.
        public static readonly Dictionary<string, WeaponDef> WEAPONS = new Dictionary<string, WeaponDef>
        {
            ["shooter"] = new WeaponDef
            {
                id = "shooter", name = "Spritzer", kind = "shooter", @class = "Shooter",
                blurb = "Rapid-fire all-rounder. Sprays a steady stream of ink blobs.",
                stats = new WeaponStats(0.5f, 0.45f, 0.85f, 0.7f, 0.6f),
                fireInterval = 0.1f, damage = 36, inkPerShot = 0.95f,
                projSpeed = 34, straightTime = 0.13f, range = 12.5f,
                spreadGround = 5.5f, spreadAir = 11,   // degrees
                impactRadius = 0.85f, trailRadius = 0.44f, trailEvery = 1.05f,
                moveSpeedFiring = 4.6f,
                special = "zooka", specialCost = 190, sub = "bomb",
            },
            ["roller"] = new WeaponDef
            {
                id = "roller", name = "Swell Roller", kind = "roller", @class = "Roller",
                blurb = "Roll out wide stripes of turf. Flick for a crushing splash.",
                stats = new WeaponStats(0.35f, 0.95f, 0.3f, 0.55f, 0.95f),
                rollSpeed = 4.4f, rollWidth = 1.9f, rollInkPerMeter = 1.1f, rollDamage = 140,
                flickInterval = 0.62f, flickWindup = 0.22f, flickInk = 9, flickDrops = 9,
                flickDamageNear = 125, flickDamageFar = 30, flickSpeed = 17, flickSpreadDeg = 34,
                impactRadius = 1.0f,
                moveSpeedFiring = 4.4f,
                special = "kraken", specialCost = 170, sub = "sticky",
            },
            ["charger"] = new WeaponDef
            {
                id = "charger", name = "Glint Charger", kind = "charger", @class = "Charger",
                blurb = "Hold to charge, release for a long piercing line. Full charge splats.",
                stats = new WeaponStats(1.0f, 1.0f, 0.25f, 0.35f, 0.45f),
                chargeTime = 1.0f, rangeMin = 11, rangeMax = 27, damageMin = 40, damageMax = 160,
                inkFull = 18, lineSplatEvery = 1.2f, lineRadius = 0.55f, impactRadius = 1.2f,
                moveSpeedFiring = 1.8f,
                special = "sonar", specialCost = 180, sub = "mine",
            },
            ["blaster"] = new WeaponDef
            {
                id = "blaster", name = "Popper Blaster", kind = "blaster", @class = "Blaster",
                blurb = "Slow shots that burst mid-air. Direct hits splat instantly.",
                stats = new WeaponStats(0.55f, 0.9f, 0.3f, 0.6f, 0.5f),
                fireInterval = 0.78f, directDamage = 125, splashDamageMax = 70, splashDamageMin = 30,
                splashRadius = 2.6f, inkPerShot = 9, projSpeed = 23, range = 10.5f,
                impactRadius = 1.5f, burstRadius = 1.9f,
                moveSpeedFiring = 4.0f,
                special = "jetpack", specialCost = 180, sub = "burst",
            },
            // `anim` = the pose family the character animates with (kind otherwise)
            ["bucket"] = new WeaponDef
            {
                id = "bucket", name = "Bilge Bucket", kind = "bucket", @class = "Bucket", anim = "blaster",
                blurb = "Hurl a wave of ink in an arc: aim higher to throw farther, lob it over cover. Full damage at any distance; two hits splat.",
                stats = new WeaponStats(0.5f, 0.8f, 0.35f, 0.6f, 0.7f),
                fireInterval = 0.6f, windup = 0.12f, inkPerShot = 6,
                throwSpeed = 15.5f, lob = 0.3f, blobs = 5, gravity = 20, drag = 0.25f, reach = 9.5f,
                damage = 55, impactRadius = 1.05f,       // no distance falloff; only targets below you take less (belowFalloff per m, capped)
                belowFalloff = 0.12f, belowFalloffMax = 0.35f,
                moveSpeedFiring = 4.2f,
                special = "blower", specialCost = 180, sub = "sprinkler",
            },
            ["spinner"] = new WeaponDef
            {
                id = "spinner", name = "Squall Spinner", kind = "spinner", @class = "Spinner", anim = "charger",
                blurb = "Spin up, then unleash a stream of ink. A full spin reaches farthest and fires longest.",
                stats = new WeaponStats(0.88f, 0.55f, 1.0f, 0.4f, 0.7f),
                chargeTime = 1.0f, inkFull = 24, burstMin = 0.35f, burstMax = 1.8f, fireInterval = 0.05f,
                damage = 26, speedMin = 25, speedMax = 40, straightMin = 0.09f, straightMax = 0.24f, rangeMin = 10, rangeMax = 21,
                spreadGround = 2.8f, spreadAir = 6, impactRadius = 0.72f, trailRadius = 0.38f, trailEvery = 1.25f,
                moveSpeedCharging = 3.3f, moveSpeedFiring = 3.0f,
                special = "crab", specialCost = 200, sub = "curtain",
            },
            ["twins"] = new WeaponDef
            {
                id = "twins", name = "Twinfire Pistols", kind = "twins", @class = "Pistols", anim = "shooter",
                blurb = "Paired pistols. Jump while firing to dodge-roll; stand still after a roll for rapid, longer-range fire. Walking returns to dual mode.",
                stats = new WeaponStats(0.4f, 0.4f, 0.95f, 0.9f, 0.55f),
                fireInterval = 0.085f, damage = 30, inkPerShot = 0.85f, projSpeed = 31, straightTime = 0.11f, range = 11,
                spreadGround = 6, spreadAir = 11, impactRadius = 0.78f, trailRadius = 0.4f, trailEvery = 1.1f,
                moveSpeedFiring = 5.2f,
                rollDist = 3.4f, rollTime = 0.3f, rollInk = 7, rollCharges = 2, rollLockout = 2.6f, rollReset = 1.4f,
                turretInterval = 0.058f, turretSpread = 1.6f, turretProjSpeed = 36, turretStraight = 0.16f, turretRange = 13.5f,
                special = "bubbler", specialCost = 180, sub = "seeker",
            },
            ["brush"] = new WeaponDef
            {
                id = "brush", name = "Swish Brush", kind = "brush", @class = "Brush", anim = "roller",
                blurb = "Dash along the ground leaving a thin trail, or swipe side to side to flick a spray of small globs.",
                stats = new WeaponStats(0.2f, 0.35f, 1.0f, 1.0f, 0.5f),
                brushSpeed = 7.4f, brushWidth = 0.95f, brushInkPerMeter = 0.32f, brushDamage = 30, brushHitCd = 0.35f,
                swipeInterval = 0.16f, swipeInk = 2.2f, swipeDrops = 5, swipeSpeed = 12.5f, swipeSpreadDeg = 40,
                swipeDamageNear = 26, swipeDamageFar = 12, impactRadius = 0.7f,
                moveSpeedFiring = 5.8f,
                special = "stamp", specialCost = 170, sub = "mist",
            },
            // ---- kit weapons: each kind lives in Game/Kits/<kind> (registered in the kit registry); the numbers here are its tuning
            ["brolly"] = new WeaponDef
            {
                id = "brolly", name = "Canopy Brolly", kind = "brolly", @class = "Brolly", anim = "shooter",
                blurb = "A shotgun spray of ink pellets. Hold to open the canopy: a shield for you and your team. Keep holding to launch it as a rolling wall.",
                stats = new WeaponStats(0.35f, 0.7f, 0.5f, 0.6f, 0.55f),
                range = 7,
                // pellets: a narrow cone of `pellets` per shot; each does pelletDamage out to falloffStart (m), easing to
                // pelletDamageFar at falloffEnd — a near-full blast (10–11 pellets) is 80–88, so two shots splat up close
                fireInterval = 0.45f, inkPerShot = 5.5f, pellets = 11, pelletDamage = 8, pelletDamageFar = 2.5f, falloffStart = 2.8f, falloffEnd = 6.5f,
                projSpeed = 25, straightTime = 0.1f, pelletGrav = 30, pelletDrag = 2, pelletLife = 0.6f, pelletSize = 0.1f, pelletPaint = 0.5f,
                spreadDeg = 8, spreadAir = 10,
                moveSpeedFiring = 4.6f, moveSpeedShield = 3.1f,
                // canopy: hold after the shot to open it; kept open launchHold s it launches as a sliding wall.
                // It blocks enemy shots / beams / ink, soaks bomb blasts, breaks at 0 hp and regrows regrowTime s after a break
                // or a launch; a damaged canopy mends at canopyRegen hp/s once folded and untouched for canopyRegenDelay s
                canopyHp = 450, openDelay = 0.12f, openTime = 0.14f, closeTime = 0.1f, launchHold = 0.9f, regrowTime = 5,
                canopyRegen = 60, canopyRegenDelay = 2,
                launchSpeed = 2.6f, launchLife = 6, launchPaint = 0.8f,
                special = "bubbler", specialCost = 180, sub = "tracer",
            },
            ["bow"] = new WeaponDef
            {
                id = "bow", name = "Tideline Bow", kind = "bow", @class = "Bow", anim = "charger",
                blurb = "Draw to fill two rings, then loose three arrows at once: a flat fan on the ground, an upright one in the air. Past the first ring the arrows stick where they land and burst.",
                stats = new WeaponStats(0.85f, 0.6f, 0.45f, 0.5f, 0.6f),
                // draw time to full; ring 1 is this share of it. Charge < ring1 = a tap: short, weak, no lodge
                chargeTime = 1.0f, ring1 = 0.45f,
                inkFull = 16, inkMin = 0.15f,                                   // ink per shot = inkFull × max(inkMin, charge)
                range = 25,                                                     // full-draw reach where a level shot lands (aim assist, bots, reticle)
                flightTap = new[] { 5f, 7.5f }, flightRing = new[] { 12.5f, 16.5f }, flightFull = 21,   // straight flight per tier (m), then the arrow dives
                speedTap = 32, speedRing = new[] { 42f, 48f }, speedFull = 58,   // arrow speed (m/s): fast, with a light arc
                straight = 0.05f, grav = 6, diveGrav = 140, diveDrag = 6,       // past its flight an arrow noses down into the ground (~3-4 m)
                damageTap = new[] { 14f, 20f }, damageRing = new[] { 30f, 38f }, damageFull = 55,   // direct hit, centre arrow
                sideMul = 0.8f, sideFull = 45,                                  // side arrows (full draw: centre + side = a splat)
                fanTap = 8, fanRing = 6, fanFull = 4.5f,                        // degrees between neighbouring arrows
                fuseRing = 0.7f, fuseFull = 0.55f,                              // lodged arrow → burst (s)
                burstRadiusTier = new[] { 1.35f, 1.8f }, burstInner = 0.6f,     // [ring, full] (m); full damage inside burstInner
                burstDamage = new[] { 30f, 45f }, burstEdge = new[] { 12f, 15f },
                paintTap = 0.55f, paintStick = 0.42f, burstPaint = new[] { 1.2f, 1.55f }, trailEvery = 2.4f, trailRadius = 0.32f,
                cooldown = 0.22f, moveSpeedDrawing = 2.6f, moveSpeedFiring = 4.2f,
                special = "strike", specialCost = 190, sub = "waddle",
            },
            ["blade"] = new WeaponDef
            {
                id = "blade", name = "Brine Cutlass", kind = "blade", @class = "Cutlass", anim = "blade",
                blurb = "Tap for quick slashes that fling a crescent of ink. Hold to charge an overhead cut that sends a long ink wave. The blade itself hits hard: a charged cut splats in one hit.",
                stats = new WeaponStats(0.35f, 0.85f, 0.75f, 0.85f, 0.55f),
                // range = the quick cut's droplet reach (aim assist, bots)
                range = 6.5f, projSpeed = 17,
                // quick cut: alternating horizontal slashes, a crescent of droplets (one hit per victim per cut) + the blade itself
                tapInterval = 0.27f, tapInk = 3.2f, tapDrops = 7, tapSpreadDeg = 66, tapSpeed = 17.5f, tapStraight = 0.07f,
                tapDamageNear = 40, tapDamageFar = 20, tapDropRadius = 0.64f, tapMelee = 35,
                // charged cut: hold past the swing to charge; release at full → overhead cut + lunge + a straight ink wave
                chargeDelay = 0.14f, chargeTime = 0.62f, chargeStore = 1.0f, heavyInk = 9, heavyInterval = 0.46f,
                heavyMelee = 130, lungeDist = 1.05f, lungeTime = 0.13f, strokeRadius = 0.6f,
                waveDamage = 70, waveDamageFar = 50, waveSpeed = 26, waveRange = 12, waveWidth = 0.7f, waveHeight = 1.7f,
                wavePaintEvery = 0.7f, wavePaintRadius = 0.62f, waveEndRadius = 1.05f,
                // the blade's own hitbox: an arc in front of the kid, `reach` m (plus the victim's body radius)
                reach = 1.5f, meleeArcDeg = 150, heavyArcDeg = 110,
                moveSpeedFiring = 5.6f, moveSpeedCharging = 4.4f,
                special = "zipcaster", specialCost = 180, sub = "shaker",
            },
            ["mitts"] = new WeaponDef
            {
                id = "mitts", name = "Sponge Mitts", kind = "mitts", @class = "Mitts", anim = "shooter",
                blurb = "Punch out ink fists that burst a few metres ahead — with your gloves up, the sponge soaks part of any hit from the front. Hold fire and press jump to charge a long leap that splashes where you land, and sticks you to walls (holding on drains ink).",
                stats = new WeaponStats(0.3f, 0.75f, 0.9f, 0.9f, 0.5f),
                range = 5.3f,              // reach for bots / aim assist: the fist's flight + its burst
                // punches: alternating gloves; each throws an ink fist that flies fistRange and bursts (or bursts on what it hits)
                punchInterval = 0.12f, inkPerPunch = 1.6f, punchSpread = 2.5f,   // three fists splat: ≈ 0.24 s + the flight
                fistSpeed = 28, fistRange = 4.6f, fistSize = 0.2f,
                // sponge guard: while punching (and a beat after), a hit from within guardArc° of where you face takes × guardArmor
                guardArmor = 0.5f, guardArc = 70, guardAfter = 0.25f,
                botApproach = 1,          // bots: swim / weave / punch their way in; 0 = the plain melee walk (A/B tests)
                punchDamage = 38,         // direct: three fists splat
                splashRadius = 1.4f, splashMax = 22, splashMin = 10, fistPaint = 0.95f,
                moveSpeedFiring = 5.4f,
                // charged leap: hold fire, press jump (hold to charge, release to leap along the aim; aim higher = higher arc)
                leapChargeTime = 0.45f, leapInkMin = 9, leapInkMax = 15, moveSpeedCharging = 1.1f,
                leapArmor = 0.35f, leapArmorGrace = 0.15f,  // damage taken × leapArmor while in the air on a leap (and for leapArmorGrace s after landing)
                leapSpeedMin = 10.9f, leapSpeedMax = 18.6f,   // launch speed at 0 / full charge (≈ 4.5 m / 13 m on flat ground)
                leapAngle = 40, leapAngleMin = 18, leapAngleMax = 60, leapPitchK = 0.55f, leapVyMax = 13,
                landRadius = 2.8f, landDamageMax = 100, landDamageMin = 35, landPaint = 2.5f,   // a dead-centre landing splats
                landInvuln = 0.5f,         // s untouchable after a leap lands (ground or wall): the spawn-protection flicker shows it
                gloveRadius = 1.1f, gloveDamage = 40,   // the gloves themselves: anyone the kid lands on takes this on top of the splash
                // wall cling (a leap into a wall sticks there): drains ink; jump or an empty tank lets go
                clingDrain = 8,
                special = "slam", specialCost = 180, sub = "boomerang",
            },
            // upstream's retired dualies / slosher / splatling are not ported: WEAPON_SUCCESSOR maps old ids forward.
        };

        public static readonly string[] WEAPON_ORDER = { "shooter", "twins", "brolly", "blaster", "spinner", "charger", "bow", "roller", "brush", "blade", "mitts", "bucket" };
        // retired weapon → the one that replaced it (saved loadouts, links)
        public static readonly Dictionary<string, string> WEAPON_SUCCESSOR = new Dictionary<string, string> { ["dualies"] = "twins", ["splatling"] = "spinner", ["slosher"] = "bucket" };

        // effective reach (m) for aim assist, the in-range reticle and bots
        public static float weaponRange(WeaponDef w)
        {
            switch (w.kind)
            {
                case "charger": case "spinner": return w.rangeMax;
                case "roller": return 6;
                case "brush": return 5;
                case "bucket": return w.reach;
                default: return w.range > 0 ? w.range : 12;
            }
        }

        // Sub weapons. Each weapon has a default (`sub` on the weapon); the loadout can swap it for any of these.
        public static readonly Dictionary<string, SubDef> SUBS = new Dictionary<string, SubDef>
        {
            ["bomb"] = new SubDef
            {
                id = "bomb", name = "Splat Bomb", kind = "bomb", blurb = "Bounces, arms when it lands, then bursts. Splats anyone close.",
                inkCost = 70, throwSpeed = 13.5f, fuse = 0.95f, radius = 3.1f, damageMax = 180, damageMin = 35, paintRadius = 2.7f,
            },
            ["sticky"] = new SubDef
            {
                id = "sticky", name = "Cling Charge", kind = "sticky", blurb = "Sticks to whatever it hits (floors, walls, ceilings) and blows after a moment with a wide blast.",
                inkCost = 70, throwSpeed = 13.5f, fuse = 2.4f, radius = 4.0f, damageMax = 180, damageMin = 35, paintRadius = 3.3f,
            },
            ["burst"] = new SubDef
            {
                id = "burst", name = "Pop Pellet", kind = "burst", blurb = "Pops on impact. Cheap enough to throw twice: two direct hits or three near misses splat.",
                inkCost = 40, throwSpeed = 16, radius = 2.1f, directDamage = 60, splashDamage = 35, paintRadius = 1.8f,
            },
            ["seeker"] = new SubDef
            {
                id = "seeker", name = "Skitter Bomb", kind = "seeker", blurb = "Scuttles after the nearest foe, laying a swimmable ink trail, and bursts when it reaches them. It turns wide: sidestep it late.",
                // turnRate: rad/s (1.75 ≈ 100°/s: a ~3.6 m turning circle at full speed); commitDist: dashes straight once this
                // close and lined up; creep: how much it slows turning onto a slow / standing target. speed 6.3: only just faster
                // than a run (6), so a foe who sidesteps and keeps running gets away
                inkCost = 65, throwSpeed = 9, speed = 6.3f, seekRange = 15, life = 4.5f, trailRadius = 0.6f, triggerDist = 1.2f, turnRate = 1.75f, commitDist = 2.5f, creep = 0.6f,
                radius = 2.8f, damageMax = 180, damageMin = 35, paintRadius = 2.4f,
            },
            ["scan"] = new SubDef
            {
                id = "scan", name = "Echo Orb", kind = "scan", blurb = "Bursts into a sensing cloud. Foes it touches are tracked for your whole team. No damage.",
                inkCost = 55, throwSpeed = 14, fuse = 1.1f, radius = 4.2f, cloudTime = 0.9f, trackTime = 8,
            },
            ["curtain"] = new SubDef
            {
                id = "curtain", name = "Drip Curtain", kind = "curtain", blurb = "Drops a wall of falling ink that stops enemy players and shots. It fades over time, faster when shot.",
                inkCost = 55, throwSpeed = 9, width = 3.4f, height = 2.7f, hp = 170, decay = 19, shotMul = 0.5f,
            },
            ["sprinkler"] = new SubDef
            {
                id = "sprinkler", name = "Twirl Sprinkler", kind = "sprinkler", blurb = "Sticks to any surface and sprays ink around it in pulses, until it is shot or you get splatted.",
                inkCost = 60, throwSpeed = 12, hp = 70, pulse = 0.3f, drops = 6, sprayRadius = 3.2f, sprayFade = 12, dropDamage = 8,
            },
            ["mine"] = new SubDef
            {
                id = "mine", name = "Lurk Mine", kind = "mine", blurb = "Planted at your feet and hidden in your ink. Foes who come close are hit and tracked. Two at a time.",
                inkCost = 55, placed = true, max = 2, triggerRadius = 2.1f, armTime = 0.9f, delay = 0.35f, radius = 2.6f, damage = 45, trackTime = 8, paintRadius = 2.0f,
            },
            ["beacon"] = new SubDef
            {
                id = "beacon", name = "Hop Beacon", kind = "beacon", blurb = "A super-jump point for your team. Place up to three; each takes two jumps.",
                inkCost = 70, placed = true, max = 3, uses = 2, hp = 50,
            },
            ["mist"] = new SubDef
            {
                id = "mist", name = "Murk Bomb", kind = "mist", blurb = "Releases a poison mist that slows foes and drains their ink. A direct hit keeps them poisoned until the mist fades.",
                inkCost = 60, throwSpeed = 13.5f, fuse = 1.0f, radius = 3.3f, mistTime = 4.5f, slow = 0.55f, inkDrain = 12,
            },
            // ---- kit subs
            ["shaker"] = new SubDef
            {
                id = "shaker", name = "Shaker Bomb", kind = "shaker", blurb = "Hold to shake it up (mash jump, keep moving): up to three blasts that hop it forward.",
                inkCost = 60, throwSpeed = 13,
                // charge (0..1; 2 blasts at ½, 3 at full): plain holding fills it in chargeTime; running adds up to moveBoost × that
                // rate; each jump press adds jumpBoost, each stick / mouse waggle shakeBoost; never faster than maxRate per second
                chargeTime = 2.7f, moveBoost = 0.9f, jumpBoost = 0.075f, shakeBoost = 0.05f, maxRate = 1.6f,
                fuse = 0.5f, gap = 0.42f, hopSpeed = 4.2f, hopUp = 4.6f,          // first landing → first blast, blast → blast, each blast's hop
                radius = 2.3f, damageMax = 110, damageMin = 30, paintRadius = 2.0f, // per blast: a smaller Splat Bomb
                trailEvery = 0.38f, trailRadius = 0.3f,                            // thin ink trail sprayed while it travels
            },
            ["waddle"] = new SubDef
            {
                id = "waddle", name = "Waddle Bomb", kind = "waddle", blurb = "Waddles after foes it senses near where it lands, noisily. Blows up where it lands if nobody is near.",
                inkCost = 65, throwSpeed = 12,
                senseRadius = 7.5f, senseUp = 4, fuse = 1.1f,                       // sensing circle on landing; nobody inside → blows after fuse
                speed = 4.0f, turnRate = 8, life = 9, maxTravel = 26, triggerDist = 1.2f,   // tracks its foe (nav paths, hops up steps)
                radius = 3.0f, damageMax = 180, damageMin = 35, paintRadius = 2.6f, hp = 30,  // Splat Bomb blast; shoot-able (30 hp)
            },
            ["torpedo"] = new SubDef
            {
                id = "torpedo", name = "Tide Torpedo", kind = "torpedo", blurb = "Locks onto a foe in mid-air, then darts at them and bursts into droplets. One out at a time.",
                inkCost = 65, throwSpeed = 13.5f, feetPaint = 1.25f,
                // flight → lock (a foe within lockRange, in sight, after armTime) → unfold (hovers) → launch (swims at them, homing)
                armTime = 0.12f, lockRange = 6.5f, flyLife = 2.5f, unfoldTime = 0.5f, launchSpeed0 = 2.2f, launchSpeed = 9.5f, launchAccel = 8, turnRate = 1.9f, launchLife = 2.8f,
                hp = 20,                                                             // shot down by 20 damage (any form)
                radius = 2.4f, coreRadius = 1.2f, damageMax = 60, damageMin = 35,     // the burst (both forms): 60 up close, 35 at the rim
                paintRadius = 3.6f, fallbackPaint = 1.5f,                             // locked burst paints a bit more than a Cling Charge (3.3)
                drops = 10, dropDamage = 12, dropHits = 3, dropPaint = 0.65f,         // locked burst only: 60 + 3 × 12 = 96 max
            },
            ["tracer"] = new SubDef
            {
                id = "tracer", name = "Tracer Bolt", kind = "tracer", blurb = "A fast bolt fired a little low that skims and ricochets, puddling ink on every bounce. Hits and its trail mark foes for your team.",
                inkCost = 40, speed = 40, range = 32, pitchDown = 9, floorExit = 0.3f, maxBounces = 12, size = 0.16f,
                puddleRadius = 1.35f, directDamage = 35, directMark = 9, trailLife = 1.0f, trailRadius = 0.42f, trailDamage = 22, trailMark = 3,
            },
            ["boomerang"] = new SubDef
            {
                id = "boomerang", name = "Whirl Boomerang", kind = "boomerang", blurb = "Spins out, hovers and shreds, whirls back round you, then bursts. Hit a foe and it bursts on them like a Splat Bomb. One out at a time.",
                inkCost = 60, throwSpeed = 16,           // throwSpeed: only the bots' lob maths read it (the throw itself is flat)
                range = 9, outTime = 0.6f,               // flat along the aim, easing to a stop ~9 m out (stops early at a wall)
                hover = 1.75f, hoverRadius = 1.35f, tickRate = 8, tickDamage = 9, hoverPaint = 1.0f,   // shreds ≈ 72 dmg/s around it
                returnSpeed = 17, orbit = 2.5f, orbitRadius = 1.5f, orbitSpin = 7, orbitDamage = 6, orbitHitCd = 0.3f,
                radius = 2.4f, damageMax = 100, damageMin = 30, paintRadius = 2.2f,                    // the burst beside you at the end
                contact = 0.3f, hitFuse = 0.6f, hitRadius = 3.1f, hitDamageMax = 180, hitDamageMin = 35, hitPaintRadius = 2.7f,   // hit a foe in flight: Splat Bomb
            },
        };
        public static readonly string[] SUB_ORDER = { "bomb", "sticky", "burst", "shaker", "seeker", "waddle", "torpedo", "tracer", "boomerang", "scan", "curtain", "sprinkler", "mine", "beacon", "mist" };

        // Specials. Every special refills your ink tank when it starts. `duration` = how long a timed special lasts (s).
        public static readonly Dictionary<string, SpecialDef> SPECIALS = new Dictionary<string, SpecialDef>
        {
            ["slam"] = new SpecialDef { id = "slam", name = "Tidal Slam", blurb = "Leap up and slam down in a huge ink shockwave.", rise = 0.55f, hang = 0.25f, radius = 5.2f, killRadius = 3.2f, damageMax = 180, damageMin = 55 },
            ["storm"] = new SpecialDef { id = "storm", name = "Ink Tempest", blurb = "Hurl a rain cloud that soaks the turf below.", duration = 6.5f, radius = 3.4f, dps = 34, throwSpeed = 16, driftSpeed = 1.1f },
            // Bomb Barrages: throw one kind of bomb as fast as you like (no ink) while the main weapon still works. Each variant
            // is its own special (kind 'barrage'); `gap` = the shortest time between throws.
            ["barrage"] = new SpecialDef { id = "barrage", kind = "barrage", bomb = "bomb", name = "Splat Bomb Barrage", blurb = "Throw Splat Bombs as fast as you like for a few seconds — no ink needed. Your main weapon still works.", duration = 6.5f, gap = 0.3f },
            ["barrage_sticky"] = new SpecialDef { id = "barrage_sticky", kind = "barrage", bomb = "sticky", name = "Cling Charge Barrage", blurb = "Stick Cling Charges to every wall in sight — no ink needed. Your main weapon still works.", duration = 6.5f, gap = 0.4f },
            ["barrage_burst"] = new SpecialDef { id = "barrage_burst", kind = "barrage", bomb = "burst", name = "Pop Pellet Barrage", blurb = "Pelt foes with rapid-fire Pop Pellets — no ink needed. Your main weapon still works.", duration = 6.5f, gap = 0.2f },
            ["barrage_seeker"] = new SpecialDef { id = "barrage_seeker", kind = "barrage", bomb = "seeker", name = "Skitter Bomb Barrage", blurb = "Send a pack of Skitter Bombs chasing foes, each inking a trail — no ink needed. Your main weapon still works.", duration = 6.5f, gap = 0.5f },
            ["barrage_mist"] = new SpecialDef { id = "barrage_mist", kind = "barrage", bomb = "mist", name = "Murk Bomb Barrage", blurb = "Smother an area in Murk Bomb mist — no ink needed. Your main weapon still works.", duration = 6.5f, gap = 0.45f },
            // force field: hits become knockback (reduced); touching teammates shares it
            ["bubbler"] = new SpecialDef { id = "bubbler", name = "Bubble Guard", blurb = "A force field that turns every hit into a shove instead of damage. Touch teammates to share it.", duration = 6.5f, radius = 1.3f, knockPerDamage = 0.045f, knockMax = 6.5f, shareRange = 1.5f },
            // reveals every enemy to your team (screen + map); they are slowed and burn ink faster
            ["sonar"] = new SpecialDef { id = "sonar", name = "Deep Sonar", blurb = "Reveals every enemy to your team on screen and on the map. Revealed foes move slower and burn through ink faster.", duration = 8, slow = 0.8f, inkMul = 1.4f, cast = 0.6f },
            // pick a spot on the map; a missile lands there as a swirling ink vortex
            ["strike"] = new SpecialDef { id = "strike", name = "Vortex Strike", blurb = "Pick a spot on the map and launch a missile. It lands as a huge swirling vortex of ink.", aimTime = 7, flight = 2.2f, radius = 5.5f, duration = 4.5f, dps = 62, pull = 1.6f },
            // bazooka: tall narrow twisters in quick succession, long range, one-shot splats
            ["zooka"] = new SpecialDef { id = "zooka", name = "Twister Zooka", blurb = "A bazooka that fires tall twisters of ink in quick succession — splats foes at long range.", duration = 6, interval = 1.0f, speed = 34, range = 44, damage = 180, height = 2.8f, radius = 0.55f, paintEvery = 0.7f, paintRadius = 0.95f },
            // speaker: after a short charge, a sound wave in the aimed direction through walls; splats anything in it
            ["wail"] = new SpecialDef { id = "wail", name = "Howl Box", blurb = "Hold up a huge speaker, aim it and click: it blasts a sound wave that goes through walls and splats anything in its path.", charge = 1.3f, blast = 3.2f, radius = 1.5f, range = 72, dps = 260, holdTime = 6, holdSpeed = 3.2f },
            // invincible kraken: fast through any ink, splats with a jump attack
            ["kraken"] = new SpecialDef { id = "kraken", name = "Kraken", blurb = "Turn into an invincible kraken. Race through any ink (even the enemy's) and splat foes with a jump attack.", duration = 7, speed = 7.2f, hopVel = 10.5f, attackVel = 8.5f, attackFwd = 6.5f, radius = 2.3f, damage = 200, knockPerDamage = 0.02f, cooldown = 0.65f, paintRadius = 1.15f },
            // up to three giant bubbles: they wall off an area and burst into a deadly blast when your team shoots them
            ["blower"] = new SpecialDef { id = "blower", name = "Bubble Blower", blurb = "Blow up to three giant bubbles that wall off an area. Shoot them (you or your team) to set off a huge ink blast.", duration = 9, max = 3, inflate = 1.0f, rMin = 0.9f, rMax = 2.1f, drift = 1.4f, life = 9, popDamage = 55, blastMul = 1.9f, damageMax = 180, damageMin = 45 },
            // jetpack: hover and fire blaster-like shots; super jump back to the take-off point when it runs out
            ["jetpack"] = new SpecialDef { id = "jetpack", name = "Ink Jet", blurb = "Hover over the stage firing powerful blasts — jump for a boost. When it runs out you super jump back to where you took off (marked for everyone to see).", duration = 7, height = 3.8f, speed = 5.5f, accel = 11, boost = 9, boostGap = 0.9f, interval = 0.55f, projSpeed = 30, range = 34, directDamage = 125, splashMax = 70, splashMin = 30, splashRadius = 2.4f, paintRadius = 1.5f },
            // giant stamp: slam repeatedly while advancing, jump attack, or throw it (ends the special)
            ["stamp"] = new SpecialDef
            {
                id = "stamp", name = "Mega Stamp", blurb = "Charge forward smashing with a giant stamp: each swing deflects attacks from the front and smashes bombs before they go off, but it turns slowly and is open from the sides and back. Swing mid-air for a flip that reaches further and hits behind you too. Throw it (sub) as a long-range blast — that ends the special.",
                duration = 7.5f, interval = 0.42f, reach = 1.6f, radius = 1.55f, damage = 200, lunge = 2.6f, moveSpeed = 7.2f, throwSpeed = 25, throwRadius = 3.2f, throwDamageMax = 200, throwDamageMin = 60,
                // the body turns slowly while charging (quicker once stopped); sideways steps are short — charge, stop, turn, charge
                turnRate = 1.7f, turnRateStill = 6.0f, strafe = 0.35f,
                // each swing guards the front: attacks from within deflectArc° of the facing do nothing, shots are knocked away and
                // bombs (thrown or planted) within bombClearR in front are smashed before they go off
                guardTime = 0.45f, deflectArc = 70, deflectR = 2.6f, bombClearR = 3.2f,
                // swing in the air: one flip — hits behind you as the stamp goes over, then a longer-reaching smash in front
                flipTime = 0.5f, flipReach = 2.7f, flipRadius = 1.9f, flipBackReach = 1.2f, flipBackRadius = 1.6f,
            },
            // a ball of ink held overhead charges over time (faster with "Yeah!" cheers); throw it once full for a huge blast
            ["booyah"] = new SpecialDef { id = "booyah", name = "Cheer Orb", blurb = "Hold up a ball of ink that charges over time, then throw it for a huge blast. Teammates' \"Yeah!\" cheers charge it faster and top up their own special.", charge = 4.5f, cheer = 0.12f, cheerSpecial = 12, autoThrow = 2.5f, moveSpeed = 1.8f, throwSpeed = 19.6f, fuse = 1.5f, radius = 8.4f, killRadius = 4.6f, damageMax = 220, damageMin = 60 },
            // grapple: the sub button fires a tether to latch onto surfaces and zip over; super jump back when it ends
            ["zipcaster"] = new SpecialDef
            {
                id = "zipcaster", name = "Zipline", blurb = "Cloaked in a mysterious aura, your sub becomes a grapple: latch onto walls from afar and zip over, main weapon in hand. You super jump back when it ends (marked for everyone to see).",
                duration = 8.5f, range = 21, speed = 22, hang = 1.4f, cooldown = 0.35f,
                // body impact at the end of every zip (or on an enemy met mid-zip): a mini explosion
                impactDirect = 100, impactSplash = 30, impactDirectR = 1.0f, impactRadius = 2.4f,
            },
            // ride a crab tank: gatling (main), mortar (sub), roll into an armoured ball (swim); the tank has HP, the rider is
            // exposed from above and behind
            ["crab"] = new SpecialDef
            {
                id = "crab", name = "Crab Rig", blurb = "Ride a crab tank: gatling on fire, mortar on sub, roll into an armoured ball with swim. The tank can be shot down, and you're exposed from above and behind.",
                duration = 9, hp = 460, speed = 3.0f, rollSpeed = 9.5f, turnRate = 1.5f, gunInterval = 0.075f, gunDamage = 18, gunSpeed = 36, gunRange = 22, gunSpread = 3.5f,
                cannonGap = 1.15f, cannonSpeed = 17, cannonRadius = 3.0f, cannonDamageMax = 150, cannonDamageMin = 40, rollArmor = 0.35f,
            },
        };
        public static readonly string[] SPECIAL_ORDER = { "slam", "storm", "barrage", "barrage_sticky", "barrage_burst", "barrage_seeker", "barrage_mist", "bubbler", "sonar", "strike", "zooka", "wail", "kraken", "blower", "jetpack", "stamp", "booyah", "zipcaster", "crab" };

        public static readonly DifficultyDef[] DIFFICULTY_LIST =
        {
            // aimOmega / aimTurn: bot aim spring stiffness (rad/s) and turn-rate cap (rad/s) — see bots
            new DifficultyDef { id = "easy", name = "Chill", reaction = 0.55f, aimError = 0.11f, fireDiscipline = 0.55f, awareness = 16, aimOmega = 9, aimTurn = 7 },
            new DifficultyDef { id = "normal", name = "Fresh", reaction = 0.32f, aimError = 0.06f, fireDiscipline = 0.8f, awareness = 21, aimOmega = 13, aimTurn = 10 },
            new DifficultyDef { id = "hard", name = "Fierce", reaction = 0.17f, aimError = 0.03f, fireDiscipline = 0.95f, awareness = 26, aimOmega = 18, aimTurn = 14 },
        };
        public static readonly Dictionary<string, DifficultyDef> DIFFICULTY = DIFFICULTY_LIST.ToDictionary(d => d.id);

        // Every stage can be played by day or at dusk: `times` maps the time of day to an environment theme (`theme` is the
        // stage's day look, kept for older callers). Pick with mapTheme(map, time).
        public static readonly string[] TIMES = { "day", "dusk" };
        public static string mapTheme(MapDef map, string time = "day")
        {
            if (map == null) return "day";
            string t = time == "dusk" ? map.timeDusk : map.timeDay;
            return t ?? map.theme ?? "day";
        }

        // the Cargo stage is online-only upstream and the online mode is not ported, so it is not listed here
        public static readonly MapDef[] MAPS =
        {
            new MapDef { id = "tidewater", name = "Tidewater Plaza", blurb = "A Victorian seaside square: fight round the Jubilee clock tower, under the colonnade and along the promenade.", theme = "day", timeDay = "day", timeDusk = "sunset" },
            new MapDef { id = "kelpline", name = "Kelpline Terminal", blurb = "A container terminal at shift change: a gantry crane straddles the pier between two moored ships. Mind the water.", theme = "day", timeDay = "day", timeDusk = "sunset" },
            new MapDef { id = "halyard", name = "Halyard Marina", blurb = "Floating docks, a tug on blocks and a car ferry moored across the middle. Mind the water.", theme = "golden", timeDay = "golden", timeDusk = "sunset" },
            new MapDef { id = "saltpan", name = "Saltpan Basin", blurb = "An open salt works: sunken pans and boardwalks round a wind pump, with the shed roof and salt heap as high ground.", theme = "day", timeDay = "day", timeDusk = "sunset" },
            new MapDef { id = "crossmarket", name = "Crossroads Market", blurb = "Narrow shop streets and an iron gallery close in on the glass Market Hall, where the No. 3 tram waits under the clock.", theme = "golden", timeDay = "golden", timeDusk = "sunset" },
            new MapDef { id = "lockgate", name = "Lockgate Canals", blurb = "Drained locks through a brick warehouse district: the canal splits the map, so hold the bridge and the gates.", theme = "day", timeDay = "day", timeDusk = "sunset" },
            new MapDef { id = "terraces", name = "Terrace Heights", blurb = "A whitewashed hill village: hold the terraces, fight for the stairs and drop in on the Piazzetta.", theme = "golden", timeDay = "golden", timeDusk = "sunset" },
        };
        public static MapDef mapById(string id) => MAPS.FirstOrDefault(m => m.id == id);

        public static readonly string[] BOT_NAMES =
        {
            "Squiddo", "Blotch", "Marlo", "Inky Vee", "Pip", "Coral", "Riptide", "Nori", "Suki", "Zest",
            "Kelp", "Drip", "Tako", "Sprinkle", "Bubbles", "Moxie", "Juno", "Wasabi", "Fizz", "Loop",
        };

        // The renderer's view of the quality tier. Touch devices default to `low` for its MEMORY footprint (small atlas,
        // small shadow map) — that part is load-bearing on a phone — but sharpen the image knobs (pixel ratio, MSAA) only.
        public static QualityDef rendererQuality(Settings settings, bool touch)
        {
            var q = QUALITY.TryGetValue(settings?.quality ?? "high", out var found) ? found : QUALITY["high"];
            if (!touch) return q;
            var t = q.Clone();
            t.pixelRatio = UnityEngine.Mathf.Max(q.pixelRatio, TOUCH.minPixelRatio);
            t.msaa = UnityEngine.Mathf.Max(q.msaa, TOUCH.minMsaa);
            return t;
        }

        // Quality presets consumed by the renderer + fx. pixelRatio = cap on the screen's density (on a phone it is the
        // render scale against the panel's native resolution, see Core/Renderer).
        public static readonly Dictionary<string, QualityDef> QUALITY = new Dictionary<string, QualityDef>
        {
            ["low"] = new QualityDef { id = "low", pixelRatio = 0.75f, shadowSize = 1024, msaa = 0, bloom = false, ao = false, paintAtlas = 2048, particles = 0.4f },
            ["medium"] = new QualityDef { id = "medium", pixelRatio = 1.0f, shadowSize = 2048, msaa = 2, bloom = true, ao = false, paintAtlas = 2048, particles = 0.7f },
            ["high"] = new QualityDef { id = "high", pixelRatio = 1.5f, shadowSize = 4096, msaa = 4, bloom = true, ao = true, paintAtlas = 4096, particles = 1.0f },
            ["ultra"] = new QualityDef { id = "ultra", pixelRatio = 2.0f, shadowSize = 4096, msaa = 4, bloom = true, ao = true, paintAtlas = 4096, particles = 1.0f },
        };
    }

    // ---- Player physics / feel (meters, seconds) ----
    public static class PLAYER
    {
        public const float hp = 100;
        public const float specialChargeRate = 0.8f;   // special meter points per m² of turf inked (0.8 = charges 20% slower); special ink never charges it
        public const float radius = 0.38f;
        public const float height = 1.45f;             // kid form standing height (feet -> top of head)
        public const float squidHeight = 0.55f;
        public const float runSpeed = 6.0f;
        public const float squidDrySpeed = 2.9f;       // squid hopping on unpainted ground
        public const float swimSpeed = 11.8f;          // squid submerged in own ink
        public const float enemyInkSpeed = 1.9f;
        public const float climbSpeed = 7.5f;
        public const float accelGround = 42;
        public const float accelAir = 14;
        public const float accelSwim = 60;
        public const float jumpVel = 8.4f;
        public const float swimJumpVel = 9.4f;
        public const float gravity = 25;
        public const float maxFall = 40;
        public const float inkMax = 100;
        public const float inkRefillSwim = 42;         // per second while submerged
        public const float inkRefillKid = 9;           // per second in kid form after idle delay
        public const float inkRefillDelay = 0.9f;
        public const float enemyInkDps = 20;           // damage/s while standing in enemy ink ...
        public const float enemyInkDamageCap = 40;     // ... never takes you below (hp - cap) from ink alone
        public const float regenDelay = 1.3f;
        public const float regenRate = 22;
        public const float regenRateSwim = 60;
        public const float respawnTime = 5.5f;
        // share of the special gauge you keep when splatted (a special running at the time ends and the gauge restarts
        // from this share of full); the rest is lost, and what's kept carries through the respawn
        public const float specialKeepOnSplat = 0.5f;
        public const float spawnInvuln = 1.6f;
        public const float fallDeathY = -1.45f;        // touching the sea (surface y = -1.6) splats you
        public const float waterY = -1.6f;

        // ---- handling (see actor _horizontal / _integrate)
        // ground run: S-curve accel (ease-in over the first ~1.6 m/s, ease-out over the last 28 % of top speed)
        public const float runAccel = 70, runAccelIn = 0.5f, runInKnee = 1.6f, runOutKnee = 0.28f, runOutMin = 0.22f;
        public const float runDecel = 58, runDecelMin = 0.4f, runDecelKnee = 2.2f;   // brake: strong at speed, eases into the stop
        public const float reverseDecel = 78, reverseAngle = 2.2f;                  // > ~126° input change = plant-and-reverse
        public const float turnRate = 15, turnRateSlow = 1.5f;                      // velocity heading slew (rad/s)
        public const float airAccel = 20, airDecel = 4, airMinSpeed = 4.6f;
        public const float squidAccel = 34, squidDecel = 26, squidTurn = 13;        // squid hopping on dry ground (also the swim-exit glide)
        public const float swimAccel = 64, swimAccelIn = 0.75f, swimDecel = 42, swimTurn = 11, swimOutKnee = 0.22f;   // 90 % speed in 0.18 s
        public const float squidAirAccel = 14, squidAirDecel = 3;
        public const float enemyInkDecel = 30, enemyInkAccel = 30;                  // wading into enemy ink: a quick but readable bog-down
        // jumping
        public const float jumpBuffer = 0.13f;      // a jump pressed this long before touching down still fires on landing
        public const float coyoteTime = 0.12f;      // ... and this long after walking off an edge
        public const float fallGravityMul = 1.2f;   // snappier descent
        public const float apexGravityMul = 0.82f;  // a hair of hang at the top of the arc (|vy| < apexBand)
        public const float apexBand = 1.6f;
        public const float hardLandSpeed = 11.5f;   // landings faster than this (falls > ~2.3 m) cost a short recovery
        public const float hardLandSlow = 0.72f, hardLandTime = 0.16f;
        // character controller
        public const float footRadius = 0.24f;      // flat footprint for the ground probe (ledge hold / lips)
        public const float stepUp = 0.35f;          // curbs/lips a kid walks straight onto (body capsule is lifted by this much)
        public const float stepDown = 0.45f;        // ground stick range while grounded (ramps, steps down)
        public const float squidStepUp = 0.24f, squidBodyLift = 0.16f;
        public const float ledgeAssist = 0.35f;     // falling feet this far below a ledge top still land on it (pop-up, visually smoothed)
        // facing (angular spring with a rate cap: smooth ease-in/out turns, never a snap)
        public const float faceOmega = 20, faceMaxRate = 12.5f, faceMaxAcc = 170, squidFaceOmega = 26, squidFaceMaxRate = 17, swimFaceMaxRate = 14, squidFaceMaxAcc = 260;
        public const float aimFaceOmega = 36, aimFaceMaxRate = 24, aimFaceMaxAcc = 380;
        // wall climb
        public const float climbAccel = 46, climbSideSpeed = 5.2f, climbAttachDot = 0.5f, climbDetachDot = -0.45f;
        public const float ledgePopClear = 0.42f;   // apex this far above the ledge top when popping over it
        public const float ledgePopCarry = 2.5f;    // forward speed onto the ledge
        public const float emergeDelay = 0.07f;     // squid → kid before the first shot can leave the barrel (the shot is buffered, not lost)
        public const float fireBuffer = 0.16f;
    }

    // ---- Match ----
    public static class MATCH
    {
        public static readonly float[] durations = { 90, 180 };   // seconds
        public const float defaultDuration = 180;
        public const float maxDuration = 180;      // hard cap (3 min): the final-minute song expects rounds of at most this length
        public const float finalCountdown = 10;
        public const int teamSize = 4;
        public const float pointsPerM2 = 1.0f;     // turf points per square metre newly inked
        // death markers: a squid-skull in the victim's ink where anyone was splatted (world view, minimap, map)
        public const float deathMarkLife = 5;      // seconds on screen
        public const float deathMarkFade = 1.3f;   // … the last of which fade out
    }

    // ---- Zone Control: the rules' numbers
    public static class ZONES
    {
        public const float duration = 300;              // 5 minutes (+ overtime)
        public const float count = 100;                 // each team's countdown
        public const float rotateMin = 30, rotateMax = 60;   // the operational objective swaps between the centre and a side zone this often (s)
        public const float finalCentre = 30;            // from this many seconds left (and all through overtime) only the centre is live
        public const float warn = 0.30f;                // the other team's share of a held zone that sounds the "about to flip" warning
        public const float control = 0.80f, contest = 0.40f;   // ink share to take a zone / to neutralise the other team's
        public const float rateCenter = 1;              // points / s holding the centre
        public const float rateHome = 0.5f;             // … holding the side zone on your own half (closer to your spawn)
        public const float rateAway = 2;                // … holding the side zone on the other team's half
        public const float penaltyK = 0.75f;            // penalty = ROUND(0.75 × (start − end)) (+1 if start was 100)
        public const float gaugeHeld = 4.5f;            // special points / s for the team NOT holding the objective
        public const float gaugeNeutral = 1.5f;         // … for the team behind while nobody holds it
        public const float overtimeGrace = 10;          // s off the objective before overtime ends against the team behind
        public const float overtimeMax = 300;           // overtime cap (s)
        public const float sampleHz = 5;                // coverage checks per second
    }

    // ---- Progression ----
    public static class PROGRESSION
    {
        public static int xpForLevel(int lvl) => 800 + lvl * 350;
        public const float xpWin = 1200, xpLose = 500, xpPerTurfPoint = 1.0f, xpPerSplat = 40;
        // Zone Control: turf counts for less, ink laid on the live zone counts extra, and a knockout win pays a flat bonus
        public const float zonesTurfScale = 0.6f, zonesXpPerZoneTurfPoint = 1.0f, zonesXpKnockout = 300;
    }

    // ---- On-screen touch controls (Core/TouchControls) ----
    public static class TOUCH
    {
        // Button cluster geometry, in multiples of the step unit `u`. `u` follows the short screen edge so the cluster keeps
        // its proportions in landscape. Values are in density-independent points (CSS px); TouchControls scales by dpi.
        public const float stepFrac = 0.062f;     // u = min(viewport w, h) * stepFrac, clamped to the two values below
        public const float minStep = 22;          // never smaller (the fire button is ~3 steps across ⇒ a ~44dp+ target)
        public const float maxStep = 42;
        public const float edgeSteps = 1.4f;      // keep this far (in steps) off the screen edge …
        public const float edgeMin = 34;          // … but never closer than this: Android's back gesture owns the edge and cancels touches
        public const float stickR = 4.6f;         // movement stick radius, in steps
        public const float topGuard = 0.16f;      // the stick never starts above this fraction of the screen height
        // Camera gain for touch look: radians per point (~2.5x the mouse's 0.0021). Scaled by settings.touchSensitivity.
        public const float lookSens = 0.0052f;
        // Floors for the renderer's image settings on a touch device (see rendererQuality).
        public const float minPixelRatio = 1.5f;
        public const int minMsaa = 2;
    }

    // ------------------------------------------------------------------------------------------------ def types

    public class TeamPalette
    {
        public string id, a, b;
        public string[] names;
        public TeamPalette(string id, string a, string b, string nameA, string nameB) { this.id = id; this.a = a; this.b = b; names = new[] { nameA, nameB }; }
    }

    public class WeaponStats
    {
        public float range, damage, rate, mobility, paint;
        public WeaponStats(float range, float damage, float rate, float mobility, float paint) { this.range = range; this.damage = damage; this.rate = rate; this.mobility = mobility; this.paint = paint; }
    }

    public class WeaponDef
    {
        public string id, name, kind, @class, anim, blurb, special, sub;
        public WeaponStats stats;
        public float specialCost;
        // shooter family
        public float fireInterval, damage, inkPerShot, projSpeed, straightTime, range, spreadGround, spreadAir;
        public float impactRadius, trailRadius, trailEvery, moveSpeedFiring, moveSpeedCharging;
        // roller
        public float rollSpeed, rollWidth, rollInkPerMeter, rollDamage, flickInterval, flickWindup, flickInk, flickDrops;
        public float flickDamageNear, flickDamageFar, flickSpeed, flickSpreadDeg;
        // charger / spinner
        public float chargeTime, rangeMin, rangeMax, damageMin, damageMax, inkFull, lineSplatEvery, lineRadius;
        public float burstMin, burstMax, speedMin, speedMax, straightMin, straightMax;
        // blaster
        public float directDamage, splashDamageMax, splashDamageMin, splashRadius, burstRadius;
        // bucket
        public float windup, throwSpeed, lob, blobs, gravity, drag, reach, belowFalloff, belowFalloffMax;
        // twins
        public float rollDist, rollTime, rollInk, rollCharges, rollLockout, rollReset;
        public float turretInterval, turretSpread, turretProjSpeed, turretStraight, turretRange;
        // brush
        public float brushSpeed, brushWidth, brushInkPerMeter, brushDamage, brushHitCd;
        public float swipeInterval, swipeInk, swipeDrops, swipeSpeed, swipeSpreadDeg, swipeDamageNear, swipeDamageFar;
        // brolly
        public float pellets, pelletDamage, pelletDamageFar, falloffStart, falloffEnd, pelletGrav, pelletDrag, pelletLife, pelletSize, pelletPaint;
        public float spreadDeg, moveSpeedShield, canopyHp, openDelay, openTime, closeTime, launchHold, regrowTime;
        public float canopyRegen, canopyRegenDelay, launchSpeed, launchLife, launchPaint;
        // bow
        public float ring1, inkMin, flightFull, speedTap, speedFull, straight, grav, diveGrav, diveDrag, damageFull;
        public float sideMul, sideFull, fanTap, fanRing, fanFull, fuseRing, fuseFull, burstInner;
        public float paintTap, paintStick, cooldown, moveSpeedDrawing;
        public float[] flightTap, flightRing, speedRing, damageTap, damageRing, burstRadiusTier, burstDamage, burstEdge, burstPaint;
        // blade
        public float tapInterval, tapInk, tapDrops, tapSpreadDeg, tapSpeed, tapStraight, tapDamageNear, tapDamageFar, tapDropRadius, tapMelee;
        public float chargeDelay, chargeStore, heavyInk, heavyInterval, heavyMelee, lungeDist, lungeTime, strokeRadius;
        public float waveDamage, waveDamageFar, waveSpeed, waveRange, waveWidth, waveHeight, wavePaintEvery, wavePaintRadius, waveEndRadius;
        public float meleeArcDeg, heavyArcDeg;
        // mitts
        public float punchInterval, inkPerPunch, punchSpread, fistSpeed, fistRange, fistSize, guardArmor, guardArc, guardAfter;
        public float botApproach, punchDamage, splashMax, splashMin, fistPaint, leapChargeTime, leapInkMin, leapInkMax;
        public float leapArmor, leapArmorGrace, leapSpeedMin, leapSpeedMax, leapAngle, leapAngleMin, leapAngleMax, leapPitchK, leapVyMax;
        public float landRadius, landDamageMax, landDamageMin, landPaint, landInvuln, gloveRadius, gloveDamage, clingDrain;

        public string animKind => anim ?? kind;
    }

    public class SubDef
    {
        public string id, name, kind, blurb;
        public bool placed;
        public float inkCost, throwSpeed, fuse, radius, damageMax, damageMin, paintRadius, directDamage, splashDamage;
        public float speed, seekRange, life, trailRadius, triggerDist, turnRate, commitDist, creep, cloudTime, trackTime;
        public float width, height, hp, decay, shotMul, pulse, drops, sprayRadius, sprayFade, dropDamage;
        public float max, triggerRadius, armTime, delay, damage, uses, mistTime, slow, inkDrain;
        public float chargeTime, moveBoost, jumpBoost, shakeBoost, maxRate, gap, hopSpeed, hopUp, trailEvery;
        public float senseRadius, senseUp, maxTravel, feetPaint, lockRange, flyLife, unfoldTime, launchSpeed0, launchSpeed, launchAccel, launchLife;
        public float coreRadius, fallbackPaint, dropHits, dropPaint, range, pitchDown, floorExit, maxBounces, size;
        public float puddleRadius, directMark, trailLife, trailDamage, trailMark, outTime, hover, hoverRadius, tickRate, tickDamage, hoverPaint;
        public float returnSpeed, orbit, orbitRadius, orbitSpin, orbitDamage, orbitHitCd, contact, hitFuse, hitRadius, hitDamageMax, hitDamageMin, hitPaintRadius;
    }

    public class SpecialDef
    {
        public string id, name, kind, blurb, bomb;
        public float rise, hang, radius, killRadius, damageMax, damageMin, duration, dps, throwSpeed, driftSpeed, gap;
        public float knockPerDamage, knockMax, shareRange, slow, inkMul, cast, aimTime, flight, pull, interval, speed, range, damage;
        public float height, paintEvery, paintRadius, charge, blast, holdTime, holdSpeed, hopVel, attackVel, attackFwd, cooldown;
        public float max, inflate, rMin, rMax, drift, life, popDamage, blastMul, accel, boost, boostGap, projSpeed, directDamage;
        public float splashMax, splashMin, splashRadius, reach, lunge, moveSpeed, throwRadius, throwDamageMax, throwDamageMin;
        public float turnRate, turnRateStill, strafe, guardTime, deflectArc, deflectR, bombClearR;
        public float flipTime, flipReach, flipRadius, flipBackReach, flipBackRadius, cheer, cheerSpecial, autoThrow, fuse;
        public float impactDirect, impactSplash, impactDirectR, impactRadius, hp, rollSpeed;
        public float gunInterval, gunDamage, gunSpeed, gunRange, gunSpread, cannonGap, cannonSpeed, cannonRadius, cannonDamageMax, cannonDamageMin, rollArmor;
    }

    public class DifficultyDef
    {
        public string id, name;
        public float reaction, aimError, fireDiscipline, awareness, aimOmega, aimTurn;
    }

    public class MapDef
    {
        public string id, name, blurb, theme, timeDay, timeDusk;
    }

    public class QualityDef
    {
        public string id;
        public float pixelRatio, particles;
        public int shadowSize, msaa, paintAtlas;
        public bool bloom, ao;
        public QualityDef Clone() => (QualityDef)MemberwiseClone();
    }

    // ---- Settings defaults (persisted as JSON in PlayerPrefs 'inkwave.settings') ----
    [System.Serializable]
    public class Settings
    {
        public float sensitivity = 1.0f;        // mouse multiplier 0.2..3
        public float padSensitivity = 1.0f;
        public bool invertY = false;
        public float fov = 82;                  // horizontal FOV at 16:9, 65..100
        public string quality = "high";         // 'low' | 'medium' | 'high' | 'ultra'
        public bool qualityChosen = false;      // set when the player picks a tier; the touch default only applies while false
        public bool shadowsChosen = false;      // …and the same for shadows, tracked separately
        public bool shadows = true;
        public bool bloom = true;
        public float cameraShake = 1.0f;        // 0..1
        public bool showFps = false;
        public int fpsCap = 0;                  // frame rate limit: 0 = match the display, else 60 / 30
        public float master = 0.8f, music = 0.6f, sfx = 0.85f;
        public bool colorblind = false;
        public bool minimap = true;
        public float matchLength = 180;
        public string lastMode = "turf";        // battle mode last picked on the stage select: 'turf' | 'zones'
        public string difficulty = "normal";
        public float rumble = 1.0f;             // gamepad vibration 0..1
        public float aimAssist = 1.0f;          // gamepad aim assist 0..1
        public bool aimAssistMouse = false;     // optional aim assist for mouse
        public string touchControls = "auto";   // on-screen controls: 'auto' (decide by device) | 'on' | 'off'
        public float touchSensitivity = 1.0f;   // touch look multiplier 0.2..3
        public bool touchInvertY = false;       // kept separate from invertY: the natural touch mapping is usually the opposite

        const string KEY = "inkwave.settings";

        public static Settings load()
        {
            var json = UnityEngine.PlayerPrefs.GetString(KEY, "");
            if (string.IsNullOrEmpty(json)) return new Settings();
            try { return UnityEngine.JsonUtility.FromJson<Settings>(json) ?? new Settings(); }
            catch { return new Settings(); }
        }

        public void save()
        {
            UnityEngine.PlayerPrefs.SetString(KEY, UnityEngine.JsonUtility.ToJson(this));
            UnityEngine.PlayerPrefs.Save();
        }
    }
}
