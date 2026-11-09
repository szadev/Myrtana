using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public sealed class InteractiveObjectTests
    {
        const string Lockpick = "It_Key_Lockpick", Key = "It_Key_Ali";
        static readonly LockRules Rules = LockRules.Default;

        static LockVerdict Check(LockDef def, int keys = 0, int picks = 0, params string[] perks) =>
            LockCheck.Check(Rules, def, def?.Locked ?? false,
                            new LockPicker(item => item == Key ? keys : item == Lockpick ? picks : 0, perks.Contains));

        [Test]
        public void LockCheck_FollowsCanOpen()
        {
            Assert.That(Check(null), Is.EqualTo(LockVerdict.Open));
            Assert.That(Check(new LockDef(false, 2, null, 1)), Is.EqualTo(LockVerdict.Open));
            Assert.That(Check(new LockDef(true, 1, Key, 1), keys: 1), Is.EqualTo(LockVerdict.Key));
            Assert.That(Check(new LockDef(true, -1, Key, 1)), Is.EqualTo(LockVerdict.NeedKey));
            Assert.That(Check(new LockDef(true, 5, Key, 1), 0, 3, "Perk_LockPick_3"), Is.EqualTo(LockVerdict.NeedKey));
            Assert.That(Check(new LockDef(true, 4, null, 1), 0, 3, "Perk_LockPick_3"), Is.EqualTo(LockVerdict.Impossible));
            Assert.That(Check(new LockDef(true, 1, null, 1), picks: 1), Is.EqualTo(LockVerdict.MissingSkill));
            Assert.That(Check(new LockDef(true, 2, null, 1), 0, 1, "Perk_LockPick_1"), Is.EqualTo(LockVerdict.TooHard));
            Assert.That(Check(new LockDef(true, 3, null, 1), 0, 1, "Perk_LockPick_2"), Is.EqualTo(LockVerdict.Impossible));
            Assert.That(Check(new LockDef(true, 1, null, 1), 0, 0, "Perk_LockPick_1"), Is.EqualTo(LockVerdict.NeedLockpick));
            Assert.That(Check(new LockDef(true, 1, Key, 1), 0, 0, "Perk_LockPick_1"), Is.EqualTo(LockVerdict.NeedKeyOrLockpick));
            Assert.That(Check(new LockDef(true, 2, null, 1), 0, 1, "Perk_LockPick_2"), Is.EqualTo(LockVerdict.Pick));
            Assert.That(Check(new LockDef(true, 1, Key, 2), 1, 1, "Perk_LockPick_1"), Is.EqualTo(LockVerdict.Pick), "CP KeyAmount");
        }

        [Test]
        public void BreakChance_IsCp175()
        {
            // G3 _AI_Unlock: GetRandomNumber(100) > 70 (rand() % 100) breaks the pick: 29 of the 100 rolls
            Assert.That(LockCheck.BreakChance(Rules, 1, p => p == "Perk_LockPick_1"), Is.EqualTo(29));
            Assert.That(LockCheck.BreakChance(Rules, 1, p => true), Is.EqualTo(0));
            Assert.That(LockCheck.BreakChance(Rules, 2, p => p != "Perk_LockPick_3"), Is.EqualTo(29));
            Assert.That(LockCheck.BreakChance(Rules, 2, p => true), Is.EqualTo(0));
        }

        sealed class Rolls : IRandom
        {
            readonly Queue<int> _values;
            public Rolls(params int[] values) => _values = new Queue<int>(values);
            public int Calls { get; private set; }
            public int Next(int maxExclusive) { Calls++; return _values.Dequeue(); }
            public float NextFloat() => 0;
            public RandomState Save() => default;
            public void Load(RandomState state) { }
        }

        [Test]
        public void Fix117_RollPick_IsG3sRollAbove70()
        {
            bool OnlyPerk1(string p) => p == "Perk_LockPick_1";
            Assert.That(LockCheck.RollPick(Rules, 1, OnlyPerk1, new Rolls(0), out int roll, out int chance) && roll == 0 && chance == 29, Is.True);
            Assert.That(LockCheck.RollPick(Rules, 1, OnlyPerk1, new Rolls(70), out _, out _), Is.True, "70 is not > 70");
            Assert.That(LockCheck.RollPick(Rules, 1, OnlyPerk1, new Rolls(71), out _, out _), Is.False, "71 > 70 breaks");
            Assert.That(LockCheck.RollPick(Rules, 1, OnlyPerk1, new Rolls(99), out _, out _), Is.False);
            var none = new Rolls();
            Assert.That(LockCheck.RollPick(Rules, 1, p => true, none, out roll, out chance) && roll == -1 && chance == 0 && none.Calls == 0, Is.True,
                        "with Perk_LockPick_2 a level-1 pick never fails and rolls nothing");
            Assert.That(LockCheck.RollPick(Rules, 3, p => true, none, out _, out _) && none.Calls == 0, Is.True, "the top level never fails");
            int breaks = 0;
            for (int r = 0; r < 100; r++) if (!LockCheck.RollPick(Rules, 1, OnlyPerk1, new Rolls(r), out _, out _)) breaks++;
            Assert.That(breaks, Is.EqualTo(29));
        }

        [Test]
        public void Fix117_EveryAttemptRollsAnew_TheLockKeepsNoState()
        {
            // a break leaves the lock as it was (no sequence, no progress, no penalty): the next attempt is a fresh roll
            using var f = Fixture();
            var s = f.NewGame();
            s.Items.Inventories.Give(ActorId.Player, Lockpick, 5);
            s.Progression.LearnSkill("Perk_LockPick_1");
            var rolls = new Rolls(99, 80, 71, 70);
            var objects = new InteractiveObjects(f.Content.Database, Rules, s.Items.Inventories, s.Hero.Knows, new Rolls(), s.Events, null, rolls);
            for (int i = 0; i < 3; i++)
            {
                var r = objects.Unlock("chest");
                Assert.That(r.Broke && r.LockpickUsed && objects.IsLocked("chest") && r.BreakChance == 29, Is.True, $"attempt {i + 1}");
            }
            var open = objects.Unlock("chest");
            Assert.That(open.Opened && open.Roll == 70 && !objects.IsLocked("chest"), Is.True);
            Assert.That(s.Items.Inventories.Of(ActorId.Player).Count(Lockpick), Is.EqualTo(1), "one lockpick per attempt, success too");
            Assert.That(rolls.Calls, Is.EqualTo(4), "picks roll the pick random (the session's, fix-119)");
        }

        [Test]
        public void Fix121_ResetLock_ShutsAPickedChestAgain_PickedReportsEveryAttempt()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Items.Inventories.Give(ActorId.Player, Lockpick, 5);
            s.Progression.LearnSkill("Perk_LockPick_1");
            var objects = new InteractiveObjects(f.Content.Database, Rules, s.Items.Inventories, s.Hero.Knows, new Rolls(), s.Events, null,
                                                 new Rolls(10, 80, 20));
            var seen = new List<(string id, UnlockResult r)>();
            objects.Picked += (id, r) => seen.Add((id, r));
            Assert.That(objects.Unlock("chest").Opened && !objects.IsLocked("chest"), Is.True);
            objects.ResetLock("chest");
            Assert.That(objects.IsLocked("chest"), Is.True, "back to its start state");
            Assert.That(objects.Unlock("chest").Broke, Is.True);
            objects.ResetLock("chest");
            Assert.That(objects.IsLocked("chest"), Is.True, "a shut lock stays shut");
            Assert.That(objects.Unlock("chest").Opened, Is.True);
            Assert.That(seen.Count == 3 && seen.All(x => x.id == "chest"), Is.True);
            Assert.That(seen[0].r.Roll == 10 && seen[1].r.Broke && seen[1].r.Roll == 80 && seen[2].r.Opened, Is.True);
            Assert.That(s.Items.Inventories.Of(ActorId.Player).Count(Lockpick), Is.EqualTo(2));
        }

        [Test]
        public void Fix117_AReloadedGame_DoesNotReplayTheSavedPickRoll()
        {
            // owner: the pick broke 4 times in a row: the session random is saved, so every quickload in front of the chest
            // replayed the same roll. G3 rolls with rand(), which no save holds. fix-119: the game's sessions are not pinned.
            using var f = Fixture().Seed(null);
            var s = f.NewGame();
            s.Items.Inventories.Give(ActorId.Player, Lockpick, 40);
            s.Progression.LearnSkill("Perk_LockPick_1");
            var save = SessionFixture.Save(s);
            var runs = new HashSet<string>();
            for (int load = 0; load < 6; load++)
            {
                var g = f.Load(save);
                var outcome = new System.Text.StringBuilder();
                for (int i = 0; i < 12; i++)
                {
                    var r = g.Interaction.Objects.Unlock("chest");
                    outcome.Append(r.Roll).Append(',');
                    if (r.Opened) break;
                }
                runs.Add(outcome.ToString());
            }
            Assert.That(runs.Count, Is.GreaterThan(1), "the same save must not give the same pick rolls every time");
            Assert.That(LockCheck.BreakChance(Rules, 3, p => true), Is.EqualTo(0));
        }

        static readonly float[] ChestPos = { 1, 2, 3 };

        static SessionFixture Fixture() => new SessionFixture()
            .Add(RulesSchema.TreasureFolder, new TreasureSetDef("TS_Test", TreasureUse.Plunder, 1, 1,
                                                                new[] { new TreasureStack("It_Bread", 1, 0) }))
            .Add(RulesSchema.InteractiveObjectFolder,
                 new InteractiveObjectDef("chest", "G3_Object_Interact_Animated_Chest_02", InteractiveKind.Chest, "Chest", "chest.FXA", ChestPos,
                                          null, null, 1, new ActorId("Ali"), new LockDef(true, 1, null, 1),
                                          new ContainerDef(new[] { "TS_Test" }, new[] { new TreasureStack("It_Gold", 20, 0) }), null, null),
                 new InteractiveObjectDef("door", "Door", InteractiveKind.Door, "Door", "door.FXA", null, null, null, 1, default,
                                          new LockDef(true, -1, Key, 1), null, new DoorDef(true, "open", "close"), null),
                 new InteractiveObjectDef("lever", "Lever", InteractiveKind.Switch, "None", null, null, null, null, 1, default, null, null,
                                          null, new[] { "gate" }),
                 new InteractiveObjectDef("gate", "Gate", InteractiveKind.Door, "Door", null, null, null, null, 1, default, null, null,
                                          new DoorDef(true, null, null), null));

        [Test]
        public void PickingAChest_UsesALockpick_ThenLootPublishesTheft()
        {
            using var f = Fixture();
            var s = f.NewGame();
            var objects = s.Interaction.Objects;
            var taken = new List<ContainerItemTaken>();
            s.Events.Subscribe<ContainerItemTaken>(taken.Add);
            objects.Zones = new FixedZone("Faring_House");

            Assert.That(objects.OpenContainer("chest"), Is.False, "locked");
            s.Items.Inventories.Give(ActorId.Player, Lockpick, 20);
            Assert.That(objects.Unlock("chest").Verdict, Is.EqualTo(LockVerdict.MissingSkill));
            s.Progression.LearnSkill("Perk_LockPick_1");

            UnlockResult result;
            int attempts = 0;
            do { result = objects.Unlock("chest"); attempts++; } while (!result.Opened && attempts < 20);
            Assert.That(result.Opened && result.LockpickUsed, Is.True);
            Assert.That(s.Items.Inventories.Of(ActorId.Player).Count(Lockpick), Is.EqualTo(20 - attempts));
            Assert.That(objects.IsLocked("chest"), Is.False);

            Assert.That(objects.OpenContainer("chest"), Is.True);
            var chest = s.Items.Inventories.Of(InteractiveObjects.ContainerOf("chest"));
            Assert.That(chest.Count("It_Bread"), Is.EqualTo(1));
            Assert.That(chest.Count("It_Gold"), Is.EqualTo(20));
            Assert.That(objects.TakeAll("chest"), Is.EqualTo(2));
            Assert.That(chest.Stacks, Is.Empty);
            Assert.That(taken.Select(t => (t.ObjectId, t.Owner.Name, t.Zone)).Distinct().Single(), Is.EqualTo(("chest", "Ali", "Faring_House")));
            Assert.That(objects.OpenContainer("chest"), Is.True);
            Assert.That(chest.Stacks, Is.Empty, "a chest is filled only once");
        }

        [Test]
        public void PickWithoutTheNextPerk_SometimesBreaks()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Items.Inventories.Give(ActorId.Player, Lockpick, 100);
            s.Progression.LearnSkill("Perk_LockPick_1");
            int broke = 0;
            for (int i = 0; i < 60; i++)
            {
                var r = s.Interaction.Objects.Unlock("chest");
                if (r.Opened) break;
                Assert.That(r.Broke, Is.True);
                broke++;
            }
            s.Progression.LearnSkill("Perk_LockPick_2");
            Assert.That(broke, Is.LessThan(60));
        }

        [Test]
        public void Door_NeedsItsKey_ThenToggles()
        {
            using var f = Fixture();
            var s = f.NewGame();
            var objects = s.Interaction.Objects;
            Assert.That(objects.Unlock("door").Verdict, Is.EqualTo(LockVerdict.NeedKey));
            Assert.That(objects.ToggleDoor("door"), Is.False);
            s.Items.Inventories.Give(ActorId.Player, Key, 1);
            Assert.That(objects.Unlock("door").Verdict, Is.EqualTo(LockVerdict.Key));
            Assert.That(objects.ToggleDoor("door") && objects.IsOpen("door"), Is.True);
            Assert.That(objects.ToggleDoor("door") && !objects.IsOpen("door"), Is.True);
        }

        [Test]
        public void Switch_TogglesItsTargets()
        {
            using var f = Fixture();
            var s = f.NewGame();
            Assert.That(s.Interaction.Objects.UseSwitch("lever"), Is.True);
            Assert.That(s.Interaction.Objects.IsOpen("gate"), Is.True);
        }

        [Test]
        public void SaveLoad_KeepsLocksDoorsAndLootedChests()
        {
            using var f = Fixture();
            var s = f.NewGame();
            var objects = s.Interaction.Objects;
            s.Items.Inventories.Give(ActorId.Player, Key, 1);
            objects.Unlock("door");
            objects.ToggleDoor("door");
            s.Items.Inventories.Give(ActorId.Player, Lockpick, 50);
            s.Progression.LearnSkill("Perk_LockPick_1");
            s.Progression.LearnSkill("Perk_LockPick_2");
            objects.Unlock("chest");
            objects.OpenContainer("chest");
            objects.Take("chest", "It_Gold", 20);

            var loaded = f.Reload(s).Interaction.Objects;
            Assert.That(loaded.IsLocked("door"), Is.False);
            Assert.That(loaded.IsOpen("door"), Is.True);
            Assert.That(loaded.IsLocked("chest"), Is.False);
            Assert.That(loaded.IsFilled("chest"), Is.True);
            loaded.OpenContainer("chest");
            Assert.That(f.Reload(s).Items.Inventories.Of(InteractiveObjects.ContainerOf("chest")).Count("It_Bread"), Is.EqualTo(1));
            Assert.That(f.Reload(s).Items.Inventories.Of(InteractiveObjects.ContainerOf("chest")).Count("It_Gold"), Is.EqualTo(0));
        }

        [Test]
        public void Sleep_SkipsToEightAndRestores()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Tick(3600f * 13 / 12); // 08:00 + 13 h = 21:00
            Assert.That(s.Clock.HourOfDay, Is.EqualTo(21f).Within(0.01f));
            double skipped = s.Interaction.Sleep.SleepUntilMorning();
            Assert.That(skipped, Is.EqualTo(11 * 3600.0).Within(1));
            Assert.That(s.Clock.HourOfDay, Is.EqualTo(8f).Within(0.01f));
            Assert.That(s.Clock.Day, Is.EqualTo(1));
        }

        sealed class FixedZone : IZoneLocator
        {
            readonly string _zone;
            public FixedZone(string zone) => _zone = zone;
            public string ZoneAt(float x, float y, float z) => _zone;
        }
    }
}
