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
    /// <summary>
    /// Builds the <see cref="StoryWalk"/> rules from a content pack: who is in the world (sectors on at the start, sectors
    /// switched by infos, the liberation switch), where items come from (world, chests, NPC inventories, traders, recipes,
    /// dialogs), every dialog entry (owner present + condition → commands) and every objective the runtime counts.
    /// Also picks the main story: quests of the "Hauptstory" folder and the quests their starting entries depend on.
    /// </summary>
    public sealed class StoryWalkBuilder
    {
        readonly IContentDatabase _db;
        readonly List<WalkRule> _rules = new List<WalkRule>();
        readonly HashSet<string> _initial = new HashSet<string>(StringComparer.Ordinal);
        readonly Dictionary<string, string> _sectorOfActor = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly HashSet<string> _actors = new HashSet<string>(StringComparer.Ordinal);
        readonly Dictionary<string, TreasureSetDef> _sets;
        readonly HashSet<string> _started = new HashSet<string>(StringComparer.Ordinal);

        public StoryWalkBuilder(IContentDatabase db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _sets = db.All<TreasureSetDef>().GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        }

        public IReadOnlyCollection<string> Initial => _initial;
        public IReadOnlyList<WalkRule> Rules => _rules;
        public Dictionary<string, WalkRule> EntryRules { get; } = new Dictionary<string, WalkRule>(StringComparer.Ordinal);

        /// <summary>Known actor names (NPCs without the "#guid" suffix and Speaker objects).</summary>
        public bool IsKnownActor(string name) => name != null && _actors.Contains(name);

        public string SectorOf(string actor) => actor != null && _sectorOfActor.TryGetValue(actor, out var s) ? s : null;

        public StoryWalk Build()
        {
            Presence();
            ItemSources();
            foreach (var e in _db.All<DialogEntryDef>()) Entry(e);
            StoryStands();
            StoryBoulders();
            ScriptTriggers();
            foreach (var r in _rules) foreach (var f in r.Effects)
                if (f.StartsWith("quest:", StringComparison.Ordinal) && f.EndsWith("=Running", StringComparison.Ordinal)) _started.Add(f.Substring(6, f.Length - 14));
            foreach (var f in _initial)
                if (f.StartsWith("quest:", StringComparison.Ordinal) && f.EndsWith("=Running", StringComparison.Ordinal)) _started.Add(f.Substring(6, f.Length - 14));
            foreach (var q in _db.All<QuestDef>()) Objective(q);
            return new StoryWalk(_rules, _initial);
        }

        // ------------------------------------------------------------------ who is in the world

        static string BaseName(string id)
        {
            int hash = id.IndexOf('#');
            return hash > 0 ? id.Substring(0, hash) : id;
        }

        void Presence()
        {
            var sectorsOn = new HashSet<string>(_db.All<WorldSectorDef>().Where(s => s.EnabledAtStart).Select(s => s.Id), StringComparer.Ordinal);
            foreach (var s in sectorsOn) _initial.Add(WalkFacts.Sector(s));
            _initial.Add(WalkFacts.Present(ActorId.Player.Name));
            _actors.Add(ActorId.Player.Name);
            var bySector = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            void Place(string name, string sector)
            {
                _actors.Add(name);
                if (sector == null || sectorsOn.Contains(sector))
                {
                    _initial.Add(WalkFacts.Present(name));
                    _sectorOfActor.Remove(name);
                    return;
                }
                if (!_initial.Contains(WalkFacts.Present(name))) _sectorOfActor[name] = sector;
                if (!bySector.TryGetValue(sector, out var list)) bySector[sector] = list = new List<string>();
                list.Add(name);
            }
            foreach (var n in _db.All<NpcDef>().Where(n => !n.Id.IsPlayer)) Place(BaseName(n.Id.Name), n.Sector);
            foreach (var o in _db.All<InteractiveObjectDef>().Where(o => o.Kind == InteractiveKind.Speaker && o.Name != null)) Place(o.Name, o.Sector);
            foreach (var kv in bySector)
                _rules.Add(new WalkRule("sector " + kv.Key, "sector", null, WalkNeed.Of(WalkFacts.Sector(kv.Key)),
                                        kv.Value.Select(WalkFacts.Present)));
            foreach (var sw in EnclaveSectorSwitches.All.Where(s => s.SectorOn != null))
                _rules.Add(new WalkRule("liberation " + sw.Enclave, "sector", null, WalkNeed.Always,
                                        new[] { WalkFacts.Sector(sw.SectorOn) }, new[] { FormattableString.Invariant($"needs {sw.Enclave} liberated (fled enclave, hero > {EnclaveSectorSwitches.Metres:0} m away)") }));
            var start = _db.All<GameStartDef>().FirstOrDefault();
            if (start != null)
            {
                foreach (var q in start.Quests) _initial.Add(WalkFacts.Quest(q, "Running"));
            }
        }

        // ------------------------------------------------------------------ item sources

        IEnumerable<string> ItemsOfSets(IEnumerable<string> sets) =>
            (sets ?? Enumerable.Empty<string>()).SelectMany(s => _sets.TryGetValue(s, out var t) ? t.Stacks.Select(x => x.Item) : Enumerable.Empty<string>());

        /// <summary>Story bookstands (BS_Chronicles): reading needs the game event, then runs and succeeds quests, switches sectors.</summary>
        void StoryStands()
        {
            if (!_db.TryGet(CraftingRules.DefinitionId, out CraftingRules crafting)) return;
            foreach (var story in crafting.Bookstands.Stories)
                foreach (var o in _db.All<InteractiveObjectDef>().Where(o => o.UseFunc != null && o.UseFunc.Trim() == story.UseFunc))
                {
                    var need = new List<WalkNeed>();
                    if (o.Sector != null) need.Add(WalkNeed.Of(WalkFacts.Sector(o.Sector)));
                    if (!string.IsNullOrEmpty(story.RequiredFlag)) need.Add(WalkNeed.Of(WalkFacts.Flag(story.RequiredFlag)));
                    var effects = story.RunQuests.Select(q => WalkFacts.Quest(q, "Running"))
                                       .Concat(story.SucceedQuests.Select(q => WalkFacts.Quest(q, "Success")))
                                       .Concat(story.Sectors.Select(WalkFacts.Sector));
                    _rules.Add(new WalkRule("bookstand " + o.Name, "source", o.Name, WalkNeed.All(need), effects));
                }
        }

        /// <summary>Orc boulders whose lift sets a game event (OB_Faring_Boulder): lifting needs the boulder's sector and strength.</summary>
        void StoryBoulders()
        {
            var rules = _db.TryGet(BoulderRules.DefinitionId, out BoulderRules b) ? b : BoulderRules.Default;
            foreach (var o in _db.All<InteractiveObjectDef>().Where(o => o.Kind == InteractiveKind.Boulder))
            {
                var weight = rules.WeightOf(o.UseFunc);
                if (weight?.LiftFlag == null) continue;
                _rules.Add(new WalkRule("boulder " + o.Name, "source", o.Name,
                                        o.Sector != null ? WalkNeed.Of(WalkFacts.Sector(o.Sector)) : WalkNeed.Always,
                                        new[] { WalkFacts.Flag(weight.LiftFlag) },
                                        new[] { $"the hero lifts {o.Name} with >= {weight.LiftStrength} strength" }));
            }
        }

        /// <summary>Script-side story changes (StoryTriggers): received/used items and areas entered set flags or switch sectors.</summary>
        void ScriptTriggers()
        {
            if (!_db.TryGet(StoryTriggerRules.DefinitionId, out StoryTriggerRules triggers)) return;
            foreach (var t in triggers.ItemReceived)
                _rules.Add(new WalkRule("received " + t.Item, "source", null, WalkNeed.Of(WalkFacts.Item(t.Item)), t.Sectors.Select(WalkFacts.Sector)));
            foreach (var t in triggers.ItemUsed)
                _rules.Add(new WalkRule("used " + t.Item, "source", null, WalkNeed.Of(WalkFacts.Item(t.Item)), t.SetFlags.Select(WalkFacts.Flag),
                                        t.MinStaminaMax > 0 ? new[] { $"the hero drinks {t.Item} with >= {t.MinStaminaMax} maximum stamina" } : null));
            foreach (var t in triggers.AreaEntered)
                _rules.Add(new WalkRule("entered " + t.Area, "source", t.Actor, WalkNeed.Of(WalkFacts.Present(BaseName(t.Actor))), t.SetFlags.Select(WalkFacts.Flag),
                                        new[] { $"{t.Actor} must enter {t.Area}" }));
        }

        void ItemSources()
        {
            foreach (var kit in _db.All<HeroStartKit>())
                foreach (var i in kit.Items) _initial.Add(WalkFacts.Item(i.Item));
            foreach (var g in _db.All<WorldItemDef>().GroupBy(w => w.Sector ?? ""))
                _rules.Add(new WalkRule("world items" + (g.Key.Length > 0 ? " @" + g.Key : ""), "source", null,
                                        g.Key.Length > 0 ? WalkNeed.Of(WalkFacts.Sector(g.Key)) : WalkNeed.Always,
                                        g.Select(w => WalkFacts.Item(w.Item))));
            foreach (var o in _db.All<InteractiveObjectDef>().Where(o => o.Container != null))
            {
                var items = (o.Container.Stacks ?? Array.Empty<TreasureStack>()).Select(s => s.Item).Concat(ItemsOfSets(o.Container.TreasureSets)).ToList();
                if (items.Count == 0) continue;
                var notes = o.Lock != null && o.Lock.Locked ? new[] { $"chest {o.Name} locked" + (o.Lock.Key != null ? " (key " + o.Lock.Key + ")" : $" (difficulty {o.Lock.Difficulty})") } : null;
                _rules.Add(new WalkRule("chest " + o.Name, "source", o.Name,
                                        o.Sector != null ? WalkNeed.Of(WalkFacts.Sector(o.Sector)) : WalkNeed.Always,
                                        items.Select(WalkFacts.Item), notes));
            }
            foreach (var inv in _db.All<NpcInventoryDef>())
            {
                string npc = BaseName(inv.Npc.Name);
                var items = inv.Stacks.Concat(inv.Gear).Select(s => s.Item).Concat(ItemsOfSets(inv.PlunderSets)).ToList();
                if (items.Count > 0)
                    _rules.Add(new WalkRule("inventory " + npc, "source", npc, WalkNeed.Of(WalkFacts.Present(npc)), items.Select(WalkFacts.Item),
                                            new[] { $"from {npc}'s inventory (kill/knock out + loot, steal or trade)" }));
            }
            foreach (var m in _db.All<MerchantDef>())
            {
                string npc = BaseName(m.Actor.Name);
                _rules.Add(new WalkRule("trader " + npc, "source", npc, WalkNeed.Of(WalkFacts.Present(npc)), ItemsOfSets(m.Stock).Select(WalkFacts.Item)));
            }
            foreach (var r in _db.All<RecipeDef>().Where(r => r.Result != null))
                _rules.Add(new WalkRule("recipe " + r.Id, "source", null,
                                        WalkNeed.All(r.Ingredients.Select(i => WalkNeed.Of(WalkFacts.Item(i.Item)))), new[] { WalkFacts.Item(r.Result) }));
        }

        // ------------------------------------------------------------------ dialog entries

        // exact: a holder the runtime cannot resolve (e.g. the raw G3 list "PC_Hero;PC_Hero") must not pass as the player
        static bool IsPlayerRef(ActorRef r) => r.Value == ActorId.Player.Name;

        WalkNeed ActorNeed(ActorRef r) =>
            r.IsPartner || IsPlayerRef(r) || r.Value == null ? WalkNeed.Always : WalkNeed.Of(WalkFacts.Present(BaseName(r.Value)));

        /// <summary>Positive translation of a story condition; <paramref name="notes"/> collects what cannot be proven.</summary>
        public WalkNeed NeedOf(Condition c, List<string> notes)
        {
            switch (c)
            {
                case null: return WalkNeed.Always;
                case AllOf all: return WalkNeed.All(all.Conditions.Select(x => NeedOf(x, notes)));
                case AnyOf any: return WalkNeed.Any(any.Conditions.Select(x => NeedOf(x, notes)));
                case Not _: return WalkNeed.Always;
                case Never _: return WalkNeed.Never;
                case Unsupported u:
                    notes?.Add("unsupported condition " + u.Feature);
                    return WalkNeed.Never;
                case FlagSet f: return WalkNeed.Of(WalkFacts.Flag(f.Flag));
                case QuestStatusIs q:
                    return WalkNeed.Any(q.Statuses.Select(s => s == QuestStatus.Open ? WalkNeed.Always : WalkNeed.Of(WalkFacts.Quest(q.Quest.Name, s.ToString()))));
                case QuestFinished qf:
                    return WalkNeed.Any(WalkFacts.FinishedStatuses.Select(s => WalkNeed.Of(WalkFacts.Quest(qf.Quest.Name, s))));
                case QuestOverdue qo: return WalkNeed.Of(WalkFacts.Quest(qo.Quest.Name, "Running"));
                case HasItems h:
                    if (IsPlayerRef(h.Holder)) return h.Amount <= 0 ? WalkNeed.Always : WalkNeed.Of(WalkFacts.Item(h.Item));
                    if (!h.Holder.IsPartner && !IsKnownActor(BaseName(h.Holder.Value ?? "")))
                    {
                        notes?.Add($"item holder '{h.Holder.Value}' is no actor: hasItems {h.Item} is never true");
                        return WalkNeed.Never;
                    }
                    return ActorNeed(h.Holder);
                case CarriesQuestItems cq:
                {
                    // the hero carries the quest's items (all of them, or any for a part delivery)
                    if (!_db.TryGet(cq.Quest, out QuestDef def)) return WalkNeed.Never;
                    var items = def.Objective.Targets.Select(t => WalkNeed.Of(WalkFacts.Item(t.Target)));
                    return cq.Share == QuestItemShare.All ? WalkNeed.All(items) : WalkNeed.Any(items);
                }
                case TalkedToPlayer t: return ActorNeed(t.Actor);
                case ActorIs a: return ActorNeed(a.Actor);
                case InPlayerParty p: return WalkNeed.All(p.Actors.Select(ActorNeed));
                case Near n:
                    notes?.Add(FormattableString.Invariant($"{(n.Actor.IsPartner ? "partner" : n.Actor.Value)} must stand within {n.Metres:0} m of {n.Entity}"));
                    return ActorNeed(n.Actor);
                default: return WalkNeed.Always;
            }
        }

        public static IEnumerable<string> EffectsOf(DialogCommand c, ActorId owner)
        {
            switch (c)
            {
                case SetFlag f: yield return WalkFacts.Flag(f.Flag); break;
                case StartQuest s: yield return WalkFacts.Quest(s.Quest.Name, "Running"); break;
                case ReportToQuest r: yield return WalkFacts.Quest(r.Quest.Name, "Success"); break;
                case SucceedQuest s: yield return WalkFacts.Quest(s.Quest.Name, "Success"); break;
                case CompleteDelivery d: yield return WalkFacts.Quest(d.Quest.Name, "Success"); break;
                case CloseQuest cq:
                    yield return WalkFacts.Quest(cq.Quest.Name, "Obsolete");
                    yield return WalkFacts.Quest(cq.Quest.Name, "Cancelled");
                    break;
                case FailQuest fq: yield return WalkFacts.Quest(fq.Quest.Name, "Failed"); break;
                case GiveItems g when IsPlayerRef(g.To): yield return WalkFacts.Item(g.Item); break;
                case GiveItems g when IsPlayerRef(g.From) && !g.To.IsPartner: yield return WalkFacts.Gave(BaseName(g.To.Value), g.Item); break;
                case GiveItems g when IsPlayerRef(g.From): yield return WalkFacts.Gave(BaseName(owner.Name), g.Item); break;
                case SetSectorStatus s when s.Enabled: yield return WalkFacts.Sector(s.Sector); break;
            }
        }

        void Entry(DialogEntryDef e)
        {
            var notes = new List<string>();
            string owner = BaseName(e.Owner.Name);
            var need = WalkNeed.All(new[] { owner == ActorId.Player.Name ? WalkNeed.Always : WalkNeed.Of(WalkFacts.Present(owner)), NeedOf(e.Condition, notes) });
            // the menu path: every ancestor must be offered too
            var seen = new HashSet<string>(StringComparer.Ordinal) { e.Id.Name };
            for (var p = e; !p.Parent.IsEmpty && seen.Add(p.Parent.Name) && _db.TryGet(p.Parent, out DialogEntryDef parent); p = parent)
                need = WalkNeed.All(new[] { need, NeedOf(parent.Condition, null) });
            foreach (var pre in e.PreChecks)
                switch (pre)
                {
                    case MinEnclaveFame f: notes.Add($"pre-check: enclave fame >= {f.Minimum} with {(f.Member.IsPartner ? owner : f.Member.Value)}'s enclave"); break;
                    case MinPoliticalFame f: notes.Add($"pre-check: political fame >= {f.Minimum} with {(f.Member.IsPartner ? owner : f.Member.Value)}'s faction"); break;
                    case MinGold g: notes.Add($"pre-check: {g.Amount} gold"); break;
                }
            var effects = new List<string>();
            foreach (var c in e.Commands)
            {
                if (c is ReportToQuest report)
                {
                    // QuestService.Count: the reporter must be one of the objective's targets (exact name)
                    string reporter = report.Reporter.IsPartner ? owner : report.Reporter.Value;
                    if (_db.TryGet(report.Quest, out QuestDef rq) && rq.Objective.IndexOf(reporter) < 0)
                    {
                        notes.Add($"reportToQuest {report.Quest.Name} by '{reporter}' matches no target ({string.Join(",", rq.Objective.Targets.Select(t => "'" + t.Target + "'"))}): never counted");
                        continue;
                    }
                    if (rq != null && rq.Objective.Kind == QuestObjectiveKind.Report && rq.Objective.Targets.Count > 1)
                    {
                        // one of several reports: the objective succeeds when every target reported
                        effects.Add(WalkFacts.Reported(rq.Id.Name, reporter));
                        continue;
                    }
                }
                if (c is DeliverQuestItems deliver && _db.TryGet(deliver.Quest, out QuestDef dq))
                {
                    // the entry hands the quest's items to its receiver (QuestItemCounter counts them)
                    string receiver = deliver.Receiver.IsPartner ? owner : BaseName(deliver.Receiver.Value);
                    effects.AddRange(dq.Objective.Targets.Select(t => WalkFacts.Gave(receiver, t.Target)));
                    continue;
                }
                effects.AddRange(EffectsOf(c, e.Owner));
            }
            var rule = new WalkRule(e.Id.Name, "entry", owner, need, effects, notes);
            _rules.Add(rule);
            EntryRules[e.Id.Name] = rule;
        }

        // ------------------------------------------------------------------ objectives the runtime counts

        void Objective(QuestDef q)
        {
            var o = q.Objective;
            string running = WalkFacts.Quest(q.Id.Name, "Running");
            // counters also run while the quest is Open (QuestService); a quest nothing starts is counted from the start
            var parts = new List<WalkNeed> { _started.Contains(q.Id.Name) ? WalkNeed.Of(running) : WalkNeed.Always };
            var notes = new List<string>();
            if (!_started.Contains(q.Id.Name)) notes.Add("no entry starts it: counted while Open");
            if (o.Kind == QuestObjectiveKind.FollowNpc)
                _rules.Add(new WalkRule("lost " + q.Id.Name, "objective", null, WalkNeed.Of(running), new[] { WalkFacts.Quest(q.Id.Name, "Lost") },
                                        new[] { "another FollowNpc quest starts (one guide at a time)" }));
            switch (o.Kind)
            {
                case QuestObjectiveKind.HasItems:
                case QuestObjectiveKind.Plunder:
                    if (o.Kind == QuestObjectiveKind.HasItems && !o.Destination.IsEmpty && !o.Destination.IsPlayer && o.Destination.Name != "PC_Hero")
                    {
                        // counted when the items reach the destination (QuestItemCounter): an entry must hand them over
                        string dest = BaseName(o.Destination.Name);
                        parts.Add(WalkNeed.Of(WalkFacts.Present(dest)));
                        parts.AddRange(o.Targets.Select(t => WalkNeed.Of(WalkFacts.Gave(dest, t.Target))));
                        notes.Add($"the items must be handed to {dest} (an entry that gives them)");
                    }
                    else parts.AddRange(o.Targets.Select(t => WalkNeed.Of(WalkFacts.Item(t.Target))));
                    break;
                case QuestObjectiveKind.Kill:
                case QuestObjectiveKind.Defeat:
                case QuestObjectiveKind.DriveAway:
                    parts.AddRange(o.Targets.Where(t => t.Target != ActorId.Player.Name).Select(t => WalkNeed.Of(WalkFacts.Present(BaseName(t.Target)))));
                    break;
                case QuestObjectiveKind.EnterArea:
                    if (!_db.All<AiZoneDef>().Any(z => z.Name == o.Destination.Name)) parts.Add(WalkNeed.Never);
                    break;
                case QuestObjectiveKind.FreeEnclave:
                    notes.Add("enclaves " + string.Join(",", o.Targets.Select(t => t.Target)) + " must be freed in combat");
                    break;
                case QuestObjectiveKind.Arena:
                    parts.AddRange(o.Targets.Select(t => WalkNeed.Of(WalkFacts.Present(BaseName(t.Target)))));
                    break;
                case QuestObjectiveKind.Report when o.Targets.Count > 1:
                    parts.AddRange(o.Targets.Select(t => WalkNeed.Of(WalkFacts.Reported(q.Id.Name, t.Target))));
                    break;
                default: return; // Report / FollowNpc / BringNpc / SpellCast: finished by entries only
            }
            if (o.Kind == QuestObjectiveKind.Arena)
                _rules.Add(new WalkRule("lost " + q.Id.Name, "objective", null, WalkNeed.Of(running), new[] { WalkFacts.Quest(q.Id.Name, "Lost") },
                                        new[] { "the hero loses the bout or leaves the arena" }));
            _rules.Add(new WalkRule("objective " + q.Id.Name, "objective", null, WalkNeed.All(parts), new[] { WalkFacts.Quest(q.Id.Name, "Success") }, notes));
        }

        // ------------------------------------------------------------------ main story

        /// <summary>Hauptstory quests plus, recursively, the quests named in the conditions of their starting entries.</summary>
        public static IReadOnlyCollection<string> MainQuests(IContentDatabase db, string folder = "Hauptstory")
        {
            var quests = db.All<QuestDef>().ToDictionary(q => q.Id.Name);
            var starters = db.All<DialogEntryDef>()
                             .SelectMany(e => e.Commands.OfType<StartQuest>().Select(s => (s.Quest.Name, e)))
                             .GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.Select(x => x.e).ToList());
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var todo = new Stack<string>(quests.Values.Where(q => (q.Folder ?? "").StartsWith(folder, StringComparison.OrdinalIgnoreCase)).Select(q => q.Id.Name));
            while (todo.Count > 0)
            {
                string q = todo.Pop();
                if (!quests.ContainsKey(q) || !seen.Add(q)) continue;
                foreach (var p in quests[q].Prerequisites) todo.Push(p.Name);
                if (starters.TryGetValue(q, out var list))
                    foreach (var e in list)
                        foreach (var r in QuestsIn(e.Condition).Where(r => r != q)) todo.Push(r);
            }
            return seen;
        }

        public static IEnumerable<string> QuestsIn(Condition c)
        {
            switch (c)
            {
                case AllOf all: return all.Conditions.SelectMany(QuestsIn);
                case AnyOf any: return any.Conditions.SelectMany(QuestsIn);
                case Not not: return QuestsIn(not.Condition);
                case QuestStatusIs q: return new[] { q.Quest.Name };
                case QuestFinished f: return new[] { f.Quest.Name };
                case QuestOverdue o: return new[] { o.Quest.Name };
                default: return Enumerable.Empty<string>();
            }
        }
    }
}
#endif
