using System;
using System.Collections.Generic;
using System.Linq;

namespace Myrtana.Session
{
    /// <summary>
    /// Places an extra button into a vertical column of layout buttons (the options page's Performance / Video / Audio /
    /// Controls / Difficulty, 32 px tall and 37 px apart): the new button takes the place right below <c>after</c> (else
    /// it goes last) and the whole column is fitted into the span the original buttons cover (fix-72: the options page's
    /// button frame is part of G3_HUD_View_Menu_Options_Back.tga, so a column that grows past the last original button
    /// hangs over the frame's bottom edge). The buttons keep their height while it fits with at least <c>minGap</c> px
    /// between them, else they get flatter; the step shrinks to match. Null when they would be flatter than
    /// <see cref="MinHeight"/>.
    /// </summary>
    public static class MenuButtonColumn
    {
        /// <summary>A button of the column: name, top and height (layout pixels).</summary>
        public readonly struct Slot
        {
            public Slot(string name, float top, float height)
            {
                Name = name;
                Top = top;
                Height = height;
            }

            public string Name { get; }
            public float Top { get; }
            public float Height { get; }
        }

        /// <summary>The fitted column: each button's top (layout pixels) and the common button height.</summary>
        public sealed class Placement
        {
            public Placement(IReadOnlyDictionary<string, float> tops, float height)
            {
                Tops = tops;
                Height = height;
            }

            public IReadOnlyDictionary<string, float> Tops { get; }
            public float Height { get; }
        }

        /// <summary>Flattest button the column accepts (layout pixels): below it the text no longer fits.</summary>
        public const float MinHeight = 24f;

        /// <summary>
        /// The column with <paramref name="added"/> inserted below <paramref name="after"/> (existing names keep their order),
        /// fitted into the original span (first top to last bottom); null when it does not fit.
        /// </summary>
        public static Placement Insert(IReadOnlyList<Slot> column, string after, string added, float minGap = 2f) =>
            Fit(column, added, minGap, names =>
            {
                int at = names.IndexOf(after);
                return at >= 0 ? at + 1 : names.Count;
            });

        /// <summary>
        /// The column with <paramref name="added"/> inserted above <paramref name="before"/> (else first), fitted into the
        /// original span like <see cref="Insert"/> (fix-138: Continue above the main menu's first button; the "Gothic" logo
        /// lies right above that column, so it may not grow upwards).
        /// </summary>
        public static Placement InsertBefore(IReadOnlyList<Slot> column, string before, string added, float minGap = 2f) =>
            Fit(column, added, minGap, names => Math.Max(0, names.IndexOf(before)));

        static Placement Fit(IReadOnlyList<Slot> column, string added, float minGap, Func<List<string>, int> index)
        {
            if (column == null || column.Count == 0 || string.IsNullOrEmpty(added)) return null;
            var sorted = column.OrderBy(s => s.Top).ToList();
            float height = sorted[0].Height;
            float first = sorted[0].Top;
            float span = sorted[sorted.Count - 1].Top + sorted[sorted.Count - 1].Height - first;
            var names = sorted.Select(s => s.Name).ToList();
            names.Insert(index(names), added);
            int n = names.Count;
            float h = Math.Min(height, (float)Math.Floor((span - (n - 1) * minGap) / n));
            if (h < MinHeight) return null;
            float pitch = n > 1 ? (span - h) / (n - 1) : 0f;
            var tops = new Dictionary<string, float>(StringComparer.Ordinal);
            for (int i = 0; i < n; i++) tops[names[i]] = (float)Math.Round(first + i * pitch);
            return new Placement(tops, h);
        }
    }
}
