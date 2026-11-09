using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G3.Formats.Animation;
using G3.Formats.Story;
using G3.Formats.World;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using Myrtana.Story;
using Myrtana.Ui.Dialog;

namespace Myrtana.Import.G3
{
    /// <summary>
    /// Copies the voice clips a set of dialog owners needs from <c>Speech_&lt;Language&gt;.pak</c> to
    /// <c>&lt;voiceRoot&gt;/&lt;Language&gt;/&lt;clip key&gt;.ogg</c> (<c>GameData/Voice</c> next to Assets, see <see cref="GameDataLayout"/>). The clip keys come from the
    /// imported pack, so this step only needs the G3 files for the audio bytes.
    /// </summary>
    public static class VoiceExtractor
    {
        /// <summary>
        /// Shared lines any companion can speak wherever the hero recruited it, extracted for every voice set like the
        /// combat cries: the party wait answer (CP SayHoldingPosition: "PartyWait", else "Sure").
        /// </summary>
        public static readonly string[] PartySvmNames = { "PartyWait", "Sure" };

        /// <summary>
        /// Every speaker of the pack: the owners of all dialog entries, every NPC (its voice set's shared lines) and the
        /// hero. With these <see cref="Extract"/> copies all the voice the game can play (no region).
        /// </summary>
        public static IReadOnlyCollection<string> AllOwners(string packDirectory)
        {
            var db = GameContent.Load(new IContentSource[] { new DirectoryContentSource(packDirectory) }).Database;
            var names = new HashSet<string>(StringComparer.Ordinal) { ActorId.Player.Name };
            foreach (var e in db.All<DialogEntryDef>()) if (!string.IsNullOrEmpty(e.Owner.Name)) names.Add(e.Owner.Name);
            foreach (var n in db.All<NpcDef>()) names.Add(n.Id.Name);
            return names;
        }

        /// <param name="dryRun">Only count the clips and megabytes that would be written; nothing is written.</param>
        public static string Extract(string dataDirectory, string packDirectory, string voiceRoot,
                                     IReadOnlyCollection<string> owners, IEnumerable<string> languages, bool dryRun = false)
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            var content = GameContent.Load(new IContentSource[] { new DirectoryContentSource(packDirectory) });
            var ownerSet = new HashSet<string>(owners, StringComparer.Ordinal);
            var keys = content.Database.All<DialogEntryDef>()
                .Where(e => ownerSet.Contains(e.Owner.Name))
                .SelectMany(ClipKeys)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            // Shared lines (SVMs) are spoken by voice set at runtime, so every line of the owners' voice sets is needed.
            var svmPrefixes = content.Database.All<NpcDef>()
                .Where(n => ownerSet.Contains(n.Id.Name) && !string.IsNullOrEmpty(n.VoiceSet))
                .Select(n => "SVM_" + n.VoiceSet + "_")
                .Distinct(StringComparer.Ordinal)
                .ToList();

            // Combat cries (Aargh, Down, Dead, FinishingMove; Docs/combat.md §13) of every voice set: any NPC can fight;
            // the party wait answers too (any companion can be told to wait).
            var combatKeys = content.Database.All<NpcDef>()
                .Select(n => n.VoiceSet).Where(v => !string.IsNullOrEmpty(v))
                .Concat(new[] { "Player" })
                .Distinct(StringComparer.Ordinal)
                .SelectMany(v => CombatSoundRules.SvmNames.Concat(PartySvmNames).Select(line => CombatSoundRules.ClipKey(v, line)))
                .ToList();
            keys = keys.Concat(combatKeys).Distinct(StringComparer.Ordinal).ToList();

            var install = new G3Install(dataDirectory);
            var summary = new List<string>();
            foreach (var language in languages)
            {
                using (var volume = new SpeechVolume(install, language, _ => false))
                {
                    if (volume.ClipCount == 0)
                    {
                        summary.Add($"{language}: no Speech_{language} volume");
                        continue;
                    }
                    string dir = Path.Combine(voiceRoot, language);
                    if (!dryRun) Directory.CreateDirectory(dir);
                    int written = 0, kept = 0, missing = 0, lips = 0;
                    long bytes = 0;
                    var shared = volume.Keys.Where(k => svmPrefixes.Any(p => k.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
                    foreach (var key in keys.Concat(shared).Distinct(StringComparer.Ordinal))
                    {
                        if (!volume.Contains(key)) { missing++; continue; }
                        long size = volume.SizeOf(key);
                        bytes += size;
                        if (dryRun) { written++; continue; }
                        string ogg = Path.Combine(dir, key + ".ogg");
                        // A clip already extracted with the same size is kept (a re-run only adds what is new).
                        if (File.Exists(ogg) && new FileInfo(ogg).Length == size) { kept++; continue; }
                        File.WriteAllBytes(ogg, volume.Read(key));
                        written++;
                        var lip = volume.ReadLipSync(key);
                        if (lip == null) continue;
                        File.WriteAllText(Path.Combine(dir, key + LipSyncTrack.FileExtension), ToTrack(lip).Write());
                        lips++;
                    }
                    summary.Add(dryRun
                        ? $"{language}: {written} clips would be written ({bytes / 1048576.0:F0} MB), {missing} not recorded"
                        : $"{language}: {written} clips written ({lips} with lip sync), {kept} already there, {missing} not recorded, {bytes / 1048576.0:F0} MB in all");
                }
            }
            return $"{owners.Count} owners, {keys.Count} clip keys; " + string.Join("; ", summary) + $"; {started.Elapsed.TotalSeconds:F0} s";
        }

        /// <summary>Names of the uniquely named NPCs spawned within a square of the world (metres, G3 x/z axes).</summary>
        public static IReadOnlyCollection<string> OwnersInRegion(string dataDirectory, float xMetres, float zMetres, float halfSizeMetres)
        {
            const string volume = "Projects_compiled";
            var install = new G3Install(dataDirectory);
            using (var fs = install.Open(volume))
            {
                var layout = WorldLayout.Build(fs.Files.Select(f => f.Path), p => fs.Get(volume + "/" + p)?.Read());
                float x = xMetres * 100, z = zMetres * 100, h = halfSizeMetres * 100;
                var names = NpcSpawns.LoadWorld(layout, p => fs.Get(volume + "/" + p)?.Read())
                    .Where(s => s.Enabled && !string.IsNullOrEmpty(s.Name))
                    .Where(s => Math.Abs(s.X - x) <= h && Math.Abs(s.Z - z) <= h)
                    .Select(s => s.Name)
                    .ToList();
                return new HashSet<string>(names, StringComparer.Ordinal) { ActorId.Player.Name };
            }
        }

        /// <summary>The G3 viseme track as the game's own lip-sync form (Docs/dialog-presentation.md §4).</summary>
        static LipSyncTrack ToTrack(Xlip xlip) =>
            new LipSyncTrack(xlip.Times.Select((t, i) => new LipSyncTrack.Key(t, (int)xlip.Visemes[i])));

        static IEnumerable<string> ClipKeys(DialogEntryDef entry) =>
            entry.Commands.Concat(entry.PreChecks.SelectMany(p => p.Refusal))
                 .OfType<Say>()
                 .Where(s => !string.IsNullOrEmpty(s.Voice))
                 .Select(s => s.Voice);
    }
}
