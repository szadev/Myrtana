namespace Myrtana.Core
{
    /// <summary>Resolves text ids for display. Only views call it; the game state keeps ids.</summary>
    public interface ILocalizer
    {
        /// <summary>
        /// Text in the selected language, else in the fallback language, else a visible marker <c>[id]</c> so missing
        /// strings are noticed instead of silently showing nothing.
        /// </summary>
        string Text(TextId id);
        bool TryText(TextId id, out string text);
        /// <summary>Stage direction of the line in the selected language (fallback language); null if none.</summary>
        string Stage(TextId id);
    }

    public sealed class Localizer : ILocalizer
    {
        readonly LanguageSettings _settings;
        readonly IStringTableSource _tables;

        public Localizer(LanguageSettings settings, IStringTableSource tables)
        {
            _settings = settings;
            _tables = tables;
        }

        public string Text(TextId id) => TryText(id, out var text) ? text : "[" + id.Value + "]";

        public bool TryText(TextId id, out string text)
        {
            text = null;
            if (id.IsEmpty) return false;
            return TryIn(_settings.TextLanguage, id, out text) || TryIn(_settings.FallbackLanguage, id, out text) ||
                   PatchStrings.TryGet(_settings.TextLanguage, id.Value, out text);
        }

        /// <summary>The text in one given language (no fallback); dev name search looks in several languages.</summary>
        public bool TryTextIn(string language, TextId id, out string text)
        {
            text = null;
            return !id.IsEmpty && !string.IsNullOrEmpty(language) && TryIn(language, id, out text);
        }

        public string Stage(TextId id)
        {
            if (id.IsEmpty) return null;
            foreach (var language in new[] { _settings.TextLanguage, _settings.FallbackLanguage })
            {
                var table = _tables.Load(language);
                if (table != null && table.TryGetStage(id.Value, out var stage)) return stage;
            }
            return null;
        }

        bool TryIn(string language, TextId id, out string text)
        {
            text = null;
            var table = _tables.Load(language);
            return table != null && table.TryGetText(id.Value, out text);
        }
    }
}
