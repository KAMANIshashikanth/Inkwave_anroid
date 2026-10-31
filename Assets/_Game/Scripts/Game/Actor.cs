// Port of src/game/actor.js — one squidkid (local player or bot). Owns movement physics, ink/health/special state
// and drives its Character visual. Controllers (player input / bot AI) only write `intent` + aim; everything else
// lives here so bots and the player play by exactly the same rules.
//
// Movement = a kinematic character controller:
//  · feet: flat-footprint ground probe (physics.groundProbe) — exact on slopes, ledges hold until the whole
//    footprint is off, curbs ≤ stepUp are walked onto, ground is stuck to within stepDown (ramps, steps down)
//  · body: a capsule lifted above the step height, pushed out horizontally while grounded (walls never fight the feet)
//  · grounded velocity follows the ground plane (no micro-hops up ramps, no sliding when standing on them)
//  · vertical discontinuities (step-ups, ledge assists) are absorbed by a critically-damped visual offset (smoothY)
//  · horizontal: S-curve accel, eased braking, heading slew (turns carve at full speed), plant-and-reverse
//  · facing: angular spring with a rate cap (turnRate is fed to the animation)
//  · jump buffer + coyote time, apex hang, hard-landing recovery, fire buffer across form changes
//
// Specials: Tidal Slam and Ink Tempest run here; every other special runs through G.specials (Specials/SpecialSystem.cs),
// and one whose kind is not ported yet plays as Tidal Slam (SpecialSystem.resolve).
using System.Collections.Generic;
using Inkwave.World;
using UnityEngine;
using static Inkwave.Ctx;

namespace Inkwave
{
    public static partial class G
    {
        public static readonly List<Actor> actors = new List<Actor>();
        public static Actor local;
        public static PaintSystem paint;
        public static Vector3 camPos;   // three-space camera position (audio / fx distance checks)
    }

    public class Intent { public Vector3 move; public bool jump, squid, fire, sub, special; }

    public class Status { public float track, poison, reveal, shield; public int trackTeam = -1, revealTeam = -1; }

    public class Stats { public float turf; public int splats, deaths, specials; }

    public class SpecialState
    {
        public string id, phase, kind;
        public SpecialDef def;
        public float t, dur, startY;
        public bool ended, endCued;
        public SubDef bomb; public float nextThrow;   // Bomb Barrage: its bomb, the next throw allowed at (G.time)
        public object data;                           // a kind's own state (Strike.State, Blower.State, …)
        public bool armor, body, inkProof, noSquid, faceMove, aimFace, jumpBack, raise;
        public float? speed, accel, faceYaw;
        public float hang, seat, firing;
        public Vector3? origin;
    }

    public class SuperJumpState { public string phase; public float t, dur, marker; public object target; public Vector3 from, to; public bool home; }

    public class Actor
    {
        const float TAU = Mathf.PI * 2;
        static readonly Vector3 DOWN = new Vector3(0, -1, 0);

        public readonly int team; public string name; public readonly bool isLocal; public bool isBot; public int slot;
        public string weaponId; public WeaponDef weapon;
        public string subId; public SubDef sub;
        public string specialId;
        public readonly Status status = new Status();
        public Vector3 pos, vel;
        public float yaw, aimYaw, aimPitch;
        public Vector3 aimDir = new Vector3(0, 0, 1), aimPoint;
        public readonly Intent intent = new Intent();
        readonly Intent _prevIntent = new Intent();
        float _squidPressT = -1, _firePressT = -1;
        public readonly Contacts contacts = new Contacts();
        public readonly Hit groundHit = new Hit(), wallHit = new Hit();
        public readonly GroundHit ground = new GroundHit();
        public Vector3 groundN = Vector3.up, wallN = new Vector3(0, 0, 1);
        readonly Hit _ledgeHit = new Hit();
        public readonly ICharacter character;
        public readonly WeaponRunner weaponRunner;
        public readonly Stats stats = new Stats();
        public readonly AnimState anim = new AnimState();
        public bool remote;   // online proxies (not ported) — always false here
        public BotBrain bot;  // bots (and the local player on autopilot)

        // state (reset())
        public bool alive, submerged, climbing, grounded, onEnemy;
        public float hp, ink, special;
        public SpecialState specialActive;
        public string form;
        public int groundTeam;   // 0 none, 1 own, 2 enemy
        public float respawnTimer, invuln, lastDamage, lastFire, inkIdle, damageFromInk, hurtFlash;
        public Actor lastAttacker;
        public float fireFacing, airTime, landSpeed, landT, hardLand, coyote, jumpBuffer, fireBuffer, kidT, climbExit, climbV, inkWarnCd;
        public SuperJumpState superJumpState;
        public SubItem _jumpBeacon;   // the jump beacon this super jump lands on (subs.js uses it up on landing)
        public float yawVel; float? _faceTarget;
        public float smoothY, smoothYV;
        bool _evSub, _evEnemy; string _evForm = "kid";
        float roofT, roofStall; Vector3? roofDir; Vector3 roofP;
        float _wakeT;
        readonly List<int> _railIds = new List<int>();

        public Actor(int team, string name, string weapon = "shooter", string sub = null, string special = null, bool isLocal = false, bool isBot = false, int slot = 0, CharacterStyle.Style style = null)
        {
            this.team = team; this.name = name; this.isLocal = isLocal; this.isBot = isBot; this.slot = slot;
            weaponId = weapon;
            this.weapon = Config.WEAPONS[weapon];
            subId = sub != null && Config.SUBS.ContainsKey(sub) ? sub : (this.weapon.sub ?? "bomb");
            if (!SubSystem.ported(subId)) subId = "bomb";   // a kit sub not ported yet throws the Splat Bomb
            this.sub = Config.SUBS[subId];
            specialId = special != null && Config.SPECIALS.ContainsKey(special) ? special : (this.weapon.special ?? "slam");
            character = new Character(G.teamColors[team], weapon, style, name, isLocal) { actor = this };
            character.setSub(this.sub.kind);
            // Character → game bus: re-emitted as 'actor:<name>' with the actor and the surface under it
            character.onEvent = (n, data) => { data ??= new Ev(); data.actor = this; data.surface = groundTeam; emit("actor:" + n, data); };
            weaponRunner = new WeaponRunner(this);
            reset();
        }

        public Color color => G.teamColors[team];
        public int enemyTeam => 1 - team;

        public void reset()
        {
            alive = true;
            hp = PLAYER.hp;
            ink = PLAYER.inkMax;
            special = 0;            // points toward special
            specialActive = null;
            form = "kid";           // desired form
            submerged = false;
            climbing = false;
            grounded = false;
            groundTeam = 0;
            respawnTimer = 0;
            invuln = 0;
            lastDamage = 99; lastFire = 99; inkIdle = 0;
            damageFromInk = 0;
            hurtFlash = 0;
            lastAttacker = null;
            fireFacing = 0;
            airTime = 0;
            landSpeed = 0;
            landT = 99;             // time since the last landing
            hardLand = 0;           // 0..1 recovery weight after a hard landing
            coyote = 0;
            jumpBuffer = 0;
            fireBuffer = 0;
            kidT = 99;              // time since becoming a kid (emerge delay for the first shot)
            climbExit = 0;
            climbV = 0;
            inkWarnCd = 0;
            superJumpState = null;
            yawVel = 0; _faceTarget = null;
            smoothY = 0; smoothYV = 0;
            onEnemy = false;
            _evSub = false; _evEnemy = false; _evForm = "kid";
            status.track = 0; status.poison = 0; status.reveal = 0; status.shield = 0;
            _jumpBeacon = null;
            weaponRunner?.reset();
        }

        public void setWeapon(string id)
        {
            weaponId = id; weapon = Config.WEAPONS[id];
            character.setWeapon(id);
            weaponRunner.reset();
        }

        public float specialCost() => weapon.specialCost;
        public float specialFrac() => clamp(special / specialCost(), 0, 1);
        public bool specialReady() => special >= specialCost() && specialActive == null;

