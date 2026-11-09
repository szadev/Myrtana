#if !UNITY_5_3_OR_NEWER || UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;
using Myrtana.Story;

namespace Myrtana.Session.Dev
{
    /// <summary>
    /// A headless play plan for one quest (Docs/story-walk.md §5), from the static walk: the quest's own steps (the
    /// entries and objective that change it, the deliveries it counts, script triggers on the way) are played through the
    /// real paths with <see cref="ChapterDryRun"/> ops (<c>ask</c>, <c>kill</c>, <c>fight</c>, <c>raid</c>, <c>use</c>, <c>tp</c>);
    /// everything else the steps need (flags, other quests, items, sectors, gold and fame of pre-checks, an NPC standing
    /// at a place) is set up directly just before the step that needs it.
    /// </summary>
    public sealed class QuestDryPlan
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly Regex StaminaNote = new Regex(@">= (\d+) maximum stamina", RegexOptions.Compiled);
        static readonly Regex StrengthNote = new Regex(@">= (\d+) strength", RegexOptions.Compiled);

        readonly IContentDatabase _db;
        readonly StoryWalk _walk;
        readonly Dictionary<string, List<string>> _spawns;
        readonly Dictionary<string, string> _enclaveOf, _factionOf;
        readonly HashSet<string> _places, _zones;
        readonly Dictionary<string, int> _itemAmounts = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly Dictionary<string, DialogEntryDef> _entries;
        readonly HashSet<string> _parents;
        readonly HashSet<string> _quests;
        /// <summary>Quests whose status the current plan already set up (one status per quest and plan).</summary>
        readonly Dictionary<string, QuestStatus> _setUp = new Dictionary<string, QuestStatus>(StringComparer.Ordinal);

