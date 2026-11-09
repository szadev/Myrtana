using System;
using System.IO;
using System.Linq;
using G3.Formats.Mesh;
using G3.Formats.Pak;
using G3.Player;
using UnityEditor;

namespace G3.Unity.Editor
{
    /// <summary>
    /// Imports the G3 water zones (water meshes with gCWaterZone_PS in the load set: 268 sea cells, 54 river and lake
    /// pieces) into <see cref="OutputPath"/> for <see cref="G3Water"/>: per zone the surface plane, box, radius and the
    /// mesh footprint as a 0.5–1 m raster (about 1.2 MB). Docs/hero.md "Water and swimming". The same build runs offline
    /// as <c>G3Cli water stats</c>.
    /// </summary>
    public static class G3WaterImporter
    {
        public const string OutputPath = "Assets/G3Imported/Water/Resources/" + G3Water.ResourcePath + ".bytes";

        [MenuItem("G3/World/Import Water Zones", priority = 120)]
        static void Menu() => UnityEngine.Debug.Log(Build());

        /// <summary>Builds the water zone file; returns a one-line summary.</summary>
        public static string Build()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var entities = G3WorldImporter.LoadEntities(null, e => e.Water != null);
            int missing = 0, triangles = 0;
            var zones = new System.Collections.Generic.List<WaterZone>();
            using (var fs = new G3FileSystem(G3WorldImporter.DataDir, "_compiledMesh"))
            {
                foreach (var (e, _) in entities)
                {
                    MeshResource mesh = null;
                    try
                    {
                        string res = e.Resource;
                        if (res != null && res.EndsWith(".xlmsh", StringComparison.OrdinalIgnoreCase))
                        {
                            var lod = fs.GetByName(res);
                            res = lod != null ? Xlmsh.Read(lod.Read()).FirstOrDefault() : null;
                        }
                        var entry = res != null ? fs.GetByName(res) : null;
                        if (entry != null) mesh = Xcmsh.Read(entry.Read(), res);
                    }
                    catch (Exception) { mesh = null; }
                    if (mesh == null) missing++;
                    var w = e.Water;
                    var zone = WaterZone.FromG3(e.Name, w.Normal, w.Centre, w.SquareRadius, w.MinX, w.MaxX, w.MinZ, w.MaxZ, e.World,
                        mesh?.Elements.Select(el => (el.Positions, el.Indices)));
                    triangles += zone.TriangleCount;
                    zone.Rasterize();
                    zones.Add(zone);
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
            using (var file = File.Create(OutputPath)) WaterMapFile.Write(file, zones);
            AssetDatabase.ImportAsset(OutputPath);
            G3Water.SetMap(null);
            long bytes = new FileInfo(OutputPath).Length;
            return $"Water zones: {zones.Count} ({triangles} footprint triangles, {missing} meshes missing) -> {OutputPath} " +
                   $"({bytes / 1024} KiB) in {sw.Elapsed.TotalSeconds:F1} s";
        }
    }
}
