using System;
using System.Collections.Generic;
using System.IO;
using Myrtana.Core;

namespace Myrtana.Unity
{
    /// <summary>
    /// Voice clips extracted to <c>GameData/Voice/&lt;Language&gt;/&lt;clip key&gt;.ogg</c> (<see cref="ContentLocations.VoiceRoot"/>, outside Assets). Each language folder is
    /// listed once, the first time it is asked for.
    /// </summary>
    public sealed class StreamingVoiceCatalog : IVoiceClipCatalog
    {
        const string Extension = ".ogg";

        readonly string _root;
        readonly Dictionary<string, HashSet<string>> _byLanguage = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        public StreamingVoiceCatalog(string root) => _root = root;

        public bool Contains(string language, string clipKey) => Keys(language).Contains(clipKey);

        public string PathOf(VoiceClip clip) => Path.Combine(_root, clip.Language, clip.Key + Extension);

        HashSet<string> Keys(string language)
        {
            if (_byLanguage.TryGetValue(language, out var keys)) return keys;
            keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string dir = Path.Combine(_root, language);
            if (Directory.Exists(dir))
                foreach (var file in Directory.GetFiles(dir, "*" + Extension)) keys.Add(Path.GetFileNameWithoutExtension(file));
            _byLanguage[language] = keys;
            return keys;
        }
    }
}
