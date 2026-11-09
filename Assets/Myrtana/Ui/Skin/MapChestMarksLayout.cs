using System;
using System.Collections.Generic;

namespace Myrtana.Ui.Skin
{
    /// <summary>A group of locked-chest marks drawn as one: its centre (map picture px) and how many chests it stands for.</summary>
    public readonly struct MapMarkGroup
    {
        public MapMarkGroup(float x, float y, int count)
        {
            X = x;
            Y = y;
            Count = count;
        }

        public float X { get; }
        public float Y { get; }
        public int Count { get; }
    }

    /// <summary>
    /// How the port's locked-chest marks look on the map page (fix-102, fix-116 [owner]: "shown only by a filter";
    /// Docs/map-teleport.md §2.3): hidden by default, shown by the chest button of the map's filter bar (fix-126,
    /// <see cref="MapFilterBarLayout"/>, inside the map's top-right corner); small, slightly transparent icons; chests a few
    /// pixels apart are drawn as one mark with a count.
    /// </summary>
    public static class MapChestMarksLayout
    {
        /// <summary>The filter's state on a new install: the marks are hidden ([owner], fix-116).</summary>
        public static bool ShownByDefault => MapFilterBarLayout.Default(MapFilter.Chests);

        /// <summary>Mark icon edge (reference px; G3's map markers and arrow are 32).</summary>
        public const float IconSize = 13f;
        /// <summary>Mark opacity: the map stays readable under the marks.</summary>
        public const float Opacity = 0.8f;
        /// <summary>Marks whose centres lie closer than this (map picture px) to a group's centre join that group.</summary>
        public const float GroupRadius = 10f;

        /// <summary>The map view's size in reference px (<c>IDC_VIEW_MAP_2</c> 22,35–598,603): grouping before layout.</summary>
        public const float MapWidth = 576f, MapHeight = 568f;

        /// <summary>
        /// Groups points (map picture px) greedily in the given order: a point joins the first group whose centre (the mean
        /// of its points) lies within <paramref name="radius"/>, else starts a new group. Same input order, same groups.
        /// </summary>
        public static List<MapMarkGroup> Group(IReadOnlyList<(float x, float y)> points, float radius = GroupRadius)
        {
            var groups = GroupIndices(points, radius);
            var result = new List<MapMarkGroup>(groups.Count);
            foreach (var (group, _) in groups) result.Add(group);
            return result;
        }

        /// <summary>
        /// <see cref="Group"/> with the indices (into <paramref name="points"/>) each group stands for, in input order (fix-126:
        /// the quest marks name the people of a group and light up the selected quest's).
        /// </summary>
        public static List<(MapMarkGroup group, List<int> members)> GroupIndices(IReadOnlyList<(float x, float y)> points, float radius = GroupRadius)
        {
            var sums = new List<(double x, double y, List<int> members)>();
            if (points != null)
            {
                double r2 = (double)radius * radius;
                for (int p = 0; p < points.Count; p++)
                {
                    var (x, y) = points[p];
                    int at = -1;
                    for (int i = 0; i < sums.Count && at < 0; i++)
                    {
                        int n = sums[i].members.Count;
                        double dx = sums[i].x / n - x, dy = sums[i].y / n - y;
                        if (dx * dx + dy * dy <= r2) at = i;
                    }
                    if (at < 0) sums.Add((x, y, new List<int> { p }));
                    else
                    {
                        sums[at].members.Add(p);
                        sums[at] = (sums[at].x + x, sums[at].y + y, sums[at].members);
                    }
                }
            }
            var groups = new List<(MapMarkGroup, List<int>)>(sums.Count);
            foreach (var s in sums)
            {
                int n = s.members.Count;
                groups.Add((new MapMarkGroup((float)(s.x / n), (float)(s.y / n), n), s.members));
            }
            return groups;
        }

        /// <summary>The count text of a group: none for a single chest, "9+" above nine.</summary>
        public static string CountText(int count) => count <= 1 ? string.Empty : count > 9 ? "9+" : count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
