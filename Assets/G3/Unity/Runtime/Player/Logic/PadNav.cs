using System;
using System.Collections.Generic;

namespace G3.Player
{
    /// <summary>A UI move direction.</summary>
    public enum NavDir : byte { None, Up, Down, Left, Right }

    /// <summary>
    /// The UI commands of the pad (Docs/gamepad.md §4). Directions come from the d-pad and the left stick with key repeat
    /// (<see cref="UiNavRepeat"/>), the rest are button presses.
    /// </summary>
    public enum UiCommand : byte
    {
        None, Up, Down, Left, Right,
        /// <summary>A: Enter, click.</summary>
        Accept,
        /// <summary>B: Esc.</summary>
        Back,
        /// <summary>X: the page's second action (one piece, assign to a quick slot).</summary>
        Secondary,
        /// <summary>Y: the page's third action (take all, clear).</summary>
        Tertiary,
        /// <summary>LB / RB: tabs.</summary>
        TabPrev, TabNext,
        /// <summary>LT / RT: the other grid / pane, zoom.</summary>
        PanePrev, PaneNext,
        /// <summary>Start: the game menu (Esc).</summary>
        Menu,
        /// <summary>View: the next journal page.</summary>
        View,
    }

    /// <summary>
    /// Key repeat for held UI directions: the first move at once, then after <see cref="Delay"/> one every
    /// <see cref="Interval"/>. A change of direction starts over.
    /// </summary>
    public sealed class UiNavRepeat
    {
        public const double Delay = 0.4, Interval = 0.09;

        NavDir _held;
        double _next;

        /// <summary>The move of this frame for the held direction (None when nothing is due).</summary>
        public NavDir Update(NavDir held, double now)
        {
            if (held == NavDir.None)
            {
                _held = NavDir.None;
                return NavDir.None;
            }
            if (held != _held)
            {
                _held = held;
                _next = now + Delay;
                return held;
            }
            if (now < _next) return NavDir.None;
            _next = Math.Max(_next + Interval, now - Interval); // after a long frame: one move, no burst
            return held;
        }

        public void Reset() => _held = NavDir.None;

        /// <summary>
        /// The direction a stick points to: the larger axis when the deflection passes <paramref name="threshold"/> (y up).
        /// </summary>
        public static NavDir StickDir(float x, float y, float threshold = 0.5f)
        {
            if (x * x + y * y < threshold * threshold) return NavDir.None;
            if (Math.Abs(x) > Math.Abs(y)) return x > 0 ? NavDir.Right : NavDir.Left;
            return y > 0 ? NavDir.Up : NavDir.Down;
        }
    }

    /// <summary>A screen rectangle (y grows downwards, as UI Toolkit's worldBound).</summary>
    public readonly struct NavRect
    {
        public NavRect(float x, float y, float w, float h)
        {
            X = x;
            Y = y;
            W = w;
            H = h;
        }

        public float X { get; }
        public float Y { get; }
        public float W { get; }
        public float H { get; }
        public float CenterX => X + W * 0.5f;
        public float CenterY => Y + H * 0.5f;
        public float Right => X + W;
        public float Bottom => Y + H;
    }

