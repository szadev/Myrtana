using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Story;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-118: a teacher's lesson list (sub-choices of a Teach parent) stays open after a refused lesson and after a
    /// learned one, as G3 OnInfoScriptFinished (0x101cd5a0) re-shows the parent's children; Back returns to the topics.
    /// Lesson labels carry their price as it is now (G3 gCInfo_PS::GetDialogDescription).
    /// </summary>
    public class TeachListTests
    {
        static readonly ActorId Teacher = StoryFixture.Npc;

        static PreCheck[] Checks(Lesson lesson, int gold) => new PreCheck[]
        {
            new LessonCheck(lesson, gold, TeachResult.MissingRequirements, new DialogCommand[] { new ShowMessage(new TextId("GO_NoLearnRequirements")) }),
            new LessonCheck(lesson, gold, TeachResult.NotEnoughLearningPoints, new DialogCommand[] { new ShowMessage(new TextId("GO_NotEnoughLP")) }),
            new LessonCheck(lesson, gold, TeachResult.NotEnoughGold, new DialogCommand[] { new ShowMessage(new TextId("GO_NotEnoughGold")) }),
        };

        static StoryFixture Fixture()
        {
            var str = new Lesson("STR", 1, null);
            var hp = new Lesson("HP", 5, null);
            var perk = new Lesson(null, 0, "Perk_1H_2");
            var f = new StoryFixture()
                .Define(new SkillDef("Perk_1H_2", "It_Perk_1H_2", 5, new[] { new AttributeRequirement("STR", 150) }, null, "Skill0"))
                .Define(new TeacherDef(Teacher, new[] { "STR", "HP" }, new[] { "Perk_1H_2" }, false))
                .Entry("teach", repeatable: true, commands: StoryFixture.PlayerSays("L_teach"))
                .Entry("str1", repeatable: true, parent: "teach", order: 1, preChecks: Checks(str, 50), commands: new DialogCommand[] { new TeachLesson(str, 50) })
                .Entry("hp5", repeatable: true, parent: "teach", order: 2, preChecks: Checks(hp, 250), commands: new DialogCommand[] { new TeachLesson(hp, 250) })
                .Entry("perk", repeatable: true, parent: "teach", order: 3, preChecks: Checks(perk, 2500), commands: new DialogCommand[] { new TeachLesson(perk, 2500) })
                .Entry("back", DialogEntryKind.Back, parent: "teach", order: 99)
                .Entry("other", repeatable: true, order: 5)
                .Entry("end", DialogEntryKind.Exit, order: 99);
            var _ = f.Dialog;
            f.Progression.AddLearningPoints(20);
            f.Items.Inventories.Give(ActorId.Player, f.Items.GoldItem, 1000);
            return f;
        }

        static void OpenLessons(StoryFixture f)
        {
            f.Dialog.Talk(Teacher);
            f.FinishLines();
            f.Choose("teach");
            f.FinishLines();
            Assert.That(f.OfferedIds(), Is.EqualTo(new[] { "str1", "hp5", "perk", "back" }));
        }

        [Test]
        public void RefusedLesson_StaysInTheLessonList()
        {
            var f = Fixture();
            OpenLessons(f);
            f.Choose("perk"); // STR 100 < 150
            f.FinishLines();
            Assert.That(f.Dialog.Current.Phase, Is.EqualTo(ConversationPhase.AwaitingChoice));
            Assert.That(f.OfferedIds(), Is.EqualTo(new[] { "str1", "hp5", "perk", "back" }), "G3 re-shows the parent's children after an IS_Running_* refusal");
            Assert.That(f.Hero.Knows("Perk_1H_2"), Is.False);
        }

        [Test]
        public void LearnedLesson_StaysInTheLessonList_BackReturnsToTopics()
        {
            var f = Fixture();
            OpenLessons(f);
            int str = f.Hero.Stats.Value("STR");
            f.Choose("str1");
            f.FinishLines();
            Assert.That(f.Hero.Stats.Value("STR"), Is.EqualTo(str + 1));
            Assert.That(f.Hero.LearningPoints, Is.EqualTo(19));
            Assert.That(f.Items.Inventories.Of(ActorId.Player).Count(f.Items.GoldItem), Is.EqualTo(950));
            Assert.That(f.OfferedIds(), Is.EqualTo(new[] { "str1", "hp5", "perk", "back" }));
            f.Choose("back");
            f.FinishLines();
            Assert.That(f.OfferedIds(), Is.EqualTo(new[] { "teach", "other", "end" }));
        }

        [Test]
        public void RefusedTopLevelEntry_StillReturnsToTheTopics()
        {
            var f = new StoryFixture()
                .Entry("buy", repeatable: true, preChecks: new PreCheck[] { new MinGold(100000, new DialogCommand[] { new ShowMessage(new TextId("GO_NotEnoughGold")) }) })
                .Entry("other", repeatable: true, order: 5)
                .Entry("end", DialogEntryKind.Exit, order: 99);
            f.Dialog.Talk(Teacher);
            f.Choose("buy");
            f.FinishLines();
            Assert.That(f.OfferedIds(), Is.EqualTo(new[] { "buy", "other", "end" }));
            Assert.That(f.Dialog.Current.Options[0].Cost.Gold, Is.EqualTo(100000), "a gold cost outside teaching is shown too");
        }

        [Test]
        public void LessonLabels_NameTheCurrentPrice()
        {
            var f = Fixture();
            OpenLessons(f);
            var options = f.Dialog.Current.Options;
            var str1 = options.First(o => o.Entry.Name == "str1").Cost;
            var hp5 = options.First(o => o.Entry.Name == "hp5").Cost;
            var back = options.First(o => o.Entry.Name == "back").Cost;
            Assert.That((str1.IsLesson, str1.LearningPoints, str1.Gold, str1.AttributeGain), Is.EqualTo((true, 1, 50, 0)));
            Assert.That((hp5.LearningPoints, hp5.Gold, hp5.AttributeGain), Is.EqualTo((5, 250, 20)), "HP: 4 points per learning point");
            Assert.That(back.IsEmpty, Is.True);

            var words = new Dictionary<string, string> { ["GO_LP"] = "Оч. обучения", ["GO_GoldCost"] = "Золото" };
            string Text(TextId id) => words[id.Value];
            Assert.That(DialogOptionText.Format("Сила +1", str1, Text), Is.EqualTo("Сила +1 (1 Оч. обучения, 50 Золото)"));
            Assert.That(DialogOptionText.Format("Здоровье +5", hp5, Text), Is.EqualTo("Здоровье +20 (5 Оч. обучения, 250 Золото)"));
            Assert.That(DialogOptionText.Format("...назад", back, Text), Is.EqualTo("...назад"));
            Assert.That(DialogOptionText.Format("Купить", new OptionCost(false, 0, 100), Text), Is.EqualTo("Купить (100 Золото)"));
        }

        [Test]
        public void LessonLabels_FollowTheHeroAfterLearning()
        {
            // Alternative Balancing: STR/DEX/INT lessons cost double from 250 on; the list shown after the lesson says so
            var f = Fixture();
            f.Hero.Stats.Set("STR", new StatValue(249, 0));
            OpenLessons(f);
            Assert.That(f.Dialog.Current.Options.First(o => o.Entry.Name == "str1").Cost.LearningPoints, Is.EqualTo(1));
            f.Choose("str1");
            f.FinishLines();
            var cost = f.Dialog.Current.Options.First(o => o.Entry.Name == "str1").Cost;
            Assert.That((cost.LearningPoints, cost.Gold), Is.EqualTo((2, 100)));
        }
    }
}
