using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Story;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public class QuestServiceTests
    {
        static readonly Id<QuestDef> Q = new Id<QuestDef>("Q");

        [TestCase(QuestStatus.Open, QuestStatus.Running, true)]
        [TestCase(QuestStatus.Lost, QuestStatus.Running, true)]
        [TestCase(QuestStatus.Running, QuestStatus.Running, false)]
        [TestCase(QuestStatus.Open, QuestStatus.Success, true)]
        [TestCase(QuestStatus.Running, QuestStatus.Won, true)]
        [TestCase(QuestStatus.Success, QuestStatus.Failed, false)]
        [TestCase(QuestStatus.Open, QuestStatus.Obsolete, true)]
        [TestCase(QuestStatus.Running, QuestStatus.Obsolete, false)]
        [TestCase(QuestStatus.Running, QuestStatus.Cancelled, true)]
        [TestCase(QuestStatus.Open, QuestStatus.Cancelled, false)]
        [TestCase(QuestStatus.Running, QuestStatus.Lost, true)]
        [TestCase(QuestStatus.Running, QuestStatus.Open, false)]
        public void Transitions(QuestStatus from, QuestStatus to, bool allowed) =>
            Assert.That(QuestTransitions.IsAllowed(from, to), Is.EqualTo(allowed));

        [Test]
        public void SetStatus_PublishesTheChange_AndRefusesInvalidOnes()
        {
            var f = new StoryFixture().Quest("Q");
            var _ = f.Dialog;
            var changes = new List<QuestStatusChanged>();
            f.Events.Subscribe<QuestStatusChanged>(changes.Add);

            Assert.That(f.Quests.TrySetStatus(Q, QuestStatus.Running), Is.True);
            Assert.That(f.Quests.TrySetStatus(Q, QuestStatus.Running), Is.False);
            Assert.That(f.Quests.TrySetStatus(new Id<QuestDef>("Unknown"), QuestStatus.Running), Is.False);

            Assert.That(changes.Count, Is.EqualTo(1));
            Assert.That(changes[0].From, Is.EqualTo(QuestStatus.Open));
            Assert.That(changes[0].To, Is.EqualTo(QuestStatus.Running));
            // fix-186: Running -> Running is a quiet no-op; only the unknown quest warns
            Assert.That(f.Log.Messages.Count, Is.EqualTo(1));
        }
    }
}
