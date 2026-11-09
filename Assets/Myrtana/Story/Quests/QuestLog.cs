using System;
using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Story
{
    /// <summary>One line of a quest's log: what was said, by whom. Stored as a text id, so it follows the language.</summary>
    public readonly struct QuestLogEntry
    {
        public QuestLogEntry(ActorId speaker, TextId text)
        {
            Speaker = speaker;
            Text = text;
        }

        /// <summary>Empty for a plain note (AddQuestLog).</summary>
        public ActorId Speaker { get; }
        public TextId Text { get; }
    }

    /// <summary>A line was spoken in a dialog entry; <see cref="LogQuest"/> names the quest whose log records it, if any.</summary>
    public readonly struct DialogLineSpoken : IGameEvent
    {
        public DialogLineSpoken(Id<DialogEntryDef> entry, Id<QuestDef> logQuest, ActorId speaker, TextId text)
        {
            Entry = entry;
            LogQuest = logQuest;
            Speaker = speaker;
            Text = text;
        }

        public Id<DialogEntryDef> Entry { get; }
        public Id<QuestDef> LogQuest { get; }
        public ActorId Speaker { get; }
        public TextId Text { get; }
    }

    /// <summary>
    /// The quest log as G3 builds it (gameplay-logic.md §9): the transcript of the quest's dialog entries, in the order
    /// they were spoken, plus notes added by content. Quests are listed most recently updated first.
    /// </summary>
    public sealed class QuestLog : IDisposable
    {
        readonly Dictionary<Id<QuestDef>, List<QuestLogEntry>> _entries = new Dictionary<Id<QuestDef>, List<QuestLogEntry>>();
        readonly List<Id<QuestDef>> _recent = new List<Id<QuestDef>>();
        readonly IDisposable _lines, _status;

        public QuestLog(IEventBus events)
        {
            _lines = events.Subscribe<DialogLineSpoken>(e =>
            {
                if (!e.LogQuest.IsEmpty) Add(e.LogQuest, new QuestLogEntry(e.Speaker, e.Text));
            });
            _status = events.Subscribe<QuestStatusChanged>(e => Touch(e.Quest));
        }

        public event Action Changed;

        /// <summary>Quests with log entries or a status change, most recent first.</summary>
        public IReadOnlyList<Id<QuestDef>> Quests => _recent;

        public IReadOnlyList<QuestLogEntry> EntriesOf(Id<QuestDef> quest) =>
            _entries.TryGetValue(quest, out var list) ? list : (IReadOnlyList<QuestLogEntry>)Array.Empty<QuestLogEntry>();

        public void Add(Id<QuestDef> quest, QuestLogEntry entry)
        {
            if (!_entries.TryGetValue(quest, out var list)) _entries[quest] = list = new List<QuestLogEntry>();
            list.Add(entry);
            Touch(quest);
        }

        public void Dispose()
        {
            _lines.Dispose();
            _status.Dispose();
        }

        /// <summary>For loading: restores the order without events.</summary>
        internal void Restore(IReadOnlyList<Id<QuestDef>> recent, IReadOnlyDictionary<Id<QuestDef>, List<QuestLogEntry>> entries)
        {
            _recent.Clear();
            _recent.AddRange(recent);
            _entries.Clear();
            foreach (var kv in entries) _entries[kv.Key] = kv.Value;
            Changed?.Invoke();
        }

        void Touch(Id<QuestDef> quest)
        {
            _recent.Remove(quest);
            _recent.Insert(0, quest);
            Changed?.Invoke();
        }
    }

    /// <summary>Save section "story.questlog".</summary>
    public sealed class QuestLogSaveSection : ISaveParticipant
    {
        readonly QuestLog _log;

        public QuestLogSaveSection(QuestLog log) => _log = log;

        public string Section => "story.questlog";
        public int Version => 1;

        public void Write(ISaveWriter w)
        {
            w.WriteInt32(_log.Quests.Count);
            foreach (var quest in _log.Quests)
            {
                w.WriteString(quest.Name);
                var entries = _log.EntriesOf(quest);
                w.WriteInt32(entries.Count);
                foreach (var e in entries)
                {
                    w.WriteString(e.Speaker.Name);
                    w.WriteString(e.Text.Value);
                }
            }
        }

        public void Read(ISaveReader r, int version)
        {
            int n = r.ReadInt32();
            var order = new List<Id<QuestDef>>(n);
            var entries = new Dictionary<Id<QuestDef>, List<QuestLogEntry>>();
            for (int i = 0; i < n; i++)
            {
                var quest = new Id<QuestDef>(r.ReadString());
                order.Add(quest);
                int m = r.ReadInt32();
                var list = new List<QuestLogEntry>(m);
                for (int j = 0; j < m; j++) list.Add(new QuestLogEntry(new ActorId(r.ReadString()), new TextId(r.ReadString())));
                if (m > 0) entries[quest] = list;
            }
            _log.Restore(order, entries);
        }
    }
}
