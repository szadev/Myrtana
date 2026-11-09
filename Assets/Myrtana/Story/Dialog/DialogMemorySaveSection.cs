using System.Linq;
using Myrtana.Core;

namespace Myrtana.Story
{
    /// <summary>Save section "story.dialog": the one-shot dialog entries that have already run, timed entries, dialog ends.</summary>
    public sealed class DialogMemorySaveSection : ISaveParticipant
    {
        readonly DialogMemory _memory;

        public DialogMemorySaveSection(DialogMemory memory) => _memory = memory;

        public string Section => "story.dialog";
        /// <summary>2: + last runs of timed entries and the actors' dialog ends.</summary>
        public int Version => 2;

        public void Write(ISaveWriter w)
        {
            w.WriteSortedStrings(_memory.Given.Select(id => id.Name));
            var runs = _memory.LastRuns.OrderBy(kv => kv.Key.Name, System.StringComparer.Ordinal).ToList();
            w.WriteInt32(runs.Count);
            foreach (var kv in runs)
            {
                w.WriteString(kv.Key.Name);
                w.WriteDouble(kv.Value);
            }
            var ends = _memory.DialogEnds.OrderBy(kv => kv.Key.Name, System.StringComparer.Ordinal).ToList();
            w.WriteInt32(ends.Count);
            foreach (var kv in ends)
            {
                w.WriteString(kv.Key.Name);
                w.WriteDouble(kv.Value);
            }
        }

        public void Read(ISaveReader r, int version)
        {
            foreach (var id in r.ReadStrings()) _memory.MarkGiven(new Id<DialogEntryDef>(id));
            if (version < 2) return;
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                var entry = new Id<DialogEntryDef>(r.ReadString());
                _memory.MarkRun(entry, r.ReadDouble());
            }
            n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                var actor = new ActorId(r.ReadString());
                _memory.MarkDialogEnd(actor, r.ReadDouble());
            }
        }
    }
}
