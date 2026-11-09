using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G3.Formats.Animation;
using G3.Formats.Pak;

/// <summary>
/// `G3Cli anim validate`            parse every .xact/.xmot in _compiledAnimation to the exact payload end
/// `G3Cli anim actor  <name>`       dump an actor (nodes, meshes, materials)
/// `G3Cli anim motion <name>`       dump a motion (tracks, keys)
/// `G3Cli anim pose <actor> <motion> [frames]`  pose + skin the actor along the clip, print foot/bounds stats
/// `G3Cli anim names`               statistics of the motion-name fields
/// </summary>
static partial class Program
{
    static int AnimCommand(string data, string[] args)
    {
        string sub = args.Length > 1 ? args[1] : "validate";
        using var fs = new G3FileSystem(data, "_compiledAnimation");
        switch (sub)
        {
            case "validate": return AnimValidate(fs);
            case "actor": return AnimActor(fs, args[2]);
            case "motion": return AnimMotion(fs, args[2]);
            case "pose": return AnimPose(fs, args[2], args[3], args.Length > 4 ? int.Parse(args[4]) : 8);
            case "names": return AnimNames(fs);
            case "visemes": return AnimVisemes(fs, args[2], args[3]); // AnimVisemeCommands.cs
            case "beard": return AnimBeard(data, fs, args[2], args[3], args.Length > 4 ? int.Parse(args[4]) : 2); // AnimBeardCommands.cs (fix-246)
            case "effects": return AnimEffects(fs, args.Length > 2 ? args[2] : "");
            case "hold": return AnimHold(data, fs, args[2], args[3], args[4], args.Length > 5 ? args[5] : "Slot_RightHand_Weapon", args.Length > 6 ? float.Parse(args[6], System.Globalization.CultureInfo.InvariantCulture) : 0f);
            case "reach": return AnimReach(data, fs, args[2], args[3], args[4], args.Length > 5 ? args[5] : "Slot_RightHand_Weapon", args.Length > 6 ? int.Parse(args[6]) : 14); // AnimReachCommands.cs
            case "bone": return AnimBone(fs, args[2], args[3], args[4], args.Length > 5 ? int.Parse(args[5]) : 8);
            case "slots": return AnimSlots(fs, args.Length > 2 ? args[2] : "", args.Length > 3 ? args[3] : "Slot_RightHand_Weapon");
            case "list":
                foreach (var f in fs.Files.Select(f => Path.GetFileNameWithoutExtension(f.Path)).Where(f => f.IndexOf(args.Length > 2 ? args[2] : "", StringComparison.OrdinalIgnoreCase) >= 0))
                    Console.WriteLine(f);
                return 0;
            case "winding": return AnimWinding(data, args[2], args.Length > 3 ? args[3] : null);
            case "mat": return AnimMaterials(data, args.Skip(2).ToArray());
            case "layers": return AnimLayerAudit(data, fs, args.Length > 2 ? args[2] : ""); // fix-237
            case "npc": return NpcCommand(data, args.Skip(1).ToArray()); // NpcCommands.cs
        }
        Console.WriteLine("anim validate | actor <name> | motion <name> | pose <actor> <motion> [frames] | names | slots <filter> [slot] | hold <actor> <motion>[+overlay]|- <mesh> [slot] [t]");
        return 1;
    }

