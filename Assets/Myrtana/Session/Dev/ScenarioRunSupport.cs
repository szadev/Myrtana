#if !UNITY_5_3_OR_NEWER || UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Myrtana.Session.Dev
{
    // fix-54 (r30 tooling gaps): named save slots + resume from a step, NPC-initiated dialogs answered by the runner,
    // loadouts as a first-class run option. Pure parts here (unit-tested); the Unity side is ScenarioRunner.Run.cs.

    /// <summary>
    /// The progress file of one scenario run (<c>Work/scenario-runs/&lt;name&gt;.json</c>): written at every step, on every
    /// checkpoint save and at the end, so a run that died with the editor can be resumed from its last checkpoint
    /// (<c>Tools/scenario.sh resume &lt;name&gt;</c>).
    /// </summary>
    public sealed class ScenarioProgress
    {
        public string Scenario { get; set; }
        /// <summary>running | ok | fail | stopped.</summary>
        public string State { get; set; } = "running";
        /// <summary>1-based number of the step running now (or the last one run).</summary>
        public int Step { get; set; }
        public int Steps { get; set; }
        public string Loadout { get; set; }
        /// <summary>Save slot of the last checkpoint (null = none yet).</summary>
        public string Slot { get; set; }
        /// <summary>1-based step a resume starts at: the step after the checkpoint (0 = no checkpoint).</summary>
        public int ResumeStep { get; set; }
        /// <summary>The scenario's $variables when the checkpoint was saved.</summary>
        public Dictionary<string, string> Vars { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string SavedUtc { get; set; }
        public string UpdatedUtc { get; set; }
        /// <summary>The result line once the run ended.</summary>
        public string Result { get; set; }

        public bool CanResume => !string.IsNullOrEmpty(Slot) && ResumeStep > 0;

        public string ToJson()
        {
            var o = new Dictionary<string, object>
            {
                ["scenario"] = Scenario, ["state"] = State, ["step"] = Step, ["steps"] = Steps, ["loadout"] = Loadout,
                ["slot"] = Slot, ["resumeStep"] = ResumeStep, ["vars"] = Vars ?? new Dictionary<string, string>(),
                ["savedUtc"] = SavedUtc, ["updatedUtc"] = UpdatedUtc, ["result"] = Result,
            };
            return JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>Parses a progress file; null when it is not one.</summary>
        public static ScenarioProgress Parse(string json)
        {
            try
            {
                using (var doc = JsonDocument.Parse(json ?? ""))
                {
                    var r = doc.RootElement;
                    if (r.ValueKind != JsonValueKind.Object) return null;
                    string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    int I(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : 0;
                    var p = new ScenarioProgress
                    {
                        Scenario = S("scenario"), State = S("state") ?? "running", Step = I("step"), Steps = I("steps"),
                        Loadout = S("loadout"), Slot = S("slot"), ResumeStep = I("resumeStep"), SavedUtc = S("savedUtc"),
                        UpdatedUtc = S("updatedUtc"), Result = S("result"),
                    };
                    if (r.TryGetProperty("vars", out var vars) && vars.ValueKind == JsonValueKind.Object)
                        foreach (var kv in vars.EnumerateObject())
                            if (kv.Value.ValueKind == JsonValueKind.String) p.Vars[kv.Name] = kv.Value.GetString();
                    return p.Scenario == null ? null : p;
                }
            }
            catch (JsonException) { return null; }
        }
    }

    /// <summary>Slot names and timing of scenario checkpoints (named saves through the game's own save system).</summary>
    public static class ScenarioCheckpoints
    {
        /// <summary>Minutes between two checkpoints inside one long run (owner: "about every 15 min").</summary>
        public const float DefaultMinutes = 15f;

        static readonly Regex SlotPattern = new Regex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);

        /// <summary>A slot name is a file name: letters, digits, '_' and '-' (1-64).</summary>
        public static bool ValidSlot(string slot) => slot != null && SlotPattern.IsMatch(slot);

        /// <summary>The rolling checkpoint slot of a scenario ("scn_&lt;name&gt;").</summary>
        public static string RunSlot(string scenario) => "scn_" + Clean(scenario);

        /// <summary>The slot saved after the scenario passed ("scn_&lt;name&gt;_ok").</summary>
        public static string PassSlot(string scenario) => "scn_" + Clean(scenario) + "_ok";

        /// <summary>
        /// fix-65 (r32 story suite, R32-6): a save blocked by combat waits up to this long (s) for the fight to end. The
        /// pass saves of chapters 07/08/09 were "blocked after 10 s: inCombat" and lost: the next chapter's fresh Play came.
        /// </summary>
        public const float CombatWaitSeconds = 60f;

        /// <summary>
        /// fix-65: whether a save still waits after <paramref name="waited"/> s while it is blocked by
        /// <paramref name="blockReason"/> (null = not blocked: stop waiting and save): <paramref name="waitSeconds"/> for
        /// any block, at least <paramref name="combatWaitSeconds"/> while the block is combat (<paramref name="combatReason"/>).
        /// </summary>
        public static bool KeepWaiting(string blockReason, float waited, float waitSeconds, float combatWaitSeconds, string combatReason) =>
            blockReason != null && waited < (blockReason == combatReason ? Math.Max(waitSeconds, combatWaitSeconds) : waitSeconds);

        static string Clean(string name)
        {
            var s = Regex.Replace(name ?? "run", "[^A-Za-z0-9_-]", "_");
            return s.Length > 56 ? s.Substring(0, 56) : s;
        }

        /// <summary>
        /// A checkpoint is due between two steps when checkpoints are on (<paramref name="minutes"/> &gt; 0) and that much time
        /// passed since the run started or the last checkpoint.
        /// </summary>
        public static bool Due(float secondsSinceLast, float minutes) => minutes > 0f && secondsSinceLast >= minutes * 60f;

        /// <summary>
        /// The 0-based index a run starts at for <c>--from N</c> (1-based, as the status line counts); an error when N is
        /// outside the scenario.
        /// </summary>
        public static bool TryStartIndex(int from, int steps, out int index, out string error)
        {
            index = 0;
            error = null;
            if (from <= 1) return true;
            if (from > steps) { error = $"--from {from}: the scenario has {steps} steps"; return false; }
            index = from - 1;
            return true;
        }
    }

    /// <summary>
    /// fix-57 (r31: <c>scenario.sh suite</c> polled 300 s, then started the next scenario's fresh Play while hammerclan_04
    /// was at step 31/41): the progress watchdog behind <c>Scenario.Heartbeat</c>. A run makes progress when its step
    /// advances, it writes a log line, or the hero moves <see cref="MoveProgress"/> m (a long escort is one step that walks
    /// for minutes). <c>scenario.sh</c> waits for DONE however long that takes and stops only a run quiet for the stall time.
    /// </summary>
    public sealed class ScenarioWatchdog
    {
        /// <summary>Minutes without progress after which <c>scenario.sh</c> stops a run (<c>--stall MIN</c>).</summary>
        public const float DefaultStallMinutes = 10f;
        /// <summary>The hero moving this far (flat, m) counts as progress.</summary>
        public const float MoveProgress = 2f;

        int _step = -1, _lines = -1;
        float _x, _z, _lastAt;
        bool _started;

        /// <summary>Seconds since the last progress at <paramref name="now"/>.</summary>
        public float QuietSeconds(float now) => Math.Max(0f, now - _lastAt);

        /// <summary>A new run starts: progress now.</summary>
        public void Reset(float now)
        {
            _started = false;
            _step = _lines = -1;
            _lastAt = now;
        }

        /// <summary>Feeds the run's state; true when it made progress since the last call.</summary>
        public bool Observe(float now, int step, int lines, float heroX, float heroZ)
        {
            float dx = heroX - _x, dz = heroZ - _z;
            bool moved = !_started || dx * dx + dz * dz >= MoveProgress * MoveProgress;
            bool progress = !_started || step != _step || lines != _lines || moved;
            _started = true;
            _step = step;
            _lines = lines;
            if (moved) { _x = heroX; _z = heroZ; }
            if (progress) _lastAt = now;
            return progress;
        }

        /// <summary>A running run quiet this long is stalled (<paramref name="stallMinutes"/> &lt;= 0: never).</summary>
        public static bool Stalled(float quietSeconds, float stallMinutes) => stallMinutes > 0f && quietSeconds >= stallMinutes * 60f;

        /// <summary>
        /// The heartbeat line <c>scenario.sh</c> polls: the status as it is when the run is done or idle; for a running one
        /// the status plus "| quiet N s", or "STALLED after N s without progress: status" once <see cref="Stalled"/>.
        /// </summary>
        public static string Heartbeat(string status, float quietSeconds, float stallMinutes)
        {
            status = status ?? "idle";
            if (!status.StartsWith("running", StringComparison.Ordinal)) return status;
            string q = ((int)quietSeconds).ToString(CultureInfo.InvariantCulture);
            return Stalled(quietSeconds, stallMinutes) ? $"STALLED after {q} s without progress: {status}" : $"{status} | quiet {q} s";
        }
    }

    /// <summary>What the runner does with an open dialog now.</summary>
    public enum DialogAct { Wait, CompleteLine, Choose, Close, ForceClose }

    /// <summary>
    /// The runner's answer to a dialog it did not open (an NPC that starts talking to the hero: G3 AssessTalk → ZS_Talk,
    /// gate warnings, Important infos). Owner (r30): "the runner must handle NPCs who start talking to the hero". Lines are
    /// completed; at a menu it takes the entry the scenario expects (<c>answer=</c> / <c>"answers"</c>), else the first
    /// topic (non-END) it has not taken yet, at most <see cref="MaxTopics"/> of them, else END; a dialog still open after
    /// <see cref="ForceCloseSeconds"/> is closed with a BUG line.
    /// </summary>
    public static class DialogAutoAnswer
    {
        /// <summary>A dialog the scenario did not open is left alone this long (a following 'wait until=dialog' takes it over).</summary>
        public const float GraceSeconds = 2f;
        /// <summary>Open longer than this (foreign) or this long without progress (the scenario's own, outside a dialog op): closed.</summary>
        public const float ForceCloseSeconds = 30f;
        /// <summary>Topics taken in one foreign dialog before the runner chooses END (a trader's menu never runs out).</summary>
        public const int MaxTopics = 3;

        /// <summary>Ops that drive the dialog themselves: the handler does not act while one runs.</summary>
        public static readonly string[] DialogOps = { "talk", "choose", "lines", "abort", "ask", "tp", "rob", "dialogstate" };

        /// <summary>Condition kinds that read the open dialog: a step that evaluates one takes the dialog over.</summary>
        public static readonly string[] DialogConditions = { "dialog", "partner", "entry", "options", "offered", "label", "cost" };

        public static bool IsDialogOp(string op) => op != null && Array.IndexOf(DialogOps, op.ToLowerInvariant()) >= 0;

        public static bool IsDialogCondition(string kind) => kind != null && Array.IndexOf(DialogConditions, kind.ToLowerInvariant()) >= 0;

        /// <summary>
        /// The next act for a foreign dialog in <paramref name="phase"/> ("AwaitingLine", "AwaitingChoice", "Running",
        /// "AwaitingTime") open <paramref name="openSeconds"/>.
        /// </summary>
        public static DialogAct Next(string phase, float openSeconds)
        {
            if (openSeconds >= ForceCloseSeconds) return DialogAct.ForceClose;
            if (openSeconds < GraceSeconds) return DialogAct.Wait;
            switch (phase)
            {
                case "AwaitingLine": return DialogAct.CompleteLine;
                case "AwaitingChoice": return DialogAct.Choose;
                default: return DialogAct.Wait;
            }
        }

        /// <summary>
        /// The option to choose (index into <paramref name="options"/>, -1 = none: abort) and why, one policy for every talk the
        /// scenario did not open (the per-frame handler and the walkto/escort/draw answer of fix-53):
        /// "expected" (the first of <paramref name="expected"/> on offer), "guard" (fix-53 <see cref="UnaskedTalk"/>: the
        /// answer that switches a gate guard off without a fight — Sivert's "Friend!", Garik's "Why are you stopping me?"),
        /// "topic" (the first non-END option not in <paramref name="taken"/> that starts no fight, while fewer than
        /// <see cref="MaxTopics"/> were taken), "END", else "only" (the first option that starts no fight); -1 when every
        /// option starts a fight.
        /// </summary>
        public static int Pick(IReadOnlyList<(string entry, TalkChoice choice)> options, IEnumerable<string> expected,
                               ICollection<string> taken, out string why)
        {
            why = "none";
            if (options == null || options.Count == 0) return -1;
            foreach (var e in expected ?? Enumerable.Empty<string>())
            {
                int i = IndexOf(options, o => string.Equals(o.entry, e, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) { why = "expected"; return i; }
            }
            int g = UnaskedTalk.Pick(options.Select(o => o.choice).ToList());
            if (g >= 0 && !options[g].choice.IsExit && (taken == null || !taken.Contains(options[g].entry))) { why = "guard"; return g; }
            int topics = taken?.Count ?? 0;
            if (topics < MaxTopics)
            {
                int i = IndexOf(options, o => !o.choice.IsExit && !o.choice.StartsFight && (taken == null || !taken.Contains(o.entry)));
                if (i >= 0) { why = "topic"; return i; }
            }
            int x = IndexOf(options, o => o.choice.IsExit);
            if (x >= 0) { why = "END"; return x; }
            int only = IndexOf(options, o => !o.choice.StartsFight);
            why = only >= 0 ? "only" : "every option starts a fight";
            return only;
        }

        /// <summary>The same for options known only by entry and END flag (no guard or fight information).</summary>
        public static int Pick(IReadOnlyList<(string entry, bool exit)> options, IEnumerable<string> expected,
                               ICollection<string> taken, out string why) =>
            Pick(options?.Select(o => (o.entry, new TalkChoice(o.exit, false, false))).ToList(), expected, taken, out why);

        /// <summary>Choices in one talk the runner did not open before it aborts it (topics, a guard's answers, END).</summary>
        public const int MaxChoices = MaxTopics + UnaskedTalk.MaxChoices;

        static int IndexOf(IReadOnlyList<(string entry, TalkChoice choice)> list, Func<(string entry, TalkChoice choice), bool> match)
        {
            for (int i = 0; i < list.Count; i++) if (match(list[i])) return i;
            return -1;
        }

        /// <summary>
        /// Whether a scenario answers NPC dialogs automatically by default: not when it tests whether NPCs start talking
        /// (a <c>forget</c> step replays an Important info, an <c>expect !dialog</c> checks that none started), where the
        /// handler would hide the very talk it checks. <c>"autodialog"</c> in the file and the <c>autodialog</c> op override.
        /// </summary>
        public static bool DefaultOn(IEnumerable<ScenarioStep> steps)
        {
            foreach (var st in steps ?? Enumerable.Empty<ScenarioStep>())
            {
                if (st.Op == "forget") return false;
                if (st.Op == "expect" && ScenarioCondition.TryParse(st.Get("cond"), out var c, out _) && c.Kind == "dialog" && c.Value == "false") return false;
            }
            return true;
        }
    }

    /// <summary>How the fight op attacks with a loadout.</summary>
    public enum AttackStyle { Melee, Fists, Bow, Crossbow, Spell }

    /// <summary>
    /// A weapon set for a scenario run (<c>Tools/scenario.sh run &lt;name&gt; --loadout &lt;name&gt;</c>, the <c>loadout</c> op):
    /// the items with the attributes and G3 perks they need (CP 1.75 items.json requirements; <c>attr ... atleast=1</c> never
    /// lowers a stat), and the attack style the fight op uses. Replaces runner-30's loop2.sh/run2.sh rotation.
    /// </summary>
    public sealed class ScenarioLoadout
    {
        public ScenarioLoadout(string name, string label, AttackStyle style, string ops, string spell = null, string[] quickItems = null)
        {
            Name = name; Label = label; Style = style; Ops = ops; Spell = spell; QuickItems = quickItems ?? Array.Empty<string>();
        }

        public string Name { get; }
        public string Label { get; }
        public AttackStyle Style { get; }
        /// <summary>Scenario statements that grant and equip the set.</summary>
        public string Ops { get; }
        /// <summary>The spell item the fight op casts (spells loadout).</summary>
        public string Spell { get; }
        /// <summary>
        /// fix-57: items besides the worn weapons that go on the quick bar (the spells loadout's learned spells: G3 readies a
        /// spell from its quick slot, OnPlayerUseQuickSlot; OnPlayerAssignStackKey needs HasLearnedSpell).
        /// </summary>
        public IReadOnlyList<string> QuickItems { get; }

        /// <summary>
        /// fix-57: the <c>"loadout"</c> value of a scenario file that pins "no loadout": the scenario sets up its own weapons
        /// (draw_aim_strafe, drawn_gaits, dual_wield …) and the suite's rotation is ignored. "none" means the same.
        /// </summary>
        public const string Own = "own";

        public static bool IsOwn(string value) =>
            string.Equals(value?.Trim(), Own, StringComparison.OrdinalIgnoreCase) || string.Equals(value?.Trim(), "none", StringComparison.OrdinalIgnoreCase);

        /// <summary>A scenario file's loadout field is valid: a loadout name, alias or index, or <see cref="Own"/>.</summary>
        public static bool ValidFileValue(string value) => IsOwn(value) || Find(value) != null;

        /// <summary>
        /// fix-57 (r31 draw_aim_strafe [fists]): the loadout a run uses. A scenario file that names one pins it (it tests
        /// those weapons, or sets them up itself with <c>"loadout": "own"</c>) and wins over the requested one
        /// (<c>--loadout</c>, the suite's rotation); otherwise the requested one. Null = none. <paramref name="note"/> says
        /// why a request was ignored (null when nothing was).
        /// </summary>
        public static string Choose(string fileLoadout, string requested, out string note)
        {
            note = null;
            bool asked = !string.IsNullOrWhiteSpace(requested);
            if (string.IsNullOrWhiteSpace(fileLoadout)) return asked ? requested : null;
            bool own = IsOwn(fileLoadout);
            if (asked && (own || Find(requested) != Find(fileLoadout)))
                note = $"loadout {requested} ignored: the scenario pins " + (own ? "its own weapons" : "loadout " + fileLoadout);
            return own ? null : fileLoadout;
        }

        /// <summary>
        /// fix-65 (r32 story suite, R32-5): a scenario's own <c>equip</c> of a weapon, shield or ammunition does not replace
        /// the weapons of a loadout the run was asked for (<c>--loadout</c>, the suite's rotation) when the scenario file pins
        /// none: chapters 04/05/14/15 equipped It_1H_Sword_01 at step 1 over the rotation's set. The loadout's own ops and a
        /// pinned or op-set loadout equip as written.
        /// </summary>
        /// <param name="requestedLoadout">The run's loadout came from the request, not from the scenario file.</param>
        /// <param name="fromLoadoutOps">The equip is one of a loadout's own statements (or the <c>loadout</c> op).</param>
        /// <param name="weaponSlot">The item goes to a melee, ranged, shield or ammunition slot.</param>
        public static bool KeepsRotationWeapons(bool requestedLoadout, bool fromLoadoutOps, bool weaponSlot) =>
            requestedLoadout && !fromLoadoutOps && weaponSlot;

        /// <summary>The rotation order (runner-30 loop2.sh).</summary>
        public static readonly IReadOnlyList<ScenarioLoadout> All = new[]
        {
            new ScenarioLoadout("1H", "one-handed sword", AttackStyle.Melee,
                "attr STR 100 atleast=1; learn Perk_1H_1; equip It_1H_Sword_01"),
            new ScenarioLoadout("shield", "one-handed sword + shield", AttackStyle.Melee,
                "attr STR 100 atleast=1; learn Perk_1H_1,Perk_Shield_1; equip It_1H_Sword_01; equip It_Shield_Round_01"),
            new ScenarioLoadout("dual", "two one-handed swords (1H_1H)", AttackStyle.Melee,
                "attr STR 140 atleast=1; learn Perk_1H_1,Perk_1H1H_1; equip It_1H_Sword_01; equip It_1H_AssSword_01"),
            new ScenarioLoadout("2H", "two-handed sword", AttackStyle.Melee,
                "attr STR 150 atleast=1; learn Perk_Axe_1; equip It_2H_Sword_01"),
            new ScenarioLoadout("axe", "battle axe", AttackStyle.Melee,
                "attr STR 200 atleast=1; learn Perk_Axe_1,Perk_Axe_2; equip It_Axe_BattleAxe_01"),
            new ScenarioLoadout("staff", "quarterstaff", AttackStyle.Melee,
                "learn Perk_Staff_1; equip It_Staff_Quarterstaff_01"),
            new ScenarioLoadout("bow", "bow + arrows", AttackStyle.Bow,
                "attr DEX 70 atleast=1; learn Perk_Bow_1; give Arrow 100; equip It_Bow_01; equip Arrow"),
            new ScenarioLoadout("crossbow", "crossbow + bolts", AttackStyle.Crossbow,
                "attr STR 100 atleast=1; learn Perk_Crossbow_1; give Bolt 100; equip It_Crossbow_01; equip Bolt"),
            // fix-57 (r31 melee_small: "spell It_Spell_Fireball not readied"): learned as G3 teaches them (TeachingService: INT
            // 30/50/60 and 5 LP each, no circles in G3), MP for casting, on the quick bar; the fight op readies the spell with
            // its quick-slot key and casts with the cast button
            new ScenarioLoadout("spells", "fireball, ice lance, lightning bolt", AttackStyle.Spell,
                "attr INT 60 atleast=1; attr MP 200 atleast=1; teach It_Spell_Icelance; teach It_Spell_Lightningbolt; teach It_Spell_Fireball",
                "It_Spell_Fireball", new[] { "It_Spell_Fireball", "It_Spell_Icelance", "It_Spell_Lightningbolt" }),
            new ScenarioLoadout("fists", "fists (no melee weapon)", AttackStyle.Fists, "draw fist; sheathe"),
        };

        static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["1h"] = "1H", ["sword"] = "1H", ["1h+shield"] = "shield", ["1h1h"] = "dual", ["dual1h"] = "dual",
            ["2h"] = "2H", ["2hsword"] = "2H", ["bows"] = "bow", ["xbow"] = "crossbow", ["magic"] = "spells", ["spell"] = "spells",
            ["fist"] = "fists", ["none"] = "fists", ["quarterstaff"] = "staff",
        };

        /// <summary>A loadout by name, alias or rotation index ("0".."9"; larger numbers wrap); null when unknown.</summary>
        public static ScenarioLoadout Find(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            name = name.Trim();
            if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) && i >= 0) return All[i % All.Count];
            if (Aliases.TryGetValue(name, out var real)) name = real;
            return All.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public static string Names => string.Join("|", All.Select(l => l.Name));
    }
}
#endif
