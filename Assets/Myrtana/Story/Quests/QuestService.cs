using System;
using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Story
{
    /// <summary>Read side of the quest state, used by conditions.</summary>
    public interface IQuestLog
    {
        QuestStatus StatusOf(Id<QuestDef> quest);
    }

    /// <summary>Runtime state of one quest that has left its initial state.</summary>
    public sealed class QuestProgress
    {
        internal QuestProgress(int targets) => Counters = new int[targets];

        public QuestStatus Status { get; internal set; }
        /// <summary>Game seconds when the quest was started (set Running); 0 if never.</summary>
        public double StartedAt { get; internal set; }
        /// <summary>Progress per <see cref="QuestObjective.Targets"/> entry.</summary>
        public int[] Counters { get; internal set; }
    }

    /// <summary>
    /// Quest status machine and objective counters. Every quest starts Open; changes follow
    /// <see cref="QuestTransitions"/> and are published as <see cref="QuestStatusChanged"/>. Refused changes are logged,
    /// never thrown, because content may try them. Counters run while a quest is Open, Running or Lost; when every
    /// target is reached the quest succeeds, also while still Open (the player did the deed before accepting it).
    /// </summary>
    public sealed class QuestService : IQuestLog
    {
        readonly IContentDatabase _content;
        readonly IEventBus _events;
        readonly IGameLog _log;
        readonly IGameClock _clock;
        readonly Dictionary<Id<QuestDef>, QuestProgress> _progress = new Dictionary<Id<QuestDef>, QuestProgress>();

        public QuestService(IContentDatabase content, IEventBus events, IGameLog log, IGameClock clock = null)
        {
            _content = content;
            _events = events;
            _log = log;
            _clock = clock;
        }

        /// <summary>Game seconds now (0 without a clock).</summary>
        public double GameSeconds => _clock?.GameSeconds ?? 0;

        public QuestStatus StatusOf(Id<QuestDef> quest) => _progress.TryGetValue(quest, out var p) ? p.Status : QuestStatus.Open;

        /// <summary>Null while the quest has never changed.</summary>
        public QuestProgress ProgressOf(Id<QuestDef> quest) => _progress.TryGetValue(quest, out var p) ? p : null;

        public bool TryGetDefinition(Id<QuestDef> quest, out QuestDef definition) => _content.TryGet(quest, out definition);

        /// <summary>Running and past its time limit (G3 Overtime); false for quests without a limit or without a clock.</summary>
        public bool IsOverdue(Id<QuestDef> quest)
        {
            if (_clock == null || StatusOf(quest) != QuestStatus.Running || !_content.TryGet(quest, out QuestDef def) ||
                def.TimeLimitHours <= 0) return false;
            return _clock.GameSeconds - _progress[quest].StartedAt > def.TimeLimitHours * 3600.0;
        }

        public bool TrySetStatus(Id<QuestDef> quest, QuestStatus to)
        {
            if (!_content.TryGet(quest, out QuestDef def))
            {
                _log.Warning($"Quest '{quest}' does not exist; status {to} ignored.");
                return false;
            }
            var from = StatusOf(quest);
            // fix-186: a quest already in that state stays as it is, quietly (regress-1021b: 24x 'PC_Hero_FIndTeleportToKing:
            // Success → Success' — the picked-up stone completes the FindItem quest, then a SucceedQuest / the dev step
            // 'quest .. Success' asks again). No event, false as before [interp: G3 SetStatus to the same state is a no-op].
            if (from == to) return false;
            if (!QuestTransitions.IsAllowed(from, to))
            {
                _log.Warning($"Quest '{quest}': {from} → {to} is not allowed.");
                return false;
            }
            var progress = GetOrCreate(def);
            progress.Status = to;
            if (to == QuestStatus.Running && from == QuestStatus.Open) progress.StartedAt = _clock?.GameSeconds ?? 0;
            _events.Publish(new QuestStatusChanged(quest, from, to));
            return true;
        }

        /// <summary>
        /// Adds to the counter of <paramref name="target"/> (an NPC, item, area … of the objective). Returns false when
        /// the quest has no such target or its counters are not active.
        /// </summary>
        public bool Count(Id<QuestDef> quest, string target, int delta)
        {
            if (!_content.TryGet(quest, out QuestDef def)) return false;
            int index = def.Objective.IndexOf(target);
            var status = StatusOf(quest);
            if (index < 0 || !QuestTransitions.CountersActive(status)) return false;
            var progress = GetOrCreate(def);
            progress.Counters[index] = Math.Max(0, progress.Counters[index] + delta);
            if (AllTargetsReached(def, progress))
                TrySetStatus(quest, def.Objective.Kind == QuestObjectiveKind.Sparring ? QuestStatus.Won : QuestStatus.Success);
            return true;
        }

        /// <summary>
        /// fix-161: a Failed quest re-judged by a port rule (<see cref="ForcedReleases"/>, a save from before the rule): Failed →
        /// Success outside <see cref="QuestTransitions"/> (Failed is final for content and the console), every target counted,
        /// published like any change so the rewards and the log follow. False unless the quest exists and is Failed.
        /// </summary>
        public bool ReviseFailedToSuccess(Id<QuestDef> quest)
        {
            if (!_content.TryGet(quest, out QuestDef def) || StatusOf(quest) != QuestStatus.Failed) return false;
            var progress = GetOrCreate(def);
            var targets = def.Objective.Targets;
            for (int i = 0; i < targets.Count; i++) progress.Counters[i] = Math.Max(progress.Counters[i], targets[i].Amount);
            progress.Status = QuestStatus.Success;
            _events.Publish(new QuestStatusChanged(quest, QuestStatus.Failed, QuestStatus.Success));
            return true;
        }

        /// <summary>Quests with state, for saving.</summary>
        internal IEnumerable<KeyValuePair<Id<QuestDef>, QuestProgress>> All => _progress;

        /// <summary>Sets a quest's state from a save game: no transition check, no event.</summary>
        internal void Restore(Id<QuestDef> quest, QuestStatus status, double startedAt, int[] counters)
        {
            int targets = _content.TryGet(quest, out QuestDef def) ? def.Objective.Targets.Count : counters.Length;
            var progress = new QuestProgress(targets) { Status = status, StartedAt = startedAt };
            Array.Copy(counters, progress.Counters, Math.Min(targets, counters.Length));
            _progress[quest] = progress;
        }

        QuestProgress GetOrCreate(QuestDef def)
        {
            if (!_progress.TryGetValue(def.Id, out var p)) _progress[def.Id] = p = new QuestProgress(def.Objective.Targets.Count);
            return p;
        }

        static bool AllTargetsReached(QuestDef def, QuestProgress progress)
        {
            var targets = def.Objective.Targets;
            if (targets.Count == 0) return false;
            for (int i = 0; i < targets.Count; i++)
                if (progress.Counters[i] < targets[i].Amount) return false;
            return true;
        }
    }

    /// <summary>Allowed status changes and the named helpers of the content commands (gameplay-logic.md §10, quests.json).</summary>
    public static class QuestTransitions
    {
        public static bool IsAllowed(QuestStatus from, QuestStatus to)
        {
            switch (to)
            {
                case QuestStatus.Running: return from == QuestStatus.Open || from == QuestStatus.Lost;
                case QuestStatus.Success:
                case QuestStatus.Won:
                case QuestStatus.Failed: return from == QuestStatus.Open || from == QuestStatus.Running;
                case QuestStatus.Obsolete: return from == QuestStatus.Open;
                case QuestStatus.Cancelled:
                case QuestStatus.Lost: return from == QuestStatus.Running;
                default: return false; // nothing returns to Open
            }
        }

        /// <summary>Objective counters count in these states only.</summary>
        public static bool CountersActive(QuestStatus status) =>
            status == QuestStatus.Open || status == QuestStatus.Running || status == QuestStatus.Lost;

        /// <summary>"Close": an Open quest becomes Obsolete, a Running one Cancelled.</summary>
        public static QuestStatus CloseTarget(QuestStatus from) => from == QuestStatus.Open ? QuestStatus.Obsolete : QuestStatus.Cancelled;

        /// <summary>"Fail": an Open quest becomes Obsolete, anything else Failed.</summary>
        public static QuestStatus FailTarget(QuestStatus from) => from == QuestStatus.Open ? QuestStatus.Obsolete : QuestStatus.Failed;
    }
}
