using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace G3.Formats.Remaster
{
    /// <summary>
    /// LOD layout of remastered characters (Docs/remaster-pipeline.md §10): a part with LOD files gets one renderer per
    /// level named <c>&lt;part&gt;_LOD&lt;n&gt;</c> (level 0 keeps the part's own name); parts without LODs (hair, beard,
    /// unremastered parts) show in every level. Engine-free so the plan is unit-tested.
    /// </summary>
    public static class RemasterLods
    {
        public const string Suffix = "_LOD";

        /// <summary>
        /// LODGroup transitions (screen-relative height of the character's bounds, about 2.2 m with the importer's
        /// generous skinned bounds). At 60° vertical FOV and lodBias 1: LOD0 (mid-poly) closer than ~6 m, LOD1
        /// (original count) to ~19 m, LOD2 (half) beyond; the last value only culls past ~1 km (NPCs unload earlier).
        /// </summary>
        public static readonly float[] ScreenHeights = { 0.30f, 0.10f, 0.002f };

        static readonly Regex LodName = new Regex(@"^(?<base>.+)" + Suffix + @"(?<n>\d+)$");

        /// <summary>LOD level encoded in a renderer name (0 = none / LOD0).</summary>
        public static int LevelOf(string name)
        {
            var m = LodName.Match(name ?? "");
            return m.Success ? int.Parse(m.Groups["n"].Value) : 0;
        }

        /// <summary>The part a renderer belongs to (name without the LOD suffix).</summary>
        public static string PartOf(string name)
        {
            var m = LodName.Match(name ?? "");
            return m.Success ? m.Groups["base"].Value : name ?? "";
        }

        /// <summary>
        /// Renderer names per LOD level for a set of renderer names. Each part shows its highest available level not
        /// above the LOD level (a part with only LOD1 keeps it in LOD2); parts without LODs show everywhere.
        /// Empty when no renderer carries a LOD suffix (nothing to group).
        /// </summary>
        public static List<List<string>> Plan(IEnumerable<string> rendererNames)
        {
            var levels = new Dictionary<string, SortedDictionary<int, List<string>>>(StringComparer.Ordinal);
            int max = 0;
            foreach (var n in rendererNames)
            {
                int l = LevelOf(n);
                string part = PartOf(n);
                if (!levels.TryGetValue(part, out var byLevel)) levels[part] = byLevel = new SortedDictionary<int, List<string>>();
                if (!byLevel.TryGetValue(l, out var list)) byLevel[l] = list = new List<string>();
                list.Add(n);
                max = Math.Max(max, l);
            }
            var plan = new List<List<string>>();
            if (max == 0) return plan;
            max = Math.Min(max, ScreenHeights.Length - 1);
            for (int lod = 0; lod <= max; lod++)
            {
                var names = new List<string>();
                foreach (var byLevel in levels.Values)
                {
                    int best = -1;
                    foreach (int l in byLevel.Keys) if (l <= lod) best = l;
                    if (best >= 0) names.AddRange(byLevel[best]);
                }
                plan.Add(names);
            }
            return plan;
        }
    }
}
