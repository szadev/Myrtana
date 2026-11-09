using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Story;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public class QuestProgressTests
    {
        static readonly Id<QuestDef> Q = new Id<QuestDef>("Gnar_EnterCastle");

        static QuestDef ReportQuest(QuestRewards rewards = null, params string[] reportTo) =>
            new QuestDef(Q, new TextId("QUEST_X"), null,
                         new QuestObjective(QuestObjectiveKind.Report, reportTo.Select(r => new QuestTarget(r, 1)).ToList(), default),
                         rewards);

        [Test]
        public void ReportingToEveryTarget_SucceedsTheQuest_EvenWhileOpen()
        {
            var f = new StoryFixture().Quest(ReportQuest(null, "Gnar", "Kan"));
            var _ = f.Dialog;
            Assert.That(f.Quests.Count(Q, "Gnar", 1), Is.True);
            Assert.That(f.Quests.StatusOf(Q), Is.EqualTo(QuestStatus.Open));
            Assert.That(f.Quests.Count(Q, "Somebody", 1), Is.False);
            f.Quests.Count(Q, "Kan", 1);
            Assert.That(f.Quests.StatusOf(Q), Is.EqualTo(QuestStatus.Success));
            Assert.That(f.Quests.Count(Q, "Kan", 1), Is.False, "counters stop when the quest is finished");
        }

        [Test]
        public void Success_GivesRewardsInOrder()
        {
            var rewards = new QuestRewards(new[]
            {
                new FameChange(FameKind.Enclave, "Faring", 4), new FameChange(FameKind.Political, "Orc", 10),
                new FameChange(FameKind.Political, "Reb", -5),
            }, new AttributeGain("STR", ModifierOp.AddToVal, 1), xp: 2500);
            var f = new StoryFixture().Quest(ReportQuest(rewards, "Gnar"));
            var _ = f.Dialog;
            f.Quests.TrySetStatus(Q, QuestStatus.Running);
            Assert.That(f.Hero.Xp, Is.EqualTo(0));

            f.Quests.Count(Q, "Gnar", 1);

            Assert.That(f.Hero.Xp, Is.EqualTo(2500));
            Assert.That(f.Hero.Level, Is.EqualTo(2));
            Assert.That(f.Hero.Stats.Value("STR"), Is.EqualTo(101));
            Assert.That(f.Fame.Of(FameKind.Enclave, "Faring"), Is.EqualTo(4));
            Assert.That(f.Fame.Of(FameKind.Political, "Orc"), Is.EqualTo(10));
            Assert.That(f.Fame.Of(FameKind.Political, "Reb"), Is.EqualTo(0), "fame is floored at 0");
            Assert.That(f.Quests.ProgressOf(Q).StartedAt, Is.EqualTo(0));
        }

        [Test]
        public void FailedOrClosedQuests_GiveNoRewards()
        {
            var f = new StoryFixture().Quest(ReportQuest(new QuestRewards(null, null, 100), "Gnar"));
            var _ = f.Dialog;
            f.Quests.TrySetStatus(Q, QuestStatus.Failed);
            Assert.That(f.Hero.Xp, Is.EqualTo(0));
        }

        [TestCase(QuestStatus.Open, QuestStatus.Obsolete, QuestStatus.Obsolete)]
        [TestCase(QuestStatus.Running, QuestStatus.Cancelled, QuestStatus.Failed)]
        public void CloseAndFail_DependOnTheCurrentStatus(QuestStatus from, QuestStatus closed, QuestStatus failed)
        {
            Assert.That(QuestTransitions.CloseTarget(from), Is.EqualTo(closed));
            Assert.That(QuestTransitions.FailTarget(from), Is.EqualTo(failed));
        }

        [Test]
        public void DialogCommands_DriveQuestsXpAndTheLog()
        {
            var f = new StoryFixture().Quest(ReportQuest(new QuestRewards(null, null, 500), "Gnar"));
            f.Entry(new DialogEntryDef(new Id<DialogEntryDef>("deliver"), StoryFixture.Npc, default, DialogEntryKind.News, 0, default,
                                       false, false, false, null, null, null,
                                       new DialogCommand[]
                                       {
                                           StoryFixture.PlayerSays("I_am_back"), StoryFixture.NpcSays("Well_done"),
                                           new GiveXp(100), new ReportToQuest(Q, ActorRef.Partner), new AddQuestLog(Q, new TextId("NOTE")),
                                       }, logQuest: Q));
            f.Dialog.Talk(StoryFixture.Npc);
            f.FinishLines();

            Assert.That(f.Quests.StatusOf(Q), Is.EqualTo(QuestStatus.Success));
            Assert.That(f.Hero.Xp, Is.EqualTo(600));
            var log = f.QuestLog.EntriesOf(Q).Select(e => e.Text.Value).ToList();
            Assert.That(log, Is.EqualTo(new[] { "I_am_back", "Well_done", "NOTE" }));
            Assert.That(f.QuestLog.EntriesOf(Q)[0].Speaker, Is.EqualTo(ActorId.Player));
            Assert.That(f.QuestLog.Quests.First(), Is.EqualTo(Q));
        }

        [Test]
        public void RefusalLines_AreNotLogged()
        {
            var f = new StoryFixture().Quest("Q");
            var q = new Id<QuestDef>("Q");
            f.Entry(new DialogEntryDef(new Id<DialogEntryDef>("ask"), StoryFixture.Npc, default, DialogEntryKind.Choice, 0, default,
                                       true, false, false, null, null,
                                       new PreCheck[] { new MinEnclaveFame(ActorRef.Partner, 75, new DialogCommand[] { StoryFixture.NpcSays("No") }) },
                                       new DialogCommand[] { StoryFixture.NpcSays("Yes") }, logQuest: q));
            f.Entry("other");
            f.Dialog.Talk(StoryFixture.Npc);
            f.Choose("ask");
            f.FinishLines();
            Assert.That(f.QuestLog.EntriesOf(q), Is.Empty);
        }

        [Test]
        public void ActorIs_UsesTheVitalsPort()
        {
            var f = new StoryFixture();
            var context = f.Conditions();
            Assert.That(new ActorIs(ActorRef.Partner, ActorCondition.Alive).Evaluate(context), Is.True);
            Assert.That(new ActorIs(new ActorRef("Unknown_Snapper"), ActorCondition.Dead).Evaluate(context), Is.False);
        }

        static QuestDef KillQuest(QuestObjectiveKind kind, string giver) =>
            new QuestDef(Q, new TextId("QUEST_X"), null,
                         new QuestObjective(kind, new[] { new QuestTarget("Wolf_Pack", 3) }, new ActorId(giver)), null);

        [TestCase(QuestObjectiveKind.Kill, QuestStatus.Open, true, QuestStatus.Obsolete)]
        [TestCase(QuestObjectiveKind.Defeat, QuestStatus.Running, true, QuestStatus.Cancelled)]
        [TestCase(QuestObjectiveKind.DriveAway, QuestStatus.Running, true, QuestStatus.Cancelled)]
        [TestCase(QuestObjectiveKind.Kill, QuestStatus.Running, false, QuestStatus.Running)]
        public void GiverDeath_CancelsKillQuests_OnlyUnderAlternativeBalancing(QuestObjectiveKind kind, QuestStatus from, bool ab, QuestStatus expected)
        {
            var f = new StoryFixture { AlternativeBalancing = ab }.Quest(KillQuest(kind, "Gnar"));
            var _ = f.Dialog;
            if (from != QuestStatus.Open) f.Quests.TrySetStatus(Q, from);

            f.Events.Publish(new ActorDefeated(new ActorId("Gnar"), true, false, 5, false, "Human"));
            Assert.That(f.Quests.StatusOf(Q), Is.EqualTo(from), "a knock-out of the giver changes nothing");
            f.Events.Publish(new ActorDefeated(new ActorId("Gnar"), true, true, 5, false, "Human"));

            Assert.That(f.Quests.StatusOf(Q), Is.EqualTo(expected));
        }

        [Test]
        public void SpellCastQuest_FailsWhenTheDestinationDies()
        {
            var f = new StoryFixture().Quest(KillQuest(QuestObjectiveKind.SpellCast, "Gnar"));
            var _ = f.Dialog;
            f.Quests.TrySetStatus(Q, QuestStatus.Running);
            f.Events.Publish(new ActorDefeated(new ActorId("Gnar"), true, true, 5, false, "Human"));
            Assert.That(f.Quests.StatusOf(Q), Is.EqualTo(QuestStatus.Failed));
        }
    }
}
