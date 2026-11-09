#if !UNITY_5_3_OR_NEWER || UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;
using Myrtana.Story;

namespace Myrtana.Session.Dev
{
    /// <summary>One quest's verdict in the static walk: can it start, can it succeed, and the root facts that block it.</summary>
    public sealed class QuestCheckResult
    {
        public QuestCheckResult(string quest, string region) { Quest = quest; Region = region; }

        public string Quest { get; }
        /// <summary>The quest folder's first two parts ("Myrtana/Silden_City"); "Hauptstory" for the main story.</summary>
        public string Region { get; }
        public bool Starts { get; set; }
        public bool Succeeds { get; set; }
        /// <summary>The quest is counted from the start (no entry starts it) — legal, but noted.</summary>
        public bool CountedWhileOpen { get; set; }
        /// <summary>Facts no rule can ever produce that block the start or the success (deepest cause first).</summary>
        public List<string> Roots { get; } = new List<string>();
        /// <summary>Static problems of the quest definition itself (unknown targets, too few spawns, unknown zone).</summary>
        public List<string> Defects { get; } = new List<string>();
        /// <summary>Conditions on the way the walk cannot prove (NPC near a place, pre-checks, freeing enclaves in combat).</summary>
        public List<string> Notes { get; } = new List<string>();
        /// <summary>Entries that would change this quest but are never offered ("id@owner: root causes").</summary>
        public List<string> DeadEntries { get; } = new List<string>();

        public bool Ok => Starts && Succeeds && Defects.Count == 0;
    }

    /// <summary>
    /// Every quest of the pack against the static walk (Docs/story-walk.md §5): starter reachable, objective completable
    /// (targets exist and are present, enough spawns to kill, items obtainable, area known, destination present), and,
    /// when blocked, the root facts nothing produces (a flag no entry sets, an NPC that is never in the world, an item
    /// without a source). Pure: works on the rules of <see cref="StoryWalkBuilder"/> after <see cref="StoryWalk.Run"/>.
    /// </summary>
    public sealed class QuestCheck
    {
        readonly IContentDatabase _db;
        readonly StoryWalkBuilder _builder;
        readonly StoryWalk _walk;
        readonly Dictionary<string, List<WalkRule>> _producers = new Dictionary<string, List<WalkRule>>(StringComparer.Ordinal);
        readonly Dictionary<string, int> _spawns;
        readonly HashSet<string> _items;
        readonly HashSet<string> _zones;

        public QuestCheck(IContentDatabase db, StoryWalkBuilder builder, StoryWalk walk)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _builder = builder ?? throw new ArgumentNullException(nameof(builder));
            _walk = walk ?? throw new ArgumentNullException(nameof(walk));
            foreach (var r in walk.Rules)
                foreach (var e in r.Effects)
                {
                    if (!_producers.TryGetValue(e, out var list)) _producers[e] = list = new List<WalkRule>();
                    if (!list.Contains(r)) list.Add(r);
                }
            _spawns = db.All<NpcDef>().GroupBy(n => BaseName(n.Id.Name)).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            _items = new HashSet<string>(db.All<ItemDef>().Select(i => i.Id), StringComparer.Ordinal);
            _zones = new HashSet<string>(db.All<AiZoneDef>().Select(z => z.Name).Where(n => n != null), StringComparer.Ordinal);
        }

        public static string BaseName(string id)
        {
            int hash = id?.IndexOf('#') ?? -1;
            return hash > 0 ? id.Substring(0, hash) : id;
        }

        public static string RegionOf(QuestDef q)
        {
            var parts = (q.Folder ?? "").Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? "(none)" : parts.Length == 1 ? parts[0] : parts[0] + "/" + parts[1];
        }

        public IReadOnlyList<WalkRule> Producers(string fact) =>
            fact != null && _producers.TryGetValue(fact, out var list) ? list : (IReadOnlyList<WalkRule>)Array.Empty<WalkRule>();

        public IEnumerable<QuestCheckResult> All() => _db.All<QuestDef>().OrderBy(q => RegionOf(q)).ThenBy(q => q.Id.Name).Select(Check);

        public QuestCheckResult Check(QuestDef q)
        {
            var res = new QuestCheckResult(q.Id.Name, RegionOf(q));
            string run = WalkFacts.Quest(q.Id.Name, "Running"), ok = WalkFacts.Quest(q.Id.Name, "Success");
            bool hasStarter = Producers(run).Count > 0 || _walk.DepthOf(run) == 0;
            res.CountedWhileOpen = !hasStarter;
            res.Starts = _walk.Has(run) || !hasStarter && _walk.Has(ok);
            res.Succeeds = _walk.Has(ok);
            Defects(q, res);
            if (!res.Starts && hasStarter) Roots(run, res.Roots);
            if (!res.Succeeds)
            {
                if (Producers(ok).Count == 0) res.Roots.Add(hasStarter ? $"nothing finishes it ({q.Objective.Kind})" : "no starter and no finisher");
                else if (res.Starts || !hasStarter) Roots(ok, res.Roots);
            }
            // entries that would change this quest but can never be offered (an alternative path or reward that is dead)
            if (res.Starts)
                foreach (var r in _builder.EntryRules.Values.Where(r => !_walk.Fired(r) && r.Effects.Any(e => e.StartsWith("quest:" + q.Id.Name + "=", StringComparison.Ordinal))))
                {
                    var roots = new List<string>();
                    foreach (var m in _walk.Missing(r)) Roots(m, roots);
                    if (_walk.Missing(r).Count == 0) roots.Add("can never run" + (r.Notes.Count > 0 ? " (" + string.Join("; ", r.Notes) + ")" : ""));
                    res.DeadEntries.Add($"{r.Id}@{r.Owner}: {string.Join("; ", roots)}");
                }
            foreach (var r in Producers(ok).Concat(Producers(run)))
                foreach (var n in r.Notes)
                    if (!res.Notes.Contains(n) && !n.StartsWith("no entry starts it", StringComparison.Ordinal) &&
                        !n.StartsWith("the items must be handed", StringComparison.Ordinal) && !n.StartsWith("from ", StringComparison.Ordinal))
                        res.Notes.Add(n);
            return res;
        }

