using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>The skills half of the character page (SkillSheet).</summary>
    public class SkillSheetTests
    {
        [Test]
        public void HiddenPerksWithoutAnItem_StayOffThePage()
        {
            // The owner's screenshot: "Perk_1H1H_SecretBonus" shown as a raw id with a placeholder icon in combat slot 3.
            using (var f = new SessionFixture()
                       .Add(RulesSchema.ItemFolder, ItemTests.Item("It_Perk_1H_1"))
                       .Add(RulesSchema.SkillFolder,
                            new SkillDef("Perk_1H_1", "It_Perk_1H_1", 5, null, null, "Skill0"),
                            new SkillDef("Perk_1H1H_SecretBonus", null, 5, null, null, "Skill0"),
                            new SkillDef("Perk_Lost", "It_Perk_Lost", 5, null, null, "Skill0")))
            {
                var sheet = SkillSheet.Of(f.NewGame());
                Assert.That(sheet.Of("Skill0").Select(s => s.Id), Is.EqualTo(new[] { "Perk_1H_1" }),
                            "a skill without a perk item (or with a missing one) is a hidden perk");
            }
        }

        static string[] Lines(System.Collections.Generic.IReadOnlyList<System.Collections.Generic.IReadOnlyList<LabelledValue>> sections) =>
            sections.Select(section => string.Join(" | ", section.Select(r => r.Label + "=" + r.Value))).ToArray();

        [Test]
        public void RequirementRows_AttributesThenRequiredSkill_InTwoSections()
        {
            // Owner (fix-145): "Тяжелое оружие III" listed STR 250 and the learning points but not Heavy Weapons II.
            var rows = SkillSheet.RequirementRows(new[] { new AttributeRequirement("STR", 250) }, t => "Req_" + t,
                                                  "Нужен навык", "Тяжелое оружие II", "Очки обучения", 5);
            Assert.That(Lines(rows), Is.EqualTo(new[] { "Req_STR=250", "Нужен навык=Тяжелое оружие II", "Очки обучения=5" }),
                        "G3 gCHUDTableSkillStats: attribute section, then HUD_Req_Skill in its own section; [port] LP cost last");
        }

        [Test]
        public void RequirementRows_AttributesFromTheLastSlot()
        {
            // Perk_1H1H_1: ReqAttrib1 STR 150, ReqAttrib2 DEX 150. G3 emits ReqAttrib4 … ReqAttrib1.
            var rows = SkillSheet.RequirementRows(new[] { new AttributeRequirement("STR", 150), new AttributeRequirement("DEX", 150) },
                                                  t => t, "Skill", "Perk_1H_2");
            Assert.That(Lines(rows), Is.EqualTo(new[] { "DEX=150 | STR=150", "Skill=Perk_1H_2" }));
        }

        [Test]
        public void RequirementRows_LeaveEmptySectionsOut()
        {
            Assert.That(SkillSheet.RequirementRows(null, t => t, "Skill", null), Is.Empty);
            Assert.That(Lines(SkillSheet.RequirementRows(null, t => t, "Skill", "Perk_Axe_1")), Is.EqualTo(new[] { "Skill=Perk_Axe_1" }));
            Assert.That(Lines(SkillSheet.RequirementRows(new[] { new AttributeRequirement("STR", 0), new AttributeRequirement("", 5),
                                                                 new AttributeRequirement("INT", 50) }, t => t, "Skill", "")),
                        Is.EqualTo(new[] { "INT=50" }), "G3 skips slots with value <= 0 or no tag");
        }

        [Test]
        public void Sheet_NamesTheRequiredSkillByItsPerkItem()
        {
            using (var f = new SessionFixture()
                       .Add(RulesSchema.ItemFolder, ItemTests.Item("It_Perk_Axe_2"), ItemTests.Item("It_Perk_Axe_3"))
                       .Add(RulesSchema.SkillFolder,
                            new SkillDef("Perk_Axe_2", "It_Perk_Axe_2", 5, new[] { new AttributeRequirement("STR", 150) }, null, "Skill0"),
                            new SkillDef("Perk_Axe_3", "It_Perk_Axe_3", 5, new[] { new AttributeRequirement("STR", 250) }, "Perk_Axe_2", "Skill0")))
            {
                var sheet = SkillSheet.Of(f.NewGame());
                var axe3 = sheet.Of("Skill0").Single(s => s.Id == "Perk_Axe_3");
                Assert.That(axe3.Requirements.Count, Is.EqualTo(3));
                Assert.That(axe3.Requirements[1].Single().Label, Is.EqualTo("[HUD_Req_Skill]"));
                Assert.That(axe3.Requirements[1].Single().Value, Is.EqualTo("[FO_It_Perk_Axe_2]"), "FO_It_<ReqSkill1>");
                Assert.That(axe3.Requirements[2].Single().Label, Is.EqualTo("[GO_LearningPoints]"), "[port] learning-point row last");
                Assert.That(axe3.Requirements[2].Single().Value, Is.EqualTo("5"));
                Assert.That(sheet.Of("Skill0").Single(s => s.Id == "Perk_Axe_2").Requirements.Count, Is.EqualTo(2), "attributes + [port] LP");
            }
        }
    }
}
