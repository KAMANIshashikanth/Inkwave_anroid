// Port of src/game/match.js — turf-war rules, lifecycle (intro → countdown → play → time's up → judge → results),
// team setup. (Zone Control, Boss Battle and the online roster arrive with M7 / not ported.)
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static Inkwave.Ctx;

namespace Inkwave
{
    public static partial class G
    {
        public static Match match;
    }

    public class MatchOpts
    {
        public float duration;
        public string difficulty = "normal", playerName = "You", weapon = "shooter", sub, special, mode = "turf";
        public bool attract, practice, autopilot;
        public InputState input;
        public CameraRig rig;
    }

    public class MatchResult { public Vector2 coverage; public int winner; }

    public class Match
    {
        public readonly MatchOpts opts;
        public readonly bool attract, practice;
        public readonly string mode;
        public readonly float duration;
        public float time, stateT;
        public string state = "init";
        public readonly List<Actor> actors = new List<Actor>();
        public PlayerController controller;
        public Actor local;
        public MatchResult result;
        public bool paused;
        bool lastMinuteFired;
        int lastCount = 99;
        public readonly List<(float t, Actor victim, Actor attacker, string cause)> events = new List<(float, Actor, Actor, string)>();
        List<Action> unsubs;

        public Match(MatchOpts opts)
        {
            this.opts = opts;
            attract = opts.attract;
            practice = opts.practice;   // solo on an empty stage: no enemies, no teammates, no clock
            mode = "turf";
            duration = opts.duration > 0 ? opts.duration : MATCH.defaultDuration;
            time = duration;
        }

        public bool playing() => state == "playing" && !paused;
        public bool canRespawn() => state == "playing";

        public void setup()
        {
            var o = opts;
            // weapons: each team gets a balanced mix (of the ported kinds: the built-ins + every kit registered so far)
            string[] ported = Config.WEAPON_ORDER.Where(k => Weapons.BUILTIN.Contains(k) || Kits.MAIN.ContainsKey(k)).ToArray();
            List<string> pickTeam(string first)
            {
                var pool = new List<string>(ported);
                var outL = new List<string>();
                if (first != null) { outL.Add(first); pool.Remove(first); }
                while (outL.Count < MATCH.teamSize)
                {
                    if (pool.Count == 0) pool.AddRange(ported);
                    int i = (int)(random() * pool.Count);
                    outL.Add(pool[i]); pool.RemoveAt(i);
                }
                return outL;
            }
            var names = Config.BOT_NAMES.OrderBy(_ => random()).ToList();
            string[] subs = Config.SUB_ORDER.Where(SubSystem.ported).ToArray();   // (of the ported kinds)
            // -smoketest-roster random: a seeded loadout per bot, covering WEAPON_ORDER × SUB_ORDER × SPECIAL_ORDER
            Func<float> rosterRng = GameMain.randomRoster ? rng(GameMain.rosterSeed) : null;
            int ni = 0;
            for (int team = 0; team < (practice ? 1 : 2); team++)
            {
                var weapons = pickTeam(team == 0 && !attract ? (ported.Contains(o.weapon) ? o.weapon : "shooter") : null);
                for (int s = 0; s < (practice ? 1 : MATCH.teamSize); s++)
                {
                    bool isLocal = team == 0 && s == 0 && !attract;
                    // your look from the Locker (an empty style resolves from your name); bots get random looks
                    var style = isLocal ? new CharacterStyle.Style() : CharacterStyle.randomStyle(() => UnityEngine.Random.value);
                    // subs: yours from the loadout; bots carry a random one (about half keep their weapon's default)
                    string sub = isLocal ? o.sub : random() < 0.5f ? null : subs[(int)(random() * subs.Length)];
                    string weapon = weapons[s], special = null;
                    if (rosterRng != null && (!isLocal || o.autopilot))
                    {
                        weapon = Config.WEAPON_ORDER[(int)(rosterRng() * Config.WEAPON_ORDER.Length)];
                        sub = Config.SUB_ORDER[(int)(rosterRng() * Config.SUB_ORDER.Length)];
                        special = Config.SPECIAL_ORDER[(int)(rosterRng() * Config.SPECIAL_ORDER.Length)];
                    }
                    var a = new Actor(team, isLocal ? (o.playerName ?? "You") : names[ni++ % names.Count], weapon, sub, special, isLocal, !isLocal, s, style);
                    if (!isLocal || o.autopilot) a.bot = new BotBrain(a, o.difficulty);
                    actors.Add(a);
                }
            }
            G.actors.Clear(); G.actors.AddRange(actors);
            local = actors.FirstOrDefault(a => a.isLocal);
            G.local = local;
            if (local != null && !o.autopilot) controller = new PlayerController(local, o.rig, o.input);
            // initial placement on the spawn decks (standing, no drop)
            foreach (var a in actors)
            {
                var pad = G.level.spawnPads[a.team];
                float ang = (a.slot / 4f) * Mathf.PI * 2 + 0.6f, rr = 1.2f;
                a.spawnAt(new Vector3(pad.x + Mathf.Cos(ang) * rr, pad.y, pad.z + Mathf.Sin(ang) * rr), a.team == 0 ? 0 : Mathf.PI);
                a.invuln = 0;
                if (a.bot != null) { a.bot.aimYaw = a.yaw; a.bot.aimPitch = 0; }
            }
            unsubs = new List<Action> { on("splatted", onSplatted) };
        }

