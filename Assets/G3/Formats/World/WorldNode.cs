using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using G3.Formats.Genome;

namespace G3.Formats.World
{
    public enum VisualKind { None, Mesh, SpeedTree }

    /// <summary>A placed entity from a .node / .lrentdat archive, reduced to what the static scene needs.</summary>
    public sealed class WorldEntity
    {
        public Guid Guid;
        public string Name;
        public bool Enabled, RenderingEnabled;
        /// <summary>Row-major, row vectors (rows 0..2 = axes, row 3 = translation), centimetres.</summary>
        public float[] World = new float[16];
        /// <summary>Matrix relative to the parent entity (same layout as World); only filled when read with keepRaw. Additive.</summary>
        public float[] Local;
        /// <summary>World-space AABB of the visual (min xyz, max xyz), cm. Terrain cells have identity matrices, so use this.</summary>
        public float[] Bounds = new float[6];

        public VisualKind Visual;
        public string Resource;          // .xcmsh / .xlmsh / .spt file name
        public int MaterialSwitch;
        public bool IsNpc, IsItem;       // skipped by the static scene
        public PointLight Light;
        public Vegetation Vegetation;
        public bool IsWater;
        /// <summary>gCWaterZone_PS values (null when not a water zone). Additive (swimming, Docs/hero.md "Water").</summary>
        public WaterZoneInfo Water;
        /// <summary>eCCollisionShape_PS shapes (null when the entity has none). Additive; see CollisionShape.</summary>
        public List<CollisionShape> CollisionShapes;
        /// <summary>All property sets, only filled when the archive is read with keepRaw.</summary>
        public List<PropertyObject> RawPropertySets;
        /// <summary>keepRaw only: stream offset right after each raw PS's class version (start of its post data), parallel to RawPropertySets. Additive (items: gCInventory_PS stacks).</summary>
        public List<long> RawPostDataStart;
        public GenomeReader Source;
        /// <summary>Index of the parent entity in the same archive (-1 = none / root). Additive (characters).</summary>
        public int Parent = -1;
        /// <summary>Creator template GUID (.lrentdat / .node), Guid.Empty when absent. Additive (characters).</summary>
        public Guid Creator;

        public float X => World[12];
        public float Y => World[13];
        public float Z => World[14];
    }

    /// <summary>
    /// gCWaterZone_PS (306 entities on water meshes: oceans, rivers, lakes). World space, centimetres, G3 axes. The water
    /// surface is the plane through <see cref="Centre"/> with normal <see cref="Normal"/>; the zone covers the mesh's
    /// footprint inside [MinX, MaxX] × [MinZ, MaxZ] and within sqrt(<see cref="SquareRadius"/>) of the centre (2D).
    /// </summary>
    public sealed class WaterZoneInfo
    {
        public float[] Normal = { 0, 1, 0 };
        /// <summary>RadiusOffset: the zone centre, on the water surface.</summary>
        public float[] Centre = new float[3];
        public float SquareRadius;
        public float MinX, MaxX, MinZ, MaxZ;

        public static WaterZoneInfo Read(PropertyObject ps)
        {
            var w = new WaterZoneInfo();
            Vec(ps.Get("FaceNormal"), w.Normal);
            Vec(ps.Get("RadiusOffset"), w.Centre);
            w.SquareRadius = ps.Get("SquareRadius")?.AsFloat() ?? 0;
            w.MinX = ps.Get("MinX")?.AsFloat() ?? 0;
            w.MaxX = ps.Get("MaxX")?.AsFloat() ?? 0;
            w.MinZ = ps.Get("MinZ")?.AsFloat() ?? 0;
            w.MaxZ = ps.Get("MaxZ")?.AsFloat() ?? 0;
            return w;
        }

        static void Vec(Property p, float[] v)
        {
            if (p?.Value == null || p.Value.Length < 12) return;
            for (int i = 0; i < 3; i++) v[i] = BitConverter.ToSingle(p.Value, i * 4);
        }
    }

    /// <summary>eECollisionShapeType</summary>
    public enum CollisionShapeType { None = 0, TriMesh = 1, Plane = 2, Box = 3, Capsule = 4, Sphere = 5, ConvexHull = 6, Point = 7 }

