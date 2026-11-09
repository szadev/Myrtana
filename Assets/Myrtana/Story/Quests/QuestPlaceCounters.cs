using System;
using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Story
{
    /// <summary>
    /// Whether an actor stands in a named area of the world (an AI/nav zone such as "Faring_Arena_Village" or
    /// "Xardas_Tower"). Implemented by the session over the imported zones and the actors' poses; tests pass fakes.
    /// </summary>
    public interface IAreaLocator
    {
        /// <summary>False when the actor's position or the area is unknown (no decision).</summary>
        bool TryIsInside(ActorId actor, string area, out bool inside);
    }

    /// <summary>
    /// EnterArea quests (quests.json types: "first delivery entity enters the destination area → +1"). G3 notifies the
    /// quest when the entity touches the area; here <see cref="Check"/> polls (the session calls it about once a second)
    /// while the quest's counters are active (Open, Running, Lost).
    /// </summary>
    public sealed class QuestAreaCounter
    {
        readonly QuestService _quests;
        readonly IAreaLocator _areas;
        readonly List<(QuestDef Quest, ActorId Entity, string Target, string Area)> _watched = new List<(QuestDef, ActorId, string, string)>();

        public QuestAreaCounter(QuestService quests, IEnumerable<QuestDef> definitions, IAreaLocator areas)
        {
            _quests = quests;
            _areas = areas;
            foreach (var q in definitions)
            {
                if (q.Objective.Kind != QuestObjectiveKind.EnterArea || q.Objective.Targets.Count == 0) continue;
                string area = q.Objective.Destination.Name;
                string target = q.Objective.Targets[0].Target;
                if (string.IsNullOrEmpty(area) || string.IsNullOrEmpty(target)) continue;
                _watched.Add((q, new ActorId(target), target, area));
            }
        }

        public int Count => _watched.Count;

        /// <summary>Counts every watched entity that is inside its quest's area now.</summary>
        public void Check()
        {
            if (_areas == null) return;
            foreach (var (quest, entity, target, area) in _watched)
            {
                if (!QuestTransitions.CountersActive(_quests.StatusOf(quest.Id)) || Reached(quest)) continue;
                if (_areas.TryIsInside(entity, area, out bool inside) && inside) _quests.Count(quest.Id, target, 1);
            }
        }

        bool Reached(QuestDef quest)
        {
            var p = _quests.ProgressOf(quest.Id);
            if (p == null) return false;
            for (int i = 0; i < quest.Objective.Targets.Count; i++)
                if (p.Counters[i] < Math.Max(1, quest.Objective.Targets[i].Amount)) return false;
            return true;
        }
    }

    /// <summary>
    /// Arena quests (quests.json types: "player defeated or leaving the arena → Lost; opponent defeated or leaving → +1").
    /// The fight is "on" while the opponent fights the player for the arena (<c>fightOn</c>, from the NPC combat); a
    /// Reactivator entry later restarts a Lost quest (Lost → Running).
    /// </summary>
    public sealed class QuestArenaCounter : IDisposable
    {
        readonly QuestService _quests;
        readonly IAreaLocator _areas;
        readonly Func<ActorId, bool> _fightOn;
        readonly Action<ActorId> _endFight;
        readonly IEventBus _events;
        readonly List<(QuestDef Quest, ActorId Opponent, string Target, string Arena)> _fights = new List<(QuestDef, ActorId, string, string)>();
        readonly List<IDisposable> _subscriptions = new List<IDisposable>();
        /// <summary>The indices into <see cref="_fights"/> by opponent, in list order (<see cref="ArenaOf"/> runs per NPC per frame, perf-9).</summary>
        readonly Dictionary<ActorId, List<int>> _fightsOf = new Dictionary<ActorId, List<int>>();

        /// <summary>CP 1.75: the player leaving the ring mid-fight sees this message once (and the fight ends).</summary>
        public static readonly TextId ArenaWarning = new TextId("GO_ArenaWarning");

        /// <param name="fightOn">True while the opponent is in its arena fight against the player.</param>
        /// <param name="endFight">Ends the opponent's arena attack (CP ZS_Attack_Arena_End) when the player left the ring.</param>
        public QuestArenaCounter(QuestService quests, IEnumerable<QuestDef> definitions, IEventBus events, IAreaLocator areas,
                                 Func<ActorId, bool> fightOn, Action<ActorId> endFight = null)
        {
            _quests = quests;
            _areas = areas;
            _events = events;
            _fightOn = fightOn ?? (_ => false);
            _endFight = endFight;
            foreach (var q in definitions)
            {
                if (q.Objective.Kind != QuestObjectiveKind.Arena && q.Objective.Kind != QuestObjectiveKind.Sparring) continue;
                foreach (var t in q.Objective.Targets)
                    if (!string.IsNullOrEmpty(t.Target)) _fights.Add((q, new ActorId(t.Target), t.Target, q.Objective.Destination.Name));
            }
            for (int i = 0; i < _fights.Count; i++)
            {
                if (!_fightsOf.TryGetValue(_fights[i].Opponent, out var list)) _fightsOf[_fights[i].Opponent] = list = new List<int>();
                list.Add(i);
            }
            _subscriptions.Add(events.Subscribe<ActorDefeated>(OnDefeated));
            _subscriptions.Add(events.Subscribe<PlayerDefeated>(_ => OnPlayerDefeated()));
        }

        public int Count => _fights.Count;

        public void Dispose()
        {
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
        }

        void OnDefeated(ActorDefeated e)
        {
            foreach (var (quest, opponent, target, _) in _fights)
                if (opponent == e.Victim && Running(quest)) _quests.Count(quest.Id, target, 1);
        }

        void OnPlayerDefeated()
        {
            foreach (var (quest, opponent, _, _) in _fights)
                if (Running(quest) && _fightOn(opponent) && _quests.TrySetStatus(quest.Id, QuestStatus.Lost))
                    LastLoss = $"{quest.Id.Name}: the player was defeated by/while fighting {opponent}";
        }

        /// <summary>Why the last arena fight was lost (diagnostics, fix-46: r28 ishtar_01 ended Lost with no reason in the log); null = none yet.</summary>
        public string LastLoss { get; private set; }

        /// <summary>
        /// Leaving the arena during the fight: the player loses, an opponent counts as beaten. G3 reacts to the zone's
        /// leave notification, so only someone seen inside during this fight can leave it: an opponent that starts the
        /// fight from its waiting spot beside the ring (Goran in Faring) or a player who never stepped in decides nothing.
        /// The player must wait in the ring for an opponent that starts further away (Spike's stool).
        /// </summary>
        public void Check()
        {
            if (_areas == null) return;
            foreach (var (quest, opponent, target, arena) in _fights)
            {
                if (string.IsNullOrEmpty(arena) || !Running(quest) || !_fightOn(opponent))
                {
                    _inside.Remove((quest.Id, opponent));
                    _inside.Remove((quest.Id, ActorId.Player));
                    continue;
                }
                if (Left(quest.Id, ActorId.Player, arena))
                {
                    // G3 OnLeave: Lost. CP 1.75 also shows GO_ArenaWarning and the opponent stops attacking, so a hero
                    // who walks out to meet a far opponent (Spike gets up from his stool 7 m beside the ring) sees why.
                    if (_quests.TrySetStatus(quest.Id, QuestStatus.Lost)) LastLoss = $"{quest.Id.Name}: the player left the arena {arena} while fighting {opponent}";
                    _events.Publish(new GameMessage(ArenaWarning));
                    _endFight?.Invoke(opponent);
                    _inside.Remove((quest.Id, opponent));
                }
                else if (Left(quest.Id, opponent, arena)) _quests.Count(quest.Id, target, 1);
            }
        }

        /// <summary>
        /// The arena (the quest's destination entity) whose fight <paramref name="opponent"/> is in now: a running Arena or
        /// Sparring quest lists it (G3: the arena status is Running iff the quest is; its delivery NPCs are sent there).
        /// Null = none.
        /// </summary>
        public string ArenaOf(ActorId opponent)
        {
            if (!_fightsOf.TryGetValue(opponent, out var indices)) return null;
            foreach (int i in indices)
            {
                var (quest, _, _, arena) = _fights[i];
                if (!string.IsNullOrEmpty(arena) && Running(quest)) return arena;
            }
            return null;
        }

        readonly HashSet<(Id<QuestDef>, ActorId)> _inside = new HashSet<(Id<QuestDef>, ActorId)>();

        /// <summary>True once when <paramref name="actor"/>, seen inside the arena during the fight, is outside now.</summary>
        bool Left(Id<QuestDef> quest, ActorId actor, string arena)
        {
            if (!_areas.TryIsInside(actor, arena, out bool inside)) return false;
            if (!inside) return _inside.Remove((quest, actor));
            _inside.Add((quest, actor));
            return false;
        }

        bool Running(QuestDef quest) => _quests.StatusOf(quest.Id) == QuestStatus.Running;
    }

    /// <summary>FreeEnclave quests (quests.json types: "listed enclave liberated → +1"), on <see cref="EnclaveLiberated"/>.</summary>
    public sealed class QuestEnclaveCounter : IDisposable
    {
        readonly QuestService _quests;
        readonly Dictionary<string, List<QuestDef>> _byEnclave = new Dictionary<string, List<QuestDef>>(StringComparer.OrdinalIgnoreCase);
        readonly IDisposable _subscription;

        public QuestEnclaveCounter(QuestService quests, IEnumerable<QuestDef> definitions, IEventBus events)
        {
            _quests = quests;
            foreach (var q in definitions)
            {
                if (q.Objective.Kind != QuestObjectiveKind.FreeEnclave) continue;
                foreach (var t in q.Objective.Targets)
                {
                    if (string.IsNullOrEmpty(t.Target)) continue;
                    if (!_byEnclave.TryGetValue(t.Target, out var list)) _byEnclave[t.Target] = list = new List<QuestDef>();
                    list.Add(q);
                }
            }
            _subscription = events.Subscribe<EnclaveLiberated>(OnLiberated);
        }

        public void Dispose() => _subscription.Dispose();

        void OnLiberated(EnclaveLiberated e)
        {
            if (e.Enclave == null || !_byEnclave.TryGetValue(e.Enclave, out var quests)) return;
            foreach (var q in quests)
            {
                int i = q.Objective.IndexOf(e.Enclave);
                if (i < 0) // listed with another spelling (case)
                    for (int k = 0; k < q.Objective.Targets.Count && i < 0; k++)
                        if (string.Equals(q.Objective.Targets[k].Target, e.Enclave, StringComparison.OrdinalIgnoreCase)) i = k;
                if (i >= 0) _quests.Count(q.Id, q.Objective.Targets[i].Target, 1);
            }
        }
    }
}