    /// <summary>
    /// Spatial focus navigation for button pages (the game menu, options): from the focused rectangle, the nearest
    /// candidate in the direction. A candidate must lie beyond the focused one's centre in that direction. Candidates in
    /// the beam (overlapping the focused one across the direction) win over all others; within each group the score is
    /// the gap along the direction plus twice the offset across it, so the row or column straight ahead wins.
    /// </summary>
    public static class SpatialNav
    {
        public static int Pick(NavRect from, IReadOnlyList<NavRect> candidates, NavDir dir, int exclude = -1)
        {
            int best = -1;
            float bestScore = float.MaxValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (i == exclude) continue;
                var c = candidates[i];
                float along, across;
                switch (dir)
                {
                    case NavDir.Right:
                        if (c.CenterX <= from.CenterX + 0.5f) continue;
                        along = Math.Max(0f, c.X - from.Right);
                        across = Gap(from.Y, from.Bottom, c.Y, c.Bottom);
                        break;
                    case NavDir.Left:
                        if (c.CenterX >= from.CenterX - 0.5f) continue;
                        along = Math.Max(0f, from.X - c.Right);
                        across = Gap(from.Y, from.Bottom, c.Y, c.Bottom);
                        break;
                    case NavDir.Down:
                        if (c.CenterY <= from.CenterY + 0.5f) continue;
                        along = Math.Max(0f, c.Y - from.Bottom);
                        across = Gap(from.X, from.Right, c.X, c.Right);
                        break;
                    case NavDir.Up:
                        if (c.CenterY >= from.CenterY - 0.5f) continue;
                        along = Math.Max(0f, from.Y - c.Bottom);
                        across = Gap(from.X, from.Right, c.X, c.Right);
                        break;
                    default:
                        return -1;
                }
                // in the beam (overlapping across) first, as Android's FocusFinder; then gap + twice the offset
                float score = along + 2f * across + (across > 0f ? 100000f : 0f);
                if (score < bestScore - 1e-3f)
                {
                    bestScore = score;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>The first focus of a page: the top-most, then left-most candidate.</summary>
        public static int First(IReadOnlyList<NavRect> candidates)
        {
            int best = -1;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (best < 0) { best = i; continue; }
                var c = candidates[i];
                var b = candidates[best];
                if (c.Y < b.Y - 2f || Math.Abs(c.Y - b.Y) <= 2f && c.X < b.X) best = i;
            }
            return best;
        }

        static float Gap(float a0, float a1, float b0, float b1)
        {
            if (b1 < a0) return a0 - b1;
            if (b0 > a1) return b0 - a1;
            return 0f;
        }
    }

    /// <summary>
    /// The grid moves the pages use for the arrow keys (G3 item grids): left/right step one cell, up/down one row and
    /// stay at the edge; nothing selected yet → the first cell.
    /// </summary>
    public static class GridNav
    {
        public static int Move(int selected, int count, int columns, NavDir dir)
        {
            if (count <= 0) return -1;
            if (selected < 0) return 0;
            columns = Math.Max(1, columns);
            switch (dir)
            {
                case NavDir.Left: return Math.Max(0, selected - 1);
                case NavDir.Right: return Math.Min(count - 1, selected + 1);
                case NavDir.Up: return selected - columns >= 0 ? selected - columns : selected;
                case NavDir.Down: return selected + columns < count ? selected + columns : selected;
                default: return selected;
            }
        }

        /// <summary>A list: up/down one row, clamped.</summary>
        public static int MoveList(int selected, int count, NavDir dir)
        {
            if (count <= 0) return -1;
            if (selected < 0) return 0;
            if (dir == NavDir.Up) return Math.Max(0, selected - 1);
            if (dir == NavDir.Down) return Math.Min(count - 1, selected + 1);
            return selected;
        }
    }
}

namespace G3.Player
{
    /// <summary>
    /// The pad key list's capture (Docs/gamepad.md §5): the first pad button pressed while a cell waits; with LB held it
    /// is an LB chord. LB itself is taken on its release when no other button came (so LB + X can still be pressed).
    /// Start is never taken (it cancels the wait).
    /// </summary>
    public sealed class PadCapture
    {
        bool _modifierDown;

        public PadButton Button { get; private set; }
        public bool Chord { get; private set; }

        public void Reset()
        {
            _modifierDown = false;
            Button = PadButton.None;
            Chord = false;
        }

        /// <summary>One frame: true when a binding was captured (<see cref="Button"/>, <see cref="Chord"/>).</summary>
        public bool Update(Func<PadButton, bool> down, Func<PadButton, bool> pressed)
        {
            for (var b = PadButton.A; b <= PadButton.RsRight; b++)
            {
                if (b == PadButton.Start || b == PadMap.Modifier || !pressed(b)) continue;
                Button = b;
                Chord = down(PadMap.Modifier);
                _modifierDown = false;
                return true;
            }
            if (pressed(PadMap.Modifier)) _modifierDown = true;
            if (_modifierDown && !down(PadMap.Modifier))
            {
                _modifierDown = false;
                Button = PadMap.Modifier;
                Chord = false;
                return true;
            }
            return false;
        }