        public void addTurf(float area)
        {
            if (area <= 0) return;
            stats.turf += area;
            emit("turf", new Ev { actor = this, area = area });
            if (specialActive == null)
            {
                bool was = specialReady();
                special = Mathf.Min(specialCost(), special + area * PLAYER.specialChargeRate);
                if (!was && specialReady()) emit("special:ready", new Ev { actor = this });
            }
        }
        public void addTurfNoSpecial(float area) { if (area > 0) { stats.turf += area; emit("turf", new Ev { actor = this, area = area }); } }

        public void spawnAt(Vector3 p, float yaw)
        {
            reset();
            pos = p;
            vel = Vector3.zero;
            this.yaw = aimYaw = yaw;
            aimPitch = 0;
            invuln = PLAYER.spawnInvuln;
            character.root.localPosition = p;
            character.root.localRotation = Quaternion.AngleAxis(yaw * Mathf.Rad2Deg, Vector3.up);
            character.setVisible(true);
            character.setHurt(0, G.teamColors[enemyTeam]);
            // settle onto whatever is under the spawn point
            var gh = G.physics.groundProbe(p.x, p.y, p.z, 0.3f, 0.3f, PLAYER.footRadius, ground, false);
            if (gh.hit && Mathf.Abs(gh.y - p.y) < 0.3f) { pos.y = gh.y; grounded = true; groundN = gh.normal; }
        }

        // Drop in from above the spawn pad (respawn), landing with a splash.
        public void respawn()
        {
            var pad = G.level.spawnPads[team];
            float a = (slot / 4f) * Mathf.PI * 2 + 0.6f;
            var p = new Vector3(pad.x + Mathf.Cos(a) * 1.1f, pad.y + 4.5f, pad.z + Mathf.Sin(a) * 1.1f);
            float y = team == 0 ? 0 : Mathf.PI;
            float sp = special;      // the share kept through the splat (spawnAt → reset() would zero it)
            spawnAt(p, y);
            special = sp;
            grounded = false;
            vel = new Vector3(0, -4, 0);
            character.trigger("spawn");
            G.fx?.spawnFlash(new Vector3(p.x, pad.y, p.z), color);
            if (isLocal) G.audio?.play("respawn");
            emit("respawn", new Ev { actor = this });
        }

        // ------------------------------------------------------------------ damage
        public bool damage(float amount, Actor attacker, string source = "weapon")
        {
            if (!alive || amount <= 0) return false;
            if (invuln > 0) return false;
            if (specialActive != null && specialActive.armor) amount *= 0.25f;
            if (G.specials != null) { amount = G.specials.filterDamage(this, amount, attacker, source); if (!(amount > 0)) return false; }
            { var K = Kits.main(weapon?.kind); if (K != null) { amount = K.damageTaken(weaponRunner, amount, attacker, source); if (!(amount > 0)) return false; } }   // kit armour (e.g. the mitts' leap)
            hp -= amount;
            lastDamage = 0;
            hurtFlash = Mathf.Min(1, hurtFlash + amount / 60);
            if (attacker != null) lastAttacker = attacker;
            if (source != "ink")
            {
                // flinch toward the attacker: direction in the character's local frame (+z forward, +x to its left)
                float dx = 0, dz = 1;
                if (attacker != null && attacker != this) { dx = attacker.pos.x - pos.x; dz = attacker.pos.z - pos.z; }
                float cy = Mathf.Cos(yaw), sy = Mathf.Sin(yaw);
                float lx = dx * cy - dz * sy, lz = dx * sy + dz * cy;
                float l = Mathf.Sqrt(lx * lx + lz * lz); if (l == 0) l = 1;
                lx /= l; lz /= l;
                float amp = clamp(amount / 60, 0.4f, 1.2f);
                character.trigger("hit", amp, new Vector2(lx, lz));
                if (isLocal) rumble(this, clamp(amount / 110, 0.18f, 0.7f), clamp(amount / 80, 0.25f, 0.8f), 90 + Mathf.Min(120, amount));
            }
            emit("damage", new Ev { victim = this, attacker = attacker, amount = amount, source = source });
            if (hp <= 0) { splat(attacker, source); return true; }
            return false;
        }

        public void splat(Actor attacker, string cause = "weapon")
        {
            if (!alive) return;
            alive = false;
            hp = 0;
            respawnTimer = PLAYER.respawnTime;
            stats.deaths++;
            // keep a share of the gauge (PLAYER.specialKeepOnSplat); a special cut short by the splat restarts the gauge
            // from that share of full (its points were spent when it started). Carried through respawn().
            float keep = PLAYER.specialKeepOnSplat;
            special = specialActive != null ? specialCost() * keep : Mathf.Min(specialCost(), special) * keep;
            G.specials?.onSplat(this);
            specialActive = null;
            climbing = false;
            weaponRunner.onDeath();
            var col = attacker != null ? attacker.color : G.teamColors[enemyTeam];
            G.fx?.splatted(pos + Vector3.up * 0.6f, col);
            if (attacker != null)
            {
                attacker.stats.splats++;
                // burst into the attacker's ink
                attacker.addTurf(G.paint.splat(pos + Vector3.up * 0.35f, 1.7f, attacker.team, new SplatOpts { seed = random() }));
            }
            character.setVisible(false);
            if (isLocal) rumble(this, 0.8f, 0.6f, 260);
            emit("splatted", new Ev { victim = this, attacker = attacker, cause = cause });
        }

        public static void rumble(Actor actor, float strong, float weak, float ms)
        {
            if (actor == null || !actor.isLocal || actor.isBot) return;
            G.input?.rumble(strong, weak, ms);
        }

