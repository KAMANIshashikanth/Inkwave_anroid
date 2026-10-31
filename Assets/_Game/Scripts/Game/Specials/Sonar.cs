// Port of specials.js IMPL.sonar — Deep Sonar: a short cast that reveals every enemy to your team (tracked: tags on
// screen and the map) for `duration`; revealed foes move slower and burn ink faster (Actor.cs reads status.reveal).
using UnityEngine;
using static Inkwave.Ctx;

namespace Inkwave
{
    public class Sonar : SpecialImpl
    {
        public override void start(SpecialSystem sys, Actor a, SpecialState s)
        {
            s.body = true; s.dur = s.def.cast;
            a.character.trigger("throw");
            foreach (var e in G.actors)
            {
                if (e.team == a.team) continue;
                e.status.reveal = s.def.duration; e.status.revealTeam = a.team;
                G.subs?.track(e, a.team, s.def.duration);
                if (e.isLocal) SpecialSystem.play("sonar_mark", new AudioOpts { volume = 0.8f });
            }
            SpecialSystem.play("sonar_ping", new AudioOpts { volume = 0.9f });
            emit("special:sonar", new Ev { actor = a, pos = a.pos });
            if (SpecialSystem.near(a.pos, 60))
                for (int k = 0; k < 3; k++)
                    G.fx?.ring(new Vector3(a.pos.x, a.pos.y + 0.1f, a.pos.z), Vector3.up, a.color, new FxOpts { radius = 6 + k * 7, life = 0.8f + k * 0.25f });
        }
        public override void body(SpecialSystem sys, Actor a, SpecialState s, float dt) { sys.stand(a, dt); }
    }
}
