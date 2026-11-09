using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Story;

namespace Myrtana.Session
{
    /// <summary>Where the hero talked to <see cref="Npc"/> about <see cref="Quest"/> last (world metres).</summary>
    public readonly struct QuestTalkMark
    {
        public QuestTalkMark(Id<QuestDef> quest, ActorId npc, float x, float y, float z, int order)
        {
            Quest = quest;
            Npc = npc;
            X = x;
            Y = y;
            Z = z;
            Order = order;
        }

        public Id<QuestDef> Quest { get; }
        public ActorId Npc { get; }
        public float X { get; }
        public float Y { get; }
        public float Z { get; }
        /// <summary>When it was recorded (rising; the save keeps it): the latest talk wins, marks are listed in this order.</summary>
        public int Order { get; }
    }

    /// <summary>
    /// fix-126 [owner] ("mark where the hero talked to the people of a quest"; G3 has no such marks, its quest log map only
    /// filters by place, Docs/map-teleport.md §2.5): a dialog line that goes into a quest's log (<see cref="DialogLineSpoken"/>
    /// with a log quest: the quest's start, its updates, its end) records the dialog partner — the NPC the hero talks to
    /// (<see cref="DialogStarted"/>), else the line's speaker when that is not the hero — at the partner's position at that
    /// moment. One mark per quest and NPC; a later talk moves it. Saved in the section "story.questtalks".
    /// </summary>
    public sealed class QuestTalkMarks : IDisposable
    {
        readonly Func<ActorId, (float x, float y, float z)?> _positionOf;
        readonly Dictionary<(Id<QuestDef> quest, ActorId npc), QuestTalkMark> _marks = new Dictionary<(Id<QuestDef>, ActorId), QuestTalkMark>();
        readonly List<IDisposable> _subscriptions = new List<IDisposable>();
        ActorId _partner;
        int _next;

        public QuestTalkMarks(IEventBus events, Func<ActorId, (float x, float y, float z)?> positionOf)
        {
            _positionOf = positionOf;
            if (events == null) return;
            _subscriptions.Add(events.Subscribe<DialogStarted>(e => _partner = e.Partner));
            _subscriptions.Add(events.Subscribe<DialogEnded>(e => _partner = default));
            _subscriptions.Add(events.Subscribe<DialogLineSpoken>(e => Record(e.LogQuest, e.Speaker)));
        }

        public event Action Changed;

        /// <summary>Every mark, oldest first.</summary>
        public IReadOnlyList<QuestTalkMark> All => _marks.Values.OrderBy(m => m.Order).ToList();

        /// <summary>The marks of the quests <paramref name="quests"/> accepts, oldest first.</summary>
        public IReadOnlyList<QuestTalkMark> Of(Func<Id<QuestDef>, bool> quests) =>
            _marks.Values.Where(m => quests == null || quests(m.Quest)).OrderBy(m => m.Order).ToList();

        /// <summary>
        /// A line of <paramref name="quest"/>'s log was spoken by <paramref name="speaker"/>: the dialog partner (or the
        /// speaker, if not the hero) is marked where he stands. False when nothing was recorded.
        /// </summary>
        public bool Record(Id<QuestDef> quest, ActorId speaker)
        {
            if (quest.IsEmpty) return false;
            var npc = !_partner.IsEmpty && !_partner.IsPlayer ? _partner : speaker;
            if (npc.IsEmpty || npc.IsPlayer) return false;
            var at = _positionOf?.Invoke(npc);
            if (at == null) return false;
            var key = (quest, npc);
            if (_marks.TryGetValue(key, out var old) && old.X == at.Value.x && old.Y == at.Value.y && old.Z == at.Value.z) return false;
            _marks[key] = new QuestTalkMark(quest, npc, at.Value.x, at.Value.y, at.Value.z, _next++);
            Changed?.Invoke();
            return true;
        }

        public void Dispose()
        {
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
        }

        internal void Restore(IEnumerable<QuestTalkMark> marks)
        {
            _marks.Clear();
            _partner = default;
            _next = 0;
            foreach (var m in marks)
            {
                if (m.Quest.IsEmpty || m.Npc.IsEmpty) continue;
                _marks[(m.Quest, m.Npc)] = m;
                _next = Math.Max(_next, m.Order + 1);
            }
            Changed?.Invoke();
        }
    }

    /// <summary>Save section "story.questtalks": the quest talk marks, oldest first.</summary>
    public sealed class QuestTalkMarksSaveSection : ISaveParticipant
    {
        readonly QuestTalkMarks _marks;

        public QuestTalkMarksSaveSection(QuestTalkMarks marks) => _marks = marks;

        public string Section => "story.questtalks";
        public int Version => 1;

        public void Write(ISaveWriter w)
        {
            var marks = _marks.All;
            w.WriteInt32(marks.Count);
            foreach (var m in marks)
            {
                w.WriteString(m.Quest.Name);
                w.WriteString(m.Npc.Name);
                w.WriteDouble(m.X);
                w.WriteDouble(m.Y);
                w.WriteDouble(m.Z);
                w.WriteInt32(m.Order);
            }
        }

        public void Read(ISaveReader r, int version)
        {
            int count = r.ReadInt32();
            var marks = new List<QuestTalkMark>(Math.Max(0, count));
            for (int i = 0; i < count; i++)
            {
                var quest = new Id<QuestDef>(r.ReadString());
                var npc = new ActorId(r.ReadString());
                float x = (float)r.ReadDouble(), y = (float)r.ReadDouble(), z = (float)r.ReadDouble();
                marks.Add(new QuestTalkMark(quest, npc, x, y, z, r.ReadInt32()));
            }
            _marks.Restore(marks);
        }
    }
}
