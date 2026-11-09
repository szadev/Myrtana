using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// A map item (G3 <c>gCMap_PS</c>, Docs/map-teleport.md §1.1): the picture it shows and the world rectangle the picture
    /// covers, in metres in the presentation's axes (x east, z north). The id is the item's id.
    /// </summary>
    public sealed class MapDef : IDefinition
    {
        [JsonConstructor]
        public MapDef(string id, string bitmap, float left, float top, float right, float bottom,
                      IReadOnlyList<MapMarkerDef> markers = null)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Bitmap = bitmap;
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
            Markers = markers ?? Array.Empty<MapMarkerDef>();
        }

        public string Id { get; }
        /// <summary>GUI image of the map, e.g. "G3_Map_World".</summary>
        public string Bitmap { get; }
        /// <summary>World x of the picture's left edge (G3 WorldTopLeft.x).</summary>
        public float Left { get; }
        /// <summary>World z of the picture's top edge (G3 WorldTopLeft.z; north).</summary>
        public float Top { get; }
        /// <summary>World x of the picture's right edge (G3 WorldBottomRight.x).</summary>
        public float Right { get; }
        /// <summary>World z of the picture's bottom edge (G3 WorldBottomRight.z).</summary>
        public float Bottom { get; }

        /// <summary>The map's markers in G3's order (gCMap_PS marker list), visible or not.</summary>
        public IReadOnlyList<MapMarkerDef> Markers { get; }

        /// <summary>The markers G3 draws (the visible ones), in drawing order.</summary>
        [JsonIgnore] public IEnumerable<MapMarkerDef> VisibleMarkers => Markers.Where(m => m != null && m.Visible);

        [JsonIgnore] public MapProjection Projection => new MapProjection(Left, Top, Right, Bottom);

        string IDefinition.Key => Id;
    }
}
