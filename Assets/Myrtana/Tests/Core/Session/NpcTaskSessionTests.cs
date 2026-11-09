using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Import.G3;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>par-npc2 in a whole session: the KO get-up line, the liberation of an enclave, JoinNPC.</summary>
    public class NpcTaskSessionTests
    {
        static readonly ActorId Loyal = new ActorId("Orc_Faring_01"), Near = new ActorId("Orc_Faring_02"),
                                Slave = new ActorId("Slave_Faring_01"), Leader = new ActorId("Orc_Faring_Boss");

        static SessionFixture Fixture() => new SessionFixture()
            .Add(SimulationSchema.PerceptionRulesFolder, G3CrimeImporter.Perception())
            .Add(RulesSchema.AttitudeRulesFolder, G3CrimeImporter.Attitudes())
            .Add(RulesSchema.CrimeRulesFolder, G3CrimeImporter.Crime())
            .Add(RulesSchema.EnclaveFolder, new EnclaveDef("Faring", "Orc"))
            .Add(SimulationSchema.NpcFolder, Npc(Loyal, 2), Npc(Near, 4), Npc(Slave, 6, "Human", "Slave"), Npc(Leader, 8));

        static NpcDef Npc(ActorId id, float x, string species = "Orc", string faction = "Orc") =>
            new NpcDef(id, new TextId("FO_" + id.Name), "Orc_Warrior_Hard", species, "visual", new WorldPose(x, 0, 0, 0),
                       combat: new NpcCombatDef(5, 10, species, true, faction: faction, enclave: "Faring", hasEnclave: true));

        [Test]
        public void AKnockedOutNpc_SaysOhMyHeadOrNextTime_WhenItGetsUp()
        {
            using (var f = Fixture())
            {
                var s = f.NewGame();
                var shouts = new List<CombatShout>();
                s.Events.Subscribe<CombatShout>(shouts.Add);
                s.Combat.Vitals.KnockOut(Near, byPlayerParty: false);
                s.Combat.Vitals.Tick(60);
                Assert.That(shouts.Where(e => e.Speaker == Near).Select(e => e.Line),
                            Has.Some.Matches<string>(l => l == "OhMyHead" || l == "NextTimeYoureInForIt"));
            }
        }

        /// <summary>
        /// fix-156 (Nordmar duel with Hogar: a watcher shouted four cheers in a few seconds): CheerFight and "ThereIsAFight"
        /// share G3's say gate kind 6, 0.0018 of the HH.MM playing time = one line per game minute (5 real seconds).
        /// </summary>
        [Test]
        public void FightWatchers_CheerAtMostOncePerGameMinute()
        {
            using (var f = Fixture())
            {
                var s = f.NewGame();
                var lines = new List<(double hours, ActorId who, string line)>();
                s.Events.Subscribe<CombatShout>(e => lines.Add((s.Clock.GameSeconds / 3600.0, e.Speaker, e.Line)));
                s.Events.Publish(new NpcReacted(Loyal, Reaction.Watch(Near, Leader), null));
                s.Events.Publish(new NpcReacted(Slave, Reaction.Watch(Near, Leader), null));
                for (int i = 0; i < 40; i++) // a hit every 0.5 s for 20 s
                {
                    s.Events.Publish(new HitResolved(Near, Leader, Stumble()));
                    s.Tick(0.5f);
                }
                var said = lines.Where(l => l.who == Loyal || l.who == Slave).ToList();
                Assert.That(said.Count, Is.InRange(2, 5), string.Join(", ", said.Select(l => l.line)));
                var minutes = said.Select(l => SpeechGate.PlayingTime(l.hours)).ToList();
                Assert.That(minutes, Is.Unique, "never two watch lines in one game minute");
                Assert.That(said.Select(l => l.line), Has.Some.Matches<string>(l => l.StartsWith("Ooh_") || l.StartsWith("Cheer_")));
            }
        }

        static HitOutcome Stumble()
        {
            var o = HitOutcome.Ignored(HitGate.None);
            typeof(HitOutcome).GetProperty(nameof(HitOutcome.Result)).SetValue(o, HitResult.Hit);
            typeof(HitOutcome).GetProperty(nameof(HitOutcome.Reaction)).SetValue(o, HitReaction.Stumble);
            return o;
        }

        [Test]
        public void ALiberatedEnclave_LosesItsLoyalMembersOutOfRange_AndItsOppressedApplaud()
        {
            using (var f = Fixture())
            {
                var s = f.NewGame();
                s.Npcs.SetDetail(Near, DetailLevel.Near);
                s.Npcs.SetDetail(Slave, DetailLevel.Near);
                var applause = new List<NpcApplauds>();
                s.Events.Subscribe<NpcApplauds>(applause.Add);
                s.Events.Publish(new EnclaveLiberated("Faring"));
                Assert.That(s.Npcs.TryGet(Loyal, out _), Is.False, "the loyal member out of range fled for good");
                Assert.That(s.Npcs.TryGet(Near, out _), Is.True, "the loyal member in range stays");
                Assert.That(applause.Select(a => (a.Npc, a.Target)), Is.EqualTo(new[] { (Slave, ActorId.Player) }));
            }
        }

        [Test]
        public void JoinNpc_MakesAMemberFollowItsNpcLeader_AndIsSaved()
        {
            using (var f = Fixture())
            {
                var s = f.NewGame();
                Assert.That(s.WorldState.Director.JoinNpc(Near, Leader), Is.True);
                Assert.That(s.Life.Parties.TryGetLeader(Near, out var leader) && leader == Leader, Is.True);
                var loaded = f.Reload(s);
                Assert.That(loaded.Life.Parties.TryGetLeader(Near, out leader) && leader == Leader, Is.True);
            }
        }
    }
}
