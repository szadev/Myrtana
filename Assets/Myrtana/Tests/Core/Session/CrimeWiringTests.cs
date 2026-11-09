using System.Linq;
using Myrtana.Core;
using Myrtana.Import.G3;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using Myrtana.Story;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>Crime milestone 2 (Docs/perception-crime.md §9): zones, enclaves, guard news, grudges, story hooks.</summary>
    public class CrimeWiringTests
    {
        static readonly ActorId Guard = new ActorId("Faring_Guard"), Boss = new ActorId("Faring_Boss");

        /// <summary>A 20 m square quarter (outdoor, public) with a 4 m house (indoor, enclave) at its centre.</summary>
        static ZoneMap Faring() => new ZoneMap(new[]
        {
            new AiZoneDef("quarter", "Quarter", "Faring", ZoneSecurity.Public, false, new float[] { -10, -10, 10, -10, 10, 10, -10, 10 }, 0, 3, 1),
            new AiZoneDef("house", "House", "Faring", ZoneSecurity.Enclave, true, new float[] { -2, -2, 2, -2, 2, 2, -2, 2 }, 0, 1.5f, 1),
        }, new[] { new EnclaveDef("Faring", "Orc") });

        [Test]
        public void ZoneMap_PrefersTheHouseInsideTheQuarter_AndKeepsToTheHeightBand()
        {
            var zones = Faring();
            var house = zones.ZoneAt(new WorldPose(0, 0.5f, 0, 0));
            Assert.That((house.Id, house.Indoor, house.Security, house.Owner, house.OwnerAlignment),
                        Is.EqualTo(("house", true, ZoneSecurity.Enclave, "Faring", "Orc")));
            Assert.That(zones.ZoneAt(new WorldPose(5, 0, 5, 0)).Id, Is.EqualTo("quarter"));
            Assert.That(zones.ZoneAt(new WorldPose(0, 2.5f, 0, 0)).Id, Is.EqualTo("quarter"), "above the house's band, still in the quarter");
            Assert.That(zones.ZoneAt(new WorldPose(30, 0, 0, 0)).IsOwned, Is.False);
            Assert.That(zones.IndoorZoneAt(new WorldPose(1, 0, 1, 0)), Is.EqualTo("house"));
            Assert.That(zones.IndoorZoneAt(new WorldPose(5, 0, 5, 0)), Is.Null);
        }

        static SessionFixture Fixture() => new SessionFixture()
            .Add(SimulationSchema.PerceptionRulesFolder, G3CrimeImporter.Perception())
            .Add(RulesSchema.AttitudeRulesFolder, G3CrimeImporter.Attitudes())
            .Add(RulesSchema.CrimeRulesFolder, G3CrimeImporter.Crime())
            .Add(RulesSchema.EnclaveFolder, new EnclaveDef("Faring", "Orc"))
            .Add(SimulationSchema.NpcFolder,
                 new NpcDef(Guard, new TextId("FO_Guard"), "Orc_Warrior_Hard").WithCombat(Orc(NpcRole.Guard)),
                 new NpcDef(Boss, new TextId("FO_Boss"), "Orc_Warrior_Hard").WithCombat(Orc(NpcRole.Political)));

        static NpcCombatDef Orc(NpcRole role) =>
            new NpcCombatDef(5, 10, "Orc", true, boss: role == NpcRole.Political, role: role, hasEnclave: true, faction: "Orc", enclave: "Faring");

        [Test]
        public void GuardNews_FollowTheTheftCount_AndOpenTheBribe()
        {
            using (var f = Fixture())
            {
                var s = f.NewGame();
                var talk = s.Crime.Talk;
                Assert.That(s.Crime.Attitudes.Facts.TryGetProfile(Guard, out var guard) && guard.Guard && guard.EnclaveAlignment == "Orc", Is.True);
                Assert.That(talk.HasGuardNews(Guard), Is.False);

                s.Crime.Ledger.Count("Faring", CrimeType.Theft, 20);
                Assert.That(talk.HasGuardNews(Guard), Is.True);
                Assert.That(talk.HasBossNews(Boss), Is.True);
                var first = talk.TellGuardNews(Guard);
                Assert.That(first.Lines, Is.EqualTo(new[] { "Halt", "FirstTheft", "No" }));
                Assert.That(first.OffersWayOut, Is.False, "a first warning");
                Assert.That(talk.HasGuardNews(Guard), Is.False, "discussed");

                s.Crime.Ledger.Count("Faring", CrimeType.Theft, 20);
                var second = talk.TellGuardNews(Guard);
                Assert.That((second.Lines.Last(), second.OffersWayOut), Is.EqualTo(("SecondTheft", true)));
                var bribe = talk.Bribe(Guard);
                Assert.That(bribe.Attacks, Is.False);
                Assert.That(new[] { "ThisTimeYoureLucky", "NoMoreTroubleHere" }, Does.Contain(bribe.Lines.Single()));

                s.Crime.Ledger.Report("Faring", CrimeType.Murder);
                s.Crime.Ledger.Count("Faring", CrimeType.Murder);
                Assert.That(talk.TellGuardNews(Guard).Lines[1], Is.EqualTo("KnownMurder"));
                var refused = talk.Bribe(Guard);
                Assert.That((refused.Lines.Single(), refused.Attacks), Is.EqualTo(("No", true)));
                Assert.That(s.NpcCombat.Get(Guard).State.Reason, Is.EqualTo(AttackReason.Revolution), "a known murder mobilises the enclave");

                talk.ResetCrime(Guard);
                Assert.That(s.Crime.Ledger.Enclave("Faring").IsClean, Is.True);
                Assert.That(s.NpcCombat.Get(Guard).IsFighting, Is.False, "members drop the player");
            }
        }

        [Test]
        public void TheDialogRunsTheGuardNewsAndSetsTheWayOutFlags()
        {
            using (var f = Fixture()
                .Add(StorySchema.DialogFolder, new DialogEntryDef(new Id<DialogEntryDef>("NEWS"), ActorId.Player, default, DialogEntryKind.Important,
                                                                  1, new TextId("NEWS"), true, false, false, null, new HasGuardNews(),
                                                                  null, new DialogCommand[] { new SayGuardNews() }),
                                               new DialogEntryDef(new Id<DialogEntryDef>("HELLO"), Guard, default, DialogEntryKind.Choice,
                                                                  2, new TextId("HELLO"), true, false, false, null, null, null, null)))
            {
                var s = f.NewGame();
                s.Crime.Ledger.Count("Faring", CrimeType.Theft, 40);
                s.Dialog.Talk(Guard);
                Assert.That(s.Flags.IsSet(CrimeSpeechHandler<SayGuardNews>.BribeFlag), Is.True);
                Assert.That(s.Crime.Ledger.Enclave("Faring").DiscussedOf(CrimeType.Theft), Is.EqualTo(40));
            }
        }

        [Test]
        public void AGivenUpChaseLeavesAGrudge_AndTheChapterIsSaved()
        {
            using (var f = Fixture())
            {
                var s = f.NewGame();
                s.Events.Publish(new FightGivenUp(Guard, ActorId.Player, AttackReason.Theft));
                Assert.That(s.Crime.Attitudes.IsUpset(Guard, out var reason) && reason == AttackReason.Theft, Is.True);

                s.Fame.Add(FameKind.Enclave, "Faring", 80);
                Assert.That(new EnclaveReputation(s.Fame, s.Crime.Attitudes.Facts).EnclaveFameOf(Guard), Is.EqualTo(80));

                s.Chapter.Set(2);
                Assert.That(f.Reload(s).Chapter.Current, Is.EqualTo(2));
            }
        }
    }
}
