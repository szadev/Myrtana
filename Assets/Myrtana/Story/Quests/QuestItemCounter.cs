using System;
using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Story
{
    /// <summary>
    /// Counts item deliveries for quests (quests.json "types"): items given to a HasItems quest's destination count,
    /// items taken back from it subtract; for Plunder quests items taken from the destination count. A destination may be a
    /// chest (G3 entity name, e.g. Plunder "Esiel_Chest"): <c>entityOf</c> maps its container inventory to that name.
    /// </summary>
    public sealed class QuestItemCounter : IDisposable
    {
        readonly QuestService _quests;
        readonly Dictionary<ActorId, List<QuestDef>> _byDestination = new Dictionary<ActorId, List<QuestDef>>();
        readonly IDisposable _subscription;
        readonly Func<ActorId, ActorId> _entityOf;

        /// <param name="entityOf">The quest entity an inventory belongs to (a chest's container → the chest's name); null = itself.</param>
        public QuestItemCounter(QuestService quests, IEnumerable<QuestDef> definitions, IEventBus events, Func<ActorId, ActorId> entityOf = null)
        {
            _quests = quests;
            _entityOf = entityOf ?? (a => a);
            foreach (var q in definitions)
            {
                var kind = q.Objective.Kind;
                if ((kind != QuestObjectiveKind.HasItems && kind != QuestObjectiveKind.Plunder) || q.Objective.Destination.IsEmpty) continue;
                if (!_byDestination.TryGetValue(q.Objective.Destination, out var list)) _byDestination[q.Objective.Destination] = list = new List<QuestDef>();
                list.Add(q);
            }
            _subscription = events.Subscribe<ItemsTransferred>(OnTransferred);
        }

        public void Dispose() => _subscription.Dispose();

        void OnTransferred(ItemsTransferred e)
        {
            if (!e.To.IsEmpty && _byDestination.TryGetValue(_entityOf(e.To), out var receiving))
                foreach (var q in receiving) _quests.Count(q.Id, e.Item, q.Objective.Kind == QuestObjectiveKind.HasItems ? e.Amount : -e.Amount);
            if (!e.From.IsEmpty && _byDestination.TryGetValue(_entityOf(e.From), out var giving))
                foreach (var q in giving) _quests.Count(q.Id, e.Item, q.Objective.Kind == QuestObjectiveKind.Plunder ? e.Amount : -e.Amount);
        }
    }
}
