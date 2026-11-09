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
    /// story-2 in a whole session (Docs/story-coverage.md): world sectors, G3's town switches, doors, enclave flags,
    /// liberation (FreeEnclave), CP's Ardea staging, EnterArea quests over the imported zones, NPC classes and EndGame.
    /// </summary>
    public class WorldStateTests
    {
        const string GothaRebels = "G3_Myrtana_01_Gotha_NPC_02";
        static readonly ActorId Rebel = new ActorId("RebWarrior_01");

        static NpcDef Orc(string name, string enclave, float x = 0) =>
            new NpcDef(new ActorId(name), new TextId("FO_" + name), "Orc_Warrior_Hard", "Orc", "visual", new WorldPose(x, 0, 0, 0),
                       combat: new NpcCombatDef(1, 1, "Orc", true, faction: "Orc", enclave: enclave));

        static QuestDef Quest(string id, QuestObjectiveKind kind, string destination, params string[] targets) =>
            new QuestDef(new Id<QuestDef>(id), new TextId("QUEST_" + id), null,
                         new QuestObjective(kind, targets.Select(t => new QuestTarget(t, 1)).ToList(), new ActorId(destination)));

        static SessionFixture Fixture() => new SessionFixture()
            .Add(SimulationSchema.SectorFolder, new WorldSectorDef(GothaRebels, false), new WorldSectorDef("G3_Myrtana_01_Gotha_NPC_01", true))
            .Add(SimulationSchema.NpcFolder, new NpcDef(Rebel, new TextId("FO_Reb"), "Hum_Warrior", "Human", "visual", new WorldPose(5, 0, 0, 0))
                                                 .WithSector(GothaRebels))
            .Add(RulesSchema.EnclaveFolder, new EnclaveDef("Ardea", "Orc"), new EnclaveDef("Gotha", "Orc"), new EnclaveDef("Silden", "Orc"));

        [Test]
        public void SwitchedOffSector_KeepsItsNpcsOutOfTheWorld_UntilTheStorySwitchesItOn_AndSurvivesSaveLoad()
        {
            using (var f = Fixture())
            {
                var s = f.NewGame();
                s.Npcs.TryGet(Rebel, out var rebel);
                Assert.That(rebel, Is.Not.Null);
                Assert.That(s.Npcs.IsInWorld(rebel), Is.False);

                s.WorldState.Director.SetSectorEnabled(GothaRebels, true);
                Assert.That(s.Npcs.IsInWorld(rebel), Is.True);
                s.WorldState.Director.SetSectorEnabled("G3_Myrtana_01_Gotha_NPC_01", false);

                var loaded = f.Reload(s);
                loaded.Npcs.TryGet(Rebel, out var again);
                Assert.That(loaded.Npcs.IsInWorld(again), Is.True);
                Assert.That(loaded.WorldState.Sectors.IsEnabled("G3_Myrtana_01_Gotha_NPC_01"), Is.False);
                Assert.That(loaded.WorldState.Sectors.IsEnabled("Unknown_Sector"), Is.True);
            }
        }

        [Test]
        public void GothaRebelSector_SwitchesGothaToTheRebels_ForgivesCrimes_AndEndsTheFight()
        {
            using (var f = Fixture().Add(SimulationSchema.NpcFolder, Orc("Gotha_Orc", "Gotha")))
            {
                var s = f.NewGame();
                s.Enclaves.Set("Gotha", new EnclaveState(EnclaveStatus.Attack, true, true));
                s.Crime.Ledger.Count("Gotha", CrimeType.Murder, 2);

                s.WorldState.Director.SetSectorEnabled(GothaRebels, true);

                Assert.That(s.Enclaves.AlignmentOverride("Gotha"), Is.EqualTo("Reb"));
                Assert.That(s.Enclaves.IsSwitched("Gotha"), Is.True);
                Assert.That(s.Enclaves.Of("Gotha"), Is.EqualTo(new EnclaveState(EnclaveStatus.Routine, false, false)));
                Assert.That(s.Crime.Ledger.Enclave("Gotha").CountOf(CrimeType.Murder), Is.EqualTo(0));
                Assert.That(s.Crime.Attitudes.Facts.TryGetProfile(new ActorId("Gotha_Orc"), out var p) ? p.EnclaveAlignment : null, Is.EqualTo("Reb"));

                Assert.That(s.Fame.Of(FameKind.Enclave, "Gotha"), Is.EqualTo(100), "fix-144: ChangeEnclaveStatus(Routine) sets fame 100");

                var loaded = f.Reload(s);
                Assert.That(loaded.Enclaves.AlignmentOverride("Gotha"), Is.EqualTo("Reb"));
                Assert.That(loaded.Enclaves.IsSwitched("Gotha"), Is.True);
                Assert.That(loaded.Fame.Of(FameKind.Enclave, "Gotha"), Is.EqualTo(100));
            }
        }

        static SessionFixture ArdeaFixture() => Fixture()
            .Add(SimulationSchema.SectorFolder, new WorldSectorDef(StoryWorldDirector.ArdeaRebelSector, false))
            .Add(SimulationSchema.NpcFolder, Orc("Ardea_Orc", "Ardea"));

        [Test]
        public void ArdeaRebelSector_AfterTheLiberation_GivesArdeaFame100_AsG3ChangeEnclaveStatusRoutine()
        {
            using (var f = ArdeaFixture())
            {
                var s = f.NewGame();
                s.Enclaves.Set("Ardea", new EnclaveState(EnclaveStatus.Flee, true, true));
                Assert.That(s.Fame.Of(FameKind.Enclave, "Ardea"), Is.EqualTo(0));

                s.WorldState.Director.SetSectorEnabled(StoryWorldDirector.ArdeaRebelSector, true);

                Assert.That(s.Enclaves.Of("Ardea"), Is.EqualTo(new EnclaveState(EnclaveStatus.Routine, false, false)));
                Assert.That(s.Fame.Of(FameKind.Enclave, "Ardea"), Is.EqualTo(100), "G3 ChangeEnclaveStatus @ 100602d0: PlayerFame = 100");
                Assert.That(CharacterSheet.Of(s).Reputation.Select(r => r.Value), Has.Member("100"));
                Assert.That(f.Reload(s).Fame.Of(FameKind.Enclave, "Ardea"), Is.EqualTo(100));
            }
        }

        [Test]
        public void ArdeaRebelSector_WhenArdeaIsAlreadyRoutine_LeavesTheFame()
        {
            using (var f = ArdeaFixture())
            {
                var s = f.NewGame();
                s.Enclaves.Set("Ardea", new EnclaveState(EnclaveStatus.Routine, false, false));
                s.WorldState.Director.SetSectorEnabled(StoryWorldDirector.ArdeaRebelSector, true);
                Assert.That(s.Fame.Of(FameKind.Enclave, "Ardea"), Is.EqualTo(0), "G3: only a change of status acts");
            }
        }

        [Test]
        public void OldSave_OfALiberatedArdea_IsMigratedToFame100_OnlyOnce()
        {
            using (var f = ArdeaFixture())
            {
                // The state a save before fix-144 holds: Ardea liberated and Routine with the rebels, no Ardea fame.
                var s = f.NewGame();
                s.WorldState.Sectors.Set(StoryWorldDirector.ArdeaRebelSector, true);
                s.Enclaves.SetAlignment("Ardea", "Reb");
                s.Enclaves.Set("Ardea", new EnclaveState(EnclaveStatus.Routine, false, false));
                s.Fame.Add(FameKind.Enclave, "Faring", 30);
                var save = SessionFixture.Save(s);

                var current = f.Load(save);
                Assert.That(current.Fame.Of(FameKind.Enclave, "Ardea"), Is.EqualTo(0), "a fix-144 save is not migrated");

                var migrated = f.Load(WithSectionVersion(save, "rules.fame", 1));
                Assert.That(migrated.Fame.Of(FameKind.Enclave, "Ardea"), Is.EqualTo(100));
                Assert.That(migrated.Fame.Of(FameKind.Enclave, "Faring"), Is.EqualTo(30));
                Assert.That(migrated.Fame.Of(FameKind.Enclave, "Gotha"), Is.EqualTo(0), "Gotha's rebel sector is still off");
                Assert.That(f.Log.Messages, Has.Some.Contains("fix-144"));

                migrated.Fame.Add(FameKind.Enclave, "Ardea", -20); // a later quest failure
                Assert.That(f.Reload(migrated).Fame.Of(FameKind.Enclave, "Ardea"), Is.EqualTo(80), "the migrated save is v2 now");
            }
        }

        /// <summary>fix-261 [owner]: a save whose Mora Sul temple was freed by its zombies loses the crime against Ass, once.</summary>
        [Test]
        public void OldSave_OfAFreedMoraSulTemple_TakesBackTheCrimeAgainstAss_OnlyOnce()
        {
            using (var f = Fixture().Add(RulesSchema.EnclaveFolder, new EnclaveDef("MoraSulTemple", "Ass")))
            {
                var s = f.NewGame();
                s.Enclaves.Set("MoraSulTemple", new EnclaveState(EnclaveStatus.Flee, false, false));
                s.Crime.Ledger.AddPoliticalCrime("Ass");
                s.Crime.Ledger.AddPoliticalCrime("Orc");
                var save = SessionFixture.Save(s);

                Assert.That(f.Load(save).Crime.Ledger.PoliticalCrimesAgainst("Ass"), Is.EqualTo(1), "a fix-261 save is not migrated");

                var migrated = f.Load(WithSectionVersion(save, "rules.crime", 1));
                Assert.That(migrated.Crime.Ledger.PoliticalCrimesAgainst("Ass"), Is.EqualTo(0));
                Assert.That(migrated.Crime.Ledger.PoliticalCrimesAgainst("Orc"), Is.EqualTo(1), "other factions keep theirs");
                Assert.That(f.Log.Messages, Has.Some.Contains("fix-261"));
                migrated.Crime.Ledger.AddPoliticalCrime("Ass"); // a later liberation by the hero
                Assert.That(f.Reload(migrated).Crime.Ledger.PoliticalCrimesAgainst("Ass"), Is.EqualTo(1), "the migrated save is v2 now");
            }
        }

        /// <summary>The save with one section's version number replaced (an older save of the same layout).</summary>
        static byte[] WithSectionVersion(byte[] save, string section, int version)
        {
            var r = new System.IO.BinaryReader(new System.IO.MemoryStream(save));
            string Str() => r.ReadBoolean() ? r.ReadString() : null;
            r.ReadInt32(); r.ReadInt32(); r.ReadDouble(); Str(); r.ReadUInt64();
            int packs = r.ReadInt32();
            for (int i = 0; i < packs; i++) { Str(); Str(); }
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string name = Str();
                long at = r.BaseStream.Position;
                r.ReadInt32();
                int length = r.ReadInt32();
                if (name == section)
                {
                    var copy = (byte[])save.Clone();
                    System.BitConverter.GetBytes(version).CopyTo(copy, (int)at);
                    return copy;
                }
                r.BaseStream.Position += length;
            }
            throw new System.InvalidOperationException("no section " + section);
        }

        [Test]
        public void EnclaveFlags_ByEnclaveOrMember_AndStartRevolution()
        {
            using (var f = Fixture().Add(SimulationSchema.NpcFolder, Orc("Silden_Orc", "Silden")))
            {
                var s = f.NewGame();
                var world = s.WorldState.Director;
                world.SetEnclaveFlag("Silden", EnclaveFlag.Revolution, true);
                Assert.That(s.Enclaves.Of("Silden"), Is.EqualTo(new EnclaveState(EnclaveStatus.Routine, false, true)));
                world.SetEnclaveFlag("Silden_Orc", EnclaveFlag.Raid, true);
                Assert.That(s.Enclaves.Of("Silden").Raid, Is.True);
                world.StartRevolution(new ActorId("Silden_Orc"));
                Assert.That(s.Enclaves.Of("Silden"), Is.EqualTo(new EnclaveState(EnclaveStatus.Attack, true, true)));
                world.SetEnclaveFlag("Nowhere", EnclaveFlag.Raid, true);
                Assert.That(f.Log.Messages, Has.Some.Contains("neither an enclave nor a member"));
            }
        }

        [Test]
        public void Liberation_WhenFewerThanOneInFiveStand_FreesTheEnclave_AndCountsFreeEnclaveQuests()
        {
            var f = Fixture().Add(StorySchema.QuestFolder, Quest("Silden_Revolution", QuestObjectiveKind.FreeEnclave, null, "Silden"));
            for (int i = 0; i < 6; i++) f.Add(SimulationSchema.NpcFolder, Orc("Silden_Orc_" + i, "Silden", i));
            using (f)
            {
                var s = f.NewGame();
                s.Enclaves.Set("Silden", new EnclaveState(EnclaveStatus.Attack, true, true));
                for (int i = 0; i < 4; i++) s.Combat.Vitals.Kill(new ActorId("Silden_Orc_" + i), byPlayerParty: true);
                s.WorldState.CheckNow();
                Assert.That(s.Enclaves.Of("Silden").Status, Is.EqualTo(EnclaveStatus.Attack), "2 of 6 stand: 5·2 ≥ 6");

                s.Combat.Vitals.Kill(new ActorId("Silden_Orc_4"), byPlayerParty: true);
                s.WorldState.CheckNow();
                Assert.That(s.Enclaves.Of("Silden").Status, Is.EqualTo(EnclaveStatus.Flee), "1 of 6 stands: 5·1 < 6");
                Assert.That(s.Quests.StatusOf(new Id<QuestDef>("Silden_Revolution")), Is.EqualTo(QuestStatus.Success));
                Assert.That(s.Crime.Ledger.PoliticalCrimesAgainst("Orc"), Is.EqualTo(1), "G3: the faction remembers the liberation");
                Assert.That(s.Crime.Attitudes.Of(new ActorId("Silden_Orc_5"), ActorId.Player), Is.EqualTo(Attitude.Panic), "the last orc flees");
            }
        }

        /// <summary>
        /// fix-261 [owner]: the Mora Sul temple stayed in Attack after the hero's brawl with its orcs; the zombies the opened
        /// temple lets in killed the orcs and the once-a-second test freed the enclave without the hero: a political crime
        /// against Ass made every Hashishin hostile. G3 tests the give-up only inside an alarm (the hero's attack).
        /// </summary>
        [Test]
        public void Liberation_NeedsTheHerosStrike_MembersKilledByOthersDoNotFreeTheEnclave()
        {
            var f = Fixture();
            for (int i = 0; i < 6; i++) f.Add(SimulationSchema.NpcFolder, Orc("Temple_Orc_" + i, "MoraSulTemple", i));
            using (f)
            {
                var s = f.NewGame();
                s.Enclaves.Set("MoraSulTemple", new EnclaveState(EnclaveStatus.Attack, false, false));
                for (int i = 0; i < 5; i++) s.Combat.Vitals.Kill(new ActorId("Temple_Orc_" + i), byPlayerParty: false);
                s.WorldState.CheckNow();
                Assert.That(s.Enclaves.Of("MoraSulTemple").Status, Is.EqualTo(EnclaveStatus.Attack), "the zombies' kills raise no alarm");
                Assert.That(s.Crime.Ledger.PoliticalCrimesAgainst("Orc"), Is.EqualTo(0));

                s.Combat.Vitals.KnockOut(new ActorId("Temple_Orc_5"), byPlayerParty: true);
                s.WorldState.CheckNow();
                Assert.That(s.Enclaves.Of("MoraSulTemple").Status, Is.EqualTo(EnclaveStatus.Flee), "the hero's strike: 0 of 6 stand");
            }
        }

        /// <summary>fix-261 [owner]: Ramirez (G3 Rtn_Dead) lies dead in his pit from the start, as Ningal's quest expects.</summary>
        [Test]
        public void NpcPlacedAsCorpse_IsDeadFromTheStart_WithoutAKill()
        {
            var f = Fixture().Add(SimulationSchema.NpcFolder, Orc("Ramirez", null).AsDeadAtStart(), Orc("Mezir", null, 3));
            using (f)
            {
                int defeats = 0;
                var s = f.NewGame();
                using (s.Events.Subscribe<ActorDefeated>(_ => defeats++))
                {
                    Assert.That(s.Combat.Vitals.IsDead(new ActorId("Ramirez")), Is.True);
                    Assert.That(s.Combat.Vitals.IsDead(new ActorId("Mezir")), Is.False);
                    var loaded = f.Reload(s);
                    Assert.That(loaded.Combat.Vitals.IsDead(new ActorId("Ramirez")), Is.True, "kept by the save");
                }
                Assert.That(defeats, Is.Zero, "nobody killed him: no XP, no crime");
            }
        }

        static NpcDef Raider(string name, string enclave, float x = 0, bool boss = false) =>
            new NpcDef(new ActorId(name), new TextId("FO_" + name), "Orc_Warrior_Hard", "Orc", "visual", new WorldPose(x, 0, 0, 0),
                       combat: new NpcCombatDef(1, 1, "Orc", true, boss: boss, role: boss ? NpcRole.Political : NpcRole.Personal,
                                                faction: "Out", enclave: enclave));

        /// <summary>
        /// fix-198 [owner, build 23c]: a Nordmar orc raider camp (an outlaw enclave) the hero routs. CP NotifyEnclave turns the
        /// standing members and then the camp itself Orc; only the members turned, so the survivors (the leader
        /// "Предводитель", a shaman) were hostile to their own camp and the revolution table's Flee row made them Friendly
        /// to the hero: a green focus name. Now they panic and flee as in G3, also after a reload; the big mine stays Out (its loyal members panic all the same).
        /// </summary>
        [TestCase("Nrd_OrcCamp_5", "Orc", Attitude.Panic)]
        [TestCase(EnclaveSectorSwitches.BigMine, "Out", Attitude.Panic)]
        public void RoutedRaiderCamp_TurnsOrc_AndItsSurvivorsPanicInsteadOfTurningFriendly(string camp, string after, Attitude survivor)
        {
            var f = Fixture().Add(RulesSchema.EnclaveFolder, new EnclaveDef(camp, "Out"));
            for (int i = 0; i < 6; i++) f.Add(SimulationSchema.NpcFolder, Raider("Raider_" + i, camp, i, boss: i == 5));
            using (f)
            {
                var s = f.NewGame();
                var leader = new ActorId("Raider_5");
                s.Npcs.SetDetail(leader, DetailLevel.Near);
                s.Fame.Add(FameKind.Political, "Orc", 100); // an orc mercenary: the boss rule alone would say Friendly
                s.Enclaves.Set(camp, new EnclaveState(EnclaveStatus.Attack, false, false));
                for (int i = 0; i < 5; i++) s.Combat.Vitals.Kill(new ActorId("Raider_" + i), byPlayerParty: true);
                s.WorldState.CheckNow();
                Assert.That(s.Enclaves.Of(camp).Status, Is.EqualTo(EnclaveStatus.Flee));
                Assert.That(s.Combat.Vitals.IsDead(leader), Is.False, "the leader in range survives the liberation");
                s.Crime.Attitudes.Facts.TryGetProfile(leader, out var p);
                Assert.That((p.Alignment, p.EnclaveAlignment), Is.EqualTo((after, after)), "CP NotifyEnclave: members, then the camp");
                Assert.That(s.Crime.Attitudes.Of(leader, ActorId.Player), Is.EqualTo(survivor));
                Assert.That(HudFocus.Npc(s, leader).Relation, Is.Not.EqualTo(FocusRelation.Friendly), "no green focus name");

                var loaded = f.Reload(s);
                Assert.That(loaded.Crime.Attitudes.Of(leader, ActorId.Player), Is.EqualTo(survivor), "kept by the save");
            }
        }

        /// <summary>fix-198: a save of a raider camp liberated before the fix (members Orc, camp still Out) is migrated on load.</summary>
        [Test]
        public void OldSave_OfALiberatedRaiderCamp_TurnsTheCampOrcOnLoad()
        {
            const string camp = "Nrd_OrcCamp_5";
            var f = Fixture().Add(RulesSchema.EnclaveFolder, new EnclaveDef(camp, "Out"), new EnclaveDef("Nrd_OrcCamp_1", "Out"))
                             .Add(SimulationSchema.NpcFolder, Raider("Shaman", camp));
            using (f)
            {
                var s = f.NewGame();
                var shaman = new ActorId("Shaman");
                s.Enclaves.Set(camp, new EnclaveState(EnclaveStatus.Flee, false, false));
                s.Actors.TryGet(shaman, out var state);
                state.ChangeAlignment("Orc"); // the state build 23c left
                Assert.That(s.Crime.Attitudes.Of(shaman, ActorId.Player), Is.EqualTo(Attitude.Friendly), "the bug");

                var loaded = f.Reload(s);
                Assert.That(loaded.Enclaves.AlignmentOverride(camp), Is.EqualTo("Orc"));
                Assert.That(loaded.Enclaves.AlignmentOverride("Nrd_OrcCamp_1"), Is.Null, "a camp not liberated keeps Out");
                Assert.That(loaded.Crime.Attitudes.Of(shaman, ActorId.Player), Is.EqualTo(Attitude.Panic));
                Assert.That(f.Log.Messages, Has.Some.Contains("save migration (fix-198)"));
            }
        }

        [Test]
        public void Ardea_AfterTheOpeningFight_GivesUp_AndIsStagedOnceTheHeroIsFarAway()
        {
            var start = new GameStartDef(GameStartDef.DefinitionId, new WorldPose(879.8f, 51.5f, -102f, 119f), 0, 12f, 1,
                                         new string[0], new RoutineChange[0], new EnclaveStart[0], new string[0]);
            var f = Fixture().Add(SimulationSchema.GameStartFolder, start)
                             .Add(StorySchema.QuestFolder, Quest("Ardea_Revolution", QuestObjectiveKind.FreeEnclave, null, "Ardea"),
                                  new QuestDef(new Id<QuestDef>("Gorn_ShowReddock"), new TextId("QUEST_Gorn_ShowReddock"), null))
                             .Add(SimulationSchema.NpcFolder, new NpcDef(new ActorId("Gorn"), new TextId("FO_Gorn"), "Gorn", "Human", "visual",
                                                                         new WorldPose(880, 51, -100, 0)));
            for (int i = 0; i < 6; i++) f.Add(SimulationSchema.NpcFolder, Orc("Ardea_Orc_" + i, "Ardea", 880 + i));
            using (f)
            {
                var s = f.NewGame();
                s.Enclaves.Set("Ardea", new EnclaveState(EnclaveStatus.Attack, true, true));
                s.Quests.TrySetStatus(new Id<QuestDef>("Gorn_ShowReddock"), QuestStatus.Running);
                s.Party.JoinCompanion(new ActorId("Gorn"));
                for (int i = 0; i < 6; i++) s.Combat.Vitals.Kill(new ActorId("Ardea_Orc_" + i), byPlayerParty: true);
                s.Crime.ReportPlayer(new WorldPose(880, 51, -100, 0));
                s.WorldState.CheckNow();
                Assert.That(s.Enclaves.Of("Ardea").Status, Is.EqualTo(EnclaveStatus.Flee));
                Assert.That(s.Quests.StatusOf(new Id<QuestDef>("Ardea_Revolution")), Is.EqualTo(QuestStatus.Success));
                Assert.That(s.Crime.Ledger.PoliticalCrimesAgainst("Orc"), Is.EqualTo(0), "not for Ardea");
                Assert.That(s.Enclaves.IsSwitched("Ardea"), Is.False, "the hero is still there");

                s.Crime.ReportPlayer(new WorldPose(1400, 51, -100, 0));
                s.WorldState.CheckNow();
                Assert.That(s.Enclaves.IsSwitched("Ardea"), Is.True, "more than 420 m away");
                Assert.That(s.Quests.StatusOf(new Id<QuestDef>("Gorn_ShowReddock")), Is.EqualTo(QuestStatus.Cancelled));
                Assert.That(s.Party.Members.IsLedByPlayer(new ActorId("Gorn")), Is.False, "Gorn goes to Gotha's prison");
                Assert.That(f.Log.Messages, Has.Some.Contains("Ardea staged"));
            }
        }

        [Test]
        public void EnterArea_CountsWhenTheHeroStandsInTheNamedZone()
        {
            var tower = new AiZoneDef("z1", "Xardas_Tower", null, ZoneSecurity.None, false, new[] { 0f, 0f, 10f, 0f, 10f, 10f, 0f, 10f }, 100f, 2f, 1f);
            using (var f = Fixture().Add(RulesSchema.AiZoneFolder, tower)
                                    .Add(StorySchema.QuestFolder, Quest("Xardas_FindXardas", QuestObjectiveKind.EnterArea, "Xardas_Tower", "player")))
            {
                var s = f.NewGame();
                var quest = new Id<QuestDef>("Xardas_FindXardas");
                s.Quests.TrySetStatus(quest, QuestStatus.Running);
                s.Crime.ReportPlayer(new WorldPose(5, 90, 5, 0));
                s.Tick(1.1f);
                Assert.That(s.Quests.StatusOf(quest), Is.EqualTo(QuestStatus.Running), "below the zone");
                s.Crime.ReportPlayer(new WorldPose(5, 100, 5, 0));
                s.Tick(1.1f);
                Assert.That(s.Quests.StatusOf(quest), Is.EqualTo(QuestStatus.Success));
            }
        }

        [Test]
        public void OpenDoor_OpensTheNamedDoor()
        {
            var door = new InteractiveObjectDef("d1", "MoraSul_TempleDoor_Tester_01", InteractiveKind.Door, "None", "G3_TempleDoor_01.FXA",
                                                new[] { 0f, 0f, 0f }, new[] { 0f, 0f, 1f }, new[] { 0f, 1f, 0f }, 1f, default, null, null,
                                                new DoorDef(true, "G3_TempleDoor_01_Open", null), null);
            using (var f = Fixture().Add(RulesSchema.InteractiveObjectFolder, door))
            {
                var s = f.NewGame();
                Assert.That(s.Interaction.Objects.IsOpen("d1"), Is.False);
                Assert.That(s.WorldState.Director.OpenDoor("MoraSul_TempleDoor_Tester_01"), Is.True);
                Assert.That(s.Interaction.Objects.IsOpen("d1"), Is.True);
                Assert.That(s.WorldState.Director.OpenDoor("MoraSul_TempleDoor_Tester_01"), Is.True, "already open stays open");
                Assert.That(s.Interaction.Objects.IsOpen("d1"), Is.True);
                Assert.That(s.WorldState.Director.OpenDoor("No_Door"), Is.False);
            }
        }

        /// <summary>fix-261 [owner]: the temple door's talking object leaves the focus (and talk) once its door is open.</summary>
        [Test]
        public void DoorTester_IsDone_OnceItsDoorIsOpen()
        {
            var door = new InteractiveObjectDef("d1", "MoraSul_TempleDoor_Tester_01", InteractiveKind.Door, "None", "G3_TempleDoor_01.FXA",
                                                new[] { 0f, 0f, 0f }, new[] { 0f, 0f, 1f }, new[] { 0f, 1f, 0f }, 1f, default, null, null,
                                                new DoorDef(true, "G3_TempleDoor_01_Open", null), null);
            using (var f = Fixture().Add(RulesSchema.InteractiveObjectFolder, door))
            {
                var s = f.NewGame();
                var tester = new ActorId("MoraSul_TempleDoor_Tester");
                Assert.That(s.WorldState.Director.IsDoneDoorTester(tester.Name), Is.False, "closed: the tester takes the keys");
                s.WorldState.Director.OpenDoor("MoraSul_TempleDoor_Tester_01");
                Assert.That(s.WorldState.Director.IsDoneDoorTester(tester.Name), Is.True);
                Assert.That(s.Dialog.CanTalkTo(tester), Is.False, "no focus, no 'nothing to say' in the open doorway");
                Assert.That(s.WorldState.Director.IsDoneDoorTester("Gnar"), Is.False);
            }
        }

        [Test]
        public void SetClassAndGuardStatus_AreRemembered_AcrossSaveLoad()
        {
            using (var f = Fixture())
            {
                var s = f.NewGame();
                s.WorldState.Director.SetClass(SessionFixture.Npc, "Paladin");
                s.WorldState.Director.SetGuardActive(SessionFixture.Npc, false);
                var loaded = f.Reload(s);
                Assert.That(loaded.WorldState.Director.TryGetClass(SessionFixture.Npc, out var c) ? c : NpcClass.None, Is.EqualTo(NpcClass.Paladin));
                Assert.That(loaded.WorldState.Director.IsGuardInactive(SessionFixture.Npc), Is.True);
            }
        }

        [Test]
        public void ActorFacts_TalkedAlignmentClassImmortalAndWorn_SurviveSaveLoad_OnlyForTheActorsTheyWereSetFor()
        {
            // fix-157: the sections walk only the actors with a fact set, not every actor of the content
            var diego = new ActorId("Diego");
            var milten = new ActorId("Milten");
            using (var f = Fixture())
            {
                f.Add(SimulationSchema.NpcFolder, new NpcDef(diego, new TextId("FO_Diego"), "Human"), new NpcDef(milten, new TextId("FO_Milten"), "Human"));
                var s = f.NewGame();
                s.Actors.MarkTalkedToPlayer(diego);
                s.Actors.MarkTalkedToPlayer(SessionFixture.Npc);
                Assert.That(s.Actors.TryGet(milten, out var m), Is.True);
                m.ChangeAlignment("Reb");
                m.ImmortalOverride = true;
                m.Wear("ItAr_Fire");
                s.WorldState.Director.SetClass(diego, "Paladin");

                var loaded = f.Reload(s);
                Assert.That(loaded.Actors.HasTalkedToPlayer(diego) && loaded.Actors.HasTalkedToPlayer(SessionFixture.Npc), Is.True);
                Assert.That(loaded.Actors.HasTalkedToPlayer(milten), Is.False);
                Assert.That(loaded.Actors.TryGet(milten, out var lm) && lm.AlignmentChanged && lm.Alignment == "Reb", Is.True);
                Assert.That(lm.ImmortalOverride, Is.True);
                Assert.That(lm.Worn, Is.EqualTo(new[] { "ItAr_Fire" }));
                Assert.That(loaded.Actors.TryGet(diego, out var ld) && !ld.AlignmentChanged && ld.ImmortalOverride == null && ld.Worn.Count == 0, Is.True);
                Assert.That(loaded.WorldState.Director.TryGetClass(diego, out var c) ? c : NpcClass.None, Is.EqualTo(NpcClass.Paladin));
                Assert.That(loaded.WorldState.Director.TryGetClass(milten, out _), Is.False);
                Assert.That(SessionFixture.Save(f.Reload(loaded)), Is.EqualTo(SessionFixture.Save(loaded)), "equal states, equal saves");
            }
        }

        [Test]
        public void EndGame_PlaysTheCredits_ThenQuits()
        {
            var flow = new GameFlow(null, quickStart: true);
            flow.WorldReady(false);
            Assert.That(flow.Phase, Is.EqualTo(GamePhase.Playing));
            flow.EndGame();
            Assert.That(flow.Phase, Is.EqualTo(GamePhase.Credits));
            Assert.That(flow.CurrentVideo, Is.EqualTo("G3_Credits2"));
            flow.VideoFinished();
            Assert.That(flow.CurrentVideo, Is.EqualTo("G3_Credits"));
            flow.VideoFinished();
            Assert.That(flow.Phase, Is.EqualTo(GamePhase.Quit));
        }
    }
}
