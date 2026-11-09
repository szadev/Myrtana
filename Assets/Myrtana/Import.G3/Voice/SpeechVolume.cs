using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G3.Formats.Pak;
using G3.Formats.Story;

namespace Myrtana.Import.G3
{
    /// <summary>
    /// The <c>Speech_&lt;Language&gt;</c> volumes. Every .ogg gets a language-independent clip key: its file name without
    /// the language suffix (<c>INFO_Orc_Warrior_Hard_Gnar_FILLER787_Russian.ogg</c> → <c>INFO_Orc_Warrior_Hard_Gnar_FILLER787</c>).
    /// The few Russian files mis-named <c>_German.ogg</c> get the same key as their correctly named siblings.
    /// </summary>
    public sealed class SpeechVolume : IDisposable
    {
        static readonly string[] StraySuffixes = { "_German" };

        readonly G3FileSystem _fs;
        readonly VoiceIndex _index;
        readonly Dictionary<string, PakEntry> _byKey = new Dictionary<string, PakEntry>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, PakEntry> _lipByKey = new Dictionary<string, PakEntry>(StringComparer.OrdinalIgnoreCase);

        public SpeechVolume(G3Install install, string language, Func<string, bool> isTextId)
        {
            Language = language;
            _fs = install.Open("Speech_" + language);
            var oggs = _fs.Files.Where(f => f.Path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)).ToList();
            _index = new VoiceIndex(_fs.Files.Select(f => f.Path), language, isTextId);
            foreach (var e in oggs) _byKey[KeyOf(e.Path)] = e;
            foreach (var e in _fs.Files.Where(f => f.Path.EndsWith(".xlip", StringComparison.OrdinalIgnoreCase)))
                _lipByKey[KeyOf(e.Path)] = e;
        }

        public string Language { get; }
        public int ClipCount => _byKey.Count;

        /// <summary>Every clip key of the volume.</summary>
        public IEnumerable<string> Keys => _byKey.Keys;

        /// <summary>Clip key of the recording of <paramref name="textId"/> spoken by <paramref name="speaker"/> (G3 NPC name), or null.</summary>
        public string FindLine(string textId, string speaker)
        {
            var files = _index.Find(textId, speaker);
            var match = files.FirstOrDefault(f => f.Prefix.Equals(speaker, StringComparison.OrdinalIgnoreCase) ||
                                                  f.Prefix.EndsWith("_" + speaker, StringComparison.OrdinalIgnoreCase));
            return match == null ? null : KeyOf(match.OggPath);
        }

        public bool Contains(string clipKey) => clipKey != null && _byKey.ContainsKey(clipKey);

        public byte[] Read(string clipKey) => _byKey[clipKey].Read();

        /// <summary>Unpacked size of a clip in bytes.</summary>
        public long SizeOf(string clipKey) => _byKey[clipKey].Size;

        /// <summary>The visemes of a clip (its <c>.xlip</c>, Docs/formats-story.md §4), or null when the clip has none.</summary>
        public Xlip ReadLipSync(string clipKey) =>
            clipKey != null && _lipByKey.TryGetValue(clipKey, out var e) ? Xlip.Read(e.Read()) : null;

        string KeyOf(string path)
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            foreach (var suffix in new[] { "_" + Language }.Concat(StraySuffixes))
                if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return stem.Substring(0, stem.Length - suffix.Length);
            return stem;
        }

        public void Dispose() => _fs.Dispose();
    }
}
