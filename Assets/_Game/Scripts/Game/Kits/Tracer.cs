// Port of src/game/kits/tracer.js — Tracer Bolt (sub kind 'tracer'), see Kits.cs.
//
// Fired the instant the sub button is released: a fast bolt (no gravity) sent a few degrees BELOW the crosshair line.
// It ricochets off every surface except grates (it flies straight through those), leaving a big ink puddle at each
// bounce; off a floor the exit angle is flattened (the bounce's vertical speed is scaled down), so after hitting the
// ground it skims along close to it — which is how it usually finds feet, and why firing it from high up is easy.
// It flies until its total travel reaches `range` or it hits a foe. A direct hit damages and marks the foe for a long
// time; the bolt also leaves a team-coloured trail that lingers about a second, and a foe touching it is damaged and
// marked for a short time (once per bolt; a direct hit takes the place of the trail hit).
// While the button is held the local player sees a thin guide: the true launch line to the first bounce, then the
// first rebound, so the low angle is learnable.
// The bolt model is baked from tracer.js's own builder (sub_tracer: body, ink, glowing core). Trail: Shaders/Fx/Trail.
// Not ported: the online ghosts, specials.js shotHit (with that module), the sounds (M5: by name).
using System.Collections.Generic;
using Inkwave.World;
using UnityEngine;
using UnityEngine.Rendering;
using static Inkwave.Ctx;

namespace Inkwave
{
    public class Tracer : SubKit
    {
        const float DEG = Mathf.PI / 180;
        static readonly Vector3 UP = new Vector3(0, 1, 0), ZAX = new Vector3(0, 0, 1);
        const float SCALE = 1.9f;
        const float YC = 0.02f;   // the bolt's axis height in prop space (tracer.js YC)
        static readonly Hit _hit = new Hit(), _hit2 = new Hit();
        static readonly int ID_Color = Shader.PropertyToID("_Color"), ID_Time = Shader.PropertyToID("_Time2"), ID_Life = Shader.PropertyToID("_Life");
        static bool nearCam(Vector3 p, float r = 34) => (G.camPos - p).sqrMagnitude < r * r;
        static Vector3 hitBase(Actor e) => new Vector3(e.pos.x, e.pos.y + e.smoothY, e.pos.z);
        static float hitH(Actor e) => e.form == "squid" ? PLAYER.squidHeight : PLAYER.height;
        static float hitR(Actor e) => PLAYER.radius;

        public Tracer() { bot = new SubKitBot { fight = botFight, paint = botPaint }; }
        public override bool noArc => true;

        // =============================================================================================== trail ribbon
        // A camera-facing strip through the bolt's path points; each point fades out `life` seconds after it was laid.
        const int RIB_CAP = 160;
        static int[] _ribIdx;
        class Ribbon { public Mesh mesh; public Material mat; public Transform t; public Vector3[] pos = new Vector3[RIB_CAP * 2]; public Vector2[] info = new Vector2[RIB_CAP * 2]; }
        static Ribbon makeRibbon(int team, float life)
        {
            if (_ribIdx == null)
            {
                _ribIdx = new int[(RIB_CAP - 1) * 6];
                for (int i = 0, k = 0; i < RIB_CAP - 1; i++) { int a = i * 2; _ribIdx[k++] = a; _ribIdx[k++] = a + 1; _ribIdx[k++] = a + 2; _ribIdx[k++] = a + 1; _ribIdx[k++] = a + 3; _ribIdx[k++] = a + 2; }
            }
            var R = new Ribbon { mesh = new Mesh { name = "tracer-trail" } };
            R.mesh.MarkDynamic();
            R.mesh.vertices = R.pos; R.mesh.uv = R.info;
            R.mesh.SetIndices(_ribIdx, MeshTopology.Triangles, 0, false);
            R.mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 2000);
            R.mesh.SetSubMesh(0, new SubMeshDescriptor(0, 0), MeshUpdateFlags.DontRecalculateBounds);
            R.mat = new Material(Shader.Find("Inkwave/Fx/Trail")) { name = "iw-tracer-trail", renderQueue = 3002 };
            Color c = G.teamColors[team].linear;
            R.mat.SetVector(ID_Color, new Vector4(c.r, c.g, c.b, 1)); R.mat.SetFloat(ID_Life, life);
            var go = ThreeSpace.create("tracerTrail");
            go.AddComponent<MeshFilter>().sharedMesh = R.mesh;
            var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = R.mat; r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
            R.t = go.transform;
            return R;
        }
        static void drawRibbon(Bolt b)
        {
            var R = b.rib; var P = b.pts; int n = P.Count;
            if (n < 2) { R.mesh.SetSubMesh(0, new SubMeshDescriptor(0, 0), MeshUpdateFlags.DontRecalculateBounds); return; }
            Vector3 cam = G.camPos; float life = b.sub.trailLife;
            for (int i = 0; i < n; i++)
            {
                Vector3 p = P[i].p;
                Vector3 along = P[Mathf.Min(n - 1, i + 1)].p - P[Mathf.Max(0, i - 1)].p; if (along.sqrMagnitude < 1e-8f) along = b.dir;
                Vector3 side = Vector3.Cross(along, cam - p); if (side.sqrMagnitude < 1e-8f) side = UP;
                float age = clamp((clock - P[i].b) / life, 0, 1);
                float w = 0.085f * (1 + 0.6f * (1 - age));
                side = side.normalized * w;
                R.pos[i * 2] = p + side; R.info[i * 2] = new Vector2(-1, P[i].b);
                R.pos[i * 2 + 1] = p - side; R.info[i * 2 + 1] = new Vector2(1, P[i].b);
            }
            for (int i = n * 2; i < RIB_CAP * 2; i++) { R.pos[i] = R.pos[n * 2 - 1]; R.info[i] = R.info[n * 2 - 1]; }   // (unused: collapsed)
            R.mesh.vertices = R.pos; R.mesh.uv = R.info;
            R.mesh.SetSubMesh(0, new SubMeshDescriptor(0, (n - 1) * 6), MeshUpdateFlags.DontRecalculateBounds);
            R.mat.SetFloat(ID_Time, clock);
        }