        // ------------------------------------------------------------------ update
        public void update(float dt)
        {
            anim.time = G.time;
            // safety net: a non-finite position/velocity must never poison the camera or physics
            if (!float.IsFinite(pos.x + pos.y + pos.z + vel.x + vel.y + vel.z + yaw + smoothY))
            {
                Debug.LogWarning("[inkwave] non-finite actor state recovered " + name);
                superJumpState = null; specialActive = null; yaw = 0; yawVel = 0; smoothY = 0; smoothYV = 0;
                if (alive) respawn(); else { pos = G.level.spawnPads[team]; vel = Vector3.zero; }
            }
            if (!alive)
            {
                respawnTimer -= dt;
                if (respawnTimer <= 0 && (G.match?.canRespawn() ?? true)) respawn();
                return;
            }
            var prev = _prevIntent;
            bool firePressed = intent.fire && !prev.fire;
            bool jumpPressed = intent.jump && !prev.jump;
            bool subReleased = !intent.sub && prev.sub;
            bool specialPressed = intent.special && !prev.special;
            if (intent.squid && !prev.squid) _squidPressT = G.time;
            if (firePressed) _firePressT = G.time;
            prev.fire = intent.fire; prev.jump = intent.jump; prev.sub = intent.sub; prev.special = intent.special; prev.squid = intent.squid;

            invuln = Mathf.Max(0, invuln - dt);
            lastDamage += dt; lastFire += dt; landT += dt; kidT += dt;
            hurtFlash = Mathf.Max(0, hurtFlash - dt * 0.6f);
            inkWarnCd -= dt;
            jumpBuffer = jumpPressed ? PLAYER.jumpBuffer : Mathf.Max(0, jumpBuffer - dt);
            fireBuffer = firePressed ? PLAYER.fireBuffer : Mathf.Max(0, fireBuffer - dt);
            hardLand = Mathf.Max(0, hardLand - dt / PLAYER.hardLandTime);

            // aim vector from yaw/pitch
            float cp = Mathf.Cos(aimPitch);
            aimDir = new Vector3(Mathf.Sin(aimYaw) * cp, Mathf.Sin(aimPitch), Mathf.Cos(aimYaw) * cp);

            // ---- super jump / specials in progress own the body
            if (superJumpState != null) { updateSuperJump(dt); finishFrame(dt); return; }
            var spx = specialActive;
            if (spx != null && spx.body) { updateSpecial(dt); if (alive) finishFrame(dt); return; }
            if (specialPressed && specialReady()) { startSpecial(); finishFrame(dt); return; }

            // ---- form: squid while the swim button is held. Swim + fire both held → the most recent press wins, so diving
            // mid-spray and popping out of the ink to shoot both work instantly (the pop-out shot is buffered, never lost).
            bool fireWins = (intent.fire || fireBuffer > 0) && _firePressT >= _squidPressT;
            bool wantSquid = intent.squid && !fireWins && !weaponRunner.busy() && !(spx != null && spx.noSquid);
            if (wantSquid != (form == "squid"))
            {
                form = wantSquid ? "squid" : "kid";
                if (!wantSquid) kidT = 0;
                if (isLocal || nearCamera()) G.audio?.play(wantSquid ? "squid_in" : "squid_out", new AudioOpts { pos = pos, volume = isLocal ? 0.8f : 0.5f });
                if (wantSquid && groundTeam == 1) G.fx?.burst(new Vector3(pos.x, pos.y + 0.1f, pos.z), Vector3.up, color, new FxOpts { count = 8, speed = 2.5f, size = 0.07f });
            }
            bool isSquid = form == "squid";

            // ---- surface under feet (from last frame's ground probe; position hasn't moved since)
            surface();
            submerged = isSquid && grounded && groundTeam == 1;
            // enemy ink: slows and hurts — unless a special makes you ink-proof
            bool enemyInk = grounded && groundTeam == 2 && !submerged && !(spx != null && spx.inkProof);
            onEnemy = enemyInk;

            // ---- wall climb (squid on own-ink wall, pushing into it)
            updateClimb(dt, isSquid);

            // ---- horizontal movement (a twins dodge roll owns it while it lasts)
            if (spx != null) G.specials.move(this, spx, dt);
            if (!climbing) horizontal(dt, isSquid, enemyInk);
            if (weaponRunner.dodgeVelocity(out var dv)) { vel.x = dv.x; vel.z = dv.z; }

            // ---- dodge roll (twin pistols): jump pressed while firing and moving rolls instead of jumping. The runner's
            // tryDodge dispatches by weapon kind (it spends the ink, triggers the character's roll, emits the FX events)
            if (jumpBuffer > 0 && !isSquid && weaponRunner.tryDodge(intent)) jumpBuffer = 0;
            // kit weapons may take the jump press instead: Kits.MAIN[kind].jump(runner, intent) → true swallows it
            // (Sponge Mitts: fire + jump charges a leap; jump lets go of a wall)
            if (jumpBuffer > 0 && !isSquid && (Kits.main(weapon.kind)?.jump(weaponRunner, intent) ?? false)) jumpBuffer = 0;

            // ---- jump (buffered, with coyote time)
            coyote = grounded ? PLAYER.coyoteTime : coyote - dt;
            bool jumped = false;
            if (jumpBuffer > 0 && (grounded || coyote > 0) && !climbing)
            {
                float jv = submerged ? PLAYER.swimJumpVel : PLAYER.jumpVel;
                if (enemyInk) jv *= 0.72f;
                vel.y = jv;
                grounded = false; coyote = 0; jumpBuffer = 0;
                jumped = true;
                character.trigger("jump");
                if (submerged) G.fx?.burst(pos, Vector3.up, color, new FxOpts { count = 10, speed = 3.5f, size = 0.08f });
                if (isLocal || nearCamera()) G.audio?.play(submerged ? "swim_splash" : "jump", new AudioOpts { pos = pos, volume = 0.6f });
                emit("actor:jump", new Ev { actor = this, surface = groundTeam, swim = submerged });
            }

            // ---- integrate + collide (feet + body)
            integrate(dt, isSquid, jumped);
            G.subs?.blockActor(this);
            spawnBarrier();

            // ---- ink / hp
            if (enemyInk)
            {
                if (damageFromInk < PLAYER.enemyInkDamageCap && invuln <= 0 && !(status.shield > 0))
                {
                    float d = Mathf.Min(PLAYER.enemyInkDps * dt, PLAYER.enemyInkDamageCap - damageFromInk);
                    damageFromInk += d;
                    hp = Mathf.Max(1, hp - d);
                    hurtFlash = Mathf.Min(1, hurtFlash + dt * 0.5f);
                }
                lastDamage = Mathf.Min(lastDamage, 0.4f);
            }
            else damageFromInk = Mathf.Max(0, damageFromInk - dt * 30);
            if (lastDamage > PLAYER.regenDelay && hp < PLAYER.hp)
                hp = Mathf.Min(PLAYER.hp, hp + (submerged ? PLAYER.regenRateSwim : PLAYER.regenRate) * dt);
            bool wasFull = ink >= PLAYER.inkMax;
            var st = status;
            if (st.track > 0) st.track = Mathf.Max(0, st.track - dt);
            if (st.reveal > 0) st.reveal = Mathf.Max(0, st.reveal - dt);
            if (st.poison > 0) { st.poison = Mathf.Max(0, st.poison - dt); ink = Mathf.Max(0, ink - Config.SUBS["mist"].inkDrain * dt); }
            // poisoned: no idle refill at all and half-speed refill while swimming, so the drain is felt
            if (submerged || climbing) ink = Mathf.Min(PLAYER.inkMax, ink + PLAYER.inkRefillSwim * (st.poison > 0 ? 0.5f : 1) * dt);
            else if (st.poison > 0) { /* drain only */ }
            else if (!isSquid && lastFire > PLAYER.inkRefillDelay && !weaponRunner.busy()) ink = Mathf.Min(PLAYER.inkMax, ink + PLAYER.inkRefillKid * dt);
            else if (isSquid) ink = Mathf.Min(PLAYER.inkMax, ink + PLAYER.inkRefillKid * 0.5f * dt);
            if (!wasFull && ink >= PLAYER.inkMax && isLocal) G.audio?.play("refill_full", new AudioOpts { volume = 0.5f });

            // ---- weapons (a squid → kid pop-out holds the first shot for emergeDelay; a tap during it is buffered)
            bool fire = false, pressed = false;
            if (!isSquid && kidT >= PLAYER.emergeDelay)
            {
                bool buffered = fireBuffer > 0;
                fire = intent.fire || buffered;
                pressed = firePressed || buffered;
                fireBuffer = 0;
            }
            var winp = new WeaponInput { fire = fire, firePressed = pressed, sub = intent.sub && !isSquid, subReleased = subReleased && !isSquid };
            float inkBefore = ink;
            // specials that replace the main weapon (zooka, stamp, blower, crab …) take the trigger; others may take the sub
            if (!(spx != null && G.specials.weapon(this, spx, dt, winp))) weaponRunner.update(dt, winp);
            // sonar-revealed players burn ink faster
            if (st.reveal > 0 && ink < inkBefore) ink = Mathf.Max(0, ink - (inkBefore - ink) * (Config.SPECIALS["sonar"].inkMul - 1));
            if (specialActive != null) G.specials.tick(this, specialActive, dt);
            if (!alive) return;

            // ---- fall into the sea
            // the sea: below the waterline with no deck underneath (dry-dock trenches sit below sea level and are safe)
            if (pos.y < PLAYER.fallDeathY && float.IsNegativeInfinity(G.level.groundHeight(pos.x, pos.z, pos.y + 0.6f)))
            {
                G.fx?.burst(new Vector3(pos.x, PLAYER.waterY + 0.05f, pos.z), Vector3.up, hex("#bfe9ff"), new FxOpts { count = 18, speed = 5, size = 0.1f });
                G.audio?.play("splat_big", new AudioOpts { pos = pos });
                splat(lastDamage < 4 ? lastAttacker : null, "water");
                return;
            }

            finishFrame(dt);
        }

        public bool nearCamera() => (G.camPos - pos).sqrMagnitude < 30 * 30;

