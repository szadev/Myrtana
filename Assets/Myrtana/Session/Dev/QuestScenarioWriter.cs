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
    /// <summary>One quest's part of an editor scenario: its steps, the editor time they take, and how it ends.</summary>
    public sealed class QuestScenarioBlock
    {
        public QuestScenarioBlock(string quest, string region, string note)
        {
            Quest = quest;
            Region = region;
            Note = note;
        }

        public string Quest { get; }
        public string Region { get; }
        /// <summary>Why the quest needs the editor (the static walk's unprovable conditions).</summary>
        public string Note { get; }
        public List<string> Steps { get; } = new List<string>();
        /// <summary>Estimated editor seconds (teleports, talks, walks, fights).</summary>
        public float Seconds { get; set; }
        /// <summary>The quest's status at the end of the headless run (Success/Won), or null when it failed.</summary>
        public string EndStatus { get; set; }
        /// <summary>Why the headless run failed (null: passed).</summary>
        public string Failure { get; set; }
        public bool Liberates { get; set; }
        public int Escorts { get; set; }
        public int Fights { get; set; }
    }

    /// <summary>
    /// Editor scenarios for the quests a static walk cannot prove (Docs/story-walk.md §5, class c): escort/guide arrivals,
    /// enclave liberations and gold/fame pre-checks. Starts from the headless play plan (<see cref="QuestDryPlan"/>) and
    /// plays it on a fresh session (<see cref="ChapterDryRun"/>), watching each step: an NPC that is moved next to a place
    /// while it follows or guides the hero becomes an <c>escort</c> (the hero walks there with it); an enclave raid becomes
    /// the fight itself (the first defenders in melee with <c>fight</c>, the rest killed by the hero with <c>kill</c>, as
    /// many as the liberation took headless); gold and fame of pre-checks stay setup (<c>give</c>, <c>fame</c>). An
    /// <c>expect</c> follows every talk, purse and quest end.
    /// </summary>
    public sealed class QuestScenarioWriter
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly Regex AskRx = new Regex(@"^ask (\S+) (\S+)", RegexOptions.Compiled);
        /// <summary>Defenders the hero fights in melee before he kills the rest.</summary>
        public const int MeleeDefenders = 3;
        public const float RunSpeed = 5.5f, GuideSpeed = 4f, PathFactor = 1.35f;

        readonly GameContent _content;
        readonly QuestDryPlan _plan;
        readonly Dictionary<string, QuestDef> _quests;

        public QuestScenarioWriter(GameContent content, StoryWalk walk)
        {
            _content = content ?? throw new ArgumentNullException(nameof(content));
            _plan = new QuestDryPlan(content.Database, walk);
            _quests = content.Database.All<QuestDef>().ToDictionary(q => q.Id.Name, StringComparer.Ordinal);
        }

        GameSession NewSession() =>
            GameSession.NewGame(_content, new SessionPorts(new ChapterDryRun.SilentPresenter(), null), new GameSettings(new LanguageSettings("English", "English"), randomSeed: 1));

        /// <summary>The scenario block of one quest (<see cref="QuestScenarioBlock.Failure"/> set when the headless run fails).</summary>
        public QuestScenarioBlock Build(string quest, string region, string note)
        {
            var block = new QuestScenarioBlock(quest, region, note);
            if (!_quests.TryGetValue(quest, out var q)) { block.Failure = "no quest"; return block; }
            var plan = _plan.Steps(q, out string skip);
            if (plan == null) { block.Failure = "no plan: " + skip; return block; }

            // play the plan headless and note what each step really is
            var session = NewSession();
            var run = new ChapterDryRun(session);
            var spec = ScenarioParser.ParseInline(string.Join("\n", plan), quest);
            if (spec.Steps.Count != plan.Count) { block.Failure = "plan does not parse: " + string.Join("; ", spec.Errors); return block; }
            var escorts = new Dictionary<int, (EscortMode mode, float metres)>();
            var raids = new Dictionary<int, (string enclave, List<ActorId> defenders)>();
            int index = -1;
            WorldPose heroBefore = run.HeroPose;
            run.BeforeStep = st =>
            {
                index++;
                if (st.Op == "bring" && st.Has("near"))
                {
                    var id = new ActorId(st.Get("npc"));
                    var mode = session.Party.Escorts.ModeOf(id);
                    if ((mode == EscortMode.Follow || mode == EscortMode.Guide) && session.Npcs.TryGet(id, out var agent) && run.TryPose(st.Get("near"), out var to))
                    {
                        var from = session.Npcs.PoseOf(agent);
                        escorts[index] = (mode, Flat(from, to));
                    }
                }
                else if (st.Op == "raid")
                {
                    string enclave = st.Get("enclave");
                    var facts = session.Crime.Attitudes.Facts;
                    var defenders = session.Content.Database.All<NpcDef>()
                                           .Where(n => facts.TryGetProfile(n.Id, out var p) && p.Enclave == enclave && session.Crime.Attitudes.LoyalToEnclave(p))
                                           .Where(n => session.Npcs.TryGet(n.Id, out var a) && session.Npcs.IsInWorld(a) && session.Combat.Vitals.StateOf(n.Id) != VitalState.Dead)
                                           .Select(n => n.Id).ToList();
                    raids[index] = (enclave, defenders);
                }
            };
            try { run.Run(spec); }
            catch (Exception e) { block.Failure = "exception " + e.GetType().Name + ": " + e.Message; }
            var status = session.Quests.StatusOf(q.Id);
            if (block.Failure == null && status != QuestStatus.Success && status != QuestStatus.Won)
                block.Failure = run.Failures.FirstOrDefault() ?? "ended " + status;
            block.EndStatus = status.ToString();

            // the scenario: the plan with the real paths
            var steps = block.Steps;
            steps.Add($"log -- {quest} ({region}): {note.Replace(';', ',')}"); // ';' would split the step
            float seconds = 0f;
            var hero = heroBefore;
            for (int i = 0; i < spec.Steps.Count; i++)
            {
                var st = spec.Steps[i];
                string text = plan[i];
                if (escorts.TryGetValue(i, out var esc))
                {
                    string npc = st.Get("npc"), to = st.Get("near");
                    float pathMetres = esc.metres * PathFactor;
                    float est = pathMetres / (esc.mode == EscortMode.Guide ? GuideSpeed : RunSpeed) + 10f;
                    int limit = EscortTimeout(pathMetres, esc.mode);
                    // the plan froze the NPC for its bring; the escort replaces the bring and must not keep it frozen
                    // (editor-runner-23: frozen followers never move, a frozen guide whose job had not started stays Idle)
                    var frozen = ActiveFreeze(steps, out int freezeAt);
                    if (frozen != null && frozen.Contains(npc))
                    {
                        var others = frozen.Where(f => f != npc).ToList();
                        string rest = others.Count > 0 ? "freeze only=" + string.Join(",", others) : null;
                        if (steps.Skip(freezeAt + 1).All(x => x.StartsWith("log ", StringComparison.Ordinal)))
                        {
                            // the freeze was only for this bring: it covers the others alone
                            steps.RemoveAt(freezeAt);
                            if (rest != null) steps.Insert(freezeAt, rest);
                        }
                        else
                        {
                            steps.Add("unfreeze");
                            if (rest != null) steps.Add(rest);
                        }
                    }
                    // the escort starts where the NPC stands: the hero goes there first (editor-runner-24 benerai_01: the lead
                    // began 375 m from the follower, which was 8 m from its target)
                    steps.Add($"tp npc:{npc} dist=2");
                    steps.Add(FormattableString.Invariant($"log -- {(esc.mode == EscortMode.Guide ? "follow the guide" : "lead")} {npc} to {to} ({esc.metres:0} m straight, ~{pathMetres:0} m path)"));
                    steps.Add($"escort {npc} to={to} near=5 secs={limit.ToString(Inv)}");
                    steps.Add($"expect dist:{npc}<=12");
                    // the arrival talk (an Important info the NPC starts by itself) must end before the next tp: a tp aborts
                    // an open dialog, and an aborted info is remembered as given without its commands (editor-runner-18)
                    steps.Add("wait until=dialog timeout=5 soft=1");
                    steps.Add("lines max=60");
                    // the arrival entry ("Cruz near Cruz_SandcrawlerAmbush 5 m") needs the NPC back on the spot: a guide that
                    // engages an ambush right after arriving fights it 10+ m away and walks back afterwards (fix-38, r27
                    // morasul_01: the ask came while Cruz fought a sandcrawler 13 m from the place)
                    string arrival = ArrivalEntry(spec.Steps, i, npc);
                    if (arrival != null) steps.Add($"wait until=available:{arrival} timeout=60 soft=1");
                    seconds += est + 6f;
                    block.Escorts++;
                    continue;
                }
                if (raids.TryGetValue(i, out var raid))
                {
                    var killed = raid.defenders.Where(d => session.Combat.Vitals.StateOf(d) == VitalState.Dead).ToList();
                    // several enclaves in one quest (a clan's wipe-out): one melee fight each keeps the file short
                    int melee = Math.Min(raids.Count > 2 ? 1 : MeleeDefenders, killed.Count);
                    steps.Add($"log -- liberate {raid.enclave}: {killed.Count} of {raid.defenders.Count} defenders (the first {melee} in melee)");
                    for (int k = 0; k < killed.Count; k++)
                    {
                        string d = killed[k].Name;
                        if (k < melee)
                        {
                            steps.Add($"tp npc:{d} dist=3");
                            steps.Add($"fight {d} hp=5 secs=25");
                            steps.Add($"kill {d} hero");
                            seconds += 30f;
                            block.Fights++;
                            if (k == 0) steps.Add($"enclave {raid.enclave} Attack");
                        }
                        else
                        {
                            steps.Add($"kill {d} hero");
                            seconds += 0.3f;
                        }
                    }
                    steps.Add("wait 3");
                    steps.Add($"expect state:{(killed.Count > 0 ? killed[killed.Count - 1].Name : "hero")}=Dead");
                    if (melee > 0) steps.Add("sheathe"); // a drawn weapon makes the next NPCs react to it (Wilson fought the hero)
                    seconds += 3f;
                    block.Liberates = true;
                    continue;
                }
                steps.Add(text);
                seconds += Cost(st, run, ref hero);
                if (st.Op == "fight") steps.Add("sheathe"); // arena fights: the next NPC would react to the drawn weapon
                var ask = AskRx.Match(text);
                // repeatable entries keep no one-shot memory: nothing to expect of them
                if (ask.Success && !(_content.Database.TryGet(new Id<DialogEntryDef>(ask.Groups[2].Value), out DialogEntryDef entry) && entry.Repeatable))
                    steps.Add($"expect given:{ask.Groups[2].Value}");
                else if (st.Op == "give" && st.Get("item") == "It_Gold") steps.Add($"expect has:It_Gold>={st.Get("n", "1")}");
            }
            GuardKillQuest(q, steps);
            steps.Add($"expect quest:{quest}={(status == QuestStatus.Won ? "Won" : "Success")}");
            if (block.Failure == null) RewardChecks(q, session, steps);
            block.Seconds = seconds;
            return block;
        }

        /// <summary>
        /// The quest's rewards as lower bounds of what the headless run reached (XP and fame only grow in a scenario: kills,
        /// other quests and pre-check fame add to them, so the editor run may be higher, never lower): <c>xp</c>, political
        /// <c>fame:Group</c>, enclave <c>efame:Group</c>.
        /// </summary>
        static void RewardChecks(QuestDef q, GameSession session, List<string> steps)
        {
            if (q.Rewards.Xp > 0) steps.Add($"expect xp>={session.Hero.Xp.ToString(Inv)}");
            foreach (var f in q.Rewards.Fame.Where(f => f.Amount > 0 && !string.IsNullOrEmpty(f.Group)).GroupBy(f => (f.Kind, f.Group)).Select(g => g.First()))
            {
                int have = session.Fame.Of(f.Kind, f.Group);
                if (have > 0) steps.Add($"expect {(f.Kind == FameKind.Enclave ? "efame" : "fame")}:{f.Group}>={have.ToString(Inv)}");
            }
        }

        /// <summary>
        /// A kill quest counts its targets while still Open (G3): when an earlier block's escort defends killed them all
        /// (editor-runner-25 okara_02: Randall's guide defends killed the 9 Okara crawlers, Randall_KillMinecrawler was
        /// Success before its activator BPANKRATZ31133, whose "quest Open" condition was then false), the block's steps
        /// up to its last kill are skipped (<c>unless=quest:Q=Success</c>); the steps after the kills (a reward talk) stay.
        /// </summary>
        static void GuardKillQuest(QuestDef q, List<string> steps)
        {
            var kind = q.Objective.Kind;
            if (kind != QuestObjectiveKind.Kill && kind != QuestObjectiveKind.Defeat && kind != QuestObjectiveKind.DriveAway) return;
            int last = steps.FindLastIndex(x => x.StartsWith("kill ", StringComparison.Ordinal) || x.StartsWith("ko ", StringComparison.Ordinal) ||
                                                x.StartsWith("fight ", StringComparison.Ordinal));
            string guard = $" {OpSpec.Unless}=quest:{q.Id.Name}=Success";
            for (int i = 0; i <= last; i++)
                if (!steps[i].StartsWith("log ", StringComparison.Ordinal) && !steps[i].Contains(guard)) steps[i] += guard;
        }

        /// <summary>
        /// The <c>escort secs=</c> limit for an estimated path length (straight distance × <see cref="PathFactor"/>): 90 s
        /// (stuck re-plans, detours, a fight on the way) plus 2.5 × the walk at the guide's / hero's speed, at least 180 s,
        /// rounded up to 30 s.
        /// </summary>
        public static int EscortTimeout(float pathMetres, EscortMode mode)
        {
            float walk = pathMetres / (mode == EscortMode.Guide ? GuideSpeed : RunSpeed);
            return (int)Math.Ceiling(Math.Max(180f, 90f + 2.5f * walk) / 30f) * 30;
        }

        /// <summary>Editor seconds of one plain step; tracks where teleports put the hero.</summary>
        /// <summary>
        /// The entry the plan asks <paramref name="npc"/> for right after the escort at <paramref name="escort"/> (only
        /// tp/log steps between), or null.
        /// </summary>
        public static string ArrivalEntry(IReadOnlyList<ScenarioStep> steps, int escort, string npc)
        {
            for (int j = escort + 1; j < steps.Count; j++)
            {
                var st = steps[j];
                if (st.Op == "tp" || st.Op == "log") continue;
                if (st.Op == "ask" && string.Equals(st.Get("npc"), npc, StringComparison.Ordinal)) return st.Get("entry");
                return null;
            }
            return null;
        }

        static float Cost(ScenarioStep st, ChapterDryRun run, ref WorldPose hero)
        {
            switch (st.Op)
            {
                case "tp":
                {
                    string target = st.Get("target");
                    float far = target != null && run.TryPose(target.StartsWith("npc:", StringComparison.OrdinalIgnoreCase) ? target.Substring(4) : target, out var p) ? Flat(hero, p) : 500f;
                    if (target != null && run.TryPose(target.StartsWith("npc:", StringComparison.OrdinalIgnoreCase) ? target.Substring(4) : target, out var p2)) hero = p2;
                    return far > 120f ? 12f : 2.5f; // streaming in, ground snap, 0.7 s settle
                }
                case "ask": return 5f;
                case "bring": return 3f;
                case "fight": return 20f;
                case "use": return 12f;
                case "loot": return 8f;
                case "wait": return st.Float("secs", 1f);
                case "kill":
                case "ko": return 0.5f;
                default: return 0.2f;
            }
        }

        static float Flat(WorldPose a, WorldPose b) => (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

        /// <summary>
        /// Plays the blocks one after the other on one fresh session (as one scenario file runs): the index of the first block
        /// that fails there (-1: all pass) and why.
        /// </summary>
        /// <summary>The NPCs the last <c>freeze only=</c> holds when no <c>unfreeze</c> followed it (null: none frozen).</summary>
        static List<string> ActiveFreeze(List<string> steps, out int freeze)
        {
            freeze = steps.FindLastIndex(x => x.StartsWith("freeze only=", StringComparison.Ordinal));
            if (freeze < 0 || freeze < steps.FindLastIndex(x => x == "unfreeze")) return null;
            return steps[freeze].Substring("freeze only=".Length).Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        }

        public int Check(IReadOnlyList<QuestScenarioBlock> blocks, IEnumerable<string> header, out string failure)
        {
            failure = null;
            var session = NewSession();
            var run = new ChapterDryRun(session);
            if (!run.Run(ScenarioParser.ParseInline(string.Join("\n", header), "header"))) { failure = run.Failures.FirstOrDefault(); return 0; }
            for (int i = 0; i < blocks.Count; i++)
            {
                if (!run.Run(ScenarioParser.ParseInline(string.Join("\n", blocks[i].Steps), blocks[i].Quest)))
                {
                    failure = run.Failures.FirstOrDefault();
                    return i;
                }
            }
            return -1;
        }
    }
}
#endif
