using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// A marker of a map item (G3 <c>gSMapMarker</c>, Docs/map-teleport.md §1.2): a label text id and a world point in
    /// metres (x east, z north). Only visible markers are drawn; nothing changes the flag at run time.
    /// </summary>
    public sealed class MapMarkerDef
    {
        [JsonConstructor]
        public MapMarkerDef(TextId label, float x, float z, bool visible)
        {
            Label = label;
            X = x;
            Z = z;
            Visible = visible;
        }

        /// <summary>Loc string id of the label (e.g. "MAP_World_Myrtana_Ardea_01"); a missing text draws no label (G3: empty string).</summary>
        public TextId Label { get; }
        public float X { get; }
        public float Z { get; }
        public bool Visible { get; }
    }
}
