using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace G3.Effects
{
    /// <summary>One live effect as the effect runtime holds it (for <see cref="EffectBudgetReport"/>).</summary>
    public readonly struct LiveEffectInfo
    {
        public readonly string Name;
        public readonly int Parts, Pending;
        public readonly bool Stopped, Follows, TargetGone;
        public readonly float Age, MaxSeconds;

        public LiveEffectInfo(string name, int parts, int pending, bool stopped, bool follows, bool targetGone, float age, float maxSeconds)
        {
            Name = name; Parts = parts; Pending = pending; Stopped = stopped; Follows = follows; TargetGone = targetGone;
            Age = age; MaxSeconds = maxSeconds;
        }
    }

    /// <summary>
    /// fix-58: the live effects grouped by name for the dev command <c>fx</c> (who holds the part budget): per name the
    /// effects, their parts, how many are stopped (fading), never stop on their own (no time limit, no target: a caller must
    /// stop them), and the oldest age. Sorted by parts, most first. Pure.
    /// </summary>
    public static class EffectBudgetReport
    {
        public static string Build(IEnumerable<LiveEffectInfo> live, int partBudget, int top = 30)
        {
            var list = live?.ToList() ?? new List<LiveEffectInfo>();
            int parts = list.Sum(e => e.Parts);
            var sb = new StringBuilder();
            sb.Append(string.Format(CultureInfo.InvariantCulture, "effects: {0} live, {1}/{2} parts", list.Count, parts, partBudget));
            var groups = list.GroupBy(e => e.Name ?? "?")
                .Select(g => new
                {
                    Name = g.Key, Count = g.Count(), Parts = g.Sum(e => e.Parts), Stopped = g.Count(e => e.Stopped),
                    Endless = g.Count(e => !e.Stopped && e.MaxSeconds <= 0f && !e.Follows), Gone = g.Count(e => e.TargetGone),
                    Oldest = g.Max(e => e.Age),
                })
                .OrderByDescending(g => g.Parts).ThenBy(g => g.Name, StringComparer.Ordinal).ToList();
            foreach (var g in groups.Take(Math.Max(1, top)))
                sb.Append(string.Format(CultureInfo.InvariantCulture, "\n  {0}: {1} x, {2} parts, {3} stopped, {4} endless, {5} target gone, oldest {6:0} s",
                                        g.Name, g.Count, g.Parts, g.Stopped, g.Endless, g.Gone, g.Oldest));
            if (groups.Count > top) sb.Append(string.Format(CultureInfo.InvariantCulture, "\n  ... {0} more names", groups.Count - top));
            return sb.ToString();
        }
    }
}
