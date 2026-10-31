// Port of src/main.js (M1 subset) — boot, stage build, main loop and game-flow orchestration for a Turf War match
// against bots on Tidewater Plaza. Menus, the attract backdrop, music, the death marks, environment dressing and
// online play are not in M1; their places in the loop are kept in the same order as the JS so later milestones slot in.
using System.Collections.Generic;
using Inkwave.World;
using Inkwave.World.Stages;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using static Inkwave.Ctx;

namespace Inkwave
{
    public class GameMain : MonoBehaviour
    {
        public static GameMain instance;
        public Camera cam;
        public Light sun;
        public TouchControls touch;
        public HudLite hud;
        public LevelView levelView;
        public TeamPalette palette;
        public float fps;
        float fpsAcc; int fpsN;
        readonly List<System.Action> unsubs = new List<System.Action>();

        // command line / smoke-test overrides
        public static bool autopilot;
        public static float matchSeconds;
        public static string startWeapon;   // the local player's main weapon (smoke tests: -smoketest-weapon)
        public static string startSub;      // … and sub (-smoketest-sub; default: the weapon's own)
        public static bool randomRoster;    // -smoketest-roster random: every bot gets a random weapon / sub / special
        public static int rosterSeed;       // the roster's RNG seed (logged in summary.txt so a run can be replayed)
        public static string startSpecial;  // the local player's special (-smoketest-special; default: the weapon's own)
        public static string stageTime = "day";   // 'day' | 'dusk' (MAPS[].times → the environment theme; -smoketest-time)
        public GameObject stageDressing;

        void Awake()
        {
            instance = this;
            Application.targetFrameRate = 60;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
            G.settings = Settings.load();
            G.input = new InputState();
            bool touchMode = TouchControls.shouldUseTouch(G.settings);
            // touch devices default to the low tier for its memory footprint (small atlas, small shadow map), and to no
            // shadows — each default only while the player has not picked one themselves
            if (touchMode && !G.settings.qualityChosen) G.settings.quality = "low";
            if (touchMode && !G.settings.shadowsChosen) G.settings.shadows = false;
            G.quality = Config.rendererQuality(G.settings, touchMode);
            G.input.setTouchMode(touchMode);
            touch = new TouchControls(G.input);
            touch.mount();
            G.fx = new Fx();
            G.audio = new AudioSys();
            Kits.init();   // kits/index.js: the kit weapons register themselves
            SpecialSystem.init();
            if (cam == null) cam = Camera.main;
            applyRenderQuality(touchMode);
        }

        void applyRenderQuality(bool touchMode)
        {
            var q = G.quality;
            // pixelRatio caps the density against a CSS pixel; on a phone that is the render scale against the panel
            float dpr = Screen.dpi > 0 ? Screen.dpi / 160f : 1;
            if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp)
            {
                urp.renderScale = touchMode ? Mathf.Clamp(q.pixelRatio / Mathf.Max(1, dpr), 0.5f, 1f) : 1f;
                urp.msaaSampleCount = q.msaa >= 4 ? 4 : q.msaa >= 2 ? 2 : 1;
                urp.shadowDistance = 60;
                // environment.js _fitShadow: ONE sun shadow map over the arena at the tier's size (not URP's 4 cascades,
                // which redraw the whole-stage dressing meshes four times)
                urp.shadowCascadeCount = 1;
                urp.mainLightShadowmapResolution = q.shadowSize;
            }
        }

        void Start()
        {
            buildStage();
            startMatch();
        }

        // ------------------------------------------------------------------ stage
        void buildStage()
        {
            var layout = Tidewater.build();
            // the set dressing's solid props join the level (physics, nav, paint, the lightmap layout), as in main.js
            var level = new Level(layout, StageColliders.load(layout.id));
            G.level = level;
            G.physics = new Physics(level);
            var q = G.quality;
            G.paint = new PaintSystem(level, Mathf.Max(512, q.paintAtlas), q.paintAtlas >= 4096 ? 30 : 18);
            bool texlib = Texlib.bind(q, G.input.touch.active);
            levelView = new LevelView(level, G.paint, q.id != "low", layout.id, texlib);
            G.nav = new NavGraph(level, G.physics);
            setPalette(G.settings.colorblind ? Config.COLORBLIND_PALETTE : Config.TEAM_PALETTES[(int)(random() * Config.TEAM_PALETTES.Length)]);
            G.projectiles = new Projectiles();
            G.subs = new SubSystem();
            G.specials = new SpecialSystem();
            G.rig = new CameraRig();
            buildEnvironment(layout.id);
            Debug.Log($"[Inkwave] stage {layout.id}: layout {level.layoutHash ?? "-"}, AO {(levelView.lightmap != null ? "on" : "off")}, texlib {(texlib ? Texlib.size.ToString() : "off")}, {level.blocks.Count} blocks, {level.renderTris} tris, atlas {G.paint.size} @ {G.paint.ppm:0.0} ppm, nav {G.nav.validIds.Count}/{G.nav.nodes.Count} nodes");
        }

