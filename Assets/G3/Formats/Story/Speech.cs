using System;
using System.Collections.Generic;
using System.IO;
using G3.Formats.Genome;

namespace G3.Formats.Story
{
    /// <summary>A voice sample in Speech_&lt;Language&gt;.pak.</summary>
    public sealed class VoiceFile
    {
        /// <summary>Path inside the volume, e.g. "Russian_ogg/INFO_Orc_Warrior_Bearish_Barrack_BPANKRATZ314_Russian.ogg".</summary>
        public string OggPath;
        /// <summary>Matching lip-sync path ("&lt;Lang&gt;_LipSyncData/….xlip"), or null when missing.</summary>
        public string XlipPath;
        /// <summary>"INFO" or "SVM".</summary>
        public string Kind;
        /// <summary>
        /// For INFO: "&lt;Voice&gt;_&lt;Speaker&gt;" (voice = NPC voice set like "Orc_Warrior_Bearish", "Player", "Xardas";
        /// speaker = NPC name). For SVM: "&lt;Voice&gt;_&lt;SvmName&gt;" ("Ass_Elite_Hard_Aargh").
        /// </summary>
        public string Prefix;
        /// <summary>INFO: stringtable id without the "INFO_" prefix ("BPANKRATZ314"); SVM: the variant index ("00").</summary>
        public string Key;
    }

    /// <summary>
    /// Maps dialogue lines to voice files (Docs/formats-story.md §4). Naming:
    /// <c>&lt;Lang&gt;_ogg/INFO_&lt;Voice&gt;_&lt;Speaker&gt;_&lt;TextId minus "INFO_"&gt;_&lt;Lang&gt;.ogg</c>
    /// plus the same stem in <c>&lt;Lang&gt;_LipSyncData/…xlip</c>. Because voice and speaker names contain '_', the
    /// INFO split is resolved against the set of known text ids (longest suffix that is a known id).
    /// </summary>
    public sealed class VoiceIndex
    {
        public readonly string Language;
        public readonly List<VoiceFile> Files = new List<VoiceFile>();
        /// <summary>INFO files whose key did not match any known text id.</summary>
        public readonly List<string> Unresolved = new List<string>();
        readonly Dictionary<string, List<VoiceFile>> _byKey = new Dictionary<string, List<VoiceFile>>(StringComparer.OrdinalIgnoreCase);

        /// <param name="paths">All entry paths of Speech_&lt;Language&gt; volumes.</param>
        /// <param name="isTextId">Predicate "is this a stringtable id" (e.g. StringTable.Entries.ContainsKey).</param>
        public VoiceIndex(IEnumerable<string> paths, string language, Func<string, bool> isTextId)
        {
            Language = language;
            string oggDir = language + "_ogg/", lipDir = language + "_LipSyncData/", tail = "_" + language;
            var xlips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var oggs = new List<string>();
            foreach (var p in paths)
            {
                if (p.StartsWith(lipDir, StringComparison.OrdinalIgnoreCase) && p.EndsWith(".xlip", StringComparison.OrdinalIgnoreCase))
                    xlips.Add(Stem(p));
                else if (p.StartsWith(oggDir, StringComparison.OrdinalIgnoreCase) && p.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
                    oggs.Add(p);
            }
            foreach (var p in oggs)
            {
                string stem = Stem(p);
                var f = new VoiceFile { OggPath = p, XlipPath = xlips.Contains(stem) ? lipDir + stem + ".xlip" : null };
                string s = stem.EndsWith(tail, StringComparison.OrdinalIgnoreCase) ? stem.Substring(0, stem.Length - tail.Length) : stem;
                int us = s.IndexOf('_');
                f.Kind = us > 0 ? s.Substring(0, us) : s;
                s = s.Substring(us + 1);
                if (f.Kind == "INFO")
                {
                    for (int i = 0; i < s.Length; i++)
                    {
                        if (s[i] != '_') continue;
                        string cand = s.Substring(i + 1);
                        if (isTextId("INFO_" + cand) || isTextId(cand)) { f.Prefix = s.Substring(0, i); f.Key = cand; break; }
                    }
                    if (f.Key == null) { Unresolved.Add(p); f.Key = s; f.Prefix = ""; }
                }
                else
                {
                    int last = s.LastIndexOf('_');
                    f.Prefix = last > 0 ? s.Substring(0, last) : s;
                    f.Key = last > 0 ? s.Substring(last + 1) : "";
                }
                Files.Add(f);
                if (f.Kind == "INFO")
                {
                    if (!_byKey.TryGetValue(f.Key, out var l)) _byKey[f.Key] = l = new List<VoiceFile>();
                    l.Add(f);
                }
            }
        }

        static string Stem(string p)
        {
            int s = p.LastIndexOf('/') + 1, d = p.LastIndexOf('.');
            return p.Substring(s, d - s);
        }

        /// <summary>
        /// Voice files for a dialogue text id ("INFO_BPANKRATZ314" or "BPANKRATZ314"). If <paramref name="speaker"/> is given,
        /// files whose prefix ends with "_" + speaker (or equals it) are preferred.
        /// </summary>
        public IReadOnlyList<VoiceFile> Find(string textId, string speaker = null)
        {
            if (string.IsNullOrEmpty(textId)) return Array.Empty<VoiceFile>();
            string key = textId.StartsWith("INFO_", StringComparison.OrdinalIgnoreCase) ? textId.Substring(5) : textId;
            if (!_byKey.TryGetValue(key, out var l)) return Array.Empty<VoiceFile>();
            if (speaker == null || l.Count == 1) return l;
            var m = l.FindAll(f => f.Prefix.Equals(speaker, StringComparison.OrdinalIgnoreCase) ||
                                   f.Prefix.EndsWith("_" + speaker, StringComparison.OrdinalIgnoreCase));
            return m.Count > 0 ? m : l;
        }
    }

    /// <summary>
    /// .xlip lip-sync track. Payload: u16 version 1, u32 count, count × { f32 time (seconds), u32 viseme index }.
    /// Two containers occur: GENOMFLE-wrapped (all English, ~half of Russian; empty string table after the payload)
    /// and bare payload (the other Russian files). Both parse to the exact end.
    /// </summary>
    public sealed class Xlip
    {
        public bool Genome;
        public ushort Version;
        public float[] Times;
        public uint[] Visemes;

        public static Xlip Read(byte[] data)
        {
            var g = new GenomeReader(data);
            var r = g.R;
            var x = new Xlip { Genome = g.IsGenomFile, Version = r.ReadUInt16() };
            if (x.Version != 1) throw new InvalidDataException("xlip: version " + x.Version);
            int n = r.ReadInt32();
            x.Times = new float[n];
            x.Visemes = new uint[n];
            for (int i = 0; i < n; i++) { x.Times[i] = r.ReadSingle(); x.Visemes[i] = r.ReadUInt32(); }
            if (g.Position != g.PayloadEnd) throw new InvalidDataException($"xlip: {g.PayloadEnd - g.Position} unread bytes");
            return x;
        }
    }
}
