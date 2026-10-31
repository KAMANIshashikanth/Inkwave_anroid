// Self-playing match for automated checks (the Unity side of INKWAVE's `?autostart=…&autopilot` + tools/smoke.sh).
// Only active when the player is launched with -smoketest (development builds / editor). The local player is handed
// to a bot brain so the whole loop runs unattended; screenshots and a summary land in -smoketest-out.
//   -smoketest-seconds N   match length (default 60)     -smoketest-timescale X   sim speed (default 1)
//   -smoketest-weapon ID   the local player's main weapon (default shooter)
//   -smoketest-sub ID      the local player's sub (default: the weapon's own)
#if DEVELOPMENT_BUILD || UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;

namespace Inkwave.Dev
{
    public class SmokeTest : MonoBehaviour
    {
        string outDir;
        int errors;
        readonly StringBuilder log = new StringBuilder();
        float fpsSum; int fpsN; float fpsMin = 999;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Arm()
        {
            if (!HasArg("-smoketest")) return;
            // keep running when the window loses focus (the player settings pause it — fine for play, but an unattended run
            // must not freeze because someone clicked another window)
            Application.runInBackground = true;
            GameMain.autopilot = true;
            GameMain.matchSeconds = float.TryParse(Arg("-smoketest-seconds"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 60;
            GameMain.startWeapon = Arg("-smoketest-weapon");
            GameMain.startSub = Arg("-smoketest-sub");
            GameMain.startSpecial = Arg("-smoketest-special");
            if (Arg("-smoketest-time") == "dusk") GameMain.stageTime = "dusk";
            if (Arg("-smoketest-roster") == "random")
            {
                GameMain.randomRoster = true;
                var seedArg = Arg("-smoketest-seed");
                GameMain.rosterSeed = seedArg != null && int.TryParse(seedArg, out var sd) ? sd
                    : unchecked((int)(System.DateTime.UtcNow.Ticks & 0x7fffffff)) | 1;
            }
            var go = new GameObject("SmokeTest");
            DontDestroyOnLoad(go);
            go.AddComponent<SmokeTest>();
        }

        void Awake()
        {
            Application.logMessageReceived += (msg, stack, type) =>
            {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) { errors++; Log($"ERROR {type}: {msg}\n{stack}"); }
            };
        }

        IEnumerator Start()
        {
            outDir = Arg("-smoketest-out") ?? Application.persistentDataPath;
            Directory.CreateDirectory(outDir);
            float ts = float.TryParse(Arg("-smoketest-timescale"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) ? x : 1;
            Log($"start: screen {Screen.width}x{Screen.height}, timescale {ts}, match {GameMain.matchSeconds}s");
            if (GameMain.randomRoster) Log($"roster: random seed {GameMain.rosterSeed}");
            yield return new WaitForSecondsRealtime(1.0f);
            Time.timeScale = ts;
            yield return Shot("01_intro");
            var m = G.match;
            if (m == null) { Log("FAIL: no match"); Finish(2); yield break; }
            Inkwave.Ctx.on("splatted", e => Log($"splat: {(e.attacker as Actor)?.name ?? "-"} -> {(e.victim as Actor)?.name} ({e.cause}) t={m.duration - m.time:0.0}"));
            Inkwave.Ctx.on("match:state", e => Log($"state -> {e.state} t={m.duration - m.time:0.0}"));
            // sub weapons: every use / landing / cloud / destroyed device, by kind (local player's own counted apart)
            var subN = new System.Collections.Generic.SortedDictionary<string, int>();
            void count(string k) { subN.TryGetValue(k, out int n); subN[k] = n + 1; }
            foreach (var ev in new[] { "sub:use", "sub:land", "sub:cloud", "sub:arm", "sub:destroyed", "actor:tracked", "actor:poisoned", "sub:cantuse" })
                Inkwave.Ctx.on(ev, e => { count(ev + " " + (e.kind ?? "-")); if (e.actor == (object)G.local && ev == "sub:use") count("local " + e.kind); });
            // a few frames of the local player's own shots in flight (projectile / beam visuals)
            float lastFireShot = -99, firedAt = -1;
            Inkwave.Ctx.on("weapon:fire", e => { if (e.actor == (object)G.local && firedAt < 0 && Time.unscaledTime - lastFireShot > 6) firedAt = Time.unscaledTime; });
            // …and of your own subs at work (0.5 s and 1.4 s after each use, the first four uses)
            var subShots = new System.Collections.Generic.List<(float at, string name)>(); int su = 0;
            Inkwave.Ctx.on("sub:use", e => { if (e.actor == (object)G.local && su < 4) { su++; subShots.Add((Time.unscaledTime + 0.5f / Time.timeScale, $"sub_{su}a_{e.kind}")); subShots.Add((Time.unscaledTime + 1.4f / Time.timeScale, $"sub_{su}b_{e.kind}")); } });
            // specials: every start / end by id (the local player's own counted apart), and your own at work (0.3 s and
            // 1.5 s after each start, the first two)
            foreach (var ev in new[] { "special:start", "special:end", "special:shield", "special:sonar", "special:launch", "special:strike", "special:wail" })
                Inkwave.Ctx.on(ev, e => { count(ev + " " + (e.id ?? "-") + (e.reason != null ? " (" + e.reason + ")" : "")); if (e.actor == (object)G.local && ev == "special:start") count("local special " + e.id); });
            int sp = 0;
            void addShot(float at, string name) { int i = subShots.FindIndex(x => x.at > at); if (i < 0) subShots.Add((at, name)); else subShots.Insert(i, (at, name)); }
            int tn = 0;   // your Vortex Strike's vortex, 1 s after it lands
            Inkwave.Ctx.on("special:strike", e => { if (e.actor == (object)G.local && tn < 2) { tn++; addShot(Time.unscaledTime + 1.0f / Time.timeScale, $"special_{tn}c_strike_vortex"); } });
            Inkwave.Ctx.on("special:use", e => { if (e.actor == (object)G.local && sp < 2) { sp++; addShot(Time.unscaledTime + 0.3f / Time.timeScale, $"special_{sp}a_{e.id}"); addShot(Time.unscaledTime + 1.5f / Time.timeScale, $"special_{sp}b_{e.id}"); } });
            int f = 1;
            while (m.state != "playing") yield return null;
            yield return new WaitForSecondsRealtime(2.5f / ts);
            yield return Shot("02_play_start");
            float shotEvery = Mathf.Max(8, GameMain.matchSeconds / 4);
            float next = shotEvery; int k = 3;
            while (m.state == "playing")
            {
                float fps = 1 / Mathf.Max(1e-4f, Time.unscaledDeltaTime);
                fpsSum += fps; fpsN++; fpsMin = Mathf.Min(fpsMin, fps);
                if (m.duration - m.time >= next) { next += shotEvery; yield return Shot($"{k++:00}_play"); }
                if (subShots.Count > 0 && Time.unscaledTime >= subShots[0].at) { var ss = subShots[0]; subShots.RemoveAt(0); yield return Shot(ss.name); }
                if (firedAt >= 0 && Time.unscaledTime - firedAt > 0.06f && f <= 4) { firedAt = -1; lastFireShot = Time.unscaledTime; yield return Shot($"fire_{f++}"); }
                yield return null;
            }
            while (m.result == null) yield return null;
            yield return new WaitForSecondsRealtime(1.0f);
            yield return Shot($"{k:00}_results");
            var cov = m.result.coverage;
            Log($"result: alpha {cov.x * 100:0.0}% bravo {cov.y * 100:0.0}% winner {(m.result.winner == 0 ? "alpha" : "bravo")}");
            foreach (var a in m.actors) Log($"  {a.name,-10} team {a.team} {a.weaponId,-8} {a.subId,-9} {a.specialId,-12} turf {a.stats.turf,6:0} splats {a.stats.splats} deaths {a.stats.deaths} specials {a.stats.specials}");
            { var B = Brolly.STATS; if (B.blasts > 0) Log($"brolly: blasts {B.blasts} opens {B.opens} blocked {B.blocked} ({B.blockedDmg:0} dmg) launches {B.launches} breaks {B.breaks} guarded {B.guarded}"); }
            foreach (var kv in subN) Log($"{(kv.Key.StartsWith("special") || kv.Key.StartsWith("local special") ? "specials" : "subs")}: {kv.Key} x{kv.Value}");
            { if (BotBrain.THREAT_STATS.noticed > 0) Log($"threats: noticed {BotBrain.THREAT_STATS.noticed} shoot {BotBrain.THREAT_STATS.shoot} evade {BotBrain.THREAT_STATS.evade} swim {BotBrain.THREAT_STATS.swim} sidestep {BotBrain.THREAT_STATS.sidestep} gone {BotBrain.THREAT_STATS.gone}"); }
            if (SubSystem.beaconLandings > 0) Log($"subs: beacon landings x{SubSystem.beaconLandings}");
            Log($"fps: avg {fpsSum / Mathf.Max(1, fpsN):0.0} min {fpsMin:0.0}  errors {errors}");
            bool ok = errors == 0 && cov.x > 0 && cov.y > 0;
            Log(ok ? "PASS" : "FAIL");
            Finish(ok ? 0 : 1);
        }

        IEnumerator Shot(string name)
        {
            yield return new WaitForEndOfFrame();
            var path = Path.Combine(outDir, name + ".png");
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Destroy(tex);
            Log("shot " + path);
        }

        void Log(string s)
        {
            log.AppendLine(s);
            Debug.Log("[Smoke] " + s);
        }

        void Finish(int code)
        {
            File.WriteAllText(Path.Combine(outDir, "summary.txt"), log.ToString());
            Application.Quit(code);
        }

        static bool HasArg(string n) { foreach (var a in System.Environment.GetCommandLineArgs()) if (a == n) return true; return false; }
        static string Arg(string n)
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == n) return args[i + 1];
            return null;
        }
    }
}
#endif
