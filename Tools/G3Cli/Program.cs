using System;
using System.IO;
using System.Linq;
using G3.Formats.Pak;

static partial class Program
{
    static string DefaultData => G3.Formats.G3Install.DataDirOrHint;

    static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("usage: G3Cli list <volume> [filter] | extract <volume> <outDir> [filter]");
            return 1;
        }
        string data = Environment.GetEnvironmentVariable("G3_DATA") ?? DefaultData;
        switch (args[0])
        {
            case "list":
            {
                using var fs = new G3FileSystem(data, args[1]);
                foreach (var e in fs.Files.Where(e => Match(e, args, 2)).OrderBy(e => e.Path))
                    Console.WriteLine($"{e.Size,10} {Path.GetFileName(e.Archive.FilePath),-24} {e.Path}");
                return 0;
            }
            case "extract":
            {
                using var fs = new G3FileSystem(data, args[1]);
                int n = 0;
                foreach (var e in fs.Files.Where(e => Match(e, args, 3)))
                {
                    var dst = Path.Combine(args[2], args[1], e.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.WriteAllBytes(dst, e.Read());
                    n++;
                }
                Console.WriteLine($"extracted {n} files");
                return 0;
            }
            case "validate":
                return Validate(data);
            case "imgmean":
            {
                // imgmean <filter> : size, format, mean and std-dev of the gamma-space colour (largest mip, DXT colour blocks)
                using var fs = new G3FileSystem(data, "_compiledImage");
                foreach (var e in fs.Files.Where(e => Match(e, args, 1)).OrderBy(e => e.Path))
                {
                    var img = G3.Formats.Image.Ximg.Read(e.Read());
                    var mip = img.Mips.OrderByDescending(m => m.Length).First();
                    double[] sum = new double[3], sq = new double[3]; long n = 0;
                    if (img.Format != G3.Formats.Image.PixelFormat.BGRA32)
                    {
                        int bs = img.Format == G3.Formats.Image.PixelFormat.DXT1 ? 8 : 16, co = bs - 8;
                        for (int b = 0; b + bs <= mip.Length; b += bs)
                        {
                            int c0 = mip[b + co] | mip[b + co + 1] << 8, c1 = mip[b + co + 2] | mip[b + co + 3] << 8;
                            var pal = new double[4][];
                            double[] C(int c) => new[] { (c >> 11 & 31) / 31.0, (c >> 5 & 63) / 63.0, (c & 31) / 31.0 };
                            pal[0] = C(c0); pal[1] = C(c1);
                            bool four = c0 > c1 || bs == 16;
                            pal[2] = new double[3]; pal[3] = new double[3];
                            for (int k = 0; k < 3; k++)
                            {
                                pal[2][k] = four ? (2 * pal[0][k] + pal[1][k]) / 3 : (pal[0][k] + pal[1][k]) / 2;
                                pal[3][k] = four ? (pal[0][k] + 2 * pal[1][k]) / 3 : 0;
                            }
                            uint idx = BitConverter.ToUInt32(mip, b + co + 4);
                            for (int p = 0; p < 16; p++)
                            {
                                var c = pal[(idx >> (2 * p)) & 3];
                                for (int k = 0; k < 3; k++) { sum[k] += c[k]; sq[k] += c[k] * c[k]; }
                                n++;
                            }
                        }
                    }
                    if (n == 0) { Console.WriteLine($"{e.Path} {img.Width}x{img.Height} {img.Format} (skipped)"); continue; }
                    string Fmt(Func<int, double> f) => string.Join(",", Enumerable.Range(0, 3).Select(k => f(k).ToString("0.000")));
                    Console.WriteLine($"{Path.GetFileNameWithoutExtension(e.Path)} {img.Width}x{img.Height} {img.Format} mean {Fmt(k => sum[k] / n)} sd {Fmt(k => Math.Sqrt(Math.Max(0, sq[k] / n - sum[k] * sum[k] / n / n)))}");
                }
                return 0;
            }
            case "matgraph":
            {
                // matgraph [filter] : one line per material, slot = graph expression (Docs/formats-mesh-material-texture.md §4.6)
                G3.Formats.Material.Xshmat.DescribeGraphs = true;
                using var fs = new G3FileSystem(data, "_compiledMaterial");
                foreach (var e in fs.Files.Where(e => e.Path.EndsWith(".xshmat", StringComparison.OrdinalIgnoreCase) && Match(e, args, 1)).OrderBy(e => e.Path))
                {
                    try
                    {
                        var m = G3.Formats.Material.Xshmat.Read(e.Read(), e.Path);
                        if (m.Graph == null) continue;
                        Console.WriteLine($"{Path.GetFileNameWithoutExtension(e.Path)}	" +
                            string.Join("	", m.Graph.Where(kv => kv.Key != "SpecularPower").Select(kv => kv.Key + "=" + kv.Value)));
                    }
                    catch (Exception ex) { Console.WriteLine($"FAIL {e.Path}: {ex.Message}"); }
                }
                return 0;
            }
            case "world":
                return WorldStats(data);
            case "inspect":
                return Inspect(data, args[1]);
            case "entity":
                return DumpEntity(data, args[1], args.Length > 2 ? args[2] : ".lrentdat");
            case "collision" when args.Length > 1 && args[1] == "audit": return CollisionAudit(data, args); // Tools/G3Cli/CollisionAuditCommands.cs
            case "collision" when args.Length > 1 && args[1] == "cells": return CollisionCells(args);
            case "collision" when args.Length > 1 && args[1] == "winding": return CollisionWinding(data, args);
            case "collision" when args.Length > 1 && args[1] == "near": return CollisionNear(data, args); // Tools/G3Cli/CollisionNearCommands.cs
            case "collision" when args.Length > 1 && args[1] == "missing": return CollisionMissing(data, args); // Tools/G3Cli/CollisionMissingCommands.cs
            case "collision" when args.Length > 1 && args[1] == "water": return CollisionWater(data, args); // Tools/G3Cli/CollisionMissingCommands.cs
            case "collision" when args.Length > 1 && args[1] == "column": return CollisionColumn(data, args); // Tools/G3Cli/CollisionColumnCommands.cs
            case "collision" when args.Length > 1 && args[1] == "navfacing": return CollisionNavFacing(data, args); // Tools/G3Cli/CollisionNavFacingCommands.cs
            case "collision" when args.Length > 1 && args[1] == "interiors": return CollisionInteriors(data, args); // Tools/G3Cli/CollisionFloorCommands.cs
            case "collision" when args.Length > 1 && args[1] == "itemsupports": return CollisionItemSupports(data, args); // Tools/G3Cli/ItemSupportCommands.cs (fix-114)
            case "collision" when args.Length > 1 && args[1] == "meshsweep": return MeshSweep(data, args); // Tools/G3Cli/MeshSweepCommands.cs (fix-254)
            case "collision" when args.Length > 1 && args[1] == "itemsight": return CollisionItemSight(data, args); // Tools/G3Cli/ItemSightCommands.cs (fix-254)
            case "collision" when args.Length > 1 && args[1] == "caveroofs": return CaveRoofs(data, args); // Tools/G3Cli/CaveRoofCommands.cs (fix-128)
            case "collision" when args.Length > 1 && args[1] == "usespots": return CollisionUseSpots(data, args); // Tools/G3Cli/UseSpotScanCommands.cs (fix-103)
            case "collision":
                return ValidateCollision(data, args.Length > 1 ? args[1] : null);
            case "anim":
                return AnimCommand(data, args); // Tools/G3Cli/AnimCommands.cs
            case "weather":
                return DumpWeather(data, args.Length > 1 && args[1] != "--hours" ? args[1] : null,
                    Array.IndexOf(args, "--hours") is int hi && hi >= 0 && hi + 1 < args.Length ? args[hi + 1] : null);   // fix-239: --hours 17,18.5,20
            case "audio": return AudioCommand(data, args); // Tools/G3Cli/AudioCommands.cs
            case "items": return ItemCommand(data, args); // Tools/G3Cli/ItemCommands.cs
            case "story": return StoryCommand(data, args); // Tools/G3Cli/StoryCommands.cs
            case "perf": return PerfCommand(data, args); // Tools/G3Cli/PerfCommands.cs
            case "content": return ContentCommand(data, args); // Tools/G3Cli/ContentCommands.cs
            case "dialog": return DialogCommand(args); // Tools/G3Cli/ContentCommands.cs
            case "gui": return GuiCommand(data, args); // Tools/G3Cli/GuiCommands.cs
            case "spt": return SptCommand(data, args); // Tools/G3Cli/SptCommands.cs
            case "objects": return WorldObjectCommand(data, args); // Tools/G3Cli/WorldObjectCommands.cs
            case "zones": return ZoneCommand(data, args); // Tools/G3Cli/ZoneCommands.cs
            case "nav": return NavCommand(data, args); // Tools/G3Cli/NavCommands.cs
            case "floors": return FloorCommand(data, args); // Tools/G3Cli/FloorCommands.cs
            case "bodies": return BodyHeights(data, args); // Tools/G3Cli/BodyHeightAuditCommands.cs (fix-163)
            case "fx": return FxCommand(data, args); // Tools/G3Cli/FxCommands.cs
            case "water": return WaterCommand(data, args); // Tools/G3Cli/WaterCommands.cs
            case "spots": return SpotCommand(data, args); // Tools/G3Cli/SpotCommands.cs
            case "relief": return ReliefCommand(data, args); // Tools/G3Cli/ReliefCommands.cs (remaster-relief)
            case "dune": return DuneCommand(data, args); // Tools/G3Cli/DuneCommands.cs (dune-1)
            case "selfillum": return SelfIllumCommand(data, args); // Tools/G3Cli/SelfIllumCommands.cs (fix-260)
            case "remaster" when args.Length > 1 && (args[1] == "mats" || args[1] == "textures"): return RemasterTextureCommand(data, args); // Tools/G3Cli/RemasterTextureCommands.cs
            case "remaster" when args.Length > 1 && (args[1] == "export" || args[1] == "compare"): return RemasterExportCommand(data, args); // Tools/G3Cli/RemasterExportCommands.cs
            case "remaster" when args.Length > 1 && args[1] == "world": return RemasterWorldCommand(data, args); // Tools/G3Cli/RemasterWorldCommands.cs
            case "remaster": return RemasterCommand(args); // Tools/G3Cli/RemasterCommands.cs
            case "build": return BuildCommand(args); // Tools/G3Cli/BuildLayoutCommands.cs (fix-167)
        }
        Console.WriteLine("unknown command");
        return 1;
    }

    /// <summary>Parse every mesh, material and image with the C# readers and report failures.</summary>
    static int Validate(string data)
    {
        using var fs = new G3FileSystem(data, "_compiledMesh", "_compiledMaterial", "_compiledImage");
        int ok = 0, fail = 0, diffuse = 0, normal = 0;
        long verts = 0;
        var formats = new System.Collections.Generic.Dictionary<string, int>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var e in fs.Files)
        {
            var ext = Path.GetExtension(e.Path).ToLowerInvariant();
            try
            {
                switch (ext)
                {
                    case ".xcmsh":
                        var m = G3.Formats.Mesh.Xcmsh.Read(e.Read(), e.Path);
                        if (m != null)
                            foreach (var el in m.Elements)
                            {
                                verts += el.VertexCount;
                                if (el.Indices.Length % 3 != 0) throw new Exception("index count");
                                foreach (var i in el.Indices) if (i >= el.VertexCount) throw new Exception("index range");
                            }
                        break;
                    case ".xlmsh": G3.Formats.Mesh.Xlmsh.Read(e.Read()); break;
                    case ".xshmat":
                        var mat = G3.Formats.Material.Xshmat.Read(e.Read(), e.Path);
                        if (mat.Diffuse != null) diffuse++;
                        if (mat.Normal != null) normal++;
                        break;
                    case ".ximg":
                        var img = G3.Formats.Image.Ximg.Read(e.Read());
                        var key = img.Format + (img.IsCubemap ? " cube" : "");
                        formats[key] = formats.TryGetValue(key, out var c) ? c + 1 : 1;
                        if (img.Format == G3.Formats.Image.PixelFormat.DXT3 && ok % 50 == 0)
                            G3.Formats.Image.Dxt.DecodeDxt3(img.Mips[0], img.Width, img.Height);
                        break;
                    default: continue;
                }
                ok++;
            }
            catch (Exception ex)
            {
                fail++;
                if (fail <= 20) Console.WriteLine($"FAIL {e.Path}: {ex.Message}");
            }
        }
        Console.WriteLine($"ok {ok}, failed {fail}, mesh vertices {verts:N0}, materials with diffuse {diffuse}, with normal {normal}, {sw.Elapsed.TotalSeconds:F1}s");
        foreach (var kv in formats) Console.WriteLine($"  {kv.Key}: {kv.Value}");
        return fail == 0 ? 0 : 2;
    }

    /// <summary>Parse every .xnvmsh collision mesh (both paks) to its exact end; print stats (or one file's meshes).</summary>
    static int ValidateCollision(string data, string filter)
    {
        using var fs = new G3FileSystem(data, "_compiledPhysic", "_compiledMesh");
        int ok = 0, fail = 0, empty = 0, tri = 0, cvx = 0, disabled = 0;
        long verts = 0, tris = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var e in fs.Files)
        {
            if (!e.Path.EndsWith(".xnvmsh", StringComparison.OrdinalIgnoreCase)) continue;
            if (filter != null && e.Path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            try
            {
                var m = G3.Formats.Mesh.Xnvmsh.Read(e.Read(), e.Path);
                if (m == null) { empty++; continue; }
                foreach (var s in m.Meshes)
                {
                    if (s.Convex) cvx++; else tri++;
                    if (s.DisableCollision) disabled++;
                    verts += s.VertexCount; tris += s.Indices.Length / 3;
                    if (filter != null)
                        Console.WriteLine($"{e.Path}: {(s.Convex ? "convex" : "trimesh")} {s.VertexCount} v {s.Indices.Length / 3} t, mat {s.ShapeMaterial}, " +
                                          $"ign {s.IgnoredByTraceRay} dc {s.DisableCollision} dr {s.DisableResponse}");
                }
                if (filter != null)
                    Console.WriteLine($"  cv {m.ClassVersion} convexRes {m.ConvexResource} bounds {string.Join(" ", m.Bounds.Select(b => b.ToString("F1")))}");
                ok++;
            }
            catch (Exception ex)
            {
                fail++;
                if (fail <= 20) Console.WriteLine($"FAIL {e.Path}: {ex.Message}");
            }
        }
        Console.WriteLine($"xnvmsh ok {ok}, failed {fail}, empty {empty}; trimesh {tri}, convex {cvx}, collision-disabled {disabled}; " +
                          $"{verts:N0} verts, {tris:N0} tris, {sw.Elapsed.TotalSeconds:F1}s");
        if (filter == null) CollisionShapeStats(data, fs);
        return fail == 0 ? 0 : 2;
    }

    /// <summary>eCCollisionShape_PS usage in the world's load set: shape types, groups, missing .xnvmsh references.</summary>
    static void CollisionShapeStats(string data, G3FileSystem meshFs)
    {
        using var fs = new G3FileSystem(data, "Projects_compiled");
        const string vol = "Projects_compiled/";
        var layout = G3.Formats.World.WorldLayout.Build(fs.Files.Select(f => f.Path), p => fs.Get(vol + p)?.Read());
        var types = new System.Collections.Generic.Dictionary<string, int>();
        int withShapes = 0, visualMeshes = 0, missing = 0, badIndex = 0, shapes = 0;
        var cache = new System.Collections.Generic.Dictionary<string, G3.Formats.Mesh.CollisionMeshResource>(StringComparer.OrdinalIgnoreCase);
        G3.Formats.World.EntityArchive.CollisionParseErrors = 0;
        foreach (var s in layout.Sectors)
        foreach (var path in s.Nodes.Concat(s.Lrentdats))
        {
            var entry = fs.Get(vol + path);
            System.Collections.Generic.List<G3.Formats.World.WorldEntity> list;
            try { list = entry == null ? null : G3.Formats.World.EntityArchive.Read(entry.Read(), false); }
            catch { continue; }
            if (list == null) continue;
            foreach (var e in list)
            {
                if (e.Visual == G3.Formats.World.VisualKind.Mesh) visualMeshes++;
                if (e.CollisionShapes == null) continue;
                withShapes++;
                foreach (var cs in e.CollisionShapes)
                {
                    shapes++;
                    var key = $"{cs.Type} group {cs.Group}{(cs.DisableCollision ? " disabled" : "")}";
                    types[key] = types.TryGetValue(key, out var c) ? c + 1 : 1;
                    if (cs.File == null) continue;
                    if (!cache.TryGetValue(cs.File, out var res))
                    {
                        var me = meshFs.GetByName(cs.File);
                        res = me == null ? null : G3.Formats.Mesh.Xnvmsh.Read(me.Read(), cs.File);
                        cache[cs.File] = res;
                    }
                    if (res == null) { missing++; if (missing <= 5) Console.WriteLine($"  missing {cs.File} ({e.Name})"); }
                    else if (!res.Obsolete && cs.ResourceIndex >= res.Meshes.Count) badIndex++;
                }
            }
        }
        Console.WriteLine($"entities with eCCollisionShape_PS {withShapes:N0} (visual meshes {visualMeshes:N0}), shapes {shapes:N0}, " +
                          $"missing xnvmsh refs {missing}, resource index out of range {badIndex}, parse mismatches {G3.Formats.World.EntityArchive.CollisionParseErrors}");
        foreach (var kv in types.OrderByDescending(k => k.Value)) Console.WriteLine($"{kv.Value,8} {kv.Key}");
    }

    /// <summary>Print a mesh's elements and each material's shader graph summary.</summary>
    static int Inspect(string data, string meshName)
    {
        using var fs = new G3FileSystem(data, "_compiledMesh", "_compiledMaterial", "_compiledImage");
        var mesh = G3.Formats.Mesh.Xcmsh.Read(fs.GetByName(meshName).Read(), meshName);
        foreach (var el in mesh.Elements)
        {
            int distinct = el.Diffuse == null ? 0 : el.Diffuse.Distinct().Count();
            Console.WriteLine($"element: {el.VertexCount} verts, {el.Indices.Length / 3} tris, material {el.Material}, " +
                              $"uv1 {(el.Uv1 != null)}, distinct diffuse colors {distinct}, sample {(el.Diffuse != null ? el.Diffuse[0].ToString("X8") : "-")}");
            var me = fs.GetByName(Path.GetFileNameWithoutExtension(el.Material) + ".xshmat");
            if (me == null) { Console.WriteLine("   material missing"); continue; }
            DumpMaterial(me.Read());
        }
        return 0;
    }

    static void DumpMaterial(byte[] bytes)
    {
        var g = new G3.Formats.Genome.GenomeReader(bytes);
        var mat = g.ReadObject();
        g.ReadResourceTail();
        var sh = g.ReadObject();
        Console.WriteLine($"   shader {sh.ClassName} v{sh.ClassVersion}: " +
                          string.Join(", ", sh.Properties.Select(p => $"{p.Key}={Fmt(g, p.Value)}")));
        // walk raw: proxies then elements
        int proxies = sh.ClassVersion > 1 ? 7 : 6;
        for (int i = 0; i < proxies; i++) { g.R.ReadUInt32(); var gd = g.Guid(); Console.WriteLine($"     proxy{i} {gd}"); }
        g.R.ReadUInt16(); g.R.ReadUInt16(); g.Guid(); g.Skip(16);
        int n = g.R.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            var o = g.ReadObject();
            var props = string.Join(", ", o.Properties.Select(p => $"{p.Key}={Fmt(g, p.Value)}"));
            var start = g.Position;
            var rest = (int)(o.End - start);
            var raw = g.R.ReadBytes(rest);
            // token GUID is located 16+20 bytes before the end (u16,u16,guid20,rect16)
            var token = new Guid(raw.Skip(rest - 36).Take(16).ToArray());
            var inputs = new System.Collections.Generic.List<string>();
            int k = o.ClassName == "eCColorSrcCombiner" ? 2 : o.ClassName == "eCColorSrcBlend" ? 3 : 0;
            for (int j = 0; j < k; j++) inputs.Add(new Guid(raw.Skip(j * 24 + 4).Take(16).ToArray()).ToString().Substring(0, 8));
            if (o.ClassName == "eCColorSrcSampler") { k = 1; inputs.Add("uv:" + new Guid(raw.Skip(4).Take(16).ToArray()).ToString().Substring(0, 8) + " valid " + raw[20]); }
            if (o.ClassName == "eCTexCoordSrcScale")
            {
                var s = o.Get("Scale").Value;
                props = $"Scale=({BitConverter.ToSingle(s, 0)}, {BitConverter.ToSingle(s, 4)}) rawlen={rest} rawhead={BitConverter.ToString(raw.Take(Math.Min(rest, 40)).ToArray())}";
            }
            Console.WriteLine($"     [{token.ToString().Substring(0, 8)}] {o.ClassName} {props} {(k > 0 ? "inputs " + string.Join(",", inputs) : "")}");
            g.Position = o.End;
        }
    }

    /// <summary>Dump all property sets of entities whose name contains the filter, from files matching the extension.</summary>
    static int DumpEntity(string data, string nameFilter, string ext)
    {
        using var fs = new G3FileSystem(data, "Projects_compiled");
        foreach (var f in fs.Files.Where(f => f.Path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
        {
            System.Collections.Generic.List<G3.Formats.World.WorldEntity> list;
            try { list = G3.Formats.World.EntityArchive.Read(f.Read(), false, keepRaw: true); } catch { continue; }
            if (list == null) continue;
            // "@x,z,r" (fix-254): every entity within r m of x z (Unity metres), named or not
            float[] at = nameFilter.StartsWith("@") ? nameFilter.Substring(1).Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray() : null;
            foreach (var e in list.Where(e => at != null ? Math.Abs(e.X / 100 - at[0]) <= at[2] && Math.Abs(e.Z / 100 - at[1]) <= at[2]
                                                         : e.Name != null && e.Name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                Console.WriteLine($"== {e.Name} {e.Resource} in {f.Path} pos ({e.X:F0},{e.Y:F0},{e.Z:F0})");
                foreach (var ps in e.RawPropertySets ?? new())
                {
                    Console.WriteLine($"  {ps.ClassName} v{ps.ClassVersion}");
                    foreach (var p in ps.Properties)
                        Console.WriteLine($"     {p.Key} : {p.Value.Type} = {Fmt(e.Source, p.Value)}");
                }
            }
        }
        return 0;
    }

    /// <summary>A nested bTPropertyObject (u16 version, u32 size, u16 30, u32 count, properties, …) as "{Name=value, …}"; null if it does not parse.</summary>
    static string FmtObject(G3.Formats.Genome.GenomeReader g, byte[] value)
    {
        try
        {
            var r = new System.IO.BinaryReader(new System.IO.MemoryStream(value));
            r.ReadUInt16();
            r.ReadUInt32();
            r.ReadUInt16();
            uint count = r.ReadUInt32();
            var parts = new System.Collections.Generic.List<string>();
            for (int k = 0; k < count; k++)
            {
                string name = g.StrFrom(r.ReadBytes(2)), type = g.StrFrom(r.ReadBytes(2));
                r.ReadUInt16();
                var prop = new G3.Formats.Genome.Property { Type = type, Value = r.ReadBytes(r.ReadInt32()) };
                parts.Add(name + "=" + Fmt(g, prop));
            }
            return "{" + string.Join(", ", parts) + "}";
        }
        catch (Exception) { return null; }
    }

    static string Fmt(G3.Formats.Genome.GenomeReader g, G3.Formats.Genome.Property p)
    {
        if (p.Type == "bCFloatColor" && p.Value.Length >= 16)
            return $"rgb({BitConverter.ToSingle(p.Value, 4):G3}, {BitConverter.ToSingle(p.Value, 8):G3}, {BitConverter.ToSingle(p.Value, 12):G3})";
        if ((p.Type == "bCVector" || p.Type == "bCEulerAngles") && p.Value.Length >= 12)
            return $"({BitConverter.ToSingle(p.Value, 0):G4}, {BitConverter.ToSingle(p.Value, 4):G4}, {BitConverter.ToSingle(p.Value, 8):G4})";
        if (p.Type == "bCVector2" && p.Value.Length >= 8)
            return $"({BitConverter.ToSingle(p.Value, 0):G4}, {BitConverter.ToSingle(p.Value, 4):G4})";
        if (p.Type.Contains("String")) { try { return "\"" + g.StrFrom(p.Value) + "\""; } catch (IndexOutOfRangeException) { return $"<{p.Type} {p.Value.Length} bytes>"; } }
        if (p.Type.StartsWith("bTPropertyContainer")) return p.AsEnum().ToString();
        if (p.Type.StartsWith("bTPropertyObject") && FmtObject(g, p.Value) is string nested) return nested;
        if (p.Type == "float") return p.AsFloat().ToString("G4");
        if (p.Value.Length <= 4) return p.AsInt().ToString();
        if (p.Type.EndsWith("EntityProxy")) { var gid = G3.Formats.Items.Props.ProxyValue(p.Value, 0); return gid == Guid.Empty ? "-" : "{" + gid + "}"; }
        if (p.Type == "bTValArray<long>" && p.Value.Length >= 5)
        {
            int n = BitConverter.ToInt32(p.Value, 1);
            return "[" + string.Join(",", Enumerable.Range(0, n).Where(i => 5 + 4 * i + 4 <= p.Value.Length).Select(i => BitConverter.ToInt32(p.Value, 5 + 4 * i))) + "]";
        }
        return $"<{p.Type}>";
    }

    static int WorldStats(string data)
    {
        using var fs = new G3FileSystem(data, "Projects_compiled");
        const string vol = "Projects_compiled/";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var layout = G3.Formats.World.WorldLayout.Build(
            fs.Files.Select(f => f.Path), p => fs.Get(vol + p)?.Read());
        int files = 0, skipped = 0, fail = 0, entities = 0, meshes = 0, trees = 0, lights = 0, plants = 0, terrain = 0;
        var distinct = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var treeKinds = new System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        foreach (var s in layout.Sectors)
        foreach (var path in s.Nodes)
        {
            var entry = fs.Get(vol + path);
            try
            {
                var list = entry == null ? null : G3.Formats.World.EntityArchive.Read(entry.Read());
                if (list == null) { skipped++; continue; }
                files++;
                foreach (var e in list)
                {
                    entities++;
                    if (!e.Enabled || !e.RenderingEnabled || e.IsNpc || e.IsItem) continue;
                    if (e.Visual == G3.Formats.World.VisualKind.Mesh)
                    {
                        meshes++; distinct.Add(e.Resource);
                        if (e.Resource.IndexOf("_Landscape_Cell_", StringComparison.OrdinalIgnoreCase) >= 0) terrain++;
                        minX = Math.Min(minX, e.X); maxX = Math.Max(maxX, e.X);
                        minZ = Math.Min(minZ, e.Z); maxZ = Math.Max(maxZ, e.Z);
                    }
                    if (e.Visual == G3.Formats.World.VisualKind.SpeedTree)
                    {
                        trees++;
                        treeKinds[e.Resource] = treeKinds.TryGetValue(e.Resource, out var tk) ? tk + 1 : 1;
                    }
                    if (e.Light != null) lights++;
                    if (e.Vegetation != null) plants += e.Vegetation.Plants.Count;
                }
            }
            catch (Exception ex)
            {
                fail++;
                if (fail <= 20) Console.WriteLine($"FAIL {path}: {ex.Message}");
            }
        }
        Console.WriteLine($"sectors {layout.Sectors.Count}, node files {files} (skipped {skipped}, failed {fail}), entities {entities:N0}");
        Console.WriteLine($"mesh instances {meshes:N0} ({distinct.Count} distinct, terrain cells {terrain}), speedtrees {trees:N0}, lights {lights:N0}, plants {plants:N0}");
        Console.WriteLine($"mesh origins X {minX:F0}..{maxX:F0}, Z {minZ:F0}..{maxZ:F0} cm, {sw.Elapsed.TotalSeconds:F1}s");
        if (Environment.GetEnvironmentVariable("G3_TREES") != null)
            foreach (var kv in treeKinds.OrderByDescending(k => k.Value)) Console.WriteLine($"{kv.Value,7} {kv.Key}");
        return fail == 0 ? 0 : 2;
    }

    static bool Match(PakEntry e, string[] args, int i) =>
        args.Length <= i || e.Path.IndexOf(args[i], StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Parse WeatherStates.g3ws (Library, else _intern, or a file path) and dump states, curves and environments.</summary>
    static int DumpWeather(string data, string file, string hours = null)
    {
        byte[] bytes;
        if (file != null && File.Exists(file)) bytes = File.ReadAllBytes(file);
        else
        {
            bytes = null; // CP 1.75 ships its own copy in Library (as G3WeatherImporter); vanilla _intern is the fallback
            foreach (var volume in new[] { "Library", "_intern" })
            {
                try { using var fs = new G3FileSystem(data, volume); var e = fs.GetByName("WeatherStates.g3ws"); if (e != null) { bytes = e.Read(); Console.WriteLine("source: " + volume); break; } }
                catch (IOException) { }
            }
            if (bytes == null) { Console.WriteLine("WeatherStates.g3ws not found"); return 1; }
        }
        var w = G3.Formats.Environment.WeatherStatesFile.Read(bytes);
        Console.WriteLine($"GEWS v{w.Version}: {w.States.Count} states, {w.Environments.Count} environments (parsed to end)");
        foreach (var s in w.States)
        {
            Console.WriteLine($"== {s.Name} (classVer {s.ClassVersion}) particles/s={s.ParticlesPerSecond} speed={s.SpeedMin}-{s.SpeedMax} size={s.SizeX}x{s.SizeY} dirScale={s.DirectionScale} tile={s.CurrentTextureTile} dizzer={s.SpawnPointDizzer} jitter={s.JitterPower}/{s.JitterSpeed} splash={s.SplashDuration} tile {s.SplashTextureTile} scale {s.SplashSizeScaleX}x{s.SplashSizeScaleY} loop='{s.LoopSample}' vol={s.LoopVolume}");
            for (int c = 0; c < s.Curves.Length; c++)
            {
                var sc = s.Curves[c];
                var line = new System.Text.StringBuilder($"  {(G3.Formats.Environment.WeatherCurve)c,-22} n={sc.Count,2} |");
                var hs = hours != null ? Array.ConvertAll(hours.Split(','), x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)) : new[] { 0f, 6, 9, 12, 15, 18, 21 };
                foreach (float h in hs)
                {
                    float t = h / 24f;
                    if (sc.IsColor) { var v = sc.EvaluateColor(t); line.Append($" {h,2}h({v.r:0.00},{v.g:0.00},{v.b:0.00})"); }
                    else line.Append($" {h,2}h:{sc.EvaluateFloat(t):0.###}");
                }
                Console.WriteLine(line);
            }
        }
        foreach (var e in w.Environments)
        {
            Console.WriteLine($"== env {e.Name} v{e.Version} climate={e.Climate} music={e.MusicLocation}");
            foreach (var p in e.Probabilities)
                Console.WriteLine($"  {p.StateName,-20} p={p.Probability:0.###} dur={p.MinDuration * 24:0.##}h..{p.MaxDuration * 24:0.##}h");
        }
        return 0;
    }
}
