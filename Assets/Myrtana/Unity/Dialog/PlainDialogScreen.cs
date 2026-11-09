using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// Fallback look when the G3 GUI is not imported: the <c>dialog</c> box of Hud.uxml with speaker, line and a list
    /// of plain buttons (hover selects, click activates, as in the G3 look).
    /// </summary>
    public sealed class PlainDialogScreen : IDialogScreen
    {
        const string SelectedClass = "dialog-option--selected", ExitClass = "dialog-option--exit", OptionClass = "dialog-option";

        readonly VisualElement _panel;
        readonly Label _speaker, _line;
        readonly ScrollView _options;
        readonly List<Button> _buttons = new List<Button>();
        int _selected;

        public event Action<int> ChoiceActivated;

        public PlainDialogScreen(VisualElement root)
        {
            _panel = root.Q("dialog");
            _speaker = root.Q<Label>("dialog-speaker");
            _line = root.Q<Label>("dialog-line");
            _options = root.Q<ScrollView>("dialog-options");
            var hint = root.Q<Label>("dialog-hint");
            if (hint != null) hint.style.display = DisplayStyle.None;
            Hide();
        }

        public int SelectedChoice => _selected;

        public void ShowLine(string speaker, string text, bool newLine)
        {
            _speaker.text = speaker;
            _line.text = text;
            Show(line: true);
        }

        public void ShowChoices(IReadOnlyList<DialogChoiceText> choices, int selected)
        {
            _options.Clear();
            _buttons.Clear();
            for (int i = 0; i < choices.Count; i++)
            {
                int index = i;
                var button = new Button(() => ChoiceActivated?.Invoke(index)) { text = choices[i].Title };
                button.AddToClassList(OptionClass);
                button.EnableInClassList(ExitClass, choices[i].IsExit);
                button.RegisterCallback<PointerEnterEvent>(_ => SelectChoice(index));
                _options.Add(button);
                _buttons.Add(button);
            }
            Show(line: false);
            SelectChoice(selected);
        }

        public void SelectChoice(int index)
        {
            if (_buttons.Count == 0) return;
            _selected = Math.Clamp(index, 0, _buttons.Count - 1);
            for (int i = 0; i < _buttons.Count; i++) _buttons[i].EnableInClassList(SelectedClass, i == _selected);
            if (_options.panel != null) _options.ScrollTo(_buttons[_selected]);
        }

        public void Hide() => _panel.style.display = DisplayStyle.None;

        void Show(bool line)
        {
            _panel.style.display = DisplayStyle.Flex;
            _speaker.style.display = line ? DisplayStyle.Flex : DisplayStyle.None;
            _line.style.display = line ? DisplayStyle.Flex : DisplayStyle.None;
            _options.style.display = line ? DisplayStyle.None : DisplayStyle.Flex;
        }
    }
}
