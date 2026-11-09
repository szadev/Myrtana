using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using Myrtana.Story;
using NUnit.Framework;

namespace G3Tests.Content
{
    /// <summary>
    /// fix-235: every recorded shared (SVM) line the port can say with a subtitle resolves to a non-blank Russian and
    /// English text (the speaker's voice set's own, another set's stand-in, or a patch string), so no empty subtitle box
    /// is left: dialog <see cref="SayShared"/> lines of the content, crime talk, pickpocket and lock refusals, object use
    /// and bookstand lines of the hero. NPC shouts and combat barks are heard, not shown, and are not checked. Needs the
    /// imported pack and GameData/Voice (this checkout, $G3WORLD or .); ignored without them.
    /// </summary>
    public class SharedLineSubtitleTests
    {
        /// <summary>Recorded lines that may stay without a subtitle: (key, why). None today.</summary>
        static readonly Dictionary<string, string> AllowedSilent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        const string PackRel = "Assets/StreamingAssets/Content/Gothic3", VoiceRel = "GameData/Voice";

        static string Find(string rel, string probe)
        {
            for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, rel, probe)) || Directory.Exists(Path.Combine(d.FullName, rel, probe))) return Path.Combine(d.FullName, rel);
            foreach (var root in new[] { Environment.GetEnvironmentVariable("G3WORLD"), "." })
                if (!string.IsNullOrEmpty(root) && (File.Exists(Path.Combine(root, rel, probe)) || Directory.Exists(Path.Combine(root, rel, probe))))
                    return Path.Combine(root, rel);
            return null;
        }

        static HashSet<string> Clips(string voiceRoot, string language)
        {
            string dir = Path.Combine(voiceRoot, language);
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(dir))
                foreach (var f in Directory.EnumerateFiles(dir, "SVM_*.ogg")) set.Add(Path.GetFileNameWithoutExtension(f));
            return set;
        }

        /// <summary>Every <see cref="SayShared"/> reachable from an entry (commands, pre-checks, nested command lists).</summary>
        static IEnumerable<SayShared> SharedLinesOf(object root)
        {
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<object>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var o = stack.Pop();
                if (o == null || o is string || o.GetType().IsPrimitive || o.GetType().IsEnum || !seen.Add(o)) continue;
                if (o is SayShared say) { yield return say; continue; }
                if (o is IEnumerable list && !(o is IDictionary))
                {
                    foreach (var item in list) stack.Push(item);
                    continue;
                }
                var type = o.GetType();
                if (type.Namespace == null || !type.Namespace.StartsWith("Myrtana", StringComparison.Ordinal) || type.IsValueType) continue;
                foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length > 0 || p.PropertyType.IsValueType || p.PropertyType == typeof(string)) continue;
                    object value;
                    try { value = p.GetValue(o); } catch { continue; }
                    stack.Push(value);
                }
            }
        }

        [Test]
        public void EveryRecordedSharedLine_HasRussianAndEnglishSubtitle()
        {
            string pack = Find(PackRel, "pack.json");
            if (pack == null) Assert.Ignore("content pack not found (import it, or set G3WORLD)");
            string voice = Find(VoiceRel, "Russian");
            if (voice == null) Assert.Ignore("GameData/Voice not found");
            var content = GameContent.Load(new IContentSource[] { new DirectoryContentSource(pack) });
            var db = content.Database;
            Assert.That(db.TryGet(SharedLines.DefinitionId, out SharedLines lines), "sharedLines definition");
            var voiceSets = db.All<NpcDef>().Select(n => n.VoiceSet).Where(v => !string.IsNullOrEmpty(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var npcSets = voiceSets.Where(v => !string.Equals(v, SharedLineTexts.HeroVoiceSet, StringComparison.OrdinalIgnoreCase)).ToList();
            var setOf = db.All<NpcDef>().Where(n => !string.IsNullOrEmpty(n.VoiceSet)).ToDictionary(n => n.Id.Name, n => n.VoiceSet, StringComparer.Ordinal);
            var clips = Clips(voice, "Russian");
            clips.UnionWith(Clips(voice, "English"));
            Assert.That(clips.Count, Is.GreaterThan(1000), "voice clips");

            // (voice set, line) -> where it is said
            var said = new Dictionary<(string set, string line), string>();
            void Add(string set, string line, string source)
            {
                if (!string.IsNullOrEmpty(set) && !string.IsNullOrEmpty(line)) said.TryAdd((set, line), source);
            }
            // a line said "any of its numbered variants, else as named" (HeroLines.SayAny)
            void AddAny(string set, string line, string source)
            {
                int count = 0;
                while (count < 20 && clips.Contains(lines.TextOf(set, $"{line}_{count:00}").Value)) Add(set, $"{line}_{count++:00}", source);
                if (count == 0) Add(set, line, source);
            }

            foreach (var entry in db.All<DialogEntryDef>())
                foreach (var say in SharedLinesOf(entry))
                {
                    IEnumerable<string> sets;
                    if (say.Speaker.Equals(ActorRef.Player)) sets = new[] { SharedLineTexts.HeroVoiceSet };
                    else if (say.Speaker.IsPartner) sets = setOf.TryGetValue(entry.Owner.Name, out var own) ? new[] { own } : npcSets; // the player's topics: anyone
                    else sets = setOf.TryGetValue(say.Speaker.Value, out var named) ? new[] { named } : Array.Empty<string>();
                    foreach (var set in sets)
                    {
                        string line = say.LineFor(set);
                        if (say.Variants <= 0) Add(set, line, $"dialog {entry.Id.Name}");
                        else for (int i = 0; i < say.Variants; i++) Add(set, $"{line}_{i:00}", $"dialog {entry.Id.Name}");
                    }
                }
            foreach (var set in npcSets)
                foreach (var line in CrimeTalk.Lines) Add(set, line, "crime talk");

            const string hero = SharedLineTexts.HeroVoiceSet;
            foreach (var line in HeroSpokenLines.Plain) Add(hero, line, "hero refusal");
            foreach (var line in HeroSpokenLines.Numbered) AddAny(hero, line, "hero refusal");
            Add(hero, PickpocketRules.TooHardLine, "pickpocket");
            Add(hero, PickpocketRules.ImpossibleLine, "pickpocket");
            var boulders = db.TryGet(BoulderRules.DefinitionId, out BoulderRules b) ? b : BoulderRules.Default;
            AddAny(hero, boulders.RefusalLine, "boulder");
            AddAny(hero, ObjectUseRules.Default.NothingLine, "object use");
            AddAny(hero, ObjectUseRules.Default.ShrineRefusalLine, "shrine");
            if (db.TryGet(CraftingRules.DefinitionId, out CraftingRules crafting))
            {
                var stands = crafting.Bookstands;
                foreach (var book in stands.Books)
                    for (int i = 0; i < book.Pages; i++) Add(hero, $"{book.Line}_{i:00}", "bookstand");
                foreach (var lesson in stands.Lessons) Add(hero, lesson.Line, "bookstand");
                foreach (var story in stands.Stories)
                {
                    Add(hero, story.NotReadyLine, "bookstand");
                    foreach (var line in story.Lines) Add(hero, line, "bookstand");
                }
                foreach (var line in stands.NothingNewLines) Add(hero, line, "bookstand");
            }

            var languages = new[] { "Russian", "English" };
            var texts = languages.ToDictionary(l => l, l => new SharedLineTexts(lines, new Localizer(new LanguageSettings(l, l), content.Strings), voiceSets));
            int recorded = 0;
            var missing = new List<string>();
            foreach (var pair in said.OrderBy(p => p.Key.set, StringComparer.Ordinal).ThenBy(p => p.Key.line, StringComparer.Ordinal))
            {
                string key = lines.TextOf(pair.Key.set, pair.Key.line).Value;
                if (!clips.Contains(key)) continue; // not recorded: a silent line shows its text only when it has one
                recorded++;
                if (AllowedSilent.ContainsKey(key)) continue;
                var lacking = languages.Where(l => !texts[l].TryText(pair.Key.set, pair.Key.line, out _)).ToList();
                if (lacking.Count > 0) missing.Add($"{key} ({pair.Value}): no {string.Join("/", lacking)} text");
            }
            TestContext.Out.WriteLine($"{said.Count} shared lines said, {recorded} recorded, {missing.Count} without subtitle");
            Assert.That(recorded, Is.GreaterThan(500), "recorded shared lines found");
            Assert.That(missing, Is.Empty, string.Join(Environment.NewLine, missing.Take(60)));
        }
    }
}
