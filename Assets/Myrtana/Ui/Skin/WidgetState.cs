using System;

namespace Myrtana.Ui.Skin
{
    /// <summary>
    /// The six visual states of a themed G3 widget, in the order of the theme file (Docs/formats-gui.md §3). Every theme
    /// element has a text colour and a blend (image tint) colour per state.
    /// </summary>
    public enum WidgetState { Normal = 0, Disabled = 1, Hidden = 2, Focus = 3, MouseOver = 4, Pressed = 5 }

    /// <summary>What is true about a widget right now (several flags at once); <see cref="WidgetStates.Resolve"/> picks the state.</summary>
    [Flags]
    public enum WidgetFlags
    {
        None = 0,
        Disabled = 1,
        /// <summary>Keyboard focus (list: the focus row; G3 tints it <c>#FF8080</c>).</summary>
        Focused = 2,
        Hovered = 4,
        /// <summary>Mouse button held down on the widget.</summary>
        Pressed = 8,
        /// <summary>Selected list row / tile, checked tab or toggle: drawn with the Pressed colours.</summary>
        Selected = 16,
    }

    public static class WidgetStates
    {
        /// <summary>
        /// Visual state for a set of flags. Priority: Disabled &gt; Pressed/Selected &gt; MouseOver &gt; Focus &gt; Normal — a
        /// selected row keeps its selection colours while hovered. The same priority is encoded in the order of the
        /// generated USS rules (later rule wins), so C# and USS agree.
        /// </summary>
        public static WidgetState Resolve(WidgetFlags flags)
        {
            if ((flags & WidgetFlags.Disabled) != 0) return WidgetState.Disabled;
            if ((flags & (WidgetFlags.Pressed | WidgetFlags.Selected)) != 0) return WidgetState.Pressed;
            if ((flags & WidgetFlags.Hovered) != 0) return WidgetState.MouseOver;
            if ((flags & WidgetFlags.Focused) != 0) return WidgetState.Focus;
            return WidgetState.Normal;
        }

        /// <summary>States in the order their USS rules are emitted (lowest priority first).</summary>
        public static readonly WidgetState[] CascadeOrder =
            { WidgetState.Normal, WidgetState.Focus, WidgetState.MouseOver, WidgetState.Pressed, WidgetState.Disabled };
    }
}
