using G3.Player;
using G3.Unity;
using UnityEngine.InputSystem;

namespace Myrtana.Unity
{
    /// <summary>
    /// gamepad-1: the page-local keys of this frame from the keyboard (unchanged: arrows, Enter, PageUp/PageDown, Space …)
    /// or the pad's UI commands (Docs/gamepad.md §4): d-pad / left stick with key repeat, A = Enter, X = Space (one piece),
    /// LT/RT = PageUp/PageDown, LB/RB = the page's tab keys. The keyboard may be held by a text field (then only the pad).
    /// </summary>
    public readonly struct PageKeys
    {
        readonly Keyboard _kb;

        PageKeys(Keyboard kb) => _kb = kb;

        public static PageKeys Now => new PageKeys(G3Input.GameKeyboard);

        /// <summary>The game keyboard (null while a text field holds it).</summary>
        public Keyboard Keyboard => _kb;

        public bool Key(Key k) => _kb != null && _kb[k].wasPressedThisFrame;

        public bool Left => Key(UnityEngine.InputSystem.Key.LeftArrow) || G3Pad.UiMove == NavDir.Left;
        public bool Right => Key(UnityEngine.InputSystem.Key.RightArrow) || G3Pad.UiMove == NavDir.Right;
        public bool Up => Key(UnityEngine.InputSystem.Key.UpArrow) || G3Pad.UiMove == NavDir.Up;
        public bool Down => Key(UnityEngine.InputSystem.Key.DownArrow) || G3Pad.UiMove == NavDir.Down;

        /// <summary>Enter, numpad Enter or A.</summary>
        public bool Enter => Key(UnityEngine.InputSystem.Key.Enter) || Key(UnityEngine.InputSystem.Key.NumpadEnter) || G3Pad.Ui(UiCommand.Accept);

        /// <summary>PageDown or RT (the next grid / pane).</summary>
        public bool PageNext => Key(UnityEngine.InputSystem.Key.PageDown) || G3Pad.Ui(UiCommand.PaneNext);

        /// <summary>PageUp or LT.</summary>
        public bool PagePrev => Key(UnityEngine.InputSystem.Key.PageUp) || G3Pad.Ui(UiCommand.PanePrev);

        /// <summary>Space or X (one piece).</summary>
        public bool Space => Key(UnityEngine.InputSystem.Key.Space) || G3Pad.Ui(UiCommand.Secondary);

        /// <summary>The page's previous-tab key or LB.</summary>
        public bool TabPrev(Key key) => Key(key) || G3Pad.Ui(UiCommand.TabPrev);

        /// <summary>The page's next-tab key or RB.</summary>
        public bool TabNext(Key key) => Key(key) || G3Pad.Ui(UiCommand.TabNext);

        /// <summary>Y (the page's third action: take all, clear the quick slot).</summary>
        public bool PadTertiary => G3Pad.Ui(UiCommand.Tertiary);

        /// <summary>X on the pad only.</summary>
        public bool PadSecondary => G3Pad.Ui(UiCommand.Secondary);

        /// <summary>Space held, or X held on the pad (hold-to-repeat transfers).</summary>
        public static bool SpaceHeld
        {
            get
            {
                var kb = G3Input.GameKeyboard;
                return kb != null && kb.spaceKey.isPressed || G3Pad.Down(PadButton.X);
            }
        }

        /// <summary>The page's grid move of this frame for the arrow keys / d-pad (<see cref="GridNav.Move"/>), or null.</summary>
        public int? GridMove(int selected, int count, int columns)
        {
            var dir = Left ? NavDir.Left : Right ? NavDir.Right : Up ? NavDir.Up : Down ? NavDir.Down : NavDir.None;
            if (dir == NavDir.None) return null;
            return GridNav.Move(selected, count, columns, dir);
        }
    }
}

namespace Myrtana.Unity
{
    /// <summary>gamepad-1: the right stick scrolls a page's text (quest log entries, documents).</summary>
    public static class PadScroll
    {
        /// <summary>Scroll speed at full deflection (reference pixels per second).</summary>
        public const float Speed = 900f;

        public static void Apply(UnityEngine.UIElements.ScrollView view)
        {
            if (view == null) return;
            float y = G3.Unity.G3Pad.RightStick.y;
            if (y == 0f) return;
            var offset = view.scrollOffset;
            offset.y -= y * Speed * UnityEngine.Time.unscaledDeltaTime;
            view.scrollOffset = offset; // the ScrollView clamps to its content
        }
    }
}
