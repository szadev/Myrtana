using System.Collections.Generic;

namespace Myrtana.Import.G3
{
    /// <summary>Finds the clip key of a line in the first speech volume that has it (keys are equal across languages).</summary>
    public sealed class VoiceLines
    {
        readonly IReadOnlyList<SpeechVolume> _volumes;

        public VoiceLines(IReadOnlyList<SpeechVolume> volumes) => _volumes = volumes;

        public string FindLine(string textId, string g3Speaker)
        {
            if (g3Speaker == null) return null;
            foreach (var v in _volumes)
            {
                var key = v.FindLine(textId, g3Speaker);
                if (key != null) return key;
            }
            return null;
        }

        /// <summary>The voice sets that have a recording of the shared line <paramref name="line"/> (clips "SVM_&lt;set&gt;_&lt;line&gt;").</summary>
        public List<string> VoiceSetsWith(string line)
        {
            var sets = new SortedSet<string>(System.StringComparer.Ordinal);
            string suffix = "_" + line;
            foreach (var v in _volumes)
                foreach (var key in v.Keys)
                    if (key.StartsWith("SVM_", System.StringComparison.OrdinalIgnoreCase) && key.EndsWith(suffix, System.StringComparison.OrdinalIgnoreCase) &&
                        key.Length > 4 + suffix.Length)
                        sets.Add(key.Substring(4, key.Length - 4 - suffix.Length));
            return new List<string>(sets);
        }

        /// <summary>The key itself if any volume has the clip (SVM clips are addressed by name).</summary>
        public string Existing(string clipKey)
        {
            foreach (var v in _volumes)
                if (v.Contains(clipKey)) return clipKey;
            return null;
        }
    }
}
