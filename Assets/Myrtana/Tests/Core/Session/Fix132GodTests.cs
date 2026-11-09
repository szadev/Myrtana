using Myrtana.Core;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-132 (owner, build 18f): the dev god / tp immunity is a damage filter that never writes the hero's HP attribute,
    /// and saves left with the old god's 99999/99999 HP get the real maximum back on load.
    /// </summary>
    public class Fix132GodTests
    {
        SessionFixture _f;

        [SetUp]
        public void SetUp() => _f = new SessionFixture();

        [TearDown]
        public void TearDown() => _f.Dispose();

        [Test]
        public void Invulnerable_TakesNoHarm_AndLeavesTheHpAttributeAlone()
        {
            var game = _f.NewGame();
            var vitals = game.Combat.Vitals;
            int max = game.Hero.Stats.Maximum("HP");
            game.Hero.Stats.Set("HP", new StatValue(max - 30, max));
            bool god = true;
            vitals.PlayerInvulnerable = () => god;

            vitals.AddHitPoints(ActorId.Player, -500);
            vitals.KnockOut(ActorId.Player, byPlayerParty: false);
            vitals.Kill(ActorId.Player, byPlayerParty: false);
            Assert.That(vitals.HitPoints(ActorId.Player), Is.EqualTo(max - 30));
            Assert.That(vitals.HitPointsMax(ActorId.Player), Is.EqualTo(max));
            Assert.That(vitals.StateOf(ActorId.Player), Is.EqualTo(VitalState.Alive));
            vitals.AddHitPoints(ActorId.Player, 10);
            Assert.That(vitals.HitPoints(ActorId.Player), Is.EqualTo(max - 20), "healing still works");

            // the combat snapshot never sees a lethal hit, the stat keeps its own values
            Assert.That(game.Combat.PlayerCombatant(game.Hero).HitPoints, Is.EqualTo(ActorVitals.InvulnerableHitPoints));
            Assert.That(game.Hero.Stats.Permanent("HP").Maximum, Is.EqualTo(max));

            god = false;
            Assert.That(game.Combat.PlayerCombatant(game.Hero).HitPoints, Is.EqualTo(max - 20));
            vitals.AddHitPoints(ActorId.Player, -5);
            Assert.That(vitals.HitPoints(ActorId.Player), Is.EqualTo(max - 25));
        }

        [Test]
        public void Load_RepairsTheOldDevGodHitPoints()
        {
            var fresh = _f.NewGame();
            int start = fresh.Hero.Stats.Permanent("HP").Maximum;
            var game = _f.NewGame();
            game.Hero.Stats.Set("HP", new StatValue(99095, 99999)); // the owner's save (build 18f)
            var loaded = _f.Reload(game);
            Assert.That(loaded.Hero.Stats.Permanent("HP").Maximum, Is.EqualTo(start));
            Assert.That(loaded.Hero.Stats.Value("HP"), Is.EqualTo(start));

            game.Hero.Stats.Set("HP", new StatValue(120, 260)); // an honest save stays as it is
            loaded = _f.Reload(game);
            Assert.That(loaded.Hero.Stats.Permanent("HP"), Is.EqualTo(new StatValue(120, 260)));
        }
    }
}
