using Myrtana.Core;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// Text and voice language as player preferences (kept outside save games). Russian text and voice by default;
    /// every change is stored immediately (except <see cref="Transient"/> ones).
    /// </summary>
    public static class LanguagePreferences
    {
        public const string DefaultLanguage = "Russian";
        const string TextKey = "Myrtana.TextLanguage", VoiceKey = "Myrtana.VoiceLanguage";

        public static LanguageSettings Load()
        {
            var settings = new LanguageSettings(PlayerPrefs.GetString(TextKey, DefaultLanguage), PlayerPrefs.GetString(VoiceKey, DefaultLanguage));
            settings.Changed += () => Save(settings);
            return settings;
        }

        /// <summary>
        /// While true, language changes are not stored (dev scenarios switch languages for a test; editor-runner-16: a
        /// scenario's <c>lang</c> changed the owner's language for good). The runner restores the values it found.
        /// </summary>
        public static bool Transient { get; set; }

        static void Save(LanguageSettings settings)
        {
            if (Transient) return;
            PlayerPrefs.SetString(TextKey, settings.TextLanguage);
            PlayerPrefs.SetString(VoiceKey, settings.VoiceLanguage);
            PlayerPrefs.Save();
        }
    }
}
