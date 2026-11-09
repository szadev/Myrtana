using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Session;
using Myrtana.Story;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// The quest log (key L): quests grouped as current / successful / failed on the left, the selected quest's
    /// transcript on the right (<see cref="QuestJournal"/>). ↑/↓ or the mouse select.
    /// </summary>
    public sealed class QuestLogScreen : GameScreen
    {
        static readonly TextId Title = new TextId("HUD_SessionKey_JournalModeLog");

        readonly Label _title, _hint;
        readonly ScrollView _list, _detail;
        readonly List<QuestJournal.Quest> _shown = new List<QuestJournal.Quest>();
        readonly List<Button> _buttons = new List<Button>();
        Id<QuestDef> _selected;

        public QuestLogScreen(VisualElement hudRoot) : base(hudRoot, "QuestLogScreen")
        {
            _title = Root.Q<Label>("screen-title");
            _hint = Root.Q<Label>("screen-hint");
            _list = Root.Q<ScrollView>("quest-list");
            _detail = Root.Q<ScrollView>("quest-detail");
        }

        public override void Render()
        {
            var journal = QuestJournal.Of(Session);
            _title.text = Session.Localizer.Text(Title);
            _hint.text = "↑↓    L / Esc";
            _list.Clear();
            _shown.Clear();
            _buttons.Clear();
            foreach (QuestJournal.Group group in System.Enum.GetValues(typeof(QuestJournal.Group)))
            {
                var quests = journal.Groups[group];
                if (quests.Count == 0) continue;
                _list.Add(Label(journal.GroupTitles[group], "screen-section-title"));
                foreach (var quest in quests) AddQuestButton(quest, group != QuestJournal.Group.Running);
            }
            if (_shown.Count == 0)
            {
                _detail.Clear();
                _detail.Add(Label(journal.EmptyText, "screen-empty"));
                return;
            }
            if (_shown.FindIndex(q => q.Id == _selected) < 0) _selected = _shown[0].Id;
            ShowDetail();
        }

        public override void Tick()
        {
            var kb = global::G3.Unity.G3Input.GameKeyboard;
            if (kb == null || _shown.Count == 0) return;
            int index = _shown.FindIndex(q => q.Id == _selected);
            if (kb.downArrowKey.wasPressedThisFrame) Select(_shown[(index + 1) % _shown.Count].Id);
            if (kb.upArrowKey.wasPressedThisFrame) Select(_shown[(index - 1 + _shown.Count) % _shown.Count].Id);
        }

        void AddQuestButton(QuestJournal.Quest quest, bool finished)
        {
            var button = new Button(() => Select(quest.Id)) { text = quest.Title };
            button.AddToClassList("screen-item");
            button.EnableInClassList("screen-item--dim", finished);
            _list.Add(button);
            _shown.Add(quest);
            _buttons.Add(button);
        }

        void Select(Id<QuestDef> quest)
        {
            _selected = quest;
            ShowDetail();
        }

        void ShowDetail()
        {
            int index = _shown.FindIndex(q => q.Id == _selected);
            for (int i = 0; i < _buttons.Count; i++) _buttons[i].EnableInClassList("screen-item--selected", i == index);
            if (index < 0) return;
            _list.ScrollTo(_buttons[index]);
            var quest = _shown[index];
            _detail.Clear();
            _detail.Add(Label(quest.Title, "screen-section-title"));
            foreach (var line in quest.Lines)
            {
                var label = Label(string.IsNullOrEmpty(line.Speaker) ? line.Text : $"<b>{line.Speaker}:</b> {line.Text}", "quest-line");
                label.enableRichText = true;
                _detail.Add(label);
            }
        }
    }
}
