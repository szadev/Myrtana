using System;
using System.Linq;
using Myrtana.Core;

namespace Myrtana.Story
{
    /// <summary>
    /// Save section "story.quests": status, start time and objective counters of every quest that has state.
    /// Version 1 (slice 2) held the status only.
    /// </summary>
    public sealed class QuestSaveSection : ISaveParticipant
    {
        readonly QuestService _quests;

        public QuestSaveSection(QuestService quests) => _quests = quests;

        public string Section => "story.quests";
        public int Version => 2;

        public void Write(ISaveWriter w)
        {
            var entries = _quests.All.OrderBy(kv => kv.Key.Name, StringComparer.Ordinal).ToList();
            w.WriteInt32(entries.Count);
            foreach (var kv in entries)
            {
                w.WriteString(kv.Key.Name);
                w.WriteInt32((int)kv.Value.Status);
                w.WriteDouble(kv.Value.StartedAt);
                w.WriteInt32(kv.Value.Counters.Length);
                foreach (int c in kv.Value.Counters) w.WriteInt32(c);
            }
        }

        public void Read(ISaveReader r, int version)
        {
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                var quest = new Id<QuestDef>(r.ReadString());
                var status = (QuestStatus)r.ReadInt32();
                double startedAt = 0;
                var counters = Array.Empty<int>();
                if (version >= 2)
                {
                    startedAt = r.ReadDouble();
                    counters = new int[r.ReadInt32()];
                    for (int c = 0; c < counters.Length; c++) counters[c] = r.ReadInt32();
                }
                _quests.Restore(quest, status, startedAt, counters);
            }
        }
    }
}
