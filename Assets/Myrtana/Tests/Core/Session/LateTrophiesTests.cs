using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-143 [owner] port option "Late trophies": G3 CP 1.75 rolls a body's trophies once, at the knock-out or death, by the
    /// perks the hero knows then (CleanUpPlunderInv → GeneratePlunderInvFromTreasureSets, GeneratedPlunder flag); with the
    /// option a corpse rolled before the hero learnt a trophy perk gives that perk's trophies when looked at again, once.
    /// </summary>
    public class LateTrophiesTests
    {
        static readonly ActorId Wolf = new ActorId("Wolf"), Bandit = new ActorId("Bandit");

        static SessionFixture Fixture() => new SessionFixture()
            .Add(RulesSchema.ItemFolder, ItemTests.Item("It_Wolf_Fur", 50, kind: "TrophyFur"), ItemTests.Item("It_Wolf_Teeth", 20, kind: "TrophyTeeth"),
                 ItemTests.Item("It_Meat_Raw", 5, kind: "Meat"), ItemTests.Item("It_Gold", 1))
            .Add(RulesSchema.TreasureFolder,
                 new TreasureSetDef("TS_Trophy_Wolf", TreasureUse.Trophy, 0, 0,
                                    new[] { new TreasureStack("It_Wolf_Fur", 1, 0), new TreasureStack("It_Wolf_Teeth", 2, 0), new TreasureStack("It_Meat_Raw", 2, 0) }))
            .Add(RulesSchema.NpcInventoryFolder, new NpcInventoryDef(Wolf, plunderSets: new[] { "TS_Trophy_Wolf" }),
                 new NpcInventoryDef(Bandit, new[] { new TreasureStack("It_Gold", 5, 0) }))
            .Add(SimulationSchema.NpcFolder, Npc(Wolf, "Wolf"), Npc(Bandit, "Human"));

        static NpcDef Npc(ActorId id, string species) =>
            new NpcDef(id, new TextId("FO_" + id.Name), "Bandit").WithCombat(new NpcCombatDef(1, 5, species, species == "Human"));

        static GameSession LootedWolf(SessionFixture f, bool late)
        {
            var s = f.NewGame();
            s.Items.NpcLoot.LateTrophies = late;
            s.Combat.Vitals.Kill(Wolf, byPlayerParty: true);
            Assert.That(s.Items.NpcLoot.TakeAll(Wolf).Select(t => t.Item), Is.EquivalentTo(new[] { "It_Meat_Raw" }), "no perk: meat only");
            Assert.That(s.Items.NpcLoot.BodyFocusable(Wolf, dead: true), Is.False, "looted out");
            return s;
        }

        static int Held(GameSession s, string item) => s.Items.Inventories.Of(ActorId.Player).Count(item);

        [Test]
        public void Off_IsTheOriginal_TheFurIsNeverGivenLater()
        {
            using var f = Fixture();
            var s = LootedWolf(f, late: false);
            s.Progression.LearnSkill(NpcLoot.TrophyFurPerk);
            Assert.That(s.Items.NpcLoot.HasLoot(Wolf), Is.False, "G3: trophies were decided at death");
            Assert.That(s.Items.NpcLoot.BodyFocusable(Wolf, dead: true), Is.False);
        }

        [Test]
        public void On_ALootedCorpse_GivesTheFurOfAPerkLearntLater_ExactlyOnce()
        {
            using var f = Fixture();
            var s = LootedWolf(f, late: true);
            var loot = s.Items.NpcLoot;
            Assert.That(loot.HasLoot(Wolf), Is.False, "no new perk yet");
            s.Progression.LearnSkill(NpcLoot.TrophyFurPerk);
            Assert.That(loot.BodyFocusable(Wolf, dead: true), Is.True, "the corpse can be focused again");
            Assert.That(loot.Pool(Wolf).Select(p => (p.Item, p.Amount)), Is.EquivalentTo(new[] { ("It_Wolf_Fur", 1) }), "fur only: no meat or teeth again");
            loot.TakeAll(Wolf);
            Assert.That(Held(s, "It_Wolf_Fur"), Is.EqualTo(1));
            Assert.That(Held(s, "It_Meat_Raw"), Is.EqualTo(2), "meat not duplicated");
            Assert.That(loot.HasLoot(Wolf), Is.False, "once per perk");
            Assert.That(loot.GrantLateTrophies(Wolf), Is.Zero);

            s.Progression.LearnSkill(NpcLoot.TrophyTeethPerk);
            Assert.That(loot.Pool(Wolf).Select(p => (p.Item, p.Amount)), Is.EquivalentTo(new[] { ("It_Wolf_Teeth", 2) }), "the next perk adds its own trophies");
            loot.TakeAll(Wolf);
            Assert.That(Held(s, "It_Wolf_Fur"), Is.EqualTo(1), "fur not again");
            Assert.That(loot.HasLoot(Wolf), Is.False);
        }

        [Test]
        public void On_ACorpseRolledWithThePerk_GetsNothingTwice()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Items.NpcLoot.LateTrophies = true;
            s.Progression.LearnSkill(NpcLoot.TrophyFurPerk);
            s.Combat.Vitals.Kill(Wolf, byPlayerParty: true);
            s.Items.NpcLoot.TakeAll(Wolf);
            Assert.That(Held(s, "It_Wolf_Fur"), Is.EqualTo(1));
            Assert.That(s.Items.NpcLoot.HasLoot(Wolf), Is.False, "the fur was decided at death");
        }

        [Test]
        public void On_ADownedOrUnrolledNpc_GetsNoLateTrophies()
        {
            using var f = Fixture();
            var s = f.NewGame();
            var loot = s.Items.NpcLoot;
            loot.LateTrophies = true;
            s.Progression.LearnSkill(NpcLoot.TrophyFurPerk);
            Assert.That(loot.GrantLateTrophies(Wolf), Is.Zero, "alive, never rolled");

            var t = f.NewGame();
            t.Items.NpcLoot.LateTrophies = true;
            t.Combat.Vitals.KnockOut(Wolf, byPlayerParty: true);
            t.Items.NpcLoot.TakeAll(Wolf);
            t.Progression.LearnSkill(NpcLoot.TrophyFurPerk);
            Assert.That(t.Items.NpcLoot.GrantLateTrophies(Wolf), Is.Zero, "downed, not a corpse");
            Assert.That(t.Items.NpcLoot.HasLoot(Wolf), Is.False);
        }

        [Test]
        public void TheDecidedPerks_SurviveASave_SoALoadNeverGivesTwice()
        {
            using var f = Fixture();
            var s = LootedWolf(f, late: true);
            s.Progression.LearnSkill(NpcLoot.TrophyFurPerk);
            s.Items.NpcLoot.TakeAll(Wolf);
            Assert.That(Held(s, "It_Wolf_Fur"), Is.EqualTo(1));

            var loaded = f.Reload(s);
            loaded.Items.NpcLoot.LateTrophies = true;
            Assert.That(loaded.Items.NpcLoot.HasLoot(Wolf), Is.False, "fur already granted before the save");
            loaded.Progression.LearnSkill(NpcLoot.TrophyTeethPerk);
            Assert.That(loaded.Items.NpcLoot.Pool(Wolf).Select(p => p.Item), Is.EquivalentTo(new[] { "It_Wolf_Teeth" }), "a perk learnt after the load still counts");
        }

        [Test]
        public void ASaveBeforeLearning_GivesTheFurAfterTheLoad()
        {
            using var f = Fixture();
            var s = LootedWolf(f, late: true);
            var loaded = f.Reload(s);
            loaded.Items.NpcLoot.LateTrophies = true;
            loaded.Progression.LearnSkill(NpcLoot.TrophyFurPerk);
            Assert.That(loaded.Items.NpcLoot.Pool(Wolf).Select(p => p.Item), Is.EquivalentTo(new[] { "It_Wolf_Fur" }));
        }

        [Test]
        public void PortOption_DefaultsOn_WithRussianTexts()
        {
            Assert.That(new PortOptions().LateTrophies, Is.True, "[owner] default Yes (fix-143)");
            Assert.That(PortOptions.RowText(PortOptions.Row.LateTrophies, "Russian"), Is.EqualTo("Трофеи после обучения"));
            Assert.That(PortOptions.RowText(PortOptions.Row.LateTrophies, "English"), Is.EqualTo("Late trophies"));
            Assert.That(PortOptions.ValueText(PortOptions.Row.LateTrophies, false, "Russian"), Is.EqualTo("Нет (оригинал)"), "position 0 = original");
            Assert.That(PortOptions.ValueText(PortOptions.Row.LateTrophies, true, "Russian"), Is.EqualTo("Да"));
            using var f = Fixture();
            Assert.That(f.NewGame().Items.NpcLoot.LateTrophies, Is.False, "the rules alone play like CP 1.75 (the menu's applier turns it on)");
        }

        [Test]
        public void ControlsTopBand_HoldsLateTrophiesAndCookFire_WithoutOverlap()
        {
            float late = Myrtana.Ui.Skin.ControlsPortRowsLayout.LateTrophiesTop, cook = Myrtana.Ui.Skin.ControlsPortRowsLayout.CookFireTop;
            Assert.That(late, Is.GreaterThanOrEqualTo(Myrtana.Ui.Skin.ControlsPortRowsLayout.FrameTop + 10f), "inside the page frame");
            Assert.That(late + 25f, Is.LessThanOrEqualTo(cook), "label boxes");
            Assert.That(Myrtana.Ui.Skin.ControlsPortRowsLayout.RowBottom(late), Is.LessThanOrEqualTo(cook + Myrtana.Ui.Skin.ControlsPortRowsLayout.SliderOffset), "sliders");
            Assert.That(Myrtana.Ui.Skin.ControlsPortRowsLayout.RowBottom(cook), Is.LessThan(Myrtana.Ui.Skin.ControlsPortRowsLayout.MouseHeadingTop), "above G3's Mouse heading");
        }
    }
}