    static PakEntry AnimFind(G3FileSystem fs, string name, string ext)
    {
        if (!name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) name += ext;
        return fs.GetByName(name) ?? fs.Files.FirstOrDefault(f => Path.GetFileName(f.Path).StartsWith(name.Substring(0, name.Length - ext.Length), StringComparison.OrdinalIgnoreCase) && f.Path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
               ?? throw new FileNotFoundException(name);
    }

    /// <summary>Motions whose name contains <paramref name="filter"/> and that carry effect events: name, duration, frame:effect (time s).</summary>
    static int AnimEffects(G3FileSystem fs, string filter)
    {
        int n = 0;
        foreach (var e in fs.Files)
        {
            string file = Path.GetFileName(e.Path);
            if (!file.EndsWith(".xmot", StringComparison.OrdinalIgnoreCase) || file.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var m = Xmot.Read(e.Read(), file);
            if (m == null || m.Effects.Count == 0) continue;
            n++;
            Console.WriteLine($"{Path.GetFileNameWithoutExtension(file)} {m.Duration:F2}s: " +
                              string.Join(", ", m.Effects.Select(x => $"{x.KeyFrame}({x.KeyFrame * m.KeyStep:F2}s):{x.Name}")));
        }
        Console.WriteLine($"{n} motions with effects");
        return 0;
    }

    static int AnimValidate(G3FileSystem fs)
    {
        int deleted = 0, actors = 0, motions = 0, fail = 0, skinned = 0, lods = 0, fixedKeys = 0, effects = 0, badSkinNode = 0, unnormWeights = 0, maxInf = 0;
        long tris = 0, keys = 0;
        float longest = 0;
        var chunkStats = new Dictionary<int, int>();
        var interp = new Dictionary<string, int>();
        var nonUnitScale = 0; var shear = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var e in fs.Files.OrderBy(f => f.Path))
        {
            string ext = Path.GetExtension(e.Path).ToLowerInvariant();
            try
            {
                if (ext == ".xact")
                {
                    var a = Xact.Read(e.Read(), Path.GetFileName(e.Path));
                    actors++;
                    lods += a.Lods.Count;
                    foreach (var act in a.Lods.Append(a.Actor))
                    {
                        foreach (var kv in act.OtherChunks) chunkStats[kv.Key] = chunkStats.TryGetValue(kv.Key, out var c) ? c + kv.Value : kv.Value;
                        foreach (var n in act.Nodes)
                        {
                            if (Math.Abs(n.Scale.X - 1) > 1e-3 || Math.Abs(n.Scale.Y - 1) > 1e-3 || Math.Abs(n.Scale.Z - 1) > 1e-3) nonUnitScale++;
                            if (n.Shear.Length > 1e-4) shear++;
                            if (!string.IsNullOrEmpty(n.ParentName) && n.Parent < 0) throw new Exception($"parent {n.ParentName} of {n.Name} missing");
                        }
                        foreach (var m in act.Meshes)
                        {
                            tris += m.Indices.Length / 3;
                            if (m.Skin == null) continue;
                            if (act == a.Actor) skinned++;
                            foreach (var inf in m.Skin)
                            {
                                maxInf = Math.Max(maxInf, inf.Length);
                                float s = 0;
                                foreach (var i in inf) { s += i.Weight; if (i.Node >= act.Nodes.Count) badSkinNode++; }
                                if (inf.Length > 0 && Math.Abs(s - 1) > 0.01f) unnormWeights++;
                            }
                        }
                        if (act.VertexColors.Count != act.Tangents.Count) throw new Exception("lod attribute mismatch");
                    }
                }
                else if (ext == ".xmot")
                {
                    var m = Xmot.Read(e.Read(), Path.GetFileName(e.Path));
                    if (m == null) { deleted++; continue; }
                    motions++;
                    longest = Math.Max(longest, m.Duration);
                    fixedKeys += m.FixedKeys;
                    if (m.Effects.Count > 0) effects++;
                    foreach (var t in m.Tracks)
                    {
                        keys += t.Position.Length + t.Rotation.Length + t.Scale.Length;
                        void I(string k, char c, int n) { if (n == 0) return; var key = k + c; interp[key] = interp.TryGetValue(key, out var x) ? x + 1 : 1; }
                        I("P", t.PositionInterp, t.Position.Length); I("R", t.RotationInterp, t.Rotation.Length); I("S", t.ScaleInterp, t.Scale.Length);
                    }
                }
                else continue;
            }
            catch (Exception ex)
            {
                fail++;
                if (fail <= 20) Console.WriteLine($"FAIL {e.Path}: {ex.Message}" + (fail == 1 || Environment.GetEnvironmentVariable("G3_TRACE") != null ? ex.StackTrace : ""));
            }
        }
        Console.WriteLine($"actors {actors} (+{lods} embedded LoD actors), motions {motions} (+{deleted} empty patch tombstones), failed {fail}, {sw.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"  skinned meshes {skinned}, triangles {tris:N0}, max influences/vertex {maxInf}, weights not summing to 1: {unnormWeights}, skin node out of range: {badSkinNode}");
        Console.WriteLine($"  nodes with non-unit scale {nonUnitScale}, with shear {shear}");
        Console.WriteLine($"  other FXA chunks: {string.Join(", ", chunkStats.OrderBy(k => k.Key).Select(k => $"{(EmfxChunk)k.Key}({k.Key})×{k.Value}"))}");
        Console.WriteLine($"  keys {keys:N0}, interpolation {string.Join(", ", interp.Select(k => $"{k.Key}:{k.Value}"))}, renormalised rotation keys {fixedKeys}, clips with frame effects {effects}, longest {longest:F2}s");
        return fail == 0 ? 0 : 2;
    }

    static int AnimActor(G3FileSystem fs, string name)
    {
        var e = AnimFind(fs, name, ".xact");
        var a = Xact.Read(e.Read(), e.Path);
        Console.WriteLine($"{e.Path}: v{a.Version} bounds {a.BoundsMin}..{a.BoundsMax} native {a.NativeFileSize} lookAt {a.LookAt.Count} lods {a.Lods.Count}");
        foreach (var l in a.LookAt) Console.WriteLine($"  lookAt {l.Node} speed {l.InterpolationSpeed} {l.Min}..{l.Max}");
        int li = 0;
        foreach (var act in a.Lods.Append(a.Actor))
        {
            Console.WriteLine(li < a.Lods.Count ? $"-- LoD actor {li}" : "-- main actor");
            li++;
            var bind = ActorPose.Bind(act);
            for (int i = 0; i < act.Nodes.Count; i++)
            {
                var n = act.Nodes[i];
                Console.WriteLine($"  [{i,3}] {n.Name,-40} parent {n.Parent,3} pos {n.Position} rot {n.Rotation} world {bind[i].Pos}" +
                                  (Math.Abs(n.Scale.X - 1) > 1e-3 ? $" scale {n.Scale}" : ""));
            }
            foreach (var m in act.Meshes)
            {
                float minY = float.MaxValue, maxY = float.MinValue, minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
                foreach (var p in m.Positions) { minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y); minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z); }
                var used = m.Skin == null ? "" : string.Join(",", m.Skin.SelectMany(s => s).Select(s => (int)s.Node).Distinct().OrderBy(x => x));
                Console.WriteLine($"  mesh on node {m.Node} ({act.Nodes[m.Node].Name}): {m.OrgVertexCount} org verts, {m.Positions.Length} verts, {m.Indices.Length / 3} tris, uv sets {m.UvSets}, collision {m.IsCollisionMesh}, skin {(m.Skin != null)}");
                Console.WriteLine($"     bounds X {minX:F1}..{maxX:F1} Y {minY:F1}..{maxY:F1} Z {minZ:F1}..{maxZ:F1}; bones used: {used}");
                foreach (var s in m.SubMeshes)
                {
                    var ps = Enumerable.Range(s.VertexStart, s.VertexCount).Select(i => m.Positions[i]).ToList();
                    var bonesS = m.Skin == null ? "" : string.Join(",", Enumerable.Range(s.VertexStart, s.VertexCount).SelectMany(i => m.Skin[m.OrgVertex[i]]).Select(x => act.Nodes[x.Node].Name).Distinct());
                    Console.WriteLine($"     submesh mat {s.MaterialId} ({act.MaterialName(s.MaterialId)}) verts {s.VertexCount} tris {s.IndexCount / 3} uvsets {s.UvSets} X {ps.Min(p => p.X):F1}..{ps.Max(p => p.X):F1} Y {ps.Min(p => p.Y):F1}..{ps.Max(p => p.Y):F1} Z {ps.Min(p => p.Z):F1}..{ps.Max(p => p.Z):F1} bones {bonesS}");
                }
            }
            foreach (var mt in act.Materials)
                Console.WriteLine($"  material '{mt.Name}' shader '{mt.ShaderFile}' diffuse ({string.Join(",", mt.Diffuse.Select(x => x.ToString("0.##")))}) opacity {mt.Opacity} 2s {mt.DoubleSided} transp {mt.TransparencyType} layers: " +
                                  string.Join("; ", mt.Layers.Select(l => $"{l.MapType}:{l.Texture} [{BitConverter.ToString(l.Raw)}]")));
            foreach (var r in act.MaterialRefs) Console.WriteLine($"  materialRef lod {r.Lod} mat {r.MaterialIndex} -> {r.Name}");
            Console.WriteLine($"  vertex colour sets {act.VertexColors.Count} ({string.Join(",", act.VertexColors.Select(c => c.Length))}), other chunks {string.Join(", ", act.OtherChunks.Select(k => $"{k.Key}×{k.Value}"))}");
        }
        return 0;
    }

