using System;
using System.Collections.Generic;
using System.Linq;
using G3.Formats.Animation;
using G3.Formats.Genome;
using G3.Formats.Items;
using G3.Formats.World;
using Myrtana.Simulation;

namespace Myrtana.Import.G3
{
    /// <summary>
    /// The designer-placed navigation of the world (Docs/navigation.md): every gCNavZone_PS (zone polygon + height band)
    /// and gCNavPath_PS (path ribbon + the ids of the zones at its ends) of the .node / .lrentdat files, per world sector,
    /// in world metres. Sectors that are off at the start are included (their entities are marked disabled until the story
    /// switches them on); developer test sectors are not. gCNegZone_PS would be read here too, but the CP 1.75 world has none.
    /// </summary>
    public sealed class G3NavImporter
    {
        const string Volume = "Projects_compiled";
        const float Cm = 0.01f;

        public List<NavSectorDef> Sectors { get; } = new List<NavSectorDef>();
        public int ZoneCount => Sectors.Sum(s => s.Zones.Length);
        public int PathCount => Sectors.Sum(s => s.Paths.Length);
        /// <summary>gCNegZone_PS entities seen (none in CP 1.75).</summary>
        public int NegZoneCount { get; private set; }

        public void Import(G3Install install, ImportReport report)
        {
            using (var fs = install.Open(Volume))
                Import(fs.Files.Select(f => f.Path), p => fs.Get(Volume + "/" + p)?.Read(), report);
        }

        /// <param name="files">Projects_compiled pak paths.</param>
        /// <param name="read">Bytes of a Projects_compiled pak path.</param>
        public void Import(IEnumerable<string> files, Func<string, byte[]> read, ImportReport report)
        {
            var layout = WorldLayout.Build(files, read, includeDisabled: true);
            var seen = new HashSet<Guid>();
            foreach (var sector in layout.Sectors.Where(s => !NpcSpawns.IsTestSector(s.Name)))
            {
                var zones = new List<NavZoneDef>();
                var paths = new List<NavPathDef>();
                foreach (var path in sector.Lrentdats.Concat(sector.Nodes))
                {
                    List<WorldEntity> list;
                    try { list = EntityArchive.Read(read(path), false, keepRaw: true); }
                    catch (Exception ex) when (ex is System.IO.InvalidDataException || ex is ArgumentException || ex is NullReferenceException)
                    {
                        report?.Count("unreadable world entity file (navigation)", path + ": " + ex.Message);
                        continue;
                    }
                    if (list == null) continue;
                    foreach (var e in list)
                    {
                        if (e.RawPropertySets == null || (!e.Enabled && sector.Enabled)) continue;
                        foreach (var ps in e.RawPropertySets)
                        {
                            if (ps.ClassName == "gCNegZone_PS") NegZoneCount++;
                            if (ps.ClassName != "gCNavZone_PS" && ps.ClassName != "gCNavPath_PS") continue;
                            if (!seen.Add(e.Guid)) continue;
                            if (ps.ClassName == "gCNavZone_PS")
                            {
                                var z = Zone(e, ps);
                                if (z != null) zones.Add(z);
                                else report?.Count("nav zone without an outline", e.Name);
                            }
                            else
                            {
                                var p = Path(e, ps);
                                if (p != null) paths.Add(p);
                                else report?.Count("nav path with fewer than 2 points", e.Name);
                            }
                        }
                    }
                }
                if (zones.Count + paths.Count > 0)
                    Sectors.Add(new NavSectorDef(sector.Name, zones.OrderBy(z => z.Id, StringComparer.Ordinal).ToArray(),
                                                 paths.OrderBy(p => p.Id, StringComparer.Ordinal).ToArray()));
            }
            Sectors.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            report?.Note($"navigation: {ZoneCount} zones, {PathCount} paths, {NegZoneCount} neg zones in {Sectors.Count} sectors");
        }

        static NavZoneDef Zone(WorldEntity e, PropertyObject ps)
        {
            var pts = World(e.World, Points(ps.Get("Point")?.Value));
            if (pts.Length < 9) return null;
            Band(pts, out float minY, out float maxY);
            var c = Transform(e.World, Props.Vec3(ps, "RadiusOffset") ?? new float[3]);
            float radius = 0;
            for (int i = 0; i < pts.Length; i += 3)
                radius = Math.Max(radius, (float)Math.Sqrt(Sq(pts[i] - c[0]) + Sq(pts[i + 2] - c[2])));
            return new NavZoneDef(e.Guid.ToString("D"), e.Name, pts, new[] { c[0], c[2] }, radius + 0.01f,
                                  maxY + Props.Float(ps, "TopToleranz", 150) * Cm, minY - Props.Float(ps, "BottomToleranz", 100) * Cm, Link(ps));
        }

