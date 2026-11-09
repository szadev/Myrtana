using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Story
{
    /// <summary>
    /// FollowNpc quests (quests.json type 7; Docs/party.md §4): when one becomes Running, its first delivery NPC starts
    /// guiding the hero to the quest's destination (G3 StartGuidePlayer) and any other Running FollowNpc quest is Lost (one
    /// guide at a time). When the quest leaves Running (success by its Delivery info, failure, cancel, loss), its guide
    /// stops (StopGuidePlayer). The guide is saved with the party; a Running quest whose guide is missing after loading
    /// gets it back (G3 SelectRoutineDestination looks the running guide quest up). A guide that turned away from the hero
    /// (<see cref="GuideTurnedAway"/>, G3 NotifyEnclave Script_Game @1005ef70: the player's party leader is reset, then
    /// the first quest of type FollowNpc that is Running gets status Lost) leaves its quest Lost; CP lets the player take
    /// some up again by talking (Wenzel NEWM0121 "Let's attack!": Lost -> Running).
    /// </summary>
    public sealed class FollowQuestGuides : IDisposable
    {
        readonly QuestService _quests;
        readonly PlayerParty _party;
        readonly Dictionary<Id<QuestDef>, QuestDef> _followQuests = new Dictionary<Id<QuestDef>, QuestDef>();
        readonly IDisposable _subscription, _turnedAway;

        public FollowQuestGuides(QuestService quests, PlayerParty party, IEnumerable<QuestDef> definitions, IEventBus events)
        {
            _quests = quests ?? throw new ArgumentNullException(nameof(quests));
            _party = party ?? throw new ArgumentNullException(nameof(party));
            foreach (var q in definitions)
                if (q.Objective.Kind == QuestObjectiveKind.FollowNpc && q.Objective.Targets.Count > 0) _followQuests[q.Id] = q;
            _subscription = events.Subscribe<QuestStatusChanged>(OnStatusChanged);
            _turnedAway = events.Subscribe<GuideTurnedAway>(OnGuideTurnedAway);
        }

        public void Dispose()
        {
            _subscription.Dispose();
            _turnedAway.Dispose();
        }

        /// <summary>G3 NotifyEnclave: the first Running FollowNpc quest (in quest order) is Lost.</summary>
        void OnGuideTurnedAway(GuideTurnedAway e)
        {
            var running = _followQuests.Values.FirstOrDefault(q => _quests.StatusOf(q.Id) == QuestStatus.Running);
            if (running != null) _quests.TrySetStatus(running.Id, QuestStatus.Lost);
        }

        /// <summary>The guide of a FollowNpc quest (its first delivery NPC).</summary>
        public static ActorId GuideOf(QuestDef quest) =>
            quest.Objective.Targets.Count > 0 ? new ActorId(quest.Objective.Targets[0].Target) : default;

        /// <summary>After loading: a Running FollowNpc quest without a guide gets its guide again.</summary>
        public void Resume()
        {
            if (_party.Guide != null) return;
            var running = _followQuests.Values.FirstOrDefault(q => _quests.StatusOf(q.Id) == QuestStatus.Running);
            if (running != null) Start(running);
        }

        void OnStatusChanged(QuestStatusChanged e)
        {
            if (!_followQuests.TryGetValue(e.Quest, out var quest)) return;
            if (e.To == QuestStatus.Running)
            {
                foreach (var other in _followQuests.Values.ToList())
                    if (other.Id != quest.Id && _quests.StatusOf(other.Id) == QuestStatus.Running) _quests.TrySetStatus(other.Id, QuestStatus.Lost);
                Start(quest);
            }
            else if (e.From == QuestStatus.Running && _party.Guide != null && _party.Guide.Quest == quest.Id.Name)
                _party.StopGuide();
        }

        void Start(QuestDef quest) => _party.StartGuide(GuideOf(quest), quest.Objective.Destination.Name, quest.Id.Name);
    }
}
