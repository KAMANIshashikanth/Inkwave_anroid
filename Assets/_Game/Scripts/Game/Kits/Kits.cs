// Port of src/game/kits/registry.js — the kit weapons (a main weapon or a sub that lives in its own module, kits/*.js)
// plug into WeaponRunner / Projectiles / the bot brain through these hooks. A main kit registers under its weapon kind;
// every hook is optional, like the JS object's methods. Kits not ported yet are simply absent: the runner falls back to
// the Spritzer for their weapon kind. Online hooks (netState / netApply / ghost / netHurt) are not ported (offline only).
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Inkwave
{
    public abstract class MainKit
    {
        public virtual void reset(WeaponRunner r) { }
        public virtual bool busy(WeaponRunner r) => false;
        public virtual bool firingPose(WeaponRunner r) => false;
        /** 0 = no say (the runner's default) */
        public virtual float moveSpeed(WeaponRunner r, WeaponDef w) => 0;
        /** null = no say */
        public virtual float? spreadDeg(WeaponRunner r, WeaponDef w) => null;
        public abstract void update(WeaponRunner r, float dt, WeaponInput inp, WeaponDef w);
        /** jump pressed while grounded in kid form: true swallows it (Sponge Mitts: fire + jump charges a leap) */
        public virtual bool jump(WeaponRunner r, Intent intent) => false;
        /** Projectiles.clear: drop the kit's own world objects */
        public virtual void clear() { }
        /** Projectiles.update: step the kit's own world objects */
        public virtual void tick(float dt) { }
        /** a shot segment prev→pos hits something this kit owns (e.g. a shield) */
        public virtual bool blockShot(Vector3 prev, Vector3 pos, int team, float dmg) => false;
        /** distance along a beam to the first thing this kit owns that stops it (or len) */
        public virtual float blockRay(Vector3 from, Vector3 dir, float len, int team, float dmg) => len;
        /** scale damage the wielder takes (actor.damage; e.g. the mitts' leap armour); ≤ 0 cancels the hit */
        public virtual float damageTaken(WeaponRunner r, float amount, Actor attacker, string source) => amount;
        /** push a descriptor for every shield up this frame (bots flank held ones and steer round launched ones) */
        public virtual void shields(List<ShieldInfo> o) { }
        public KitBot bot;
    }

    /** The bot brain's hooks for a kit weapon (registry.js `bot`); a null delegate = no hook. */
    public class KitBot
    {
        public float? paintPitch;
        public bool melee, charges, @long, painter;
        /** a fight frame with the aim on the target: the trigger (may steer ctx.move) */
        public Func<BotBrain, BotCtx, bool> fight;
        /** a paint frame: the trigger */
        public Func<BotBrain, BotCtx, bool> paint;
        /** every fight / retreat frame after the defaults: owns fire, hold, swim and move */
        public Action<BotBrain, BotCtx> tactics;
        /** veto the low-HP retreat (keep fighting when the kill is close) */
        public Func<BotBrain, bool> stayIn;
        /** added to that enemy's target score in perceive */
        public Func<BotBrain, Actor, float, float> targetBias;
    }

    /** What a kit bot hook sees (bots.js passes { a, w, dist, range, dt, it, move, target, … }). `move` is read back. */
    public class BotCtx
    {
        public Actor a, target; public WeaponDef w; public Intent it;
        public float dist, range, dt, inkFrac;
        public Vector3 move;
        public bool needPaint, wantMove, visible, aimed, canFire, retreat;
    }

    /** One shield up this frame (registry.js `shields`): stable per shield, the getters read its live state. */
    public abstract class ShieldInfo
    {
        public string kind; public bool held, blocksActors; public Actor owner;
        public float hpMax, halfW, planeOff, speed;
        /** disc centre / facing (launched: travel direction) / radius */
        public abstract Vector3 C { get; }
        public abstract Vector3 N { get; }
        public abstract float R { get; }
        /** launched only: foot position */
        public abstract Vector3 pos { get; }
        public abstract int team { get; }
        public abstract float hp { get; }
        public abstract bool live { get; }
        /** launched: seconds until it fades */
        public abstract float left { get; }
    }

    /** A sub weapon that lives in its own module (registry.js SUB_KITS[kind]); every hook but use is optional. */
    public abstract class SubKit
    {
        /** the throw / placement on release (SubSystem.use) */
        public abstract void use(SubSystem subs, Actor a, SubDef sub);
        /** while the sub button is held (charge-up) */
        public virtual void hold(WeaponRunner r, float dt, WeaponInput inp, SubDef sub) { }
        /** refuse the throw ("Can't use" — e.g. one already out); the runner emits 'sub:cantuse' */
        public virtual bool blocked(Actor a, SubDef sub) => false;
        public virtual void tick(float dt) { }
        public virtual void clear() { }
        /** shoot-able bombs */
        public virtual bool blockShot(Vector3 prev, Vector3 pos, int team, float dmg) => false;
        /** charger beams vs shoot-able bombs */
        public virtual float blockRay(Vector3 from, Vector3 dir, float len, int team, float dmg) => len;
        /** a blast went off (G.subs.damageArea): kit objects of other teams caught in it */
        public virtual void damageArea(Vector3 c, float radius, float dmg, int team) { }
        /** no bomb-arc preview while held (the kit draws its own aim guide) */
        public virtual bool noArc => false;
        /** push a descriptor for every live object of this kind that hunts / hurts foes (bots shoot them down or evade them) */
        public virtual void threats(List<ThreatInfo> o) { }
        public SubKitBot bot;
    }

    /** One device that hunts / hurts foes (registry.js `threats`): stable per object, the getters read its live state.
     *  pos: live (feet for walkers, centre for flyers); aimY: the hit centre above pos; radius: its blast; trigger: its
     *  contact reach; ground: walks on the ground. Kind extras: waddle senseRadius / left, torpedo lockRange. */
    public abstract class ThreatInfo
    {
        public string kind; public int team; public Actor owner;
        public float aimY, radius, trigger, speed, senseRadius, lockRange; public bool ground;
        public abstract Vector3 pos { get; }
        public abstract Vector3 vel { get; }
        public abstract bool live { get; }
        public abstract string state { get; }
        public abstract float hp { get; }
        public abstract bool shootable { get; }
        public abstract bool locked { get; }
        /** the actor it tracks, or null */
        public abstract Actor target { get; }
        /** seconds of chase it has left (waddle); NaN = no say */
        public virtual float left => float.NaN;
    }

    /** The bot brain's hooks for a kit sub (registry.js SUB_KITS[k].bot); a null delegate = no hook. */
    public class SubKitBot
    {
        /** mid-duel: throw now? (dist to the target) */
        public System.Func<BotBrain, float, bool> fight;
        /** while painting: throw now? */
        public System.Func<BotBrain, bool> paint;
    }

    public static class Kits
    {
        /** main-weapon kits by weapon kind (kits/registry.js MAIN_KITS) */
        public static readonly Dictionary<string, MainKit> MAIN = new Dictionary<string, MainKit>();
        public static MainKit main(string kind) => kind != null && MAIN.TryGetValue(kind, out var k) ? k : null;
        /** sub-weapon kits by sub kind (kits/registry.js SUB_KITS) */
        public static readonly Dictionary<string, SubKit> SUB = new Dictionary<string, SubKit>();
        public static SubKit sub(string kind) => kind != null && SUB.TryGetValue(kind, out var k) ? k : null;

        /** three's RingGeometry(r0, r1, n): a flat ring in the XY plane facing +Z (kit aim guides) */
        public static Mesh ringMesh(float r0, float r1, int n)
        {
            var v = new Vector3[n * 2]; var idx = new int[n * 6];
            for (int i = 0; i < n; i++)
            {
                float a = i * Mathf.PI * 2 / n;
                v[i * 2] = new Vector3(Mathf.Cos(a) * r0, Mathf.Sin(a) * r0, 0); v[i * 2 + 1] = new Vector3(Mathf.Cos(a) * r1, Mathf.Sin(a) * r1, 0);
                int j = (i + 1) % n, k = i * 6;
                idx[k] = i * 2; idx[k + 1] = i * 2 + 1; idx[k + 2] = j * 2; idx[k + 3] = j * 2; idx[k + 4] = i * 2 + 1; idx[k + 5] = j * 2 + 1;
            }
            var m = new Mesh { name = "ring" }; m.vertices = v; m.triangles = idx; m.RecalculateBounds();
            return m;
        }

        // kits/index.js: every kit module registers itself once
        static bool _init;
        public static void init()
        {
            if (_init) return;
            _init = true;
            MAIN["brolly"] = new Brolly();
            WeaponModel.KIT_ANIM["brolly"] = BrollyModel.animate;
            MAIN["bow"] = new Bow();
            WeaponModel.KIT_ANIM["bow"] = BowModel.animate;
            MAIN["blade"] = new Blade();
            WeaponModel.KIT_ANIM["blade"] = BladeModel.animate;
            MAIN["mitts"] = new Mitts();
            WeaponModel.KIT_ANIM["mitts"] = MittsModel.animate;
            SUB["shaker"] = new Shaker(); BotBrain.THROWN.Add("shaker");
            SUB["tracer"] = new Tracer();
            SUB["boomerang"] = new Boomerang(); BotBrain.THROWN.Add("boomerang");
            SUB["waddle"] = new Waddle(); BotBrain.THROWN.Add("waddle");
            SUB["torpedo"] = new Torpedo(); BotBrain.THROWN.Add("torpedo");
        }
    }
}
