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
    /// <summary>
    /// fix-218 [owner]: Tjalf gave the hero the key to his chest, yet the Hammerclan attacked when he looted it. G3
    /// IsBotheredByPlayerTheftOf: a chest whose key the player carries is free, and Tjalf_Room is free after the game event
    /// Tjalf_Room_Plunder (the flag his key info sets). Both the loot page (InteractiveObjects) and quick loot.
    /// </summary>
    public sealed class ChestKeyTheftTests
    {
        const string Key = "It_Key_Tjalf";
        const string PlunderFlag = "TJALF_ROOM_PLUNDER";
        static readonly float[] HallPos = { 0, 0, 0 };
        static readonly float[] RoomPos = { 20, 0, 0 };

        static SessionFixture Fixture() => new SessionFixture()
            .Add(SimulationSchema.PerceptionRulesFolder, G3CrimeImporter.Perception())
            .Add(RulesSchema.AttitudeRulesFolder, G3CrimeImporter.Attitudes())
            .Add(RulesSchema.CrimeRulesFolder, G3CrimeImporter.Crime())
            .Add(RulesSchema.EnclaveFolder, new EnclaveDef("Hammerclan", "Nrd"))
            .Add(RulesSchema.AiZoneFolder,
                 new AiZoneDef("hall", "Hammerclan_Hall", "Hammerclan", ZoneSecurity.Enclave, true, new float[] { -5, -5, 5, -5, 5, 5, -5, 5 }, 0, 3, 1),
                 new AiZoneDef("room", "Tjalf_Room", "Hammerclan", ZoneSecurity.Enclave, true, new float[] { 15, -5, 25, -5, 25, 5, 15, 5 }, 0, 3, 1))
            .Add(RulesSchema.ItemFolder, ItemTests.Item("It_Gold", 1), ItemTests.Item("It_Ring", 50), ItemTests.Item(Key, 10))
            .Add(RulesSchema.InteractiveObjectFolder,
                 Chest("keyed", HallPos, new LockDef(true, 2, Key, 1)),
                 Chest("keyedOpen", HallPos, new LockDef(false, 2, Key, 1)),
                 Chest("tjalf", RoomPos, null));

        static InteractiveObjectDef Chest(string id, float[] pos, LockDef chestLock) =>
            new InteractiveObjectDef(id, "G3_Object_Interact_Animated_Chest_Tjalf", InteractiveKind.Chest, "Chest", "chest.FXA", pos,
                                     null, null, 1, default, chestLock,
                                     new ContainerDef(new string[0], new[] { new TreasureStack("It_Gold", 100, 0), new TreasureStack("It_Ring", 1, 0) }),
                                     null, null);

        static List<BroadcastKind> Crimes(GameSession s)
        {
            var list = new List<BroadcastKind>();
            s.Events.Subscribe<PerceptionBroadcast>(b =>
            {
                if (b.Source.IsPlayer && (b.Kind == BroadcastKind.TakeItem || b.Kind == BroadcastKind.UseInteractObject)) list.Add(b.Kind);
            });
            return list;
        }

        [Test]
        public void KeyedChest_WithoutTheKey_OpeningAndEachStackAreCrimes()
        {
            using var f = Fixture();
            var s = f.NewGame();
            var crimes = Crimes(s);
            var objects = s.Interaction.Objects;
            Assert.That(objects.OpenContainer("keyedOpen"), Is.True);
            Assert.That(objects.TakeAll("keyedOpen"), Is.EqualTo(2));
            Assert.That(crimes, Is.EqualTo(new[] { BroadcastKind.UseInteractObject, BroadcastKind.TakeItem, BroadcastKind.TakeItem }));
            Assert.That(s.Crime.Ledger.Enclave("Hammerclan").CountOf(CrimeType.Theft), Is.EqualTo(2));
        }

        [Test]
        public void KeyedChest_WithTheKey_LootPage_NoCrime()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Items.Inventories.Give(ActorId.Player, Key, 1);
            var crimes = Crimes(s);
            var objects = s.Interaction.Objects;
            Assert.That(objects.Unlock("keyed").Opened, Is.True, "the key opens the lock");
            Assert.That(objects.OpenContainer("keyed"), Is.True);
            Assert.That(objects.Take("keyed", "It_Gold", 100), Is.EqualTo(100));
            Assert.That(objects.TakeAll("keyed"), Is.EqualTo(1));
            Assert.That(crimes, Is.Empty, "G3: a chest whose key the player carries is free");
            Assert.That(s.Crime.Ledger.Enclave("Hammerclan").IsClean, Is.True);
        }

        [Test]
        public void KeyedChest_WithTheKey_QuickLoot_NoTheft()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Items.Inventories.Give(ActorId.Player, Key, 1);
            var crimes = Crimes(s);
            var r = new QuickLoot(s).Chest("keyedOpen");
            Assert.That(r.Outcome, Is.EqualTo(QuickLootOutcome.Taken));
            Assert.That(r.Gold, Is.EqualTo(100));
            Assert.That(r.Theft, Is.False);
            Assert.That(r.Thefts, Is.Zero);
            Assert.That(crimes, Is.Empty);
            Assert.That(s.Crime.Ledger.Enclave("Hammerclan").IsClean, Is.True);
        }

        [Test]
        public void TjalfRoom_IsFreeOnlyAfterThePlunderFlag()
        {
            using var f = Fixture();
            var s = f.NewGame();
            var crimes = Crimes(s);
            var objects = s.Interaction.Objects;
            objects.OpenContainer("tjalf");
            objects.Take("tjalf", "It_Ring", 1);
            Assert.That(crimes.Count, Is.EqualTo(2), "before Tjalf's info his room is owned");

            crimes.Clear();
            s.Flags.Set(PlunderFlag);
            objects.OpenContainer("tjalf");
            var r = new QuickLoot(s).Chest("tjalf");
            Assert.That(r.Outcome, Is.EqualTo(QuickLootOutcome.Taken));
            Assert.That(r.Thefts, Is.Zero);
            Assert.That(crimes, Is.Empty, "G3: Tjalf_Room after Tjalf_Room_Plunder");
            Assert.That(s.Crime.Ledger.Enclave("Hammerclan").CountOf(CrimeType.Theft), Is.EqualTo(1), "only the take before the flag");
        }

        [Test]
        public void Rules_FreeTjalfRoomByTheFlagTheDialogSets()
        {
            var theft = G3CrimeImporter.Crime().Theft;
            Assert.That(theft.IsFreedZone("Tjalf_Room", flag => flag == PlunderFlag), Is.True);
            Assert.That(theft.IsFreedZone("Tjalf_Room", _ => false), Is.False);
            Assert.That(theft.IsFreedZone("Other_Room", _ => true), Is.False);
            Assert.That(CrimeCatalogue.ChestKeyCarried(new LockDef(true, 2, Key, 1), k => k == Key), Is.True);
            Assert.That(CrimeCatalogue.ChestKeyCarried(new LockDef(true, 2, null, 1), _ => true), Is.False, "no key item: no free pass");
            Assert.That(CrimeCatalogue.ChestKeyCarried(null, _ => true), Is.False);
        }
    }
}