        // The stage dressing + environment baked from the web build (StageLoader / StageEnvironment); without a bake, M1's
        // stand-in: a sun, an ambient fill and a flat sea
        void buildEnvironment(string stageId)
        {
            if (sun == null) sun = FindAnyObjectByType<Light>();
            if (sun == null) sun = new GameObject("Sun").AddComponent<Light>();
            var sj = StageLoader.describe(stageId, stageTime);
            if (sj != null)
            {
                stageDressing = StageLoader.load(sj);
                StageEnvironment.apply(sj, sun, cam, G.settings.shadows, G.quality.bloom);
                var owner = stageDressing.GetComponent<StageOwner>();
                StageEnvironment.envGlobals(sj, owner.materials);
                LevelView.setMurals(owner.materials.muralTexture(), sj.murals?.table);
                return;
            }
            LevelView.setMurals(null, null);
            Debug.LogWarning($"[inkwave] no stage bake for {stageId}/{stageTime} — stand-in environment");
            sun.type = LightType.Directional;
            sun.intensity = 1.3f;
            sun.shadows = G.settings.shadows ? LightShadows.Soft : LightShadows.None;
            sun.transform.rotation = Quaternion.LookRotation(ThreeSpace.toUnity(-new Vector3(-0.41f, 0.83f, -0.38f).normalized));
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.62f, 0.72f, 0.85f);
            RenderSettings.ambientEquatorColor = new Color(0.6f, 0.6f, 0.6f);
            RenderSettings.ambientGroundColor = new Color(0.4f, 0.38f, 0.35f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.62f, 0.8f, 0.95f);
            var sea = ThreeSpace.create("sea");
            sea.AddComponent<MeshFilter>().sharedMesh = Geo.box(600, 0.02f, 600).translate(0, PLAYER.waterY, 0).ensureColor(new Color(0.2f, 0.55f, 0.72f)).toMesh("sea");
            sea.AddComponent<MeshRenderer>().sharedMaterial = new Material(Shader.Find("Inkwave/Debug/VertexColorLit"));
        }

        public void setPalette(TeamPalette p)
        {
            palette = p;
            G.teamHex = new[] { p.a, p.b };
            G.teamColors = new[] { hex(p.a), hex(p.b) };
            LevelView.setTeamColors(G.teamColors[0], G.teamColors[1]);
            G.projectiles?.refreshColors();
            G.subs?.refreshColors();
            foreach (var a in G.actors) a.character.setColor(G.teamColors[a.team]);
        }

        // ------------------------------------------------------------------ match
        int matchN;

        public void startMatch()
        {
            // TEMPORARY (until the menus, Task 14): every new match alternates day and dusk so both are playable
            if (matchN++ > 0 && !autopilot && StageLoader.describe(G.level.layout.id, stageTime == "day" ? "dusk" : "day") != null)
            {
                stageTime = stageTime == "day" ? "dusk" : "day";
                if (stageDressing != null) Destroy(stageDressing);
                buildEnvironment(G.level.layout.id);
            }
            G.match?.dispose();
            G.paint.clear();
            G.projectiles.clear();
            G.subs.clear();
            G.specials.clear();
            var m = new Match(new MatchOpts
            {
                duration = matchSeconds > 0 ? matchSeconds : G.settings.matchLength,
                difficulty = G.settings.difficulty,
                autopilot = autopilot,
                weapon = startWeapon != null && Config.WEAPONS.ContainsKey(startWeapon) ? startWeapon : "shooter",
                sub = startSub,
                special = startSpecial,
                input = G.input, rig = G.rig,
            });
            G.match = m;
            m.setup();
            wireEvents();
            if (hud == null) hud = HudLite.create();
            G.mode = "match";
            m.start();
        }

        void wireEvents()
        {
            foreach (var u in unsubs) u();
            unsubs.Clear();
            unsubs.Add(on("splatted", e =>
            {
                var m = G.match; if (m == null || m.attract) return;
                var victim = e.victim as Actor; var attacker = e.attacker as Actor;
                if (attacker != null && attacker.isLocal) hud?.feed($"You splatted {victim.name}!", G.teamColors[m.local.team]);
                else if (victim.isLocal)
                {
                    hud?.showSplatted(attacker != null ? attacker.name : e.cause == "water" ? null : "enemy ink");
                    G.rig.spectateFrom(attacker != null && attacker.alive ? attacker : null, victim.pos, victim.pos);
                }
                else if (m.local != null && victim.team == m.local.team) hud?.feed($"{victim.name} was splatted{(attacker != null ? " by " + attacker.name : "")}", G.teamColors[victim.enemyTeam]);
                else if (attacker != null && m.local != null && attacker.team == m.local.team) hud?.feed($"{attacker.name} splatted {victim.name}", G.teamColors[attacker.team]);
            }));
            unsubs.Add(on("respawn", e =>
            {
                var actor = e.actor as Actor;
                if (actor != null && actor.isLocal) { hud?.hideSplatted(); G.rig.follow(actor, true); G.rig.yaw = actor.yaw; G.rig.pitch = -0.12f; }
            }));
            unsubs.Add(on("shake", e => { if (G.match != null && !G.match.attract) G.rig.addShake(e.amount, e.pos == Vector3.zero ? (Vector3?)null : e.pos); }));
            unsubs.Add(on("recoil", e => { if (G.match != null && !G.match.attract) G.rig.recoil(e.amount); }));
            unsubs.Add(on("match:count", e => hud?.countdown(e.n)));
            unsubs.Add(on("match:state", e =>
            {
                var m = e.match as Match;
                if (m == null || m.attract || m != G.match) return;
                if (e.state == "intro") intro();
                if (e.state == "playing") { hud?.banner("GO!"); if (m.local != null) G.rig.follow(m.local, true); }
                if (e.state == "finish") { hud?.banner("TIME!"); G.rig.overview(); }
                if (e.state == "judge") hud?.showResults(m.result);
            }));
        }

        void intro()
        {
            var L = G.level;
            var local = G.match.local;
            int team = local != null ? local.team : 0; var pad = L.spawnPads[team];
            float sgn = team == 0 ? 1 : -1;   // the layout is a 180° mirror: Bravo's shot is Alpha's rotated about the centre
            // sweep from high over the enemy base down behind the player (a stage can open on its own hero shot instead)
            var I = L.layout.intro;
            var from = I != null ? new Vector3(I.from.x * sgn, I.from.y, I.from.z * sgn) : new Vector3(18 * sgn, 26, 30 * sgn);
            var to = new Vector3(pad.x, pad.y + 2.6f, pad.z - (I?.toBack ?? 5.2f) * sgn);
            var lookFrom = I != null ? new Vector3(I.lookFrom.x * sgn, I.lookFrom.y, I.lookFrom.z * sgn) : new Vector3(0, 0, 10 * sgn);
            var lookTo = new Vector3(pad.x, pad.y + 1.6f, pad.z + 6 * sgn);
            G.rig.cinematic(from, to, lookFrom, lookTo, 3.6f);
            G.rig.yaw = team == 0 ? 0 : Mathf.PI; G.rig.pitch = -0.12f;
            G.rig.dioFlip = team == 1;
            hud?.banner("READY?");
        }

        // ------------------------------------------------------------------ loop (main.js _frame)
        void Update()
        {
            float raw = Time.unscaledDeltaTime;
            fpsAcc += raw; fpsN++;
            if (fpsAcc > 0.5f) { fps = Mathf.Round(fpsN / fpsAcc); fpsAcc = 0; fpsN = 0; }
            float dt = Mathf.Min(Time.deltaTime, 1 / 24f);
            G.time += dt;
            var inp = G.input;
            inp.poll();
            inp.pollPad();
            inp.pollTouch();
            inp.updateRumble();
            var m = G.match;
            touch.update(G.mode == "match" && m != null && !(hud?.menuOpen ?? false));
            if (m != null)
            {
                // desktop: click to capture the mouse, Escape to release it
                if (!inp.touch.active && !inp.locked && UnityEngine.InputSystem.Mouse.current?.leftButton.wasPressedThisFrame == true && m.state == "playing") inp.requestLock();
                if (inp.wasPressed("Escape")) inp.exitLock();
                m.updateController(dt);
                int sub = dt > 1 / 45f ? 2 : 1;   // substep physics on slow frames
                for (int i = 0; i < sub; i++) m.update(dt / sub);
                if (!m.paused) { G.projectiles.update(dt); G.subs.update(dt); G.specials.update(dt); }
                if (m.state == "playing" && m.local != null && m.local.alive && G.rig.mode != "follow" && G.rig.mode != "path") G.rig.follow(m.local, true);
            }
            G.fx.update(dt);
            // map diorama: held map key during live play (or while waiting to respawn) swoops the view overhead
            G.rig.setMap(m != null && !m.attract && !m.paused && m.state == "playing" && (m.controller?.mapHeld ?? false));
            G.rig.update(dt, cam.aspect);
            G.rig.camera.apply(cam);
            G.camPos = G.rig.camera.position;
            // local player camera-dependent aim must use this frame's camera
            if (m != null && m.controller != null && m.state == "playing") m.controller.computeAim();
            // bomb arc preview
            var loc = m?.local;
            // the throw arc: while aiming a sub, or holding a charged Cheer Orb
            bool orbReady = loc != null && loc.specialActive != null && loc.specialActive.id == "booyah" && loc.specialActive.data is Booyah.State ob && ob.charge >= 1;
            G.projectiles.updateArc(loc, loc != null && loc.alive && (loc.weaponRunner.aimingSub || orbReady) && m.state == "playing" && !m.paused);
            // paint → atlas, shader uniforms
            G.paint.setView(G.camPos);
            G.paint.flush(dt);
            levelView.update(G.paint);
            hud?.update(dt, m, fps);
            inp.endFrame();
        }

        void OnDestroy()
        {
            foreach (var u in unsubs) u();
            G.match?.dispose();
            G.paint?.dispose();
            G.subs?.dispose();
            touch?.dispose();
            StageEnvironment.dispose();
        }
    }
}
