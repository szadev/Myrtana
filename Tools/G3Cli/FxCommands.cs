using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G3.Formats.Audio;
using G3.Formats.Genome;
using G3.Formats.Pak;
using G3.Formats.Templates;
using G3.Formats.Effects;

/// <summary>
/// `G3Cli fx census`              g3.efm command classes + property names; PS classes of templates spawned by effects
/// `G3Cli fx dump <template>`     every header / property set of a template (+ post-data hex)
/// `G3Cli fx effect <filter>`     effects (commands with properties) matching a filter, with spawned template names
/// Docs/formats-effects.md
/// </summary>
static partial class Program
{
    static int FxCommand(string data, string[] args)
    {
        string sub = args.Length > 1 ? args[1] : "census";
        switch (sub)
        {
            case "census": return FxCensus(data);
            case "dump": return FxDump(data, args[2]);
            case "effect": return FxEffect(data, args.Length > 2 ? args[2] : "");
            case "post": return FxPost(data, args.Length > 2 ? args[2] : "eCParticle_PS", args.Length > 3 ? int.Parse(args[3]) : 20);
            case "blocks": return FxBlocks(data, args.Length > 2 ? int.Parse(args[2]) : 10);
            case "list": return FxList(data);
            case "world": return FxWorld(data);
            case "worldlights": return FxWorldLights(data, args.Length > 2 ? args[2] : "");
            case "parse": return FxParseAll(data, args.Length > 2 ? args[2] : null);
            case "tex": return FxTex(data, args[2], args.Length > 3 ? args[3] : null);
            case "props": return FxProps(data, args.Length > 2 ? args[2] : "eCParticle_PS", args[3], args.Length > 4 ? args[4] : null);
            case "items": return FxItems(data);
            case "npctorch": return FxNpcTorches(data);

        }
        Console.WriteLine("fx census | dump <template> | effect <filter> | post <class> [n] | props <class> <prop>");
        return 1;
    }

