using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Story
{
    /// <summary>Which one-shot dialog entries have already run, when timed entries last ran and when conversations ended.</summary>
    public sealed class DialogMemory
    {
        readonly HashSet<Id<DialogEntryDef>> _given = new HashSet<Id<DialogEntryDef>>();

        public IEnumerable<Id<DialogEntryDef>> Given => _given;

        public bool WasGiven(Id<DialogEntryDef> entry) => _given.Contains(entry);

        public void MarkGiven(Id<DialogEntryDef> entry) => _given.Add(entry);

        readonly Dictionary<Id<DialogEntryDef>, double> _lastRun = new Dictionary<Id<DialogEntryDef>, double>();
        readonly Dictionary<ActorId, double> _dialogEnd = new Dictionary<ActorId, double>();

        /// <summary>Entries whose run time is remembered (CP QuestRunning infos), game seconds.</summary>
        public IEnumerable<KeyValuePair<Id<DialogEntryDef>, double>> LastRuns => _lastRun;
        /// <summary>When each actor's last conversation with the player ended (G3 EndDialogTimestamp), game seconds.</summary>
        public IEnumerable<KeyValuePair<ActorId, double>> DialogEnds => _dialogEnd;

        public void MarkRun(Id<DialogEntryDef> entry, double gameSeconds) => _lastRun[entry] = gameSeconds;

        public bool TryGetLastRun(Id<DialogEntryDef> entry, out double gameSeconds) => _lastRun.TryGetValue(entry, out gameSeconds);

        public void MarkDialogEnd(ActorId actor, double gameSeconds)
        {
            if (!actor.IsEmpty) _dialogEnd[actor] = gameSeconds;
        }

        public bool TryGetDialogEnd(ActorId actor, out double gameSeconds) => _dialogEnd.TryGetValue(actor, out gameSeconds);
    }
}
