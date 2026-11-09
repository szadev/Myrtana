using System.Linq;
using Myrtana.Core;
using Myrtana.Session.Dev;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-151: the console 'enemies' scan (<see cref="CombatThreats"/>).</summary>
    public class CombatThreatsTests
    {
        static readonly ActorId Wolf = new ActorId("Wolf#01"), Orc = new ActorId("Orc#02"), Gorn = new ActorId("Gorn"),
                                Bandit = new ActorId("Bandit#03"), Scav = new ActorId("Scavenger#04"), Deer = new ActorId("Deer#05");

        static ThreatFighter F(ActorId id, CombatPhase phase, ActorId target, bool token = false, bool atCoord = true, float d = 10f) =>
            new ThreatFighter(id, phase, target, token, atCoord, d);

        [Test]
        public void Battle_IsTheSaveBlockRule()
        {
            var list = CombatThreats.Collect(new[]
            {
                F(Wolf, CombatPhase.Fight, ActorId.Player, token: true),
                F(Orc, CombatPhase.Fight, ActorId.Player, token: false),            // waiting group fighter: no save block
                F(Scav, CombatPhase.Fight, ActorId.Player, token: false, atCoord: false), // engaged this frame: counts
            }, _ => false);
            Assert.That(list.Single(t => t.Id == Wolf).Reasons & ThreatReason.Battle, Is.EqualTo(ThreatReason.Battle));
            Assert.That(list.Single(t => t.Id == Orc).Reasons & ThreatReason.Battle, Is.EqualTo(ThreatReason.None));
            Assert.That(list.Single(t => t.Id == Scav).Reasons & ThreatReason.Battle, Is.EqualTo(ThreatReason.Battle));
            Assert.That(list.Single(t => t.Id == Orc).Reasons, Is.EqualTo(ThreatReason.Hero | ThreatReason.Music));
        }

        [Test]
        public void Music_OnlyWithin30m_NotFleeing_NotIdle()
        {
            var list = CombatThreats.Collect(new[]
            {
                F(Wolf, CombatPhase.WaitForFlee, ActorId.Player, d: 25f),
                F(Orc, CombatPhase.Fight, ActorId.Player, d: 45f),
                F(Deer, CombatPhase.Flee, ActorId.Player, d: 5f),
                F(Bandit, CombatPhase.Idle, ActorId.Player, d: 5f),
            }, _ => false);
            Assert.That(list.Single(t => t.Id == Wolf).Reasons, Is.EqualTo(ThreatReason.Hero | ThreatReason.Music));
            Assert.That(list.Single(t => t.Id == Orc).Reasons, Is.EqualTo(ThreatReason.Hero));
            Assert.That(list.Any(t => t.Id == Deer || t.Id == Bandit), Is.False);
        }

        [Test]
        public void Party_TargetsAndCompanionTargets()
        {
            var list = CombatThreats.Collect(new[]
            {
                F(Bandit, CombatPhase.Fight, Gorn, d: 12f),
                F(Gorn, CombatPhase.Fight, Scav, d: 3f),          // the companion runs to it
                F(Gorn, CombatPhase.Fight, ActorId.Player, d: 3f), // (a duel: never a threat row for the companion)
            }, id => id == Gorn, id => id == Scav ? 40f : float.NaN);
            Assert.That(list.Single(t => t.Id == Bandit).Reasons, Is.EqualTo(ThreatReason.Party | ThreatReason.Music));
            var scav = list.Single(t => t.Id == Scav);
            Assert.That(scav.Reasons, Is.EqualTo(ThreatReason.Companion));
            Assert.That(scav.DistanceMetres, Is.EqualTo(40f));
            Assert.That(list.Any(t => t.Id == Gorn), Is.False);
        }

        [Test]
        public void Ignore_LeavesOutDead_AndSortsBattleFirst()
        {
            var list = CombatThreats.Collect(new[]
            {
                F(Orc, CombatPhase.Fight, ActorId.Player, d: 2f),
                F(Wolf, CombatPhase.Fight, ActorId.Player, token: true, d: 20f),
                F(Deer, CombatPhase.Fight, ActorId.Player, token: true, d: 1f),
            }, _ => false, ignore: id => id == Deer);
            Assert.That(list.Select(t => t.Id), Is.EqualTo(new[] { Wolf, Orc }));
        }

        [TestCase(0f, 10f, "N")]
        [TestCase(10f, 0f, "E")]
        [TestCase(0f, -10f, "S")]
        [TestCase(-10f, 0f, "W")]
        [TestCase(7f, 7f, "NE")]
        [TestCase(-7f, -7f, "SW")]
        [TestCase(1f, 10f, "N")]
        [TestCase(-1f, 10f, "N")]
        [TestCase(0f, 0f, "-")]
        public void Compass_NorthIsPlusZ(float dx, float dz, string want) =>
            Assert.That(CombatThreats.Compass(dx, dz), Is.EqualTo(want));

        [TestCase(null, CombatThreats.Mode.On)]
        [TestCase("off", CombatThreats.Mode.Off)]
        [TestCase("LIST", CombatThreats.Mode.List)]
        [TestCase("fix", CombatThreats.Mode.Fix)]
        [TestCase("kill", CombatThreats.Mode.Kill)]
        public void Mode_Parses(string arg, CombatThreats.Mode want)
        {
            Assert.That(CombatThreats.TryParseMode(arg, out var got), Is.True);
            Assert.That(got, Is.EqualTo(want));
        }

        [Test]
        public void Mode_RejectsJunk() => Assert.That(CombatThreats.TryParseMode("boom", out _), Is.False);

        [Test]
        public void Flags_AndStuck()
        {
            var fine = new CombatThreats.Place { Cave = "Open", CaveOk = true, Path = "complete", OffMesh = 0f };
            Assert.That(CombatThreats.Flags(fine), Is.EqualTo(""));
            Assert.That(CombatThreats.Stuck(fine), Is.False);
            var partial = fine; partial.Path = "partial";
            Assert.That(CombatThreats.Stuck(partial), Is.False);
            var rock = new CombatThreats.Place { Inside = "Rock_02", Cave = "InRock", CaveOk = false, Path = "offmesh", OffMesh = 3.2f };
            Assert.That(CombatThreats.Flags(rock), Is.EqualTo("in:Rock_02 cave:InRock offmesh:3.2"));
            Assert.That(CombatThreats.Stuck(rock), Is.True);
            var nopath = fine; nopath.Path = "none";
            Assert.That(CombatThreats.Flags(nopath), Is.EqualTo("nopath"));
        }

        [Test]
        public void Line_HasEverything()
        {
            var t = CombatThreats.Collect(new[] { F(Wolf, CombatPhase.Fight, ActorId.Player, token: true, d: 12.34f) }, _ => false).Single();
            var place = new CombatThreats.Place { Inside = "Rock_02", Cave = "Open", CaveOk = true, Path = "none" };
            string line = CombatThreats.Line(t, "Wolf", 8f, 8f, 1f, 2f, 3f, place);
            Assert.That(line, Is.EqualTo("Wolf#01 \"Wolf\" 12.3m NE (1.0,2.0,3.0) battle,music,hero Fight->hero token path:none [in:Rock_02 nopath]"));
        }

        [Test]
        public void Op_RegisteredWithAlias()
        {
            Assert.That(ScenarioOps.TryGet("threats", out var spec), Is.True);
            Assert.That(spec.Name, Is.EqualTo("enemies"));
            var errors = new System.Collections.Generic.List<string>();
            var step = ScenarioOps.Bind("enemies", new[] { "fix" }, null, "enemies fix", errors);
            Assert.That(errors, Is.Empty);
            Assert.That(step.Get("mode"), Is.EqualTo("fix"));
        }
    }
}
