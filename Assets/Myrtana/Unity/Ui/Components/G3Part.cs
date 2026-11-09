using Myrtana.Ui.Skin;
using UnityEngine.UIElements;

namespace Myrtana.Unity.Ui.Components
{
    /// <summary>
    /// A themed widget part: the element draws theme layer <c>&lt;part&gt;_back</c> as its background (class
    /// <c>g3-&lt;part&gt;</c> from the generated G3Theme.uss) and, when the theme has one, a <c>_front</c> layer in a
    /// child (class <c>g3-front</c>). Hover/press come from UI Toolkit pseudo-states; selection, keyboard focus and
    /// disabled are classes so presenters can set them from view-model state (<see cref="WidgetFlags"/>).
    /// </summary>
    public class G3Part : VisualElement
    {
        public VisualElement Front { get; }
        public string Part { get; }

        public G3Part(string part, bool front = true)
        {
            Part = part;
            AddToClassList(ThemeUssWriter.PartClass(part));
            if (front)
            {
                Front = new VisualElement { pickingMode = PickingMode.Ignore, name = "front" };
                Front.AddToClassList(ThemeUssWriter.FrontClass);
                Add(Front);
            }
        }

        public bool Selected
        {
            get => ClassListContains(ThemeUssWriter.SelectedClass);
            set => EnableInClassList(ThemeUssWriter.SelectedClass, value);
        }

        /// <summary>Keyboard-focus look (the list "focus row"), independent of UI Toolkit focus.</summary>
        public bool FocusLook
        {
            get => ClassListContains(ThemeUssWriter.FocusClass);
            set => EnableInClassList(ThemeUssWriter.FocusClass, value);
        }

        /// <summary>Applies flags as classes (hover/press flags force the look, e.g. for screenshots and tests).</summary>
        public void SetFlags(WidgetFlags flags)
        {
            Selected = (flags & (WidgetFlags.Selected | WidgetFlags.Pressed)) != 0;
            FocusLook = (flags & WidgetFlags.Focused) != 0;
            EnableInClassList(ThemeUssWriter.DisabledClass, (flags & WidgetFlags.Disabled) != 0);
            EnableInClassList(ThemeUssWriter.HoverClass, (flags & WidgetFlags.Hovered) != 0);
        }
    }
}
