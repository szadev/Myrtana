using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>What reading a bookstand did: the hero's lines in order, the attribute raise, the story effects.</summary>
    public sealed class BookstandReading
    {
        public List<string> Lines { get; } = new List<string>();
        /// <summary>Null when nothing was learned.</summary>
        public AttributeGain Gain { get; set; }
        /// <summary><see cref="Gain"/> has been applied to the hero.</summary>
        public bool Learned { get; set; }
        public List<string> RunQuests { get; } = new List<string>();
        public List<string> SucceedQuests { get; } = new List<string>();
        public List<string> EnableSectors { get; } = new List<string>();
        /// <summary>The stand had nothing (more) to give.</summary>
        public bool NothingNew { get; set; }
    }

    /// <summary>Published after the hero read a bookstand that taught him something.</summary>
    public readonly struct BookstandRead : IGameEvent
    {
        public BookstandRead(string objectId, string useFunc)
        {
            ObjectId = objectId;
            UseFunc = useFunc;
        }

        public string ObjectId { get; }
        public string UseFunc { get; }
    }

    /// <summary>What the hero has read: the stands already used and the pages read of each book (G3 PlayerMemory BookOf*).</summary>
    public sealed class BookstandMemory
    {
        readonly HashSet<string> _used = new HashSet<string>(StringComparer.Ordinal);
        readonly Dictionary<string, int> _pages = new Dictionary<string, int>(StringComparer.Ordinal);

        public bool IsUsed(string objectId) => _used.Contains(objectId);
        public void MarkUsed(string objectId) => _used.Add(objectId);
        public int PagesRead(string book) => _pages.TryGetValue(book, out int n) ? n : 0;
        public void SetPagesRead(string book, int pages) => _pages[book] = pages;
        public IEnumerable<string> Used => _used;
        public IEnumerable<KeyValuePair<string, int>> Pages => _pages;

        public void Clear()
        {
            _used.Clear();
            _pages.Clear();
        }
    }

    /// <summary>Saves <see cref="BookstandMemory"/> (section "world.bookstands").</summary>
    public sealed class BookstandSaveSection : ISaveParticipant
    {
        readonly Func<BookstandMemory> _memory;

        public BookstandSaveSection(Func<BookstandMemory> memory) => _memory = memory;

        public string Section => "world.bookstands";
        public int Version => 1;

        public void Write(ISaveWriter w)
        {
            var m = _memory();
            var used = m.Used.OrderBy(u => u, StringComparer.Ordinal).ToList();
            w.WriteInt32(used.Count);
            foreach (var id in used) w.WriteString(id);
            var pages = m.Pages.OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
            w.WriteInt32(pages.Count);
            foreach (var p in pages)
            {
                w.WriteString(p.Key);
                w.WriteInt32(p.Value);
            }
        }

        public void Read(ISaveReader r, int version)
        {
            var m = _memory();
            m.Clear();
            int used = r.ReadInt32();
            for (int i = 0; i < used; i++) m.MarkUsed(r.ReadString());
            int pages = r.ReadInt32();
            for (int i = 0; i < pages; i++) m.SetPagesRead(r.ReadString(), r.ReadInt32());
        }
    }

    /// <summary>
    /// Reading bookstands and stone plates (G3 <c>PS_Interact_Bookstand</c> and the <c>BS_*</c> states). The stand's use
    /// function decides: a used stand, an empty function or a "#event" function says "nothing new"; wisdom stands read the
    /// next unfinished book; a book reading speaks two pages and raises intelligence; lesson stands raise a craft once;
    /// story stands wait for their game event. Every stand that taught something is used up.
    /// </summary>
    public sealed class Bookstands
    {
        const char EventMarker = '#';

        readonly BookstandRules _rules;
        readonly RuleOptions _options;
        readonly BookstandMemory _memory;
        readonly IGameEventFlags _flags;
        readonly IRandom _random;
        readonly IEventBus _events;

        public Bookstands(BookstandRules rules, RuleOptions options, BookstandMemory memory, IGameEventFlags flags, IRandom random,
                          IEventBus events)
        {
            _rules = rules ?? BookstandRules.None;
            _options = options;
            _memory = memory;
            _flags = flags;
            _random = random;
            _events = events;
        }

        public BookstandMemory Memory => _memory;

        /// <summary>Reads the stand; the caller applies <see cref="BookstandReading.Gain"/>, the quests and the sectors.</summary>
        public BookstandReading Read(string objectId, string useFunc)
        {
            var reading = new BookstandReading();
            if (_memory.IsUsed(objectId) || string.IsNullOrWhiteSpace(useFunc)) return NothingNew(reading);
            int marker = useFunc.IndexOf(EventMarker);
            if (marker >= 0)
            {
                string flag = useFunc.Split(new[] { ' ', EventMarker }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrEmpty(flag)) _flags?.Set(flag);
                return NothingNew(reading);
            }
            string func = useFunc.Trim();
            if (func == _rules.WisdomUseFunc)
                func = _rules.WisdomOrder.FirstOrDefault(b => Book(b) is StandBook book && _memory.PagesRead(book.UseFunc) < book.Pages);
            if (func == null) return NothingNew(reading);

            if (Book(func) is StandBook standBook) return ReadBook(objectId, standBook, reading);
            var lesson = _rules.Lessons.FirstOrDefault(l => l.UseFunc == func);
            if (lesson != null)
            {
                reading.Lines.Add(lesson.Line);
                reading.Gain = Gain(lesson.Attribute);
                return Used(objectId, func, reading);
            }
            var story = _rules.Stories.FirstOrDefault(s => s.UseFunc == func);
            if (story != null)
            {
                if (!string.IsNullOrEmpty(story.RequiredFlag) && _flags != null && !_flags.IsSet(story.RequiredFlag))
                {
                    if (!string.IsNullOrEmpty(story.NotReadyLine)) reading.Lines.Add(story.NotReadyLine);
                    return reading;
                }
                reading.Lines.AddRange(story.Lines);
                reading.RunQuests.AddRange(story.RunQuests);
                reading.SucceedQuests.AddRange(story.SucceedQuests);
                reading.EnableSectors.AddRange(story.Sectors);
                return Used(objectId, func, reading);
            }
            return NothingNew(reading);
        }

        StandBook Book(string useFunc) => _rules.Books.FirstOrDefault(b => b.UseFunc == useFunc);

        BookstandReading ReadBook(string objectId, StandBook book, BookstandReading reading)
        {
            int read = _memory.PagesRead(book.UseFunc);
            if (read >= book.Pages) return NothingNew(reading);
            int pages = Math.Min(_rules.PagesPerReading, book.Pages - read);
            for (int i = 0; i < pages; i++) reading.Lines.Add($"{book.Line}_{read + i:00}");
            _memory.SetPagesRead(book.UseFunc, read + pages);
            reading.Gain = Gain(_rules.BookAttribute);
            return Used(objectId, book.UseFunc, reading);
        }

        AttributeGain Gain(string attribute)
        {
            int points = _rules.AttributePoints.For(_options ?? RuleOptions.Default);
            return string.IsNullOrEmpty(attribute) || points == 0 ? null : new AttributeGain(attribute, ModifierOp.AddToVal, points);
        }

        BookstandReading Used(string objectId, string func, BookstandReading reading)
        {
            _memory.MarkUsed(objectId);
            _events?.Publish(new BookstandRead(objectId, func));
            return reading;
        }

        BookstandReading NothingNew(BookstandReading reading)
        {
            reading.NothingNew = true;
            var lines = _rules.NothingNewLines;
            if (lines.Count > 0) reading.Lines.Add(lines[_random == null ? 0 : _random.Next(lines.Count)]);
            return reading;
        }
    }
}
