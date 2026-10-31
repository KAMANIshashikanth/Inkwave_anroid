// Port of specials.js IMPL.jetpack + ReturnMarker — Ink Jet: the pack replaces the ink tank on your back and you hover
// over the stage (a held height above whatever is below), steering with the stick, boosting with jump, firing blast
// shots from the jet gun (sp_jetgun) and inking the ground under the exhaust. When it ends you super jump back to the
// take-off point, which a Return marker shows to both teams until you land.
// The marker's badge is a team disc that greys out on the way home (the special's icon and the clock-sweep wipe come
// with the icon sprites).
using System;
using UnityEngine;
using static Inkwave.Ctx;

namespace Inkwave
{
    public class Jetpack : SpecialImpl
    {
        public class State { public float cd, paintT, fx, boostCd, boostT; public bool jumpWas; public Transform pack; public Vector3[] nozzles; public AudioLoop loop; }

        public override void start(SpecialSystem sys, Actor a, SpecialState s)
        {
            var st = new State { cd = 0.3f, jumpWas = true };
            s.data = st;
            s.body = true; s.origin = a.pos; s.jumpBack = true;
            s.marker = new ReturnMarker(a, a.pos); sys.world.Add(s.marker);
            a.grounded = false; a.vel.y = 7;
            sys.swapWeapon(a, "sp_jetgun");
            // the pack replaces the ink tank on the kid's back
            var d = SpecialProps.get("jetpack");
            if (a.character is Character ch && ch.tankGroup != null)
            {
                ch.hideTank(true);
                if (d != null) st.pack = SpecialSystem.propMesh(d, a.team, sys.glowMat(a.team), ch.tankGroup);
                else
                {
                    st.pack = SpecialSystem.part("pack", ch.tankGroup, SpecialSystem.sphere(), CharacterMats.ink(a.color), true).transform;
                    st.pack.localPosition = new Vector3(0, 0.1f, -0.05f); st.pack.localScale = new Vector3(0.14f, 0.18f, 0.08f);
                }
                st.nozzles = d != null && d.nozzles != null && d.nozzles.Length > 0 ? d.nozzles : new[] { new Vector3(-0.08f, -0.12f, -0.05f), new Vector3(0.08f, -0.12f, -0.05f) };
            }
            st.loop = SpecialSystem.hearable(a) ? G.audio?.loop("jet_loop", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = a.isLocal ? 0.55f : 0.4f }) : null;
        }
        static Vector3 nozzleAt(Actor a, Vector3 n) => a.character is Character ch && ch.tankGroup != null ? ThreeSpace.toThree(ch.tankGroup.TransformPoint(n)) : a.pos;
        public override void body(SpecialSystem sys, Actor a, SpecialState s, float dt)
        {
            var d = s.def; var st = (State)s.data; var mv = a.intent.move;
            // horizontal: steer toward the stick at hover speed
            float tvx = mv.x * d.speed, tvz = mv.z * d.speed;
            float ex = tvx - a.vel.x, ez = tvz - a.vel.z, el = Mathf.Sqrt(ex * ex + ez * ez), r = d.accel * dt;
            if (el <= r) { a.vel.x = tvx; a.vel.z = tvz; } else { a.vel.x += ex / el * r; a.vel.z += ez / el * r; }
            // vertical: hold a hover height over whatever is below (never lower than above the take-off point)
            Vector3 origin = s.origin.Value;
            float gy = G.level.groundHeight(a.pos.x, a.pos.z, a.pos.y + 0.5f);
            float floor = float.IsNegativeInfinity(gy) ? origin.y : Mathf.Max(gy, origin.y - 1);
            float wantY = floor + d.height + Mathf.Sin(s.t * 2.2f) * 0.12f;
            // jump = a boost: a burst of thrust upward, then the hover settles back down
            st.boostCd -= dt; st.boostT -= dt;
            bool jump = a.intent.jump;
            if (jump && !st.jumpWas && st.boostCd <= 0)
            {
                st.boostCd = d.boostGap; st.boostT = 0.35f;
                a.vel.y = d.boost;
                if (SpecialSystem.hearable(a)) SpecialSystem.play("zip_pull", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = 0.7f, pitch = 0.8f });
                if (SpecialSystem.near(a.pos) && st.nozzles != null) foreach (var n in st.nozzles) G.fx?.burst(nozzleAt(a, n), Vector3.down, a.color, new FxOpts { count = 8, speed = 7, size = 0.09f });
                Actor.rumble(a, 0.3f, 0.3f, 90);
            }
            st.jumpWas = jump;
            if (st.boostT <= 0)
            {
                float vy = clamp((wantY - a.pos.y) * 3, -5, 6);
                a.vel.y += (vy - a.vel.y) * Mathf.Min(1, dt * (a.pos.y > wantY + 1 ? 2.5f : 6));
            }
            else a.vel.y -= PLAYER.gravity * 0.35f * dt;
            float py = a.pos.y;
            a.pos += a.vel * dt;
            a.resolve(false, py, false);
            a.grounded = false;
            s.faceYaw = a.aimYaw;
            // fire
            st.cd -= dt;
            if (a.intent.fire && st.cd <= 0)
            {
                st.cd = d.interval; s.firing = 0.4f;
                Vector3 m = G.projectiles.muzzle(a);
                Vector3 dir = Projectiles.aimFrom(a, m);
                G.projectiles.fireCustom(a, m, dir, new FireOpts
                {
                    type = "blast", speed = d.projSpeed, damage = d.directDamage, range = d.range, weaponId = "jetpack",
                    burst = new BurstSpec { radius = d.splashRadius, splashRadius = d.splashRadius, dmgMax = d.splashMax, dmgMin = d.splashMin, paint = d.paintRadius },
                });
                a.character.trigger("shoot");
                if (SpecialSystem.hearable(a)) SpecialSystem.play("jet_fire", new AudioOpts { pos = a.isLocal ? (Vector3?)null : m, volume = a.isLocal ? 0.8f : 0.55f });
                if (a.isLocal) emit("recoil", new Ev { amount = 0.014f });
                Actor.rumble(a, 0.3f, 0.4f, 100);
            }
            // exhaust ink below
            st.paintT -= dt;
            if (st.paintT <= 0)
            {
                st.paintT = 0.28f;
                var g = SpecialSystem.groundBelow(a.pos, 9);
                if (g.HasValue) SpecialSystem.paint(a, g.Value + Vector3.up * 0.2f, 1.0f, a.team);
            }
            st.fx -= dt;
            if (st.fx <= 0 && SpecialSystem.near(a.pos, 30) && st.nozzles != null)
            {
                st.fx = 0.05f;
                foreach (var n in st.nozzles) G.fx?.burst(nozzleAt(a, n), Vector3.down, a.color, new FxOpts { count = 2, speed = 4, size = 0.07f, ring = false, mist = false });
            }
            st.loop?.set(pos: a.isLocal ? (Vector3?)null : a.pos);
        }
        public override void end(SpecialSystem sys, Actor a, SpecialState s, string reason)
        {
            var st = (State)s.data;
            st.loop?.stop(0.2f); st.loop = null;
            if (SpecialSystem.hearable(a) && a.alive) SpecialSystem.play("jet_end", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = 0.7f });
            if (st.pack != null) { destroy(st.pack.gameObject); st.pack = null; }
            if (a.character is Character ch) ch.hideTank(false);
            sys.restoreWeapon(a);
        }
    }

    // Where an Ink Jet / Zipline user took off: they super jump back here when the special ends, so it is marked for both
    // teams until they land or are splatted. Once they take off for home the badge greys out with their flight.
    public class ReturnMarker : SpecialWorld
    {
        static readonly Color GREY = new Color(0x9a / 255f, 0xa0 / 255f, 0xa6 / 255f);
        public readonly Vector3 pos; public bool back, done; public float fade = 1, wipeK;
        float t; readonly Color col;
        readonly Transform group, ring, ring2, pillar, badge; readonly Material ringMat, ring2Mat, pillarMat, badgeMat;
        readonly Action[] unsub;
        public ReturnMarker(Actor a, Vector3 pos)
        {
            kind = "return"; owner = a; team = a.team; this.pos = pos; col = a.color;
            group = ThreeSpace.create("returnMarker").transform; group.localPosition = pos;
            ringMat = SpecialSystem.tintMat(col, 0.8f); ring2Mat = SpecialSystem.tintMat(Color.white, 0.6f);
            ring = SpecialSystem.part("ring", group, SpecialSystem.ringGeo(), ringMat, false).transform;
            ring.localPosition = new Vector3(0, 0.05f, 0); ring.localScale = Vector3.one * 1.3f;
            ring2 = SpecialSystem.part("ring2", group, SpecialSystem.ringGeo(), ring2Mat, false).transform;
            ring2.localPosition = new Vector3(0, 0.06f, 0);
            pillarMat = SpecialSystem.swirlMat(col, 0.8f, 1, 0.55f, 0.04f);
            pillar = SpecialSystem.part("pillar", group, SpecialSystem.funnelGeo(), pillarMat, false).transform;
            pillar.localScale = new Vector3(0.45f, 5.5f, 0.45f);
            badgeMat = SpecialSystem.tintMat(col, 1);
            badge = SpecialSystem.part("badge", group, SpecialSystem.sphere(), badgeMat, false).transform;
            badge.localPosition = new Vector3(0, 2.7f, 0);
            unsub = new[]
            {
                on("superjump:land", e => { if (e.actor == (object)owner && back) { wipeK = 1; finish(true); } }),   // grey for the landing frame, then gone
                on("splatted", e => { if (e.victim == (object)owner) finish(); }),
            };
        }
        // the owner has taken off for home: the colour → grey wipe runs with their flight
        public void returning() { back = true; t = Mathf.Min(t, 1); }
        public void finish(bool now = false) { done = true; if (now) fade = 0.001f; }
        // how far through the jump home the owner is: 0 at take-off … 1 on touch-down
        float homeK()
        {
            var sj = owner.superJumpState;
            if (sj == null) return back ? 1 : 0;
            if (sj.phase != "flight") return 0;
            return Mathf.Min(1, sj.t / Mathf.Max(0.01f, sj.dur > 0 ? sj.dur : 1));
        }
        static void tint(Material m, Color c, float a) { var l = c.linear; m.SetVector(SpecialSystem.ID_Color, new Vector4(l.r, l.g, l.b, a)); }
        public override bool update(float dt)
        {
            t += dt;
            if (done) { fade -= dt * 3; if (fade <= 0) return false; }
            if (t > 30) done = true;          // safety net
            float p = 0.5f + 0.5f * Mathf.Sin(t * (back ? 12 : 4));
            Color c = col;
            if (back) { wipeK = homeK(); c = Color.Lerp(col, GREY, wipeK); pillarMat.SetVector(SpecialSystem.ID_Color, (Vector4)c.linear); }
            tint(ringMat, c, (0.45f + 0.4f * p) * fade);
            float k = (t * 0.8f) % 1;
            ring2.localScale = Vector3.one * (0.4f + k * 1.4f); tint(ring2Mat, Color.white, (1 - k) * 0.7f * fade);
            pillarMat.SetFloat(SpecialSystem.ID_Time, t);
            pillarMat.SetFloat(SpecialSystem.ID_Alpha, (back ? 0.9f : 0.55f) * fade);
            badge.localPosition = new Vector3(0, 2.7f + Mathf.Sin(t * 2) * 0.15f, 0);
            badge.localScale = Vector3.one * (0.4f * fade * (back ? 1 + 0.06f * p : 1));
            tint(badgeMat, c, fade);
            return true;
        }
        public override void dispose()
        {
            foreach (var u in unsub) u();
            destroy(group.gameObject); destroy(ringMat); destroy(ring2Mat); destroy(pillarMat); destroy(badgeMat);
        }
    }
}
