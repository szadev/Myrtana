using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Skin;
using UnityEngine.UIElements;

namespace Myrtana.Unity.Ui.Components
{
    /// <summary>
    /// A text row of a G3 details list (dialog choices, quest titles, save slots): theme
    /// <c>listctrl_item_details</c> — no bar in the normal state, the soft bar tinted #FFC0C0 on hover, #FF0000 when
    /// selected, #FF8080 as keyboard focus row; text parchment, #FFEBDC on hover (Docs/formats-gui.md §6.2).
    /// An optional 18×18 icon sits left of the text (quest list location icons).
    /// </summary>
    public class G3ListRow : G3Part
    {
        public const string DefaultPart = "listctrl_item_details";

        public Label Label { get; }
        public G3Image Icon { get; private set; }
        /// <summary>Optional right-aligned text after the name (station recipe lists: the craftable count).</summary>
        public Label Count { get; private set; }
        public int Index { get; set; }

        /// <summary>Row text size: the 16 px bold default font of the engine.</summary>
        public const int FontSize = 16;

        public G3ListRow(string text = null, string part = DefaultPart) : base(part)
        {
            AddToClassList("g3-list-row");
            Label = new Label(text) { pickingMode = PickingMode.Ignore };
            Label.AddToClassList("g3-list-row__label");
            Add(Label);
            G3Text.Font(this, FontSize, bold: true);
        }

        public string Text { get => Label.text; set => Label.text = value; }

        /// <summary>
        /// [owner] fix-125: a station recipe the hero cannot make now: name, count and icon in the theme's disabled text
        /// colour (parchment at α 0.5). Only the content dims; the row bar keeps its normal/selected look.
        /// </summary>
        public bool Unavailable
        {
            get => ClassListContains(UnavailableClass);
            set => EnableInClassList(UnavailableClass, value);
        }

        public const string UnavailableClass = "g3-list-row--unavailable";

        /// <summary>Shows a right-aligned text after the name; null or empty removes it.</summary>
        public void SetCount(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                Count?.RemoveFromHierarchy();
                Count = null;
                return;
            }
            if (Count == null)
            {
                Count = new Label { pickingMode = PickingMode.Ignore };
                Count.AddToClassList("g3-list-row__count");
                Add(Count);
            }
            Count.text = text;
        }

        /// <summary>Shows an icon before the text (e.g. a location icon of the quest list); null removes it.</summary>
        public void SetIcon(string imageName)
        {
            if (string.IsNullOrEmpty(imageName))
            {
                Icon?.RemoveFromHierarchy();
                Icon = null;
                return;
            }
            if (Icon == null)
            {
                Icon = new G3Image();
                Icon.AddToClassList("g3-list-row__icon");
                Insert(IndexOf(Label), Icon);
            }
            Icon.Image = imageName;
        }
    }
}
