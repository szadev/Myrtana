namespace Myrtana.Core
{
    /// <summary>Which voice clips exist per language (GameData/Voice folders in Unity, a set in tests).</summary>
    public interface IVoiceClipCatalog
    {
        bool Contains(string language, string clipKey);
    }

    /// <summary>A voice clip chosen for playback: language-independent key + the language it was found in.</summary>
    public readonly struct VoiceClip
    {
        public VoiceClip(string language, string key)
        {
            Language = language;
            Key = key;
        }

        public string Language { get; }
        public string Key { get; }
    }

    /// <summary>
    /// Voice fallback chain: the selected voice language, then the fallback language, then silence (the subtitle is
    /// then shown for an estimated time). Voice keys are language-independent and resolved at import.
    /// </summary>
    public sealed class VoiceResolver
    {
        readonly LanguageSettings _settings;
        readonly IVoiceClipCatalog _catalog;

        public VoiceResolver(LanguageSettings settings, IVoiceClipCatalog catalog)
        {
            _settings = settings;
            _catalog = catalog;
        }

        public bool TryResolve(string clipKey, out VoiceClip clip)
        {
            clip = default;
            if (string.IsNullOrEmpty(clipKey)) return false;
            foreach (var language in new[] { _settings.VoiceLanguage, _settings.FallbackLanguage })
            {
                if (!_catalog.Contains(language, clipKey)) continue;
                clip = new VoiceClip(language, clipKey);
                return true;
            }
            return false;
        }
    }
}
