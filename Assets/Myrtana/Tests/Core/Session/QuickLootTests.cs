using System.Collections.Generic;
using System.Linq;
using G3.Player;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-205 [owner] QoL (not G3): take everything from a body, sleeper or chest without the loot page — the same moves as
    /// the page's "Take all" (stacks, gold, no spell books, mission items included), the same crime consequences, grouped
    /// "Got: …" lines; the Ctrl / right button chord and its key binding next to Sneak.
    /// </summary>
    public sealed class QuickLootTests
    {
        static readonly ActorId Gnar = SessionFixture.Npc;
        static readonly float[] ChestPos = { 1, 2, 3 };

        static SessionFixture Fixture() => new SessionFixture()
            .Add(RulesSchema.ItemFolder, ItemTests.Item("It_Gold", 1), ItemTests.Item("It_Ring", 50),
                 ItemTests.Item("It_Letter", 0, flags: ItemFlags.Mission), Spellbook("It_Spell_Fireball"))
            .Add(RulesSchema.NpcInventoryFolder,
                 new NpcInventoryDef(Gnar, new[]
                 {
                     new TreasureStack("It_Gold", 40, 0), new TreasureStack("It_Ring", 1, 0), new TreasureStack("It_Ring", 2, 1),
                     new TreasureStack("It_Letter", 1, 0), new TreasureStack("It_Spell_Fireball", 1, 0),
                 }))
            .Add(RulesSchema.InteractiveObjectFolder,
                 new InteractiveObjectDef("chest", "G3_Object_Interact_Animated_Chest_02", InteractiveKind.Chest, "Chest", "chest.FXA", ChestPos,
                                          null, null, 1, new ActorId("Ali"), null,
                                          new ContainerDef(new string[0], new[] { new TreasureStack("It_Gold", 20, 0), new TreasureStack("It_Ring", 1, 0) }), null, null),
                 new InteractiveObjectDef("locked", "G3_Object_Interact_Animated_Chest_02", InteractiveKind.Chest, "Chest", "chest.FXA", ChestPos,
                                          null, null, 1, new ActorId("Ali"), new LockDef(true, 1, null, 1),
                                          new ContainerDef(new string[0], new[] { new TreasureStack("It_Gold", 5, 0) }), null, null));

        static ItemDef Spellbook(string id) =>
            new ItemDef(id, new TextId("FO_" + id), default, ItemCategory.Spellbook, "Cast", EquipSlot.None, 10, 0, null, null,
                        null, null, null, null, null, null, ItemFlags.None);

        static List<AttackReason> Thefts(GameSession s)
        {
            var list = new List<AttackReason>();
            s.Events.Subscribe<PerceptionBroadcast>(b => { if (b.Kind == BroadcastKind.TakeItem) list.Add(b.Reason); });
            return list;
        }

        [Test]
        public void Group_SumsStacksPerItemInOrder_AndSplitsTheGold()
        {
            var lines = QuickLoot.Group(new[] { ("It_Ring", 1), ("It_Gold", 30), ("It_Letter", 1), ("It_Ring", 2), ("It_Gold", 12), ("It_X", 0) },
                                        "It_Gold", out int gold);
            Assert.That(gold, Is.EqualTo(42));
            Assert.That(lines, Is.EqualTo(new[] { new QuickLootLine("It_Ring", 3), new QuickLootLine("It_Letter", 1) }));
        }

        [Test]
        public void DeadBody_TakesWhatTheLootPageTakes_GoldAndStacksGrouped_NoTheft()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Crime.ReportPlayer(new WorldPose(0, 0, 0, 0));
            var thefts = Thefts(s);
            s.Combat.Vitals.Kill(Gnar, byPlayerParty: true);

            var r = new QuickLoot(s).Body(Gnar);
            Assert.That(r.Outcome, Is.EqualTo(QuickLootOutcome.Taken));
            Assert.That(r.Gold, Is.EqualTo(40));
            Assert.That(r.Lines, Is.EquivalentTo(new[] { new QuickLootLine("It_Ring", 3), new QuickLootLine("It_Letter", 1) }),
                        "both ring stacks in one line; the mission letter is lootable as on the page");
            var hero = s.Items.Inventories.Of(ActorId.Player);
            Assert.That(hero.Count("It_Gold"), Is.EqualTo(40));
            Assert.That(hero.Count("It_Ring"), Is.EqualTo(3));
            Assert.That(hero.Count("It_Spell_Fireball"), Is.Zero, "spell books stay (as on the loot page)");
            Assert.That(s.Items.Inventories.Of(Gnar).Count("It_Spell_Fireball"), Is.EqualTo(1));
            Assert.That(r.Theft, Is.False);
            Assert.That(thefts, Is.Empty, "looting a corpse is no crime");

            Assert.That(new QuickLoot(s).Body(Gnar).Outcome, Is.EqualTo(QuickLootOutcome.Nothing), "emptied");
        }

        [Test]
        public void LivingAwakeNpc_IsNotLootable()
        {
            using var f = Fixture();
            var s = f.NewGame();
            Assert.That(new QuickLoot(s).Body(Gnar).Outcome, Is.EqualTo(QuickLootOutcome.NotLootable));
            Assert.That(new QuickLoot(s).Sleeper(Gnar, heroSneaking: false).Outcome, Is.EqualTo(QuickLootOutcome.NotLootable), "awake: talk");
            Assert.That(s.Items.Inventories.Of(ActorId.Player).Count("It_Gold"), Is.Zero);
        }

        [Test]
        public void SpellSleeper_EachStackIsATheft_AsViaTheLootPage()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Crime.ReportPlayer(new WorldPose(0, 0, 0, 0));
            var thefts = Thefts(s);
            Assert.That(s.SpellEffects.World.Sleep(Gnar), Is.True);

            var r = new QuickLoot(s).Sleeper(Gnar, heroSneaking: false);
            Assert.That(r.Outcome, Is.EqualTo(QuickLootOutcome.Taken));
            Assert.That(r.Gold, Is.EqualTo(40));
            Assert.That(r.Theft, Is.True);
            Assert.That(r.Thefts, Is.EqualTo(thefts.Count(t => t == AttackReason.Theft)));
            Assert.That(r.Thefts, Is.EqualTo(4), "one theft per stack taken: gold, two rings, letter");
        }

        [Test]
        public void SpellSleeper_SneakingMasterThief_NoTheft()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Crime.ReportPlayer(new WorldPose(0, 0, 0, 0));
            s.Progression.LearnSkill(PickpocketRules.MasterThiefPerk);
            s.SpellEffects.World.Sleep(Gnar);
            var r = new QuickLoot(s).Sleeper(Gnar, heroSneaking: true);
            Assert.That(r.Outcome, Is.EqualTo(QuickLootOutcome.Taken));
            Assert.That(r.Theft, Is.False);
        }

        [Test]
        public void OwnedChest_OpensAndTakesAll_EachStackATheft_LockedChestRefuses()
        {
            using var f = Fixture();
            var s = f.NewGame();
            var thefts = Thefts(s);
            var ql = new QuickLoot(s);

            var locked = ql.Chest("locked");
            Assert.That(locked.Outcome, Is.EqualTo(QuickLootOutcome.Locked));
            Assert.That(s.Items.Inventories.Of(ActorId.Player).Count("It_Gold"), Is.Zero, "nothing moves from a locked chest");
            Assert.That(thefts, Is.Empty);

            var r = ql.Chest("chest");
            Assert.That(r.Outcome, Is.EqualTo(QuickLootOutcome.Taken));
            Assert.That(r.Gold, Is.EqualTo(20));
            Assert.That(r.Lines, Is.EqualTo(new[] { new QuickLootLine("It_Ring", 1) }));
            Assert.That(r.Thefts, Is.EqualTo(2), "Ali's chest: each stack taken is a theft (ContainerItemTaken)");
            Assert.That(s.Items.Inventories.Of(InteractiveObjects.ContainerOf("chest")).Stacks, Is.Empty);
            Assert.That(ql.Chest("chest").Outcome, Is.EqualTo(QuickLootOutcome.Nothing));
            Assert.That(ql.Chest("nope").Outcome, Is.EqualTo(QuickLootOutcome.NotLootable));
        }

        [Test]
        public void Notices_PostOneLinePerItemAndOneGoldLine_NotOnePerStack()
        {
            using var f = Fixture();
            var s = f.NewGame();
            using var notices = new GameNotices(s);
            var posted = new List<string>();
            notices.Posted += (text, kind) => { if (kind == NoticeKind.Item) posted.Add(text); };
            s.Combat.Vitals.Kill(Gnar, byPlayerParty: true);
            new QuickLoot(s).Body(Gnar);
            Assert.That(posted.Count, Is.EqualTo(3), "ring (2 stacks), letter, gold: " + string.Join(" | ", posted));
            Assert.That(posted.Last(), Does.Contain("40"), "the gold line comes last with the sum");

            posted.Clear();
            s.Items.Inventories.Give(Gnar, "It_Ring", 1);
            s.Items.Inventories.Transfer(Gnar, ActorId.Player, "It_Ring", 1);
            Assert.That(posted.Count, Is.EqualTo(1), "outside a quick loot every move is announced at once");
        }

        // ------------------------------------------------------------------------------------------------ input

        [Test]
        public void Route_ChordTurnsUse1IntoQuickLoot_OnlyWithEmptyHands()
        {
            Assert.That(HeroUseInput.Route(HeroInput.Primary, false, true, true, takeAllChord: true), Is.EqualTo(HeroInputRoute.QuickLoot));
            Assert.That(HeroUseInput.Route(HeroInput.Primary, false, true, true, takeAllChord: false), Is.EqualTo(HeroInputRoute.Use), "normal use unchanged");
            Assert.That(HeroUseInput.Route(HeroInput.Primary, true, true, true, takeAllChord: true), Is.EqualTo(HeroInputRoute.Combat));
            Assert.That(HeroUseInput.Route(HeroInput.Primary, false, false, true, takeAllChord: true), Is.EqualTo(HeroInputRoute.None), "a page has the cursor");
            Assert.That(QuickLootChord.Active(true, modifierHeld: true, secondaryHeld: false), Is.True);
            Assert.That(QuickLootChord.Active(true, modifierHeld: false, secondaryHeld: true), Is.True, "right button held + left click");
            Assert.That(QuickLootChord.Active(false, true, true), Is.False, "option off = G3");
            Assert.That(QuickLootChord.Active(true, false, false), Is.False);
        }

        [Test]
        public void ChordedToggle_FiresOnPress_OrOnReleaseUnlessAChordUsedTheKey()
        {
            var t = new ChordedToggle();
            Assert.That(t.Press(defer: false), Is.True, "no loot in focus: G3 toggles on the press");
            Assert.That(t.Press(defer: true), Is.False);
            Assert.That(t.Pending, Is.True);
            Assert.That(t.Release(), Is.True, "Ctrl tapped alone facing a corpse: sneak on release");
            t.Press(defer: true);
            t.Consume();
            Assert.That(t.Release(), Is.False, "Ctrl + click: no sneak toggle");
            t.Consume();
            Assert.That(t.Release(), Is.False);
        }

        [Test]
        public void KeyMap_ModifierSharesCtrlWithSneak_AndIsRebindable()
        {
            var map = KeyMap.Default();
            var lctrl = KeyInput.Key(Dik.LeftCtrl);
            Assert.That(map.IsBound(SessionKey.Down, lctrl), Is.True, "Sneak keeps G3's Ctrl");
            Assert.That(map.IsBound(SessionKey.QuickLootModifier, lctrl), Is.True, "default Ctrl");
            Assert.That(map.Owner(lctrl, out _), Is.EqualTo(SessionKey.Down), "a normal key first");
            Assert.That(map.ConflictOwner(SessionKey.QuickLootModifier, lctrl, out _), Is.EqualTo(SessionKey.QuickLootModifier));
            Assert.That(map.ConflictOwner(SessionKey.Up, lctrl, out _), Is.EqualTo(SessionKey.Down));

            var lalt = KeyInput.Key(Dik.LeftAlt);
            Assert.That(map.Assign(SessionKey.QuickLootModifier, 0, lalt), Is.Null, "a modifier takes no input from a normal key");
            Assert.That(map.IsBound(SessionKey.Up, lalt), Is.True, "Jump keeps Left Alt");
            map.Assign(SessionKey.Down, 1, lalt);
            Assert.That(map.IsBound(SessionKey.QuickLootModifier, lalt), Is.True, "and the other way round");

            var parsed = KeyMap.Parse(map.Serialize());
            Assert.That(parsed.Get(SessionKey.QuickLootModifier, 0), Is.EqualTo(lalt));
            Assert.That(parsed.Get(SessionKey.Down, 1), Is.EqualTo(lalt), "shared inputs survive the storage text");

            var old = KeyMap.Parse("Down=0:157,0:29"); // a text from before fix-205
            Assert.That(old.IsBound(SessionKey.QuickLootModifier, lctrl), Is.True, "missing key: its default, although Sneak has Ctrl");
            Assert.That(KeyMap.IsPortKey(SessionKey.QuickLootModifier) && !KeyMap.IsPortKey(SessionKey.Down), Is.True);
            Assert.That(KeyMap.PortText(SessionKey.QuickLootModifier, "Russian"), Does.StartWith("Забрать всё"));
            Assert.That(KeyMap.PortText(SessionKey.QuickLootModifier, "English"), Does.StartWith("Take all"));
        }

        [Test]
        public void PortOption_DefaultsOn_WithARowOnTheControlsPage()
        {
            Assert.That(new PlayerOptions().Port.QuickLootChord, Is.True, "[owner] default on");
            Assert.That(PortOptions.ValueText(PortOptions.Row.QuickLootChord, false, "English"), Is.EqualTo("No (original)"));
            Assert.That(PortOptions.RowText(PortOptions.Row.QuickLootChord, "Russian"), Does.Contain("Ctrl"));
        }
    }
}