        // Paint under the feet: 0 dry, 1 own ink, 2 enemy ink (only while grounded).
        void surface()
        {
            var g = ground;
            if (grounded && g.hit && g.face >= 0)
            {
                int t = G.paint.sample(g.face, g.u, g.v);
                groundTeam = t == 0 ? 0 : (t - 1 == team ? 1 : 2);
            }
            else groundTeam = 0;
        }

        // Probe (super jump charge / external callers): refresh ground + surface at the current position.
        public void probeGround()
        {
            var gh = G.physics.groundProbe(pos.x, pos.y, pos.z, 0.4f, 0.35f, PLAYER.footRadius, ground, form == "squid");
            if (gh.hit) { int t = G.paint.sample(gh.face, gh.u, gh.v); groundTeam = gh.face < 0 || t == 0 ? 0 : (t - 1 == team ? 1 : 2); }
            else groundTeam = 0;
        }

        // ------------------------------------------------------------------ horizontal movement model
        void horizontal(float dt, bool isSquid, bool enemyInk)
        {
            Vector3 mv = intent.move;
            float mh = Mathf.Sqrt(mv.x * mv.x + mv.z * mv.z);
            float mag = Mathf.Min(1, mh);
            float vx = vel.x, vz = vel.z;
            float sp = Mathf.Sqrt(vx * vx + vz * vz);
            // ---- airborne: vector steering with light air control (momentum is kept)
            if (!grounded)
            {
                float target, accel, decel;
                if (isSquid) { target = Mathf.Max(PLAYER.squidDrySpeed, sp); accel = PLAYER.squidAirAccel; decel = PLAYER.squidAirDecel; }
                else { target = Mathf.Max(specialActive?.speed ?? weaponRunner.moveSpeed(), PLAYER.airMinSpeed); accel = PLAYER.airAccel; decel = PLAYER.airDecel; }
                float tvx = mh > 0.01f ? (mv.x / mh) * target * mag : 0, tvz = mh > 0.01f ? (mv.z / mh) * target * mag : 0;
                float dvx = tvx - vx, dvz = tvz - vz, dl = Mathf.Sqrt(dvx * dvx + dvz * dvz);
                float rate = (mh > 0.01f ? accel : decel) * dt;
                if (dl <= rate) { vel.x = tvx; vel.z = tvz; } else { vel.x += (dvx / dl) * rate; vel.z += (dvz / dl) * rate; }
                return;
            }
            // ---- grounded: speed + heading model
            float vt, A, aIn, inKnee, outKnee, D, dMin, dKnee, W;
            if (isSquid && submerged)
            {
                vt = PLAYER.swimSpeed; A = PLAYER.swimAccel; aIn = PLAYER.swimAccelIn; inKnee = 3; outKnee = PLAYER.swimOutKnee; D = PLAYER.swimDecel; dMin = 0.5f; dKnee = 4; W = PLAYER.swimTurn;
            }
            else if (isSquid)
            {
                vt = PLAYER.squidDrySpeed; A = PLAYER.squidAccel; aIn = 0.6f; inKnee = 1; outKnee = 0.3f; D = PLAYER.squidDecel; dMin = 0.5f; dKnee = 2; W = PLAYER.squidTurn;
            }
            else
            {
                vt = weaponRunner.moveSpeed(); A = PLAYER.runAccel; aIn = PLAYER.runAccelIn; inKnee = PLAYER.runInKnee; outKnee = PLAYER.runOutKnee;
                D = PLAYER.runDecel; dMin = PLAYER.runDecelMin; dKnee = PLAYER.runDecelKnee; W = PLAYER.turnRate;
                if (hardLand > 0) vt *= 1 - (1 - PLAYER.hardLandSlow) * hardLand;
            }
            var spx = specialActive;
            if (spx != null && spx.speed != null) { vt = spx.speed.Value; if (spx.accel != null) { A = spx.accel.Value; aIn = 1; D = Mathf.Max(D, spx.accel.Value); } }
            if (enemyInk) { vt = Mathf.Min(vt, PLAYER.enemyInkSpeed); A = Mathf.Min(A, PLAYER.enemyInkAccel); D = Mathf.Max(PLAYER.enemyInkDecel, 0); }
            if (status.poison > 0) vt *= Config.SUBS["mist"].slow;
            if (status.reveal > 0) vt *= Config.SPECIALS["sonar"].slow;
            if (mh < 0.01f)
            {
                // brake: strong at speed, easing into the stop
                if (sp < 1e-4f) { vel.x = 0; vel.z = 0; return; }
                float d = D * (dMin + (1 - dMin) * smoothstep(0, dKnee, sp)) * dt;
                float k = Mathf.Max(0, sp - d) / sp;
                vel.x *= k; vel.z *= k;
                return;
            }
            float tx = mv.x / mh, tz = mv.z / mh, vts = vt * mag;
            float dx = tx, dz = tz;
            if (sp > 0.05f) { dx = vx / sp; dz = vz / sp; }
            float cosA = clamp(dx * tx + dz * tz, -1, 1);
            float ang = Mathf.Acos(cosA);
            if (sp > 0.5f && ang > PLAYER.reverseAngle)
            {
                // plant-and-reverse: brake through zero toward the new direction
                float tvx = tx * vts, tvz = tz * vts, ex = tvx - vx, ez = tvz - vz, el = Mathf.Sqrt(ex * ex + ez * ez);
                float r = Mathf.Max(PLAYER.reverseDecel, D) * dt * (enemyInk ? 0.5f : 1);
                if (el <= r) { vel.x = tvx; vel.z = tvz; } else { vel.x += (ex / el) * r; vel.z += (ez / el) * r; }
                return;
            }
            // heading slews toward the input (faster when slow) — turns carve at full speed instead of dipping
            float wmax = W * (1 + PLAYER.turnRateSlow * (1 - smoothstep(0, vt, sp)));
            float rot = Mathf.Min(ang, wmax * dt);
            if (rot > 1e-6f)
            {
                float s = (dz * tx - dx * tz) >= 0 ? 1 : -1;
                float c = Mathf.Cos(rot * s), sn = Mathf.Sin(rot * s);
                float nx = dx * c + dz * sn, nz = -dx * sn + dz * c;
                dx = nx; dz = nz;
            }
            float ns;
            if (sp < vts)
            {
                float a = A * (aIn + (1 - aIn) * smoothstep(0, inKnee, sp)) * clamp((vts - sp) / (outKnee * vt), PLAYER.runOutMin, 1);
                ns = Mathf.Min(vts, sp + a * dt);
            }
            else
            {
                // over speed (swim exit glide, entering enemy ink, starting to fire): shed it at the brake rate
                ns = Mathf.Max(vts, sp - D * (dMin + (1 - dMin) * smoothstep(0, dKnee, sp - vts)) * dt);
            }
            vel.x = dx * ns; vel.z = dz * ns;
        }

