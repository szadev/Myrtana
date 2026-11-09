using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using G3.Audio;
using G3.Formats.Pak;
using UnityEditor;
using UnityEngine;
using F = G3.Formats.Audio;
using Debug = UnityEngine.Debug;

namespace G3.Unity.Editor
{
    /// <summary>
    /// Imports G3 audio on demand, per category, into Assets/G3Imported/Audio (gitignored): extracts the needed samples
    /// from Music.pak / Sound.pak (IMA-ADPCM WAVs converted to PCM), lets Unity import them
    /// (<see cref="G3AudioPostprocessor"/> sets load types) and (re)builds the <see cref="G3AudioLibrary"/> asset.
    /// </summary>
    public static class G3AudioImporter
    {
        public const string Root = G3AssetCache.Root + "/Audio";
        public const string LibraryPath = Root + "/G3AudioLibrary.asset";
        public const string MusicFolder = Root + "/Music";
        public const string SoundFolder = Root + "/Sound";

        public enum Category { Music, Ambience, Footsteps, AllSound, AnimationEffects, Combat }

        [MenuItem("G3/Audio/Import All (music, ambience, emitters, footsteps)", priority = 200)]
        static void MenuAll() => Debug.Log(ImportAll());
        [MenuItem("G3/Audio/Import Music", priority = 211)]
        static void MenuMusic() => Debug.Log(Import(Category.Music));
        [MenuItem("G3/Audio/Import Ambience + World Emitters", priority = 212)]
        static void MenuAmbience() => Debug.Log(Import(Category.Ambience));
        [MenuItem("G3/Audio/Import Footsteps", priority = 213)]
        static void MenuFootsteps() => Debug.Log(Import(Category.Footsteps));
        [MenuItem("G3/Audio/Import Animation Effects (tools, eating, drinking)", priority = 215)]
        static void MenuAnimationEffects() => Debug.Log(Import(Category.AnimationEffects));
        [MenuItem("G3/Audio/Import Combat Sounds (hits, creatures, bow, magic)", priority = 216)]
        static void MenuCombat() => Debug.Log(Import(Category.Combat));
        [MenuItem("G3/Audio/Import Whole Sound.pak", priority = 214)]
        static void MenuAllSound() => Debug.Log(Import(Category.AllSound));
        [MenuItem("G3/Audio/Rebuild Library Asset", priority = 230)]
        static void MenuLibrary() => Debug.Log(BuildLibrary(out _));

        /// <summary>World bake + music, ambience/emitter and footstep samples + library asset.</summary>
        public static string ImportAll()
        {
            var sw = Stopwatch.StartNew();
            var world = G3AudioWorldBaker.Bake(out string bake);
            int n = ExtractMusic() + ExtractSounds(AmbienceSamples(world)) + ExtractSounds(FootstepSamples())
                    + ExtractSounds(EffectSamples(AnimationEffects())) + ExtractSounds(EffectSamples(CombatEffects()));
            AssetDatabase.Refresh();
            BuildLibrary(out string lib);
            return $"audio: {bake}; extracted {n} new files; {lib}; {sw.Elapsed.TotalSeconds:F0} s";
        }

        public static string Import(Category category)
        {
            var sw = Stopwatch.StartNew();
            int n;
            string extra = "";
            switch (category)
            {
                case Category.Music: n = ExtractMusic(); break;
                case Category.Ambience:
                    var world = G3AudioWorldBaker.Bake(out extra);
                    n = ExtractSounds(AmbienceSamples(world));
                    break;
                case Category.Footsteps: n = ExtractSounds(FootstepSamples()); break;
                case Category.AnimationEffects: n = ExtractSounds(EffectSamples(AnimationEffects())); break;
                case Category.Combat: n = ExtractSounds(EffectSamples(CombatEffects())); break;
                default: n = ExtractSounds(null); break;
            }
            AssetDatabase.Refresh();
            BuildLibrary(out string lib);
            return $"audio {category}: extracted {n} new files{(extra != "" ? "; " + extra : "")}; {lib}; {sw.Elapsed.TotalSeconds:F0} s";
        }

        // ------------------------------------------------------------------ sample sets

        static IEnumerable<string> AmbienceSamples(G3AudioWorldAsset world)
        {
            var ams = AudioCatalogConverter.Ambient(F.AmbientLibrary.Read(ReadLibraryFile("g3.ams")));
            foreach (var s in ams.sounds) yield return s.clip;
            foreach (var b in AmbientBeds.Defaults()) yield return b.clip;
            if (world != null) foreach (var e in world.emitters) yield return e.clip;
        }

