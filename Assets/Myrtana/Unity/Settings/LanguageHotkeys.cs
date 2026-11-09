using System;
using Myrtana.Core;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Myrtana.Unity
{
    /// <summary>
    /// Dev keys (the audio options page sets both languages too): F11 switches the text language, F12 the voice language
    /// (Russian ↔ English). The change applies at once, also in a running conversation. They were F9 / F10 until
    /// par-controls: F9 is G3's quickload and F10 opens the dev console.
    /// </summary>
    public sealed class LanguageHotkeys : MonoBehaviour
    {
        static readonly string[] Cycle = { "Russian", "English" };

        LanguageSettings _settings;

        public void Bind(LanguageSettings settings) => _settings = settings;

        void Update()
        {
            var kb = global::G3.Unity.G3Input.GameKeyboard;
            if (kb == null || _settings == null) return;
            if (kb.f11Key.wasPressedThisFrame) _settings.TextLanguage = Next(_settings.TextLanguage);
            if (kb.f12Key.wasPressedThisFrame) _settings.VoiceLanguage = Next(_settings.VoiceLanguage);
        }

        static string Next(string current)
        {
            int i = Array.IndexOf(Cycle, current);
            return Cycle[(i + 1) % Cycle.Length];
        }
    }
}
