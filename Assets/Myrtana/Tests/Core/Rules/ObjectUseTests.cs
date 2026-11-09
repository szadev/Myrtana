using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>par-interact: ore veins, treasure spots, water barrels, shrines, regeneration, consuming, robbing sleepers.</summary>
    public sealed class ObjectUseTests
    {
        static readonly RuleOptions Classic = new RuleOptions(alternativeBalancing: false);
        static readonly RuleOptions Ab = new RuleOptions(alternativeBalancing: true);

        static InteractiveObjectDef Obj(string id, string name, InteractiveKind kind, string useType, ContainerDef container = null,
                                        string useFunc = null) =>
            new InteractiveObjectDef(id, name, kind, useType, "mesh.xcmsh", new float[] { 1, 2, 3 }, null, null, 1, default, null,
                                     container, null, null, useFunc);

        static SessionFixture Fixture() => new SessionFixture()
            .Add(RulesSchema.ItemFolder, ItemTests.Item("It_Pickaxe"), ItemTests.Item("It_Shovel"), ItemTests.Item("It_Saw"), ItemTests.Item("It_DrumStick"), ItemTests.Item("It_Gold"),
                 ItemTests.Item("It_Nugget"), ItemTests.Item("It_Sulfur"),
                 ItemTests.Item("It_Potion_Health", use: new[] { new AttributeGain("HP", ModifierOp.AddToVal, 100) }),
                 ItemTests.Item("It_Potion_HP_Max", use: new[] { new AttributeGain("HP", ModifierOp.AddToMax, 10) }),
                 ItemTests.Item("It_Apple_Str", use: new[] { new AttributeGain("STR", ModifierOp.AddToVal, 1) },
                                grants: new[] { "Perk_Test" }))
            .Add(RulesSchema.TreasureFolder,
                 new TreasureSetDef("TS_Mining_Gold", TreasureUse.Mining, 1, 1, new[] { new TreasureStack("It_Nugget", 3, 0), new TreasureStack("It_Sulfur", 1, 0) }),
                 new TreasureSetDef("TS_Plunder_Junk", TreasureUse.Plunder, 1, 1, new[] { new TreasureStack("It_Gold", 5, 0) }),
                 new TreasureSetDef("TS_Treasure", TreasureUse.Plunder, 1, 1, new[] { new TreasureStack("It_Gold", 50, 0) }))
            .Add(RulesSchema.InteractiveObjectFolder,
                 Obj("vein", "G3_Object_Interact_Pick_Gold_01", InteractiveKind.OreVein, "PickOre",
                     new ContainerDef(new[] { "TS_Mining_Gold", "TS_Plunder_Junk" }, null)),
                 Obj("spot", "G3_Object_Interact_Treasure_Spot_01", InteractiveKind.Treasure, "PirateTreasure",
                     new ContainerDef(new[] { "TS_Treasure" }, null)),
                 Obj("spot2", "G3_Object_Interact_PirateTreasure_01", InteractiveKind.Treasure, "PirateTreasure",
                     new ContainerDef(new[] { "TS_Treasure", "TS_Plunder_Junk" }, null), useFunc: "Dug_Spot2"),
                 Obj("barrel", "G3_Object_Interact_Barrel_01", InteractiveKind.WaterBarrel, "WaterBarrel", useFunc: "Drank_Water"),
                 Obj("bench", "G3_Object_Interact_Bench_01", InteractiveKind.Seat, "Bench"),
                 Obj("ancestor", "Living_Ancestor_01_Tester", InteractiveKind.Speaker, "Shrine"),
                 Obj("beliar", "Beliar_Shrine_Tester", InteractiveKind.Speaker, "Shrine"),
                 Obj("statue", "Innos_Tester", InteractiveKind.Speaker, "None"),
                 Obj("pipe", "G3_Object_Interact_Waterpipe_01", InteractiveKind.Activity, "Waterpipe"),
                 Obj("log", "G3_Object_Interact_SawLog_01", InteractiveKind.Activity, "SawLog"),
                 Obj("drum", "G3_Object_Interact_OrcDrum_01", InteractiveKind.Activity, "OrcDrum"));

        // ------------------------------------------------------------------------------------------------ regeneration

        [Test]
        public void Regen_MasterGladiator_IsFlatFiveClassic_AndCappedHpMaxOver80UnderAb()
        {
            var s = new HeroRegenState { MasterGladiator = true, HitPointsMax = 320 };
            Assert.That(RegenRules.HitPointsPerSecond(s, Classic), Is.EqualTo(5));
            Assert.That(RegenRules.HitPointsPerSecond(s, Ab), Is.EqualTo(4), "320/80");
            s.HitPointsMax = 1000;
            Assert.That(RegenRules.HitPointsPerSecond(s, Ab), Is.EqualTo(5), "min(5, …)");
            s.HitPointsMax = 60;
            Assert.That(RegenRules.HitPointsPerSecond(s, Ab), Is.EqualTo(0), "no minimum of 1");
            s.HitPointsMax = 1000;
            s.InCombat = true;
            Assert.That(RegenRules.HitPointsPerSecond(s, Ab), Is.EqualTo(0), "AB: none in combat");
            Assert.That(RegenRules.HitPointsPerSecond(s, Classic), Is.EqualTo(5), "classic: also in combat");
        }

        [Test]
        public void Regen_Sitting_GivesHpMaxOver100_OnlyWithoutMasterGladiator()
        {
            var s = new HeroRegenState { Sitting = true, HitPointsMax = 350 };
            Assert.That(RegenRules.HitPointsPerSecond(s, Classic), Is.EqualTo(3));
            Assert.That(RegenRules.HitPointsPerSecond(s, Ab), Is.EqualTo(3));
            s.HitPointsMax = 900;
            Assert.That(RegenRules.HitPointsPerSecond(s, Ab), Is.EqualTo(5), "capped at 5");
            s.MasterGladiator = true;
            Assert.That(RegenRules.HitPointsPerSecond(s, Classic), Is.EqualTo(5), "the perk replaces sitting, no sum");
            Assert.That(RegenRules.HitPointsPerSecond(new HeroRegenState { HitPointsMax = 900 }, Ab), Is.EqualTo(0), "standing: nothing");
        }

        [Test]
        public void Regen_PageOpenStopsGainUnderAb_BarrierAndDeathAlways()
        {
            var s = new HeroRegenState { MasterGladiator = true, MasterMage = true, HitPointsMax = 800, ManaMax = 250, PageOpen = true };
            Assert.That(RegenRules.HitPointsPerSecond(s, Ab), Is.EqualTo(0));
            Assert.That(RegenRules.ManaPerSecond(s, Ab), Is.EqualTo(0));
            Assert.That(RegenRules.HitPointsPerSecond(s, Classic), Is.EqualTo(5));
            Assert.That(RegenRules.ManaPerSecond(s, Classic), Is.EqualTo(5));
            s.InMagicBarrier = true;
            Assert.That(RegenRules.HitPointsPerSecond(s, Classic), Is.EqualTo(-5), "+5 − 10");
            s.Dead = true;
            Assert.That(RegenRules.HitPointsPerSecond(s, Classic), Is.EqualTo(-10), "the barrier penalty hits everyone");
        }

        [Test]
        public void Regen_MasterMage_ClassicFive_AbClampedMpMaxOver100()
        {
            var s = new HeroRegenState { MasterMage = true, ManaMax = 50 };
            Assert.That(RegenRules.ManaPerSecond(s, Classic), Is.EqualTo(5));
            Assert.That(RegenRules.ManaPerSecond(s, Ab), Is.EqualTo(1), "at least 1");
            s.ManaMax = 320;
            Assert.That(RegenRules.ManaPerSecond(s, Ab), Is.EqualTo(3));
            s.ManaMax = 2000;
            Assert.That(RegenRules.ManaPerSecond(s, Ab), Is.EqualTo(5));
            Assert.That(RegenRules.ManaPerSecond(new HeroRegenState { ManaMax = 2000 }, Ab), Is.EqualTo(0), "no perk");
        }

        [Test]
        public void SessionTick_SittingHeroRegainsHitPointsEverySecond()
        {
            using var f = Fixture();
            var s = f.NewGame();
            var stats = s.Hero.Stats;
            int max = stats.Maximum(ActorVitals.HitPointsId);
            Assume.That(max, Is.GreaterThan(100));
            s.Combat.Vitals.AddHitPoints(ActorId.Player, -50);
            int before = stats.Value(ActorVitals.HitPointsId);
            s.Tick(1.5f);
            Assert.That(stats.Value(ActorVitals.HitPointsId), Is.EqualTo(before), "standing: no regeneration");
            s.Regeneration.Sitting = true;
            s.Tick(1.0f); // 0.5 s carried + 1 s: one tick while sitting
            int per = System.Math.Min(5, max / 100);
            Assert.That(stats.Value(ActorVitals.HitPointsId), Is.EqualTo(before + per));
            s.Tick(2.0f);
            Assert.That(stats.Value(ActorVitals.HitPointsId), Is.EqualTo(before + 3 * per));
        }

        // ------------------------------------------------------------------------------------------------ ore veins

        [Test]
        public void OreVein_NeedsPickaxe_GivesEveryMiningStackOnce_DoubledWithPerk()
        {
            using var f = Fixture();
            var s = f.NewGame();
            var uses = s.Interaction.Uses;
            var mined = new List<OreMined>();
            s.Events.Subscribe<OreMined>(mined.Add);
            Assert.That(uses.CheckMining("vein"), Is.EqualTo(ObjectUseVerdict.MissingTool));
            Assert.That(uses.Mine("vein"), Is.Empty);
            Assert.That(uses.CheckMining("barrel"), Is.EqualTo(ObjectUseVerdict.NotUsable));

            s.Items.Inventories.Give(ActorId.Player, "It_Pickaxe", 1);
            s.Progression.LearnSkill("Perk_Mining");
            Assert.That(uses.CheckMining("vein"), Is.EqualTo(ObjectUseVerdict.Ok));
            var gained = uses.Mine("vein");
            var hero = s.Items.Inventories.Of(ActorId.Player);
            Assert.That(hero.Count("It_Nugget"), Is.EqualTo(6), "every stack in full, x2 with Perk_Mining");
            Assert.That(hero.Count("It_Sulfur"), Is.EqualTo(2));
            Assert.That(hero.Count("It_Gold"), Is.Zero, "only Mining sets");
            Assert.That(gained.Select(g => g.Item), Is.EqualTo(new[] { "It_Nugget", "It_Sulfur" }));
            Assert.That(mined.Select(m => m.Amount), Is.EqualTo(new[] { 6, 2 }), "one GO_Mining message per stack");
            Assert.That(hero.Count("It_Pickaxe"), Is.EqualTo(1), "the pickaxe is kept");

            Assert.That(uses.CheckMining("vein"), Is.EqualTo(ObjectUseVerdict.NothingToGet), "UsedByPlayer");
            Assert.That(uses.Mine("vein"), Is.Empty);

            var loaded = f.Reload(s);
            Assert.That(loaded.Interaction.Uses.WasMined("vein"), Is.True, "saved as world.veins");
        }

        [Test]
        public void OreVein_WithoutPerk_GivesTheSetAmounts()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Items.Inventories.Give(ActorId.Player, "It_Pickaxe", 1);
            s.Interaction.Uses.Mine("vein");
            Assert.That(s.Items.Inventories.Of(ActorId.Player).Count("It_Nugget"), Is.EqualTo(3));
        }

        // ------------------------------------------------------------------------------------------------ treasure spots

        [Test]
        public void Treasure_NeedsShovel_DigOpensPlunder_EmptySpotHasNothing()
        {
            using var f = Fixture();
            var s = f.NewGame();
            var uses = s.Interaction.Uses;
            Assert.That(uses.CheckDig("spot"), Is.EqualTo(ObjectUseVerdict.MissingTool));
            Assert.That(uses.Dig("spot"), Is.False);
            s.Items.Inventories.Give(ActorId.Player, "It_Shovel", 1);
            Assert.That(uses.Dig("spot"), Is.True);
            Assert.That(s.Interaction.Objects.IsFilled("spot"), Is.True, "plunder generated");
            Assert.That(uses.CheckDig("spot"), Is.EqualTo(ObjectUseVerdict.Ok), "not empty yet: dig again opens the loot");
            s.Interaction.Objects.TakeAll("spot");
            Assert.That(s.Items.Inventories.Of(ActorId.Player).Count("It_Gold"), Is.InRange(25, 50), "G3 plunder: rand[amount/2, amount]");
            Assert.That(uses.CheckDig("spot"), Is.EqualTo(ObjectUseVerdict.NothingToGet));
        }

        [Test]
        public void Treasure_PlunderRolledOnce_DigAgainShowsWhatWasLeft()
        {
            using var f = Fixture();
            var s = f.NewGame();
            var uses = s.Interaction.Uses;
            var spot = s.Items.Inventories.Of(InteractiveObjects.ContainerOf("spot2"));
            s.Items.Inventories.Give(ActorId.Player, "It_Shovel", 1);
            Assert.That(uses.WasDug("spot2"), Is.False);
            Assert.That(uses.Dig("spot2"), Is.True);
            Assert.That(uses.WasDug("spot2"), Is.True);
            int gold = spot.Count("It_Gold");
            Assert.That(gold, Is.GreaterThan(0));
            s.Interaction.Objects.Take("spot2", "It_Gold", 1);
            Assert.That(uses.Dig("spot2"), Is.True, "not empty: dug again");
            Assert.That(spot.Count("It_Gold"), Is.EqualTo(gold - 1), "GeneratePlunderInvFromTreasureSets: no second roll");
            Assert.That(s.Items.Inventories.Of(ActorId.Player).Count("It_Shovel"), Is.EqualTo(1), "the shovel is kept");
            Assert.That(uses.WasDug("vein"), Is.False, "only treasure spots are dug");
        }

        [Test]
        public void Treasure_DigSetsTheUseFuncAsGameEvent_RefusedDigDoesNot()
        {
            using var f = Fixture();
            var s = f.NewGame();
            var uses = s.Interaction.Uses;
            Assert.That(uses.Dig("spot2"), Is.False, "no shovel");
            Assert.That(s.Flags.IsSet("Dug_Spot2"), Is.False);
            Assert.That(s.Interaction.Objects.IsFilled("spot2"), Is.False, "a refused dig generates nothing");
            s.Items.Inventories.Give(ActorId.Player, "It_Shovel", 1);
            Assert.That(uses.Dig("spot2"), Is.True);
            Assert.That(s.Flags.IsSet("Dug_Spot2"), Is.True, "PS_Interact_PirateTreasure: SetGameEvent(UseFunc) for the player");
        }

        [Test]
        public void Treasure_DugStateAndLeftoverSurviveSaveAndLoad_EmptiedStaysNothingToGet()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Items.Inventories.Give(ActorId.Player, "It_Shovel", 1);
            s.Interaction.Uses.Dig("spot2");
            int stacks = s.Items.Inventories.Of(InteractiveObjects.ContainerOf("spot2")).Stacks.Count;
            Assert.That(stacks, Is.GreaterThan(0));

            var loaded = f.Reload(s);
            Assert.That(loaded.Interaction.Uses.WasDug("spot2"), Is.True);
            Assert.That(loaded.Items.Inventories.Of(InteractiveObjects.ContainerOf("spot2")).Stacks.Count, Is.EqualTo(stacks), "the left plunder");
            Assert.That(loaded.Interaction.Uses.CheckDig("spot2"), Is.EqualTo(ObjectUseVerdict.Ok));
            loaded.Interaction.Uses.Dig("spot2");
            Assert.That(loaded.Items.Inventories.Of(InteractiveObjects.ContainerOf("spot2")).Stacks.Count, Is.EqualTo(stacks), "no new roll after a load");
            loaded.Interaction.Objects.TakeAll("spot2");
            Assert.That(loaded.Interaction.Uses.CheckDig("spot2"), Is.EqualTo(ObjectUseVerdict.NothingToGet));

            var again = f.Reload(loaded);
            Assert.That(again.Interaction.Uses.WasDug("spot2"), Is.True);
            Assert.That(again.Interaction.Uses.CheckDig("spot2"), Is.EqualTo(ObjectUseVerdict.NothingToGet), "emptied spots stay empty");
            Assert.That(again.Interaction.Uses.CheckDig("spot"), Is.EqualTo(ObjectUseVerdict.Ok), "other spots untouched");
        }

        // ------------------------------------------------------------------------------------------------ barrels, shrines

        [Test]
        public void WaterBarrel_HealsTen_AndSetsItsUseEvent()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Combat.Vitals.AddHitPoints(ActorId.Player, -40);
            int before = s.Hero.Stats.Value(ActorVitals.HitPointsId);
            Assert.That(s.Interaction.Uses.DrinkFromBarrel("barrel"), Is.True);
            Assert.That(s.Interaction.Uses.DrinkFromBarrel("barrel"), Is.True, "no limit");
            Assert.That(s.Hero.Stats.Value(ActorVitals.HitPointsId), Is.EqualTo(before + 20));
            Assert.That(s.Flags.IsSet("Drank_Water"), Is.True);
            Assert.That(s.Interaction.Uses.DrinkFromBarrel("bench"), Is.False);
        }

        [Test]
        public void Shrine_OpposedGodRefuses()
        {
            using var f = Fixture();
            var s = f.NewGame();
            var uses = s.Interaction.Uses;
            Assert.That(uses.IsShrine("ancestor") && uses.IsShrine("beliar"), Is.True);
            Assert.That(uses.IsShrine("statue"), Is.False, "a talking object without UseType Shrine");
            Assert.That(uses.ShrineRefuses("beliar"), Is.False, "no god chosen");
            s.Flags.Set("Innos_chosen");
            Assert.That(uses.ShrineRefuses("beliar"), Is.True);
            Assert.That(uses.ShrineRefuses("ancestor"), Is.False);

            var flags = new GameEventFlags();
            flags.Set("Adanos_chosen");
            Assert.That(ObjectUses.ShrineRefuses("Innos_Shrine", flags) && ObjectUses.ShrineRefuses("Beliar_Shrine", flags), Is.True);
            Assert.That(ObjectUses.ShrineRefuses("Adanos_Shrine", flags), Is.False);
            flags = new GameEventFlags();
            flags.Set("Beliar_chosen");
            Assert.That(ObjectUses.ShrineRefuses("Innos_Shrine", flags), Is.True);
            Assert.That(ObjectUses.ShrineRefuses("Beliar_Shrine", flags), Is.False);
        }

        // ------------------------------------------------------------------------------------------------ consuming

        [Test]
        public void Consuming_AnnouncesMaximumRaisesAndAttributes_NotHealing_AndLearnedSkills()
        {
            using var f = Fixture();
            var s = f.NewGame();
            var announced = new List<ItemEffectAnnounced>();
            var learned = new List<SkillLearnedFromItem>();
            s.Events.Subscribe<ItemEffectAnnounced>(announced.Add);
            s.Events.Subscribe<SkillLearnedFromItem>(learned.Add);
            s.Items.Inventories.Give(ActorId.Player, "It_Potion_Health", 1);
            s.Items.Inventories.Give(ActorId.Player, "It_Potion_HP_Max", 1);
            s.Items.Inventories.Give(ActorId.Player, "It_Apple_Str", 2);
            int maxBefore = s.Hero.Stats.Maximum("HP"), strBefore = s.Hero.Stats.Value("STR");

            Assert.That(s.Items.Use.Use("It_Potion_Health"), Is.True);
            Assert.That(announced, Is.Empty, "healing (AddToVal on HP) is not announced");
            s.Items.Use.Use("It_Potion_HP_Max");
            Assert.That(announced.Single().Attribute, Is.EqualTo("HP"));
            Assert.That(announced.Single().Maximum, Is.True);
            Assert.That(announced.Single().OldValue, Is.EqualTo(maxBefore));
            Assert.That(announced.Single().Amount, Is.EqualTo(10));

            announced.Clear();
            s.Items.Use.Use("It_Apple_Str");
            Assert.That(announced.Single().Attribute, Is.EqualTo("STR"));
            Assert.That(announced.Single().OldValue, Is.EqualTo(strBefore));
            Assert.That(learned.Single().Skill, Is.EqualTo("Perk_Test"));
            s.Items.Use.Use("It_Apple_Str");
            Assert.That(learned, Has.Count.EqualTo(1), "GO_Learned only for a skill not known before");

            Assert.That(ItemUse.Announces("SP", ModifierOp.AddToMax), Is.True);
            Assert.That(ItemUse.Announces("MP", ModifierOp.AddToVal), Is.False);
            Assert.That(ItemUse.Announces("DEX", ModifierOp.AddToMax), Is.False);
        }

        [Test]
        public void ConsumeNotice_ReadsAttributeEqualsOldPlusAmount()
        {
            using var f = Fixture();
            var s = f.NewGame();
            using var notices = new GameNotices(s);
            var posted = new List<string>();
            notices.Posted += (text, _) => posted.Add(text);
            s.Items.Inventories.Give(ActorId.Player, "It_Potion_HP_Max", 1);
            int max = s.Hero.Stats.Maximum("HP");
            s.Items.Use.Use("It_Potion_HP_Max");
            Assert.That(posted, Has.Some.EqualTo($"{s.Localizer.Text(new TextId("ATTRIB_HPMax"))} = {max} + 10"));
        }

        // ------------------------------------------------------------------------------------------------ sleepers

        [Test]
        public void RobbingHelpless_NeedsSneakMasterThiefAndHumanoid()
        {
            Assert.That(PickpocketRules.CanRobHelpless(true, true, true, true), Is.True);
            Assert.That(PickpocketRules.CanRobHelpless(false, true, true, true), Is.False, "awake: AssessTalk");
            Assert.That(PickpocketRules.CanRobHelpless(true, false, true, true), Is.False, "not sneaking");
            Assert.That(PickpocketRules.CanRobHelpless(true, true, false, true), Is.False, "no Perk_MasterThief");
            Assert.That(PickpocketRules.CanRobHelpless(true, true, true, false), Is.False, "animals");
        }

        [Test]
        public void SleeperTheft_AwakeNpcIsNotHelpless()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Progression.LearnSkill(PickpocketRules.MasterThiefPerk);
            Assert.That(s.SleeperTheft.IsHelpless(SessionFixture.Npc), Is.False);
            Assert.That(s.SleeperTheft.CanRob(SessionFixture.Npc, heroSneaking: true), Is.False);
            Assert.That(s.SleeperTheft.IsHelpless(ActorId.Player), Is.False);
        }

        // ------------------------------------------------------------------------------------------------ activities (par-misc)

        [Test]
        public void Activities_WaterpipeIsFree_SawNeedsTheSaw_DrumNeedsTwoSticks()
        {
            using var f = Fixture();
            var s = f.NewGame();
            var uses = s.Interaction.Uses;
            Assert.That(uses.CheckActivity("pipe"), Is.EqualTo(ObjectUseVerdict.Ok));
            Assert.That(uses.CheckActivity("log"), Is.EqualTo(ObjectUseVerdict.MissingTool));
            Assert.That(uses.ActivityToolOf("log"), Is.EqualTo("It_Saw"));
            Assert.That(uses.CheckActivity("vein"), Is.EqualTo(ObjectUseVerdict.NotUsable));
            s.Items.Inventories.Give(ActorId.Player, "It_Saw", 1);
            Assert.That(uses.CheckActivity("log"), Is.EqualTo(ObjectUseVerdict.Ok));
            s.Items.Inventories.Give(ActorId.Player, "It_DrumStick", 1);
            Assert.That(uses.CheckActivity("drum"), Is.EqualTo(ObjectUseVerdict.MissingTool), "HasItems It_DrumStick 2");
            Assert.That(uses.ActivityToolOf("drum"), Is.EqualTo("It_DrumStick"));
            s.Items.Inventories.Give(ActorId.Player, "It_DrumStick", 1);
            Assert.That(uses.CheckActivity("drum"), Is.EqualTo(ObjectUseVerdict.Ok));
        }
    }
}
