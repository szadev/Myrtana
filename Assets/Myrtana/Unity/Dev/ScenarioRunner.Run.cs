#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G3.Unity;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Session.Dev;
using Myrtana.Story;
using UnityEngine;

namespace Myrtana.Unity.Dev
{
    /// <summary>How a run starts (fix-54): at a step, with variables, after loading a slot, with a loadout, checkpoints.</summary>
    public sealed class RunOptions
    {
        /// <summary>0-based index of the first step.</summary>
        public int StartIndex;
        /// <summary>$variables the skipped steps would have set (from the progress file).</summary>
        public Dictionary<string, string> Vars;
        /// <summary>A save slot loaded before the first step (resume).</summary>
        public string LoadSlot;
        /// <summary>Loadout applied before the first step (on a resume only its attack style: the items are in the save).</summary>
        public string Loadout;
        /// <summary>Checkpoint minutes; null = the scenario's own ("checkpoint" in the file).</summary>
        public float? CheckpointMinutes;
        /// <summary>The run continues an earlier one from its checkpoint.</summary>
        public bool Resumed;
        /// <summary>Write the progress file Work/scenario-runs/&lt;name&gt;.json (named runs).</summary>
        public bool Track;
    }

    /// <summary>
    /// fix-54 (r30 tooling gaps): checkpoints (named save slots, resume from a step, a progress file per run), the
    /// handler for dialogs the scenario did not open (NPCs that start talking), loadouts and their attack style.
    /// </summary>
    public sealed partial class ScenarioRunner
    {
        RunOptions _opt = new RunOptions();
        ScenarioProgress _progress;
        float _checkpointMinutes;
        float _lastCheckpoint; // realtime
        int _includeDepth;     // > 0 inside a 'scenario' include
        string _currentOp;     // the op running now (dialog handler)
        string _stepAnswers;   // answer= of the running step

        // ---------------------------------------------------------------- progress file + checkpoints

        internal static string ProgressDir => Path.Combine(Scenario.ProjectRoot, "Work", "scenario-runs");

        internal static string ProgressPath(string scenario) => Path.Combine(ProgressDir, scenario + ".json");

        internal static ScenarioProgress ReadProgress(string scenario)
        {
            try
            {
                var path = ProgressPath(scenario);
                return File.Exists(path) ? ScenarioProgress.Parse(File.ReadAllText(path)) : null;
            }
            catch (IOException) { return null; }
        }

