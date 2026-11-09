#if !UNITY_5_3_OR_NEWER || UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System;
using System.Collections.Generic;
using System.Linq;

namespace Myrtana.Session.Dev
{
    /// <summary>
    /// Pure helpers of the story ops (<c>ask</c>, <c>loot</c>, story targets; Docs/story-walk.md): the menu path to a
    /// dialog entry, story target references and zone centres.
    /// </summary>
    public static class StoryScenario
    {
        /// <summary>
        /// The entries to choose, root first, to reach <paramref name="entry"/>: its parent chain ending with the entry.
        /// Stops at a missing parent or a cycle (the chain found so far is returned).
        /// </summary>
        public static IReadOnlyList<string> PathTo(string entry, Func<string, string> parentOf, int maxDepth = 16)
        {
            var path = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (string at = entry; !string.IsNullOrEmpty(at) && path.Count < maxDepth && seen.Add(at); at = parentOf(at))
                path.Add(at);
            path.Reverse();
            return path;
        }

        /// <summary>
        /// The next node of <paramref name="path"/> (root first, from <paramref name="from"/>) to choose in the current menu:
        /// the deepest node that is offered now; nodes already played (this talk or earlier) are passed. Returns the index to
        /// choose, <c>path.Count</c> when every node is done, or -1 with <paramref name="why"/> when the walk is stuck.
        /// </summary>
        public static int NextChoice(IReadOnlyList<string> path, int from, Func<string, bool> offered, Func<string, bool> done, out string why)
        {
            why = null;
            for (int i = from; i < path.Count; i++)
            {
                for (int j = path.Count - 1; j >= i; j--)
                    if (offered(path[j])) return j;
                if (done(path[i])) continue;
                why = $"'{path[i]}' not offered";
                return -1;
            }
            return path.Count;
        }

        /// <summary>Story target prefixes: a named place, an AI zone (by name), an interactive object (by entity name).</summary>
        public static readonly string[] TargetKinds = { "place", "zone", "obj" };

        /// <summary>"place:Adanos_Gate" → ("place", "Adanos_Gate"); false for other targets.</summary>
        public static bool TryParseTarget(string text, out string kind, out string name)
        {
            kind = name = null;
            if (string.IsNullOrEmpty(text)) return false;
            int colon = text.IndexOf(':');
            if (colon <= 0 || colon == text.Length - 1) return false;
            string k = text.Substring(0, colon).ToLowerInvariant();
            if (!TargetKinds.Contains(k)) return false;
            kind = k;
            name = text.Substring(colon + 1).Trim();
            return name.Length > 0;
        }

        /// <summary>
        /// A point inside a zone outline (x,z pairs): the area centroid when it lies inside the polygon, else the middle
        /// of the widest horizontal chord through the vertex-average row (concave zones). False for fewer than 3 points.
        /// </summary>
        public static bool TryInside(float[] outline, out float x, out float z)
        {
            x = z = 0;
            if (outline == null || outline.Length < 6) return false;
            int n = outline.Length / 2;
            double a = 0, cx = 0, cz = 0, ax = 0, az = 0;
            for (int i = 0; i < n; i++)
            {
                double x0 = outline[2 * i], z0 = outline[2 * i + 1];
                double x1 = outline[2 * ((i + 1) % n)], z1 = outline[2 * ((i + 1) % n) + 1];
                double cross = x0 * z1 - x1 * z0;
                a += cross;
                cx += (x0 + x1) * cross;
                cz += (z0 + z1) * cross;
                ax += x0;
                az += z0;
            }
            if (Math.Abs(a) > 1e-6)
            {
                x = (float)(cx / (3 * a));
                z = (float)(cz / (3 * a));
                if (Contains(outline, x, z)) return true;
            }
            // concave: scan the row through the vertex average for the widest inside span
            z = (float)(az / n);
            var xs = new List<double>();
            for (int i = 0; i < n; i++)
            {
                double x0 = outline[2 * i], z0 = outline[2 * i + 1];
                double x1 = outline[2 * ((i + 1) % n)], z1 = outline[2 * ((i + 1) % n) + 1];
                if (z0 > z == z1 > z) continue;
                xs.Add(x0 + (z - z0) / (z1 - z0) * (x1 - x0));
            }
            xs.Sort();
            double best = -1;
            for (int i = 0; i + 1 < xs.Count; i += 2)
                if (xs[i + 1] - xs[i] > best)
                {
                    best = xs[i + 1] - xs[i];
                    x = (float)((xs[i] + xs[i + 1]) / 2);
                }
            if (best < 0) x = (float)(ax / n);
            return true;
        }

        /// <summary>Even-odd point-in-polygon test on x,z pairs.</summary>
        public static bool Contains(float[] outline, float x, float z)
        {
            bool inside = false;
            int n = outline.Length / 2;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                float xi = outline[2 * i], zi = outline[2 * i + 1], xj = outline[2 * j], zj = outline[2 * j + 1];
                if (zi > z != zj > z && x < (xj - xi) * (z - zi) / (zj - zi) + xi) inside = !inside;
            }
            return inside;
        }

        /// <summary>"A, B,,C" → [A, B, C].</summary>
        public static IReadOnlyList<string> List(string value) =>
            (value ?? "").Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
    }
}
#endif
