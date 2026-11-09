using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Story;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-84 item 2: a refused teach entry names what is missing, as G3 gCInventory_PS::CanLearnSkill does
    /// (HUD_MissingSkill / HUD_MissingAttrib before GO_NoLearnRequirements).
    /// </summary>
    public class TeachShortfallTests
    {
        static readonly ActorId Cobryn = StoryFixture.Npc;

        static StoryFixture Fixture(out Lesson perk)
        {
            perk = new Lesson(null, 0, "Perk_1H_3");
            var lesson = perk;
            return new StoryFixture()
                .Define(new SkillDef("Perk_1H_2", "It_Perk_1H_2", 5, new[] { new AttributeRequirement("STR", 150) }, "Perk_1H_1", "Skill0"))
                .Define(new SkillDef("Perk_1H_3", "It_Perk_1H_3", 5, new[] { new AttributeRequirement("STR", 200), new AttributeRequirement("DEX", 90) },
                                     "Perk_1H_2", "Skill0"))
                .Define(new TeacherDef(Cobryn, new[] { "STR" }, new[] { "Perk_1H_2", "Perk_1H_3" }, false))
                .Entry("teach", repeatable: true, preChecks: new PreCheck[]
                {
                    new LessonCheck(lesson, 0, TeachResult.MissingRequirements, new DialogCommand[]
                        { new ReportLearnShortfalls(lesson), new ShowMessage(new TextId("GO_NoLearnRequirements")) }),
                }, commands: new DialogCommand[] { new TeachLesson(lesson, 0) })
                .Entry("end", DialogEntryKind.Choice, order: 99);
        }

        [Test]
        public void Shortfalls_ListRequiredSkillAndMissingAttributePoints()
        {
            var f = Fixture(out var perk);
            var _ = f.Dialog;
            var list = f.Items.Teaching.Shortfalls(perk);
            Assert.That(list.Select(s => s.ToString()), Is.EqualTo(new[] { "Skill:Perk_1H_2", "STR-100" }),
                        "hero starts with STR 100, DEX 100: Perk_1H_2 and 100 strength are missing, DEX is enough");
            Assert.That(f.Items.Teaching.Check(perk, 0), Is.EqualTo(TeachResult.MissingRequirements));
            Assert.That(f.Items.Teaching.Shortfalls(new Lesson("STR", 1, null)), Is.Empty, "attribute lessons have no requirements");
        }

        [Test]
        public void RefusedTeachEntry_ReportsEachShortfallThenTheGeneralMessage()
        {
            var f = Fixture(out _);
            var log = new List<string>();
            f.Events.Subscribe<LearnRequirementsMissing>(e => log.AddRange(e.Shortfalls.Select(s => s.ToString())));
            f.Events.Subscribe<GameMessage>(e => log.Add(e.Text.Value));
            f.Dialog.Talk(Cobryn);
            f.FinishLines();
            f.Choose("teach");
            f.FinishLines();
            Assert.That(log, Is.EqualTo(new[] { "Skill:Perk_1H_2", "STR-100", "GO_NoLearnRequirements" }));
            Assert.That(f.Hero.Knows("Perk_1H_3"), Is.False);
        }
    }
}