    /// <summary>
    /// One eCCollisionShape of an entity's eCCollisionShape_PS (g3dit eCCollisionShape.java). All values local to the
    /// entity, centimetres, G3 axes. Axes = 3 row vectors (x, y, z axis) of the shape orientation.
    /// </summary>
    public sealed class CollisionShape
    {
        public CollisionShapeType Type;
        public int Group;                 // eEShapeGroup (1 Static, 2 Dynamic, 9 Tree_Trunk, 10 Tree_Branches, 19 Trigger, 20 Door …)
        public int Material;              // eEShapeMaterial
        public bool DisableCollision, IgnoredByTraceRay, DisableResponse, IsTrigger;
        public string File;               // TriMesh/ConvexHull: .xnvmsh file name
        public int ResourceIndex;         // TriMesh/ConvexHull: sub-mesh index inside the .xnvmsh
        public float[] Center = new float[3];
        public float[] Extent = new float[3];   // Box: half extents
        public float[] Axes = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
        public float Radius, Height;      // Sphere/Capsule (capsule height = cylinder part, along Axes row 1)
        public float[] LocalBounds = new float[6]; // OuterAABBLocal
    }

    public sealed class PointLight
    {
        public float R, G, B;
        public float Range;              // cm
        public float OffX, OffY, OffZ;   // local offset
        public bool CastShadows;
    }

    public sealed class VegetationMesh
    {
        public int Index;
        public string MeshFile;          // basename of MeshFilePath
        public string DiffuseTexture;    // file name as stored (".dds" etc.)
        public bool DoubleSided;
        public float WindStrength = 1;   // eCVegetation_Mesh WindStrength (multiplier of the global wind)
        public float[] Positions, Normals, Uvs;
        public uint[] Indices;
    }

    public struct VegetationPlant
    {
        public ushort MeshIndex;
        public float X, Y, Z;            // world space, cm
        public float Qx, Qy, Qz, Qw;
        public float ScaleWidth, ScaleHeight;
        public uint Color;               // 0xAARRGGBB
    }

    public sealed class Vegetation
    {
        public readonly Dictionary<int, VegetationMesh> Meshes = new Dictionary<int, VegetationMesh>();
        public readonly List<VegetationPlant> Plants = new List<VegetationPlant>();
        public float ViewRange = 5000;
    }

    /// <summary>
    /// Entity archives (.node = eCSpatialEntity, .lrentdat = eCDynamicEntity). Docs/formats-world.md §4–5.
    /// </summary>
    public static class EntityArchive
    {
        const uint DeadC0de = 0xDEADC0DE;

        /// <summary>Returns null for empty files and legacy (non-GENOMFLE) archives, which are dead data.</summary>
        public static List<WorldEntity> Read(byte[] data, bool decodeVegetation = true, bool keepRaw = false)
        {
            if (data == null || data.Length < 22) return null;
            var g = new GenomeReader(data);
            if (!g.IsGenomFile) return null;
            bool lrentdat = Encoding.ASCII.GetString(data, 14, 8) == "GENOMEDL";
            if (lrentdat) g.Skip(8);
            ushort ver = g.R.ReadUInt16();
            if (ver != 83) throw new InvalidDataException($"entity archive version {ver}");
            if (lrentdat)
            {
                // eCEntityDynamicContext: its size field runs to EOF, so parse the post data explicitly.
                g.ReadObject();
                g.R.ReadByte(); g.R.ReadSingle(); g.R.ReadSingle(); g.Skip(24);
            }

            int count = g.R.ReadInt32();
            var list = new List<WorldEntity>(count);
            for (int i = 0; i < count; i++)
            {
                var e = new WorldEntity();
                if (lrentdat)
                {
                    g.R.ReadUInt16(); g.R.ReadUInt16();
                    if (g.R.ReadByte() != 0) e.Creator = g.Guid().Value;
                    ReadEntity(g, e, decodeVegetation, keepRaw);
                }
                else
                {
                    bool hasCreator = g.R.ReadByte() != 0, disablePatch = g.R.ReadByte() != 0;
                    g.R.ReadUInt16();
                    if (g.R.ReadByte() != 0) e.Creator = g.Guid().Value;
                    g.Skip(24 + 60);
                    ReadEntity(g, e, decodeVegetation, keepRaw);
                    if (hasCreator && !disablePatch) g.Guid();
                }
                list.Add(e);
            }
            // Hierarchy pairs, terminated by two -1 values. World matrices are absolute; parents are kept for NPC parts.
            for (int p; (p = g.R.ReadInt32()) != -1;)
            {
                int c = g.R.ReadInt32();
                if ((uint)c < (uint)list.Count) list[c].Parent = p;
            }
            g.R.ReadInt32();
            if (g.Position != g.PayloadEnd)
                throw new InvalidDataException($"entity archive: {g.PayloadEnd - g.Position} bytes left over");
            return list;
        }

