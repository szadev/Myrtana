using System;

namespace Myrtana.Core
{
    /// <summary>
    /// The two independent language settings (G3 <c>ge3.ini [Language] Text= / Audio=</c>). Changing one only swaps
    /// lookup tables; listeners (open UI) refresh through <see cref="Changed"/>. Languages use the G3 column names
    /// ("Russian", "English", …). Missing text or voice falls back to <see cref="FallbackLanguage"/>.
    /// </summary>
    public sealed class LanguageSettings
    {
        public const string DefaultFallback = "English";

        string _text, _voice;

        public LanguageSettings(string textLanguage, string voiceLanguage, string fallbackLanguage = DefaultFallback)
        {
            _text = textLanguage;
            _voice = voiceLanguage;
            FallbackLanguage = fallbackLanguage;
        }

        public event Action Changed;

        public string FallbackLanguage { get; }

        public string TextLanguage
        {
            get => _text;
            set => Update(ref _text, value);
        }

        public string VoiceLanguage
        {
            get => _voice;
            set => Update(ref _voice, value);
        }

        void Update(ref string field, string value)
        {
            if (string.IsNullOrEmpty(value) || string.Equals(field, value, StringComparison.Ordinal)) return;
            field = value;
            Changed?.Invoke();
        }
    }
}