        // ------------------------------------------------------------------ character controller
        void integrate(float dt, bool isSquid, bool jumped)
        {
            if (climbing)
            {
                pos += vel * dt;
                G.physics.collideBody(ref pos, PLAYER.radius, PLAYER.squidBodyLift, PLAYER.squidHeight, contacts, false, true);
                if (contacts.ceiling && vel.y > 0) vel.y = 0;
                grounded = false;
                airTime = 0;
                return;
            }
            bool wasGrounded = grounded && !jumped;
            // (zipline: clinging to where the tether landed.) Kit weapons can hang the kid too: runner.kit.hang (Sponge Mitts: stuck
            // to a wall after a leap) — kid form only, and never while a special runs
            bool kitHang = specialActive == null && form == "kid" && weaponRunner.kit is KitState ks && ks.hang;
            if ((specialActive != null && specialActive.hang > 0) || kitHang)
            {
                vel = Vector3.zero; grounded = false; airTime = 0;
                return;
            }
            // off-limits tops (roofs, crane tops …): nobody can stand there — you slide off
            var rb = wasGrounded && ground.hit && ground.block >= 0 ? G.level.blocks[ground.block] : null;
            if (rb != null && rb.roof) roofSlide(rb, dt); else if (roofT > 0) { roofT = 0; roofDir = null; }
            // balancing on a railing: the feet settle onto its line (unless you're stepping off it)
            if (rb != null && rb.rail && !isSquid) railCentre(rb, dt);
            if (wasGrounded)
            {
                // follow the ground plane: the vertical component keeps the feet on the surface at the current horizontal speed
                var n = groundN;
                vel.y = -(vel.x * n.x + vel.z * n.z) / Mathf.Max(0.35f, n.y);
            }
            else
            {
                float g = PLAYER.gravity;
                if (vel.y < 0) g *= PLAYER.fallGravityMul;
                if (Mathf.Abs(vel.y) < PLAYER.apexBand) g *= PLAYER.apexGravityMul;
                vel.y = Mathf.Max(-PLAYER.maxFall, vel.y - g * dt);
            }
            float prevY = pos.y;
            pos += vel * dt;
            resolve(isSquid, prevY, wasGrounded);
        }

        // Standing on a roof block: pushed toward its nearest edge (a pitched top: downhill), picking up speed until you
        // drop off. The direction sticks for the whole slide; blocked by a wall for a moment → turn 90° and try the next
        // way out. Input still steers across the slide.
        void roofSlide(Block b, float dt)
        {
            var n = b.axes[1];
            if (roofDir == null || roofT == 0)
            {
                roofStall = 0; roofP = pos;
                if (n.y < 0.995f) roofDir = new Vector3(n.x, 0, n.z).normalized;
                else
                {
                    Vector3 ax = b.axes[0], az = b.axes[2];
                    float ddx = pos.x - b.center.x, ddz = pos.z - b.center.z;
                    float lx = ddx * ax.x + ddz * ax.z, lz = ddx * az.x + ddz * az.z;
                    bool useX = b.half.x - Mathf.Abs(lx) < b.half.z - Mathf.Abs(lz);
                    Vector3 axis = useX ? ax : az; float sg = useX ? (lx != 0 ? Mathf.Sign(lx) : 1) : (lz != 0 ? Mathf.Sign(lz) : 1);
                    roofDir = new Vector3(axis.x * sg, 0, axis.z * sg);
                }
            }
            var d = roofDir.Value;
            roofT += dt;
            float moved = (pos.x - roofP.x) * d.x + (pos.z - roofP.z) * d.z;
            roofP = pos;
            roofStall = roofT > 0.1f && moved < 0.3f * dt ? roofStall + dt : 0;
            if (roofStall > 0.25f) { d = new Vector3(-d.z, 0, d.x); roofDir = d; roofStall = 0; }
            float want = Mathf.Min(9, 3 + 14 * roofT);
            float along = vel.x * d.x + vel.z * d.z;
            if (along < want) { vel.x += d.x * (want - along); vel.z += d.z * (want - along); }
        }

        // Body (walls/ceilings) then feet (ground stick / landing). `stick` = we were grounded and did not jump.
        public void resolve(bool isSquid, float prevY, bool stick)
        {
            float lift = isSquid ? PLAYER.squidBodyLift : PLAYER.stepUp;
            float height = isSquid ? PLAYER.squidHeight : PLAYER.height;
            var c = G.physics.collideBody(ref pos, PLAYER.radius, lift, height, contacts, stick, isSquid);
            if (c.ceiling && vel.y > 0) vel.y = 0;
            if (c.wall)
            {
                var n = c.wallNormal;
                float vn = vel.x * n.x + vel.z * n.z;
                if (vn < 0) { vel.x -= n.x * vn; vel.z -= n.z * vn; }
            }
            var gh = ground;
            float up = isSquid ? PLAYER.squidStepUp : PLAYER.stepUp;
            bool wasGrounded = grounded;
            bool isGrounded = false;
            if (stick)
            {
                G.physics.groundProbe(pos.x, pos.y, pos.z, up, PLAYER.stepDown, PLAYER.footRadius, gh, isSquid);
                if (!isSquid) railFeet(gh, pos.y - PLAYER.stepDown, pos.y + up);
                if (gh.hit)
                {
                    float dy = gh.y - pos.y;
                    pos.y = gh.y;
                    isGrounded = true;
                    // discontinuities (curbs, steps up / down) are eased visually; slope following and ramp creases stay exact
                    if (Mathf.Abs(dy) > 0.06f) smoothY -= dy;
                }
            }
            else if (vel.y <= 0.5f)
            {
                // landing: probe from the highest point this frame passed through (never tunnels), with ledge assist
                float top = Mathf.Max(prevY, pos.y);
                float assist = isSquid ? PLAYER.squidStepUp : PLAYER.ledgeAssist;
                G.physics.groundProbe(pos.x, pos.y, pos.z, (top - pos.y) + assist, 0.02f, PLAYER.footRadius, gh, isSquid);
                if (!isSquid) railFeet(gh, pos.y - 0.02f, top + assist);
                if (gh.hit && gh.y >= pos.y - 0.02f && (vel.y <= 0 || gh.y - pos.y < 0.02f))
                {
                    // touching down from above is continuous; only a ledge-assist pop (feet were already below the top last
                    // frame) is a visual step to ease
                    float pop = gh.y - prevY;
                    pos.y = gh.y;
                    isGrounded = true;
                    if (pop > 0.035f) smoothY -= pop;
                }
            }
            if (isGrounded)
            {
                groundN = gh.normal;
                if (!wasGrounded) onLand(isSquid);
                vel.y = 0;
            }
            grounded = isGrounded;
            airTime = isGrounded ? 0 : airTime + 1 / 60f;
        }

        // Railings are only a few cm thick, so the ground probe's discrete footprint ring can straddle one and miss it. A
        // kid's feet are also held by any level rail top the flat footprint overlaps, within [lo, hi]. Squids fall through.
        void railFeet(GroundHit gh, float lo, float hi)
        {
            var L = G.level;
            if (!L.hasRails) return;
            float x = pos.x, z = pos.z, fr = PLAYER.footRadius;
            var ids = L.queryBlocks(x - fr - 0.05f, z - fr - 0.05f, x + fr + 0.05f, z + fr + 0.05f, _railIds);
            Block best = null; float bestY = gh.hit ? gh.y + 1e-3f : float.NegativeInfinity;
            foreach (int id in ids)
            {
                var b = L.blocks[id];
                if (!b.rail || !b.solid || b.axes[1].y < 0.999f) continue;
                float topY = b.center.y + b.half.y;
                if (topY < lo || topY > hi || topY <= bestY) continue;
                float dx = x - b.center.x, dz = z - b.center.z;
                float ex = Mathf.Max(0, Mathf.Abs(dx * b.axes[0].x + dz * b.axes[0].z) - b.half.x), ez = Mathf.Max(0, Mathf.Abs(dx * b.axes[2].x + dz * b.axes[2].z) - b.half.z);
                if (ex * ex + ez * ez > fr * fr) continue;   // the footprint misses its top
                best = b; bestY = topY;
            }
            if (best == null) return;
            gh.hit = true; gh.y = bestY; gh.normal = Vector3.up; gh.block = best.id; gh.face = -1; gh.u = 0; gh.v = 0; gh.center = false; gh.grate = true;
        }

        // Standing on a railing with the feet off its line: ease onto it (≤ 1.1 m/s across). Pushing further off steps off.
        void railCentre(Block b, float dt)
        {
            bool alongX = b.half.x >= b.half.z;
            Vector3 ax = alongX ? b.axes[2] : b.axes[0]; float hw = alongX ? b.half.z : b.half.x;   // across the rail
            float off = (pos.x - b.center.x) * ax.x + (pos.z - b.center.z) * ax.z;
            float outv = Mathf.Abs(off) - Mathf.Max(0, hw - 0.02f);
            if (outv <= 0) return;
            var mv = intent.move; float sg = Mathf.Sign(off);
            if ((mv.x * ax.x + mv.z * ax.z) * sg > 0.25f) return;
            float s = Mathf.Min(outv, 1.1f * dt) * sg;
            pos.x -= ax.x * s; pos.z -= ax.z * s;
        }

