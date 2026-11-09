using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Simulation
{
    /// <summary>
    /// Finds the AI zone a point lies in (G3 PSNavigation current zone): the zone polygons on a coarse grid, a height
    /// band per zone. Where zones overlap, an indoor zone wins over an outdoor one, then the smallest one (a house inside
    /// a city quarter) <b>[interp]</b>. Docs/perception-crime.md §3.3.
    /// </summary>
    public sealed class ZoneMap
    {
        /// <summary>Grid cell size in metres.</summary>
        const float CellSize = 32f;

        readonly Dictionary<(int, int), List<Zone>> _cells = new Dictionary<(int, int), List<Zone>>();

        sealed class Zone
        {
            public OwnedZone Value;
            public float[] Outline;
            public float Bottom, Top, Area;
        }

        public static readonly ZoneMap Empty = new ZoneMap(Array.Empty<AiZoneDef>(), Array.Empty<EnclaveDef>());

        public ZoneMap(IEnumerable<AiZoneDef> zones, IEnumerable<EnclaveDef> enclaves)
        {
            var alignments = (enclaves ?? Array.Empty<EnclaveDef>()).ToDictionary(e => e.Id, e => e.Alignment, StringComparer.Ordinal);
            foreach (var def in zones ?? Array.Empty<AiZoneDef>())
            {
                if (def.Outline.Length < 6) continue;
                var zone = new Zone
                {
                    Value = new OwnedZone
                    {
                        Id = def.Id,
                        Name = def.Name,
                        Owner = def.Owner,
                        OwnerAlignment = def.Owner != null && alignments.TryGetValue(def.Owner, out var a) ? a : null,
                        Security = def.Security,
                        Indoor = def.Indoor,
                    },
                    Outline = def.Outline,
                    Bottom = def.Floor - def.Below,
                    Top = def.Floor + def.Above,
                    Area = Math.Abs(SignedArea(def.Outline)),
                };
                Add(zone);
                Count++;
            }
        }

        public int Count { get; }

        /// <summary>The zone at <paramref name="pose"/> (default = none).</summary>
        public OwnedZone ZoneAt(WorldPose pose)
        {
            if (!_cells.TryGetValue(CellOf(pose.X, pose.Z), out var candidates)) return default;
            Zone best = null;
            foreach (var z in candidates)
            {
                if (pose.Y < z.Bottom || pose.Y > z.Top || !Contains(z.Outline, pose.X, pose.Z)) continue;
                if (best == null || Better(z, best)) best = z;
            }
            return best?.Value ?? default;
        }

        /// <summary>The indoor zone at <paramref name="pose"/> (null = outdoors).</summary>
        public string IndoorZoneAt(WorldPose pose)
        {
            var zone = ZoneAt(pose);
            return zone.Indoor ? zone.Id : null;
        }

        static bool Better(Zone a, Zone b) => a.Value.Indoor != b.Value.Indoor ? a.Value.Indoor : a.Area < b.Area;

        void Add(Zone zone)
        {
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i < zone.Outline.Length; i += 2)
            {
                minX = Math.Min(minX, zone.Outline[i]);
                maxX = Math.Max(maxX, zone.Outline[i]);
                minZ = Math.Min(minZ, zone.Outline[i + 1]);
                maxZ = Math.Max(maxZ, zone.Outline[i + 1]);
            }
            var (x0, z0) = CellOf(minX, minZ);
            var (x1, z1) = CellOf(maxX, maxZ);
            for (int x = x0; x <= x1; x++)
            for (int z = z0; z <= z1; z++)
            {
                if (!_cells.TryGetValue((x, z), out var list)) _cells[(x, z)] = list = new List<Zone>();
                list.Add(zone);
            }
        }

        static (int, int) CellOf(float x, float z) => ((int)Math.Floor(x / CellSize), (int)Math.Floor(z / CellSize));

        /// <summary>Even-odd point in polygon test on x, z pairs.</summary>
        public static bool Contains(float[] outline, float x, float z)
        {
            bool inside = false;
            int n = outline.Length / 2;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                float xi = outline[2 * i], zi = outline[2 * i + 1], xj = outline[2 * j], zj = outline[2 * j + 1];
                if ((zi > z) != (zj > z) && x < (xj - xi) * (z - zi) / (zj - zi) + xi) inside = !inside;
            }
            return inside;
        }

        /// <summary>The average of the outline's corners (x, z); false for an empty outline.</summary>
        public static bool Centre(float[] outline, out float x, out float z)
        {
            x = z = 0;
            int n = outline == null ? 0 : outline.Length / 2;
            if (n == 0) return false;
            for (int i = 0; i < n; i++) { x += outline[2 * i]; z += outline[2 * i + 1]; }
            x /= n;
            z /= n;
            return true;
        }

        /// <summary>The distance (metres, x/z) from a point to the nearest edge of the outline.</summary>
        public static float EdgeDistance(float[] outline, float x, float z)
        {
            float best = float.MaxValue;
            int n = outline.Length / 2;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                float ax = outline[2 * j], az = outline[2 * j + 1], bx = outline[2 * i], bz = outline[2 * i + 1];
                float dx = bx - ax, dz = bz - az, len2 = dx * dx + dz * dz;
                float t = len2 > 0 ? Math.Max(0f, Math.Min(1f, ((x - ax) * dx + (z - az) * dz) / len2)) : 0f;
                float px = ax + t * dx - x, pz = az + t * dz - z;
                best = Math.Min(best, (float)Math.Sqrt(px * px + pz * pz));
            }
            return best;
        }

        static float SignedArea(float[] outline)
        {
            float sum = 0;
            int n = outline.Length / 2;
            for (int i = 0, j = n - 1; i < n; j = i++) sum += outline[2 * j] * outline[2 * i + 1] - outline[2 * i] * outline[2 * j + 1];
            return sum * 0.5f;
        }
    }
}
