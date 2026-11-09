using System.Collections.Generic;
using G3.Formats.Genome;

namespace G3.Formats.Items
{
    /// <summary>One gSMapMarker of a map item (Docs/map-teleport.md §1.2).</summary>
    public sealed class MapMarker
    {
        /// <summary>Loc string id of the label (e.g. "MAP_World_Myrtana_Ardea_01"); its text is drawn at the marker.</summary>
        public string Label;
        /// <summary>World position, centimetres (G3 axes).</summary>
        public float X, Y, Z;
        /// <summary>Only markers with this flag are drawn.</summary>
        public bool Visible;
    }

    /// <summary>
    /// The marker list in the post data of <c>gCMap_PS</c> (class version 1, Docs/map-teleport.md §1.2): i32 count, u32
    /// element size (20 = the in-memory gSMapMarker; any other size → the elements are skipped and the list is empty),
    /// then per marker: eCLocString (a string, a table index in GENOMFLE files), bCVector (3 × f32, cm), bool (1 byte).
    /// </summary>
    public static class MapMarkerList
    {
        public const ushort SupportedVersion = 1;
        public const uint ElementSize = 20;

        /// <param name="postStart">Stream offset right after the PS class version (<c>TemplateEntity.PostDataStart</c>).</param>
        /// <param name="classVersion">The PS class version; other than 1 → no markers.</param>
        public static List<MapMarker> Read(GenomeReader g, long postStart, ushort classVersion)
        {
            var markers = new List<MapMarker>();
            if (g == null || classVersion != SupportedVersion) return markers;
            long save = g.Position;
            try
            {
                g.Position = postStart;
                var r = g.R;
                int count = r.ReadInt32();
                uint size = r.ReadUInt32();
                if (size != ElementSize || count <= 0) return markers;
                for (int i = 0; i < count; i++)
                {
                    var m = new MapMarker { Label = g.Str() };
                    m.X = r.ReadSingle();
                    m.Y = r.ReadSingle();
                    m.Z = r.ReadSingle();
                    m.Visible = r.ReadByte() != 0;
                    markers.Add(m);
                }
                return markers;
            }
            finally
            {
                g.Position = save;
            }
        }
    }
}
