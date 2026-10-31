// Port of specials.js IMPL.bubbler — Bubble Guard: a force field that turns every hit into a shove instead of damage;
// touching teammates shares it. The field itself (timer, mesh, sharing, filterDamage) lives in SpecialSystem.
namespace Inkwave
{
    public class Bubbler : SpecialImpl
    {
        public override void start(SpecialSystem sys, Actor a, SpecialState s) { sys.giveShield(a, s.def.duration, true); }
        // (the field runs out on its own timer)
    }
}
