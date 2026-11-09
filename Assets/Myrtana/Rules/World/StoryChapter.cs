using System;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// The story chapter (G3 PlayerMemory.Chapter, set by the info command SetChapter). Chapter 2 breaks the orc–Hashishin
    /// alliance in the political matrix (Docs/perception-crime.md §5).
    /// </summary>
    public sealed class StoryChapter
    {
        public const int First = 1;

        public int Current { get; private set; } = First;

        public event Action Changed;

        public void Set(int chapter)
        {
            chapter = Math.Max(First, chapter);
            if (chapter == Current) return;
            Current = chapter;
            Changed?.Invoke();
        }
    }

    /// <summary>Save section "rules.chapter".</summary>
    public sealed class StoryChapterSaveSection : ISaveParticipant
    {
        readonly StoryChapter _chapter;

        public StoryChapterSaveSection(StoryChapter chapter) => _chapter = chapter ?? throw new ArgumentNullException(nameof(chapter));

        public string Section => "rules.chapter";
        public int Version => 1;

        public void Write(ISaveWriter w) => w.WriteInt32(_chapter.Current);

        public void Read(ISaveReader r, int version) => _chapter.Set(r.ReadInt32());
    }
}
