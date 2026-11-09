using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;

namespace Myrtana.Story
{
    /// <summary>
    /// The subtitle of a shared (SVM) line: the speaker's voice set's own text (selected language, then the fallback
    /// language, then <see cref="PatchStrings"/>), else the same line's text of another voice set. The CP 1.75 table has
    /// texts for only some voice sets of a line (Gorn's crime news, Ass_Fat_Bearish's in Russian only, …), so another
    /// set's wording stands in rather than an empty subtitle box <b>[interp]</b>. False when no voice set has a text: the
    /// line is heard, not shown (fix-235).
    /// </summary>
    public sealed class SharedLineTexts
    {
        /// <summary>The hero's voice set (his content entry, the SVM_Player_* lines).</summary>
        public const string HeroVoiceSet = "Player";

        readonly SharedLines _lines;
        readonly ILocalizer _text;
        readonly string[] _voiceSets, _byName;
        readonly string _prefix, _middle, _suffix;
        readonly bool _parsable;

        public SharedLineTexts(SharedLines lines, ILocalizer text, IEnumerable<string> voiceSets)
        {
            _lines = lines;
            _text = text ?? throw new ArgumentNullException(nameof(text));
            // longest first: "Hum_Warrior_Hard" before a shorter set that is its prefix
            _voiceSets = (voiceSets ?? Enumerable.Empty<string>()).Where(v => !string.IsNullOrEmpty(v))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(v => v.Length).ThenBy(v => v, StringComparer.Ordinal).ToArray();
            _byName = _voiceSets.OrderBy(v => v, StringComparer.Ordinal).ToArray();
            string pattern = lines?.TextPattern;
            int voice = pattern?.IndexOf(SharedLines.VoiceToken, StringComparison.Ordinal) ?? -1;
            int line = pattern?.IndexOf(SharedLines.LineToken, StringComparison.Ordinal) ?? -1;
            _parsable = voice >= 0 && line > voice;
            if (_parsable)
            {
                _prefix = pattern.Substring(0, voice);
                _middle = pattern.Substring(voice + SharedLines.VoiceToken.Length, line - voice - SharedLines.VoiceToken.Length);
                _suffix = pattern.Substring(line + SharedLines.LineToken.Length);
            }
        }

        /// <summary>The voice sets the stand-in search looks through (longest name first).</summary>
        public IReadOnlyList<string> VoiceSets => _voiceSets;

        /// <summary>The subtitle of <paramref name="line"/> spoken with <paramref name="voiceSet"/>.</summary>
        public bool TryText(string voiceSet, string line, out string text)
        {
            text = null;
            if (_lines == null || string.IsNullOrEmpty(line)) return false;
            if (!string.IsNullOrEmpty(voiceSet) && Usable(_lines.TextOf(voiceSet, line), out text)) return true;
            var stand = StandIn(line);
            return !stand.IsEmpty && Usable(stand, out text);
        }

        /// <summary>
        /// The subtitle of a shared-line text id ("SVM_Gorn_FirstMurder"); for any other id just its text. False when
        /// nothing non-blank is found.
        /// </summary>
        public bool TryText(TextId id, out string text)
        {
            if (Usable(id, out text)) return true;
            return TrySplit(id.Value, out var voiceSet, out var line) && TryText(voiceSet, line, out text);
        }

        /// <summary>Splits a shared-line id into a known voice set and the line name.</summary>
        public bool TrySplit(string id, out string voiceSet, out string line)
        {
            voiceSet = line = null;
            if (!_parsable || string.IsNullOrEmpty(id) || !id.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase) ||
                !id.EndsWith(_suffix, StringComparison.OrdinalIgnoreCase)) return false;
            string rest = id.Substring(_prefix.Length, id.Length - _prefix.Length - _suffix.Length);
            foreach (var set in _voiceSets)
            {
                if (rest.Length <= set.Length + _middle.Length || !rest.StartsWith(set + _middle, StringComparison.OrdinalIgnoreCase)) continue;
                voiceSet = set;
                line = rest.Substring(set.Length + _middle.Length);
                return true;
            }
            return false;
        }

        /// <summary>
        /// The first voice set (by name) whose text id of <paramref name="line"/> has a text in the current languages; not
        /// cached, as the language can change (only lines without their own text get here).
        /// </summary>
        TextId StandIn(string line)
        {
            foreach (var set in _byName)
            {
                var candidate = _lines.TextOf(set, line);
                if (Usable(candidate, out _)) return candidate;
            }
            return default;
        }

        bool Usable(TextId id, out string text)
        {
            if (!id.IsEmpty && _text.TryText(id, out text) && !string.IsNullOrWhiteSpace(text)) return true;
            text = null;
            return false;
        }
    }
}
