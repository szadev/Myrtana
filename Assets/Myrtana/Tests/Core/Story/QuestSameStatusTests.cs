using System.Linq;
using Myrtana.Core;
using Myrtana.Story;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-186 (regress-1021b: 24x 'PC_Hero_FIndTeleportToKing: Success → Success is not allowed'): asking a quest for the
    /// state it is already in changes nothing, publishes nothing and warns about nothing.
    /// </summary>
    public class QuestSameStatusTests
    {
        [Test]
        public void SameStatus_IsAQuietNoOp()
        {
            var f = new StoryFixture().Quest("FindStone");
            _ = f.Dialog;
            var q = new Id<QuestDef>("FindStone");
            Assert.That(f.Quests.TrySetStatus(q, QuestStatus.Success), Is.True);
            int changes = 0;
            f.Events.Subscribe<QuestStatusChanged>(_ => changes++);
            Assert.That(f.Quests.TrySetStatus(q, QuestStatus.Success), Is.False);
            Assert.That(f.Quests.StatusOf(q), Is.EqualTo(QuestStatus.Success));
            Assert.That(changes, Is.Zero);
            Assert.That(f.Log.Messages.Where(m => m.StartsWith("WARNING")), Is.Empty);
            Assert.That(f.Quests.TrySetStatus(q, QuestStatus.Running), Is.False, "a real refusal still warns");
            Assert.That(f.Log.Messages.Count(m => m.StartsWith("WARNING")), Is.EqualTo(1));
        }
    }
}