        /// <summary>The facts below <paramref name="fact"/> that no rule produces (or only rules that are blocked themselves).</summary>
        public void Roots(string fact, List<string> into)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Visit(string f, int depth)
            {
                if (_walk.Has(f) || !seen.Add(f)) return;
                var producers = Producers(f);
                if (producers.Count == 0 || depth > 12)
                {
                    string why = Explain(f);
                    if (!into.Contains(why)) into.Add(why);
                    return;
                }
                foreach (var r in producers)
                {
                    var missing = _walk.Missing(r);
                    if (missing.Count == 0 && r.Need.Kind == WalkNeed.NeedKind.False)
                    {
                        string why = $"{r.Id}{(r.Owner != null ? "@" + r.Owner : "")} can never run" + (r.Notes.Count > 0 ? " (" + string.Join("; ", r.Notes) + ")" : "");
                        if (!into.Contains(why)) into.Add(why);
                    }
                    foreach (var m in missing) Visit(m, depth + 1);
                }
            }
            Visit(fact, 0);
        }

        /// <summary>A root fact in words: why nothing can produce it.</summary>
        public string Explain(string fact)
        {
            int colon = fact.IndexOf(':');
            string kind = colon > 0 ? fact.Substring(0, colon) : fact, name = colon > 0 ? fact.Substring(colon + 1) : "";
            switch (kind)
            {
                case "npc":
                    if (!_builder.IsKnownActor(name)) return $"npc:{name} (no such NPC or speaker in the world)";
                    string sector = _builder.SectorOf(name);
                    return sector != null ? $"npc:{name} (in sector {sector}, which nothing switches on)" : fact;
                case "item": return _items.Contains(name) ? $"item:{name} (no source)" : $"item:{name} (unknown item)";
                case "flag": return $"flag:{name} (set by nothing)";
                case "sector": return $"sector:{name} (switched on by nothing)";
                case "quest":
                {
                    string q = name.Substring(0, Math.Max(0, name.IndexOf('=')));
                    return _db.All<QuestDef>().Any(d => d.Id.Name == q) ? $"{fact} (nothing sets it)" : $"quest:{q} (no such quest)";
                }
                case "gave": return $"{fact} (no entry hands it over)";
                case "report": return $"{fact} (no entry reports it)";
                default: return fact;
            }
        }

        HashSet<string> _named;

        /// <summary>An NPC/speaker, a named place, an AI zone or an interactive object of that name.</summary>
        public bool IsNamedEntity(string name)
        {
            if (_named == null)
            {
                _named = new HashSet<string>(_zones, StringComparer.Ordinal);
                foreach (var p in _db.All<WorldPlaceDef>()) _named.Add(p.Id);
                foreach (var o in _db.All<InteractiveObjectDef>().Where(o => o.Name != null)) _named.Add(o.Name);
            }
            return _builder.IsKnownActor(BaseName(name)) || _named.Contains(name);
        }

        void Defects(QuestDef q, QuestCheckResult res)
        {
            var o = q.Objective;
            switch (o.Kind)
            {
                case QuestObjectiveKind.Kill:
                case QuestObjectiveKind.Defeat:
                case QuestObjectiveKind.DriveAway:
                case QuestObjectiveKind.Arena:
                case QuestObjectiveKind.Sparring:
                case QuestObjectiveKind.Report:
                case QuestObjectiveKind.FollowNpc:
                case QuestObjectiveKind.BringNpc:
                    foreach (var t in o.Targets.Where(t => t.Target != ActorId.Player.Name))
                    {
                        string name = BaseName(t.Target);
                        if (!_builder.IsKnownActor(name)) { res.Defects.Add($"target {t.Target}: no such NPC"); continue; }
                        int spawns = _spawns.TryGetValue(name, out int n) ? n : 1;
                        bool counts = o.Kind == QuestObjectiveKind.Kill || o.Kind == QuestObjectiveKind.Defeat || o.Kind == QuestObjectiveKind.DriveAway;
                        if (counts && t.Amount > spawns) res.Defects.Add($"target {t.Target}: {t.Amount} needed, {spawns} in the world");
                    }
                    break;
                case QuestObjectiveKind.HasItems:
                case QuestObjectiveKind.Plunder:
                    foreach (var t in o.Targets.Where(t => !_items.Contains(t.Target))) res.Defects.Add($"item {t.Target}: no such item");
                    break;
                case QuestObjectiveKind.EnterArea:
                    if (o.Destination.IsEmpty || !_zones.Contains(o.Destination.Name)) res.Defects.Add($"area {o.Destination.Name}: no such AI zone");
                    break;
            }
            if (!o.Destination.IsEmpty && !o.Destination.IsPlayer && o.Destination.Name != "PC_Hero" && o.Kind != QuestObjectiveKind.EnterArea &&
                !IsNamedEntity(o.Destination.Name))
                res.Defects.Add($"destination {o.Destination.Name}: no such NPC, place, zone or object");
            foreach (var f in q.Rewards.Fame.Where(f => f.Kind == FameKind.Enclave && !_db.All<EnclaveDef>().Any(e => e.Id == f.Group)))
                res.Defects.Add($"reward fame: enclave {f.Group} unknown");
        }
    }
}
#endif
