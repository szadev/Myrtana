using System;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Story
{
    /// <summary>
    /// Gives a quest's rewards when it ends successfully (Success or Won), in G3's order: political fame, the attribute,
    /// enclave fame, then XP (quests.json "rewardOrder").
    /// </summary>
    public sealed class QuestRewardGiver : IDisposable
    {
        readonly QuestService _quests;
        readonly ProgressionService _progression;
        readonly FameLedger _fame;
        readonly IDisposable _subscription;

        public QuestRewardGiver(QuestService quests, ProgressionService progression, FameLedger fame, IEventBus events)
        {
            _quests = quests;
            _progression = progression;
            _fame = fame;
            _subscription = events.Subscribe<QuestStatusChanged>(OnStatusChanged);
        }

        public void Dispose() => _subscription.Dispose();

        void OnStatusChanged(QuestStatusChanged e)
        {
            if (!e.To.IsSuccess() || !_quests.TryGetDefinition(e.Quest, out var quest)) return;
            var rewards = quest.Rewards;
            ApplyFame(rewards, FameKind.Political);
            if (rewards.Attribute != null) _progression.Apply(rewards.Attribute);
            ApplyFame(rewards, FameKind.Enclave);
            _progression.AwardXp(rewards.Xp);
        }

        void ApplyFame(QuestRewards rewards, FameKind kind)
        {
            foreach (var change in rewards.Fame)
                if (change.Kind == kind) _fame.Add(kind, change.Group, change.Amount);
        }
    }
}
