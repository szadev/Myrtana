using System;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Simulation
{
    /// <summary>
    /// The designer-placed navigation of one G3 world sector (Docs/navigation.md): walkable zone polygons
    /// (gCNavZone_PS) and the path ribbons that join them (gCNavPath_PS). World metres, presentation axes. The sector's
    /// entries take part in routing only while the sector is on (<see cref="WorldSectors"/>).
    /// </summary>
    public sealed class NavSectorDef : IDefinition
    {
        [JsonConstructor]
        public NavSectorDef(string id, NavZoneDef[] zones, NavPathDef[] paths)
        {
            Id = id;
            Zones = zones ?? Array.Empty<NavZoneDef>();
            Paths = paths ?? Array.Empty<NavPathDef>();
        }

        /// <summary>The world sector's name (G3 .wrldatasc entry).</summary>
        public string Id { get; }
        public NavZoneDef[] Zones { get; }
        public NavPathDef[] Paths { get; }

        string IDefinition.Key => Id;
    }

    /// <summary>G3 LinkInnerArea / LinkInnerTopArea / LinkInnerBottomArea: the object claims a point with priority.</summary>
    [Flags]
    public enum NavLinkFlags
    {
        None = 0,
        /// <summary>Within the height band.</summary>
        Inner = 1,
        /// <summary>Above the band.</summary>
        Top = 2,
        /// <summary>Below the band.</summary>
        Bottom = 4,
    }

    /// <summary>A walkable zone (G3 gCNavZone_PS): a ground polygon with a height band.</summary>
    public sealed class NavZoneDef
    {
        [JsonConstructor]
        public NavZoneDef(string id, string name, float[] points, float[] center, float radius, float top, float bottom, NavLinkFlags link)
        {
            Id = id;
            Name = name;
            Points = points ?? Array.Empty<float>();
            Center = center ?? new float[2];
            Radius = radius;
            Top = top;
            Bottom = bottom;
            Link = link;
        }

        /// <summary>The G3 entity GUID (paths name their zones by it).</summary>
        public string Id { get; }
        public string Name { get; }
        /// <summary>The polygon as x, y, z triples (world metres).</summary>
        public float[] Points { get; }
        /// <summary>The bounding circle's centre (x, z) and radius (G3 Radius / RadiusOffset): the smallest zone wins.</summary>
        public float[] Center { get; }
        public float Radius { get; }
        /// <summary>Highest point + TopToleranz and lowest point − BottomToleranz (world metres): the height band.</summary>
        public float Top { get; }
        public float Bottom { get; }
        public NavLinkFlags Link { get; }
    }

    /// <summary>
    /// A path ribbon (G3 gCNavPath_PS): a polyline with a half-width per point. Its first point belongs to zone A, its
    /// last to zone B (G3 joins zones only through paths, by these ids).
    /// </summary>
    public sealed class NavPathDef
    {
        [JsonConstructor]
        public NavPathDef(string id, float[] points, float[] radii, string zoneA, string zoneB, float top, float bottom,
                          bool unlimitedHeight, NavLinkFlags link)
        {
            Id = id;
            Points = points ?? Array.Empty<float>();
            Radii = radii ?? Array.Empty<float>();
            ZoneA = zoneA;
            ZoneB = zoneB;
            Top = top;
            Bottom = bottom;
            UnlimitedHeight = unlimitedHeight;
            Link = link;
        }

        public string Id { get; }
        /// <summary>The centre line as x, y, z triples (world metres).</summary>
        public float[] Points { get; }
        /// <summary>Half-width per point (metres).</summary>
        public float[] Radii { get; }
        /// <summary>The zone of the first / last point (zone GUIDs; null = none).</summary>
        public string ZoneA { get; }
        public string ZoneB { get; }
        public float Top { get; }
        public float Bottom { get; }
        public bool UnlimitedHeight { get; }
        public NavLinkFlags Link { get; }
    }
}
