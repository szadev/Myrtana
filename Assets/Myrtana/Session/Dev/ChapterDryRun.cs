#if !UNITY_5_3_OR_NEWER || UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;
using Myrtana.Story;

namespace Myrtana.Session.Dev
{
    /// <summary>
    /// Runs a chapter scenario against a headless <see cref="GameSession"/> (no Unity): the story ops (quest, flag, give,
    /// ask, kill, loot, pickup, raid, bring, tp …) act on the session exactly as the editor runner does, the presentation
    /// ops (camera, inputs, screenshots, real melee) are skipped or approximated, and every <c>expect</c> / <c>wait until</c>
    /// is evaluated after ticking the session. It finds dialog steps that are not offered and expectations that do not
    /// hold before a runner spends editor time on them (G3Cli <c>story walk --dry</c>, Docs/story-walk.md).
    /// </summary>
    public sealed class ChapterDryRun
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const float Step = 0.25f;

        readonly GameSession _s;
        readonly Func<string, ScenarioSpec> _include;
        readonly Dictionary<string, string> _vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        WorldPose _hero;
        int _depth;
        /// <summary>NPCs a <c>freeze</c> holds (by name; all but the exceptions after a freeze without <c>only</c>).</summary>
        readonly HashSet<string> _frozen = new HashSet<string>(StringComparer.Ordinal), _frozenExcept = new HashSet<string>(StringComparer.Ordinal);
        bool _frozenAll;

        public ChapterDryRun(GameSession session, Func<string, ScenarioSpec> include = null)
        {
            _s = session ?? throw new ArgumentNullException(nameof(session));
            _include = include;
            _hero = session.Start?.Definition?.HeroPose ?? default;
            session.Crime.World.ReportPlayer(_hero, null);
        }

        /// <summary>Called with each step (variables resolved) before it runs: the scenario writer inspects the session there.</summary>
        public Action<ScenarioStep> BeforeStep { get; set; }

        /// <summary>The hero's headless pose (moved by tp and escort).</summary>
        public WorldPose HeroPose => _hero;

        public List<string> Lines { get; } = new List<string>();
        public List<string> Failures { get; } = new List<string>();
        public int Skipped { get; private set; }

        /// <summary>Headless presenter: remembers nothing, the dry run completes lines itself.</summary>
        public sealed class SilentPresenter : IDialogPresenter
        {
            public void Open(ActorId partner) { }
            public void ShowLine(DialogLine line) { }
            public void ShowOptions(IReadOnlyList<DialogOption> options) { }
            public void Close() { }
        }

        public bool Run(ScenarioSpec spec)
        {
            foreach (var e in spec.Errors) Failures.Add("parse: " + e);
            foreach (var raw in spec.Steps)
            {
                var missing = new List<string>();
                var st = raw.Map(v => ScenarioVars.Resolve(v, _vars, missing));
                if (missing.Count > 0) { Fail(st, "unknown " + string.Join(",", missing)); return false; }
                int before = Failures.Count;
                BeforeStep?.Invoke(st);
                if (st.Has(OpSpec.Unless) && ScenarioCondition.TryParse(st.Get(OpSpec.Unless), out var unless, out _))
                {
                    string now = Evaluate(unless);
                    if (now != Unknown && unless.Test(now)) { Lines.Add($"skip (unless {unless}: {now}) {st.Source}"); continue; }
                }
                try { Execute(st); }
                catch (Exception e) { Fail(st, e.GetType().Name + ": " + e.Message); }
                if (Failures.Count > before && st.Op != "expect") return false;
            }
            return Failures.Count == 0;
        }

        void Log(ScenarioStep st, string text) => Lines.Add(text);
        void Fail(ScenarioStep st, string text) => Failures.Add($"'{st.Source}': {text}");
        void Skip(ScenarioStep st)
        {
            Skipped++;
            Lines.Add("skip (presentation) " + st.Source);
        }

        void Tick(float seconds)
        {
            for (float t = 0; t < seconds; t += Step) _s.Tick(Step);
        }

