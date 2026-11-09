using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G3.Formats.Audio;
using G3.Formats.Pak;
using G3.Formats.World;
using G3.Formats.Templates;

/// <summary>
/// `G3Cli audio music`                 Music.pak catalog + Library/g3.mul (triggers, fragments, links, transitions), cross-checked
/// `G3Cli audio ambient`               Library/g3.ams (ambient sounds / environment cells)
/// `G3Cli audio effects [filter]`      Library/g3.efm effects with sounds (+ footstep table)
/// `G3Cli audio zones [filter]`        eCWeatherZone_PS = music zones (shape, extents, environment, music location)
/// `G3Cli audio world [x z radius]`    world-wide counts; zones/emitters/effects near a point (m, default Faring 655/586, 60 m)
/// `G3Cli audio at x z [y]`            music zone / location at a point (m)
/// `G3Cli audio formats [volume]`      codec/rate/channels histogram of Music+Sound (Ogg Vorbis / WAV headers)
/// `G3Cli audio census` | `ps <class> [n]`   property-set census / dump of world entities
/// `G3Cli audio itemmat [filter]`     eEShapeMaterial of item templates' first collision shape (combat sounds: metal vs wood)
/// `G3Cli audio surfaces [filter]`   PhysicMaterial (eEShapeMaterial) of every .xshmat → footstep token histogram
/// `G3Cli audio sim`                   self-test of the runtime audio logic (G3.Audio) against the real data (AudioSimCommands.cs)
/// Docs/formats-audio.md
/// </summary>
static partial class Program
{
    static int AudioCommand(string data, string[] args)
    {
        string sub = args.Length > 1 ? args[1] : "music";
        switch (sub)
        {
            case "census": return AudioCensus(data);
            case "ps": return AudioDumpPs(data, args[2], args.Length > 3 ? int.Parse(args[3]) : 5);
            case "music": return AudioMusic(data);
            case "ambient": return AudioAmbient(data);
            case "effects": return AudioEffects(data, args.Length > 2 ? args[2] : null);
            case "zones": return AudioZones(data, args.Length > 2 ? args[2] : null);
            case "world":
                return AudioWorldCmd(data, args.Length > 3 ? F(args[2]) : 655, args.Length > 3 ? F(args[3]) : 586, args.Length > 4 ? F(args[4]) : 60);
            case "at": return AudioAt(data, F(args[2]), F(args[3]), args.Length > 4 ? F(args[4]) : float.NaN);
            case "formats": return AudioFormats(data, args.Length > 2 ? args[2] : null);
            case "sim": return AudioSim(data);
            case "surfaces": return AudioSurfaces(data, args.Length > 2 ? args[2] : null);
            case "itemmat": return AudioItemMaterials(data, args.Length > 2 ? args[2] : null);
        }
        Console.WriteLine("audio music | ambient | effects [f] | zones [f] | world [x z r] | at x z [y] | formats [vol] | census | ps <class> [n] | sim");
        return 1;
    }