    static int AnimMotion(G3FileSystem fs, string name)
    {
        var e = AnimFind(fs, name, ".xmot");
        var m = Xmot.Read(e.Read(), e.Path);
        Console.WriteLine($"{e.Path}: v{m.Version} duration {m.Duration:F3}s step {m.KeyStep:F4}s ({1 / m.KeyStep:F1} fps) tracks {m.Tracks.Count} priority {m.Priority} effects {string.Join(", ", m.Effects.Select(x => $"{x.KeyFrame}:{x.Name}"))}");
        Console.WriteLine($"  name: {MotionName.Parse(Path.GetFileName(e.Path))}");
        foreach (var t in m.Tracks)
            Console.WriteLine($"  {t.Node,-40} P{t.Position.Length,4}{t.PositionInterp} R{t.Rotation.Length,4}{t.RotationInterp} S{t.Scale.Length,3} pose {t.PosePosition} {t.PoseRotation} bind {t.BindPosition} {t.BindRotation}" +
                              (t.Position.Length > 1 ? $" pos[0] {t.Position[0].Value} pos[last] {t.Position[^1].Value} @{t.Position[^1].Time:F3}" : ""));
        return 0;
    }

    /// <summary>Static pose a motion gives a slot node (tracks with 0 keys still carry a pose): distinct poses with counts.</summary>
    static int AnimSlots(G3FileSystem fs, string filter, string slot)
    {
        var groups = new Dictionary<string, List<string>>();
        foreach (var e in fs.Files)
        {
            if (!e.Path.EndsWith(".xmot", StringComparison.OrdinalIgnoreCase)) continue;
            string stem = Path.GetFileNameWithoutExtension(e.Path);
            if (stem.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            MotionResource m;
            try { m = Xmot.Read(e.Read(), e.Path); } catch { continue; }
            var t = m.Tracks.FirstOrDefault(x => string.Equals(x.Node, slot, StringComparison.OrdinalIgnoreCase));
            string key = t == null ? "(no track)" : $"P{t.Position.Length} R{t.Rotation.Length} pose {t.PosePosition} {t.PoseRotation}";
            if (!groups.TryGetValue(key, out var l)) groups[key] = l = new List<string>();
            l.Add(stem);
        }
        foreach (var g in groups.OrderByDescending(g => g.Value.Count))
            Console.WriteLine($"{g.Value.Count,5}  {g.Key}  e.g. {string.Join(", ", g.Value.Take(3))}");
        return 0;
    }

    /// <summary>
    /// Where a held item ends up: actor posed by a motion at time t, the item mesh parented to <paramref name="slot"/>
    /// with identity (G3 gCInventorySlot). Prints the slot and the mesh's far end (tip) in actor space, cm.
    /// </summary>
    static int AnimHold(string data, G3FileSystem fs, string actorName, string motionName, string meshName, string slot, float t)
    {
        var act = Xact.Read(AnimFind(fs, actorName, ".xact").Read()).Actor;
        int si = act.FindNode(slot);
        if (si < 0) { Console.WriteLine("no node " + slot); return 1; }
        ActorPose.Xf w;
        if (motionName == "-") w = ActorPose.Bind(act)[si];
        else
        {
            // "base+overlay": the overlay's tracks win (the upper-body layer), the base drives the rest
            MotionTrack[] map = null;
            foreach (var part in motionName.Split('+'))
            {
                var m = ActorPose.MapTracks(act, Xmot.Read(AnimFind(fs, part, ".xmot").Read()));
                if (map == null) map = m;
                else for (int i = 0; i < map.Length; i++) if (m[i] != null) map[i] = m[i];
            }
            var pose = ActorPose.Sample(act, map, ActorPose.HeldHelpers(act, map), t);
            w = pose[si];
            int head = Enumerable.Range(0, act.Nodes.Count).FirstOrDefault(i => act.Nodes[i].Name.EndsWith("Head_Head_1", StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"head {pose[head].Pos}; " + string.Join(" ", Enumerable.Range(0, act.Nodes.Count)
                .Where(i => act.Nodes[i].Name.EndsWith("Toe_1") || act.Nodes[i].Name.EndsWith("Hand_Hand_1") || act.Nodes[i].Name.EndsWith("Spine_Spine_1"))
                .Select(i => $"{act.Nodes[i].Name.Split('_').Skip(1).Aggregate((x, y) => x + y)} {pose[i].Pos}")));
        }
        using var mfs = new G3FileSystem(data, "_compiledMesh");
        var mesh = G3.Formats.Mesh.Xcmsh.Read(AnimFind(mfs, meshName, ".xcmsh").Read());
        V3 tip = default, tail = default; float far = -1, farNeg = -1;
        foreach (var el in mesh.Elements)
            for (int i = 0; i < el.Positions.Length; i += 3)
            {
                var v = new V3(el.Positions[i], el.Positions[i + 1], el.Positions[i + 2]);
                if (v.Length > far) { far = v.Length; tip = v; }
            }
        foreach (var el in mesh.Elements)
            for (int i = 0; i < el.Positions.Length; i += 3)
            {
                var v = new V3(el.Positions[i], el.Positions[i + 1], el.Positions[i + 2]);
                float d = (v - tip).Length;
                if (d > farNeg) { farNeg = d; tail = v; }
            }
        V3 Out(V3 local) => w.Pos + w.Rot.Rotate(local);
        Console.WriteLine($"slot {slot} pos {w.Pos} rot {w.Rot}; axes X {w.Rot.Rotate(new V3(1, 0, 0))} Y {w.Rot.Rotate(new V3(0, 1, 0))} Z {w.Rot.Rotate(new V3(0, 0, 1))}");
        Console.WriteLine($"mesh bounds ({mesh.BoundsMin[0]:F1},{mesh.BoundsMin[1]:F1},{mesh.BoundsMin[2]:F1})..({mesh.BoundsMax[0]:F1},{mesh.BoundsMax[1]:F1},{mesh.BoundsMax[2]:F1}); tip {tip} -> {Out(tip)}; other end {tail} -> {Out(tail)}");
        var dir = Out(tip) - w.Pos;
        Console.WriteLine($"tip direction from grip (actor space): {dir * (1 / Math.Max(1e-3f, dir.Length))}");
        return 0;
    }

    static int AnimPose(G3FileSystem fs, string actorName, string motionName, int frames)
    {
        var ea = AnimFind(fs, actorName, ".xact");
        var em = AnimFind(fs, motionName, ".xmot");
        var act = Xact.Read(ea.Read(), ea.Path).Actor;
        var mot = Xmot.Read(em.Read(), em.Path);
        var map = ActorPose.MapTracks(act, mot);
        var held = ActorPose.HeldHelpers(act, map);
        var bind = ActorPose.Bind(act);
        Console.WriteLine($"{ea.Path} × {em.Path}: {map.Count(x => x != null)}/{act.Nodes.Count} nodes tracked, {held.Count(x => x)} helpers held, unmatched tracks: " +
                          string.Join(",", mot.Tracks.Where(t => act.FindNode(t.Node) < 0).Select(t => t.Node)));
        Console.WriteLine("  helpers held: " + string.Join(",", Enumerable.Range(0, held.Length).Where(i => held[i]).Select(i => act.Nodes[i].Name)));
        Console.WriteLine("  untracked, not held: " + string.Join(",", Enumerable.Range(0, held.Length).Where(i => !held[i] && map[i] == null).Select(i => act.Nodes[i].Name)));
        // skin weights referencing held helpers?
        var weighted = new HashSet<int>(act.Meshes.Where(m => m.Skin != null).SelectMany(m => m.Skin.SelectMany(s => s)).Select(s => (int)s.Node));
        Console.WriteLine("  skin-weighted held helpers: " + string.Join(",", weighted.Where(i => held[i]).Select(i => act.Nodes[i].Name)));
        Console.WriteLine("  skin-weighted untracked nodes: " + string.Join(",", weighted.Where(i => map[i] == null).Select(i => act.Nodes[i].Name)));
        // t=0 vs bind
        var p0 = ActorPose.Sample(act, map, held, 0);
        float worst = 0; string worstName = null;
        for (int i = 0; i < p0.Length; i++) { float d = (p0[i].Pos - bind[i].Pos).Length; if (d > worst) { worst = d; worstName = act.Nodes[i].Name; } }
        Console.WriteLine($"  t=0 vs bind: max node offset {worst:F2} cm ({worstName})");
        for (int f = 0; f <= frames; f++)
        {
            float t = mot.Duration * f / Math.Max(1, frames);
            var pose = ActorPose.Sample(act, map, held, t);
            float minY = float.MaxValue, maxY = float.MinValue; V3 c = default; int n = 0;
            foreach (var m in act.Meshes)
                for (int v = 0; v < m.Positions.Length; v += 7)
                {
                    var p = ActorPose.SkinVertex(m, v, bind, pose);
                    minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y); c = c + p; n++;
                }
            var feet = Enumerable.Range(0, act.Nodes.Count).Where(i => act.Nodes[i].Name.IndexOf("Foot", StringComparison.OrdinalIgnoreCase) >= 0 || act.Nodes[i].Name.IndexOf("Toe", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(i => $"{act.Nodes[i].Name.Split('_').Skip(1).Aggregate((x, y) => x + y)}:{pose[i].Pos.Y:F0}").Take(6);
            Console.WriteLine($"  t {t:F2}s: skinned Y {minY:F1}..{maxY:F1}, centroid {(n > 0 ? c * (1f / n) : c)}, root {pose[0].Pos} | {string.Join(" ", feet)}");
        }
        return 0;
    }

    static int AnimNames(G3FileSystem fs)
    {
        var fields = new Dictionary<string, Dictionary<string, int>>();
        int ok = 0, bad = 0;
        foreach (var e in fs.Files.Where(f => f.Path.EndsWith(".xmot", StringComparison.OrdinalIgnoreCase)))
        {
            var n = MotionName.Parse(Path.GetFileName(e.Path));
            if (n == null) { bad++; if (bad < 15) Console.WriteLine("unparsed: " + e.Path); continue; }
            ok++;
            void Add(string k, string v) { if (!fields.TryGetValue(k, out var d)) fields[k] = d = new Dictionary<string, int>(); d[v ?? "-"] = d.TryGetValue(v ?? "-", out var c) ? c + 1 : 1; }
            Add("Rig", n.Rig); Add("Stance", n.Stance); Add("Left", n.LeftItem); Add("Right", n.RightItem); Add("Pose", n.Pose); Add("Action", n.Action);
            Add("Phase", n.Phase); Add("Variant", n.Variant); Add("Direction", n.Direction); Add("Weight", n.Weight.ToString()); Add("Pose2", n.Pose2); Add("Suffix", n.Suffix);
        }
        Console.WriteLine($"parsed {ok}, unparsed {bad}");
        foreach (var kv in fields)
            Console.WriteLine($"{kv.Key} ({kv.Value.Count}): " + string.Join(" ", kv.Value.OrderByDescending(x => x.Value).Take(40).Select(x => $"{x.Key}:{x.Value}")));
        return 0;
    }
}

static partial class Program
{
    /// <summary>`G3Cli anim mat <xshmat stem>...` — summary of the compiled materials actors reference.</summary>
    /// <summary>fix-237: actor materials whose FXA diffuse layer differs from the .xshmat diffuse (both shipped).</summary>
    static int AnimLayerAudit(string data, G3FileSystem fs, string filter)
    {
        using var mats = new G3FileSystem(data, "_compiledMaterial");
        using var imgs = new G3FileSystem(data, "_compiledImage");
        var images = new HashSet<string>(imgs.Files.Select(f => Path.GetFileNameWithoutExtension(f.Path)), StringComparer.OrdinalIgnoreCase);
        foreach (var e in fs.Files.Where(f => f.Path.EndsWith(".xact", StringComparison.OrdinalIgnoreCase) &&
                                              f.Path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(f => f.Path))
        {
            EmfxActor act;
            try { act = Xact.Read(e.Read(), e.Path).Actor; } catch { continue; }
            foreach (var r in act.MaterialRefs.Where(r => !string.IsNullOrEmpty(r.Name)).GroupBy(r => r.MaterialIndex).Select(g => g.First()))
            {
                var mc = r.MaterialIndex >= 0 && r.MaterialIndex < act.Materials.Count ? act.Materials[r.MaterialIndex] : null;
                string layer = mc?.Layers.FirstOrDefault(l => l.MapType == 1)?.Texture;
                string xsh = Path.GetFileNameWithoutExtension(r.Name);
                var me = mats.GetByName(xsh + ".xshmat");
                if (layer == null || me == null) continue;
                string shared;
                try { shared = G3.Formats.Material.Xshmat.Read(me.Read(), xsh).Diffuse?.Image; } catch { continue; }
                if (shared == null || string.Equals(shared, layer, StringComparison.OrdinalIgnoreCase)) continue;
                Console.WriteLine($"{Path.GetFileNameWithoutExtension(e.Path)}	{xsh}	layer {layer}{(images.Contains(layer) ? "" : " (missing)")}	xshmat {shared}{(images.Contains(shared) ? "" : " (missing)")}");
            }
        }
        return 0;
    }

    static int AnimMaterials(string data, string[] names)
    {
        using var fs = new G3FileSystem(data, "_compiledMaterial");
        foreach (var n in names)
        {
            var e = fs.GetByName(n + ".xshmat");
            if (e == null) { Console.WriteLine($"{n}: missing"); continue; }
            var m = G3.Formats.Material.Xshmat.Read(e.Read(), n);
            Console.WriteLine($"{n}: shader {m.ShaderClass} blend {m.Blend} mask {m.MaskReference} diffuse {m.Diffuse?.Image} normal {m.Normal?.Image} spec {m.Specular?.Image} opacity {m.Opacity?.Image} color {(m.DiffuseColor == null ? "-" : string.Join(",", m.DiffuseColor))} specOn {m.EnableSpecular}");
            DumpMaterial(e.Read());
        }
        return 0;
    }
}

static partial class Program
{
    /// <summary>Fraction of triangles whose cross(p1-p0, p2-p0) agrees with the stored vertex normals.</summary>
    static string WindingStats(IEnumerable<(V3 a, V3 b, V3 c, V3 n)> tris)
    {
        int pos = 0, neg = 0;
        foreach (var (a, b, c, n) in tris)
        {
            var u = b - a; var v = c - a;
            var x = new V3(u.Y * v.Z - u.Z * v.Y, u.Z * v.X - u.X * v.Z, u.X * v.Y - u.Y * v.X);
            float d = x.X * n.X + x.Y * n.Y + x.Z * n.Z;
            if (d > 0) pos++; else if (d < 0) neg++;
        }
        return $"cross·normal > 0: {pos}, < 0: {neg}";
    }

    static int AnimWinding(string data, string actor, string mesh)
    {
        using (var fs = new G3FileSystem(data, "_compiledAnimation"))
        {
            var a = Xact.Read(AnimFind(fs, actor, ".xact").Read(), actor).Actor;
            foreach (var m in a.Meshes)
                Console.WriteLine($"{actor} mesh {m.Node}: " + WindingStats(Enumerable.Range(0, m.Indices.Length / 3).Select(t =>
                {
                    int i0 = m.Indices[t * 3], i1 = m.Indices[t * 3 + 1], i2 = m.Indices[t * 3 + 2];
                    var n = m.Normals[i0] + m.Normals[i1] + m.Normals[i2];
                    return (m.Positions[i0], m.Positions[i1], m.Positions[i2], n);
                })));
        }
        if (mesh != null)
            using (var fs = new G3FileSystem(data, "_compiledMesh"))
            {
                var r = G3.Formats.Mesh.Xcmsh.Read(fs.GetByName(mesh).Read(), mesh);
                foreach (var e in r.Elements)
                {
                    V3 P(uint i) => new V3(e.Positions[i * 3], e.Positions[i * 3 + 1], e.Positions[i * 3 + 2]);
                    V3 N(uint i) => new V3(e.Normals[i * 3], e.Normals[i * 3 + 1], e.Normals[i * 3 + 2]);
                    Console.WriteLine($"{mesh} element: " + WindingStats(Enumerable.Range(0, e.Indices.Length / 3).Select(t =>
                    {
                        uint i0 = e.Indices[t * 3], i1 = e.Indices[t * 3 + 1], i2 = e.Indices[t * 3 + 2];
                        return (P(i0), P(i1), P(i2), N(i0) + N(i1) + N(i2));
                    })));
                }
            }
        return 0;
    }
}
