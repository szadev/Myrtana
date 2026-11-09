using System;
using System.Collections.Generic;

namespace Myrtana.Unity
{
    /// <summary>A dialog choice as the screen shows it: localised title, and whether it ends the conversation.</summary>
    public readonly struct DialogChoiceText
    {
        public DialogChoiceText(string title, bool isExit)
        {
            Title = title;
            IsExit = isExit;
        }

        public string Title { get; }
        public bool IsExit { get; }
    }

    /// <summary>
    /// The look of the conversation: shows texts the <see cref="DialogView"/> presenter prepared and reports mouse
    /// choices back. Keys, timing and the conversation flow stay in the presenter.
    /// </summary>
    public interface IDialogScreen
    {
        /// <summary>A choice was activated with the mouse (index into the last <see cref="ShowChoices"/> list).</summary>
        event Action<int> ChoiceActivated;

        int SelectedChoice { get; }

        /// <summary>
        /// A spoken line: the subtitle (the G3 look does not show the speaker); the choice list is hidden.
        /// <paramref name="newLine"/>: a line starts (false: the same line again, e.g. after a language change).
        /// </summary>
        void ShowLine(string speaker, string text, bool newLine);

        /// <summary>The choice list with <paramref name="selected"/> highlighted; the subtitle is hidden.</summary>
        void ShowChoices(IReadOnlyList<DialogChoiceText> choices, int selected);

        void SelectChoice(int index);

        void Hide();
    }
}
