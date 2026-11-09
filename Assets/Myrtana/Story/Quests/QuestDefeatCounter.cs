using System;
using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Story
{
    /// <summary>
    /// What a defeated NPC means for quests (quests.json "types"): a kill counts for Kill and DriveAway targets, a kill or
    /// knock-out for Defeat targets; the death of a key NPC fails the quest (Obsolete while Open): the destination of a
    /// HasItems quest, a Report target not yet reported to, the first NPC or the destination of a BringNpc quest, the first
    /// NPC of a FollowNpc quest, the destination of a SpellCast quest. Under Alternative Balancing the death of the
    /// destination (quest giver) of a Kill/Defeat/DriveAway quest cancels it (Open → Obsolete, Running → Cancelled):
    /// CP 1.75 gCQuest_PS::OnNPCKilled 0x10159110 (notes/cp175/D_items_progression_dialog.md:163).
    /// </summary>
    public sealed class QuestDefeatCounter : IDisposable
    {
        enum Effect { CountKill, CountDefeat, FailOnDeath, FailOnDeathUnlessCounted, CancelOnDeath }

        readonly QuestService _quests;
        readonly Dictionary<ActorId, List<(QuestDef Quest, Effect Effect)>> _byActor = new Dictionary<ActorId, List<(QuestDef, Effect)>>();
        readonly IDisposable _subscription;
        readonly bool _alternativeBalancing;

        public QuestDefeatCounter(QuestService quests, IEnumerable<QuestDef> definitions, IEventBus events, bool alternativeBalancing = false)
        {
            _quests = quests;
            _alternativeBalancing = alternativeBalancing;
            foreach (var q in definitions) Index(q);
            _subscription = events.Subscribe<ActorDefeated>(OnDefeated);
        }

        public void Dispose() => _subscription.Dispose();

        /// <summary>
        /// fix-161 [owner]: asked before a killed Report target fails its quest (quest, target, the defeat); true = the quest
        /// was settled another way and is not failed (<see cref="ForcedReleases.TryRelease"/>). Null = G3.
        /// </summary>
        public Func<QuestDef, ActorId, ActorDefeated, bool> ReleasedByForce { get; set; }

        void Index(QuestDef quest)
        {
            var objective = quest.Objective;
            var targets = objective.Targets;
            switch (objective.Kind)
            {
                case QuestObjectiveKind.Kill:
                case QuestObjectiveKind.DriveAway:
                    foreach (var t in targets) Add(t.Target, quest, Effect.CountKill);
                    AddGiverCancel(quest);
                    break;
                case QuestObjectiveKind.Defeat:
                    foreach (var t in targets) Add(t.Target, quest, Effect.CountDefeat);
                    AddGiverCancel(quest);
                    break;
                case QuestObjectiveKind.SpellCast:
                    if (!objective.Destination.IsPlayer) Add(objective.Destination.Name, quest, Effect.FailOnDeath);
                    break;
                case QuestObjectiveKind.HasItems:
                    if (!objective.Destination.IsPlayer) Add(objective.Destination.Name, quest, Effect.FailOnDeath); // "find" quests: the hero keeps them
                    break;
                case QuestObjectiveKind.Report:
                    foreach (var t in targets) Add(t.Target, quest, Effect.FailOnDeathUnlessCounted);
                    break;
                case QuestObjectiveKind.BringNpc:
                    if (targets.Count > 0) Add(targets[0].Target, quest, Effect.FailOnDeath);
                    Add(objective.Destination.Name, quest, Effect.FailOnDeath);
                    break;
                case QuestObjectiveKind.FollowNpc:
                    if (targets.Count > 0) Add(targets[0].Target, quest, Effect.FailOnDeath);
                    break;
            }
        }

        void AddGiverCancel(QuestDef quest)
        {
            if (_alternativeBalancing && !quest.Objective.Destination.IsPlayer) Add(quest.Objective.Destination.Name, quest, Effect.CancelOnDeath);
        }

        void Add(string actor, QuestDef quest, Effect effect)
        {
            if (string.IsNullOrEmpty(actor)) return;
            var id = new ActorId(actor);
            if (!_byActor.TryGetValue(id, out var list)) _byActor[id] = list = new List<(QuestDef, Effect)>();
            list.Add((quest, effect));
        }

        void OnDefeated(ActorDefeated e)
        {
            // G3 counts by entity name: a spawn that shares its name ("Orc_Raider_Elite_02_Fireclan#1a2b3c4d") counts for it.
            var victim = e.Victim;
            if (!_byActor.TryGetValue(victim, out var entries))
            {
                int hash = victim.Name?.IndexOf('#') ?? -1;
                if (hash <= 0) return;
                victim = new ActorId(victim.Name.Substring(0, hash));
                if (!_byActor.TryGetValue(victim, out entries)) return;
            }
            foreach (var (quest, effect) in entries)
            {
                switch (effect)
                {
                    case Effect.CountKill when e.Killed:
                    case Effect.CountDefeat:
                        _quests.Count(quest.Id, victim.Name, 1);
                        break;
                    case Effect.FailOnDeath when e.Killed:
                        Fail(quest);
                        break;
                    case Effect.FailOnDeathUnlessCounted when e.Killed && !Reached(quest, victim.Name):
                        if (ReleasedByForce?.Invoke(quest, victim, e) == true) break; // fix-161 [owner] port option
                        Fail(quest);
                        break;
                    case Effect.CancelOnDeath when e.Killed:
                        Cancel(quest);
                        break;
                }
            }
        }

        bool Reached(QuestDef quest, string target)
        {
            int index = quest.Objective.IndexOf(target);
            var progress = _quests.ProgressOf(quest.Id);
            return index >= 0 && progress != null && progress.Counters[index] >= Math.Max(1, quest.Objective.Targets[index].Amount);
        }

        void Cancel(QuestDef quest)
        {
            var status = _quests.StatusOf(quest.Id);
            if (status == QuestStatus.Open || status == QuestStatus.Running)
                _quests.TrySetStatus(quest.Id, QuestTransitions.CloseTarget(status));
        }

        void Fail(QuestDef quest)
        {
            var status = _quests.StatusOf(quest.Id);
            if (status == QuestStatus.Open || status == QuestStatus.Running)
                _quests.TrySetStatus(quest.Id, QuestTransitions.FailTarget(status));
        }
    }
}
