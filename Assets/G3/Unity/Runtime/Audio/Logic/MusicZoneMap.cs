using System;
using System.Collections.Generic;

namespace G3.Audio
{
    /// <summary>eEWeatherZoneShape</summary>
    public enum ZoneShape { Circle2D = 0, Rect2D = 1, Sphere3D = 2, Box3D = 3 }

    /// <summary>
    /// One eCWeatherZone_PS (= G3 music zone) in metres. Axes are the entity's unit local axes; extents are half sizes
    /// along them [inferred]. Docs/formats-audio.md §5.1.
    /// </summary>
    [Serializable]
    public sealed class MusicZoneData
    {
        public string name;
        public bool enabled = true;
        public ZoneShape shape;
        public V3 centre, axisX = new V3(1, 0, 0), axisY = new V3(0, 1, 0), axisZ = new V3(0, 0, 1);
        public float innerRadius, outerRadius;
        public V3 innerExtents, outerExtents;
        public string weatherEnvironment, musicLocation;

        /// <summary>1 inside the inner shape, 0 outside the outer one, linear in between; boxes per axis, min-combined [inferred].</summary>
        public float BlendFactor(V3 p)
        {
            var d = p - centre;
            switch (shape)
            {
                case ZoneShape.Circle2D: return Ramp((float)Math.Sqrt(d.x * d.x + d.z * d.z), innerRadius, outerRadius);
                case ZoneShape.Sphere3D: return Ramp(d.Length, innerRadius, outerRadius);
                default:
                    float f = Ramp(Math.Abs(d.Dot(axisX)), innerExtents.x, outerExtents.x);
                    f = Math.Min(f, Ramp(Math.Abs(d.Dot(axisZ)), innerExtents.z, outerExtents.z));
                    if (shape == ZoneShape.Box3D) f = Math.Min(f, Ramp(Math.Abs(d.Dot(axisY)), innerExtents.y, outerExtents.y));
                    return f;
            }
        }

        /// <summary>Horizontal size used to prefer the more specific of two equally strong zones.</summary>
        public float Size => shape == ZoneShape.Circle2D || shape == ZoneShape.Sphere3D ? outerRadius : Math.Max(outerExtents.x, outerExtents.z);

        static float Ramp(float d, float inner, float outer)
        {
            if (d <= inner) return 1f;
            if (d >= outer) return 0f;
            return (outer - d) / (outer - inner);
        }

        public override string ToString() => $"{name} [{musicLocation}]";
    }

    /// <summary>
    /// Finds the music zone at a point. Zones nest (region circle → town / landmark → dungeon box) and the most
    /// specific one the player is in wins: the smallest zone whose blend factor reaches <see cref="EnterFactor"/>
    /// (past the middle of its fade ramp), ties → the stronger factor. The zone of the last call keeps winning while
    /// its factor stays above <see cref="StayFactor"/>, so walking along a ramp does not flip the music. No zone that
    /// far in: the strongest one [inferred from G3's nesting; there is no priority field].
    /// fix-245: "highest factor, ties → smaller" let the world-wide Myrtana circle (factor 1 everywhere) beat the
    /// Varant / Nordmar region circles on their whole 100–200 m fades and towns like Braga on theirs, so the Myrtana
    /// music kept playing on the Varant sand north of Braga.
    /// </summary>
    public sealed class MusicZoneMap
    {
        public const float EnterFactor = 0.5f, StayFactor = 0.25f;

        readonly List<MusicZoneData> _zones = new List<MusicZoneData>();
        MusicZoneData _last;

        public MusicZoneMap(IEnumerable<MusicZoneData> zones)
        {
            if (zones == null) return;
            foreach (var z in zones)
                if (z != null && z.enabled && !string.IsNullOrEmpty(z.musicLocation)) _zones.Add(z);
        }

        public int Count => _zones.Count;

        /// <summary>The zone at <paramref name="p"/>, remembering it for the hysteresis of the next call.</summary>
        public MusicZoneData ZoneAt(V3 p) => _last = Pick(p, _last);

        /// <summary>Stateless pick; <paramref name="current"/> = the zone that won last time (or null).</summary>
        public MusicZoneData Pick(V3 p, MusicZoneData current)
        {
            MusicZoneData best = null, strongest = null;
            float bestF = 0f, bestSize = float.MaxValue, strongF = 0f, strongSize = float.MaxValue;
            foreach (var z in _zones)
            {
                float f = z.BlendFactor(p);
                if (f <= 0f) continue;
                float size = z.Size;
                if (f > strongF || (f == strongF && size < strongSize)) { strongest = z; strongF = f; strongSize = size; }
                bool inside = f >= EnterFactor || (ReferenceEquals(z, current) && f > StayFactor);
                if (inside && (size < bestSize || (size == bestSize && f > bestF))) { best = z; bestF = f; bestSize = size; }
            }
            return best ?? strongest;
        }
    }
}