        public void start() => setState(attract || practice ? "playing" : "intro");

        public void setState(string s)
        {
            state = s; stateT = 0;
            emit("match:state", new Ev { state = s, match = this });
        }

        public void dispose()
        {
            foreach (var a in actors) { a.weaponRunner.reset(); a.dispose(); }
            unsubs?.ForEach(u => u());
            G.actors.Clear();
            G.local = null;
        }

        void onSplatted(Ev e) => events.Add((duration - time, e.victim as Actor, e.attacker as Actor, e.cause));

        public void update(float dt)
        {
            if (paused) return;
            stateT += dt;
            switch (state)
            {
                case "intro":
                    if (stateT > 4.2f) setState("playing");
                    break;
                case "playing":
                    {
                        if (practice) break;   // practice never runs out
                        time -= dt;
                        if (!attract)
                        {
                            if (!lastMinuteFired && time <= 60 && duration > 60) { lastMinuteFired = true; emit("match:oneminute"); }
                            int c = Mathf.CeilToInt(time);
                            if (time <= MATCH.finalCountdown && c != lastCount && c > 0) { lastCount = c; emit("match:count", new Ev { n = c }); }
                        }
                        if (time <= 0) { time = 0; if (state == "playing") setState("finish"); }
                        break;
                    }
                case "finish":
                    if (stateT > 2.6f && result == null) judge();
                    break;
            }
            // actors (the local controller runs once per rendered frame via updateController)
            bool live = state == "playing";
            foreach (var a in actors)
            {
                if (a.bot != null)
                {
                    if (live) a.bot.update(dt);
                    else { a.intent.move = Vector3.zero; a.intent.fire = a.intent.squid = a.intent.sub = a.intent.jump = a.intent.special = false; }
                }
            }
            foreach (var a in actors) a.update(dt);
            // soft push between actors
            for (int i = 0; i < actors.Count; i++)
                for (int j = i + 1; j < actors.Count; j++)
                {
                    var a = actors[i]; var b = actors[j];
                    if (!a.alive || !b.alive) continue;
                    float dx = b.pos.x - a.pos.x, dz = b.pos.z - a.pos.z, dy = b.pos.y - a.pos.y;
                    float d2 = dx * dx + dz * dz;
                    float r = PLAYER.radius * 1.7f;
                    if (d2 < r * r && Mathf.Abs(dy) < 1.2f && d2 > 1e-5f)
                    {
                        float d = Mathf.Sqrt(d2), push = r - d;
                        a.pos.x -= (dx / d) * push * 0.5f; a.pos.z -= (dz / d) * push * 0.5f;
                        b.pos.x += (dx / d) * push * 0.5f; b.pos.z += (dz / d) * push * 0.5f;
                    }
                }
        }

        public void updateController(float dt)
        {
            if (controller == null) return;
            controller.enabled = state == "playing" && !paused && local.alive;
            controller.update(dt);
        }

        void judge()
        {
            var cov = G.paint.coverage();
            int win = cov.x == cov.y ? (random() < 0.5f ? 0 : 1) : cov.x > cov.y ? 0 : 1;
            result = new MatchResult { coverage = cov, winner = win };
            setState("judge");
        }
    }
}