        static NavPathDef Path(WorldEntity e, PropertyObject ps)
        {
            var pts = World(e.World, Points(ps.Get("Point")?.Value));
            if (pts.Length < 6) return null;
            var radii = Floats(ps.Get("Radius")?.Value);
            var r = new float[pts.Length / 3];
            for (int i = 0; i < r.Length; i++) r[i] = (i < radii.Count ? radii[i] : 20f) * Cm;
            Band(pts, out float minY, out float maxY);
            return new NavPathDef(e.Guid.ToString("D"), pts, r, ZoneId(ps.Get("ZoneAEntityID")?.Value), ZoneId(ps.Get("ZoneBEntityID")?.Value),
                                  maxY + Props.Float(ps, "TopToleranz", 150) * Cm, minY - Props.Float(ps, "BottomToleranz", 100) * Cm,
                                  Props.Bool(ps, "UnlimitedHeight"), Link(ps));
        }

        static NavLinkFlags Link(PropertyObject ps) =>
            (Props.Bool(ps, "LinkInnerArea") ? NavLinkFlags.Inner : 0) | (Props.Bool(ps, "LinkInnerTopArea") ? NavLinkFlags.Top : 0) |
            (Props.Bool(ps, "LinkInnerBottomArea") ? NavLinkFlags.Bottom : 0);

        static void Band(float[] pts, out float minY, out float maxY)
        {
            minY = float.MaxValue;
            maxY = float.MinValue;
            for (int i = 1; i < pts.Length; i += 3)
            {
                minY = Math.Min(minY, pts[i]);
                maxY = Math.Max(maxY, pts[i]);
            }
        }

        static float Sq(float v) => v * v;

        /// <summary>Entity-local centimetres → world metres (G3 row-vector matrix, translation in 12..14).</summary>
        static float[] Transform(float[] m, float[] p) => new[]
        {
            (p[0] * m[0] + p[1] * m[4] + p[2] * m[8] + m[12]) * Cm,
            (p[0] * m[1] + p[1] * m[5] + p[2] * m[9] + m[13]) * Cm,
            (p[0] * m[2] + p[1] * m[6] + p[2] * m[10] + m[14]) * Cm,
        };

        static float[] World(float[] m, List<float[]> local)
        {
            var w = new float[local.Count * 3];
            for (int i = 0; i < local.Count; i++)
            {
                var p = Transform(m, local[i]);
                w[3 * i] = p[0];
                w[3 * i + 1] = p[1];
                w[3 * i + 2] = p[2];
            }
            return w;
        }

        /// <summary>bTValArray&lt;bCVector&gt;: u8 version, u32 count, then x, y, z per point.</summary>
        static List<float[]> Points(byte[] v)
        {
            var points = new List<float[]>();
            if (v == null || v.Length < 5) return points;
            int n = BitConverter.ToInt32(v, 1);
            for (int i = 0; i < n && 5 + 12 * i + 12 <= v.Length; i++)
                points.Add(new[] { BitConverter.ToSingle(v, 5 + 12 * i), BitConverter.ToSingle(v, 9 + 12 * i), BitConverter.ToSingle(v, 13 + 12 * i) });
            return points;
        }

        /// <summary>bTValArray&lt;float&gt;: u8 version, u32 count, then the floats.</summary>
        static List<float> Floats(byte[] v)
        {
            var list = new List<float>();
            if (v == null || v.Length < 5) return list;
            int n = BitConverter.ToInt32(v, 1);
            for (int i = 0; i < n && 5 + 4 * i + 4 <= v.Length; i++) list.Add(BitConverter.ToSingle(v, 5 + 4 * i));
            return list;
        }

        /// <summary>bCPropertyID: a 16-byte GUID + u32; null when empty.</summary>
        static string ZoneId(byte[] v)
        {
            if (v == null || v.Length < 16) return null;
            var b = new byte[16];
            Array.Copy(v, b, 16);
            var g = new Guid(b);
            return g == Guid.Empty ? null : g.ToString("D");
        }
    }
}
