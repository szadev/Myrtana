using System;
using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Session;
using Myrtana.Simulation;
using Myrtana.Story;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// Presenter of the conversation (<see cref="IDialogPresenter"/>): plays a line (voice clip, or an estimated time
    /// when there is no recording) while the screen shows its subtitle, then offers the choices. It maps the dialog
    /// data to localised texts for an <see cref="IDialogScreen"/> — the G3 look (<see cref="G3DialogScreen"/>) or the
    /// plain fallback — and owns the original behaviour (Docs/formats-gui.md §6.2): the mouse selects on hover and
    /// activates with one click, ↑/↓ move without wrap-around, Enter activates, Esc moves the selection to the first
    /// End choice, no number keys; a set of only End choices runs its first one (a lone choice is already run by
    /// <see cref="DialogService"/>). Space/E/Enter/Esc/left click skip the current line.
    /// Texts are resolved when shown and again when a language setting changes.
    /// </summary>
    public sealed class DialogView : MonoBehaviour, IDialogPresenter, ISessionView
    {
        GameSession _session;
        VoicePlayer _voice;
        VoiceResolver _voices;
        StreamingVoiceCatalog _catalog;
        IDialogScreen _screen;
        LanguageSettings _languages;

        DialogLine? _currentLine;
        IReadOnlyList<DialogOption> _currentOptions;
        float _silentUntil = -1;
        bool _lineFinished, _onlyExits, _hiddenForPage;
        int _shownFrame;

        public void Init(VoicePlayer voice, StreamingVoiceCatalog catalog, VisualElement root, LanguageSettings languages)
        {
            _voice = voice;
            _catalog = catalog;
            _languages = languages;
            _voices = new VoiceResolver(languages, catalog);
            _screen = (IDialogScreen)G3DialogScreen.TryCreate(transform) ?? new PlainDialogScreen(root);
            _screen.ChoiceActivated += Choose;
            languages.Changed += Render;
        }

        public void Bind(GameSession session) => _session = session;

        /// <summary>The choice list is on screen (not a spoken line, not the wait before the list): the cursor shows (fix-87).</summary>
        public bool ChoicesShown => _session != null && _session.Dialog.IsActive && _currentOptions != null && !_onlyExits;

        /// <summary>The Subtitles option (G3 StartSay shows the talk box only while it is on); null = on.</summary>
        public Func<bool> SubtitlesOn { get; set; }

        /// <summary>A line starts: its data and the path of its voice clip (null when it is not recorded).</summary>
        public event Action<DialogLine, string> LineStarted;

        /// <summary>The current line is over (finished, skipped, or the conversation closed).</summary>
        public event Action LineEnded;

        void OnDestroy()
        {
            if (_languages != null) _languages.Changed -= Render;
        }

        /// <summary>Nothing is shown until the first line or choice set (the G3 dialog page appears only for choices).</summary>
        public void Open(ActorId partner) { }

        public void ShowLine(DialogLine line)
        {
            _currentLine = line;
            _currentOptions = null;
            _lineFinished = false;
            _shownFrame = Time.frameCount;
            Render(newLine: true);
            string voicePath = null;
            if (_voices.TryResolve(line.Voice, out var clip))
            {
                _silentUntil = -1;
                voicePath = _catalog.PathOf(clip);
                _voice.Play(voicePath, () => _lineFinished = true);
            }
            else
            {
                if (!string.IsNullOrEmpty(line.Voice))
                    Debug.LogWarning($"[Myrtana] voice {line.Voice}: no clip in {_languages.VoiceLanguage} or {_languages.FallbackLanguage} " +
                                     $"under {ContentLocations.VoiceRoot}");
                _silentUntil = Time.unscaledTime + SilentDuration(LineText(line.Text));
            }
            LineStarted?.Invoke(line, voicePath);
        }

        public void ShowOptions(IReadOnlyList<DialogOption> options)
        {
            _currentLine = null;
            _currentOptions = options;
            _shownFrame = Time.frameCount;
            _onlyExits = OnlyExits(options);
            if (_onlyExits) _screen.Hide();
            else _screen.ShowChoices(ChoiceTexts(), 0);
        }

        public void Close()
        {
            _voice.Stop();
            if (_currentLine.HasValue) LineEnded?.Invoke();
            _currentLine = null;
            _currentOptions = null;
            _screen.Hide();
        }

        void Update()
        {
            if (_session == null || !_session.Dialog.IsActive || Time.frameCount == _shownFrame) return;
            if (_session.Dialog.IsHeldByPage)
            {
                // fix-140: a page (trade) holds the talk: no subtitle or choice box over it, no skip keys
                if (!_hiddenForPage) _screen.Hide();
                _hiddenForPage = true;
                return;
            }
            _hiddenForPage = false;
            if (_currentLine.HasValue) UpdateLine();
            else if (_currentOptions != null) UpdateChoice();
        }

        void UpdateLine()
        {
            bool timedOut = _silentUntil >= 0 && Time.unscaledTime >= _silentUntil;
            if (!_lineFinished && !timedOut && !SkipPressed()) return;
            _voice.Stop();
            _currentLine = null;
            LineEnded?.Invoke();
            _session.Dialog.CompleteLine();
        }

        void UpdateChoice()
        {
            if (_onlyExits)
            {
                Choose(0);
                return;
            }
            // gamepad-1: d-pad / left stick choose, A takes the choice, B selects the exit choice (as Esc)
            var keys = PageKeys.Now;
            int selected = _screen.SelectedChoice;
            if (keys.Down) _screen.SelectChoice(selected < 0 ? 0 : Mathf.Min(selected + 1, _currentOptions.Count - 1));
            else if (keys.Up) _screen.SelectChoice(Mathf.Max(selected - 1, 0));
            else if (keys.Key(Key.Escape) || global::G3.Unity.G3Pad.Ui(G3.Player.UiCommand.Back)) _screen.SelectChoice(FirstExit(_currentOptions, selected));
            else if (keys.Enter) Choose(selected);
        }

        static bool SkipPressed()
        {
            var kb = global::G3.Unity.G3Input.GameKeyboard;
            var mouse = Mouse.current;
            return (kb != null && (kb.spaceKey.wasPressedThisFrame || kb.eKey.wasPressedThisFrame || kb.enterKey.wasPressedThisFrame || kb.escapeKey.wasPressedThisFrame)) ||
                   (mouse != null && mouse.leftButton.wasPressedThisFrame) ||
                   // gamepad-1: A, B or X skips the spoken line
                   global::G3.Unity.G3Pad.Pressed(G3.Player.PadButton.A) || global::G3.Unity.G3Pad.Pressed(G3.Player.PadButton.B) ||
                   global::G3.Unity.G3Pad.Pressed(G3.Player.PadButton.X);
        }

        void Choose(int index)
        {
            if (_currentOptions == null || index < 0 || index >= _currentOptions.Count) return;
            var entry = _currentOptions[index].Entry;
            _currentOptions = null;
            _screen.Hide();
            _session.Dialog.Choose(entry);
        }

        /// <summary>
        /// The subtitle of a line. A shared (SVM) line without its voice set's text takes another set's
        /// (<see cref="SharedLineTexts"/>); one that no set has a text for (CP's "PartyWait", the hero's "TooHard" after a
        /// failed pickpocket) shows no subtitle instead of its id [interp: G3 finds no string] (fix-235).
        /// </summary>
        string LineText(TextId text)
        {
            if (_session.Localizer.TryText(text, out var s) && !string.IsNullOrWhiteSpace(s)) return s;
            if (_session.SharedLineTexts != null && _session.SharedLineTexts.TryText(text, out s)) return s;
            return text.Value != null && text.Value.StartsWith("SVM_", StringComparison.OrdinalIgnoreCase) ? string.Empty : _session.Localizer.Text(text);
        }

        void Render() => Render(newLine: false);

        void Render(bool newLine)
        {
            if (_session == null) return;
            if (_currentLine.HasValue && SubtitlesOn?.Invoke() == false)
                _screen.Hide();
            else if (_currentLine.HasValue)
            {
                // fix-235: a line without text is heard only; never an empty subtitle box
                string text = LineText(_currentLine.Value.Text);
                if (string.IsNullOrWhiteSpace(text)) _screen.Hide();
                else _screen.ShowLine(DisplayName(_currentLine.Value.Speaker), text, newLine);
            }
            else if (_currentOptions != null && !_onlyExits)
                _screen.ShowChoices(ChoiceTexts(), _screen.SelectedChoice);
        }

        List<DialogChoiceText> ChoiceTexts()
        {
            var texts = new List<DialogChoiceText>(_currentOptions.Count);
            foreach (var option in _currentOptions)
                texts.Add(new DialogChoiceText(option.Title.IsEmpty ? "…" : DialogOptionText.Format(_session.Localizer.Text(option.Title), option.Cost, _session.Localizer.Text),
                                               option.IsExit));
            return texts;
        }

        static bool OnlyExits(IReadOnlyList<DialogOption> options)
        {
            if (options.Count == 0) return false;
            foreach (var option in options)
                if (!option.IsExit) return false;
            return true;
        }

        /// <summary>Esc target: the first End choice, or the current selection when there is none.</summary>
        static int FirstExit(IReadOnlyList<DialogOption> options, int fallback)
        {
            for (int i = 0; i < options.Count; i++)
                if (options[i].IsExit) return i;
            return fallback;
        }

        string DisplayName(ActorId actor) =>
            _session.Content.Database.TryGet(actor.Name, out NpcDef def) && !def.DisplayName.IsEmpty
                ? _session.Localizer.Text(def.DisplayName)
                : actor.Name;

        /// <summary>Reading time for a line without a recording.</summary>
        static float SilentDuration(string text) => Mathf.Clamp(1f + text.Length * 0.06f, 2f, 8f);
    }
}
