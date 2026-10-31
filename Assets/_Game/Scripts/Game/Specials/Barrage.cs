// Port of specials.js IMPL.barrage — Bomb Barrage (barrage, barrage_sticky, barrage_burst, barrage_seeker, barrage_mist):
// for the special's duration the sub button throws its bomb with no ink, as fast as its throw gap allows; the main weapon
// still works. The throwing itself lives in WeaponRunner.update (the sub section) and in Subs.cs / Projectiles.
namespace Inkwave
{
    public class Barrage : SpecialImpl
    {
        public override void start(SpecialSystem sys, Actor a, SpecialState s)
        {
            s.bomb = s.def.bomb != null && Config.SUBS.TryGetValue(s.def.bomb, out var b) ? b : Config.SUBS["bomb"];
            s.nextThrow = 0;
            a.character.setSub(s.bomb.kind);
        }
        public override void end(SpecialSystem sys, Actor a, SpecialState s, string reason)
        {
            a.character.setSub((a.sub ?? Config.SUBS["bomb"]).kind);
            a.weaponRunner.aimingSub = false;
        }
    }
}
