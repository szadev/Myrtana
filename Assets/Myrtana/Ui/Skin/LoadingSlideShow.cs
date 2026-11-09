using System;

namespace Myrtana.Ui.Skin
{
    /// <summary>
    /// Slides of the G3 loading screen (Docs/formats-gui.md §6.12): the slides <c>G3_HUD_Slide_00…</c> are shuffled
    /// once per load and walked through with the progress (0…100, only growing), so a full load shows every slide once,
    /// each for 100 / count percent (30 slides: ≈3.4 %).
    /// </summary>
    public sealed class LoadingSlideShow
    {
        /// <summary>The bar's range (the original progress control is 0…100).</summary>
        public const int Range = 100;

        readonly int[] _order;

        public LoadingSlideShow(int slideCount, Random random)
        {
            _order = new int[Math.Max(0, slideCount)];
            for (int i = 0; i < _order.Length; i++) _order[i] = i;
            for (int i = _order.Length - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (_order[i], _order[j]) = (_order[j], _order[i]);
            }
        }

        public int Count => _order.Length;
        /// <summary>Bar position 0…<see cref="Range"/>.</summary>
        public int Position { get; private set; }

        /// <summary>Slide number to show now, −1 without slides.</summary>
        public int Current => _order.Length == 0 ? -1 : _order[_order.Length * Position / (Range + 1) % _order.Length];

        /// <summary>Moves the bar to <paramref name="progress"/> (0…1; smaller values are ignored). True when the slide
        /// changed.</summary>
        public bool Advance(float progress)
        {
            int pos = (int)Math.Round(Math.Max(0f, Math.Min(1f, progress)) * Range);
            if (pos <= Position) return false;
            int before = Current;
            Position = pos;
            return Current != before;
        }

        /// <summary>Image name of slide <paramref name="number"/> ("G3_HUD_Slide_07.tga").</summary>
        public static string SlideName(int number) => $"G3_HUD_Slide_{number:00}.tga";
    }
}