        static ActorId Actor(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Equals("hero", StringComparison.OrdinalIgnoreCase) || text.Equals("player", StringComparison.OrdinalIgnoreCase))
                return ActorId.Player;
            return new ActorId(text.StartsWith("npc:", StringComparison.OrdinalIgnoreCase) ? text.Substring(4) : text);
        }

        // ------------------------------------------------------------------ ops

        void Execute(ScenarioStep st)
        {
            var s = _s;
            var P = ActorId.Player;
            switch (st.Op)
            {
                case "safe":
                {
                    int have = s.Fame.Of(FameKind.Political, "Orc"), fame = st.Int("fame", 80);
                    if (have < fame) s.Fame.Add(FameKind.Political, "Orc", fame - have);
                    s.NpcCombat.DisengageAll();
                    s.Crime.Ledger.ForgiveGrudges(); // as the runner's (fix-65)
                    s.Crime.Crime.ClearTransient();
                    break;
                }
                case "tp":
                {
                    string target = st.Get("target") ?? (st.Has("spot") ? "spot:" + st.Get("spot") : st.Has("npc") ? "npc:" + st.Get("npc") : st.Get("pos"));
                    if (target != null && (target.StartsWith("spot:", StringComparison.OrdinalIgnoreCase) || target.StartsWith("?"))) { Skip(st); break; }
                    if (!TryPose(target, out var pose)) { Fail(st, "no target " + target); break; }
                    float dist = st.Has("zone") ? 0f : st.Float("dist", 0f); // zone=: stays inside (at the NPC itself)
                    _hero = new WorldPose(pose.X + dist, pose.Y, pose.Z, pose.Yaw);
                    s.Crime.World.ReportPlayer(_hero, null);
                    s.WorldState.CheckNow();
                    Log(st, FormattableString.Invariant($"tp {target} ({_hero.X:0},{_hero.Y:0},{_hero.Z:0})"));
                    break;
                }
                case "time":
                    if (s.Clock is GameClock clock) clock.Skip(clock.SecondsUntilHour(st.Float("hour", 12f)));
                    break;
                case "bring":
                {
                    var id = Actor(st.Get("npc"));
                    if (!s.Npcs.TryGet(id, out var agent)) { Fail(st, "no NPC " + id); break; }
                    var at = _hero;
                    if (st.Has("near") && !TryPose(st.Get("near"), out at)) { Fail(st, "no target " + st.Get("near")); break; }
                    var def = agent.Definition;
                    s.NpcCombat.Get(id)?.Disengage();
                    s.Npcs.Remove(id);
                    if (s.Npcs.Add(def, new WorldPose(at.X + st.Float("dist", 2f), at.Y, at.Z, at.Yaw)) == null) Fail(st, "re-adding refused " + id);
                    break;
                }
                case "escort":
                {
                    // the walk itself is an editor check: hero and NPC arrive together (the NPC keeps its party/guide role)
                    var id = Actor(st.Get("npc"));
                    if (!s.Npcs.TryGet(id, out var agent)) { Fail(st, "no NPC " + id); break; }
                    if (!TryPose(st.Get("to"), out var at)) { Fail(st, "no target " + st.Get("to")); break; }
                    // a frozen NPC never walks in the editor (editor-runner-23: followers stood still, an idle guide stayed)
                    if (_frozenAll ? !_frozenExcept.Contains(id.Name) : _frozen.Contains(id.Name)) { Fail(st, $"escort {id}: the NPC is frozen (unfreeze it first)"); break; }
                    // the escort needs the guide/follow job the quest's start gives; an entry an earlier quest of the file
                    // already played (asked with given=1) does not run again (editor-runner-23 Rufus_FleesFromFarm)
                    var mode = s.Party.Escorts.ModeOf(id);
                    if (mode != EscortMode.Follow && mode != EscortMode.Guide) { Fail(st, $"escort {id}: neither guides nor follows the hero ({mode})"); break; }
                    _hero = new WorldPose(at.X + 2f, at.Y, at.Z, at.Yaw);
                    s.Crime.World.ReportPlayer(_hero, null);
                    var def = agent.Definition;
                    s.NpcCombat.Get(id)?.Disengage();
                    s.Npcs.Remove(id);
                    if (s.Npcs.Add(def, new WorldPose(at.X + 1f, at.Y, at.Z, at.Yaw)) == null) { Fail(st, "re-adding refused " + id); break; }
                    s.WorldState.CheckNow();
                    Tick(Step);
                    Log(st, FormattableString.Invariant($"approx escort {id} to {st.Get("to")} ({at.X:0},{at.Z:0})"));
                    break;
                }
                case "despawn":
                    if (st.Has("target")) s.Removal.Remove(Actor(st.Get("target"))); else Skip(st);
                    break;
                case "party":
                {
                    var id = Actor(st.Get("npc"));
                    string mode = st.Get("mode", "add").ToLowerInvariant();
                    if (mode == "add") s.Party.JoinCompanion(id);
                    else if (mode == "remove") s.Party.Dismiss(id);
                    else Skip(st);
                    break;
                }
                case "hp":
                {
                    var id = Actor(st.Get("actor"));
                    if (!id.IsPlayer) s.Combat.Vitals.AddHitPoints(id, st.Int("value", 3) - s.Combat.Vitals.HitPoints(id));
                    break;
                }
                case "ko":
                {
                    var id = Actor(st.Get("actor"));
                    if (id.IsPlayer) { Skip(st); break; }
                    s.Combat.Vitals.AddHitPoints(id, -s.Combat.Vitals.HitPoints(id));
                    s.Combat.Vitals.KnockOut(id, true);
                    break;
                }
                case "kill":
                    s.Combat.Vitals.Kill(Actor(st.Get("actor")), string.Equals(st.Get("killer"), "hero", StringComparison.OrdinalIgnoreCase));
                    Tick(Step);
                    break;
                case "fight":
                case "finisher":
                {
                    // approximation of the real melee: the hero knocks the NPC out (fight) or kills it (finisher)
                    var id = Actor(st.Get("npc"));
                    if (st.Op == "finisher" || s.Combat.Vitals.StateOf(id) == VitalState.Down) s.Combat.Vitals.Kill(id, true);
                    else s.Combat.Vitals.KnockOut(id, true);
                    Lines.Add($"approx {st.Source}: {s.Combat.Vitals.StateOf(id)}");
                    Tick(Step);
                    break;
                }
                case "restore":
                    s.Combat.Vitals.RestoreFully(Actor(st.Get("actor")));
                    break;
                case "give":
                {
                    string item = Item(st, st.Get("item"));
                    if (item != null) s.Items.Inventories.Give(P, item, st.Int("n", 1));
                    break;
                }
                case "docs":
                {
                    var items = DocumentsPage.AllOfKind(s, st.Get("kind", "all"));
                    if (items == null) { Fail(st, "docs: unknown kind " + st.Get("kind")); break; }
                    foreach (var item in items)
                        if (s.Items.Inventories.Of(P).Count(item) == 0) s.Items.Inventories.Give(P, item, 1);
                    break;
                }
                case "equip":
                {
                    string item = Item(st, st.Get("item"));
                    if (item != null && s.Items.Inventories.Of(P).Count(item) == 0) s.Items.Inventories.Give(P, item, 1);
                    if (item != null) s.Items.Equipment.Equip(item);
                    break;
                }
                case "learn":
                    foreach (var skill in StoryScenario.List(st.Get("skills"))) s.Progression.LearnSkill(skill);
                    break;
                case "attr":
                {
                    string name = st.Get("name").ToUpperInvariant();
                    int value = st.Int("value", 100);
                    s.Hero.Stats.Set(name, new StatValue(value, Math.Max(value, s.Hero.Stats.Maximum(name))));
                    break;
                }
                case "level":
                {
                    int target = st.Int("n", 3), guard = 0;
                    while (s.Hero.Level < target && guard++ < 200) s.Progression.AwardXp((int)Math.Max(1, s.Progression.XpForNextLevel));
                    break;
                }
                case "quest":
                {
                    var to = Enum.TryParse<QuestStatus>(st.Get("status", "Running"), true, out var qs) ? qs : QuestStatus.Running;
                    foreach (var id in StoryScenario.List(st.Get("id")))
                    {
                        var q = new Id<QuestDef>(id);
                        if (!s.Content.Database.TryGet(q, out QuestDef _)) { Fail(st, "unknown quest " + id); continue; }
                        if (!s.Quests.TrySetStatus(q, to) && s.Quests.StatusOf(q) != to) Fail(st, $"quest {id}: {s.Quests.StatusOf(q)} -> {to} refused");
                    }
                    break;
                }
                case "flag":
                    foreach (var name in StoryScenario.List(st.Get("name")))
                        if (st.Bool("on", true)) s.Flags.Set(name); else s.Flags.Clear(name);
                    break;
                case "sector":
                    if (!s.WorldState.Sectors.Set(st.Get("name"), st.Bool("on", true)) && !s.WorldState.Sectors.IsKnown(st.Get("name"))) Fail(st, "sector unknown");
                    break;
                case "fame":
                {
                    var kind = Enum.TryParse<FameKind>(st.Get("kind", "Political"), true, out var k) ? k : FameKind.Political;
                    s.Fame.Add(kind, st.Get("group"), st.Int("value", 80));
                    break;
                }
                case "enclave":
                {
                    string id = st.Get("id"), status = st.Get("status", "Routine");
                    if (status.Equals("liberate", StringComparison.OrdinalIgnoreCase)) s.WorldState.Liberation.Liberate(id);
                    else if (Enum.TryParse<EnclaveStatus>(status, true, out var es)) s.Enclaves.SetStatus(id, es);
                    else Fail(st, "enclave status " + status);
                    break;
                }
                case "raid": Raid(st); break;
                case "talk":
                    if (s.Dialog.IsActive) s.Dialog.Abort();
                    s.Dialog.Talk(Actor(st.Get("npc")));
                    break;
                case "choose":
                {
                    var c = s.Dialog.Current;
                    string e = st.Get("entry");
                    if (c == null || c.Phase != ConversationPhase.AwaitingChoice || !c.Options.Any(o => o.Entry.Name == e)) { Fail(st, "not offered: " + DialogState()); break; }
                    s.Dialog.Choose(new Id<DialogEntryDef>(e));
                    break;
                }
                case "lines": CompleteLines(null); break;
                case "abort":
                    if (s.Dialog.IsActive) s.Dialog.Abort();
                    break;
                case "ask": Ask(st); break;
                case "topics":
                {
                    var npc = Actor(st.Get("npc"));
                    var builder = Topics();
                    var now = s.Content.Database.All<DialogEntryDef>().Where(e => e.Owner == npc && builder.IsAvailable(e, World.ConditionsFor(npc))).Select(e => e.Id.Name);
                    Log(st, $"topics {npc}: [{string.Join(",", now)}]");
                    break;
                }
                case "talked":
                    foreach (var npc in StoryScenario.List(st.Get("npcs"))) World.Actors.MarkTalkedToPlayer(Actor(npc));
                    break;
                case "played":
                    foreach (var id in StoryScenario.List(st.Get("entries")))
                    {
                        if (!s.Content.Database.TryGet(new Id<DialogEntryDef>(id), out DialogEntryDef def)) { Fail(st, "no entry " + id); continue; }
                        World.DialogMemory.MarkGiven(def.Id);
                        if (!def.Owner.IsPlayer) World.Actors.MarkTalkedToPlayer(def.Owner);
                    }
                    break;
                case "loot": Loot(st); break;
                case "pickup": Pickup(st); break;
                case "use": Use(st); break;
                case "wait":
                    if (!st.Has("until")) { Tick(Math.Min(st.Float("secs", 1f), 30f)); break; }
                    if (!ScenarioCondition.TryParse(st.Get("until"), out var wc, out var werr)) { Fail(st, werr); break; }
                    {
                        float timeout = st.Float("timeout", st.Float("secs", 15f)), t = 0;
                        string actual = Evaluate(wc);
                        while (!wc.Test(actual) && t < timeout && actual != Unknown)
                        {
                            // the presenter finishes spoken lines in play; here they end at once
                            if (_s.Dialog.IsActive && _s.Dialog.Current.Phase == ConversationPhase.AwaitingLine) _s.Dialog.CompleteLine();
                            _s.Tick(Step);
                            t += Step;
                            actual = Evaluate(wc);
                        }
                        if (actual == Unknown) Skip(st);
                        else if (!wc.Test(actual) && !st.Bool("soft", false)) Fail(st, $"timeout (last {actual})");
                    }
                    break;
                case "expect":
                {
                    if (!ScenarioCondition.TryParse(st.Get("cond"), out var c, out var err)) { Fail(st, err); break; }
                    string actual = Evaluate(c);
                    if (actual == Unknown) Skip(st);
                    else if (!c.Test(actual)) Fail(st, $"was {actual}");
                    else Lines.Add($"ok {c} ({actual})");
                    break;
                }
                case "freeze":
                    if (st.Has("only")) foreach (var n in st.Get("only").Split(',')) _frozen.Add(Actor(n.Trim()).Name);
                    else
                    {
                        _frozenAll = true;
                        foreach (var n in (st.Get("except") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) _frozenExcept.Add(Actor(n.Trim()).Name);
                    }
                    break;
                case "unfreeze":
                    _frozen.Clear();
                    _frozenExcept.Clear();
                    _frozenAll = false;
                    break;
                case "log": Lines.Add("log " + st.Get("text")); break;
                case "set": _vars[st.Get("name")] = st.Get("value", ""); break;
                case "scenario":
                {
                    var inner = _include?.Invoke(st.Get("name"));
                    if (inner == null) { Fail(st, "no scenario " + st.Get("name")); break; }
                    if (++_depth > 8) { Fail(st, "include depth"); break; }
                    Run(inner);
                    _depth--;
                    break;
                }
                default:
                    Skip(st);
                    break;
            }
        }

        string Item(ScenarioStep st, string query)
        {
            var db = _s.Content.Database;
            if (db.TryGet(query, out ItemDef exact)) return exact.Id;
            string id = DevLookup.Best(db.All<ItemDef>().SelectMany(i => DevLookup.Names(i.Id, i.Name, _s.Localizer)), query, out _);
            if (id == null) Fail(st, "no item " + query);
            return id;
        }

        public bool TryPose(string target, out WorldPose pose)
        {
            pose = default;
            if (string.IsNullOrEmpty(target)) return false;
            if (ScenarioStep.TryVector(target, out var xyz))
            {
                pose = xyz.Length == 3 ? new WorldPose(xyz[0], xyz[1], xyz[2], 0) : new WorldPose(xyz[0], _hero.Y, xyz[1], 0);
                return true;
            }
            if (StoryScenario.TryParseTarget(target, out string kind, out string name))
            {
                if (kind == "place") return _s.Party.Positions.TryGetPlace(name, out pose);
                if (kind == "zone")
                {
                    var zone = _s.Content.Database.All<AiZoneDef>().FirstOrDefault(z => string.Equals(z.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (zone == null || !StoryScenario.TryInside(zone.Outline, out float x, out float z)) return false;
                    pose = new WorldPose(x, zone.Floor + 0.5f, z, 0);
                    return true;
                }
                return TryObjectPose(name, out pose);
            }
            var id = Actor(target);
            if (id.IsPlayer) { pose = _hero; return true; }
            if (_s.Npcs.TryGet(id, out var agent)) { pose = _s.Npcs.PoseOf(agent); return true; }
            return TryObjectPose(id.Name, out pose);
        }

        bool TryObjectPose(string name, out WorldPose pose)
        {
            var def = Objects(name).FirstOrDefault();
            pose = def != null ? new WorldPose(def.Position[0], def.Position[1], def.Position[2], 0) : default;
            return def != null;
        }

        IEnumerable<InteractiveObjectDef> Objects(string name) =>
            _s.Content.Database.All<InteractiveObjectDef>()
              .Where(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase) && o.Position != null && _s.Interaction.Objects.IsInWorld(o))
              .OrderBy(o => Sq(o.Position[0] - _hero.X) + Sq(o.Position[1] - _hero.Y) + Sq(o.Position[2] - _hero.Z));

        static float Sq(float v) => v * v;

        // ------------------------------------------------------------------ dialog

        StoryWorld _world;
        TopicListBuilder _topics;

        StoryWorld World => _world ??= typeof(DialogService).GetField("_world", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                                                           ?.GetValue(_s.Dialog) as StoryWorld;

        TopicListBuilder Topics() => _topics ??= new TopicListBuilder(new DialogCatalog(_s.Content.Database.All<DialogEntryDef>()), World.DialogMemory);

        string DialogState()
        {
            var c = _s.Dialog.Current;
            return c == null ? "no dialog" : $"{c.Partner} {c.Phase} options=[{string.Join(",", c.Options.Select(o => o.Entry.Name))}]";
        }

        void CompleteLines(HashSet<string> played)
        {
            for (int guard = 0; _s.Dialog.IsActive && guard < 400; guard++)
            {
                var c = _s.Dialog.Current;
                if (c.Entry != null && c.Purpose == ScriptPurpose.Refusal) played?.Add("refused:" + c.Entry.Id.Name);
                else if (c.Entry != null) played?.Add(c.Entry.Id.Name);
                if (_askUntil != null && _askUntil.Test(Evaluate(_askUntil)))
                {
                    _s.Dialog.Abort();
                    return;
                }
                if (c.Phase == ConversationPhase.AwaitingChoice) return;
                if (c.Phase == ConversationPhase.AwaitingLine) _s.Dialog.CompleteLine();
                else _s.Tick(Step);
            }
        }

        ScenarioCondition _askUntil;

        void Ask(ScenarioStep st)
        {
            var s = _s;
            var npc = Actor(st.Get("npc"));
            string entry = st.Get("entry");
            var db = s.Content.Database;
            if (!db.TryGet(new Id<DialogEntryDef>(entry), out DialogEntryDef def)) { Fail(st, "no entry " + entry); return; }
            bool availableBefore = Topics().IsAvailable(def, World.ConditionsFor(def.Owner.IsPlayer ? npc : def.Owner));
            var path = StoryScenario.PathTo(entry, id => db.TryGet(new Id<DialogEntryDef>(id), out DialogEntryDef d) && !d.Parent.IsEmpty ? d.Parent.Name : null);
            var played = new HashSet<string>(StringComparer.Ordinal);
            if (World.DialogMemory.WasGiven(def.Id) && st.Bool("given", false))
            {
                // given=1: an entry that already ran (a lone sub-choice runs by itself after its parent) counts as played
                Lines.Add($"ask {npc} {entry}: already given");
                return;
            }
            if (s.Dialog.IsActive && s.Dialog.Current.Partner != npc) s.Dialog.Abort();
            if (!s.Dialog.IsActive) s.Dialog.Talk(npc);
            bool givenBefore = World.DialogMemory.WasGiven(def.Id);
            _askUntil = null;
            if (st.Has("until") && !ScenarioCondition.TryParse(st.Get("until"), out _askUntil, out string untilError)) { Fail(st, untilError); return; }
            for (int i = 0, talks = 1; ; i++)
            {
                CompleteLines(played);
                var c = s.Dialog.Current;
                bool choosing = c != null && c.Phase == ConversationPhase.AwaitingChoice;
                i = StoryScenario.NextChoice(path, i, n => choosing && c.Options.Any(o => o.Entry.Name == n),
                                             n => played.Contains(n) || World.DialogMemory.WasGiven(new Id<DialogEntryDef>(n)), out string why);
                if (i >= path.Count) break;
                if (i < 0 && talks < 3 && !played.Contains(entry))
                {
                    // the talk ended by itself (an urgent info that ends the dialog) or shows an older menu: talk again,
                    // as the player would
                    talks++;
                    i = -1;
                    if (s.Dialog.IsActive) s.Dialog.Abort();
                    s.Dialog.Talk(npc);
                    continue;
                }
                if (i < 0)
                {
                    Fail(st, $"{why} (available before: {availableBefore}; {DialogState()})" + Unmet(path, npc));
                    if (s.Dialog.IsActive) s.Dialog.Abort();
                    return;
                }
                s.Dialog.Choose(new Id<DialogEntryDef>(path[i]));
                played.Add(path[i]);
            }
            CompleteLines(played);
            if (st.Bool("end", true) && s.Dialog.IsActive)
            {
                var c = s.Dialog.Current;
                var exit = c.Phase == ConversationPhase.AwaitingChoice ? c.Options.Where(o => o.IsExit).Select(o => o.Entry).FirstOrDefault() : default;
                if (!exit.IsEmpty)
                {
                    s.Dialog.Choose(exit);
                    CompleteLines(played);
                }
                if (s.Dialog.IsActive) s.Dialog.Abort();
            }
            _askUntil = null;
            // a sub-choice without lines that ran by itself after its parent (G3 runs a lone info without the menu) shows no
            // line to see it by: its one-shot memory tells
            if (!givenBefore && World.DialogMemory.WasGiven(def.Id)) played.Add(entry);
            if (played.Contains("refused:" + entry)) Fail(st, "refused by its pre-check (fame, gold, lesson or party room)");
            else if (!played.Contains(entry)) Fail(st, (givenBefore ? "already played earlier" : "not played") + $" (available before: {availableBefore}; played {string.Join(",", played)})");
            else Lines.Add($"ask {npc} {entry}: played {string.Join(",", played)}");
        }

        /// <summary>The conditions on the menu path that do not hold now (" — unmet X: a, b"), for the failure line.</summary>
        string Unmet(IReadOnlyList<string> path, ActorId npc)
        {
            var parts = new List<string>();
            foreach (var id in path)
            {
                if (!_s.Content.Database.TryGet(new Id<DialogEntryDef>(id), out DialogEntryDef d)) continue;
                var ctx = World.ConditionsFor(d.Owner.IsPlayer ? npc : d.Owner);
                if (!d.Repeatable && World.DialogMemory.WasGiven(d.Id)) parts.Add(id + ": given");
                foreach (var leaf in Leaves(d.Condition).Where(c => !c.Evaluate(ctx))) parts.Add(id + ": " + Describe(leaf));
            }
            return parts.Count == 0 ? "" : " — unmet " + string.Join("; ", parts);
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

        /// <summary>"near(Actor=Freman, Entity=X, Metres=20)": the condition's type and public values.</summary>
        static string Describe(Condition c)
        {
            if (c is Not not) return "not " + Describe(not.Condition);
            if (c is AnyOf any) return "any(" + string.Join(" | ", any.Conditions.Select(Describe)) + ")";
            var props = c.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0)
                         .Select(p => (p.Name, Value: p.GetValue(c)))
                         .Where(x => x.Value != null && !(x.Value is System.Collections.IEnumerable) || x.Value is string)
                         .Select(x => x.Name + "=" + x.Value);
            string name = c.GetType().Name;
            return char.ToLowerInvariant(name[0]) + name.Substring(1) + "(" + string.Join(", ", props) + ")";
        }

        // ------------------------------------------------------------------ loot / pickup / raid

        void Loot(ScenarioStep st)
        {
            string target = st.Get("target");
            string name = target.StartsWith("obj:", StringComparison.OrdinalIgnoreCase) ? target.Substring(4) : null;
            if (name == null && _s.Npcs.TryGet(Actor(target), out _))
            {
                var npc = Actor(target);
                if (!_s.Combat.Vitals.IsDown(npc) && !_s.Combat.Vitals.IsDead(npc)) { Fail(st, $"{npc} not down/dead"); return; }
                _s.Items.NpcLoot.TakeAll(npc);
                return;
            }
            var obj = Objects(name ?? target).FirstOrDefault();
            if (obj == null) { Fail(st, "no NPC or object " + target); return; }
            var objects = _s.Interaction.Objects;
            if (objects.IsLocked(obj.Id)) objects.Unlock(obj.Id);
            if (objects.IsLocked(obj.Id)) { Fail(st, $"{obj.Name} locked ({objects.CheckLock(obj.Id)})"); return; }
            if (!objects.OpenContainer(obj.Id)) { Fail(st, $"{obj.Name}: not a container"); return; }
            objects.TakeAll(obj.Id);
        }

        /// <summary>Bookstands only: the station path reads them through <see cref="CraftingServices.Read"/>.</summary>
        void Use(ScenarioStep st)
        {
            string target = st.Get("target");
            if (target.StartsWith("item:", StringComparison.OrdinalIgnoreCase))
            {
                // the hero uses up an inventory item (drinks, eats, reads) as from the inventory
                string item = Item(st, target.Substring(5));
                if (item != null && !_s.Items.Use.Use(item)) Fail(st, $"cannot use {item} (have {_s.Items.Inventories.Of(ActorId.Player).Count(item)})");
                return;
            }
            string name = target.StartsWith("obj:", StringComparison.OrdinalIgnoreCase) ? target.Substring(4) : target;
            var obj = Objects(name).FirstOrDefault();
            if (obj == null) { Fail(st, "no object " + name); return; }
            if (obj.Kind == InteractiveKind.Boulder)
            {
                // tilt, lift (strength checks, a story boulder's game event), the first lift's STR raise
                var verdict = _s.Interaction.Boulders.Use(obj.Id);
                if (verdict != BoulderVerdict.Ok) Fail(st, $"{obj.Name}: {verdict} (STR {_s.Hero.Stats.Value("STR")})");
                else Lines.Add($"use {obj.Name}: lifted");
                return;
            }
            var check = _s.Crafting.Crafting.Check(obj.UseType);
            if (check.Rule == null || check.Rule.Page != StationPage.Read) { Fail(st, $"{obj.Name}: not a bookstand or boulder (dry run reads bookstands, lifts boulders)"); return; }
            var reading = _s.Crafting.Read(obj.Id);
            Lines.Add($"use {obj.Name}: read [{string.Join(",", reading.Lines)}]" + (reading.NothingNew ? " (nothing new)" : ""));
        }

        void Pickup(ScenarioStep st)
        {
            string query = st.Get("item");
            float radius = st.Float("radius", 15f);
            // placed world items still lying there, plus items dropped during the game (drop:N)
            var placed = _s.Content.Database.All<WorldItemDef>()
                           .Where(w => w.Position != null && w.Item.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 && _s.Items.World.IsPresent(w.Id))
                           .Select(w => (id: w.Id, item: w.Item, pos: w.Position));
            var dropped = _s.Items.World.AllDropped.Where(x => x.Item != null && x.Item.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                            .Select(x => (id: x.Id, item: x.Item, pos: x.Position));
            var best = placed.Concat(dropped)
                         .OrderBy(w => Sq(w.pos[0] - _hero.X) + Sq(w.pos[1] - _hero.Y) + Sq(w.pos[2] - _hero.Z)).FirstOrDefault();
            if (best.id == null) { Fail(st, "no world item " + query); return; }
            float d = (float)Math.Sqrt(Sq(best.pos[0] - _hero.X) + Sq(best.pos[1] - _hero.Y) + Sq(best.pos[2] - _hero.Z));
            if (d > radius) { Fail(st, FormattableString.Invariant($"nearest {best.item} is {d:0} m away")); return; }
            if (!_s.Items.World.PickUp(best.id)) Fail(st, "pick up refused");
        }

        void Raid(ScenarioStep st)
        {
            string enclave = st.Get("enclave");
            var facts = _s.Crime.Attitudes.Facts;
            if (_s.Enclaves.Of(enclave).Status == EnclaveStatus.Routine) _s.Enclaves.SetStatus(enclave, EnclaveStatus.Attack);
            var defenders = _s.Content.Database.All<NpcDef>()
                              .Where(n => facts.TryGetProfile(n.Id, out var p) && p.Enclave == enclave && _s.Crime.Attitudes.LoyalToEnclave(p))
                              .Where(n => _s.Npcs.TryGet(n.Id, out var a) && _s.Npcs.IsInWorld(a)).Select(n => n.Id).ToList();
            int kills = 0;
            foreach (var id in defenders)
            {
                if (Freed(enclave) || kills >= st.Int("max", 300)) break;
                if (_s.Combat.Vitals.StateOf(id) == VitalState.Dead) continue;
                _s.Combat.Vitals.Kill(id, true);
                kills++;
                Tick(Step);
            }
            Tick(2f);
            string line = $"raid {enclave}: {kills}/{defenders.Count} killed, {_s.Enclaves.Of(enclave).Status}" + (_s.Enclaves.IsSwitched(enclave) ? " (switched)" : "");
            if (!Freed(enclave)) Fail(st, line); else Lines.Add(line);
        }

        bool Freed(string enclave) => _s.Enclaves.Of(enclave).Status == EnclaveStatus.Flee || _s.Enclaves.IsSwitched(enclave);

        // ------------------------------------------------------------------ conditions

        public const string Unknown = "<presentation>";

        public string Evaluate(ScenarioCondition c)
        {
            var s = _s;
            string arg = c.Arg;
            switch (c.Kind)
            {
                case "quest": return s.Quests.StatusOf(new Id<QuestDef>(arg)).ToString();
                case "flag": return s.Flags.IsSet(arg).ToString();
                case "sector": return s.WorldState.Sectors.IsEnabled(arg).ToString();
                case "has": return s.Items.Inventories.Of(ActorId.Player).Count(arg).ToString(Inv);
                case "enclave": return s.Enclaves.Of(arg).Status.ToString();
                case "dialog": return s.Dialog.IsActive.ToString();
                case "partner": return s.Dialog.Current != null ? s.Dialog.Current.Partner.ToString() : "-";
                case "state": return s.Combat.Vitals.StateOf(Actor(arg)).ToString();
                case "party": return s.Party.Members.TypeOf(Actor(arg)).ToString();
                case "waiting": return s.Party.Members.IsWaiting(Actor(arg)).ToString();
                case "arena": return s.Npcs.InArena(Actor(arg))?.ToString() ?? Unknown;
                case "important": return s.Dialog.HasImportantInfo(Actor(arg)).ToString();
                case "given": return World.DialogMemory.WasGiven(new Id<DialogEntryDef>(arg)).ToString();
                case "available":
                    return s.Content.Database.TryGet(new Id<DialogEntryDef>(arg), out DialogEntryDef def)
                        ? Topics().IsAvailable(def, World.ConditionsFor(def.Owner)).ToString() : "-";
                case "offered":
                    return (s.Dialog.Current != null && s.Dialog.Current.Phase == ConversationPhase.AwaitingChoice && s.Dialog.Current.Options.Any(o => o.Entry.Name == arg)).ToString();
                case "inarea": return s.WorldState.Areas.TryIsInside(ActorId.Player, arg, out bool inside) ? inside.ToString() : "-";
                case "present":
                    return s.Npcs.TryGet(Actor(arg), out var agent) ? s.Npcs.IsInWorld(agent).ToString() : Objects(arg).Any().ToString();
                case "talked": return World.Actors.HasTalkedToPlayer(Actor(arg)).ToString();
                case "entry": return s.Dialog.Current?.Entry != null ? s.Dialog.Current.Entry.Id.Name : "-";
                case "guide": return s.Party.Members.Guide != null ? s.Party.Members.Guide.Npc.Name : "-";
                case "chapter": return s.Chapter.Current.ToString(Inv);
                case "hp": return s.Combat.Vitals.HitPoints(Actor(arg)).ToString(Inv);
                case "xp": return s.Hero.Xp.ToString(Inv);
                case "fame": return s.Fame.Of(FameKind.Political, arg).ToString(Inv);
                case "efame": return s.Fame.Of(FameKind.Enclave, arg).ToString(Inv);
                default: return Unknown;
            }
        }
    }
}
#endif
