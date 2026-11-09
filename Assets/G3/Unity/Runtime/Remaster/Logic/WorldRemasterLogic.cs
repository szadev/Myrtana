using System;
using System.Collections.Generic;

namespace G3.Unity
{
    // Engine-free parts of the world remaster override (G3WorldRemaster; Docs/remaster-world.md section 4), compiled into
    // G3.Unity and into Tools/G3Tests.

    /// <summary>The old single world remaster switch (fix-69 and before; since fix-71 split into the parts of
    /// <see cref="RemasterOptions.WorldFeatures"/>): its variables still force every world part (<see cref="RemasterOptions.EnvChain"/>),
    /// its stored key is migrated once (<see cref="RemasterOptions.Migrate"/>).</summary>
    public static class WorldRemasterSwitch
    {
        public const string EnvVar = "G3_REMASTER_WORLD";
        public const string SharedEnvVar = "G3_REMASTER";
        public const string PrefKey = "G3.Remaster.World";

        public static bool? ParseFlag(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            v = v.Trim();
            if (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("on", StringComparison.OrdinalIgnoreCase)) return true;
            if (v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase) || v.Equals("off", StringComparison.OrdinalIgnoreCase)) return false;
            return null;
        }

        public static bool Resolve(string worldEnv, string sharedEnv, bool pref) => ParseFlag(worldEnv) ?? ParseFlag(sharedEnv) ?? pref;
    }

    /// <summary>Material array swap used by the runtime override (pure so it is testable without Unity objects).</summary>
    public static class WorldRemasterSwap
    {
        /// <summary>Replaces every element found in <paramref name="map"/> in place; returns how many changed.</summary>
        public static int Apply<T>(T[] items, IReadOnlyDictionary<T, T> map) where T : class
        {
            if (items == null || map == null) return 0;
            int n = 0;
            for (int i = 0; i < items.Length; i++)
                if (items[i] != null && map.TryGetValue(items[i], out var to) && to != null && !ReferenceEquals(to, items[i]))
                {
                    items[i] = to;
                    n++;
                }
            return n;
        }

        /// <summary>
        /// v2 (Docs/remaster-world.md section 10): three states per material, original O, r3 copy R (may be null) and v2
        /// copy V (may be null). The map sends every state that is not the wanted one to it: remaster off → O; on → V when
        /// <paramref name="useV2"/> says so for that triple and V exists, else R, else O. So any current scene state
        /// (original, r3 or v2) converts in one walk.
        /// </summary>
        public static Dictionary<T, T> Target<T>(IEnumerable<(T original, T r3, T v2, int kind)> triples, bool on, Func<int, bool> useV2) where T : class
        {
            var parts = new List<(T, T, T, int, int)>();
            foreach (var (o, r, v, kind) in triples) parts.Add((o, r, v, kind, 0));
            return Target(parts, _ => on, useV2);
        }

        /// <summary>
        /// Per world part (fix-71, Docs/remaster-world.md 10.15.3): the same map with "on" asked per triple's
        /// <c>part</c> (a <see cref="WorldParts"/> flag), so every part converts on its own in the same walk.
        /// </summary>
        public static Dictionary<T, T> Target<T>(IEnumerable<(T original, T r3, T v2, int kind, int part)> triples, Func<int, bool> partOn, Func<int, bool> useV2) where T : class
        {
            var d = new Dictionary<T, T>();
            foreach (var (o, r, v, kind, part) in triples)
            {
                if (o == null) continue;
                bool on = partOn != null && partOn(part);
                T want = !on ? o : (v != null && useV2 != null && useV2(kind)) ? v : (r ?? o);
                if (!ReferenceEquals(o, want)) d[o] = want;
                if (r != null && !ReferenceEquals(r, want)) d[r] = want;
                if (v != null && !ReferenceEquals(v, want)) d[v] = want;
            }
            return d;
        }

        /// <summary>original to remaster when <paramref name="on"/>, else remaster to original.</summary>
        public static Dictionary<T, T> Direction<T>(IEnumerable<(T original, T remaster)> pairs, bool on) where T : class
        {
            var d = new Dictionary<T, T>();
            foreach (var (o, r) in pairs)
            {
                if (o == null || r == null) continue;
                if (on) d[o] = r; else d[r] = o;
            }
            return d;
        }
    }
}