        public QuestDryPlan(IContentDatabase db, StoryWalk walk)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _walk = walk ?? throw new ArgumentNullException(nameof(walk));
            _spawns = db.All<NpcDef>().Where(n => !n.Id.IsPlayer).GroupBy(n => QuestCheck.BaseName(n.Id.Name))
                        .ToDictionary(g => g.Key, g => g.Select(n => n.Id.Name).OrderBy(n => n, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
            _factionOf = db.All<NpcDef>().Where(n => n.Combat?.Faction != null).GroupBy(n => QuestCheck.BaseName(n.Id.Name))
                           .ToDictionary(g => g.Key, g => g.First().Combat.Faction, StringComparer.Ordinal);
            _enclaveOf = db.All<NpcDef>().Where(n => n.Combat?.Enclave != null).GroupBy(n => QuestCheck.BaseName(n.Id.Name))
                           .ToDictionary(g => g.Key, g => g.First().Combat.Enclave, StringComparer.Ordinal);
            _places = new HashSet<string>(db.All<WorldPlaceDef>().Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
            _entries = db.All<DialogEntryDef>().ToDictionary(e => e.Id.Name, StringComparer.Ordinal);
            _parents = new HashSet<string>(_entries.Values.Where(e => !e.Parent.IsEmpty).Select(e => e.Parent.Name), StringComparer.Ordinal);
            _quests = new HashSet<string>(db.All<QuestDef>().Select(q => q.Id.Name), StringComparer.Ordinal);
            _zones = new HashSet<string>(db.All<AiZoneDef>().Select(z => z.Name).Where(n => n != null), StringComparer.OrdinalIgnoreCase);
            // the largest amount any condition or objective asks of an item
            void Need(string item, int amount)
            {
                if (item == null) return;
                _itemAmounts[item] = Math.Max(amount, _itemAmounts.TryGetValue(item, out int n) ? n : 1);
            }
            foreach (var e in db.All<DialogEntryDef>()) foreach (var h in HasItemsIn(e.Condition)) Need(h.Item, h.Amount);
            foreach (var q in db.All<QuestDef>().Where(q => q.Objective.Kind == QuestObjectiveKind.HasItems || q.Objective.Kind == QuestObjectiveKind.Plunder))
                foreach (var t in q.Objective.Targets) Need(t.Target, t.Amount);
        }

        static IEnumerable<HasItems> HasItemsIn(Condition c)
        {
            switch (c)
            {
                case HasItems h: return new[] { h };
                case AllOf all: return all.Conditions.SelectMany(HasItemsIn);
                case AnyOf any: return any.Conditions.SelectMany(HasItemsIn);
                default: return Enumerable.Empty<HasItems>();
            }
        }

        /// <summary>The ops that play <paramref name="q"/> to Success; null with <paramref name="skip"/> when there is no plan.</summary>
        public List<string> Steps(QuestDef q, out string skip)
        {
            skip = null;
            string quest = q.Id.Name, goal = WalkFacts.Quest(quest, "Success");
            if (!_walk.Has(goal)) { skip = "the static walk never reaches Success"; return null; }
            _setUp.Clear();
            var local = new List<WalkRule>();
            var visited = new HashSet<WalkRule>();
            bool IsLocal(WalkRule r) =>
                r.Kind == "entry" && r.Effects.Any(e => e.StartsWith("quest:" + quest + "=", StringComparison.Ordinal) || e.StartsWith("gave:", StringComparison.Ordinal) ||
                                                           e.StartsWith("report:" + quest + ":", StringComparison.Ordinal)) ||
                r.Id == "objective " + quest ||
                r.Kind == "source" && (r.Id.StartsWith("used ", StringComparison.Ordinal) || r.Id.StartsWith("received ", StringComparison.Ordinal) || r.Id.StartsWith("entered ", StringComparison.Ordinal) ||
                                       r.Id.StartsWith("bookstand ", StringComparison.Ordinal) || r.Id.StartsWith("boulder ", StringComparison.Ordinal));
            void Visit(string fact, int guard)
            {
                if (guard > 40 || _walk.DepthOf(fact) <= 0) return;
                var r = ProducerFor(fact, local);
                if (r == null || !IsLocal(r) || !visited.Add(r)) return;
                foreach (var f in Chosen(r.Need).OrderBy(_walk.DepthOf)) Visit(f, guard + 1);
                local.Add(r);
            }
            Visit(goal, 0);
            if (local.Count == 0) { skip = "no own step (Success comes from the start state)"; return null; }

            var steps = new List<string>();
            var done = new HashSet<string>(StringComparer.Ordinal);
            void Setup(string fact)
            {
                // items: every step that needs them gets them (deliveries use them up; the start kit has too few)
                if (fact.StartsWith("item:", StringComparison.Ordinal) && (_walk.ProducerOf(fact) == null || !local.Contains(_walk.ProducerOf(fact))))
                {
                    string item = fact.Substring(5);
                    steps.Add($"give {item} {(_itemAmounts.TryGetValue(item, out int amount) ? amount : 1)}");
                    return;
                }
                if (_walk.DepthOf(fact) <= 0 || !done.Add(fact)) return;
                var r = _walk.ProducerOf(fact);
                if (r != null && local.Contains(r)) return;
                if (r != null && r.Kind == "source" && IsLocal(r))
                {
                    // a script trigger (book received, potion drunk, area entered, bookstand read): play it
                    foreach (var f in Chosen(r.Need)) Setup(f);
                    Trigger(r, steps);
                    return;
                }
                int colon = fact.IndexOf(':');
                string kind = fact.Substring(0, colon), name = fact.Substring(colon + 1);
                switch (kind)
                {
                    case "flag": steps.Add("flag " + name); break;
                    case "sector": steps.Add("sector " + name); break;
                    case "item": steps.Add($"give {name} {(_itemAmounts.TryGetValue(name, out int n) ? n : 1)}"); break;
                    case "quest":
                    {
                        int eq = name.IndexOf('=');
                        string other = name.Substring(0, eq), status = name.Substring(eq + 1);
                        if (!_quests.Contains(other)) break; // a quest the infos name but G3 does not have: never set
                        // QuestTransitions: Cancelled and Lost only from Running; the others from Open
                        var to = (QuestStatus)Enum.Parse(typeof(QuestStatus), status);
                        var from = _setUp.TryGetValue(other, out var was) ? was : QuestStatus.Open;
                        if ((to == QuestStatus.Cancelled || to == QuestStatus.Lost) && from == QuestStatus.Open)
                        {
                            steps.Add($"quest {other} Running");
                            from = QuestStatus.Running;
                        }
                        if (!QuestTransitions.IsAllowed(from, to)) { steps.Add($"log -- {other} {status}: set up as {from} before"); break; }
                        steps.Add($"quest {other} {status}");
                        _setUp[other] = to;
                        break;
                    }
                    case "npc":
                        // present through a sector (or the liberation switch): switch it on
                        if (r != null) foreach (var f in Chosen(r.Need)) Setup(f);
                        break;
                    case "gave":
                        steps.Add("log -- setup: " + fact + " (not set up)");
                        break;
                }
            }

            var played = new HashSet<WalkRule>();
            var marked = new HashSet<string>(StringComparer.Ordinal);
            void Play(WalkRule r, int guard)
            {
                if (!played.Add(r)) return;
                foreach (var f in Chosen(r.Need))
                {
                    // a fact another entry of the same NPC gives (a sibling topic, the urgent info's answer): talk it through
                    var p = _walk.ProducerOf(f);
                    if (guard < 8 && r.Kind == "entry" && p != null && p.Kind == "entry" && p.Owner == r.Owner && !local.Contains(p) && _walk.DepthOf(f) > 0 &&
                        !done.Contains(f))
                    {
                        Play(p, guard + 1);
                        done.Add(f);
                    }
                    else Setup(f);
                }
                if (r.Kind == "entry")
                {
                    if (marked.Add(r.Owner)) MarkAutoEntries(r.Owner, local.Concat(new[] { r }), steps);
                    Entry(r, steps);
                }
                else if (r.Kind == "objective") Objective(q, steps);
                else Trigger(r, steps);
            }
            foreach (var r in local) Play(r, 0);
            steps.Add("wait 2");
            return Chain(steps);
        }

        /// <summary>
        /// The earliest producer of the fact that the plan can still reach: not a sub-choice that an entry already in the
        /// plan closes (G3 ClearChildren: picking one sibling marks the others as heard).
        /// </summary>
        WalkRule ProducerFor(string fact, List<WalkRule> plan)
        {
            var first = _walk.ProducerOf(fact);
            if (first == null || first.Kind != "entry") return first;
            // the punctual way first: an entry for an overdue quest only when nothing else gives the fact
            if (Overdue(first))
                first = _walk.Rules.Where(r => r.Effects.Contains(fact) && _walk.Fired(r) && !Overdue(r)).OrderBy(_walk.RoundOf).FirstOrDefault() ?? first;
            bool Closed(WalkRule r) =>
                _entries.TryGetValue(r.Id, out var e) && !e.Parent.IsEmpty &&
                plan.Any(p => p.Kind == "entry" && p.Id != r.Id && _entries.TryGetValue(p.Id, out var o) && o.Parent == e.Parent && (o.ClosesSiblings || e.ClosesSiblings));
            if (!Closed(first)) return first;
            return _walk.Rules.Where(r => r.Effects.Contains(fact) && _walk.Fired(r) && !Closed(r)).OrderBy(_walk.RoundOf).FirstOrDefault() ?? first;
        }

        bool Overdue(WalkRule r)
        {
            for (string id = r.Id; id != null && _entries.TryGetValue(id, out var e); id = e.Parent.IsEmpty ? null : e.Parent.Name)
                if (Leaves(e.Condition).Any(c => c is QuestOverdue)) return true;
            return false;
        }

        bool IsAncestor(string ancestor, string entry)
        {
            for (int guard = 0; guard < 32 && _entries.TryGetValue(entry, out var d) && !d.Parent.IsEmpty; guard++)
            {
                if (d.Parent.Name == ancestor) return true;
                entry = d.Parent.Name;
            }
            return false;
        }

        /// <summary>
        /// Sub-choices are offered only right after their parent ran: an ask whose next ask (same NPC) is one of its
        /// sub-choices keeps the talk open (end=0). A lone sub-choice runs by itself after its parent (G3 runs a list of
        /// exactly one info without the menu): it is expected as heard instead of asked.
        /// </summary>
        List<string> Chain(List<string> steps)
        {
            var asks = Enumerable.Range(0, steps.Count).Where(i => steps[i].StartsWith("ask ", StringComparison.Ordinal)).ToList();
            // an urgent/news topic on the way to an asked entry must stay unheard
            var asked = asks.Select(i => steps[i].Split(' ')[2]).ToList();
            for (int i = 0; i < steps.Count; i++)
            {
                if (!steps[i].StartsWith("played ", StringComparison.Ordinal)) continue;
                var keep = steps[i].Substring(7).Split(',').Where(id => !asked.Any(a => a == id || IsAncestor(id, a))).ToList();
                steps[i] = keep.Count > 0 ? "played " + string.Join(",", keep) : "log -- nothing to mark";
            }
            for (int k = 0; k + 1 < asks.Count; k++)
            {
                var a = steps[asks[k]].Split(' ');
                var b = steps[asks[k + 1]].Split(' ');
                // the next topic is a sub-choice of this one or of one of its parents (a sibling): stay in the sub-menu
                if (a[1] != b[1] || !(IsAncestor(a[2], b[2]) || _entries.TryGetValue(b[2], out var next) && !next.Parent.IsEmpty &&
                                                                 (next.Parent.Name == a[2] || IsAncestor(next.Parent.Name, a[2])))) continue;
                if (!steps[asks[k]].EndsWith(" end=0", StringComparison.Ordinal)) steps[asks[k]] += " end=0";
                // what the follow-up needs (a kill, items) must hold when the sub-menu is shown: set it up first
                var between = Enumerable.Range(asks[k] + 1, asks[k + 1] - asks[k] - 1)
                                        .Where(i => !steps[i].StartsWith("tp ", StringComparison.Ordinal) && !steps[i].StartsWith("log ", StringComparison.Ordinal)).ToList();
                if (between.Count > 0)
                {
                    var moved = between.Select(i => steps[i]).ToList();
                    foreach (int i in between.OrderByDescending(i => i)) steps[i] = "log -- moved up";
                    int at = asks[k];
                    while (at > 0 && steps[at - 1].StartsWith("tp ", StringComparison.Ordinal)) at--;
                    steps.InsertRange(at, moved);
                    return Chain(steps); // indices moved: start again
                }
                // the hero stands at the NPC in the open talk: a tp between would end it (the runner's tp finishes an open
                // talk; editor-runner-25 kdw_camp_01: after FILLER526 end=0 the tp closed Tamkosch's insult menu, FILLER529
                // under the once-only "(Provoke)" was gone)
                var tps = Enumerable.Range(asks[k] + 1, asks[k + 1] - asks[k] - 1).Where(i => steps[i].StartsWith("tp ", StringComparison.Ordinal)).ToList();
                if (tps.Count > 0)
                {
                    foreach (int i in tps.OrderByDescending(i => i)) steps.RemoveAt(i);
                    return Chain(steps);
                }
            }
            return steps;
        }

        /// <summary>
        /// Urgent and news topics of the NPC that open a sub-menu, are not on the way to the entries played and are not
        /// repeatable: marked as heard (setup), else their sub-menu hides the topics the plan asks for.
        /// </summary>
        void MarkAutoEntries(string owner, IEnumerable<WalkRule> plays, List<string> steps)
        {
            var ancestors = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in plays.Where(r => r.Kind == "entry" && r.Owner == owner))
                for (string id = r.Id; id != null && ancestors.Add(id);)
                    id = _entries.TryGetValue(id, out var d) && !d.Parent.IsEmpty ? d.Parent.Name : null;
            var auto = _entries.Values.Where(e => QuestCheck.BaseName(e.Owner.Name) == owner && !e.Repeatable && !ancestors.Contains(e.Id.Name) &&
                                                  (e.Kind == DialogEntryKind.Important || e.Kind == DialogEntryKind.News) && _parents.Contains(e.Id.Name))
                               .Select(e => e.Id.Name).ToList();
            if (auto.Count > 0) steps.Add("played " + string.Join(",", auto));
        }

        /// <summary>The facts one way through the need takes (the earliest reached branch of each "any").</summary>
        IEnumerable<string> Chosen(WalkNeed need)
        {
            switch (need.Kind)
            {
                case WalkNeed.NeedKind.Fact: return new[] { need.Fact };
                case WalkNeed.NeedKind.All: return need.Parts.SelectMany(Chosen);
                case WalkNeed.NeedKind.Any:
                {
                    // a finished quest: the successful way first (as a player who did the quest), then the earliest
                    var best = need.Parts.Where(p => p.Holds(_walk.Has))
                                   .OrderBy(p => p.Facts().Any(f => f.StartsWith("quest:", StringComparison.Ordinal) && f.EndsWith("=Success", StringComparison.Ordinal)) ? 0 : 1)
                                   .ThenBy(p => p.Facts().Select(_walk.DepthOf).DefaultIfEmpty(0).Max()).FirstOrDefault();
                    return best != null ? Chosen(best) : Enumerable.Empty<string>();
                }
                default: return Enumerable.Empty<string>();
            }
        }

        string Target(string entity)
        {
            if (_spawns.ContainsKey(QuestCheck.BaseName(entity))) return "npc:" + entity;
            if (_places.Contains(entity)) return "place:" + entity;
            if (_zones.Contains(entity)) return "zone:" + entity;
            return "obj:" + entity;
        }

        void Entry(WalkRule r, List<string> steps)
        {
            string owner = r.Owner;
            ConditionSetup(r, steps);
            // pre-checks of the entry and its menu path: gold, enclave fame, political fame
            for (string id = r.Id; id != null && _entries.TryGetValue(id, out var e); id = e.Parent.IsEmpty ? null : e.Parent.Name)
                foreach (var pre in e.PreChecks)
                    switch (pre)
                    {
                        case MinGold g: steps.Add("give It_Gold " + g.Amount.ToString(Inv)); break;
                        case MinEnclaveFame f when _enclaveOf.TryGetValue(QuestCheck.BaseName(f.Member.IsPartner ? owner : f.Member.Value ?? owner), out string enclave):
                            steps.Add($"fame {enclave} {f.Minimum.ToString(Inv)} kind=Enclave");
                            break;
                        case MinPoliticalFame f when _factionOf.TryGetValue(QuestCheck.BaseName(f.Member.IsPartner ? owner : f.Member.Value ?? owner), out string faction):
                            steps.Add($"fame {faction} {f.Minimum.ToString(Inv)}");
                            break;
                    }
            if (owner == ActorId.Player.Name) { steps.Add("log -- hero entry " + r.Id + " (plays by itself)"); steps.Add("wait 2"); return; }
            steps.Add($"tp {Target(owner)} dist=1.5");
            steps.Add($"ask {owner} {r.Id} given=1");
            if (steps.Any(x => x.StartsWith("freeze only=", StringComparison.Ordinal)) &&
                steps.FindLastIndex(x => x.StartsWith("freeze only=", StringComparison.Ordinal)) > steps.FindLastIndex(x => x == "unfreeze"))
                steps.Add("unfreeze");
        }

        static IEnumerable<Condition> Leaves(Condition c)
        {
            switch (c)
            {
                case null: return Enumerable.Empty<Condition>();
                case AllOf all: return all.Conditions.SelectMany(Leaves);
                default: return new[] { c };
            }
        }

        /// <summary>
        /// What the static walk does not track, from the conditions of the entry and its menu path: an NPC that must be
        /// dead or down (killed / knocked out by the hero), must have talked to the hero, must be led by the hero, or must
        /// stand near an entity (moved there).
        /// </summary>
        void ConditionSetup(WalkRule r, List<string> steps)
        {
            string owner = r.Owner;
            string Name(ActorRef a) => a.IsPartner ? owner : a.Value;
            var near = new List<string>();
            for (string id = r.Id; id != null && _entries.TryGetValue(id, out var e); id = e.Parent.IsEmpty ? null : e.Parent.Name)
                foreach (var leaf in Leaves(e.Condition))
                    switch (leaf)
                    {
                        case ActorIs a when a.Condition == ActorCondition.Dead && Name(a.Actor) != owner && Name(a.Actor) != null:
                            steps.Add($"kill {Name(a.Actor)} hero");
                            break;
                        case ActorIs a when a.Condition == ActorCondition.Defeated && Name(a.Actor) != null:
                            steps.Add($"ko {Name(a.Actor)}");
                            break;
                        case TalkedToPlayer t when Name(t.Actor) != null && Name(t.Actor) != ActorId.Player.Name:
                            steps.Add($"talked {Name(t.Actor)}");
                            break;
                        case QuestOverdue o when _db.TryGet(o.Quest, out QuestDef late) && late.TimeLimitHours > 0:
                            // let the time limit run out: each skip goes to the next 6 o'clock
                            for (int d = 0; d <= (int)(late.TimeLimitHours / 24) + 1; d++) steps.Add("time 6");
                            break;
                        case InPlayerParty p:
                            foreach (var a in p.Actors.Select(Name).Where(n => n != null)) steps.Add($"party {a}");
                            break;
                        case Near n when Name(n.Actor) != null && _spawns.ContainsKey(QuestCheck.BaseName(Name(n.Actor))):
                            near.Add($"bring {Name(n.Actor)} near={Target(n.Entity)} dist=1");
                            break;
                    }
            // the NPC must stay there until the hero asks: frozen first (no routine plan pulls it back to its station while
            // the hero's tp streams the place in: editor-runner-21, Bram stood > 5 m from Bram_HuntWolvesCheckPoint), so the
            // re-added agent keeps the pose; unfrozen after the ask (Entry)
            var held = near.Distinct().ToList();
            if (held.Count > 0) steps.Add("freeze only=" + string.Join(",", held.Select(b => b.Split(' ')[1]).Distinct()));
            steps.AddRange(held);
            // a refusal while a quest is still open (Vatras until the Lago revolution started): start that quest
            foreach (var refuse in _entries.Values.Where(e => e.Kind == DialogEntryKind.Refuse && QuestCheck.BaseName(e.Owner.Name) == owner))
                if (refuse.Condition is QuestStatusIs open && open.Statuses.Count == 1 && open.Statuses[0] == QuestStatus.Open &&
                    !_setUp.ContainsKey(open.Quest.Name))
                {
                    steps.Add($"quest {open.Quest.Name} Running");
                    _setUp[open.Quest.Name] = QuestStatus.Running;
                }
        }

        void Objective(QuestDef q, List<string> steps)
        {
            var o = q.Objective;
            switch (o.Kind)
            {
                case QuestObjectiveKind.Kill:
                case QuestObjectiveKind.DriveAway:
                case QuestObjectiveKind.Defeat:
                    foreach (var t in o.Targets.Where(t => t.Target != ActorId.Player.Name))
                    {
                        var ids = _spawns.TryGetValue(QuestCheck.BaseName(t.Target), out var list) ? list : new List<string> { t.Target };
                        foreach (var id in ids.Take(Math.Max(1, t.Amount)))
                            steps.Add(o.Kind == QuestObjectiveKind.Defeat ? $"fight {id}" : $"kill {id} hero");
                    }
                    break;
                case QuestObjectiveKind.Arena:
                case QuestObjectiveKind.Sparring:
                    if (!o.Destination.IsEmpty) steps.Add($"tp zone:{o.Destination.Name}");
                    // then next to the opponent on its own floor (editor-runner-22 bakaresh_01: the zone point lay on the
                    // ground 6 m below the arena floor where Achmed stood), inside the arena zone (editor-runner-24 Mufassa,
                    // Makas: the spot beside the opponent lay outside it, leaving the zone lost the fight)
                    string inZone = o.Destination.IsEmpty ? "" : " zone=" + o.Destination.Name;
                    // the opponents walk into the ring first (G3 SendToArena; editor-runner-26: Irhabar, Anktos and the
                    // Paladin stood on the stands 4-6 m above the arena floor, no spot beside them inside the zone)
                    if (!o.Destination.IsEmpty)
                        foreach (var t in o.Targets) steps.Add($"wait until=arena:{t.Target} timeout=60 soft=1");
                    foreach (var t in o.Targets.Take(1)) steps.Add($"tp npc:{t.Target} dist=2.5{inZone}");
                    foreach (var t in o.Targets) steps.Add($"fight {t.Target}");
                    break;
                case QuestObjectiveKind.EnterArea:
                    foreach (var t in o.Targets.Take(1))
                        steps.Add(t.Target == ActorId.Player.Name || t.Target == "PC_Hero" ? $"tp zone:{o.Destination.Name}" : $"bring {t.Target} near=zone:{o.Destination.Name} dist=0");
                    break;
                case QuestObjectiveKind.FreeEnclave:
                    foreach (var t in o.Targets) steps.Add("raid " + t.Target);
                    break;
                case QuestObjectiveKind.Plunder when !o.Destination.IsEmpty:
                    if (_spawns.ContainsKey(QuestCheck.BaseName(o.Destination.Name)))
                    {
                        steps.Add($"ko {o.Destination.Name}");
                        steps.Add($"loot {o.Destination.Name}");
                    }
                    else
                    {
                        // a chest: the hero picks its lock (all lock-pick perks, picks to spare)
                        steps.Add("learn Perk_LockPick_1,Perk_LockPick_2,Perk_LockPick_3");
                        steps.Add("give It_Key_Lockpick 20");
                        steps.Add($"tp obj:{o.Destination.Name}");
                        steps.Add($"loot obj:{o.Destination.Name}");
                    }
                    break;
            }
            steps.Add("wait 2");
        }

        void Trigger(WalkRule r, List<string> steps)
        {
            string name = r.Id.Substring(r.Id.IndexOf(' ') + 1);
            if (r.Id.StartsWith("used ", StringComparison.Ordinal))
            {
                var m = r.Notes.Select(n => StaminaNote.Match(n)).FirstOrDefault(x => x.Success);
                if (m != null) steps.Add("attr SP " + (int.Parse(m.Groups[1].Value, Inv) + 50).ToString(Inv));
                steps.Add($"use item:{name}");
            }
            else if (r.Id.StartsWith("received ", StringComparison.Ordinal))
                steps.Add($"give {name} 1");
            else if (r.Id.StartsWith("entered ", StringComparison.Ordinal))
            {
                steps.Add($"bring {r.Owner} near=zone:{name} dist=0");
                steps.Add("wait 2");
            }
            else if (r.Id.StartsWith("bookstand ", StringComparison.Ordinal))
            {
                steps.Add($"tp obj:{name}");
                steps.Add($"use obj:{name}");
            }
            else if (r.Id.StartsWith("boulder ", StringComparison.Ordinal))
            {
                var m = r.Notes.Select(n => StrengthNote.Match(n)).FirstOrDefault(x => x.Success);
                if (m != null) steps.Add("attr STR " + (int.Parse(m.Groups[1].Value, Inv) + 50).ToString(Inv));
                steps.Add($"tp obj:{name}");
                steps.Add($"use obj:{name}");
            }
        }
    }
}
#endif
