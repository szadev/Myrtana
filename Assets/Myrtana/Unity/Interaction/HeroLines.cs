using System.Collections;
using System.IO;
using Myrtana.Core;
using Myrtana.Session;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// The hero's own SVM lines (<c>SVM_Player_&lt;line&gt;.ogg</c> of the voice language) with their subtitle as a HUD
    /// message when it has a text (the string table, or PatchStrings for the lines CP 1.75 dropped, fix-235).
    /// </summary>
    public sealed class HeroLines
    {
        const float MaxLineSeconds = 30f;

        readonly VoicePlayer _voice;
        readonly LanguageSettings _languages;
        readonly HudMessages _messages;

        public HeroLines(VoicePlayer voice, LanguageSettings languages, HudMessages messages)
        {
            _voice = voice;
            _languages = languages;
            _messages = messages;
        }

        /// <summary>Starts the line without waiting.</summary>
        public void Say(GameSession session, string line) => Start(session, line, null);

        /// <summary>
        /// One of the numbered variants of an SVM line ("DoesntWork" → SVM_Player_DoesntWork_00 … _NN), picked at random
        /// among the files of the voice language, as G3's StartSay picks one.
        /// </summary>
        public void SayAny(GameSession session, string line)
        {
            if (string.IsNullOrEmpty(line) || _languages == null) return;
            int count = 0;
            string folder = Path.Combine(ContentLocations.VoiceRoot, _languages.VoiceLanguage);
            while (count < 20 && File.Exists(Path.Combine(folder, $"SVM_Player_{line}_{count:00}.ogg"))) count++;
            Say(session, count > 0 ? $"{line}_{Random.Range(0, count):00}" : line);
        }

        /// <summary>Says the line and waits until it ends (at most 30 s; at once when the file is missing).</summary>
        public IEnumerator SayAndWait(GameSession session, string line)
        {
            bool done = false;
            if (!Start(session, line, () => done = true)) yield break;
            float end = Time.unscaledTime + MaxLineSeconds;
            while (!done && Time.unscaledTime < end) yield return null;
        }

        bool Start(GameSession session, string line, System.Action finished)
        {
            if (string.IsNullOrEmpty(line)) return false;
            string id = "SVM_Player_" + line;
            if (session != null && _messages != null && session.Localizer.TryText(new TextId(id), out var subtitle) && !string.IsNullOrEmpty(subtitle))
                _messages.Show(subtitle);
            if (_voice == null || _languages == null) return false;
            string path = Path.Combine(ContentLocations.VoiceRoot, _languages.VoiceLanguage, id + ".ogg");
            if (!File.Exists(path)) return false;
            _voice.Play(path, () => finished?.Invoke());
            return true;
        }
    }
}