        void onLand(bool isSquid)
        {
            float speed = Mathf.Max(0, -vel.y);
            landSpeed = speed;
            landT = 0;
            if (speed > PLAYER.hardLandSpeed) hardLand = clamp((speed - PLAYER.hardLandSpeed) / 6 + 0.5f, 0, 1);
            if (speed > 3)
            {
                character.trigger("land", speed);
                grounded = true; surface();
                bool swim = isSquid && groundTeam == 1;
                if (specialActive == null && (isLocal || nearCamera())) G.audio?.play(swim ? "swim_splash" : "land", new AudioOpts { pos = pos, volume = clamp(speed / 14, 0.25f, 0.9f) });
                emit("actor:land", new Ev { actor = this, speed = speed, surface = groundTeam, pos = pos });
                if (isLocal && speed > 7) rumble(this, clamp((speed - 7) / 14, 0.05f, 0.5f), clamp(speed / 22, 0.1f, 0.55f), 70 + Mathf.Min(90, speed * 4));
            }
        }

        void spawnBarrier()
        {
            var pad = G.level.spawnPads[enemyTeam];
            float R = G.level.spawnBarrier;
            float dx = pos.x - pad.x, dz = pos.z - pad.z;
            float d = Mathf.Sqrt(dx * dx + dz * dz);
            if (d < R && pos.y > pad.y - 1.0f)
            {
                float k = (R - d) / Mathf.Max(d, 0.01f);
                pos.x += dx * k; pos.z += dz * k;
                float vn = (vel.x * dx + vel.z * dz) / Mathf.Max(d, 0.01f);
                if (vn < 0) { vel.x -= (dx / d) * vn * 1.6f; vel.z -= (dz / d) * vn * 1.6f; }
            }
        }

        // ------------------------------------------------------------------ wall climb
        void updateClimb(float dt, bool isSquid)
        {
            climbExit = Mathf.Max(0, climbExit - dt);
            if (!isSquid || climbExit > 0) { if (climbing) setClimb(false); return; }
            var mv = intent.move;
            float mh = Mathf.Sqrt(mv.x * mv.x + mv.z * mv.z), mag = Mathf.Min(1, mh);
            Vector3 dir;
            if (climbing) dir = new Vector3(-wallN.x, 0, -wallN.z);
            else { if (mh < 0.2f) return; dir = new Vector3(mv.x, 0, mv.z); }
            if (dir.sqrMagnitude < 1e-6f) return;
            dir.Normalize();
            var h = G.physics.raycast(pos + Vector3.up * 0.3f, dir, PLAYER.radius + 0.35f, wallHit);
            bool isWall = h.hit && Mathf.Abs(h.normal.y) < 0.5f;
            bool inked = isWall && h.face >= 0 && G.paint.sample(h.face, h.u, h.v) - 1 == team;
            float into = isWall && mh > 0.01f ? -(mv.x * h.normal.x + mv.z * h.normal.z) / mh : 0;
            if (!climbing)
            {
                if (!(inked && into > PLAYER.climbAttachDot)) return;
                wallN = h.normal;
                climbV = Mathf.Max(0, vel.y);
                setClimb(true);
            }
            if (!h.hit) { ledgePop(dir); return; }                       // over the top
            if (!inked)
            {                                                            // ink ran out under us: let go
                setClimb(false); vel.y = Mathf.Min(vel.y, 1.5f);
                vel.x += h.normal.x * 1.2f; vel.z += h.normal.z * 1.2f; climbExit = 0.2f;
                return;
            }
            if (into < PLAYER.climbDetachDot)
            {                                                            // pushing away: hop off the wall
                setClimb(false);
                vel = new Vector3(h.normal.x * 3.2f, 3.2f, h.normal.z * 3.2f); climbExit = 0.3f;
                return;
            }
            wallN = h.normal;
            // is there more of our ink above? (stop at the ink line instead of flying past it)
            var hu = G.physics.raycast(pos + Vector3.up * 0.85f, dir, PLAYER.radius + 0.45f, _ledgeHit);
            bool capped = hu.hit && Mathf.Abs(hu.normal.y) < 0.5f && !(hu.face >= 0 && G.paint.sample(hu.face, hu.u, hu.v) - 1 == team);
            // climb speed eases in, clings when the stick is neutral, and eases toward the ledge-pop speed near the top
            float want = capped ? 0 : PLAYER.climbSpeed * clamp(into, 0, 1) * mag;
            if (!hu.hit && want > 0) want = Mathf.Min(want, Mathf.Sqrt(2 * PLAYER.gravity * PLAYER.apexGravityMul * (PLAYER.ledgePopClear + 0.3f)));
            float a = PLAYER.climbAccel * dt * (want > climbV ? 1 : 1.6f);
            climbV = climbV < want ? Mathf.Min(want, climbV + a) : Mathf.Max(want, climbV - a);
            vel.y = climbV;
            // hug the wall, slide sideways along it
            var n = h.normal;
            var side = new Vector3(mv.x, 0, mv.z);
            side -= n * Vector3.Dot(side, n);
            vel.x = side.x * PLAYER.climbSideSpeed - n.x * 1.2f;
            vel.z = side.z * PLAYER.climbSideSpeed - n.z * 1.2f;
            anim.wallNormal = n;
        }

        void setClimb(bool on)
        {
            if (climbing == on) return;
            climbing = on;
            if (!on) climbV = 0;
            emit("actor:climb", new Ev { actor = this, on = on });
        }

        // Reached the top of the wall: a controlled hop that clears the ledge by ledgePopClear and carries onto it.
        void ledgePop(Vector3 dir)
        {
            var o = pos + dir * (PLAYER.radius + 0.32f); o.y = pos.y + 1.4f;
            var t = G.physics.raycast(o, DOWN, 2.0f, _ledgeHit, true);
            float top = t.hit && t.normal.y > Physics.WALKABLE ? t.point.y : pos.y + 0.3f;
            float g = PLAYER.gravity * PLAYER.apexGravityMul;
            float rise = Mathf.Max(0.25f, top + PLAYER.ledgePopClear - pos.y);
            float cv = climbV;             // (read before setClimb clears it) — keeps the vertical speed continuous
            setClimb(false);
            vel.y = Mathf.Max(Mathf.Sqrt(2 * g * rise), Mathf.Min(cv, 6.5f));
            vel.x = dir.x * PLAYER.ledgePopCarry; vel.z = dir.z * PLAYER.ledgePopCarry;
            climbExit = 0.3f;
            grounded = false;
            G.fx?.burst(pos + Vector3.up * 0.3f, Vector3.up, color, new FxOpts { count = 7, speed = 2.6f, size = 0.07f });
            if (isLocal || nearCamera()) G.audio?.play("swim_splash", new AudioOpts { pos = pos, volume = 0.45f });
        }

        // ------------------------------------------------------------------ super jump
        // Where a teammate super jumping to this actor lands: here — or, while this actor is itself in a super jump's
        // flight, where that jump comes down (never a point up in the sky)
        public Vector3 jumpAnchor()
        {
            var s = specialActive;
            if (s != null && s.jumpBack && s.origin.HasValue) return s.origin.Value;
            var j = superJumpState;
            return j != null && j.phase == "flight" ? j.to : pos;
        }
        public bool canSuperJump() => alive && superJumpState == null && specialActive == null && (G.match?.playing() ?? false);

