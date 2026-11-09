using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Story;
using NUnit.Framework;
using static Myrtana.Tests.StoryFixture;

namespace Myrtana.Tests
{
    /// <summary>fix-206: the ancestor tombs' invisible rune pedestals speak their News entry when the hero comes up.</summary>
    public class SpeakerApproachTests
    {
        static InteractiveObjectDef Def(string name, InteractiveKind kind, string visual) =>
            new InteractiveObjectDef("id-" + name, name, kind, "Shrine", visual, new float[] { 324.35f, 204.06f, 1278.73f },
                                     null, null, 1f, default, null, null, null, null);

        [Test]
        public void OnlyInvisibleTalkingObjectsAnnounce()
        {
            Assert.That(SpeakerApproach.Announces(Def("Living_Ancestor_06_Tester", InteractiveKind.Speaker, null)), Is.True);
            Assert.That(SpeakerApproach.Announces(Def("Innos_Tester", InteractiveKind.Speaker, "G3_Object_Interact_Innos_Statue_01.xlmsh")),
                        Is.False, "a statue is prayed at");
            Assert.That(SpeakerApproach.Announces(Def("Chest", InteractiveKind.Chest, null)), Is.False);
            Assert.That(SpeakerApproach.Announces(null), Is.False);
        }

        [Test]
        public void Reach_IsFlatWithAHeightLimit()
        {
            Assert.That(SpeakerApproach.InReach(2.4f, 1.7f), Is.True, "hero on the slab beside the pedestal");
            Assert.That(SpeakerApproach.InReach(2.6f, 0f), Is.False);
            Assert.That(SpeakerApproach.InReach(1f, 3f), Is.False, "floor above");
            Assert.That(SpeakerApproach.InReach(1f, -3f), Is.False, "floor below");
        }

        [Test]
        public void SpeaksOncePerVisit_AgainAfterLeaving()
        {
            var a = new SpeakerApproach();
            Assert.That(a.ShouldAnnounce("p", 6f, 0f, true), Is.False, "far");
            Assert.That(a.ShouldAnnounce("p", 2f, 0f, true), Is.True, "comes up");
            Assert.That(a.ShouldAnnounce("p", 1.5f, 0f, true), Is.False, "stays");
            Assert.That(a.ShouldAnnounce("p", 3.5f, 0f, true), Is.False, "steps back inside the margin");
            Assert.That(a.ShouldAnnounce("p", 2f, 0f, true), Is.False, "not rearmed yet");
            Assert.That(a.ShouldAnnounce("p", 4.5f, 0f, true), Is.False, "leaves");
            Assert.That(a.ShouldAnnounce("p", 2f, 0f, true), Is.True, "next visit");
        }

        [Test]
        public void ABlockedTry_DoesNotUseUpTheVisit()
        {
            var a = new SpeakerApproach();
            Assert.That(a.ShouldAnnounce("p", 2f, 0f, false), Is.False, "weapon drawn / no News yet");
            Assert.That(a.ShouldAnnounce("p", 2f, 0f, true), Is.True, "sheathed");
            Assert.That(a.ShouldAnnounce("q", 2f, 0f, true), Is.True, "another pedestal is its own visit");
        }

        [Test]
        public void HasNews_OnlyAvailableNewsOfTheObjectItself()
        {
            var quest = new Id<QuestDef>("Living_Ancestor_06_BringStone");
            var open = new QuestStatusIs(quest, new[] { QuestStatus.Open });
            var f = new StoryFixture().Quest("Living_Ancestor_06_BringStone")
                .Entry("discover", DialogEntryKind.News, condition: open,
                       commands: new DialogCommand[] { PlayerSays("insert"), new StartQuest(quest) })
                .Entry("deliver", order: 2, condition: new QuestStatusIs(quest, new[] { QuestStatus.Running }))
                .Entry("heroNews", DialogEntryKind.News, owner: ActorId.Player);
            Assert.That(f.Dialog.HasNews(Npc), Is.True);
            f.Dialog.Talk(Npc);
            Assert.That(f.Dialog.HasNews(Npc), Is.False, "not while talking");
            f.FinishLines();
            f.Dialog.Abort();
            Assert.That(f.Quests.StatusOf(quest), Is.EqualTo(QuestStatus.Running));
            Assert.That(f.Dialog.HasNews(Npc), Is.False, "played; the hero's own News does not count");
        }
    }
}
