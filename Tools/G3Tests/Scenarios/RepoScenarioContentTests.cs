using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Session.Dev;
using Myrtana.Simulation;
using Myrtana.Story;
using NUnit.Framework;

namespace G3Tests.Scenarios
{
    /// <summary>
    /// Every scenario in Tools/Scenarios parses, and every NPC, item, quest, dialog entry (info), place, zone, object, NPC
    /// template and included scenario it names resolves against the content pack (scenarios-3). Items go through the
    /// strict resolver of fix-54 (<see cref="DevLookup.Resolve"/>: an exact id or a unique match; ambiguous and loose
    /// matches fail as they do in the editor runner). Needs the imported pack (Assets/StreamingAssets/Content/Gothic3 of
    /// this checkout, of $G3WORLD, or of .); ignored without it.
    /// </summary>
    public class RepoScenarioContentTests
    {
        static GameContent _content;
        static Localizer _localizer;

        static string FindUp(string relative)
        {
            for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, relative)) || Directory.Exists(Path.Combine(d.FullName, relative))) return Path.Combine(d.FullName, relative);
            return null;
        }

        static string PackDir()
        {
            const string rel = "Assets/StreamingAssets/Content/Gothic3";
            string local = FindUp(Path.Combine(rel, "pack.json"));
            if (local != null) return Path.GetDirectoryName(local);
            foreach (var root in new[] { Environment.GetEnvironmentVariable("G3WORLD"), "." })
                if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, rel, "pack.json"))) return Path.Combine(root, rel);
            return null;
        }

        static GameContent Content()
        {
            if (_content != null) return _content;
            string pack = PackDir();
            if (pack == null) Assert.Ignore("content pack not found (import it, or set G3WORLD)");
            _content = GameContent.Load(new IContentSource[] { new DirectoryContentSource(pack) });
            _localizer = new Localizer(new LanguageSettings("Russian", "English"), _content.Strings);
            return _content;
        }

        static List<(string file, ScenarioSpec spec)> Scenarios()
        {
            string dir = FindUp(Path.Combine("Tools", "Scenarios"));
            if (dir == null) Assert.Ignore("Tools/Scenarios not found");
            return Directory.GetFiles(dir, "*.json").Where(p => !Path.GetFileName(p).StartsWith("spots", StringComparison.Ordinal))
                            .OrderBy(p => p, StringComparer.Ordinal)
                            .Select(p => (Path.GetFileNameWithoutExtension(p), ScenarioParser.ParseJson(File.ReadAllText(p), Path.GetFileNameWithoutExtension(p))))
                            .ToList();
        }

        [Test]
        public void RepoScenarios_AllParse()
        {
            var bad = Scenarios().Where(s => !s.spec.Ok || s.spec.Steps.Count == 0)
                                 .Select(s => $"{s.file}: {(s.spec.Steps.Count == 0 ? "no steps" : string.Join("; ", s.spec.Errors.Take(3)))}").ToList();
            Assert.That(bad, Is.Empty, string.Join(Environment.NewLine, bad));
        }

        [Test]
        public void RepoScenarios_ReferencesResolveAgainstTheContentPack()
        {
            var db = Content().Database;
            var scenarios = Scenarios();
            var names = new HashSet<string>(scenarios.Select(s => s.file), StringComparer.Ordinal);
            var resolver = new ScenarioReferences(db, _localizer, names);
            var problems = new List<string>();
            int checkedRefs = 0;
            foreach (var (file, spec) in scenarios)
            {
                foreach (var a in spec.Answers) resolver.Entry(a, problems, file + " answers");
                foreach (var st in spec.Steps) checkedRefs += resolver.Check(st, problems, file);
            }
            TestContext.WriteLine($"{scenarios.Count} scenarios, {checkedRefs} references checked");
            Assert.That(checkedRefs, Is.GreaterThan(1000));
            Assert.That(problems, Is.Empty, $"{problems.Count} unresolved:{Environment.NewLine}" + string.Join(Environment.NewLine, problems.Take(80)));
        }

        [Test]
        public void Resolver_RejectsUnknownAndAmbiguousIds()
        {
            var db = Content().Database;
            var r = new ScenarioReferences(db, _localizer, new HashSet<string> { "x" });
            var p = new List<string>();
            foreach (var st in ScenarioParser.ParseInline("give It_NoSuchItem_99; quest No_Such_Quest Running; ask NoSuchNpc NOSUCHINFO; tp place:No_Such_Place; give sword_01; expect quest:No_Such_Quest=Success; scenario nope").Steps)
                r.Check(st, p, "t");
            Assert.That(p.Count, Is.EqualTo(8), string.Join(Environment.NewLine, p));
            p.Clear();
            foreach (var st in ScenarioParser.ParseInline("give It_Gold 5; tp npc:Gorn; ask Gorn FILLER12 given=1; kill $fly; spawn Wolf; expect has:It_Gold>=5; scenario x").Steps)
                r.Check(st, p, "t");
            Assert.That(p, Is.Empty, string.Join(Environment.NewLine, p));
        }
    }

    /// <summary>Resolves the ids one scenario step names (see <see cref="RepoScenarioContentTests"/>).</summary>
    sealed class ScenarioReferences
    {
        readonly IContentDatabase _db;
        readonly ILocalizer _localizer;
        readonly HashSet<string> _scenarios;
        /// <summary>The cooking fires' object names (mesh stems, InteractiveObjectImporter.AddCookFire, CP 1.75).</summary>
        static readonly string[] CookFireNames =
        {
            "G3_Object_Fireplace_02", "G3_Object_Fireplace_03", "G3_Object_Fireplace_04", "G3_Object_Fireplace_Large_01",
            "G3_Object_Fire_Cage_01", "G3_Object_Varant_Firebasin_01",
        };

        readonly HashSet<string> _npcs, _bases, _objects, _owners, _places, _zones, _quests, _entries, _enclaves;
        readonly List<NpcDef> _npcDefs;
        readonly Dictionary<string, string> _items = new Dictionary<string, string>(StringComparer.Ordinal);

        static readonly string[] ActorConditions = { "state", "hp", "dist", "important", "talked", "party", "attitude", "fighting", "routine", "unaware", "arena", "summons" };
        static readonly string[] EntryConditions = { "given", "offered", "available" };

        public ScenarioReferences(IContentDatabase db, ILocalizer localizer, HashSet<string> scenarios)
        {
            _db = db;
            _localizer = localizer;
            _scenarios = scenarios;
            _npcDefs = db.All<NpcDef>().ToList();
            _npcs = new HashSet<string>(_npcDefs.Select(n => n.Id.Name), StringComparer.Ordinal);
            _bases = new HashSet<string>(_npcDefs.Select(n => QuestCheck.BaseName(n.Id.Name)), StringComparer.Ordinal);
            _objects = new HashSet<string>(db.All<InteractiveObjectDef>().Select(o => o.Name).Where(n => n != null), StringComparer.Ordinal);
            // fix-88: the port's cooking fires exist once the content is re-imported (G3Cli content import); until then the
            // pack read here has none of them
            if (!db.All<InteractiveObjectDef>().Any(CookFires.IsCookFire)) _objects.UnionWith(CookFireNames);
            _owners = new HashSet<string>(db.All<DialogEntryDef>().Select(e => QuestCheck.BaseName(e.Owner.Name)).Where(n => n != null), StringComparer.Ordinal);
            _places = new HashSet<string>(db.All<WorldPlaceDef>().Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
            _zones = new HashSet<string>(db.All<AiZoneDef>().Select(z => z.Name).Where(n => n != null), StringComparer.OrdinalIgnoreCase);
            _quests = new HashSet<string>(db.All<QuestDef>().Select(q => q.Id.Name), StringComparer.Ordinal);
            _entries = new HashSet<string>(db.All<DialogEntryDef>().Select(e => e.Id.Name), StringComparer.Ordinal);
            _enclaves = new HashSet<string>(db.All<EnclaveDef>().Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
        }

        static bool Skip(string v) => string.IsNullOrEmpty(v) || v.Contains("$") || v.Contains(DevNpcs.Mark);

        static IEnumerable<string> List(string v) => (v ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0);

        /// <summary>The number of references checked; problems are added as "file: step: what".</summary>
        public int Check(ScenarioStep st, List<string> problems, string file)
        {
            int n = 0;
            void Bad(string what) => problems.Add($"{file}: '{st.Source}': {what}");
            void Npc(string v) { if (Skip(v)) return; n++; if (!IsActor(v)) Bad($"no NPC '{v}'"); }
            void Item(string v) { if (Skip(v)) return; n++; string why = ItemProblem(v); if (why != null) Bad(why); }
            void Quest(string v) { if (Skip(v)) return; n++; if (!_quests.Contains(v)) Bad($"no quest '{v}'"); }
            void Info(string v) { if (Skip(v) || int.TryParse(v, out _) || v == "END") return; n++; if (!_entries.Contains(v)) Bad($"no dialog entry '{v}'"); }
            void Target(string v) { if (Skip(v)) return; n++; string why = TargetProblem(v); if (why != null) Bad(why); }

            foreach (var a in List(st.Get("answer"))) Info(a);
            switch (st.Op)
            {
                case "give": case "equip": case "quick": case "eat": case "pickup": Item(st.Get("item")); break;
                case "quest": foreach (var q in List(st.Get("id"))) Quest(q); break;
                case "ask": Npc(st.Get("npc")); Info(st.Get("entry")); break;
                case "choose": case "forget": Info(st.Get("entry")); break;
                case "played": foreach (var e in List(st.Get("entries"))) Info(e); break;
                case "autodialog": foreach (var e in List(st.Get("answers"))) Info(e); break;
                case "talked": foreach (var a in List(st.Get("npcs"))) Npc(a); break;
                case "talk": case "topics": case "bring": case "fight": case "finisher": case "rob": case "disengage": case "corpse": case "npctask":
                    Npc(st.Get("npc")); break;
                case "escort": Npc(st.Get("npc")); Target(st.Get("to")); break;
                case "party": Npc(st.Get("npc")); break;
                case "engage": Npc(st.Get("npc")); if (st.Get("target") != null && st.Get("target") != "hero") Npc(st.Get("target")); break;
                case "kill": case "ko": case "hp": case "restore": Npc(st.Get("actor")); break;
                case "calm": case "freeze": foreach (var a in List(st.Get("only")).Concat(List(st.Get("except")))) Npc(a); break;
                case "tp": case "look": case "walkto": Target(st.Get("target")); break;
                case "focus": if (st.Get("npc") != "off") Npc(st.Get("npc")); break;
                case "loot": case "use":
                {
                    string t = st.Get("target");
                    if (t != null && t.StartsWith("item:", StringComparison.OrdinalIgnoreCase)) Item(t.Substring(5));
                    else if (t != null && t.IndexOf(':') < 0) Npc(t);
                    else Target(t);
                    break;
                }
                case "raid": n++; if (!Skip(st.Get("enclave")) && !_enclaves.Contains(st.Get("enclave"))) Bad($"no enclave '{st.Get("enclave")}'"); break;
                case "spawn":
                    n++;
                    if (!Skip(st.Get("template")) && DevNpcs.FindTemplate(_npcDefs, st.Get("template")) == null) Bad($"no NPC template '{st.Get("template")}'");
                    break;
                case "scenario": n++; if (!_scenarios.Contains(st.Get("name") ?? "")) Bad($"no scenario file '{st.Get("name")}'"); break;
                case "expect": case "wait":
                {
                    string cond = st.Op == "expect" ? st.Get("cond") : st.Get("until");
                    if (cond == null || !ScenarioCondition.TryParse(cond, out var c, out _) || c.Arg == null) break;
                    if (c.Kind == "quest") Quest(c.Arg);
                    else if (c.Kind == "has") Item(c.Arg);
                    else if (Array.IndexOf(EntryConditions, c.Kind) >= 0) Info(c.Arg);
                    else if (Array.IndexOf(ActorConditions, c.Kind) >= 0 && c.Arg != "hero") Npc(c.Arg);
                    else if (c.Kind == "present" && !Skip(c.Arg)) { n++; if (!IsActor(c.Arg) && !_objects.Contains(c.Arg)) Bad($"no NPC or object '{c.Arg}'"); }
                    else if (c.Kind == "inarea" && !Skip(c.Arg)) { n++; if (!_zones.Contains(c.Arg)) Bad($"no AI zone '{c.Arg}'"); }
                    break;
                }
            }
            foreach (var named in new[] { "near", "npc" })
                if (st.Op != "talk" && st.Op != "bring" && st.Op != "fight" && st.Op != "finisher" && st.Op != "rob" && st.Op != "topics" &&
                    st.Op != "escort" && st.Op != "party" && st.Op != "engage" && st.Op != "disengage" && st.Op != "focus" && st.Op != "corpse" &&
                    st.Op != "npctask" && st.Op != "ask" && st.Has(named))
                    if (named == "near") Target(st.Get(named)); else Npc(st.Get(named));
            return n;
        }

        public void Entry(string entry, List<string> problems, string where)
        {
            if (!_entries.Contains(entry) && entry != "END" && !int.TryParse(entry, out _)) problems.Add($"{where}: no dialog entry '{entry}'");
        }

        bool IsActor(string v) =>
            v == "hero" || v == "PC_Hero" || _npcs.Contains(v) || _bases.Contains(v) || _owners.Contains(v) || _objects.Contains(v);

        /// <summary>The editor runner's item lookup (ScenarioRunner.ResolveItem): an exact id, else a safe unique match.</summary>
        string ItemProblem(string query)
        {
            if (_items.TryGetValue(query, out var cached)) return cached;
            string problem = null;
            if (!_db.TryGet(query, out ItemDef _))
            {
                var r = DevLookup.Resolve(_db.All<ItemDef>().SelectMany(i => DevLookup.Names(i.Id, i.Name, _localizer)), query);
                if (r.Id == null) problem = $"no item matches '{query}'";
                else if (!r.Safe) problem = "item " + r.Problem;
            }
            return _items[query] = problem;
        }

        string TargetProblem(string v)
        {
            int colon = v.IndexOf(':');
            if (colon < 0 || v.StartsWith("?", StringComparison.Ordinal)) return null; // coordinates, spot tags
            string kind = v.Substring(0, colon).ToLowerInvariant(), name = v.Substring(colon + 1);
            int dot = name.IndexOf('.');
            switch (kind)
            {
                case "npc": return IsActor(name) ? null : $"no NPC '{name}'";
                case "place": return _places.Contains(name) ? null : $"no place '{name}'";
                case "zone": return _zones.Contains(name) ? null : $"no AI zone '{name}'";
                case "obj": return _objects.Contains(name) || IsActor(name) ? null : $"no object '{name}'";
                default: return null; // spot:… (DevScenarioTests checks spots), x,y,z
            }
        }
    }
}
