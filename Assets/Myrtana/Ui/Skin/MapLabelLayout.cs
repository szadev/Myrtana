using System;
using System.Collections.Generic;

namespace Myrtana.Ui.Skin
{
    /// <summary>A map label's text box as G3 places it: centred horizontally on its point, its bottom on the point (map px).</summary>
    public readonly struct MapLabelBox
    {
        public MapLabelBox(float x, float y, float width, float height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public float X { get; }
        public float Y { get; }
        public float Width { get; }
        public float Height { get; }

        public float Left => X - Width / 2f;
        public float Right => X + Width / 2f;
        public float Top => Y - Height;
        public float Bottom => Y;
    }

    /// <summary>
    /// fix-126 [owner] ("Ben Sala and Bakaresh overlap"): the map labels stay where G3 puts them unless a label overlaps one
    /// placed before it (G3's marker order); such a label gets the smallest nudge that frees it — up or down past the
    /// other label (preferred), else sideways — at most <see cref="MaxVerticalLines"/> label heights or one label width. A
    /// label no small nudge frees (a cluster: Klan Ognya / Monastyr, Ben Sala / Bakaresh / Ben Erai) is stacked up or down
    /// past every label in the way, whichever is nearer (fix-136: it used to stay on the other one). Labels that touch
    /// count as overlapping (fix-154). Same input, same offsets.
    /// </summary>
    public static class MapLabelLayout
    {
        /// <summary>Space kept between nudged labels (px; fix-154: 1 → 2, see <see cref="Touches"/>).</summary>
        public const float Gap = 2f;
        /// <summary>The largest vertical nudge in label heights.</summary>
        public const float MaxVerticalLines = 2f;
        /// <summary>Sideways nudges count this much more than vertical ones (labels read better stacked).</summary>
        const float SidewaysCost = 1.5f;

        /// <summary>The offset (px) to add to each label's G3 position; empty boxes (no text) never move and never block.</summary>
        public static (float dx, float dy)[] Declutter(IReadOnlyList<MapLabelBox> labels)
        {
            int n = labels?.Count ?? 0;
            var offsets = new (float dx, float dy)[n];
            var placed = new List<(float l, float t, float r, float b)>(n);
            var candidates = new List<(float dx, float dy, float cost)>();
            for (int i = 0; i < n; i++)
            {
                var box = labels[i];
                if (box.Width <= 0f || box.Height <= 0f) continue;
                var at = (box.Left, box.Top, box.Right, box.Bottom);
                if (Free(at, 0f, 0f, placed))
                {
                    placed.Add(at);
                    continue;
                }
                candidates.Clear();
                float maxDy = MaxVerticalLines * box.Height, maxDx = box.Width;
                foreach (var p in placed)
                {
                    candidates.Add((0f, p.t - Gap - at.Bottom, 0f));
                    candidates.Add((0f, p.b + Gap - at.Top, 0f));
                    candidates.Add((p.l - Gap - at.Right, 0f, 0f));
                    candidates.Add((p.r + Gap - at.Left, 0f, 0f));
                }
                (float dx, float dy) best = (0f, 0f);
                float bestCost = float.MaxValue;
                foreach (var (dx, dy, _) in candidates)
                {
                    if (Math.Abs(dy) > maxDy || Math.Abs(dx) > maxDx) continue;
                    float cost = Math.Abs(dy) + SidewaysCost * Math.Abs(dx);
                    if (cost >= bestCost || !Free(at, dx, dy, placed)) continue;
                    best = (dx, dy);
                    bestCost = cost;
                }
                if (bestCost == float.MaxValue) best = (0f, Stack(at, placed));
                offsets[i] = best;
                placed.Add((at.Left + best.dx, at.Top + best.dy, at.Right + best.dx, at.Bottom + best.dy));
            }
            return offsets;
        }

        /// <summary>fix-136: the nearer of the vertical offsets that stack the box above / below every placed label in its way.</summary>
        static float Stack((float l, float t, float r, float b) box, List<(float l, float t, float r, float b)> placed)
        {
            float up = Cascade(box, placed, -1), down = Cascade(box, placed, 1);
            return Math.Abs(up) <= Math.Abs(down) ? up : down;
        }

        /// <summary>Moves the box one way (dir −1 up, +1 down) past each placed label it meets until it is free; each placed
        /// label is passed at most once, so this ends.</summary>
        static float Cascade((float l, float t, float r, float b) box, List<(float l, float t, float r, float b)> placed, int dir)
        {
            float dy = 0f;
            for (int guard = 0; guard <= placed.Count; guard++)
            {
                bool moved = false;
                foreach (var p in placed)
                {
                    if (!Touches(box.l, box.t + dy, box.r, box.b + dy, p)) continue;
                    dy = dir < 0 ? p.t - Gap - box.b : p.b + Gap - box.t;
                    moved = true;
                    break;
                }
                if (!moved) return dy;
            }
            return dy;
        }

        static bool Free((float l, float t, float r, float b) box, float dx, float dy, List<(float l, float t, float r, float b)> placed)
        {
            float l = box.l + dx, t = box.t + dy, r = box.r + dx, b = box.b + dy;
            foreach (var p in placed)
                if (Touches(l, t, r, b, p)) return false;
            return true;
        }

        /// <summary>
        /// fix-154: closer than half the <see cref="Gap"/> counts as overlapping. Layout snaps each label to device pixels
        /// (up to ~0.36 px each at 1080/768): Klan Ognya and Monastyr, 0.64 px apart on paper, came out touching (the probe's
        /// overlap) and were never nudged. A nudged label keeps the whole gap, so it is clear of this test.
        /// </summary>
        static bool Touches(float l, float t, float r, float b, (float l, float t, float r, float b) p)
        {
            const float Clear = Gap / 2f;
            return l < p.r + Clear && p.l < r + Clear && t < p.b + Clear && p.t < b + Clear;
        }
    }
}