        void WriteProgress()
        {
            if (_progress == null) return;
            _progress.UpdatedUtc = DateTime.UtcNow.ToString("u");
            try
            {
                Directory.CreateDirectory(ProgressDir);
                var path = ProgressPath(_progress.Scenario);
                File.WriteAllText(path + ".tmp", _progress.ToJson());
                if (File.Exists(path)) File.Delete(path);
                File.Move(path + ".tmp", path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning(Scenario.Tag + "progress file: " + e.Message);
            }
        }

        void StartProgress(ScenarioSpec spec)
        {
            _progress = null;
            if (!_opt.Track) return;
            var before = ReadProgress(spec.Name);
            _progress = new ScenarioProgress { Scenario = spec.Name, Steps = spec.Steps.Count, Loadout = _opt.Loadout };
            // a resumed or --from run keeps the last checkpoint until it saves a new one (a crash before that resumes there again)
            if (before != null && (_opt.Resumed || _opt.StartIndex > 0) && before.CanResume)
            {
                _progress.Slot = before.Slot;
                _progress.ResumeStep = before.ResumeStep;
                _progress.SavedUtc = before.SavedUtc;
                _progress.Vars = before.Vars;
            }
            WriteProgress();
        }

        /// <summary>Dev state a resume puts back (it is not in the save): god mode, calmed and frozen NPCs, the attack style.</summary>
        Dictionary<string, string> CheckpointVars()
        {
            var v = new Dictionary<string, string>(_vars, StringComparer.OrdinalIgnoreCase)
            {
                ["@god"] = _god ? "1" : "0",
                ["@calm"] = string.Join(",", _calm.Except(_frozen).Select(a => a.Name)),
                ["@frozen"] = string.Join(",", _frozen.Select(a => a.Name)),
            };
            return v;
        }

        void RestoreDevState(Dictionary<string, string> vars)
        {
            if (vars == null) return;
            foreach (var kv in vars) if (!kv.Key.StartsWith("@")) _vars[kv.Key] = kv.Value;
            if (!_opt.Resumed) { Log($"--from: vars of the last checkpoint: {string.Join(",", _vars.Keys)}"); return; }
            if (vars.TryGetValue("@god", out var g) && g == "1") _god = true;
            List<ActorId> Ids(string key) => vars.TryGetValue(key, out var list) && !string.IsNullOrEmpty(list)
                ? list.Split(',').Select(x => new ActorId(x)).ToList() : new List<ActorId>();
            var calm = Ids("@calm");
            var frozen = Ids("@frozen");
            if (calm.Count > 0) SetCalm(calm, false);
            if (frozen.Count > 0) SetCalm(frozen, true);
            Log($"resume: dev state god={_god} calm={calm.Count} frozen={frozen.Count}, vars {string.Join(",", _vars.Keys)}");
        }

        SaveGameController Saves => _boot != null ? _boot.GetComponent<SaveGameController>() : null;

        /// <summary>
        /// Saves <paramref name="slot"/> (null = QuickSave) through the game's save system, waiting up to
        /// <paramref name="waitSeconds"/> while saving is blocked. <paramref name="resumeStep"/> (1-based, 0 = none) is recorded in
        /// the progress file as the step a resume starts at. Soft: a blocked save is only logged.
        /// </summary>
        IEnumerator SaveSlot(string slot, float waitSeconds, int resumeStep, string why, bool soft, float combatWaitSeconds = 0f)
        {
            _lastSaveOk = false;
            var saves = Saves;
            if (saves == null) { Fail("save: no SaveGameController"); yield break; }
            if (slot != null && !ScenarioCheckpoints.ValidSlot(slot)) { Fail($"save: bad slot name '{slot}' (letters, digits, _ -)"); yield break; }
            float t0 = Time.realtimeSinceStartup;
            bool loggedWait = false;
            // fix-65 (R32-6): a combat block may wait longer (combatWaitSeconds) for the fight to end
            while (ScenarioCheckpoints.KeepWaiting(_session.SaveBlockReason, Time.realtimeSinceStartup - t0, waitSeconds, combatWaitSeconds, CombatWiring.BlockedByCombat))
            {
                if (!loggedWait && Time.realtimeSinceStartup - t0 >= waitSeconds)
                {
                    loggedWait = true;
                    Log($"save {slot ?? "quick"} ({why}): blocked by {_session.SaveBlockReason} for {waitSeconds:0} s - waits up to {combatWaitSeconds:0} s for the fight to end" +
                        $" (attacking the hero: {AttackersOf(ActorId.Player)})");
                }
                yield return new WaitForSecondsRealtime(0.25f);
            }
            string blocked = _session.SaveBlockReason;
            if (blocked != null)
            {
                float waited = Time.realtimeSinceStartup - t0;
                string line = $"save {slot ?? "quick"} ({why}): blocked after {waited:0} s: {blocked}";
                if (soft) Log(line + (combatWaitSeconds > 0f ? " - NOT written (no safe moment before the scenario ended)" : " - tried again later"));
                else Fail(line);
                yield break;
            }
            bool ok = slot == null ? QuickSaveNow(saves) : saves.SaveTo(slot);
            yield return new WaitForSecondsRealtime(0.5f);
            if (!ok) { if (soft) Log($"save {slot} ({why}): refused"); else Fail($"save {slot}: refused"); yield break; }
            _lastCheckpoint = Time.realtimeSinceStartup;
            _lastSaveOk = true;
            if (_progress != null && slot != null && _includeDepth == 0)
            {
                _progress.Slot = slot;
                _progress.ResumeStep = resumeStep;
                _progress.Vars = CheckpointVars();
                _progress.SavedUtc = DateTime.UtcNow.ToString("u");
                WriteProgress();
            }
            Log($"save {slot ?? "quick"} ({why})" + (resumeStep > 0 ? $": resume point step {resumeStep}/{_spec?.Steps.Count}" : "") +
                $", hero {V(Hero.transform.position)}");
        }

        bool QuickSaveNow(SaveGameController saves)
        {
            if (_session.SaveBlockReason != null) return false;
            saves.QuickSave();
            return true;
        }

        /// <summary>
        /// Loads <paramref name="slot"/> (null = the quick slot) and goes on in the loaded game: the runner adopts the new
        /// session (dev state of the old one — calm, freeze, focus — is dropped) and waits for the loading hold.
        /// </summary>
        internal IEnumerator LoadSlot(string slot)
        {
            var saves = Saves;
            if (saves == null) { Fail("load: no SaveGameController"); yield break; }
            string name = slot ?? saves.QuickLoadSlot ?? SaveSlots.QuickSlot; // fix-107: the one quicksave
            if (!saves.Slots().Any(kv => kv.Key == name))
            {
                var have = saves.Slots().Select(kv => kv.Key).Where(k => k.StartsWith("scn_") || k == SaveSlots.QuickSlot).Take(12);
                Fail($"load: no save slot '{name}' (have: {string.Join(", ", have)})");
                yield break;
            }
            if (_session.Dialog.IsActive) _session.Dialog.Abort();
            ReleaseInputs();
            if (!saves.Load(name)) { Fail($"load {name}: the save could not be read"); yield break; }
            AdoptSession(_boot.Session);
            yield return null;
            float t0 = Time.realtimeSinceStartup;
            while (LoadingHold.IsHeld && Time.realtimeSinceStartup - t0 < 120f) yield return new WaitForSecondsRealtime(0.25f);
            var streamer = FindAnyObjectByType<G3WorldStreamer>();
            while (streamer != null && !streamer.IsReady(Hero.transform.position) && Time.realtimeSinceStartup - t0 < 120f) yield return new WaitForSecondsRealtime(0.25f);
            yield return new WaitForSeconds(0.5f);
            Log($"load {name}: hero {V(Hero.transform.position)} grounded={Hero.IsGrounded}, {(Time.realtimeSinceStartup - t0):0.0} s");
        }

        /// <summary>The game was replaced by a loaded one: the runner works on the new session from now on.</summary>
        void AdoptSession(GameSession session)
        {
            if (session == null || session == _session) return;
            _session = session;
            _calm.Clear();
            _frozen.Clear();
            _paralysedBefore = null; // the delegates belonged to the old session's systems
            _heldBefore = null;
            _focus = null;
            _dlg = null;
            RehookPicks(); // fix-121: the pick attempts on the 'lockedchest' chest go on being counted in the loaded game
            Bind();
        }

        /// <summary>A checkpoint before step <paramref name="index"/> (0-based) when one is due (<see cref="ScenarioCheckpoints.Due"/>).</summary>
        IEnumerator CheckpointIfDue(int index)
        {
            if (_checkpointMinutes <= 0f || _spec == null || index == _opt.StartIndex) yield break;
            if (!ScenarioCheckpoints.Due(Time.realtimeSinceStartup - _lastCheckpoint, _checkpointMinutes)) yield break;
            yield return SaveSlot(ScenarioCheckpoints.RunSlot(_spec.Name), 5f, index + 1, $"checkpoint every {_checkpointMinutes:0} min", soft: true);
            // a blocked checkpoint is tried again a minute later, not at every step
            if (ScenarioCheckpoints.Due(Time.realtimeSinceStartup - _lastCheckpoint, _checkpointMinutes))
                _lastCheckpoint = Time.realtimeSinceStartup - Mathf.Max(0f, _checkpointMinutes * 60f - 60f);
        }

        // ---------------------------------------------------------------- loadouts

        AttackStyle _style = AttackStyle.Melee;
        string _styleSpell;

        /// <summary>Grants the loadout's set (unless <paramref name="styleOnly"/>) and makes its style the fight op's.</summary>
        internal IEnumerator ApplyLoadout(string name, bool styleOnly = false)
        {
            var lo = ScenarioLoadout.Find(name);
            if (lo == null) { Fail($"loadout: unknown '{name}' ({ScenarioLoadout.Names})"); yield break; }
            _style = lo.Style;
            _styleSpell = lo.Spell;
            if (styleOnly) { Log($"loadout {lo.Name}: attack style {lo.Style} (items from the save)"); yield break; }
            var steps = ScenarioParser.ParseInline(lo.Ops, "loadout " + lo.Name);
            if (!steps.Ok) { Fail($"loadout {lo.Name}: " + string.Join(" | ", steps.Errors)); yield break; }
            int before = _failures.Count;
            _inLoadoutOps++;
            try
            {
                foreach (var st in steps.Steps)
                {
                    yield return Execute(st);
                    if (_failures.Count > before) yield break;
                }
            }
            finally { _inLoadoutOps--; }
            PutLoadoutOnQuickBar(lo);
            Result($"loadout {lo.Name} ({lo.Label}): style {lo.Style}, melee {_session.Items.Equipment.In(WornSlot.MeleeWeapon) ?? "-"}, " +
                   $"ranged {_session.Items.Equipment.In(WornSlot.RangedWeapon) ?? "-"}, off-hand {_session.Items.Equipment.In(WornSlot.OffHand) ?? "-"}");
        }

        /// <summary>
        /// fix-56: the loadout's weapons and ammunition go on the quick bar like a player's (owner shot r56: the bow loadout's
        /// It_Bow_01 was in the hand but not on the bar; its cell 3 held the start kit's hunting bow, so no cell showed the
        /// drawn bow). A worn item not on the bar takes the cell of a bar item of the same equip slot (the old bow, the old
        /// sword), else the first free cell.
        /// </summary>
        void PutLoadoutOnQuickBar(ScenarioLoadout lo)
        {
            var items = _session.Items;
            var db = _session.Content.Database;
            // fix-57: the loadout's learned spells (G3 OnPlayerAssignStackKey: only a learned spell goes on the bar), each on
            // a free cell; the fight op readies them with their key
            foreach (string spell in lo.QuickItems)
            {
                if (items.QuickSlots.SlotOf(spell) >= 0) continue;
                if (!items.Inventories.Of(ActorId.Player).Has(spell) || !_session.HeroSpells.CanReady(spell)) { Log($"loadout: {spell} not learned, not on the quick bar"); continue; }
                int free = -1;
                for (int i = 0; i < QuickSlots.Count && free < 0; i++) if (items.QuickSlots.ItemAt(i) == null) free = i;
                if (free >= 0 && items.QuickSlots.Assign(free, spell)) Log($"loadout: {spell} on quick slot {QuickSlots.DigitOfSlot(free)}");
                else Log($"loadout: no free quick slot for {spell}");
            }
            foreach (var slot in new[] { WornSlot.MeleeWeapon, WornSlot.OffHand, WornSlot.Shield, WornSlot.RangedWeapon, WornSlot.Ammo })
            {
                string item = items.Equipment.In(slot);
                if (item == null || items.QuickSlots.SlotOf(item) >= 0 || !db.TryGet(item, out ItemDef def)) continue;
                int cell = -1;
                for (int i = 0; i < QuickSlots.Count && cell < 0; i++)
                {
                    string other = items.QuickSlots.ItemAt(i);
                    if (other != null && !items.Equipment.IsWorn(other) && db.TryGet(other, out ItemDef od) && od.Slot == def.Slot) cell = i;
                }
                for (int i = 0; i < QuickSlots.Count && cell < 0; i++)
                    if (items.QuickSlots.ItemAt(i) == null) cell = i;
                if (cell >= 0 && items.QuickSlots.Assign(cell, item)) Log($"loadout: {item} on quick slot {QuickSlots.DigitOfSlot(cell)}");
            }
        }

        /// <summary>The draw kind a bare 'draw' uses: the loadout's.</summary>
        string StyleDrawKind() => _style == AttackStyle.Bow || _style == AttackStyle.Crossbow ? "ranged" : _style == AttackStyle.Fists ? "fist" : "melee";

        static AttackStyle? ParseStyle(string text)
        {
            switch ((text ?? "").ToLowerInvariant())
            {
                case "": return null;
                case "melee": return AttackStyle.Melee;
                case "fist": case "fists": return AttackStyle.Fists;
                case "bow": return AttackStyle.Bow;
                case "crossbow": case "xbow": return AttackStyle.Crossbow;
                case "spell": case "spells": case "magic": return AttackStyle.Spell;
                default: return null;
            }
        }

        // ---------------------------------------------------------------- per-step wrapper

        /// <summary>Skips a step whose unless= holds (logged); false and a failure on a bad condition.</summary>
        bool SkipByUnless(ScenarioStep step, out bool bad)
        {
            bad = false;
            if (!step.Has(OpSpec.Unless)) return false;
            if (!ScenarioCondition.TryParse(step.Get(OpSpec.Unless), out var unless, out var unlessError))
            {
                Fail($"'{step.Source}': unless: {unlessError}");
                bad = true;
                return false;
            }
            string now = Evaluate(unless);
            if (!unless.Test(now)) return false;
            Log($"skip '{step.Source}': {unless} holds ({now})");
            return true;
        }

        /// <summary>
        /// Before a step: a dialog the scenario did not open is answered and closed first (owner, r30: NPCs start talking
        /// to the hero), unless the step drives dialogs itself or reads the dialog (then it takes it over).
        /// </summary>
        IEnumerator AroundStep(ScenarioStep step)
        {
            if (_session.Dialog.Current == null || DialogAutoAnswer.IsDialogOp(step.Op)) yield break;
            if ((step.Op == "wait" || step.Op == "expect") &&
                ScenarioCondition.TryParse(step.Get("until") ?? step.Get("cond"), out var c, out _) && DialogAutoAnswer.IsDialogCondition(c.Kind)) yield break;
            DialogSeen(_session.Dialog.Current);
            if (_dlgOwned || !_autoDialog) yield break;
            _dlgHurry = true;
            _stepAnswers = step.Get(OpSpec.Answer);
            float t0 = Time.time;
            while (_session.Dialog.Current != null && !_dlgOwned && Time.time - t0 < DialogAutoAnswer.ForceCloseSeconds + 1f) yield return null;
            _dlgHurry = false;
        }

        // ---------------------------------------------------------------- dialogs the scenario did not open

        bool _autoDialog = true;
        readonly List<string> _autoAnswers = new List<string>();
        Conversation _dlg;
        bool _dlgOwned, _dlgHurry;
        bool _dlgAnswering; // an op answers the talk itself (walkto, escort, draw: AnswerUnaskedTalk)
        float _dlgSince, _dlgProgressAt, _dlgActAt;
        string _dlgKey;
        readonly HashSet<string> _dlgTaken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int _npcTalks, _dialogBugs;

        /// <summary>Remembers a conversation when it is first seen: the scenario's own when a dialog op runs.</summary>
        void DialogSeen(Conversation c)
        {
            if (ReferenceEquals(c, _dlg)) return;
            _dlg = c;
            _dlgSince = _dlgProgressAt = Time.time;
            _dlgKey = null;
            _dlgTaken.Clear();
            _dlgChoices = 0;
            _dlgOwned = DialogAutoAnswer.IsDialogOp(_currentOp);
            if (_dlgOwned) return;
            _npcTalks++;
            Log($"dialog opened by {c.Partner} (not by the scenario) during '{_currentOp ?? "-"}'{ControlsNote()}: {DialogText(c)}" +
                (_autoDialog ? "" : " - autodialog off: left to the scenario"));
        }

        /// <summary>A condition read the open dialog (wait until=dialog, expect partner=…): the scenario takes it over.</summary>
        internal void AdoptDialog()
        {
            var c = _session?.Dialog.Current;
            if (c == null) return;
            DialogSeen(c);
            if (!_dlgOwned) Log($"dialog with {c.Partner}: taken over by the scenario ({_currentOp ?? "-"})");
            _dlgOwned = true;
        }

        string DialogText(Conversation c)
        {
            if (c == null) return "no dialog";
            var loc = _session.Localizer;
            string opts = string.Join(", ", c.Options.Select(o => o.Entry + (o.IsExit ? "(END)" : "") + Title(loc, o)));
            return $"speaker {c.Partner}, {c.Phase}, info {(c.Entry != null ? c.Entry.Id.ToString() : "-")}, options [{opts}]";
        }

        /// <summary>fix-118: an option's menu text as the dialog shows it (title and price, G3 GetDialogDescription).</summary>
        internal static string OptionLabel(ILocalizer loc, DialogOption option)
        {
            if (loc == null || option.Title.IsEmpty) return "";
            return DialogOptionText.Format(loc.Text(option.Title) ?? "", option.Cost, loc.Text);
        }

        static string Title(ILocalizer loc, DialogOption option)
        {
            string t = OptionLabel(loc, option);
            if (t.Length > 48) t = t.Substring(0, 48) + "…";
            return t.Length > 0 ? $" \"{t}\"" : "";
        }

        /// <summary>
        /// Every frame of a run: a dialog the scenario did not open is answered (<see cref="DialogAutoAnswer"/>) after a short
        /// grace; one still open after 30 s is force-closed with a BUG line. A dialog the scenario opened is left to it, but
        /// closed as a player would when it stays without progress for 30 s while no dialog op runs.
        /// </summary>
        void DialogTick()
        {
            if (_spec == null || _session == null) { _dlg = null; return; }
            var c = _session.Dialog.Current;
            if (c == null)
            {
                if (_dlg != null && !_dlgOwned) Log($"dialog with {_dlg.Partner} closed ({(Time.time - _dlgSince):0.0} s)");
                _dlg = null;
                return;
            }
            DialogSeen(c);
            string key = $"{c.Phase}|{c.Entry?.Id}|{c.Options.Count}";
            if (key != _dlgKey)
            {
                if (_dlgKey != null && !_dlgOwned && c.Entry != null && !key.StartsWith(ConversationPhase.Running.ToString()))
                    Log($"dialog {c.Partner}: {DialogText(c)}");
                _dlgKey = key;
                _dlgProgressAt = Time.time;
            }
            if (DialogAutoAnswer.IsDialogOp(_currentOp) || _dlgAnswering) return;
            if (_dlgOwned)
            {
                if (Time.time - _dlgProgressAt < DialogAutoAnswer.ForceCloseSeconds) return;
                Log($"dialog with {c.Partner} left open by the scenario, no progress for {DialogAutoAnswer.ForceCloseSeconds:0} s during '{_currentOp ?? "-"}' -> END/abort: {DialogText(c)}");
                CloseDialog(c);
                return;
            }
            if (Time.time < _dlgActAt) return;
            _dlgActAt = Time.time + 0.25f;
            var act = DialogAutoAnswer.Next(c.Phase.ToString(), Time.time - _dlgSince + (_dlgHurry ? DialogAutoAnswer.GraceSeconds : 0f));
            if (!_autoDialog && act != DialogAct.ForceClose) return;
            switch (act)
            {
                case DialogAct.ForceClose:
                    ForceCloseDialog(c, _currentOp);
                    break;
                case DialogAct.CompleteLine:
                    _session.Dialog.CompleteLine();
                    break;
                case DialogAct.Choose:
                    if (_dlgChoices++ >= DialogAutoAnswer.MaxChoices || !ChooseFor(c, out _))
                    {
                        Log($"dialog {c.Partner}: abort after {_dlgChoices - 1} choices: {DialogText(c)}");
                        _session.Dialog.Abort();
                    }
                    break;
            }
        }

        int _dlgChoices;

        /// <summary>
        /// Chooses for a talk the scenario did not open (<see cref="DialogAutoAnswer.Pick"/>: expected, gate guard, topic, END)
        /// and logs it; false when nothing may be chosen (every option starts a fight). <paramref name="answer"/> = the
        /// non-END entry taken.
        /// </summary>
        internal bool ChooseFor(Conversation c, out string answer)
        {
            answer = null;
            var options = c.Options.Select(o => (o.Entry.Name, TalkChoiceOf(o))).ToList();
            var expected = (_stepAnswers ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim())
                           .Concat(_autoAnswers).Concat(_spec?.Answers ?? Enumerable.Empty<string>());
            int i = DialogAutoAnswer.Pick(options, expected, _dlgTaken, out string why);
            if (i < 0) { Log($"dialog {c.Partner}: nothing to choose ({why}): {DialogText(c)}"); return false; }
            var pick = c.Options[i];
            if (!pick.IsExit) { _dlgTaken.Add(pick.Entry.Name); answer = pick.Entry.Name; }
            Log($"dialog {c.Partner}: choose {pick.Entry}{(pick.IsExit ? "(END)" : "")} ({why}) of {DialogText(c)}");
            _session.Dialog.Choose(pick.Entry);
            return true;
        }

        /// <summary>A talk the scenario did not open still runs after 30 s: closed, with a BUG line in the result.</summary>
        void ForceCloseDialog(Conversation c, string op)
        {
            _dialogBugs++;
            string bug = $"BUG dialog with {c.Partner} still open after {DialogAutoAnswer.ForceCloseSeconds:0} s during '{op ?? "-"}', force-closed: {DialogText(c)}{ControlsNote()}";
            Debug.LogWarning(Scenario.Tag + bug);
            Result(bug);
            _session.Dialog.Abort();
        }

        /// <summary>Closes a talk as a player would: END when on offer, else abort.</summary>
        void CloseDialog(Conversation c)
        {
            if (c.Phase == ConversationPhase.AwaitingChoice)
            {
                var exit = c.Options.Where(o => o.IsExit).Select(o => o.Entry).FirstOrDefault();
                if (!exit.IsEmpty) { _session.Dialog.Choose(exit); return; }
            }
            _session.Dialog.Abort();
        }

        /// <summary>One line for <see cref="Scenario.Dialog"/> / the dialogstate op: is a dialog open, and whose.</summary>
        internal string DialogNow()
        {
            var c = _session?.Dialog.Current;
            if (c == null) return $"no dialog (NPC-initiated this run: {_npcTalks}, force-closed: {_dialogBugs})";
            bool known = ReferenceEquals(c, _dlg);
            string who = !known ? "not seen by a run" : _dlgOwned ? "opened by the scenario" : "NPC-initiated";
            return $"dialog open: {who}, {DialogText(c)}, open {(known ? Time.time - _dlgSince : 0f):0.0} s, autodialog {(_autoDialog ? "on" : "off")}{ControlsNote()}";
        }
    }
}
#endif
