// Port of specials.js IMPL.crab + Shell — Crab Rig: you ride a squat armoured crab walker (standing in its hatch). Fire
// = the gatling, the sub button = a lobbed cannon shell; the hull turns slowly toward the aim and shots go where it
// points. Swim rolls it into a fast armoured ball (no shooting). It has its own hp: hits from the front and sides wear
// it down, hits from behind or above reach the rider (filterDamage → hurt). Ink-proof, no squid.
using UnityEngine;
using static Inkwave.Ctx;

namespace Inkwave
{
    public class Crab : SpecialImpl
    {
        public class Leg { public Transform g; public int i; }
        public class State
        {
            public float hp, hull, gcd, ccd = 0.4f, legT, rollAng, seatY = 0.95f, sndT, pingT; public bool roll, subWas;
            public Transform rig, hullG, ball, gunSpin; public Leg[] legs; public Vector3 gunMuzzle, cannonMuzzle;
            public AudioLoop loop;
        }

        public override void start(SpecialSystem sys, Actor a, SpecialState s)
        {
            var d = s.def;
            var st = new State { hp = d.hp, hull = a.yaw };
            s.data = st;
            s.noSquid = true; s.inkProof = true; s.speed = d.speed; s.faceYaw = a.yaw;
            a.form = "kid";
            a.hitR = 1.0f; a.hitH = 2.2f;
            a.character.hideWeapon = true;
            var pd = SpecialProps.get("crab");
            st.rig = ThreeSpace.create("crabRig").transform;
            st.hullG = ThreeSpace.create("hull", st.rig).transform;
            if (pd != null)
            {
                SpecialSystem.propMesh(pd, a.team, sys.glowMat(a.team), st.hullG);
                st.legs = new Leg[pd.legs?.Length ?? 0];
                for (int i = 0; i < st.legs.Length; i++)
                {
                    var L = pd.legs[i];
                    var g = ThreeSpace.create("leg" + i, st.hullG).transform; g.localPosition = L.at;
                    var m = SpecialSystem.partMesh(L.body, L.ink, a.team, g); m.localPosition = -L.at;
                    st.legs[i] = new Leg { g = g, i = i };
                }
                if (pd.gunBody != null || pd.gunInk != null) SpecialSystem.partMesh(pd.gunBody, pd.gunInk, a.team, st.hullG);
                if (pd.cannonBody != null || pd.cannonInk != null) SpecialSystem.partMesh(pd.cannonBody, pd.cannonInk, a.team, st.hullG);
                if (pd.gunSpin != null)
                {
                    st.gunSpin = ThreeSpace.create("gunSpin", st.hullG).transform; st.gunSpin.localPosition = pd.gunSpinAt;
                    var m = SpecialSystem.partMesh(pd.gunSpin, pd.gunSpinInk, a.team, st.gunSpin); m.localPosition = -pd.gunSpinAt;
                }
                st.gunMuzzle = pd.gunMuzzle ?? new Vector3(0, 0.7f, 1.0f);
                st.cannonMuzzle = pd.cannonMuzzle ?? new Vector3(0.5f, 1.1f, -0.2f);
                st.seatY = pd.seatAt.HasValue ? pd.seatAt.Value.y : 0.95f;
                if (pd.ballBody != null)
                {
                    float R = pd.ballR > 0 ? pd.ballR : 0.95f;
                    st.ball = ThreeSpace.create("ball", st.rig).transform; st.ball.localPosition = new Vector3(0, R, 0);
                    var m = SpecialSystem.partMesh(pd.ballBody, pd.ballInk, a.team, st.ball); m.localPosition = new Vector3(0, -R, 0);
                    st.ball.gameObject.SetActive(false);
                }
            }
            else
            {
                var box = SpecialSystem.part("hullBox", st.hullG, SpecialSystem.sphere(), CharacterMats.ink(a.color), true).transform;
                box.localPosition = new Vector3(0, 0.55f, 0); box.localScale = new Vector3(0.8f, 0.45f, 0.8f);
                st.gunMuzzle = new Vector3(0, 0.7f, 1.0f); st.cannonMuzzle = new Vector3(0.5f, 1.1f, -0.2f);
            }
            if (st.ball == null)
            {
                st.ball = SpecialSystem.part("ball", st.rig, SpecialSystem.sphere(), CharacterMats.ink(a.color), true).transform;
                st.ball.localScale = Vector3.one * 0.95f; st.ball.localPosition = new Vector3(0, 0.95f, 0); st.ball.gameObject.SetActive(false);
            }
            sys.add(st.rig.gameObject);
            st.loop = SpecialSystem.hearable(a) ? G.audio?.loop("crab_move", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = 0 }) : null;
            G.fx?.explosion(new Vector3(a.pos.x, a.pos.y + 0.6f, a.pos.z), a.color, 1.5f);
        }
        public override bool weapon(SpecialSystem sys, Actor a, SpecialState s, float dt, WeaponInput inp)
        {
            var d = s.def; var st = (State)s.data;
            // swim = roll into an armoured ball (fast, can't shoot)
            bool roll = a.intent.squid;
            if (roll != st.roll)
            {
                st.roll = roll;
                s.speed = roll ? d.rollSpeed : d.speed;
                st.loop?.stop(0.1f);
                st.loop = SpecialSystem.hearable(a) ? G.audio?.loop(roll ? "crab_roll" : "crab_move", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = 0 }) : null;
            }
            // the hull turns slowly toward the aim (shots go where the hull points, not where you look)
            float hs = Mathf.Sqrt(a.vel.x * a.vel.x + a.vel.z * a.vel.z);
            float want = roll ? (hs > 0.8f ? Mathf.Atan2(a.vel.x, a.vel.z) : st.hull) : a.aimYaw;
            float rate = (roll ? 8 : d.turnRate) * dt;
            st.hull += clamp(angleDiff(st.hull, want), -rate, rate);
            s.faceYaw = st.hull;
            if (roll) return true;
            st.gcd -= dt; st.ccd -= dt;
            float pitch = clamp(a.aimPitch, -0.4f, 0.5f), cp = Mathf.Cos(pitch);
            if (inp.fire)
            {
                s.firing = 0.3f;
                int guard = 0;
                while (st.gcd <= 0 && guard++ < 3)
                {
                    st.gcd += d.gunInterval;
                    Vector3 m = local(a, st, st.gunMuzzle);
                    float sp = (random() - 0.5f) * 2 * d.gunSpread * Mathf.Deg2Rad, sp2 = (random() - 0.5f) * d.gunSpread * Mathf.Deg2Rad;
                    Vector3 dir = new Vector3(Mathf.Sin(st.hull + sp) * cp, Mathf.Sin(pitch + sp2), Mathf.Cos(st.hull + sp) * cp);
                    G.projectiles.fireCustom(a, m, dir, new FireOpts { type = "shot", speed = d.gunSpeed, damage = d.gunDamage, range = d.gunRange, straight = 0.4f, radius = 0.55f, trailEvery = 1.6f, trailRadius = 0.35f, weaponId = "crab" });
                    if (SpecialSystem.hearable(a) && G.time - st.sndT > 0.06f) { st.sndT = G.time; SpecialSystem.play("crab_gatling", new AudioOpts { pos = a.isLocal ? (Vector3?)null : m, volume = a.isLocal ? 0.45f : 0.3f }); }
                }
            }
            else if (st.gcd < 0) st.gcd = 0;
            bool subPress = inp.sub && !st.subWas; st.subWas = inp.sub;
            if (subPress && st.ccd <= 0)
            {
                st.ccd = d.cannonGap;
                Vector3 m = local(a, st, st.cannonMuzzle);
                float lp = clamp(a.aimPitch + 0.45f, 0.1f, 1.0f);
                Vector3 vel = new Vector3(Mathf.Sin(st.hull) * Mathf.Cos(lp), Mathf.Sin(lp), Mathf.Cos(st.hull) * Mathf.Cos(lp)) * d.cannonSpeed;
                sys.world.Add(new Shell(a, m, vel));
                if (SpecialSystem.hearable(a)) SpecialSystem.play("crab_cannon", new AudioOpts { pos = a.isLocal ? (Vector3?)null : m, volume = 0.9f });
                emit("shake", new Ev { pos = a.pos, amount = 0.25f });
                Actor.rumble(a, 0.5f, 0.4f, 140);
            }
            return true;
        }
        // a point in the hull's frame → world
        static Vector3 local(Actor a, State st, Vector3 p)
        {
            float c = Mathf.Cos(st.hull), sn = Mathf.Sin(st.hull);
            return new Vector3(a.pos.x + p.x * c + p.z * sn, a.pos.y + p.y, a.pos.z - p.x * sn + p.z * c);
        }
        public override void tick(SpecialSystem sys, Actor a, SpecialState s, float dt)
        {
            var st = (State)s.data;
            float hs = Mathf.Sqrt(a.vel.x * a.vel.x + a.vel.z * a.vel.z);
            st.rig.localPosition = new Vector3(a.pos.x, a.pos.y + a.smoothY, a.pos.z);
            st.hullG.localRotation = Quaternion.AngleAxis(st.hull * Mathf.Rad2Deg, Vector3.up);
            st.hullG.gameObject.SetActive(!st.roll);
            st.ball.gameObject.SetActive(st.roll);
            a.character.setVisible(!st.roll);
            s.seat = st.roll ? 0 : st.seatY;
            if (st.roll)
            {
                st.rollAng += (hs / 0.95f) * dt;
                st.ball.localRotation = ThreeSpace.eulerYXZ(st.rollAng, Mathf.Atan2(a.vel.x, a.vel.z), 0);
                if (a.grounded && hs > 2) { st.legT += dt; if (st.legT > 0.08f) { st.legT = 0; SpecialSystem.paint(a, a.pos + Vector3.up * 0.2f, 0.9f, a.team); } }
            }
            else
            {
                st.legT += dt * (1 + hs * 2.2f);
                if (st.legs != null) foreach (var L in st.legs) L.g.localRotation = Quaternion.AngleAxis(Mathf.Sin(st.legT * 5 + L.i * 1.7f) * 0.28f * Mathf.Min(1, hs / 1.5f) * Mathf.Rad2Deg, Vector3.right);
                if (st.gunSpin != null) st.gunSpin.localRotation *= Quaternion.AngleAxis(dt * (s.firing > 0 ? 40 : 2) * Mathf.Rad2Deg, Vector3.forward);
            }
            st.loop?.set(Mathf.Min(1, hs / (st.roll ? 6 : 2)) * (a.isLocal ? 0.5f : 0.35f), 0.8f + hs * 0.08f, a.isLocal ? (Vector3?)null : a.pos);
            if (st.hp <= 0) sys.end(a, "broken");
        }
        // the rig soaks hits from the front and sides; from behind or above they reach the rider
        public override float? filterDamage(SpecialSystem sys, Actor a, SpecialState s, float amount, Actor attacker, string source)
        {
            var d = s.def; var st = (State)s.data;
            bool toRider = false;
            if (!st.roll && attacker != null && attacker != a)
            {
                float rx = attacker.pos.x - a.pos.x, rz = attacker.pos.z - a.pos.z, rl = Mathf.Sqrt(rx * rx + rz * rz); if (rl == 0) rl = 1;
                float front = (rx * Mathf.Sin(st.hull) + rz * Mathf.Cos(st.hull)) / rl;
                toRider = front < -0.45f || attacker.pos.y > a.pos.y + 2.4f;     // from behind or from above
            }
            if (toRider) return amount;
            st.hp -= amount * (st.roll ? d.rollArmor : 1);
            sys.hitFlash(a);
            if (SpecialSystem.near(a.pos) && G.time - st.pingT > 0.08f) { st.pingT = G.time; SpecialSystem.play("crab_hit", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = 0.5f }); }
            return 0;
        }
        public override void end(SpecialSystem sys, Actor a, SpecialState s, string reason)
        {
            var st = (State)s.data;
            st.loop?.stop(0.15f); st.loop = null;
            sys.remove(st.rig.gameObject);
            a.hitR = null; a.hitH = null; s.seat = 0;
            a.character.hideWeapon = false;
            if (a.alive) a.character.setVisible(true);
            if (reason == "broken" || reason == "time")
            {
                if (SpecialSystem.near(a.pos)) SpecialSystem.play("crab_break", new AudioOpts { pos = a.isLocal ? (Vector3?)null : a.pos, volume = 0.8f });
                G.fx?.explosion(new Vector3(a.pos.x, a.pos.y + 0.8f, a.pos.z), a.color, 1.8f);
            }
            if (a.alive) { a.vel.y = 5; a.grounded = false; }
        }
    }

    // a Crab Rig cannon shell: a lobbed ink ball that bursts on whatever it hits
    public class Shell : SpecialWorld
    {
        Vector3 pos, prev, vel; float t;
        readonly Transform mesh;
        static readonly Hit _hit = new Hit();
        public Shell(Actor a, Vector3 from, Vector3 vel)
        {
            kind = "shell"; owner = a; team = a.team;
            pos = from; prev = from; this.vel = vel;
            mesh = SpecialSystem.part("shell", null, SpecialSystem.sphere(), CharacterMats.ink(a.color), true).transform;
            mesh.localScale = Vector3.one * 0.28f; mesh.localPosition = pos;
        }
        public override bool update(float dt)
        {
            t += dt;
            prev = pos;
            vel.y -= 20 * dt;
            pos += vel * dt;
            Vector3? at = null;
            var hit = G.physics.segment(prev, pos, _hit, true);
            if (hit.hit) at = hit.point + hit.normal * 0.25f;
            if (!at.HasValue)
                foreach (var e in G.actors)
                {
                    if (e.team == team || !e.alive) continue;
                    float hr = e.hitR ?? PLAYER.radius;
                    var (dist, tt) = Physics.segmentCapsuleDist(prev, pos, e.pos, hr, e.hitH ?? PLAYER.height);
                    if (dist < hr + 0.25f) { at = Vector3.Lerp(prev, pos, tt); break; }
                }
            if (!at.HasValue && (G.specials?.shotHit(prev, pos, team, 150, owner) ?? false)) at = pos;
            mesh.localPosition = pos;
            if (at.HasValue)
            {
                var d = Config.SPECIALS["crab"]; Vector3 c = at.Value;
                SpecialSystem.paint(owner, c + Vector3.up * 0.1f, d.cannonRadius * 0.75f, team);
                G.fx?.explosion(c, owner.color, d.cannonRadius);
                SpecialSystem.play("bomb_explode", new AudioOpts { pos = c, volume = 0.9f, pitch = 0.8f });
                emit("bomb:explode", new Ev { actor = owner, pos = c, team = team, radius = d.cannonRadius });
                SpecialSystem.blast(owner, team, c, d.cannonRadius, d.cannonDamageMax, d.cannonDamageMin, "crab", 0.8f);
                return false;
            }
            return t < 4 && pos.y > PLAYER.waterY - 2;
        }
        public override void dispose() { destroy(mesh.gameObject); }
    }
}
