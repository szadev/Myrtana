using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Ui.Skin;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-75 owner bugs 7–9: the item panel lists every G3 stats row (gCHUDTableStackStats), the lists follow G3's
    /// gCInventoryStack::Compare, and the owner's sort modes order rows inside the G3 groups.
    /// </summary>
    public class ItemPanelOrderTests
    {
        static readonly AttributeGain[] None = new AttributeGain[0];

        static ItemDef Def(string id, ItemCategory category, string kind, int value = 10, EquipSlot slot = EquipSlot.None,
                           AttributeRequirement[] requirements = null, string[] requiredSkills = null, ItemDamage damage = null,
                           AttributeGain[] modifiers = null, string[] bonuses = null, int quality = 0, TextId name = default) =>
            new ItemDef(id, name.IsEmpty ? new TextId("FO_" + id) : name, default, category, kind, slot, value, quality, null, null,
                        requirements, requiredSkills, null, null, null, damage, ItemFlags.None, modifiers: modifiers ?? None,
                        skillBonuses: bonuses);

        static SpellDef Spell(string id, SpellDeity deity, int intelligence, int min, int max, params string[] items) =>
            new SpellDef(id, items, null, SpellCastType.Immediate, SpellTarget.Focus, SpellKind.Projectile, deity, min, max,
                         SpellDelivery.Projectile, null, null, intelligence, null, null, null);

        static readonly ItemDef OrcSlayer = Def("It_1H_OrcSlayer_01", ItemCategory.Weapon, "OneHanded", 600, EquipSlot.MeleeWeapon,
            new[] { new AttributeRequirement("STR", 100), new AttributeRequirement("DEX", 20) }, new[] { "Perk_1H_1" },
            new ItemDamage("Blade", 50, new TextId("HUD_Damage_Blade")), bonuses: new[] { "Perk_OrcSlayer" });

        static readonly ItemDef Ring = Def("It_Ring_Power", ItemCategory.Armor, "Ring", 900, EquipSlot.Ring, modifiers: new[]
        {
            new AttributeGain("STR", ModifierOp.AddToVal, 5), new AttributeGain("HP", ModifierOp.AddToMax, 20),
            new AttributeGain("PROT_FIRE", ModifierOp.AddToVal, 10), new AttributeGain("MP", ModifierOp.AddPercentageToVal, 10),
            new AttributeGain("DEX", ModifierOp.AddToVal, 0),
        });

        static readonly ItemDef Body = Def("Body_Paladin", ItemCategory.Armor, "Body", 15000, EquipSlot.Armor, modifiers: new[]
        {
            new AttributeGain("PROT_BLADE", ModifierOp.AddToVal, 100), new AttributeGain("PROT_IMPACT", ModifierOp.AddToVal, 81),
        });

        static readonly ItemDef Potion = Def("It_Potion_Health_01", ItemCategory.Alchemy, "Potion", 50,
            modifiers: new[] { new AttributeGain("HP", ModifierOp.AddPercentageToVal, 50) });

        static SessionFixture Fixture(params ItemDef[] more) => new SessionFixture()
            .Add(RulesSchema.ItemFolder, new[]
            {
                Def("It_Gold", ItemCategory.Misc, "Valuable", 1), OrcSlayer, Ring, Body, Potion,
                Def("It_Perk_OrcSlayer", ItemCategory.Skill, "None"), Def("It_Perk_1H_1", ItemCategory.Skill, "None"),
            }.Concat(more).ToArray())
            .Add(RulesSchema.SkillFolder, new SkillDef("Perk_OrcSlayer", "It_Perk_OrcSlayer", 5, null, null, "SkillCombat"),
                 new SkillDef("Perk_1H_1", "It_Perk_1H_1", 5, null, null, "SkillCombat"));

        static IEnumerable<(ItemLineKind kind, string label, string value)> Rows(ItemDetail d) => d.Lines.Select(l => (l.Kind, l.Label, l.Value));

        // fix-85 [owner]: the string table's placeholder "-" (ITEMDESC_It_Scavenger_Egg …) leaves the description line empty.
        [TestCase("-", "")]
        [TestCase(" - ", "")]
        [TestCase("", "")]
        [TestCase(null, "")]
        [TestCase("-5 HP", "-5 HP")]
        [TestCase("Ein Ei.", "Ein Ei.")]
        public void Panel_PlaceholderDescription_IsHidden(string text, string shown) =>
            Assert.That(ItemLists.ShownDescription(text), Is.EqualTo(shown));

        [Test]
        public void Panel_OrcSlayer_ListsRequirementsLastSlotFirst_SkillRequirement_Damage_SkillBonus_Gold()
        {
            using var f = Fixture();
            var session = f.NewGame();
            var detail = ItemLists.Detail(session, OrcSlayer.Id);
            Assert.That(Rows(detail), Is.EqualTo(new[]
            {
                (ItemLineKind.RequirementMet, "[ATTRIB_DEX]", "20"),
                (ItemLineKind.RequirementMet, "[ATTRIB_STR]", "100"),
                (ItemLineKind.RequirementUnmet, "[HUD_Req_Skill]", "[FO_It_Perk_1H_1]"),
                (ItemLineKind.Damage, "[HUD_Damage_Blade]", "50"),
                (ItemLineKind.Modifier, "[HUD_Bonus_Skill]", "[FO_It_Perk_OrcSlayer]"),
                (ItemLineKind.Gold, "[HUD_GoldValue]", "600"),
            }));
        }

        [Test]
        public void Panel_Ring_ProtectionsThenModifiersLastSlotFirst_MaxAndPercentSuffixes_SkipsZero()
        {
            using var f = Fixture();
            var detail = ItemLists.Detail(f.NewGame(), Ring.Id);
            Assert.That(Rows(detail), Is.EqualTo(new[]
            {
                (ItemLineKind.Protection, "PROT_FIRE", "+10"),
                (ItemLineKind.Modifier, "[ATTRIB_MP]", "+10%"),
                (ItemLineKind.Modifier, "[ATTRIB_HPMax]", "+20"),
                (ItemLineKind.Modifier, "[ATTRIB_STR]", "+5"),
                (ItemLineKind.Gold, "[HUD_GoldValue]", "900"),
            }));
        }

        [Test]
        public void Panel_WornOutArmour_HalvesProtections_NoQualityRowsForBodies()
        {
            using var f = Fixture();
            var detail = ItemLists.Detail(f.NewGame(), Body.Id, ItemQualityBits.Worn);
            Assert.That(Rows(detail), Is.EqualTo(new[]
            {
                (ItemLineKind.Protection, "PROT_IMPACT", "+40"),
                (ItemLineKind.Protection, "PROT_BLADE", "+50"),
                (ItemLineKind.Gold, "[HUD_GoldValue]", "15000"),
            }));
        }

        [Test]
        public void Panel_ForgedSharpenedWeapon_LowersRequirement_AddsDamage_ShowsQualityRowsWithIcons()
        {
            using var f = Fixture();
            var detail = ItemLists.Detail(f.NewGame(), OrcSlayer.Id, ItemQualityBits.Forged | ItemQualityBits.Sharpened, price: 77);
            Assert.That(detail.Lines[1].Value, Is.EqualTo("90"), "forged: 10 less strength");
            Assert.That(detail.Lines.Single(l => l.Kind == ItemLineKind.Damage).Value, Is.EqualTo("70"));
            var qualities = detail.Lines.Where(l => l.Kind == ItemLineKind.Quality).ToList();
            Assert.That(qualities.Select(l => (l.Label, l.Value, l.Quality)), Is.EqualTo(new[]
            {
                ("[HUD_Quality_Selfmade]", "(+10)", ItemQualityBits.Forged), ("[HUD_Quality_Sharp]", "(+10)", ItemQualityBits.Sharpened),
            }));
            Assert.That(detail.Lines.Last().Value, Is.EqualTo("77"), "the list's price (trade) is the gold row");
        }

        [Test]
        public void Panel_Potion_PercentEffect()
        {
            using var f = Fixture();
            var detail = ItemLists.Detail(f.NewGame(), Potion.Id);
            Assert.That(Rows(detail).First(), Is.EqualTo((ItemLineKind.Modifier, "[ATTRIB_HP]", "+50%")));
        }

        [Test]
        public void Panel_Rune_ShowsManaCostOfItsSpell()
        {
            var rune = Def("It_Rune_Fireball", ItemCategory.Artefact, "Cast", 300);
            using var f = Fixture(rune).Add(RulesSchema.SpellFolder, Spell("Spl_Fireball", SpellDeity.Innos, 50, 10, 30, rune.Id));
            var detail = ItemLists.Detail(f.NewGame(), rune.Id);
            Assert.That(Rows(detail), Is.EqualTo(new[]
            {
                (ItemLineKind.ManaCost, "[HUD_Req_MP]", "10 - 30"), (ItemLineKind.Gold, "[HUD_GoldValue]", "300"),
            }));
        }

        [Test]
        public void ShownDamage_And_ManaCost_FollowG3Scripts()
        {
            Assert.That(ItemLists.ShownDamage(50, ItemQualityBits.Worn | ItemQualityBits.Blessed), Is.EqualTo(35));
            Assert.That(ItemLists.ShownDamage(50, ItemQualityBits.Poisoned), Is.EqualTo(50));
            Assert.That(ItemLists.ManaCost(20, 20), Is.EqualTo("20"));
            Assert.That(ItemLists.ManaCost(20, 5), Is.EqualTo("20"));
            Assert.That(ItemLists.ManaCost(5, 20), Is.EqualTo("5 - 20"));
        }

        /// <summary>A mixed inventory: groups in gEItemCategory order, rows by gCInventoryStack::Compare.</summary>
        static ItemDef[] Mixed() => new[]
        {
            Def("It_Axe", ItemCategory.Weapon, "Axe", damage: new ItemDamage("Blade", 200, default)),
            Def("It_1H_B", ItemCategory.Weapon, "OneHanded", damage: new ItemDamage("Blade", 40, default)),
            Def("It_1H_A", ItemCategory.Weapon, "OneHanded", damage: new ItemDamage("Blade", 40, default)),
            Def("It_2H", ItemCategory.Weapon, "TwoHanded", damage: new ItemDamage("Blade", 10, default)),
            Def("It_Bow", ItemCategory.Weapon, "Bow", damage: new ItemDamage("Missile", 90, default)),
            Def("It_Shield", ItemCategory.Armor, "Shield", 50),
            Def("It_Amulet", ItemCategory.Armor, "Amulet", 50),
            Def("Body_Light", ItemCategory.Armor, "Body", 50, modifiers: new[] { new AttributeGain("PROT_BLADE", ModifierOp.AddToVal, 20) }),
            Def("It_Letter_X", ItemCategory.Artefact, "Letter", 5),
            Def("It_Rune_Heal", ItemCategory.Artefact, "Cast", 5),
            Def("It_Rune_Ice", ItemCategory.Artefact, "Cast", 5),
            Def("It_Rune_Bolt", ItemCategory.Artefact, "Cast", 5),
            Def("It_Plant_A", ItemCategory.Alchemy, "Plant", 5),
            Def("It_Meat", ItemCategory.Alchemy, "Meat", 5),
            Def("It_Trophy_B", ItemCategory.Misc, "TrophyTeeth", 5, name: new TextId("B_Tooth")),
            Def("It_Trophy_A", ItemCategory.Misc, "TrophyFur", 500, name: new TextId("A_Fur")),
            Def("It_Book", ItemCategory.Written, "Book", 5),
        };

        static SessionFixture MixedFixture() => Fixture(Mixed())
            .Add(RulesSchema.SpellFolder, Spell("Spl_Heal", SpellDeity.Innos, 10, 5, 5, "It_Rune_Heal"),
                 Spell("Spl_Ice", SpellDeity.Adanos, 60, 5, 5, "It_Rune_Ice"), Spell("Spl_Bolt", SpellDeity.Adanos, 20, 5, 5, "It_Rune_Bolt"));

        static GameSession Carrying(SessionFixture f, params string[] items)
        {
            var session = f.NewGame();
            foreach (var item in items) session.Items.Inventories.Of(ActorId.Player).Add(item, 1);
            return session;
        }

        static readonly string[] MixedIds = Mixed().Select(d => d.Id).Concat(new[] { Body.Id, Ring.Id, Potion.Id }).ToArray();

        [Test]
        public void Lists_GroupsAndRowsFollowG3()
        {
            using var f = MixedFixture();
            var session = Carrying(f, MixedIds);
            var groups = ItemLists.Of(session, ActorId.Player);
            Assert.That(groups.Select(g => g.Page), Is.EqualTo(new[]
            {
                ItemPage.Weapons, ItemPage.Armor, ItemPage.Artefacts, ItemPage.Alchemy, ItemPage.Misc, ItemPage.Books,
            }), "Misc (category 6) before Written (7)");
            Assert.That(groups.SelectMany(g => g.Rows).Select(r => r.Item), Is.EqualTo(new[]
            {
                "It_2H", "It_1H_A", "It_1H_B", "It_Bow", "It_Axe",        // rank 2H, 1H, Bow; Axe unranked; ties by template
                Body.Id, "Body_Light", "It_Shield", "It_Amulet", Ring.Id, // Body (PROT_BLADE 100 > 20), Shield, Amulet, Ring
                "It_Rune_Bolt", "It_Rune_Ice", "It_Rune_Heal", "It_Letter_X", // Cast: Adanos INT 20, 60, Innos; Letter unranked
                Potion.Id, "It_Meat", "It_Plant_A",                       // Potion 401, Meat 403, Plant 410
                "It_Trophy_A", "It_Trophy_B",                              // misc by shown name
                "It_Book",
            }));
        }

        [Test]
        public void SortModes_KeepGroups_OrderRowsInside()
        {
            using var f = MixedFixture();
            var session = Carrying(f, "It_Trophy_A", "It_2H", "It_1H_A", "It_Trophy_B", "It_Bow");
            session.Items.Inventories.Of(ActorId.Player).Add("It_2H", 1); // more of an old stack makes it the newest
            IEnumerable<string> Order(ItemSortMode mode) =>
                ItemLists.Of(session, ActorId.Player, sort: mode).SelectMany(g => g.Rows).Select(r => r.Item)
                         .Where(i => i.StartsWith("It_Trophy") || i == "It_2H" || i == "It_1H_A" || i == "It_Bow");

            Assert.That(Order(ItemSortMode.G3), Is.EqualTo(new[] { "It_2H", "It_1H_A", "It_Bow", "It_Trophy_A", "It_Trophy_B" }));
            Assert.That(Order(ItemSortMode.Name), Is.EqualTo(new[] { "It_1H_A", "It_2H", "It_Bow", "It_Trophy_A", "It_Trophy_B" }));
            Assert.That(Order(ItemSortMode.Newest), Is.EqualTo(new[] { "It_2H", "It_Bow", "It_1H_A", "It_Trophy_B", "It_Trophy_A" }));
            Assert.That(Order(ItemSortMode.Value).Skip(3), Is.EqualTo(new[] { "It_Trophy_A", "It_Trophy_B" }), "500 gold before 5");
        }

        [Test]
        public void SortMode_CyclesAndParses()
        {
            Assert.That(ItemSorting.Next(ItemSortMode.G3), Is.EqualTo(ItemSortMode.Name));
            Assert.That(ItemSorting.Next(ItemSortMode.Newest), Is.EqualTo(ItemSortMode.G3));
            Assert.That(ItemSorting.Parse("Value"), Is.EqualTo(ItemSortMode.Value));
            Assert.That(ItemSorting.Parse("Weight"), Is.EqualTo(ItemSortMode.G3));
            Assert.That(ItemSorting.Parse(null), Is.EqualTo(ItemSortMode.G3));
        }

        [Test]
        public void SortMode_Glyph_NoneInG3Order_ThemeArrowsOtherwise()
        {
            Assert.That(ItemSorting.GlyphPart(ItemSortMode.G3), Is.Null, "G3 order: the tabs look as in the original");
            var parts = new[] { ItemSortMode.Name, ItemSortMode.Value, ItemSortMode.Newest }.Select(ItemSorting.GlyphPart).ToList();
            Assert.That(parts, Is.All.Not.Null.And.All.Not.Empty);
            Assert.That(parts.Distinct().Count(), Is.EqualTo(3), "every mode has its own mark");
            Assert.That(PatchStrings.TryGet("Russian", "MYR_InvSort_Name", out _), Is.False, "no sort caption text any more [owner]");
            Assert.That(PatchStrings.TryGet("Russian", "MYR_InvDrop", out _), Is.False, "no drop hint text any more [owner]");
        }

        [Test]
        public void TemplateCompare_IsCStricmp()
        {
            Assert.That(ItemOrder.StrICmp("It_A_x", "it_Ab"), Is.LessThan(0), "'_' (0x5F) before 'b'");
            Assert.That(ItemOrder.StrICmp("IT_SWORD", "it_sword"), Is.Zero);
        }

        [Test]
        public void AcquisitionOrder_SurvivesSaveAndLoad()
        {
            using var f = MixedFixture();
            var session = Carrying(f, "It_Trophy_A", "It_Trophy_B");
            session.Items.Inventories.Of(ActorId.Player).Add("It_Trophy_A", 2);
            var loaded = f.Reload(session);
            var stacks = loaded.Items.Inventories.Of(ActorId.Player).Stacks;
            int a = stacks.Single(s => s.Item == "It_Trophy_A").Acquired, b = stacks.Single(s => s.Item == "It_Trophy_B").Acquired;
            Assert.That(a, Is.GreaterThan(b));
            loaded.Items.Inventories.Of(ActorId.Player).Add("It_Meat", 1);
            Assert.That(loaded.Items.Inventories.Of(ActorId.Player).Stacks.Single(s => s.Item == "It_Meat").Acquired, Is.GreaterThan(a));
        }

        [Test]
        public void LongDescription_MovesTheTableDown()
        {
            Assert.That(ItemPanelLayout.TableShift(60, 218), Is.Zero);
            Assert.That(ItemPanelLayout.TableShift(75, 218), Is.EqualTo(9));
            Assert.That(ItemPanelLayout.TableShift(400, 218), Is.EqualTo(218 - ItemPanelLayout.MinTableHeight));
        }
    }
}