        static void ReadEntity(GenomeReader g, WorldEntity e, bool decodeVegetation, bool keepRaw)
        {
            var r = g.R;
            r.ReadUInt16(); r.ReadUInt16();
            e.Guid = g.Guid().Value;
            e.Enabled = r.ReadByte() != 0;
            e.RenderingEnabled = r.ReadByte() != 0;
            g.Skip(5 + 4 + 2 + 1 + 3);
            e.Name = g.Str();
            for (int i = 0; i < 16; i++) e.World[i] = r.ReadSingle();
            if (keepRaw)
            {
                e.Local = new float[16];
                for (int i = 0; i < 16; i++) e.Local[i] = r.ReadSingle();
            }
            else g.Skip(64);
            g.Skip(24 + 24); // world tree box, local node box
            for (int i = 0; i < 6; i++) e.Bounds[i] = r.ReadSingle();
            g.Skip(32 + 4 + 1 + 4 + 4 + 4 + 1 + 1);

            int psCount = r.ReadInt32();
            for (int i = 0; i < psCount; i++)
            {
                r.ReadUInt16(); // ps version
                var ps = g.ReadObject();
                if (keepRaw)
                {
                    (e.RawPropertySets ??= new List<PropertyObject>()).Add(ps);
                    (e.RawPostDataStart ??= new List<long>()).Add(g.Position);
                    e.Source = g;
                }
                switch (ps.ClassName)
                {
                    case "eCVisualMeshStatic_PS":
                    case "eCVisualMeshDynamic_PS":
                        e.Visual = VisualKind.Mesh;
                        e.Resource = StrProp(g, ps, "ResourceFileName") ?? StrProp(g, ps, "ResourceFilePath");
                        e.MaterialSwitch = ps.Get("MaterialSwitch")?.AsInt() ?? 0;
                        break;
                    case "eCSpeedTree_PS":
                        e.Visual = VisualKind.SpeedTree;
                        e.Resource = StrProp(g, ps, "ResourceFilePath");
                        break;
                    case "eCStaticPointLight_PS":
                    case "eCPointLight_PS":
                        e.Light = ReadLight(ps);
                        break;
                    case "eCVegetation_PS":
                        if (decodeVegetation) e.Vegetation = ReadVegetation(g, ps);
                        break;
                    case "gCWaterZone_PS": e.IsWater = true; e.Water = WaterZoneInfo.Read(ps); break;
                    case "eCCollisionShape_PS": e.CollisionShapes = ReadCollisionShapes(g, ps); break;
                    case "gCNPC_PS":
                    case "eCVisualAnimation_PS": e.IsNpc = true; break;
                    case "gCItem_PS": e.IsItem = true; break;
                }
                g.Position = ps.End;
                if (r.ReadUInt32() != DeadC0de) throw new InvalidDataException($"DEADC0DE missing after {ps.ClassName}");
            }
        }

        /// <summary>Diagnostics: shapes whose data did not end exactly at the object end (G3Cli "shapes").</summary>
        public static int CollisionParseErrors;

        /// <summary>eCCollisionShape_PS post data: u32 n ; n × sub-object eCCollisionShape + shape data. Never throws.</summary>
        public static List<CollisionShape> ReadCollisionShapes(GenomeReader g, PropertyObject ps)
        {
            var list = new List<CollisionShape>();
            try
            {
                var r = g.R;
                bool psDisabled = ps.Get("DisableCollision")?.AsBool() ?? false;
                int n = r.ReadInt32();
                for (int i = 0; i < n && g.Position < ps.End; i++)
                {
                    var o = g.ReadObject();
                    if (o == null) break;
                    var s = new CollisionShape
                    {
                        Type = (CollisionShapeType)(o.Get("ShapeType")?.AsEnum() ?? 0),
                        Group = o.Get("Group")?.AsEnum() ?? 0,
                        Material = o.Get("Material")?.AsEnum() ?? 0,
                        DisableCollision = psDisabled || (o.Get("DisableCollision")?.AsBool() ?? false),
                        DisableResponse = o.Get("DisableResponse")?.AsBool() ?? false,
                        IgnoredByTraceRay = o.Get("IgnoredByTraceRay")?.AsBool() ?? false,
                        IsTrigger = (o.Get("TriggersOnTouch")?.AsBool() ?? false) || (o.Get("TriggersOnIntersect")?.AsBool() ?? false),
                    };
                    switch (s.Type)
                    {
                        case CollisionShapeType.TriMesh:
                        case CollisionShapeType.ConvexHull:
                            s.File = g.Str();
                            s.ResourceIndex = r.ReadUInt16();
                            break;
                        case CollisionShapeType.Box:
                            Read(r, s.Center); Read(r, s.Extent); Read(r, s.Axes);
                            break;
                        case CollisionShapeType.Capsule:
                            s.Height = r.ReadSingle(); s.Radius = r.ReadSingle();
                            Read(r, s.Axes); Read(r, s.Center);
                            break;
                        case CollisionShapeType.Sphere:
                            s.Radius = r.ReadSingle(); Read(r, s.Center);
                            break;
                        case CollisionShapeType.Point:
                            Read(r, s.Center);
                            break;
                        default:
                            g.Position = o.End; list.Add(s); continue;
                    }
                    r.ReadUInt16();
                    Read(r, s.LocalBounds);
                    g.Skip(12); // last position
                    if (g.Position != o.End) { CollisionParseErrors++; g.Position = o.End; }
                    list.Add(s);
                }
            }
            catch (Exception) { CollisionParseErrors++; /* keep what was decoded; caller seeks to ps.End */ }
            if (g.Position != ps.End) CollisionParseErrors++;
            return list;
        }