        // =============================================================================================== bolt head
        class Head { public Transform outer, streak; public Material glow, streakMat; }
        static Mesh _sphere;
        static Head buildHead(int team)
        {
            var d = WeaponModel.getSubDef("tracer"); Color col = G.teamColors[team];
            var H = new Head { outer = ThreeSpace.create("tracer").transform };
            var model = ThreeSpace.create("model", H.outer).transform;
            model.localScale = Vector3.one * SCALE; model.localPosition = new Vector3(0, -YC * SCALE, 0);
            part("body", model, d.body, CharacterMats.plastic);
            part("ink", model, d.ink, CharacterMats.ink(col));
            H.glow = CharacterMats.lampMaterial(new Color(0x11 / 255f, 0x11 / 255f, 0x11 / 255f), 0.3f);
            CharacterMats.setLamp(H.glow, col.linear, 2.4f);
            if (d.glow != null) part("glow", model, d.glow, H.glow);
            // a hot glowing streak round it (reads at speed)
            _sphere ??= Geo.sphere(1, 12, 8).toMesh("tracer-streak");
            Color sc = col.linear * 2.2f; sc.r += 0.25f; sc.g += 0.25f; sc.b += 0.25f;
            H.streakMat = new Material(Shader.Find("Inkwave/Fx/Tint")) { name = "iw-tracer-streak" };
            H.streakMat.SetVector(ID_Color, new Vector4(sc.r, sc.g, sc.b, 0.55f));
            H.streak = part("streak", H.outer, _sphere, H.streakMat).transform;
            H.streak.localScale = new Vector3(0.075f, 0.075f, 0.42f); H.streak.localPosition = new Vector3(0, 0, -0.12f);
            return H;
        }
        static MeshRenderer part(string name, Transform parent, Mesh m, Material mat)
        {
            var g = ThreeSpace.create(name, parent);
            g.AddComponent<MeshFilter>().sharedMesh = m;
            var r = g.AddComponent<MeshRenderer>(); r.sharedMaterial = mat; r.shadowCastingMode = ShadowCastingMode.Off;
            return r;
        }

        // =============================================================================================== state
        struct Pt { public Vector3 p; public float b; }
        class Bolt
        {
            public SubDef sub; public Actor owner, direct; public int team, bounces; public string state;
            public Vector3 pos, dir; public float left, travel, age; public bool sp;
            public readonly HashSet<Actor> hitSet = new HashSet<Actor>();
            public readonly List<Pt> pts = new List<Pt>();
            public Head head; public Ribbon rib; public AudioLoop hum;
        }
        static readonly List<Bolt> bolts = new List<Bolt>();
        static float clock;
        static void credit(Bolt b, float area) { if (!(area > 0)) return; if (b.sp) b.owner.addTurfNoSpecial(area); else b.owner.addTurf(area); }