    static float F(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    static IEnumerable<(string path, WorldEntity e)> AudioWorldEntities(G3FileSystem fs, bool keepRaw)
    {
        const string vol = "Projects_compiled/";
        var layout = WorldLayout.Build(fs.Files.Select(f => f.Path), p => fs.Get(vol + p)?.Read());
        foreach (var s in layout.Sectors)
        foreach (var path in s.Nodes.Concat(s.Lrentdats))
        {
            var entry = fs.Get(vol + path);
            List<WorldEntity> list;
            try { list = entry == null ? null : EntityArchive.Read(entry.Read(), false, keepRaw); }
            catch { continue; }
            if (list == null) continue;
            foreach (var e in list) yield return (path, e);
        }
    }

    static int AudioCensus(string data)
    {
        using var fs = new G3FileSystem(data, "Projects_compiled");
        var counts = new Dictionary<string, int>();
        foreach (var (path, e) in AudioWorldEntities(fs, true))
            foreach (var ps in e.RawPropertySets ?? new())
                counts[ps.ClassName] = counts.TryGetValue(ps.ClassName, out var c) ? c + 1 : 1;
        foreach (var kv in counts.OrderBy(k => k.Key)) Console.WriteLine($"{kv.Value,8} {kv.Key}");
        return 0;
    }

    /// <summary>Print the properties of the first n world entities carrying property set `cls`.</summary>
    static int AudioDumpPs(string data, string cls, int n)
    {
        using var fs = new G3FileSystem(data, "Projects_compiled");
        int shown = 0, total = 0;
        foreach (var (path, e) in AudioWorldEntities(fs, true))
            foreach (var ps in e.RawPropertySets ?? new())
            {
                if (ps.ClassName != cls) continue;
                total++;
                if (shown >= n) continue;
                shown++;
                Console.WriteLine($"== {e.Name} in {path} pos ({e.X:F0},{e.Y:F0},{e.Z:F0}) v{ps.ClassVersion}");
                foreach (var p in ps.Properties)
                    Console.WriteLine($"     {p.Key} : {p.Value.Type} = {Fmt(e.Source, p.Value)} [{BitConverter.ToString(p.Value.Value, 0, Math.Min(16, p.Value.Value.Length))}]");
            }
        Console.WriteLine($"total {total}");
        return 0;
    }

    static byte[] AudioLib(string data, string name)
    {
        using var fs = new G3FileSystem(data, "Library");
        return fs.GetByName(name)?.Read() ?? throw new FileNotFoundException(name);
    }

    static string Ms(uint ms) => $"{ms / 60000}:{ms / 1000 % 60:00}.{ms % 1000 / 100}";

    static int AudioMusic(string data)
    {
        var lib = MusicLibrary.Read(AudioLib(data, "g3.mul"));
        using var fs = new G3FileSystem(data, "Music");
        var files = fs.Files.Where(f => f.Path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
                      .Select(f => (entry: f, file: MusicFile.Parse(f.Path))).OrderBy(f => f.file.Path).ToList();
        var byBase = files.ToDictionary(f => f.file.BaseName, f => f, StringComparer.OrdinalIgnoreCase);
        Console.WriteLine($"g3.mul v{lib.Version}: {lib.Triggers.Count} triggers, {lib.Fragments.Count} fragments " +
                          $"({lib.Fragments.Sum(f => f.Links.Count)} links), {lib.Transitions.Count} transitions; Music.pak {files.Count} .ogg");

        Console.WriteLine("\n-- triggers (g3.mul)");
        foreach (var t in lib.Triggers)
            Console.WriteLine($"  {t.Id,-16} loc='{t.Location}' sit='{t.Situation}' day='{t.DayTime}' mode={t.TransitionMode} exit={t.Exit} entrance={t.Entrance}{(t.IsStopTrigger ? " STOP" : "")}");

        Console.WriteLine("\n-- fragments: location situation daytime variation->next  sample  duration mul / ogg  leadIn..leadOut  links entr/exit/loop/gen prio  connectors in/out");
        int missing = 0, durMismatch = 0;
        foreach (var f in lib.Fragments)
        {
            string ogg = "MISSING";
            if (byBase.TryGetValue(f.SampleBaseName, out var mf))
            {
                var info = AudioFileInfo.Read(mf.entry.Read());
                ogg = $"{info.Seconds:F1}s {info.SampleRate / 1000.0:0.#}k/{info.Channels}ch";
                if (Math.Abs(info.Seconds * 1000 - f.DurationMs) > 200) durMismatch++;
            }
            else missing++;
            var lk = f.Links;
            string cin = string.Join(",", lk.Select(l => l.ConnectIn).Distinct()), cout = string.Join(",", lk.Select(l => l.ConnectOut).Distinct());
            Console.WriteLine($"  {f.Location,-20} {f.Situation,-13} {f.DayTime,-5} {f.Variation,2}->{(f.NextVariation == "" ? "-" : f.NextVariation),-2} " +
                              $"{f.SampleBaseName,-40} {Ms(f.DurationMs)} / {ogg,-18} {Ms(f.LeadInEndMs)}..{Ms(f.LeadOutBeginMs)} " +
                              $"links {lk.Count(l => l.Type == MusicLinkType.Entrance)}/{lk.Count(l => l.Type == MusicLinkType.Exit)}/{lk.Count(l => l.Type == MusicLinkType.Loop)}/{lk.Count(l => l.Type == MusicLinkType.Generic)} p{lk.Count(l => l.IsPriority)} in[{cin}] out[{cout}]");
        }
        Console.WriteLine("\n-- transitions: connectOut->connectIn  sample  duration  fadeOut  fadeIn  offset timing");
        foreach (var t in lib.Transitions)
            Console.WriteLine($"  {t.ConnectOut,3}->{t.ConnectIn,-3} {t.SampleBaseName,-22} {Ms(t.DurationMs)} out {t.FadeOutBeginMs}-{t.FadeOutEndMs} in {t.FadeInBeginMs}-{t.FadeInEndMs} off {t.OffsetMs} {t.Timing}" +
                              (byBase.ContainsKey(t.SampleBaseName) ? "" : "  MISSING"));
        var referenced = new HashSet<string>(lib.Fragments.Select(f => f.SampleBaseName).Concat(lib.Transitions.Select(t => t.SampleBaseName)), StringComparer.OrdinalIgnoreCase);
        Console.WriteLine($"\nfragments whose sample is missing in Music.pak: {missing}; |mul duration - ogg duration| > 0.2 s: {durMismatch}");
        Console.WriteLine("Music.pak files not referenced by g3.mul: " + string.Join(", ", files.Where(f => !referenced.Contains(f.file.BaseName)).Select(f => f.file.Path)));
        foreach (var grp in files.Where(f => !f.file.IsTransition).GroupBy(f => f.file.Situation).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  situation {grp.Key,-14} {grp.Count(),3} files: {string.Join(" ", grp.Select(f => f.file.Location).Distinct())}");
        Console.WriteLine($"  daytime: day {files.Count(f => f.file.DayTime == "day")}, night {files.Count(f => f.file.DayTime == "night")}; transitions {files.Count(f => f.file.IsTransition)}");
        return 0;
    }

    static HashSet<string> SoundBaseNames(string data)
    {
        using var sfs = new G3FileSystem(data, "Sound");
        return new HashSet<string>(sfs.Files.Select(f => Path.GetFileNameWithoutExtension(f.Path)), StringComparer.OrdinalIgnoreCase);
    }

    static int AudioAmbient(string data)
    {
        var lib = AmbientLibrary.Read(AudioLib(data, "g3.ams"));
        Console.WriteLine($"g3.ams v{lib.Version}: {lib.Sounds.Count} sounds, {lib.Environments.Count} environment cells");
        var names = SoundBaseNames(data);
        foreach (var s in lib.Sounds.OrderBy(s => s.Climate).ThenBy(s => s.Environment).ThenBy(s => s.DayTime))
            Console.WriteLine($"  {s.Climate,-13} {s.Environment,-8} {s.DayTime,-7} {s.Sample,-26} vol {s.Volume:0.##} dist {s.MinDistance}-{s.MaxDistance} w{s.Weight} dur {s.DurationMs}ms" +
                              (names.Contains(Path.GetFileNameWithoutExtension(s.Sample)) ? "" : "  MISSING"));
        foreach (var e in lib.Environments)
            Console.WriteLine($"  env {e.Climate,-13} {e.Environment,-8} sounds {e.MinNumSounds}-{e.MaxNumSounds} delay {e.MinSpawnDelay}-{e.MaxSpawnDelay}s p {e.SpawnProbability}");
        return 0;
    }

    static int AudioEffects(string data, string filter)
    {
        var map = EffectMap.Read(AudioLib(data, "g3.efm"));
        var names = SoundBaseNames(data);
        var withSound = map.Effects.Values.Where(e => e.Sounds.Count > 0).ToList();
        var samples = withSound.SelectMany(e => e.Sounds).SelectMany(s => s.Samples).Select(s => Path.GetFileNameWithoutExtension(s.Sample)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Console.WriteLine($"g3.efm v{map.Version}: {map.Effects.Count} effects, {withSound.Count} with PlaySound, {samples.Count} distinct samples " +
                          $"({samples.Count(s => !names.Contains(s))} not in Sound.pak), music triggers {map.Effects.Values.Sum(e => e.MusicTriggers.Count)}");
        foreach (var grp in map.Effects.Values.GroupBy(e => e.Name.Split('_').Skip(1).FirstOrDefault() ?? "?").OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {grp.Key,-10} {grp.Count(),4} effects, {grp.Count(e => e.Sounds.Count > 0),4} with sound");
        foreach (var e in map.Effects.Values.Where(e => filter != null && e.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(e => e.Name))
        {
            Console.WriteLine($"  {e.Name}: {string.Join(", ", e.Commands)}");
            foreach (var s in e.Sounds)
                Console.WriteLine($"      sound vol {s.Volume:0.##} dist {s.MinDistance}-{s.MaxDistance} t+{s.TimeOffset}s: " +
                                  string.Join(" ", s.Samples.Select(x => x.Sample + (names.Contains(Path.GetFileNameWithoutExtension(x.Sample)) ? "" : "(MISSING)"))));
        }
        var te = EffectTemplateEmitters(data, map);
        Console.WriteLine($"effects with a spawned template carrying eCAudioEmitter_PS: {te.Count}");
        foreach (var kv in te.OrderBy(k => k.Key))
            Console.WriteLine($"  {kv.Key,-40} -> {string.Join("; ", kv.Value.Select(em => $"{em.Name}: '{em.Sound}' {em.Mode} vol {em.Volume} dist {em.MinDistance}-{em.MaxDistance}"))}");
        if (filter == null)
        {
            Console.WriteLine("footstep effects (human walk left) by eEShapeMaterial: " + string.Join(" ", new[] { 1, 2, 3, 4, 5, 6, 11, 13, 15, 20 }
                .Select(m => $"{m}:{Footsteps.MaterialToken(m)}{(map.Get(Footsteps.EffectName("human", "walk", "left", m)) == null ? "(no effect)" : "")}")));
            Console.WriteLine("samples not in Sound.pak (first 20): " + string.Join(" ", samples.Where(s => !names.Contains(s)).Take(20)));
        }
        return 0;
    }

    /// <summary>Effects whose spawned templates carry an eCAudioEmitter_PS: effect name → emitters.</summary>
    static Dictionary<string, List<SoundEmitter>> EffectTemplateEmitters(string data, EffectMap map)
    {
        var tpl = LoadTemplates(data); // ItemCommands.cs
        var res = new Dictionary<string, List<SoundEmitter>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in map.Effects.Values)
            foreach (var guid in e.SpawnTemplates)
            {
                var f = tpl.Get(guid);
                if (f == null) continue;
                foreach (var h in f.Headers)
                {
                    var ps = h.Get("eCAudioEmitter_PS");
                    if (ps == null) continue;
                    var em = AudioWorld.ReadEmitter(ps, h.Source);
                    em.Name = f.Name;
                    if (!res.TryGetValue(e.Name, out var list)) res[e.Name] = list = new List<SoundEmitter>();
                    list.Add(em);
                    break;
                }
            }
        return res;
    }

    static AudioWorld LoadAudioWorld(string data)
    {
        using var fs = new G3FileSystem(data, "Projects_compiled");
        var w = new AudioWorld();
        foreach (var (path, e) in AudioWorldEntities(fs, true)) w.Add(e, path);
        return w;
    }

    static int AudioZones(string data, string filter)
    {
        var w = LoadAudioWorld(data);
        var lib = MusicLibrary.Read(AudioLib(data, "g3.mul"));
        Console.WriteLine($"eCWeatherZone_PS: {w.Zones.Count} zones ({w.Zones.Count(z => !string.IsNullOrEmpty(z.MusicLocation))} with a music location, {w.Zones.Count(z => !z.Enabled)} disabled entities)");
        foreach (var grp in w.Zones.GroupBy(z => z.Shape)) Console.WriteLine($"  shape {grp.Key}: {grp.Count()}");
        foreach (var grp in w.Zones.GroupBy(z => z.WeatherEnvironment)) Console.WriteLine($"  environment '{grp.Key}': {grp.Count()}");
        foreach (var grp in w.Zones.GroupBy(z => z.MusicLocation.ToLowerInvariant()).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  music '{grp.Key}': {grp.Count(),3} zones{(lib.Fragments.Any(f => f.Location.Equals(grp.Key, StringComparison.OrdinalIgnoreCase)) ? "" : "  (no fragment with this location)")}" +
                              $"  e.g. {string.Join(", ", grp.Take(3).Select(z => z.Name))}");
        if (filter != null)
            foreach (var z in w.Zones.Where(z => (z.Name + " " + z.MusicLocation + " " + z.WeatherEnvironment).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                                     .OrderBy(z => z.MusicLocation).ThenBy(z => z.Name))
                Console.WriteLine($"  {z.Name,-40} {z.Shape,-8} at ({z.X / 100:F0},{z.Y / 100:F0},{z.Z / 100:F0}) m r {z.InnerRadius / 100:F0}/{z.OuterRadius / 100:F0} m " +
                                  $"ext ({string.Join(",", z.InnerExtends.Select(v => (v / 100).ToString("F0")))})/({string.Join(",", z.OuterExtends.Select(v => (v / 100).ToString("F0")))}) m " +
                                  $"env '{z.WeatherEnvironment}' music '{z.MusicLocation}'{(z.Enabled ? "" : " DISABLED")}");
        return 0;
    }

    static int AudioWorldCmd(string data, float xm, float zm, float rm)
    {
        var w = LoadAudioWorld(data);
        var map = EffectMap.Read(AudioLib(data, "g3.efm"));
        var te = EffectTemplateEmitters(data, map);
        Console.WriteLine($"music/weather zones {w.Zones.Count}, audio emitters {w.Emitters.Count}, gCEffect_PS with effect {w.Effects.Count} " +
                          $"(with PlaySound {w.Effects.Count(e => map.Get(e.Effect)?.Sounds.Count > 0)}, via emitter template {w.Effects.Count(e => te.ContainsKey(e.Effect))}, " +
                          $"unknown effect {w.Effects.Count(e => map.Get(e.Effect) == null)}), " +
                          $"particles with sound {w.ParticlesWithSound}");
        Console.WriteLine("speedtrees by AmbientEnvironment: " + string.Join(", ", Enumerable.Range(0, 6).Select(i => $"{(AmbientEnvironment)i} {w.AmbientTreeCounts[i]}")));
        foreach (var grp in w.Emitters.GroupBy(e => e.Sound.ToLowerInvariant()).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  emitter {grp.Key,-42} {grp.Count(),3}x mode {grp.First().Mode} dist {grp.First().MinDistance}-{grp.First().MaxDistance} stream {grp.First().UseStream}");
        foreach (var grp in w.Effects.GroupBy(e => e.Effect.ToLowerInvariant()).OrderByDescending(g => g.Count()).Take(30))
        {
            var ef = map.Get(grp.Key);
            var s = ef?.Sounds.FirstOrDefault();
            Console.WriteLine($"  effect  {grp.Key,-42} {grp.Count(),5}x (items {grp.Count(e => e.IsItem)}, npcs {grp.Count(e => e.IsNpc)}) " +
                              (ef == null ? "NOT IN g3.efm" : s != null ? $"{s.Samples.Count} samples e.g. {s.Samples[0].Sample} dist {s.MinDistance}-{s.MaxDistance}"
                                  : te.TryGetValue(grp.Key, out var tem) ? $"emitter template {tem[0].Name} '{tem[0].Sound}'" : "no sound"));
        }
        float x = xm * 100, z = zm * 100, r = rm * 100;
        Console.WriteLine($"\n-- near ({xm}, {zm}) m, radius {rm} m");
        foreach (var zn in w.Zones.Where(q => q.BlendFactor(x, q.Y, z) > 0))
            Console.WriteLine($"  zone {zn.Name} ({zn.Shape}) music '{zn.MusicLocation}' env '{zn.WeatherEnvironment}' blend {zn.BlendFactor(x, zn.Y, z):0.00}");
        foreach (var e in w.Emitters.Where(e => Dist2(e.X, e.Z, x, z) < r * r))
            Console.WriteLine($"  emitter {e.Name} '{e.Sound}' at ({e.X / 100:F1},{e.Y / 100:F1},{e.Z / 100:F1}) m {e.Mode} dist {e.MinDistance}-{e.MaxDistance}");
        foreach (var e in w.Effects.Where(e => Dist2(e.X, e.Z, x, z) < r * r && (map.Get(e.Effect)?.Sounds.Count > 0 || te.ContainsKey(e.Effect))))
            Console.WriteLine($"  effect  {e.Name} '{e.Effect}' at ({e.X / 100:F1},{e.Y / 100:F1},{e.Z / 100:F1}) m -> " +
                              string.Join(" ", map.Get(e.Effect).Sounds.SelectMany(s => s.Samples).Select(s => s.Sample).Take(4)
                                  .Concat(te.TryGetValue(e.Effect, out var tl) ? tl.Select(t => $"{t.Sound} ({t.Mode}, {t.MinDistance}-{t.MaxDistance} cm)") : Enumerable.Empty<string>())));
        return 0;
    }

    static float Dist2(float ax, float az, float bx, float bz) => (ax - bx) * (ax - bx) + (az - bz) * (az - bz);

    static int AudioAt(string data, float xm, float zm, float ym)
    {
        var w = LoadAudioWorld(data);
        float x = xm * 100, z = zm * 100;
        foreach (var zn in w.Zones.Where(q => q.BlendFactor(x, float.IsNaN(ym) ? q.Y : ym * 100, z) > 0))
            Console.WriteLine($"  zone {zn.Name} ({zn.Shape}) music '{zn.MusicLocation}' env '{zn.WeatherEnvironment}' blend {zn.BlendFactor(x, float.IsNaN(ym) ? zn.Y : ym * 100, z):0.00}");
        var best = w.MusicZoneAt(x, float.IsNaN(ym) ? float.NaN : ym * 100, z);
        Console.WriteLine(best == null ? "no music zone -> region default" : $"music location: {best.MusicLocation} ({best.Name})");
        return 0;
    }

    static int AudioFormats(string data, string volume)
    {
        var hist = new Dictionary<string, int>();
        var odd = new List<string>();
        double total = 0;
        foreach (var vol in volume != null ? new[] { volume } : new[] { "Music", "Sound" })
        {
            using var fs = new G3FileSystem(data, vol);
            foreach (var f in fs.Files)
            {
                var info = AudioFileInfo.Read(f.Read());
                var key = $"{vol,-6} {info.Container}/{info.Codec} {info.SampleRate}Hz {info.Channels}ch{(info.Container == "wav" ? " " + info.BitsPerSample + "bit" : "")}";
                hist[key] = hist.TryGetValue(key, out var c) ? c + 1 : 1;
                if (info.Seconds > 0) total += info.Seconds;
                if (info.Container == "unknown" || (info.Container == "wav" && info.Codec != "pcm") || info.CuePoints > 0 || (info.Container == "ogg" && info.Comments.Count > 0))
                    odd.Add($"{f.Path}: {info.Container}/{info.Codec} cues {info.CuePoints} comments [{string.Join("; ", info.Comments.Take(3))}] vendor '{info.Vendor}'");
            }
        }
        foreach (var kv in hist.OrderBy(k => k.Key)) Console.WriteLine($"{kv.Value,6} {kv.Key}");
        Console.WriteLine($"total duration {total / 3600:F2} h; files with notes {odd.Count}");
        foreach (var o in odd.Take(40)) Console.WriteLine("  " + o);
        return 0;
    }

    /// <summary>Item template → eEShapeMaterial of its first eCCollisionShape (post data of eCCollisionShape_PS), histogram per use type.</summary>
    static int AudioItemMaterials(string data, string filter)
    {
        var a = LoadTemplates(data);
        var hist = new SortedDictionary<string, SortedDictionary<int, int>>();
        foreach (var f in a.Files.Where(f => !f.IsDeleted && f.Main != null))
        {
            var main = f.Main;
            var item = main.Get("gCItem_PS") ?? f.Headers.Select(h => h.Get("gCItem_PS")).FirstOrDefault(p => p != null);
            if (item == null) continue;
            int mat = TemplateShapes.FirstMaterial(f);
            var inter = f.Headers.Select(h => h.Get("gCInteraction_PS")).FirstOrDefault(p => p != null);
            int use = inter?.Get("UseType")?.AsEnum() ?? -1;
            var dmg = f.Headers.Select(h => h.Get("gCDamage_PS")).FirstOrDefault(p => p != null);
            int dt = dmg?.Get("DamageType")?.AsEnum() ?? -1;
            string key = $"use {use,3} dmgtype {dt,2}";
            if (!hist.TryGetValue(key, out var m)) hist[key] = m = new SortedDictionary<int, int>();
            m[mat] = m.TryGetValue(mat, out int c) ? c + 1 : 1;
            if (filter != null && f.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                Console.WriteLine($"  {f.Name,-36} use {use} dmgtype {dt} material {mat}");
        }
        foreach (var kv in hist) Console.WriteLine($"{kv.Key}: " + string.Join(" ", kv.Value.Select(x => $"m{x.Key}x{x.Value}")));
        return 0;
    }

    /// <summary>Footstep surface of the render materials: eCResourceShaderMaterial_PS.PhysicMaterial of every .xshmat.</summary>
    static int AudioSurfaces(string data, string filter)
    {
        using var fs = new G3FileSystem(data, "_compiledMaterial");
        var hist = new SortedDictionary<int, int>();
        int fail = 0, total = 0;
        foreach (var e in fs.Files.Where(f => f.Path.EndsWith(".xshmat", StringComparison.OrdinalIgnoreCase)))
        {
            string stem = Path.GetFileNameWithoutExtension(e.Path);
            G3.Formats.Material.MaterialResource m;
            try { m = G3.Formats.Material.Xshmat.Read(e.Read(), stem); } catch { fail++; continue; }
            total++;
            hist[m.PhysicMaterial] = hist.TryGetValue(m.PhysicMaterial, out int c) ? c + 1 : 1;
            if (filter != null && stem.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                Console.WriteLine($"  {stem,-48} physic {m.PhysicMaterial} -> {Footsteps.MaterialToken(m.PhysicMaterial)}");
        }
        Console.WriteLine($"{total} materials ({fail} unreadable); PhysicMaterial histogram:");
        foreach (var kv in hist) Console.WriteLine($"  {kv.Key,3} -> {(kv.Key < 0 ? "(missing)" : Footsteps.MaterialToken(kv.Key)),-6} x{kv.Value}");
        return 0;
    }
}