    /// <summary>Raw command objects of g3.efm (EffectMap only keeps audio-relevant ones).</summary>
    static List<(string effect, PropertyObject cmd, GenomeReader g, long post)> FxRawCommands(string data)
    {
        var bytes = AudioLib(data, "g3.efm");
        var g = new GenomeReader(bytes);
        var r = g.R;
        var res = new List<(string, PropertyObject, GenomeReader, long)>();
        r.ReadUInt16();
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            string name = g.Str();
            r.ReadUInt16();
            int cmds = r.ReadInt32();
            for (int c = 0; c < cmds; c++)
            {
                var o = g.ReadObject();
                if (o == null) continue;
                res.Add((name, o, g, g.Position));
                g.Position = o.End;
            }
        }
        return res;
    }

    static int FxCensus(string data)
    {
        var cmds = FxRawCommands(data);
        foreach (var grp in cmds.GroupBy(c => c.cmd.ClassName))
        {
            var f = grp.First();
            Console.WriteLine($"{grp.Key}: {grp.Count()} (v{f.cmd.ClassVersion}, post {f.cmd.End - f.post} B) props: " +
                              string.Join(", ", f.cmd.Properties.Select(p => p.Key + ":" + p.Value.Type)));
        }
        var map = EffectMap.Read(AudioLib(data, "g3.efm"));
        var tpl = LoadTemplates(data);
        var psCount = new Dictionary<string, int>();
        int missing = 0;
        var seen = new HashSet<Guid>();
        foreach (var e in map.Effects.Values)
            foreach (var guid in e.SpawnTemplates)
            {
                var f = tpl.Get(guid);
                if (f == null) { missing++; continue; }
                if (!seen.Add(f.Guid)) continue;
                foreach (var h in f.Headers)
                    foreach (var ps in h.PropertySets)
                        psCount[ps.ClassName] = psCount.TryGetValue(ps.ClassName, out var k) ? k + 1 : 1;
            }
        Console.WriteLine($"spawned templates: {seen.Count} distinct, {missing} GUIDs not found");
        foreach (var kv in psCount.OrderByDescending(k => k.Value)) Console.WriteLine($"  {kv.Value,5} {kv.Key}");
        int all = 0, vfx = 0;
        foreach (var f in tpl.Files)
        {
            if (f.IsDeleted) continue;
            if (f.Headers.Any(h => h.Get("eCParticle_PS") != null)) { all++; if (!seen.Contains(f.Guid)) vfx++; }
        }
        Console.WriteLine($"templates with eCParticle_PS: {all} (not spawned by any effect: {vfx})");
        return 0;
    }

    static int FxDump(string data, string name)
    {
        var tpl = LoadTemplates(data);
        var f = tpl.Get(name);
        if (f == null && Guid.TryParse(name, out var gid)) f = tpl.Get(gid);
        if (f == null) { Console.WriteLine("not found"); return 1; }
        Console.WriteLine($"{f.Path} guid {f.Guid}");
        for (int i = 0; i < f.Headers.Count; i++)
        {
            var h = f.Headers[i];
            Console.WriteLine($"-- header {i} '{h.Name}' parent {h.Parent} helper {h.HelperParent} ref {h.RefTemplate} pos ({h.World[12]:G4},{h.World[13]:G4},{h.World[14]:G4}) scale {h.UniformScaling}");
            for (int k = 0; k < h.PropertySets.Count; k++)
            {
                var ps = h.PropertySets[k];
                long post = h.PostDataStart[k];
                Console.WriteLine($"   [{ps.ClassName}] v{ps.ClassVersion} post {ps.End - post} B");
                if (ps.ClassName == "eCParticle_PS" || ps.ClassName.Contains("Light") || ps.ClassName.Contains("Decal") || ps.ClassName.Contains("Mesh") || ps.ClassName.Contains("Effect") || ps.ClassName.Contains("Audio") || Environment.GetEnvironmentVariable("FX_ALL") == "1")
                    foreach (var p in ps.Properties)
                        if (!FxIsDefault(p.Key, p.Value))
                            Console.WriteLine($"      {p.Key} = {FxFmt(h.Source, p.Value)}");
                if (ps.End - post > 0)
                {
                    var bytes = new byte[Math.Min(512, ps.End - post)];
                    Array.Copy(h.Source.Data, post, bytes, 0, bytes.Length);
                    Console.WriteLine("      post: " + BitConverter.ToString(bytes).Replace("-", " "));
                }
            }
        }
        return 0;
    }

    static bool FxIsDefault(string k, Property p) => Environment.GetEnvironmentVariable("FX_ALL") != "1" && FxQuiet.Contains(k);
    static readonly HashSet<string> FxQuiet = new HashSet<string> {
        "SpawningSound","SpawningSoundIndex","SpawningSoundProbability","CollisionSound","CollisionSoundIndex","CollisionSoundProbability",
        "MaxSimultaneousSounds","SoundPriority","TriggerTag","ResetOnTrigger","TriggerNumParticles","TriggerParticlesPerSecond",
        "DampingFactor","UseRotationDamping","RotationDampingFactor","ExtentMulitplier","MinSquaredVelocity","CollideWithCharacters",
        "CollideWithDynamic","CollideWithStatic","CollideWithTransparent","SpawnFromOtherEmitter","SpawnNumParticles","UseSpawnedVelocityScale",
        "SpawnedVelocityScale","MaxAbsoluteVelocity","Target","AddLocationFromTarget","WarmupTicksPerSecond","SecondsBeforeInactive",
        "TicksPerSecond","AlphaReference","Instanced","UseMaxCollisions","MaxCollisions" };

    static string FxFmt(GenomeReader g, Property p)
    {
        var v = p.Value;
        if (p.Type == "bCRange1" && v.Length == 8) return $"[{BitConverter.ToSingle(v, 0):G4}..{BitConverter.ToSingle(v, 4):G4}]";
        if (p.Type == "bCRange3" && v.Length >= 24)
            return $"x[{BitConverter.ToSingle(v, 0):G4}..{BitConverter.ToSingle(v, 12):G4}] y[{BitConverter.ToSingle(v, 4):G4}..{BitConverter.ToSingle(v, 16):G4}] z[{BitConverter.ToSingle(v, 8):G4}..{BitConverter.ToSingle(v, 20):G4}]"; // min vector, max vector
        if (p.Type == "bCRange3") return "R3 " + BitConverter.ToString(v);
        if (p.Type == "bCFloatAlphaColor" || p.Type == "bCFloatColor")
            return $"{p.Type} " + string.Join(" ", Enumerable.Range(0, v.Length / 4).Select(i => BitConverter.ToSingle(v, i * 4).ToString("G3")));
        if (p.Type == "char") return v[0].ToString();
        return Fmt(g, p);
    }

    static int FxEffect(string data, string filter)
    {
        var tpl = LoadTemplates(data);
        foreach (var grp in FxRawCommands(data).Where(c => c.effect.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).GroupBy(c => c.effect))
        {
            Console.WriteLine(grp.Key);
            foreach (var c in grp)
            {
                var parts = new List<string>();
                foreach (var p in c.cmd.Properties)
                {
                    if (p.Value.Type.EndsWith("TemplateEntityProxy") && p.Value.Value.Length >= 19 && p.Value.Value[2] != 0)
                    {
                        var b = new byte[16]; Array.Copy(p.Value.Value, 3, b, 0, 16);
                        parts.Add(p.Key + "=" + (tpl.Get(new Guid(b))?.Name ?? new Guid(b).ToString()));
                    }
                    else parts.Add(p.Key + "=" + FxFmt(c.g, p.Value));
                }
                Console.WriteLine($"   {c.cmd.ClassName.Replace("gCEffectCommand", "")} {string.Join(" ", parts)}" + (c.cmd.End - c.post > 0 ? $" +post {c.cmd.End - c.post}B" : ""));
            }
        }
        return 0;
    }

    /// <summary>Histogram of post-data sizes of a PS class over all templates; hex of the first n distinct sizes.</summary>
    static int FxPost(string data, string cls, int n)
    {
        var tpl = LoadTemplates(data);
        var sizes = new Dictionary<long, int>();
        int shown = 0;
        foreach (var f in tpl.Files)
            foreach (var h in f.Headers)
                for (int k = 0; k < h.PropertySets.Count; k++)
                {
                    var ps = h.PropertySets[k];
                    if (ps.ClassName != cls) continue;
                    long len = ps.End - h.PostDataStart[k];
                    sizes[len] = sizes.TryGetValue(len, out var c) ? c + 1 : 1;
                    if (shown < n && sizes[len] == 1)
                    {
                        shown++;
                        var bytes = new byte[Math.Min(400, len)];
                        Array.Copy(h.Source.Data, h.PostDataStart[k], bytes, 0, bytes.Length);
                        Console.WriteLine($"{f.Name} v{ps.ClassVersion} {len} B: {BitConverter.ToString(bytes).Replace("-", " ")}");
                    }
                }
        foreach (var kv in sizes.OrderBy(k => k.Key)) Console.WriteLine($"  post {kv.Key} B: {kv.Value}");
        return 0;
    }

    /// <summary>Histogram of a property's formatted values over all templates.</summary>
    /// <summary>Histogram of one property; with <paramref name="names"/> also lists the templates whose value contains it.</summary>
    static int FxProps(string data, string cls, string prop, string names = null)
    {
        var tpl = LoadTemplates(data);
        var hist = new Dictionary<string, int>();
        foreach (var f in tpl.Files)
            foreach (var h in f.Headers)
                foreach (var ps in h.PropertySets)
                {
                    if (ps.ClassName != cls) continue;
                    var p = ps.Get(prop);
                    string v = p == null ? "(none)" : FxFmt(h.Source, p);
                    hist[v] = hist.TryGetValue(v, out var c) ? c + 1 : 1;
                    if (names != null && v.Contains(names)) Console.WriteLine($"  {f.Name}: {v}");
                }
        foreach (var kv in hist.OrderByDescending(k => k.Value).Take(40)) Console.WriteLine($"  {kv.Value,5} {kv.Key}");
        return 0;
    }

    /// <summary>Generic block structure of eCParticle_PS post data: {u32 1, u32 size, u32 count, keys}* + trailer.</summary>
    static int FxBlocks(string data, int n)
    {
        var tpl = LoadTemplates(data);
        var shapes = new Dictionary<string, int>();
        int shown = 0;
        foreach (var f in tpl.Files)
            foreach (var h in f.Headers)
                for (int k = 0; k < h.PropertySets.Count; k++)
                {
                    var ps = h.PropertySets[k];
                    if (ps.ClassName != "eCParticle_PS") continue;
                    var d = h.Source.Data;
                    long p = h.PostDataStart[k], end = ps.End;
                    var parts = new List<string>();
                    var vals = new List<string>();
                    for (int b = 0; b < 5; b++)
                    {
                        uint ver = BitConverter.ToUInt32(d, (int)p), size = BitConverter.ToUInt32(d, (int)p + 4);
                        int cnt = size == 0 ? 0 : BitConverter.ToInt32(d, (int)p + 8);
                        int ks = cnt == 0 ? 0 : (int)size / cnt;
                        parts.Add($"{ver}:{ks}x{cnt}");
                        var kv = new List<string>();
                        for (int i = 0; i < cnt; i++)
                            kv.Add(string.Join(",", Enumerable.Range(0, ks / 4).Select(j => BitConverter.ToSingle(d, (int)p + 12 + i * ks + j * 4).ToString("G3"))));
                        vals.Add("[" + string.Join(" ", kv) + "]");
                        p += 8 + (size == 0 ? 0 : 4 + size);
                    }
                    var trailer = new byte[Math.Min(40, end - p)];
                    Array.Copy(d, p, trailer, 0, trailer.Length);
                    string shape = string.Join(" ", parts) + " | trailer " + (end - p) + "B " + BitConverter.ToString(trailer, 0, Math.Min(8, trailer.Length));
                    string key = string.Join(" ", parts.Select(x => x.Substring(0, x.IndexOf('x')))) + " | " + (end - p) + "B " + BitConverter.ToString(trailer, 0, Math.Min(4, trailer.Length));
                    shapes[key] = shapes.TryGetValue(key, out var c) ? c + 1 : 1;
                    string flags = $"vel{B(ps, "UseVelocityScale")} rev{B(ps, "UseRevolutionScale")} size{B(ps, "UseSizeScale")} sub{B(ps, "UseSubdivisionScale")} col{B(ps, "UseColorScale")} spawnvel{B(ps, "UseSpawnedVelocityScale")}";
                    if (shown++ < n) Console.WriteLine($"{f.Name}: {shape} {flags}" + NL + "    " + string.Join(NL + "    ", vals));
                }
        foreach (var kv in shapes.OrderByDescending(k => k.Value)) Console.WriteLine($"  {kv.Value,5} {kv.Key}");
        return 0;
    }

    static readonly string NL = Environment.NewLine;
    static int B(PropertyObject ps, string name) => ps.Get(name)?.AsBool() == true ? 1 : 0;

    /// <summary>Every template with eCParticle_PS: guid, name, effects spawning it.</summary>
    static int FxList(string data)
    {
        var tpl = LoadTemplates(data);
        var map = EffectMap.Read(AudioLib(data, "g3.efm"));
        var by = new Dictionary<Guid, List<string>>();
        foreach (var e in map.Effects.Values)
            foreach (var g0 in e.SpawnTemplates)
            {
                var g = tpl.Get(g0)?.Guid ?? g0;
                if (!by.TryGetValue(g, out var l)) by[g] = l = new List<string>();
                l.Add(e.Name);
            }
        foreach (var f in tpl.Files.OrderBy(f => f.Name))
        {
            if (f.IsDeleted || !f.Headers.Any(h => h.Get("eCParticle_PS") != null)) continue;
            by.TryGetValue(f.Guid, out var l);
            Console.WriteLine($"{f.Guid} {f.Name} {f.Headers.Count(h => h.Get("eCParticle_PS") != null)} emitters | {(l == null ? "-" : string.Join(",", l))}");
        }
        return 0;
    }

    /// <summary>Item templates (gCItem_PS) that carry a gCEffect_PS effect, with its offset (effects-2: torches, magic weapons, arrows).</summary>
    static int FxItems(string data)
    {
        var tpl = LoadTemplates(data);
        foreach (var f in tpl.Files.Where(f => !f.IsDeleted).OrderBy(f => f.Name))
        foreach (var h in f.Headers.Where(h => !h.HelperParent && h.Get("gCItem_PS") != null))
        {
            var fx = h.Get("gCEffect_PS");
            var light = h.Get("eCPointLight_PS");
            var proj = h.Get("gCProjectile_PS");
            string effect = fx != null ? FmtProp(h.Source, fx, "Effect") : "";
            string impact = proj != null ? FmtProp(h.Source, proj, "EffectPointImpact") + "/" + FmtProp(h.Source, proj, "EffectTargetHit") : "";
            if (effect.Length <= 2 && light == null && impact.Replace("\"", "").Length <= 1) continue;
            var ia = h.Get("gCInteraction_PS");
            Console.WriteLine($"{f.Name,-36} {effect,-40} offset {(fx != null ? FmtProp(h.Source, fx, "Offset") : "-")} use {(ia != null ? FmtProp(h.Source, ia, "UseType") : "-")}" +
                              (light != null ? $" light {FmtProp(h.Source, light, "Color")} r {FmtProp(h.Source, light, "Range")} i {FmtProp(h.Source, light, "Intensity")} at {FmtProp(h.Source, light, "PositionOffset")} fx {FmtProp(h.Source, light, "Effect")}" : "") +
                              (impact.Replace("\"", "").Length > 1 ? " impact " + impact : ""));
        }
        return 0;
    }

    /// <summary>Live NPC spawns whose inventory holds a torch item (use type Torch), with the slot it sits in.</summary>
    static int FxNpcTorches(string data)
    {
        var tpl = LoadTemplates(data);
        var torches = new HashSet<Guid>();
        foreach (var f in tpl.Files.Where(f => !f.IsDeleted))
        foreach (var h in f.Headers.Where(h => !h.HelperParent && h.Get("gCInteraction_PS") != null && h.Get("gCItem_PS") != null))
            if (FmtProp(h.Source, h.Get("gCInteraction_PS"), "UseType") == "24") torches.Add(f.Guid);
        using var fs = new G3FileSystem(data, "Projects_compiled");
        const string vol = "Projects_compiled/";
        var layout = G3.Formats.World.WorldLayout.Build(fs.Files.Select(f => f.Path), p => fs.Get(vol + p)?.Read());
        var all = G3.Formats.Animation.NpcSpawns.LoadWorld(layout, p => fs.Get(vol + p)?.Read(), new List<string>());
        int n = 0;
        foreach (var s in all.Where(s => s.Enabled && !G3.Formats.Animation.NpcSpawns.IsTestSector(s.Sector) && s.Inventory != null))
            for (int i = 0; i < s.Inventory.Stacks.Count; i++)
            {
                if (!torches.Contains(tpl.Get(s.Inventory.Stacks[i].Template)?.Guid ?? s.Inventory.Stacks[i].Template)) continue;
                var slot = s.Inventory.Slots.FirstOrDefault(sl => sl.Stack == i);
                Console.WriteLine($"{s.Name,-32} {tpl.Get(s.Inventory.Stacks[i].Template)?.Name} x{s.Inventory.Stacks[i].Amount} slot {(slot != null && slot.Stack >= 0 ? slot.Slot.ToString() : "-")} routine {s.Routine}");
                n++;
            }
        Console.WriteLine($"{n} torch stacks on {all.Count} spawns; torch templates {torches.Count}");
        return 0;
    }

    static string FmtProp(GenomeReader g, PropertyObject ps, string name)
    {
        foreach (var p in ps.Properties) if (p.Key == name) return Fmt(g, p.Value);
        return "";
    }

    /// <summary>World point lights (eCPointLight_PS on world entities): histogram of the light properties, grouped by the
    /// entity's gCEffect_PS effect (filter = substring of that effect name or the entity name).</summary>
    static int FxWorldLights(string data, string filter)
    {
        using var fs = new G3FileSystem(data, "Projects_compiled");
        var hist = new Dictionary<string, int>();
        foreach (var (path, e) in AudioWorldEntities(fs, true))
        {
            var sets = e.RawPropertySets ?? new();
            var light = sets.FirstOrDefault(p => p.ClassName == "eCPointLight_PS" || p.ClassName == "eCStaticPointLight_PS");
            if (light == null) continue;
            var fx = sets.FirstOrDefault(p => p.ClassName == "gCEffect_PS");
            string eff = fx != null ? G3.Formats.Items.Props.Str(e.Source, fx, "Effect") ?? "" : "-";
            if (filter.Length > 0 && eff.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 && e.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            string F(string n) { var p = light.Get(n); return p == null ? "-" : FxFmt(e.Source, p); }
            string key = $"{eff} I={F("Intensity")} Fx={F("Effect")} Per={F("EffectPeriod")} R={F("Range")} {F("Color")} Off={F("PositionOffset")}/{F("Offset")} fxOff={(fx != null && fx.Get("Offset") is { } o ? FxFmt(e.Source, o) : "-")}";
            hist[key] = hist.TryGetValue(key, out var c) ? c + 1 : 1;
        }
        foreach (var kv in hist.OrderByDescending(k => k.Value).Take(30)) Console.WriteLine($"  {kv.Value,5} {kv.Key}");
        return 0;
    }

    /// <summary>World census: gCEffect_PS effects and placed eCParticle_PS entities (by entity name).</summary>
    static int FxWorld(string data)
    {
        using var fs = new G3FileSystem(data, "Projects_compiled");
        var eff = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var part = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int items = 0, npcs = 0, partWithMesh = 0, children = 0;
        foreach (var (path, e) in AudioWorldEntities(fs, true))
            foreach (var ps in e.RawPropertySets ?? new())
            {
                if (ps.ClassName == "gCEffect_PS")
                {
                    if (e.IsItem) { items++; continue; }
                    if (e.IsNpc) { npcs++; continue; }
                    var n = G3.Formats.Items.Props.Str(e.Source, ps, "Effect") ?? "";
                    eff[n] = eff.TryGetValue(n, out var c) ? c + 1 : 1;
                }
                else if (ps.ClassName == "eCParticle_PS")
                {
                    part[e.Name] = part.TryGetValue(e.Name, out var c) ? c + 1 : 1;
                    if (e.Resource != null) partWithMesh++;
                    if (e.Parent >= 0) children++;
                }
            }
        Console.WriteLine($"gCEffect_PS on world entities: {eff.Values.Sum()} (items {items}, npcs {npcs})");
        foreach (var kv in eff.OrderByDescending(k => k.Value)) Console.WriteLine($"  {kv.Value,6} {kv.Key}");
        Console.WriteLine($"placed eCParticle_PS: {part.Values.Sum()} (with mesh {partWithMesh}, with parent {children})");
        foreach (var kv in part.OrderByDescending(k => k.Value).Take(60)) Console.WriteLine($"  {kv.Value,6} {kv.Key}");
        return 0;
    }

    /// <summary>Parse every template with eCParticle_PS / eCPointLight_PS through VfxTemplateDef (must not throw).</summary>
    static int FxParseAll(string data, string show)
    {
        var tpl = LoadTemplates(data);
        int ok = 0, fail = 0, lights = 0, haze = 0;
        foreach (var f in tpl.Files)
        {
            if (f.IsDeleted || !f.Headers.Any(h => h.Get("eCParticle_PS") != null || h.Get("eCPointLight_PS") != null)) continue;
            try
            {
                var d = VfxTemplateDef.From(f);
                ok++;
                if (d.Light != null) lights++;
                if (d.Emitter?.IsHeatHaze == true) haze++;
                if (show != null && f.Name.IndexOf(show, StringComparison.OrdinalIgnoreCase) >= 0 && d.Emitter is { } e)
                    Console.WriteLine($"{f.Name}: tex {e.Texture} {e.DrawStyle} max {e.MaxParticles} pps {e.ParticlesPerSecond} respawn {e.RespawnDeadParticles} life {e.Lifetime} " +
                                      $"size {string.Join(",", e.StartSize)} uni {e.UniformSize} sub {e.NumU}x{e.NumV} {e.SubdivisionStart}-{e.SubdivisionEnd} " +
                                      $"color [{string.Join(" ", e.ColorScale)}] sizeScale {(e.UseSizeScale ? string.Join(" ", e.SizeScale) : "-")}" +
                                      (d.Light != null ? $" LIGHT rgb({d.Light.R:0.##},{d.Light.G:0.##},{d.Light.B:0.##}) r {d.Light.Range} i {d.Light.Intensity} fx {d.Light.Effect}" : ""));
            }
            catch (Exception ex) { fail++; Console.WriteLine($"FAIL {f.Name}: {ex.Message}"); }
        }
        Console.WriteLine($"vfx templates parsed {ok}, failed {fail}, with light {lights}, heat haze {haze}");
        return fail == 0 ? 0 : 2;
    }

    /// <summary>fx tex &lt;stem&gt; [pngDir]: lists matching effect textures; with pngDir also writes the top mip (DXT1/DXT3) as PNG.</summary>
    static int FxTex(string data, string stem, string pngDir)
    {
        using var fs = new G3FileSystem(data, "_compiledImage");
        foreach (var e in fs.Files.Where(e => e.Path.IndexOf(stem, StringComparison.OrdinalIgnoreCase) >= 0).Take(20))
        {
            var img = G3.Formats.Image.Ximg.Read(e.Read());
            Console.WriteLine($"{e.Path}: {img.Width}x{img.Height} {img.Format} mips {img.Mips.Length}");
            if (pngDir == null) continue;
            byte[] rgba = img.Format == G3.Formats.Image.PixelFormat.DXT1 ? DecodeDxt1(img.Mips[0], img.Width, img.Height)
                : img.Format == G3.Formats.Image.PixelFormat.DXT3 ? G3.Formats.Image.Dxt.DecodeDxt3(img.Mips[0], img.Width, img.Height) : null;
            if (rgba == null) continue;
            Directory.CreateDirectory(pngDir);
            WritePng(Path.Combine(pngDir, Path.GetFileNameWithoutExtension(e.Path) + ".png"), rgba, img.Width, img.Height);
        }
        return 0;
    }

    static byte[] DecodeDxt1(byte[] src, int w, int h)
    {
        var dst = new byte[w * h * 4];
        var pal = new byte[16];
        int o = 0;
        for (int by = 0; by < (h + 3) / 4; by++)
        for (int bx = 0; bx < (w + 3) / 4; bx++, o += 8)
        {
            int c0 = src[o] | src[o + 1] << 8, c1 = src[o + 2] | src[o + 3] << 8;
            void Col(int i, int c) { pal[i * 4] = (byte)((c >> 11 & 31) * 255 / 31); pal[i * 4 + 1] = (byte)((c >> 5 & 63) * 255 / 63); pal[i * 4 + 2] = (byte)((c & 31) * 255 / 31); pal[i * 4 + 3] = 255; }
            Col(0, c0); Col(1, c1);
            for (int k = 0; k < 3; k++)
            {
                if (c0 > c1) { pal[8 + k] = (byte)((2 * pal[k] + pal[4 + k]) / 3); pal[12 + k] = (byte)((pal[k] + 2 * pal[4 + k]) / 3); }
                else { pal[8 + k] = (byte)((pal[k] + pal[4 + k]) / 2); pal[12 + k] = 0; }
            }
            pal[11] = 255; pal[15] = (byte)(c0 > c1 ? 255 : 0);
            uint bits = BitConverter.ToUInt32(src, o + 4);
            for (int py = 0; py < 4; py++)
            for (int px = 0; px < 4; px++)
            {
                int x = bx * 4 + px, y = by * 4 + py;
                if (x >= w || y >= h) continue;
                int idx = (int)(bits >> (2 * (py * 4 + px)) & 3);
                Buffer.BlockCopy(pal, idx * 4, dst, (y * w + x) * 4, 4);
            }
        }
        return dst;
    }
}