        // launch point (chest height, a little in front; never inside a wall) and direction (the crosshair line, lowered)
        static void launch(Actor a, SubDef sub, out Vector3 from, out Vector3 dir)
        {
            float sy = a.smoothY;
            Vector3 chest = new Vector3(a.pos.x, a.pos.y + sy + 1.0f, a.pos.z);
            float fx = Mathf.Sin(a.aimYaw), fz = Mathf.Cos(a.aimYaw);
            from = new Vector3(a.pos.x + fx * 0.3f, a.pos.y + sy + 1.2f, a.pos.z + fz * 0.3f);
            if (!G.physics.los(chest, from)) from = chest;
            dir = a.aimPoint - from;
            float d = dir.magnitude;
            if (!(d > 2) || Vector3.Dot(dir, a.aimDir) < 0) dir = a.aimDir; else dir *= 1 / d;
            float pitch = clamp(Mathf.Asin(clamp(dir.y, -1, 1)) - sub.pitchDown * DEG, -1.5f, 1.45f), yaw = Mathf.Atan2(dir.x, dir.z);
            float cp = Mathf.Cos(pitch);
            dir = new Vector3(Mathf.Sin(yaw) * cp, Mathf.Sin(pitch), Mathf.Cos(yaw) * cp);
        }
        // the bounce rule: mirror off the surface; off a floor the vertical part of the rebound is scaled down (skims low)
        static Vector3 rebound(Vector3 dir, Vector3 n, SubDef sub)
        {
            dir += n * (-2 * Vector3.Dot(dir, n));
            if (n.y > 0.6f)
            {
                dir.y *= sub.floorExit;
                dir.Normalize();
                float dn = Vector3.Dot(dir, n);
                if (dn < 0.04f) dir = (dir + n * (0.04f - dn)).normalized;   // sloped floor: never back into it
            }
            return dir.normalized;
        }

        public override void use(SubSystem subs, Actor a, SubDef sub)
        {
            launch(a, sub, out var from, out var dir);
            spawn(a, sub, from, dir);
            if (a.isLocal || a.nearCamera()) G.audio?.play("tracer_zap", new AudioOpts { pos = a.isLocal ? (Vector3?)null : from, volume = 0.8f });
            emit("sub:use", new Ev { actor = a, kind = "tracer" });
        }
        static Bolt spawn(Actor a, SubDef sub, Vector3 from, Vector3 dir)
        {
            var b = new Bolt
            {
                sub = sub, owner = a, team = a.team, pos = from, dir = dir, left = sub.range, state = "fly", sp = a.specialActive != null,
                head = buildHead(a.team), rib = makeRibbon(a.team, sub.trailLife),
            };
            addPoint(b);
            bolts.Add(b);
            if (nearCam(from, 40)) b.hum = G.audio?.loop("tracer_hum", new AudioOpts { pos = from, volume = 0.4f });
            if (nearCam(from, 30)) G.fx?.muzzle(from, dir, G.teamColors[a.team], "shooter");
            poseHead(b);
            return b;
        }

        static void addPoint(Bolt b)
        {
            var P = b.pts;
            if (P.Count > 0 && (P[P.Count - 1].p - b.pos).sqrMagnitude < 1e-6f) { P[P.Count - 1] = new Pt { p = P[P.Count - 1].p, b = clock }; return; }
            if (P.Count >= RIB_CAP) P.RemoveAt(0);
            P.Add(new Pt { p = b.pos, b = clock });
        }

        public override void tick(float dt)
        {
            clock += dt;
            guideTick();
            if (bolts.Count == 0) return;
            for (int i = bolts.Count - 1; i >= 0; i--)
            {
                var b = bolts[i];
                b.age += dt;
                if (b.state == "fly")
                {
                    advance(b, b.sub.speed * dt);
                    addPoint(b);
                }
                // drop trail points that have fully faded (keep one so the next segment has a start)
                float life = b.sub.trailLife;
                while (b.pts.Count > 1 && clock - b.pts[1].b > life) b.pts.RemoveAt(0);
                trailTouch(b);
                if (b.state != "fly" && (b.pts.Count == 0 || clock - b.pts[b.pts.Count - 1].b > life)) { dispose(b); bolts.RemoveAt(i); continue; }
                poseHead(b);
                drawRibbon(b);
                if (b.hum != null)
                {
                    float k = b.state == "fly" ? 1 : clamp(1 - (clock - b.pts[b.pts.Count - 1].b) / life, 0, 1);
                    b.hum.set(0.4f * k, b.state == "fly" ? 1.15f : 0.85f + 0.2f * k, b.state == "fly" ? b.pos : b.pts[b.pts.Count / 2].p);
                }
            }
        }

