using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Myrtana.Core
{
    /// <summary>
    /// <c>strings/&lt;Language&gt;.json</c> in a content pack: texts by id plus optional stage directions
    /// ("mocking", "angry") for voice acting and future subtitles.
    /// </summary>
    public sealed class StringTableFile
    {
        public const string Folder = "strings";

        [JsonConstructor]
        public StringTableFile(IReadOnlyDictionary<string, string> texts, IReadOnlyDictionary<string, string> stages)
        {
            Texts = texts ?? new Dictionary<string, string>();
            Stages = stages ?? new Dictionary<string, string>();
        }

        public IReadOnlyDictionary<string, string> Texts { get; }
        public IReadOnlyDictionary<string, string> Stages { get; }

        public static string PathFor(string language) => Folder + "/" + language + ".json";
    }

    /// <summary>All strings of one language after layering the packs.</summary>
    public sealed class StringTable
    {
        // Ids are looked up case-insensitively: the game builds some ids from template names whose case differs from the
        // table key (Perk_OrcSlayer -> FO_It_Perk_Orcslayer); no table has two keys that differ only in case.
        readonly Dictionary<string, string> _texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> _stages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public StringTable(string language) => Language = language;

        public string Language { get; }
        public int Count => _texts.Count;

        public bool TryGetText(string id, out string text) => _texts.TryGetValue(id, out text) && !string.IsNullOrEmpty(text);
        public bool TryGetStage(string id, out string stage) => _stages.TryGetValue(id, out stage) && !string.IsNullOrEmpty(stage);

        /// <summary>Overlays a pack's file: its non-empty entries win.</summary>
        public void Overlay(StringTableFile file)
        {
            foreach (var kv in file.Texts) if (!string.IsNullOrEmpty(kv.Value)) _texts[kv.Key] = kv.Value;
            foreach (var kv in file.Stages) if (!string.IsNullOrEmpty(kv.Value)) _stages[kv.Key] = kv.Value;
        }
    }
}