        static IEnumerable<string> FootstepSamples()
        {
            foreach (var fx in FootstepEffects())
                foreach (var c in fx.clips) yield return c;
        }

        static IEnumerable<string> EffectSamples(IEnumerable<EffectSoundData> effects) => effects.SelectMany(e => e.clips);

        /// <summary>
        /// The <c>eff_ani_*</c> effects that motion event tracks fire (.xmot effect list, Docs/formats-animation.md §8.7):
        /// tool hits, eating, drinking, smoking, music instruments, bed squeaks, weapon draw.
        /// </summary>
        internal static List<EffectSoundData> AnimationEffects()
        {
            var map = F.EffectMap.Read(ReadLibraryFile("g3.efm"));
            return map.Effects.Values
                .Where(e => e.Name.StartsWith("eff_ani_", StringComparison.OrdinalIgnoreCase))
                .Select(AudioCatalogConverter.Effect).Where(e => e != null).ToList();
        }

        /// <summary>
        /// Effects of fights (Docs/combat.md §13): body and parade hits, arrows and weapons in the level (<c>eff_col_*</c>),
        /// creature cries and creature steps from their motions (<c>eff_creature_*</c>, <c>eff_step_*</c> but the human ones),
        /// spells (<c>eff_magic_*</c>), <c>eff_voi_*</c>, script events (<c>eff_event_*</c>: lock pick, temple door), inventory sounds (<c>eff_inv_*</c>: sharpen, bless, poison,
        /// deny; par-misc — they were never imported, so these were silent); plus effects whose only sound is a spawned template's
        /// "Once" eCAudioEmitter_PS (the bow string <c>eff_ani_fight_bow_raise_01</c>), as an effect with that sample.
        /// </summary>
        internal static List<EffectSoundData> CombatEffects()
        {
            var map = F.EffectMap.Read(ReadLibraryFile("g3.efm"));
            string[] prefixes = { "eff_col_", "eff_creature_", "eff_magic_", "eff_voi_", "eff_event_", "eff_inv_", "eff_step_creature", "eff_step_giant", "eff_step_minecrawler" };
            var list = map.Effects.Values
                .Where(e => prefixes.Any(p => e.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                .Select(AudioCatalogConverter.Effect).Where(e => e != null).ToList();
            var have = new HashSet<string>(list.Select(e => e.name), StringComparer.OrdinalIgnoreCase);
            have.UnionWith(AnimationEffects().Select(e => e.name));
            foreach (var kv in G3AudioWorldBaker.EffectTemplateEmitters())
            {
                var em = kv.Value;
                if (have.Contains(kv.Key) || em.Mode != F.AudioEmitterMode.Once) continue;
                if (!kv.Key.StartsWith("eff_ani_fight_", StringComparison.OrdinalIgnoreCase) && !kv.Key.StartsWith("eff_magic_", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new EffectSoundData
                {
                    name = kv.Key.ToLowerInvariant(), volume = em.Volume,
                    minDistance = Math.Max(0.1f, em.MinDistance * 0.01f), maxDistance = Math.Max(1f, em.MaxDistance * 0.01f),
                    clips = new[] { AudioCatalogConverter.ClipKey(em.Sound) },
                });
            }
            return list;
        }

        internal static List<EffectSoundData> FootstepEffects()
        {
            var map = F.EffectMap.Read(ReadLibraryFile("g3.efm"));
            return map.Effects.Values
                .Where(e => e.Name.StartsWith("eff_step_human_", StringComparison.OrdinalIgnoreCase))
                .Select(AudioCatalogConverter.Effect).Where(e => e != null).ToList();
        }

        internal static byte[] ReadLibraryFile(string name)
        {
            using (var fs = new G3FileSystem(G3WorldImporter.DataDir, "Library"))
                return fs.GetByName(name)?.Read() ?? throw new FileNotFoundException($"Library/{name} not found in {G3WorldImporter.DataDir}");
        }

        // ------------------------------------------------------------------ extraction

        static int ExtractMusic()
        {
            using (var fs = new G3FileSystem(G3WorldImporter.DataDir, "Music"))
                return Extract(fs.Files.Where(e => IsAudio(e.Path) && !e.Path.Contains("_rejected")), MusicFolder);
        }

        /// <summary>Extracts Sound.pak files by sample base name (null = all audio files).</summary>
        static int ExtractSounds(IEnumerable<string> samples)
        {
            using (var fs = new G3FileSystem(G3WorldImporter.DataDir, "Sound"))
            {
                var audio = fs.Files.Where(e => IsAudio(e.Path)).ToList();
                if (samples == null) return Extract(audio, SoundFolder);
                var byName = new Dictionary<string, PakEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in audio)
                {
                    var key = AudioCatalogConverter.ClipKey(e.Path);
                    if (!byName.ContainsKey(key)) byName[key] = e;
                }
                var wanted = new List<PakEntry>();
                var missing = new SortedSet<string>();
                foreach (var s in samples.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (byName.TryGetValue(s, out var e)) wanted.Add(e);
                    else missing.Add(s);
                }
                if (missing.Count > 0) Debug.LogWarning($"G3 audio: {missing.Count} samples not in Sound.pak: {string.Join(", ", missing)}");
                return Extract(wanted, SoundFolder);
            }
        }

        static bool IsAudio(string path) =>
            path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);

        /// <summary>Writes entries under <paramref name="folder"/> keeping the pak folders; existing files are kept.</summary>
        static int Extract(IEnumerable<PakEntry> entries, string folder)
        {
            int n = 0;
            foreach (var e in entries)
            {
                var dst = Path.Combine(folder, e.Path).Replace('\\', '/');
                if (File.Exists(dst)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                var bytes = e.Read();
                if (dst.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) && F.ImaAdpcm.IsImaAdpcmWav(bytes))
                    bytes = F.ImaAdpcm.ToPcm16(bytes);
                File.WriteAllBytes(dst, bytes);
                n++;
            }
            return n;
        }

        // ------------------------------------------------------------------ library asset

        /// <summary>(Re)builds Assets/G3Imported/Audio/G3AudioLibrary.asset from the game data and the imported clips.</summary>
        public static G3AudioLibrary BuildLibrary(out string summary)
        {
            HashSet<string> shipped;
            using (var fs = new G3FileSystem(G3WorldImporter.DataDir, "Music"))
                shipped = new HashSet<string>(fs.Files.Select(f => AudioCatalogConverter.ClipKey(f.Path)));
            var music = AudioCatalogConverter.Music(F.MusicLibrary.Read(ReadLibraryFile("g3.mul")), shipped.Contains);
            var ambient = AudioCatalogConverter.Ambient(F.AmbientLibrary.Read(ReadLibraryFile("g3.ams")));
            var beds = AmbientBeds.Defaults();
            var effects = FootstepEffects();
            effects.AddRange(AnimationEffects());
            effects.AddRange(CombatEffects());
            var world = AssetDatabase.LoadAssetAtPath<G3AudioWorldAsset>(G3AudioWorldBaker.AssetPath);

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in music.fragments) names.Add(f.clip);
            foreach (var t in music.transitions) names.Add(t.clip);
            foreach (var s in ambient.sounds) names.Add(s.clip);
            foreach (var b in beds) names.Add(b.clip);
            foreach (var e in effects) names.UnionWith(e.clips);
            if (world != null) foreach (var e in world.emitters) names.Add(e.clip);

            var clipPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (AssetDatabase.IsValidFolder(Root))
                foreach (var guid in AssetDatabase.FindAssets("t:AudioClip", new[] { Root }))
                {
                    var p = AssetDatabase.GUIDToAssetPath(guid);
                    var key = AudioCatalogConverter.ClipKey(p);
                    if (names.Contains(key) && !clipPaths.ContainsKey(key)) clipPaths[key] = p;
                }

            var lib = AssetDatabase.LoadAssetAtPath<G3AudioLibrary>(LibraryPath);
            bool create = lib == null;
            if (create) lib = ScriptableObject.CreateInstance<G3AudioLibrary>();
            lib.music = music;
            lib.ambient = ambient;
            lib.beds = beds;
            lib.effects = effects;
            lib.clips = clipPaths.OrderBy(k => k.Key)
                .Select(k => new G3AudioLibrary.NamedClip { name = k.Key, clip = AssetDatabase.LoadAssetAtPath<AudioClip>(k.Value) })
                .ToList();
            Directory.CreateDirectory(Root);
            if (create) AssetDatabase.CreateAsset(lib, LibraryPath);
            else EditorUtility.SetDirty(lib);
            AssetDatabase.SaveAssets();
            lib.ResetCaches();

            summary = $"library: {music.fragments.Count} fragments, {music.transitions.Count} transitions, {ambient.sounds.Count} ambient sounds, " +
                      $"{effects.Count} effects (footsteps + animation + combat); clips {clipPaths.Count}/{names.Count} imported";
            return lib;
        }
    }
}