        // Launch toward an ally (Actor) or a fixed point (Vector3). Charge in place as a glowing squid, then arc through
        // the sky. instant: skip the ~0.75 s crouch charge.
        public bool superJump(object target, bool instant = false, bool home = false)
        {
            if (!canSuperJump()) return false;
            superJumpState = new SuperJumpState { phase = "charge", t = instant ? 0.75f : 0, target = target, home = home };
            form = "squid";
            setClimb(false);
            weaponRunner.reset();
            G.audio?.play("super_jump", new AudioOpts { pos = isLocal ? (Vector3?)null : pos, volume = isLocal ? 0.9f : 0.6f });
            emit("superjump", new Ev { actor = this, phase = "charge" });
            return true;
        }

        void updateSuperJump(float dt)
        {
            var s = superJumpState;
            s.t += dt;
            if (s.phase == "charge")
            {
                vel = Vector3.zero;
                form = "squid";
                probeGround();
                if (s.t > 0.75f)
                {
                    var tgt = s.target as Actor;
                    if (tgt != null && !tgt.alive) { superJumpState = null; return; }
                    s.from = pos;
                    if (tgt != null)
                    {
                        // land just short of the teammate (on their side facing us) instead of on top of them
                        var tp = tgt.jumpAnchor();
                        s.to = tp;
                        float dx = pos.x - tp.x, dz = pos.z - tp.z, d = Mathf.Sqrt(dx * dx + dz * dz); if (d == 0) d = 1;
                        var probe = new Vector3(tp.x + (dx / d) * 1.1f, tp.y + 0.6f, tp.z + (dz / d) * 1.1f);
                        var g = G.physics.raycast(probe, DOWN, 2.5f, groundHit);
                        if (g.hit && g.normal.y > 0.6f && !G.level.pointInside(g.point + Vector3.up * 0.5f, 0.3f)) s.to = g.point;
                    }
                    else s.to = (Vector3)s.target;
                    s.phase = "flight"; s.t = 0;
                    s.dur = 1.15f + Mathf.Min(0.6f, Vector3.Distance(s.from, s.to) / 80);
                    invuln = Mathf.Max(invuln, s.dur + 0.2f);
                    G.fx?.burst(pos, Vector3.up, color, new FxOpts { count = 16, speed = 6, size = 0.1f });
                    rumble(this, 0.35f, 0.5f, 140);
                    emit("superjump", new Ev { actor = this, phase = "flight", to = s.to });
                }
                return;
            }
            if (s.phase == "flight")
            {
                float k = Mathf.Min(1, s.t / s.dur);
                // horizontal: ease-in-out; vertical: a quick launch and a steeper, faster drop onto the target
                float e = k < 0.5f ? 2 * k * k : 1 - Mathf.Pow(-2 * k + 2, 2) / 2;
                float apex = 11 + Vector3.Distance(s.from, s.to) * 0.08f;
                float kv = Mathf.Pow(k, 0.86f);
                var p = Vector3.LerpUnclamped(s.from, s.to, e);
                p.y += Mathf.Sin(Mathf.PI * kv) * apex;
                vel = (p - pos) * (1 / Mathf.Max(dt, 1e-3f));
                pos = p;
                form = k > 0.82f ? "kid" : "squid";
                s.marker += dt;
                if (s.marker > 0.12f) { s.marker = 0; G.fx?.ring(s.to + Vector3.up * 0.05f, Vector3.up, color, new FxOpts { radius = 1.6f, life = 0.5f }); }
                if (k >= 1)
                {
                    superJumpState = null;
                    vel = new Vector3(0, -12, 0);          // lands through onLand (squash, land event, audio)
                    form = "kid"; kidT = 0.05f;
                    grounded = false;
                    resolve(false, pos.y + 0.4f, false);
                    if (!grounded) { grounded = true; resolve(false, pos.y, true); if (!grounded) vel.y = -6; }
                    addTurf(G.paint.splat(pos + Vector3.up * 0.3f, 1.4f, team, new SplatOpts { seed = random() }));
                    G.fx?.burst(pos, Vector3.up, color, new FxOpts { count = 14, speed = 5, size = 0.09f });
                    if (isLocal) emit("shake", new Ev { amount = 0.35f });
                    rumble(this, 0.55f, 0.45f, 170);
                    emit("superjump:land", new Ev { actor = this, pos = pos });
                }
            }
        }

        // ------------------------------------------------------------------ specials
        void startSpecial()
        {
            string id = SpecialSystem.resolve(specialId);   // its own special, or Tidal Slam while that kind isn't ported
            special = 0;
            stats.specials++;
            ink = PLAYER.inkMax;          // every special refills your tank
            form = "kid";
            setClimb(false);
            weaponRunner.endDodge();      // a twins roll (or its planted lock) never carries into the special
            emit("special:use", new Ev { actor = this, id = id });
            G.audio?.play("special_activate", new AudioOpts { pos = isLocal ? (Vector3?)null : pos, volume = isLocal ? 1 : 0.7f });
            rumble(this, 0.25f, 0.45f, 120);
            if (id != "slam" && id != "storm") { G.specials.start(this, id); return; }
            if (id == "slam")
            {
                specialActive = new SpecialState { id = id, t = 0, phase = "rise", armor = true, body = true, startY = pos.y };
                vel = new Vector3(vel.x * 0.3f, 11.5f, vel.z * 0.3f);
                grounded = false;
                character.trigger("special_leap");
            }
            else
            {
                specialActive = new SpecialState { id = id, t = 0, phase = "throw", armor = false, body = true };
                character.trigger("throw");
                G.projectiles.throwStorm(this);
            }
        }

        void updateSpecial(float dt)
        {
            var s = specialActive;
            if (s.id != "slam" && s.id != "storm") { G.specials.body(this, s, dt); return; }
            s.t += dt;
            var sp = Config.SPECIALS[s.id];
            if (s.id == "storm")
            {
                // brief throw animation, then control returns
                if (s.t > 0.35f) specialActive = null;
                vel.x *= Mathf.Exp(-6 * dt); vel.z *= Mathf.Exp(-6 * dt);
                bool stick = grounded;
                if (!stick) vel.y -= PLAYER.gravity * dt; else vel.y = 0;
                float py0 = pos.y;
                pos += vel * dt;
                resolve(false, py0, stick);
                return;
            }
            // slam
            var mv = intent.move;
            if (s.phase == "rise")
            {
                vel.y -= PLAYER.gravity * 0.9f * dt;
                vel.x = damp(vel.x, mv.x * 2.5f, 6, dt); vel.z = damp(vel.z, mv.z * 2.5f, 6, dt);
                if (s.t > sp.rise) { s.phase = "hang"; s.t = 0; vel = new Vector3(0, 0.6f, 0); }
            }
            else if (s.phase == "hang")
            {
                vel.y = 0.4f;
                if (s.t > sp.hang) { s.phase = "fall"; s.t = 0; vel = new Vector3(0, -34, 0); character.trigger("special_slam"); }
            }
            else if (s.phase == "fall") vel.y = -34;
            float py = pos.y;
            pos += vel * dt;
            grounded = false;
            resolve(false, py, false);
            if (s.phase == "fall" && (grounded || s.t > 1.2f))
            {
                slamImpact(sp);
                specialActive = null;
                invuln = 0.3f;
            }
        }

        void slamImpact(SpecialDef sp)
        {
            var c = pos;
            float area = 0;
            area += G.paint.splat(c + Vector3.up * 0.3f, sp.radius * 0.72f, team, new SplatOpts { seed = random() });
            for (int i = 0; i < 9; i++)
            {
                float a = (i / 9f) * Mathf.PI * 2 + random() * 0.3f;
                float r = sp.radius * (0.55f + random() * 0.3f);
                area += G.paint.splat(new Vector3(c.x + Mathf.Cos(a) * r, c.y + 0.6f, c.z + Mathf.Sin(a) * r), 1.1f + random() * 0.6f, team, new SplatOpts { seed = random() });
            }
            addTurfNoSpecial(area);
            G.fx?.explosion(c + Vector3.up * 0.3f, color, sp.radius);
            G.audio?.play("special_slam", new AudioOpts { pos = c });
            emit("shake", new Ev { pos = c, amount = 1.0f });
            emit("special:slam", new Ev { actor = this, pos = c, radius = sp.radius });
            rumble(this, 0.9f, 0.7f, 320);
            foreach (var a in G.actors)
            {
                if (a.team == team || !a.alive) continue;
                float d = Vector3.Distance(a.pos, c);
                if (d > sp.radius) continue;
                float dmg = d < sp.killRadius ? sp.damageMax : sp.damageMin + (sp.damageMax - sp.damageMin) * 0.3f * (1 - (d - sp.killRadius) / (sp.radius - sp.killRadius));
                if (G.physics.los(c + Vector3.up * 0.8f, a.pos + Vector3.up * 0.8f)) G.projectiles.applyHit(this, a, dmg, "slam");
            }
        }

