using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-226 [owner]: the take-all chord on a dead animal takes what the loot page would show — the trophies the hero's
    /// perks allow (CP 1.75 Trophy sets: teeth / skin / fur only with Perk_TrophyTeeth / Skin / Fur; meat ungated), late
    /// trophies, and what the animal carries — grouped, no crime; without the perks and nothing else it says "NothingToGet".
    /// The chord also finds the body when the focus went to something else or a weapon is still drawn.
    /// </summary>
    public sealed class QuickLootAnimalTests
    {
        static readonly ActorId Wolf = new ActorId("Wolf"), Rabbit = new ActorId("Rabbit_01");

        static SessionFixture Fixture() => new SessionFixture()
            .Add(RulesSchema.ItemFolder, ItemTests.Item("It_Wolf_Fur", 50, kind: "TrophyFur"), ItemTests.Item("It_Wolf_Teeth", 20, kind: "TrophyTeeth"),
                 ItemTests.Item("It_Meat_Raw", 5, kind: "Meat"), ItemTests.Item("It_Ring", 50), ItemTests.Item("It_Gold", 1),
                 ItemTests.Item("It_Rabbit_Fur", 10, kind: "TrophyFur"))
            .Add(RulesSchema.TreasureFolder,
                 new TreasureSetDef("TS_Trophy_Wolf", TreasureUse.Trophy, 0, 0,
                                    new[] { new TreasureStack("It_Wolf_Fur", 1, 0), new TreasureStack("It_Wolf_Teeth", 2, 0), new TreasureStack("It_Meat_Raw", 2, 0) }),
                 new TreasureSetDef("TS_Trophy_Rabbit", TreasureUse.Trophy, 0, 0, new[] { new TreasureStack("It_Rabbit_Fur", 1, 0) }))
            .Add(RulesSchema.NpcInventoryFolder,
                 new NpcInventoryDef(Wolf, new[] { new TreasureStack("It_Ring", 1, 0) }, plunderSets: new[] { "TS_Trophy_Wolf" }),
                 new NpcInventoryDef(Rabbit, plunderSets: new[] { "TS_Trophy_Rabbit" }))
            .Add(SimulationSchema.NpcFolder, Npc(Wolf, "Wolf"), Npc(Rabbit, "Rabbit"));

        static NpcDef Npc(ActorId id, string species) =>
            new NpcDef(id, new TextId("FO_" + id.Name), "Bandit").WithCombat(new NpcCombatDef(1, 5, species, false));

        static int Held(GameSession s, string item) => s.Items.Inventories.Of(ActorId.Player).Count(item);

        [Test]
        public void DeadWolf_WithTheTrophyPerks_GivesTrophiesMeatAndWhatItCarries_Grouped_NoCrime()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Progression.LearnSkill(NpcLoot.TrophyFurPerk);
            s.Progression.LearnSkill(NpcLoot.TrophyTeethPerk);
            var broadcasts = new List<BroadcastKind>();
            s.Events.Subscribe<PerceptionBroadcast>(b => { if (b.Kind == BroadcastKind.TakeItem) broadcasts.Add(b.Kind); });
            s.Combat.Vitals.Kill(Wolf, byPlayerParty: true);
            var page = s.Items.NpcLoot.Pool(Wolf).Select(p => (p.Item, p.Amount)).ToList();

            var r = new QuickLoot(s).Body(Wolf);
            Assert.That(r.Outcome, Is.EqualTo(QuickLootOutcome.Taken));
            Assert.That(r.Lines.Select(l => (l.Item, l.Amount)), Is.EquivalentTo(page), "exactly what the loot page shows");
            Assert.That(r.Lines.Select(l => (l.Item, l.Amount)), Is.EquivalentTo(new[]
            {
                ("It_Ring", 1), ("It_Wolf_Fur", 1), ("It_Wolf_Teeth", 2), ("It_Meat_Raw", 2),
            }));
            Assert.That(r.Theft, Is.False);
            Assert.That(broadcasts, Is.Empty, "plundering an animal is no crime");
            Assert.That(s.Items.NpcLoot.BodyFocusable(Wolf, dead: true), Is.False, "looted out: G3 focuses it no more");
        }

        [Test]
        public void DeadWolf_WithoutPerks_MeatAndCarriedOnly_ThenNothingToGet()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Combat.Vitals.Kill(Wolf, byPlayerParty: true);
            var r = new QuickLoot(s).Body(Wolf);
            Assert.That(r.Lines.Select(l => l.Item), Is.EquivalentTo(new[] { "It_Ring", "It_Meat_Raw" }), "no fur, no teeth without the perks");
            Assert.That(Held(s, "It_Wolf_Fur") + Held(s, "It_Wolf_Teeth"), Is.Zero);
            Assert.That(new QuickLoot(s).Body(Wolf).Outcome, Is.EqualTo(QuickLootOutcome.Nothing), "the hero's \"NothingToGet\", as Use on it");
        }

        [Test]
        public void DeadRabbit_OnlyAFurTrophy_WithoutThePerk_IsNothingToGet()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Combat.Vitals.Kill(Rabbit, byPlayerParty: true);
            Assert.That(s.Items.NpcLoot.HasLoot(Rabbit), Is.False);
            Assert.That(new QuickLoot(s).Body(Rabbit).Outcome, Is.EqualTo(QuickLootOutcome.Nothing));
        }

        [Test]
        public void LateTrophies_APerkLearntAfterTheKill_TheChordTakesItsFurOnce()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Items.NpcLoot.LateTrophies = true;
            s.Combat.Vitals.Kill(Wolf, byPlayerParty: true);
            new QuickLoot(s).Body(Wolf);
            s.Progression.LearnSkill(NpcLoot.TrophyFurPerk);
            var r = new QuickLoot(s).Body(Wolf);
            Assert.That(r.Lines, Is.EqualTo(new[] { new QuickLootLine("It_Wolf_Fur", 1) }));
            Assert.That(new QuickLoot(s).Body(Wolf).Outcome, Is.EqualTo(QuickLootOutcome.Nothing), "once");
            Assert.That(Held(s, "It_Meat_Raw"), Is.EqualTo(2), "meat not twice");
        }

        [Test]
        public void Notices_GotLinesPerItem_ForAnAnimal()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Progression.LearnSkill(NpcLoot.TrophyFurPerk);
            using var notices = new GameNotices(s);
            var posted = new List<string>();
            notices.Posted += (text, kind) => { if (kind == NoticeKind.Item) posted.Add(text); };
            s.Combat.Vitals.Kill(Wolf, byPlayerParty: true);
            new QuickLoot(s).Body(Wolf);
            Assert.That(posted.Count, Is.EqualTo(3), "ring, fur, meat: " + string.Join(" | ", posted));
        }

        // ------------------------------------------------------------------------------------------------ chord target

        [Test]
        public void Chord_EmptyHands_LootableFocusFirst_ElseTheBodyInFront()
        {
            Assert.That(QuickLootChordTarget.Decide(true, true, false, ChordFocus.Lootable, ChordBody.WithLoot), Is.EqualTo(ChordAction.LootFocus));
            Assert.That(QuickLootChordTarget.Decide(true, true, false, ChordFocus.Other, ChordBody.WithLoot), Is.EqualTo(ChordAction.LootBody),
                        "a plant or object by the corpse took the focus");
            Assert.That(QuickLootChordTarget.Decide(true, false, false, ChordFocus.None, ChordBody.WithLoot), Is.EqualTo(ChordAction.LootBody),
                        "right button held + click: the same");
            Assert.That(QuickLootChordTarget.Decide(true, true, false, ChordFocus.None, ChordBody.Empty), Is.EqualTo(ChordAction.LootBody),
                        "nothing else in front: NothingToGet");
            Assert.That(QuickLootChordTarget.Decide(true, true, false, ChordFocus.Other, ChordBody.Empty), Is.EqualTo(ChordAction.Default),
                        "an empty corpse does not steal the use of a living NPC or object");
            Assert.That(QuickLootChordTarget.Decide(true, true, false, ChordFocus.Other, ChordBody.None), Is.EqualTo(ChordAction.Default));
            Assert.That(QuickLootChordTarget.Decide(false, false, false, ChordFocus.Lootable, ChordBody.WithLoot), Is.EqualTo(ChordAction.Default),
                        "no chord: the loot page as in G3");
        }

        [Test]
        public void Chord_WeaponDrawn_OnlyCtrlOntoABodyWithLoot_ElseTheAttack()
        {
            Assert.That(QuickLootChordTarget.Decide(true, true, true, ChordFocus.None, ChordBody.WithLoot), Is.EqualTo(ChordAction.LootBody),
                        "the weapon of the fight is still drawn");
            Assert.That(QuickLootChordTarget.Decide(true, false, true, ChordFocus.None, ChordBody.WithLoot), Is.EqualTo(ChordAction.Default),
                        "right button = the parade: the attack goes");
            Assert.That(QuickLootChordTarget.Decide(true, true, true, ChordFocus.None, ChordBody.Empty), Is.EqualTo(ChordAction.Default));
            Assert.That(QuickLootChordTarget.Decide(true, true, true, ChordFocus.None, ChordBody.None), Is.EqualTo(ChordAction.Default));
        }

        [Test]
        public void Sneak_HeldBack_WhenTheChordCouldLoot()
        {
            Assert.That(QuickLootChordTarget.DefersSneak(false, ChordFocus.Lootable, ChordBody.None), Is.True);
            Assert.That(QuickLootChordTarget.DefersSneak(false, ChordFocus.Other, ChordBody.WithLoot), Is.True, "body in front, focus elsewhere");
            Assert.That(QuickLootChordTarget.DefersSneak(true, ChordFocus.None, ChordBody.WithLoot), Is.True, "weapon drawn");
            Assert.That(QuickLootChordTarget.DefersSneak(false, ChordFocus.None, ChordBody.Empty), Is.False, "nothing to take: G3 sneak on the press");
            Assert.That(QuickLootChordTarget.DefersSneak(true, ChordFocus.None, ChordBody.None), Is.False);
        }

        [Test]
        public void Chord_FocusedFloorItem_AlwaysWins_EmptyHands()
        {
            foreach (var body in new[] { ChordBody.WithLoot, ChordBody.Empty, ChordBody.None })
            {
                Assert.That(QuickLootChordTarget.Decide(true, true, false, ChordFocus.Item, body), Is.EqualTo(ChordAction.TakeItem),
                            "Ctrl + click on the dropped weapon by the corpse takes the weapon: " + body);
                Assert.That(QuickLootChordTarget.Decide(true, false, false, ChordFocus.Item, body), Is.EqualTo(ChordAction.TakeItem),
                            "right button held + click: the same: " + body);
                Assert.That(QuickLootChordTarget.Decide(false, false, false, ChordFocus.Item, body), Is.EqualTo(ChordAction.Default),
                            "plain click: the plain use picks it up: " + body);
            }
        }

        [Test]
        public void Chord_WeaponDrawn_ItemInFrontOfTheBody_KeepsThePressForTheStance()
        {
            Assert.That(QuickLootChordTarget.Decide(true, true, true, ChordFocus.Item, ChordBody.WithLoot), Is.EqualTo(ChordAction.Default),
                        "G3 takes no items with a weapon drawn; the body behind the item is not looted either");
            Assert.That(QuickLootChordTarget.Decide(true, false, true, ChordFocus.Item, ChordBody.WithLoot), Is.EqualTo(ChordAction.Default));
            Assert.That(QuickLootChordTarget.DefersSneak(true, ChordFocus.Item, ChordBody.WithLoot), Is.False, "the attack follows: G3 sneak");
            Assert.That(QuickLootChordTarget.DefersSneak(false, ChordFocus.Item, ChordBody.None), Is.True, "the chord takes the item");
            Assert.That(QuickLootChordTarget.DefersSneak(false, ChordFocus.Item, ChordBody.WithLoot), Is.True);
        }

        [Test]
        public void Chord_RightButtonHeld_EveryLeftClickLootsItsTarget_OnlyTheJustEmptiedOneIsSkipped()
        {
            // RMB press quick-loots body A, then LMB clicks within the same hold
            const string a = "a:Ogre#1", b = "a:Ogre#2", chest = "o:Chest_7";
            Assert.That(QuickLootChordTarget.SwallowsLeftClick(true, a, b), Is.False, "LMB on a second body B loots B");
            Assert.That(QuickLootChordTarget.SwallowsLeftClick(true, a, chest), Is.False, "LMB on a chest loots it");
            Assert.That(QuickLootChordTarget.SwallowsLeftClick(true, a, a), Is.True, "LMB on A that RMB just emptied: no-op");
            Assert.That(QuickLootChordTarget.SwallowsLeftClick(true, a, null), Is.False, "floor item or nothing: the press goes on");
            Assert.That(QuickLootChordTarget.SwallowsLeftClick(true, null, a), Is.False, "RMB looted nothing: the chord loots A");
            Assert.That(QuickLootChordTarget.SwallowsLeftClick(false, a, a), Is.False, "the Ctrl chord (no RMB hold) is never swallowed");
            // after B: the chord on B again finds B empty; on A (empty) still skipped; Decide gives the chord's action
            Assert.That(QuickLootChordTarget.Decide(true, false, false, ChordFocus.Lootable, ChordBody.None), Is.EqualTo(ChordAction.LootFocus),
                        "B looted by the chord; a further click on emptied B says NothingToGet (QuickLoot Nothing)");
            Assert.That(QuickLootChordTarget.Decide(true, true, true, ChordFocus.None, ChordBody.WithLoot), Is.EqualTo(ChordAction.LootBody),
                        "Ctrl path unchanged");
        }
    }
}
