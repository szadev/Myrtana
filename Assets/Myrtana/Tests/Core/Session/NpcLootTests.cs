using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>NPC inventories, plunder, looting bodies and dropped weapons (npc-inv; Docs/formats-items.md §3.6).</summary>
    public class NpcLootTests
    {
        static readonly ActorId Bandit = new ActorId("Bandit"), Trader = new ActorId("Trader"), Wolf = new ActorId("Wolf");

        static SessionFixture Fixture() => new SessionFixture()
            .Add(RulesSchema.ItemFolder, ItemTests.Item("It_Gold", 1), ItemTests.Item("It_Potion_Health", 250, use: new[] { new AttributeGain("HP", ModifierOp.AddToVal, 100) }),
                 ItemTests.Item("It_Sword", 100, EquipSlot.MeleeWeapon, "OneHanded"), ItemTests.Item("Arrow", 1, EquipSlot.Ammo, "Arrow"),
                 ItemTests.Item("It_Letter", 0, flags: ItemFlags.Mission), ItemTests.Item("It_Wolf_Fur", 50, kind: "TrophyFur"),
                 ItemTests.Item("It_Meat_Raw", 5, kind: "Meat"), Spellbook("It_Spell_Fireball"))
            .Add(RulesSchema.TreasureFolder,
                 new TreasureSetDef("TS_Plunder_Bandit", TreasureUse.Plunder, 1, 1, new[] { new TreasureStack("It_Potion_Health", 1, 0) }),
                 new TreasureSetDef("TS_Trophy_Wolf", TreasureUse.Trophy, 0, 0, new[] { new TreasureStack("It_Wolf_Fur", 1, 0), new TreasureStack("It_Meat_Raw", 2, 0) }),
                 new TreasureSetDef("TS_Trader", TreasureUse.NotRandom, 0, 0, new[] { new TreasureStack("It_Potion_Health", 5, 0), new TreasureStack("It_Gold", 300, 0) }))
            .Add(RulesSchema.MerchantFolder, new MerchantDef(Trader, "Orc", null, new[] { "TS_Trader" }, true))
            .Add(RulesSchema.NpcInventoryFolder,
                 new NpcInventoryDef(Bandit, new[] { new TreasureStack("It_Letter", 1, 0), new TreasureStack("It_Gold", 40, 0), new TreasureStack("It_Spell_Fireball", 1, 0) },
                                     new[] { new TreasureStack("It_Sword", 1, 0), new TreasureStack("Arrow", 20, 0) }, new[] { "TS_Plunder_Bandit" }),
                 new NpcInventoryDef(Trader, new[] { new TreasureStack("It_Gold", 10, 0) }),
                 new NpcInventoryDef(Wolf, plunderSets: new[] { "TS_Trophy_Wolf" }))
            .Add(SimulationSchema.NpcFolder, Npc(Bandit, "Human"), Npc(Trader, "Human"), Npc(Wolf, "Wolf"));

        static NpcDef Npc(ActorId id, string species) =>
            new NpcDef(id, new TextId("FO_" + id.Name), "Bandit").WithCombat(new NpcCombatDef(1, 5, species, species == "Human",
                                                                                                equipment: species == "Human" ? new[] { "It_Sword", "Arrow" } : null));

        static ItemDef Spellbook(string id) =>
            new ItemDef(id, new TextId("FO_" + id), default, ItemCategory.Spellbook, "Cast", EquipSlot.None, 10, 0, null, null,
                        null, null, null, null, null, null, ItemFlags.None);

        [Test]
        public void StaticStacks_AreCarriedFromTheStart_AndSurviveALoad()
        {
            using var f = Fixture();
            var s = f.NewGame();
            Assert.That(s.Items.Inventories.Of(Bandit).Count("It_Letter"), Is.EqualTo(1));
            Assert.That(s.Items.Inventories.Of(Bandit).Count("It_Gold"), Is.EqualTo(40));
            var loaded = f.Reload(s);
            Assert.That(loaded.Items.Inventories.Of(Bandit).Count("It_Gold"), Is.EqualTo(40), "not seeded twice");
        }

        [Test]
        public void Plunder_IsRolledOnceAtTheFirstKnockOut_AndAPotionCanBeDrunkLater()
        {
            using var f = Fixture();
            var s = f.NewGame();
            Assert.That(NpcPotions.Has(s.Items.Inventories, Bandit, "It_Potion_Health"), Is.False, "nothing before the knock-out");
            s.Combat.Vitals.KnockOut(Bandit, byPlayerParty: true);
            Assert.That(s.Items.NpcLoot.IsGenerated(Bandit), Is.True);
            Assert.That(s.Items.Inventories.Of(Bandit).Count("It_Potion_Health"), Is.EqualTo(1));
            Assert.That(NpcPotions.Has(s.Items.Inventories, Bandit, "It_Potion_Health"), Is.True, "plunder left on the body is carried");

            Assert.That(s.Items.NpcLoot.GeneratePlunder(Bandit), Is.False, "G3 GeneratedPlunder");
            var loaded = f.Reload(s);
            Assert.That(loaded.Items.NpcLoot.IsGenerated(Bandit), Is.True, "saved");
            loaded.Combat.Vitals.Kill(Bandit, byPlayerParty: true);
            Assert.That(loaded.Items.Inventories.Of(Bandit).Count("It_Potion_Health"), Is.EqualTo(1), "the death rolls nothing again");
        }

        [Test]
        public void Trophies_NeedTheHerosPerkAtTheMomentOfDeath()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Combat.Vitals.Kill(Wolf, byPlayerParty: true);
            Assert.That(s.Items.Inventories.Of(Wolf).Count("It_Wolf_Fur"), Is.Zero, "no Perk_TrophyFur");
            Assert.That(s.Items.Inventories.Of(Wolf).Count("It_Meat_Raw"), Is.EqualTo(2), "meat is not gated");

            var t = f.NewGame();
            t.Progression.LearnSkill(NpcLoot.TrophyFurPerk);
            t.Combat.Vitals.Kill(Wolf, byPlayerParty: true);
            Assert.That(t.Items.Inventories.Of(Wolf).Count("It_Wolf_Fur"), Is.EqualTo(1));
        }

        [Test]
        public void Loot_ShowsCarriedQuestItemsAndWeaponry_ButNoSpellBooks_AndTakingAWeaponDisarmsTheNpc()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Combat.Vitals.KnockOut(Bandit, byPlayerParty: true);
            var loot = s.Items.NpcLoot;
            var pool = loot.Pool(Bandit);
            Assert.That(pool.Select(p => p.Item), Is.EquivalentTo(new[] { "It_Letter", "It_Gold", "It_Potion_Health", "It_Sword", "Arrow" }));
            Assert.That(pool.Single(p => p.Item == "Arrow").Amount, Is.EqualTo(20));

            Assert.That(loot.Take(Bandit, "It_Sword", 1), Is.EqualTo(1));
            Assert.That(s.Items.Inventories.Of(ActorId.Player).Count("It_Sword"), Is.EqualTo(1));
            Assert.That(s.NpcCombat.HasLost(Bandit, "It_Sword"), Is.True, "fights without it");
            Assert.That(loot.Pool(Bandit).Any(p => p.Item == "It_Sword"), Is.False);

            var taken = loot.TakeAll(Bandit);
            Assert.That(taken.Select(t => t.Item), Is.EquivalentTo(new[] { "It_Letter", "It_Gold", "It_Potion_Health", "Arrow" }));
            Assert.That(loot.HasLoot(Bandit), Is.False, "the spell book stays hidden");
            Assert.That(s.Items.Inventories.Of(Bandit).Count("It_Spell_Fireball"), Is.EqualTo(1));

            s.NpcCombat.RestoreWeaponry(Bandit); // G3 AssureItems when it enters the processing range again
            Assert.That(s.NpcCombat.HasLost(Bandit, "It_Sword"), Is.False);
        }

        [Test]
        public void LootedWeaponry_LeavesTheBodysLoadout_TellsThePresentation_AndStaysGoneAfterALoad()
        {
            // fix-149 [owner]: the sword and shield taken from a skeleton stayed on its body; the body view re-shows its
            // weapons on NpcCombatSystem.WeaponryChanged, from the loadout without the lost items (saved).
            using var f = Fixture();
            var s = f.NewGame();
            s.Combat.Vitals.Kill(Bandit, byPlayerParty: true);
            var changed = new System.Collections.Generic.List<ActorId>();
            s.NpcCombat.WeaponryChanged += changed.Add;
            Assert.That(s.NpcCombat.LoadoutOf(Bandit).MeleeItem?.Id, Is.EqualTo("It_Sword"));

            Assert.That(s.Items.NpcLoot.Take(Bandit, "It_Sword", 1), Is.EqualTo(1));
            Assert.That(changed, Is.EqualTo(new[] { Bandit }), "the body is re-shown once");
            Assert.That(s.NpcCombat.LoadoutOf(Bandit).MeleeItem, Is.Null);
            s.NpcCombat.TakeItem(Bandit, "It_Sword");
            Assert.That(changed.Count, Is.EqualTo(1), "nothing changed the second time");

            var loaded = f.Reload(s);
            Assert.That(loaded.NpcCombat.LoadoutOf(Bandit).MeleeItem, Is.Null, "a reloaded corpse has no sword either");
            Assert.That(loaded.Items.NpcLoot.Pool(Bandit).Any(p => p.Item == "It_Sword"), Is.False);

            var back = new System.Collections.Generic.List<ActorId>();
            loaded.NpcCombat.WeaponryChanged += back.Add;
            loaded.NpcCombat.RestoreWeaponry(Bandit); // G3 AssureItems (a living NPC entering the range)
            Assert.That(back, Is.EqualTo(new[] { Bandit }));
        }

        [Test]
        public void ACopyOfAContentNpc_CarriesItsTemplatesStacksPlunderAndWeaponry()
        {
            // fix-120: a dev spawn ("Bandit~dev01") had no inventory definition of its own: nothing to loot or rob
            using var f = Fixture();
            var s = f.NewGame();
            var copy = new ActorId("Bandit~dev01");
            var loot = s.Items.NpcLoot;
            Assert.That(loot.HasLoot(copy), Is.False, "a bare copy carries nothing");
            loot.CarryAs(copy, Bandit);
            Assert.That(s.Items.Inventories.Of(copy).Count("It_Gold"), Is.EqualTo(40), "the template's static stacks");
            Assert.That(loot.GeneratePlunder(copy), Is.True);
            Assert.That(loot.Pool(copy).Select(p => p.Item),
                        Is.EquivalentTo(new[] { "It_Letter", "It_Gold", "It_Potion_Health", "It_Sword", "Arrow" }), "plunder sets and weaponry too");
            Assert.That(s.Items.Inventories.Of(Bandit).Count("It_Potion_Health"), Is.EqualTo(0), "the template itself is untouched");
        }

        [Test]
        public void ALootedOutCorpse_IsNotFocusable_ALivingOneStays()
        {
            using var f = Fixture();
            var s = f.NewGame();
            var loot = s.Items.NpcLoot;
            Assert.That(loot.BodyFocusable(Bandit, dead: false), Is.True, "alive");
            s.Combat.Vitals.Kill(Bandit, byPlayerParty: true);
            Assert.That(loot.BodyFocusable(Bandit, dead: true), Is.True, "something to loot");
            loot.TakeAll(Bandit);
            Assert.That(loot.BodyFocusable(Bandit, dead: true), Is.False, "fix-68: emptied (only the hidden spell book is left)");
            Assert.That(loot.BodyFocusable(Bandit, dead: false), Is.True, "the rule is for dead bodies only");
        }

        [Test]
        public void ATradersMerchandise_IsHiddenFromTheLoot()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Items.Trade.Open(Trader, 0);
            Assert.That(s.Items.Inventories.Of(Trader).Count("It_Gold"), Is.EqualTo(310));
            s.Combat.Vitals.Kill(Trader, byPlayerParty: true);
            var pool = s.Items.NpcLoot.Pool(Trader);
            Assert.That(pool.Select(p => (p.Item, p.Amount)), Is.EquivalentTo(new[] { ("It_Gold", 10) }), "his own gold only");
            s.Items.NpcLoot.TakeAll(Trader);
            Assert.That(s.Items.Inventories.Of(Trader).Count("It_Potion_Health"), Is.EqualTo(5));
            Assert.That(s.Items.NpcLoot.HasLoot(Trader), Is.False);
        }

        [Test]
        public void DroppedWeapons_AreWorldItems_SavedAndTakenByWhoeverPicksThemUp()
        {
            using var f = Fixture();
            var s = f.NewGame();
            var world = s.Items.World;
            var hero = world.Drop(ActorId.Player, "It_Sword", 1, 4, new[] { 1f, 2f, 3f }, 90f);
            var npc = world.Drop(Bandit, "It_Sword", 1, 0, new[] { 5f, 0f, 5f }, 0f);
            Assert.That(world.IsPresent(hero.Id) && world.IsPresent(npc.Id), Is.True);
            s.Combat.Vitals.KnockOut(Bandit, byPlayerParty: true);
            Assert.That(s.Items.NpcLoot.Pool(Bandit).Any(p => p.Item == "It_Sword"), Is.False, "the sword lies on the ground");

            var loaded = f.Reload(s);
            Assert.That(loaded.Items.World.TryGetDropped(hero.Id, out var back) && back.Quality == 4 && back.Position[2] == 3f && back.Yaw == 90f, Is.True);
            Assert.That(loaded.Items.World.TakeDropped(hero.Id, Bandit), Is.True, "a ransacker takes the hero's sword");
            Assert.That(loaded.Items.Inventories.Of(Bandit).Count("It_Sword"), Is.EqualTo(1), "…into his inventory, lootable");
            Assert.That(loaded.Items.World.PickUp(npc.Id), Is.True, "the hero picks up the bandit's sword");
            Assert.That(loaded.Items.Inventories.Of(ActorId.Player).Count("It_Sword"), Is.EqualTo(1));
            Assert.That(loaded.NpcCombat.HasLost(Bandit, "It_Sword"), Is.True);
            var next = loaded.Items.World.Drop(ActorId.Player, "Arrow", 1, 0, null, 0);
            Assert.That(next.Id, Is.Not.EqualTo(hero.Id).And.Not.EqualTo(npc.Id), "ids stay unique after a load");
        }
    }
}