        /// <summary>The gesture a Y press on a cell steps to: press → tap → hold → press.</summary>
        public static PadGesture NextGesture(PadGesture g) =>
            g == PadGesture.Press ? PadGesture.Tap : g == PadGesture.Tap ? PadGesture.Hold : PadGesture.Press;
    }

    /// <summary>The pages the hint bar knows (Docs/gamepad.md §4).</summary>
    public enum PadHintPage : byte { Generic, Inventory, Loot, Trade, Character, SpellBook, QuestLog, Map, Station, Menu }

    /// <summary>The pad hint bar's entries per page (button, English, Russian).</summary>
    public static class PadHints
    {
        public static IReadOnlyList<(PadButton button, string en, string ru)> For(PadHintPage page)
        {
            var list = new List<(PadButton, string, string)>();
            switch (page)
            {
                case PadHintPage.Inventory:
                    list.Add((PadButton.A, "Use", "Использовать"));
                    list.Add((PadButton.X, "To quick slot", "В быстрый слот"));
                    list.Add((PadButton.Y, "Clear slot", "Очистить слот"));
                    list.Add((PadButton.LB, "Tabs", "Вкладки"));
                    list.Add((PadButton.RT, "Quick slot", "Быстрый слот"));
                    break;
                case PadHintPage.Loot:
                    list.Add((PadButton.A, "Take stack", "Взять стопку"));
                    list.Add((PadButton.X, "Take one", "Взять один"));
                    list.Add((PadButton.Y, "Take all", "Взять всё"));
                    list.Add((PadButton.RT, "Other side", "Другая сторона"));
                    break;
                case PadHintPage.Trade:
                    list.Add((PadButton.A, "Move stack", "Переложить стопку"));
                    list.Add((PadButton.X, "Move one", "Переложить один"));
                    list.Add((PadButton.RT, "Next list", "Следующий список"));
                    break;
                case PadHintPage.Character:
                    list.Add((PadButton.RT, "Next list", "Следующий список"));
                    break;
                case PadHintPage.SpellBook:
                    list.Add((PadButton.A, "Use", "Использовать"));
                    list.Add((PadButton.X, "To quick slot", "В быстрый слот"));
                    list.Add((PadButton.Y, "Clear slot", "Очистить слот"));
                    list.Add((PadButton.RT, "Next list", "Следующий список"));
                    break;
                case PadHintPage.QuestLog:
                    list.Add((PadButton.A, "Pick place", "Выбрать место"));
                    list.Add((PadButton.LB, "Tabs", "Вкладки"));
                    list.Add((PadButton.X, "Next quest", "След. задание"));
                    list.Add((PadButton.RT, "Zoom", "Масштаб"));
                    list.Add((PadButton.RsDown, "Pan / scroll", "Сдвиг / прокрутка"));
                    break;
                case PadHintPage.Map:
                    list.Add((PadButton.LB, "Tabs", "Вкладки"));
                    list.Add((PadButton.RT, "Zoom", "Масштаб"));
                    list.Add((PadButton.RsDown, "Pan", "Сдвиг"));
                    list.Add((PadButton.R3, "Reset zoom", "Сброс масштаба"));
                    break;
                case PadHintPage.Station:
                    list.Add((PadButton.A, "Make", "Изготовить"));
                    break;
                case PadHintPage.Menu:
                    list.Add((PadButton.A, "Select", "Выбрать"));
                    list.Add((PadButton.B, "Back", "Назад"));
                    return list;
            }
            if (page != PadHintPage.Menu)
            {
                list.Add((PadButton.View, "Next page", "Следующая страница"));
                list.Add((PadButton.B, "Close", "Закрыть"));
            }
            return list;
        }
    }
}