        // move the bolt `dist` along its path: bounces, direct hits, devices, range
        static void advance(Bolt b, float dist)
        {
            var s = b.sub;
            float remain = dist; int guard = 0;
            while (remain > 1e-5f && b.state == "fly" && guard++ < 24)
            {
                float step = Mathf.Min(remain, b.left);
                var w = G.physics.raycast(b.pos, b.dir, step, _hit, true);   // grates: straight through
                float seg = w.hit ? w.dist : step;
                Vector3 end = b.pos + b.dir * seg;
                // a foe on this stretch (the earliest one)
                Actor best = null; float bt = 2;
                foreach (var e in G.actors)
                {
                    if (e.team == b.team || !e.alive) continue;
                    float hr = hitR(e);
                    if (Mathf.Max(Mathf.Abs(e.pos.x - b.pos.x), Mathf.Abs(e.pos.z - b.pos.z)) > seg + 3) continue;
                    var (d, t) = Physics.segmentCapsuleDist(b.pos, end, hitBase(e), hr, hitH(e));
                    if (d < hr + s.size && t < bt) { bt = t; best = e; }
                }
                if (best != null)
                {
                    b.pos = Vector3.Lerp(b.pos, end, bt);
                    addPoint(b);
                    directHit(b, best);
                    return;
                }
                // enemy curtains / shields / devices stop it (specials.js shotHit — bubbles — with that module)
                if (G.subs.blockShot(b.pos, end, b.team, s.directDamage))
                {
                    b.pos = end; addPoint(b);
                    endBolt(b, "blocked");
                    return;
                }
                b.pos = end;
                b.left -= seg; b.travel += seg; remain -= seg;
                if (w.hit) bounce(b, w);
                if (b.state != "fly") return;
                if (b.left <= 1e-4f) { addPoint(b); endBolt(b, "range"); return; }
                if (b.pos.y < PLAYER.waterY - 0.3f) { addPoint(b); endBolt(b, "water"); return; }
            }
        }
        static void bounce(Bolt b, Hit w)
        {
            var s = b.sub; Vector3 n = w.normal; Color col = G.teamColors[b.team];
            addPoint(b);
            // a big puddle where it strikes
            var blk = w.block >= 0 && w.block < G.level.blocks.Count ? G.level.blocks[w.block] : null;
            if (!(blk != null && (blk.roof || blk.perch)))
            {
                Vector3 h = new Vector3(b.dir.x, 0, b.dir.z); if (h.sqrMagnitude < 1e-4f) h = new Vector3(0, 0, 1); h.Normalize();
                credit(b, G.paint.splat(w.point + n * 0.1f, s.puddleRadius, b.team, new SplatOpts { seed = random(), stretch = h, stretchAmt = 0.35f }));
            }
            b.dir = rebound(b.dir, n, s);
            b.pos = w.point + n * 0.03f;
            b.bounces++;
            if (nearCam(w.point, 30))
            {
                G.fx?.burst(w.point, n, col, new FxOpts { count = 7, speed = 3.5f, size = 0.07f, ring = true });
                G.fx?.bounceSplash(w.point, n, col);
            }
            if (nearCam(w.point, 34)) G.audio?.play("tracer_bounce", new AudioOpts { pos = w.point, volume = 0.75f, pitch = 1 + Mathf.Min(0.3f, b.bounces * 0.05f) });
            emit("weapon:impact", new Ev { pos = w.point, normal = n, team = b.team, kind = "shot", radius = s.puddleRadius * 0.5f });
            if (b.bounces > s.maxBounces) endBolt(b, "bounces");
        }
        static void directHit(Bolt b, Actor e)
        {
            var s = b.sub; Color col = G.teamColors[b.team];
            b.hitSet.Add(e); b.direct = e;
            G.projectiles.applyHit(b.owner, e, s.directDamage, "tracer");
            G.subs.track(e, b.team, s.directMark);
            if (nearCam(b.pos, 34))
            {
                G.fx?.burst(b.pos, -b.dir, col, new FxOpts { count = 12, speed = 4.5f, size = 0.08f, ring = true });
                G.fx?.glint(b.pos, col, 0.5f);
            }
            if (b.owner.isLocal || e.isLocal || nearCam(b.pos, 30)) G.audio?.play("tracer_hit", new AudioOpts { pos = e.isLocal ? (Vector3?)null : b.pos, volume = 0.9f });
            emit("tracer:hit", new Ev { actor = b.owner, victim = e, final = true, pos = b.pos });
            endBolt(b, "hit");
        }
        static void endBolt(Bolt b, string why)
        {
            if (b.state != "fly") return;
            b.state = why;
            b.head.outer.gameObject.SetActive(false);
            if (why != "hit" && nearCam(b.pos, 30)) G.fx?.burst(b.pos, -b.dir, G.teamColors[b.team], new FxOpts { count = 5, speed = 2.2f, size = 0.05f });
        }
        // a foe touching the lingering trail: damaged + marked (short), once per bolt
        static void trailTouch(Bolt b)
        {
            var P = b.pts; int n = P.Count; var s = b.sub;
            if (n < 2) return;
            float life = s.trailLife * 0.92f;
            foreach (var e in G.actors)
            {
                if (e.team == b.team || !e.alive || b.hitSet.Contains(e)) continue;
                float hr = hitR(e), H = hitH(e), reach = hr + s.trailRadius;
                Vector3 bas = hitBase(e);
                for (int i = 0; i < n - 1; i++)
                {
                    var a = P[i]; var c = P[i + 1];
                    if (clock - a.b > life) continue;
                    float minx = Mathf.Min(a.p.x, c.p.x) - reach, maxx = Mathf.Max(a.p.x, c.p.x) + reach;
                    float minz = Mathf.Min(a.p.z, c.p.z) - reach, maxz = Mathf.Max(a.p.z, c.p.z) + reach;
                    if (bas.x < minx || bas.x > maxx || bas.z < minz || bas.z > maxz) continue;
                    var (d, t) = Physics.segmentCapsuleDist(a.p, c.p, bas, hr, H);
                    if (d >= reach) continue;
                    b.hitSet.Add(e);
                    G.projectiles.applyHit(b.owner, e, s.trailDamage, "tracer");
                    G.subs.track(e, b.team, s.trailMark);
                    Vector3 at = Vector3.Lerp(a.p, c.p, t);
                    if (nearCam(at, 30)) G.fx?.burst(at, UP, G.teamColors[b.team], new FxOpts { count = 6, speed = 2.6f, size = 0.06f });
                    if (b.owner.isLocal || e.isLocal) G.audio?.play("tracer_hit", new AudioOpts { pos = e.isLocal ? (Vector3?)null : at, volume = 0.55f, pitch = 1.3f });
                    emit("tracer:hit", new Ev { actor = b.owner, victim = e, final = false, pos = at });
                    break;
                }
            }
        }
        static void poseHead(Bolt b)
        {
            var H = b.head;
            if (!H.outer.gameObject.activeSelf) return;
            H.outer.localPosition = b.pos;
            H.outer.localRotation = Quaternion.FromToRotation(ZAX, b.dir) * Quaternion.AngleAxis(b.age * 26 * Mathf.Rad2Deg, ZAX);
            Color sc = G.teamColors[b.team].linear * 2.2f; sc.r += 0.25f; sc.g += 0.25f; sc.b += 0.25f;
            H.streakMat.SetVector(ID_Color, new Vector4(sc.r, sc.g, sc.b, 0.45f + 0.2f * Mathf.Sin(b.age * 70)));
            if (nearCam(b.pos, 30) && random() < 0.7f) G.fx?.glint(b.pos, G.teamColors[b.team], 0.18f);
        }
        static void dispose(Bolt b)
        {
            destroy(b.head.outer.gameObject); destroy(b.head.glow); destroy(b.head.streakMat);
            destroy(b.rib.t.gameObject); destroy(b.rib.mesh); destroy(b.rib.mat);
            if (b.hum != null) { b.hum.stop(0.1f); b.hum = null; }
        }
        public override void clear()
        {
            foreach (var b in bolts) dispose(b);
            bolts.Clear();
            hideGuide();
        }

