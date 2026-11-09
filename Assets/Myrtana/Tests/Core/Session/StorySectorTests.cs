using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using Myrtana.Story;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// story-4 (Docs/story-coverage.md §6): world items and objects of story sectors follow SetSectorStatus, CP's
    /// liberation world switch of the towns, every enclave alarm puts the enclave into Attack, talking objects.
    /// </summary>
    public class StorySectorTests
    {
        const string Lights = "G3_Nordmar_01_Ancestor_Lights_01";
        const string FaringOrcs = "G3_Myrtana_01_Faring_NPC_01", FaringRebels = "G3_Myrtana_01_Faring_NPC_02";

        static NpcDef Npc(string name, string faction, string enclave, string sector, float x = 0) =>
            new NpcDef(new ActorId(name), new TextId("FO_" + name), "Orc_Warrior_Hard", faction == "Orc" ? "Orc" : "Human", "visual",
                       new WorldPose(x, 0, 0, 0), combat: new NpcCombatDef(1, 1, faction == "Orc" ? "Orc" : "Human", true, faction: faction, enclave: enclave))
                .WithSector(sector);

        static SessionFixture Fixture() => new SessionFixture()
            .Add(SimulationSchema.SectorFolder, new WorldSectorDef(Lights, false), new WorldSectorDef(FaringOrcs, true),
                 new WorldSectorDef(FaringRebels, false))
            .Add(RulesSchema.EnclaveFolder, new EnclaveDef("Faring", "Orc", new[] { 600f, 70f, 550f }), new EnclaveDef("Geldern", "Orc"));

        [Test]
        public void WorldItemsAndObjects_OfASwitchedOffSector_AreNotInTheWorld_UntilItIsOn()
        {
            var cup = new WorldItemDef("w1", "It_FiremageCup", 1, new[] { 1f, 0f, 1f }, null, null, 1f, default, Lights);
            var loose = new WorldItemDef("w2", "It_Apple", 1, new[] { 2f, 0f, 2f }, null, null, 1f, default);
            var altar = new InteractiveObjectDef("o1", "Living_Ancestor_01_Tester", InteractiveKind.Speaker, "None", null, new[] { 0f, 0f, 0f },
                                                 null, null, 1f, default, null, null, null, null, null, Lights);
            using (var f = Fixture().Add(RulesSchema.WorldItemFolder, cup, loose).Add(RulesSchema.InteractiveObjectFolder, altar))
            {
                var s = f.NewGame();
                var removed = new List<string>();
                s.Items.World.Removed += removed.Add;
                Assert.That(s.Items.World.IsPresent("w1"), Is.False, "the sector is off at the start");
                Assert.That(s.Items.World.IsPresent("w2"), Is.True);
                Assert.That(s.Items.World.PickUp("w1"), Is.False);
                Assert.That(s.Interaction.Objects.IsInWorld("o1"), Is.False);

                s.WorldState.Director.SetSectorEnabled(Lights, true);
                Assert.That(s.Items.World.IsPresent("w1"), Is.True);
                Assert.That(s.Interaction.Objects.IsInWorld("o1"), Is.True);

                s.WorldState.Director.SetSectorEnabled(Lights, false);
                Assert.That(removed, Is.EqualTo(new[] { "w1" }), "the views drop what leaves the world");
                Assert.That(s.Items.World.IsPresent("w1"), Is.False);

                s.WorldState.Director.SetSectorEnabled(Lights, true);
                Assert.That(s.Items.World.PickUp("w1"), Is.True);
                var loaded = f.Reload(s);
                Assert.That(loaded.Items.World.IsPresent("w1"), Is.False, "taken");
                Assert.That(loaded.Interaction.Objects.IsInWorld("o1"), Is.True, "the sector stays on after loading");
            }
        }

        [Test]
        public void LiberatedTown_IsExchanged_OnceTheHeroIsMoreThan200mAway()
        {
            var f = Fixture()
                .Add(SimulationSchema.NpcFolder, Npc("Faring_Orc_0", "Orc", "Faring", FaringOrcs, 600), Npc("Faring_Orc_1", "Orc", "Faring", FaringOrcs, 601),
                     Npc("Faring_Rebel", "Reb", "Faring", FaringRebels, 602));
            using (f)
            {
                var s = f.NewGame();
                s.Enclaves.SetStatus("Faring", EnclaveStatus.Flee);
                s.Crime.ReportPlayer(new WorldPose(650, 70, 560, 0));
                s.WorldState.CheckNow();
                Assert.That(s.Enclaves.IsSwitched("Faring"), Is.False, "the hero is near");
                Assert.That(s.WorldState.Sectors.IsEnabled(FaringOrcs), Is.True);

                s.Crime.ReportPlayer(new WorldPose(900, 70, 560, 0));
                s.WorldState.CheckNow();
                Assert.That(s.Enclaves.IsSwitched("Faring"), Is.True);
                Assert.That(s.WorldState.Sectors.IsEnabled(FaringOrcs), Is.False);
                Assert.That(s.WorldState.Sectors.IsEnabled(FaringRebels), Is.True);
                Assert.That(s.Enclaves.Of("Faring").Status, Is.EqualTo(EnclaveStatus.Routine), "ChangeEnclaveStatus(Routine)");
                Assert.That(s.Enclaves.AlignmentOverride("Faring"), Is.EqualTo(EnclaveStates.NoAlignment), "CP: Faring gets no alignment");
                Assert.That(s.Crime.Attitudes.Facts.TryGetProfile(new ActorId("Faring_Rebel"), out var p) ? p.EnclaveAlignment : "?", Is.Null);
                s.Npcs.TryGet(new ActorId("Faring_Orc_0"), out var orc);
                Assert.That(s.Npcs.IsInWorld(orc), Is.False);

                var loaded = f.Reload(s);
                Assert.That(loaded.Enclaves.IsSwitched("Faring"), Is.True);
                Assert.That(loaded.WorldState.Sectors.IsEnabled(FaringRebels), Is.True);
                Assert.That(loaded.Crime.Attitudes.Facts.TryGetProfile(new ActorId("Faring_Rebel"), out var q) ? q.EnclaveAlignment : "?", Is.Null);
            }
        }

        [Test]
        public void EnclaveAlarm_PutsTheEnclaveIntoAttack_ButAKillOnlyOnceAMurderIsKnownOrTheBossDied()
        {
            using (var f = Fixture().Add(SimulationSchema.NpcFolder, Npc("Geldern_Orc", "Orc", "Geldern", null, 3))
                                    .Add(StorySchema.QuestFolder, new QuestDef(new Id<QuestDef>("Beliar_Contact"), new TextId("QUEST_Beliar_Contact"), null)))
            {
                var s = f.NewGame();
                s.Crime.ReportPlayer(new WorldPose(0, 0, 0, 0));
                var crime = s.Crime.Crime;

                crime.RaiseEnclaveAlarm("Geldern", ActorId.Player, murder: true);
                Assert.That(s.Enclaves.Of("Geldern").Status, Is.EqualTo(EnclaveStatus.Routine), "an unknown murder");
                crime.RaiseEnclaveAlarm("Geldern", ActorId.Player, murder: true, bossKilled: true);
                Assert.That(s.Enclaves.Of("Geldern").Status, Is.EqualTo(EnclaveStatus.Attack), "the boss died");

                s.Enclaves.SetStatus("Geldern", EnclaveStatus.Routine);
                s.Quests.TrySetStatus(new Id<QuestDef>("Beliar_Contact"), QuestStatus.Running);
                s.Quests.TrySetStatus(new Id<QuestDef>("Beliar_Contact"), QuestStatus.Success);
                crime.RaiseEnclaveAlarm("Geldern", ActorId.Player, murder: true, bossKilled: true);
                Assert.That(s.Enclaves.Of("Geldern").Status, Is.EqualTo(EnclaveStatus.Routine), "after Beliar_Contact a boss kill alarms no one");

                s.Crime.Ledger.Report("Geldern", CrimeType.Murder);
                crime.RaiseEnclaveAlarm("Geldern", ActorId.Player, murder: true);
                Assert.That(s.Enclaves.Of("Geldern").Status, Is.EqualTo(EnclaveStatus.Attack), "a known murder");

                s.Enclaves.SetStatus("Geldern", EnclaveStatus.Routine);
                crime.RaiseEnclaveAlarm("Geldern", ActorId.Player);
                Assert.That(s.Enclaves.Of("Geldern").Status, Is.EqualTo(EnclaveStatus.Attack), "any other alarm (revolution, raid)");

                s.Enclaves.SetStatus("Geldern", EnclaveStatus.Flee);
                Assert.That(crime.RaiseEnclaveAlarm("Geldern", ActorId.Player), Is.Empty, "a fleeing enclave defends nothing");
                Assert.That(s.Enclaves.Of("Geldern").Status, Is.EqualTo(EnclaveStatus.Flee));

                s.Enclaves.SetStatus("Faring", EnclaveStatus.Routine);
                crime.RaiseEnclaveAlarm("Faring", ActorId.Player, victim: new ActorId("Orc_Scout_05"));
                Assert.That(s.Enclaves.Of("Faring").Status, Is.EqualTo(EnclaveStatus.Routine), "CP: Faring's story orcs");
            }
        }

        [Test]
        public void TalkingObject_OwnsItsTopics()
        {
            var furnace = new InteractiveObjectDef("o2", "Erzofen", InteractiveKind.Speaker, "None", null, new[] { 0f, 0f, 0f },
                                                   null, null, 1f, default, null, null, null, null);
            using (var f = Fixture().Add(RulesSchema.InteractiveObjectFolder, furnace))
            {
                var s = f.NewGame();
                Assert.That(s.Interaction.Objects.TryGet("o2", out var def) && def.Kind == InteractiveKind.Speaker, Is.True);
                Assert.That(s.Interaction.Objects.IsInWorld("o2"), Is.True, "no sector: always there");
            }
        }

        [Test]
        public void SayHoldingPosition_PartyWaitForVoicedSets_ElseSure()
        {
            var cmd = new SayShared(ActorRef.Partner, "PartyWait", 0, "Sure", new[] { "Reb_Warrior_Hard" });
            Assert.That(cmd.LineFor("Reb_Warrior_Hard"), Is.EqualTo("PartyWait"));
            Assert.That(cmd.LineFor("Slave_Weak"), Is.EqualTo("Sure"));
            Assert.That(new SayShared(ActorRef.Partner, "Sure").LineFor("x"), Is.EqualTo("Sure"));
        }

        [Test]
        public void LiberationTable_ListsEveryTownOfCp()
        {
            var towns = EnclaveSectorSwitches.All.Select(t => t.Enclave).ToList();
            Assert.That(towns, Has.Count.EqualTo(19));
            Assert.That(towns, Does.Contain("Montera").And.Contain("Enclave_Lago").And.Contain("MoraSul_Temple"));
            Assert.That(EnclaveSectorSwitches.All.Where(t => t.SectorOn == null).Select(t => t.Enclave),
                        Is.EquivalentTo(new[] { "Nemora", "Okara", "Reddock", "Vangard" }));
            Assert.That(EnclaveSectorSwitches.All.Single(t => t.Enclave == "Braga").Alignment, Is.EqualTo("Nom"));
        }
    }
}