        // ------------------------------------------------------------------ facing
        // Critically-damped angular spring with a rate cap: small corrections are quick, big turns sweep with a smooth
        // ease-in/out instead of an exponential snap. yawVel doubles as the animation's turnRate.
        void face(float dt, bool isSquid)
        {
            var a = anim;
            var mv = intent.move;
            float mh = Mathf.Sqrt(mv.x * mv.x + mv.z * mv.z);
            float hs = Mathf.Sqrt(vel.x * vel.x + vel.z * vel.z);
            fireFacing = Mathf.Max(0, fireFacing - dt);
            float? target = null; float omega = PLAYER.faceOmega, maxRate = PLAYER.faceMaxRate, maxAcc = PLAYER.faceMaxAcc;
            var spx = specialActive;
            if (spx != null && spx.faceYaw != null) { target = spx.faceYaw; omega = 30; maxRate = 30; maxAcc = 400; }
            else if ((spx != null && (spx.body || spx.faceMove)) || superJumpState != null)
            {
                if (hs > 0.6f) target = Mathf.Atan2(vel.x, vel.z);
            }
            else if (weaponRunner.firingPose() || fireFacing > 0 || intent.sub || (spx != null && spx.aimFace))
            {
                target = aimYaw; omega = PLAYER.aimFaceOmega; maxRate = PLAYER.aimFaceMaxRate; maxAcc = PLAYER.aimFaceMaxAcc;
            }
            else if (climbing) { target = Mathf.Atan2(-wallN.x, -wallN.z); omega = 26; maxRate = 18; }
            else
            {
                if (mh > 0.2f) target = Mathf.Atan2(mv.x, mv.z);
                else if (hs > 0.6f) target = Mathf.Atan2(vel.x, vel.z);
                if (isSquid) { omega = PLAYER.squidFaceOmega; maxRate = submerged ? PLAYER.swimFaceMaxRate : PLAYER.squidFaceMaxRate; maxAcc = PLAYER.squidFaceMaxAcc; }
            }
            // feed-forward the target's own angular velocity so the body tracks a smoothly moving target with no lag
            float targetRate = 0;
            if (target != null && _faceTarget != null)
            {
                float d = angleDiff(_faceTarget.Value, target.Value);
                if (Mathf.Abs(d) < 0.12f) targetRate = clamp(d / Mathf.Max(dt, 1e-4f), -maxRate, maxRate);
            }
            _faceTarget = target;
            float acc = target == null ? -2 * omega * yawVel : omega * omega * angleDiff(yaw, target.Value) + 2 * omega * (targetRate - yawVel);
            // angular acceleration is capped too: turns spin up over a few frames instead of snapping to full rate
            yawVel = clamp(yawVel + clamp(acc, -maxAcc, maxAcc) * dt, -maxRate, maxRate);
            if (Mathf.Abs(yawVel) < 1e-5f) yawVel = 0;
            yaw += yawVel * dt;
            if (yaw > Mathf.PI) yaw -= TAU; else if (yaw < -Mathf.PI) yaw += TAU;
            a.turnRate = yawVel;
        }

        // ------------------------------------------------------------------ visuals
        void finishFrame(float dt)
        {
            var a = anim;
            bool isSquid = form == "squid";
            face(dt, isSquid);
            float hs = Mathf.Sqrt(vel.x * vel.x + vel.z * vel.z);
            float cy = Mathf.Cos(yaw), sy = Mathf.Sin(yaw);
            a.speed = hs;
            float inv = 1 / Mathf.Max(hs, 0.001f);
            float lmx = hs > 0.1f ? (vel.x * cy - vel.z * sy) * inv : 0;
            float lmz = hs > 0.1f ? (vel.x * sy + vel.z * cy) * inv : 0;
            a.localMove = new Vector2(-lmx, lmz);
            a.grounded = grounded;
            a.vy = vel.y;
            a.aimPitch = aimPitch;
            a.firing = weaponRunner.firingPose() || (specialActive != null && specialActive.firing > 0);
            a.charge = weaponRunner.charge;
            a.rolling = weaponRunner.rolling;
            a.subAim = weaponRunner.aimingSub || (specialActive != null && specialActive.raise);      // bomb cocked / orb held up
            a.form = !isSquid ? "kid" : climbing ? "climb" : submerged ? "swim" : "squid";
            if (specialActive != null && (specialActive.body || specialActive.noSquid)) a.form = "kid";
            a.specialId = specialActive?.id;
            a.ink = ink / PLAYER.inkMax;
            a.lowInk = ink < 18;
            a.special = specialFrac();
            a.invuln = invuln > 0;
            a.hp = clamp(hp / PLAYER.hp, 0, 1);
            a.inEnemyInk = onEnemy;
            a.surface = grounded ? groundTeam : 0;
            // visual step smoothing (critically damped, ~0.12 s)
            const float w = 24;
            float acc = -w * w * smoothY - 2 * w * smoothYV;
            smoothYV += acc * dt; smoothY += smoothYV * dt;
            if (Mathf.Abs(smoothY) > 0.7f) smoothY = Mathf.Sign(smoothY) * 0.7f;
            if (Mathf.Abs(smoothY) < 1e-4f && Mathf.Abs(smoothYV) < 1e-3f) { smoothY = 0; smoothYV = 0; }
            var ch = character;
            ch.root.localPosition = new Vector3(pos.x, pos.y + smoothY + (specialActive?.seat ?? 0), pos.z);
            ch.root.localRotation = Quaternion.AngleAxis(yaw * Mathf.Rad2Deg, Vector3.up);
            ch.setHurt(Mathf.Max(hurtFlash, 1 - hp / PLAYER.hp) * (hp < PLAYER.hp ? 1 : 0), G.teamColors[enemyTeam]);
            ch.update(dt, a);
            events(a);
            // swim wake
            if (a.form == "swim" && hs > 2 && G.fx != null)
            {
                _wakeT += dt;
                if (_wakeT > 0.05f) { _wakeT = 0; G.fx.wake(pos, new Vector3(vel.x, 0, vel.z).normalized, color, hs); }
            }
        }

        // state-change events for the FX / HUD / audio streams (docs/EVENTS.md)
        void events(AnimState a)
        {
            string kidForm = form == "squid" ? "squid" : "kid";
            if (kidForm != _evForm) { _evForm = kidForm; emit("actor:form", new Ev { actor = this, form = kidForm, surface = groundTeam }); }
            bool sub = a.form == "swim" || a.form == "climb";
            if (sub != _evSub)
            {
                _evSub = sub;
                float speed = Mathf.Sqrt(vel.x * vel.x + vel.z * vel.z);
                emit(sub ? "actor:dive" : "actor:emerge", new Ev { actor = this, pos = pos, speed = speed });
            }
            if (onEnemy != _evEnemy) { _evEnemy = onEnemy; emit("actor:enemyInk", new Ev { actor = this, on = _evEnemy }); }
        }

        // Visual (smoothed) world position of the feet — cameras and name tags should follow this, not the raw pos.
        public Vector3 visualPos() => new Vector3(pos.x, pos.y + smoothY, pos.z);

        public void dispose() => character.dispose();
    }
}