        // =============================================================================================== aim guide (local player)
        static LineRenderer gLine, gExit; static Transform gRing; static Material gMat, gExitMat; static bool guideSeen;
        static void guideMeshes()
        {
            if (gLine != null) return;
            var unlit = Shader.Find("Universal Render Pipeline/Unlit");
            gMat = new Material(unlit) { color = Color.white, renderQueue = 4000 }; gMat.SetFloat("_Cull", 0);
            gExitMat = new Material(unlit) { color = new Color(1, 1, 1, 0.7f), renderQueue = 4000 };
            LineRenderer mk(string name, Material m, float w)
            {
                var l = new GameObject(name).AddComponent<LineRenderer>();
                l.useWorldSpace = true; l.positionCount = 2; l.widthMultiplier = w; l.material = m;
                l.shadowCastingMode = ShadowCastingMode.Off; l.enabled = false;
                return l;
            }
            gLine = mk("tracerGuide", gMat, 0.03f);
            gExit = mk("tracerGuideExit", gExitMat, 0.022f);
            // ring 0.2 … 0.3 m, facing +Z (three RingGeometry)
            var ring = ThreeSpace.create("tracerGuideRing");
            ring.AddComponent<MeshFilter>().sharedMesh = Kits.ringMesh(0.2f, 0.3f, 32);
            var rr = ring.AddComponent<MeshRenderer>(); rr.sharedMaterial = gMat; rr.shadowCastingMode = ShadowCastingMode.Off;
            gRing = ring.transform; ring.SetActive(false);
        }
        static void hideGuide() { if (gLine != null) { gLine.enabled = gExit.enabled = false; gRing.gameObject.SetActive(false); } }
        static void guideTick() { if (!guideSeen) hideGuide(); guideSeen = false; }
        public override void hold(WeaponRunner runner, float dt, WeaponInput inp, SubDef sub)
        {
            var a = runner.a;
            if (a == null || a != G.local || !a.alive) return;
            guideSeen = true;
            guideMeshes();
            launch(a, sub, out var from, out var dir);
            var w = G.physics.raycast(from, dir, sub.range, _hit2, true);
            Vector3 p1 = from + dir * (w.hit ? w.dist : sub.range);
            Color col = (a.ink >= sub.inkCost ? G.teamColors[a.team] : new Color(0.6f, 0.6f, 0.6f)) * 1.4f;
            gLine.SetPosition(0, ThreeSpace.toUnity(from)); gLine.SetPosition(1, ThreeSpace.toUnity(p1));
            gMat.color = new Color(col.r, col.g, col.b, 0.9f);
            gLine.enabled = true;
            if (w.hit)
            {
                Vector3 n = _hit2.normal;
                gRing.localPosition = p1 + n * 0.03f;
                gRing.localRotation = Quaternion.FromToRotation(ZAX, n);
                gRing.localScale = Vector3.one * (1 + 0.08f * Mathf.Sin(clock * 9));
                gRing.gameObject.SetActive(true);
                // the first rebound, as far as the next surface / 6 m
                Vector3 d2 = rebound(dir, n, sub);
                Vector3 s0 = p1 + n * 0.03f;
                float left = Mathf.Min(6, sub.range - w.dist);
                var w2 = G.physics.raycast(s0, d2, Mathf.Max(0.1f, left), _hit, true);
                Vector3 p2 = s0 + d2 * (w2.hit ? w2.dist : Mathf.Max(0.1f, left));
                gExit.SetPosition(0, ThreeSpace.toUnity(p1)); gExit.SetPosition(1, ThreeSpace.toUnity(p2));
                gExitMat.color = new Color(col.r, col.g, col.b, 0.7f);
                gExit.enabled = left > 0.2f;
            }
            else { gRing.gameObject.SetActive(false); gExit.enabled = false; }
        }

        // =============================================================================================== bots
        // snap it at a foe in mid range (it's cheap); while painting, now and then skim one along the ground for puddles
        static bool botFight(BotBrain brain, float dist)
        {
            bool go = dist > 3 && dist < 17 && random() < 0.05f;
            if (go) brain.bombCd = 2.5f + random() * 3;
            return go;
        }
        static bool botPaint(BotBrain brain)
        {
            bool go = random() < 0.006f;
            if (go) brain.bombCd = 6 + random() * 6;
            return go;
        }
    }
}