        static void Read(BinaryReader r, float[] into)
        {
            for (int i = 0; i < into.Length; i++) into[i] = r.ReadSingle();
        }

        static string StrProp(GenomeReader g, PropertyObject o, string name)
        {
            var p = o.Get(name);
            if (p == null) return null;
            var s = g.StrFrom(p.Value);
            return string.IsNullOrEmpty(s) ? null : s;
        }

        static PointLight ReadLight(PropertyObject ps)
        {
            var l = new PointLight { R = 1, G = 1, B = 1, Range = 1000 };
            var c = ps.Get("Color");
            if (c != null && c.Value.Length >= 16)
            {
                l.R = BitConverter.ToSingle(c.Value, 4);
                l.G = BitConverter.ToSingle(c.Value, 8);
                l.B = BitConverter.ToSingle(c.Value, 12);
            }
            var range = ps.Get("Range");
            if (range != null) l.Range = range.AsFloat();
            var off = ps.Get("Offset");
            if (off != null && off.Value.Length >= 12)
            {
                l.OffX = BitConverter.ToSingle(off.Value, 0);
                l.OffY = BitConverter.ToSingle(off.Value, 4);
                l.OffZ = BitConverter.ToSingle(off.Value, 8);
            }
            l.CastShadows = ps.Get("CastShadows")?.AsBool() ?? false;
            return l;
        }

        static Vegetation ReadVegetation(GenomeReader g, PropertyObject ps)
        {
            var r = g.R;
            var v = new Vegetation();
            var vr = ps.Get("ViewRange");
            if (vr != null) v.ViewRange = vr.AsFloat();

            if (r.ReadByte() != 0)
            {
                int types = r.ReadInt32();
                for (int i = 0; i < types; i++) { r.ReadUInt16(); g.Str(); }
            }
            r.ReadUInt32(); // mesh slot count
            int meshCount = r.ReadInt32();
            for (int i = 0; i < meshCount; i++)
            {
                var mo = g.ReadObject();
                var m = new VegetationMesh();
                var path = StrProp(g, mo, "MeshFilePath");
                if (path != null)
                {
                    int slash = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
                    m.MeshFile = path.Substring(slash + 1);
                }
                m.DoubleSided = mo.Get("DoubleSided")?.AsBool() ?? false;
                m.WindStrength = mo.Get("WindStrength")?.AsFloat() ?? 1f;
                r.ReadUInt16();
                m.Index = r.ReadUInt16();
                g.Skip(8);
                m.DiffuseTexture = g.Str();
                g.Skip(24);
                m.Positions = Floats(g, 3);
                m.Normals = Floats(g, 3);
                m.Uvs = Floats(g, 2);
                int ni = g.ArrayCount();
                m.Indices = new uint[ni];
                Buffer.BlockCopy(r.ReadBytes(ni * 4), 0, m.Indices, 0, ni * 4);
                g.Position = mo.End;
                v.Meshes[m.Index] = m;
            }

            if (r.ReadUInt16() != 2) throw new InvalidDataException("vegetation grid version");
            r.ReadSingle();
            g.Skip(16);
            int nodes = r.ReadInt32();
            for (int n = 0; n < nodes; n++)
            {
                r.ReadInt32(); r.ReadUInt16(); g.Skip(24);
                int plants = r.ReadInt32();
                for (int p = 0; p < plants; p++)
                {
                    var pl = new VegetationPlant();
                    r.ReadUInt16();
                    pl.MeshIndex = r.ReadUInt16();
                    pl.X = r.ReadSingle(); pl.Y = r.ReadSingle(); pl.Z = r.ReadSingle();
                    pl.Qx = r.ReadSingle(); pl.Qy = r.ReadSingle(); pl.Qz = r.ReadSingle(); pl.Qw = r.ReadSingle();
                    pl.ScaleWidth = r.ReadSingle(); pl.ScaleHeight = r.ReadSingle();
                    pl.Color = r.ReadUInt32();
                    v.Plants.Add(pl);
                }
            }
            return v;
        }

        static float[] Floats(GenomeReader g, int components)
        {
            int n = g.ArrayCount() * components;
            var a = new float[n];
            Buffer.BlockCopy(g.R.ReadBytes(n * 4), 0, a, 0, n * 4);
            return a;
        }
    }

    /// <summary>.secdat: lists the .lrentdat and .node base names of a sector (same folder).</summary>
    public static class SecDat
    {
        public static void Read(byte[] data, List<string> lrentdats, List<string> nodes)
        {
            var g = new GenomeReader(data);
            g.R.ReadUInt16();
            int nl = g.R.ReadInt32(), nn = g.R.ReadInt32();
            for (int i = 0; i < nl; i++) lrentdats.Add(g.Str());
            for (int i = 0; i < nn; i++) nodes.Add(g.Str());
        }
    }

    /// <summary>Which files make up the playable world (Docs/formats-world.md §7.5).</summary>
    public sealed class WorldLayout
    {
        public sealed class Sector
        {
            public string Name;
            public string Folder;                      // pak path of the folder, e.g. "G3_World_01/Myrtana/..."
            public bool Enabled = true;                // listed "true" in .wrldatasc (false only with includeDisabled)
            public readonly List<string> Nodes = new List<string>();      // full pak paths
            public readonly List<string> Lrentdats = new List<string>();
        }

        public readonly List<Sector> Sectors = new List<Sector>();

        /// <summary>
        /// The distant low-poly copy of the world (G3_World_Lowpoly_01_Levelmesh_01: *_LOWPOLY houses/walls and
        /// G3_World_Landscape_Lowpoly_* terrain). Disabled in the original .wrldatasc, enabled by Community Patch 1.75, whose
        /// engine shows it only beyond the detail range. It is never part of the near world here: the master scene's
        /// G3LowpolyBackdrop draws it per cell where the detail is not loaded (Docs/formats-world.md §7.5).
        /// </summary>
        public static bool IsLowpolySector(string name) => name.IndexOf("_Lowpoly_", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <param name="files">Projects_compiled pak paths, e.g. "G3_World_01/.../X.node" mapped to a loader.</param>
        /// <param name="includeDisabled">Also list the sectors that are "false" in .wrldatasc (with <see cref="Sector.Enabled"/>
        /// false): the story switches some of them on (SetSectorStatus).</param>
        public static WorldLayout Build(IEnumerable<string> files, Func<string, byte[]> read, string world = "G3_World_01",
                                        bool includeDisabled = false)
        {
            var all = new List<string>(files);
            var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var asc = all.Find(p => p.Equals($"{world}/{world}.wrldatasc", StringComparison.OrdinalIgnoreCase));
            bool inList = false;
            foreach (var raw in Encoding.GetEncoding(28591).GetString(read(asc)).Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("[")) { inList = line.Equals("[Sector.List]", StringComparison.OrdinalIgnoreCase); continue; }
                int eq = line.IndexOf('=');
                if (inList && eq > 0 && line.Substring(eq + 1).Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
                    enabled.Add(line.Substring(0, eq).Trim());
                else if (inList && eq > 0 && line.Substring(eq + 1).Trim().Equals("false", StringComparison.OrdinalIgnoreCase))
                    disabled.Add(line.Substring(0, eq).Trim());
            }

            var layout = new WorldLayout();
            foreach (var p in all)
            {
                if (!p.StartsWith(world + "/", StringComparison.OrdinalIgnoreCase) ||
                    !p.EndsWith(".secdat", StringComparison.OrdinalIgnoreCase)) continue;
                int slash = p.LastIndexOf('/');
                var name = p.Substring(slash + 1, p.Length - slash - 1 - ".secdat".Length);
                bool on = enabled.Contains(name);
                if ((!on && !(includeDisabled && disabled.Contains(name))) || IsLowpolySector(name)) continue;
                var s = new Sector { Name = name, Folder = p.Substring(0, slash), Enabled = on };
                var lr = new List<string>(); var nd = new List<string>();
                SecDat.Read(read(p), lr, nd);
                foreach (var n in nd) s.Nodes.Add($"{s.Folder}/{n}.node");
                foreach (var n in lr) s.Lrentdats.Add($"{s.Folder}/{n}.lrentdat");
                layout.Sectors.Add(s);
            }
            return layout;
        }
    }
}
