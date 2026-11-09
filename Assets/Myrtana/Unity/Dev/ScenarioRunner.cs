#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using G3.Unity;
using G3.Unity.Characters;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Session.Dev;
using Myrtana.Simulation;
using UnityEngine;

namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// Executes dev scenarios (see <see cref="Scenario"/>): one hidden object per Play session that runs the steps as a
    /// coroutine and keeps the dev state (god mode, calmed/frozen NPCs, forced focus). Ops are in ScenarioRunner.Ops.cs,
    /// probes and conditions in ScenarioRunner.Probe.cs.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    public sealed partial class ScenarioRunner : MonoBehaviour
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static ScenarioRunner Instance { get; private set; }

        GameBootstrap _boot;
        GameSession _session;
        Coroutine _run;
        ScenarioSpec _spec;
        int _stepIndex;
        float _started;
        readonly List<string> _lines = new List<string>();
        readonly List<string> _failures = new List<string>();
        readonly List<string> _results = new List<string>();
        readonly Dictionary<string, string> _vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string _status = "idle";

        // dev state
        bool _god;
        float _godUntil;
        /// <summary>fix-65 (R32-5): the run's loadout was requested (rotation / --loadout), not pinned by the file.</summary>
        bool _loadoutRequested;
        /// <summary>fix-65: &gt; 0 while a loadout's own statements run.</summary>
        int _inLoadoutOps;
        /// <summary>fix-65: the last <c>SaveSlot</c> wrote its slot.</summary>
        bool _lastSaveOk;
        readonly HashSet<ActorId> _calm = new HashSet<ActorId>();
        readonly HashSet<ActorId> _frozen = new HashSet<ActorId>();
        Func<ActorId, bool> _paralysedBefore;
        ActorId? _focus;
        float _nextFreezeTick;
        Func<ActorId, bool> _heldBefore;
        bool _weatherForced;
        string _textBefore, _voiceBefore; // languages before the first 'lang' (null = untouched)
        bool _langSticky;                 // the run is a lone 'lang' (console): it stays until reset / Play exit
        bool? _turnKeysBefore;            // fix-73: the turn-keys option before a scenario's 'turnkeys' (null = untouched)
        bool? _decorFocusBefore;          // fix-79: the decorative-objects focus option before a scenario's 'decorfocus'
        internal float TurnKeyHeroTurn;   // fix-73: degrees the hero turned during the last 'turnkey'

        public IReadOnlyList<string> Lines => _lines;
        public string StatusLine => _status;

        /// <summary>fix-57: progress of the running scenario for <c>scenario.sh</c>'s watchdog (<see cref="ScenarioWatchdog"/>).</summary>
        readonly ScenarioWatchdog _watchdog = new ScenarioWatchdog();

        /// <summary>The status with the seconds since the run's last progress, or STALLED (<see cref="ScenarioWatchdog.Heartbeat"/>).</summary>
        public string Heartbeat(float stallMinutes) =>
            ScenarioWatchdog.Heartbeat(_status, _run != null ? _watchdog.QuietSeconds(Time.realtimeSinceStartup) : 0f, stallMinutes);

        internal GameSession S => _session;
        internal G3Player Hero { get; private set; }
        internal PlayerCombat Combat { get; private set; }
        internal NpcViewManager Views { get; private set; }

        /// <summary>The runner of the current session (created on demand); null without a session.</summary>
        public static ScenarioRunner Ensure()
        {
            var boot = FindAnyObjectByType<GameBootstrap>();
            if (boot == null || boot.Session == null || LoadingHold.IsHeld) return null;
            if (Instance != null && Instance._session != boot.Session)
            {
                Destroy(Instance.gameObject);
                Instance = null;
            }
            if (Instance == null)
            {
                var go = new GameObject("[ScenarioRunner]") { hideFlags = HideFlags.DontSave };
                Instance = go.AddComponent<ScenarioRunner>();
                Instance._boot = boot;
                Instance._session = boot.Session;
            }
            Instance.Bind();
            return Instance;
        }

        public static Vector3? HeroPosition()
        {
            var p = FindAnyObjectByType<G3Player>();
            return p != null ? p.transform.position : (Vector3?)null;
        }

        void Bind()
        {
            if (Hero == null) Hero = FindAnyObjectByType<G3Player>();
            if (Combat == null) Combat = FindAnyObjectByType<PlayerCombat>();
            if (Views == null) Views = FindAnyObjectByType<NpcViewManager>();
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            StopTouchWatch(log: false);
            ReleaseInputs();
            RestoreLanguage();
            RestoreTurnKeys();
            RestoreDecorFocus();
            RestoreCookFire();
            RestoreChestMarkers();
            RestoreMapFilters();
            RestoreShieldBeasts();
            RestoreForgiveHits();
            RestoreLateTrophies();
            RestoreForcedRelease();
            RestoreWindowCam();
            ClearLosGeometry(false);
            _pinnedObject = null; // fix-105
        }

        // ---------------------------------------------------------------- run control

        public string StartRun(ScenarioSpec spec, RunOptions options = null)
        {
            if (_run != null) StopRun("replaced by " + spec.Name);
            var opt = options ?? new RunOptions();
            // fix-57: a scenario that names a loadout ("own" = its own weapons) pins it; --loadout / the suite rotation only
            // applies to the others (r31 draw_aim_strafe ran with the fists loadout)
            string loadoutNote = null;
            if (!opt.Resumed) opt.Loadout = ScenarioLoadout.Choose(spec.Loadout, opt.Loadout, out loadoutNote);
            _loadoutRequested = opt.Loadout != null && string.IsNullOrWhiteSpace(spec.Loadout); // fix-65: the rotation's, not pinned
            if (opt.Loadout != null && ScenarioLoadout.Find(opt.Loadout) == null)
                return $"errors: unknown loadout '{opt.Loadout}' ({ScenarioLoadout.Names})";
            if (opt.StartIndex < 0 || opt.StartIndex >= Math.Max(1, spec.Steps.Count))
                return $"errors: start step {opt.StartIndex + 1} outside 1..{spec.Steps.Count}";
            _opt = opt;
            _spec = spec;
            _lines.Clear();
            _failures.Clear();
            _results.Clear();
            _vars.Clear();
            // fix-184 (regress-1021b fix68_arrow_blocker 'arrowhits:hero=0: was 2' with touchwatch "no touches"): the
            // projectile counters are static and outlive a run (and a fresh session); every run counts from zero
            Projectile.BodyHits.Clear();
            Projectile.Rests.Clear();
            Projectile.LevelHits = 0;
            _xpMark = _session?.Hero?.Xp ?? 0; // fix-97: xpgain counts from the run start until an xpmark
            _stepIndex = _opt.StartIndex;
            _started = Time.time;
            _clockCredit = 0f;
            _langSticky = spec.Steps.Count == 1 && spec.Steps[0].Op == "lang";
            _checkpointMinutes = _opt.CheckpointMinutes ?? spec.CheckpointMinutes;
            _lastCheckpoint = Time.realtimeSinceStartup;
            _autoDialog = spec.AutoDialog ?? DialogAutoAnswer.DefaultOn(spec.Steps);
            _autoAnswers.Clear();
            _npcTalks = _dialogBugs = 0;
            // a talk an earlier run opened and left open (exec "talk Gorn", then exec "choose 1") stays the scenario's own
            if (!(_dlgOwned && _dlg != null && ReferenceEquals(_dlg, _session.Dialog.Current))) _dlg = null;
            _includeDepth = 0;
            _currentOp = null;
            _style = AttackStyle.Melee;
            _styleSpell = null;
            _status = $"running {_stepIndex}/{spec.Steps.Count} {spec.Name}";
            Log($"{spec.Name}: start, {spec.Steps.Count} steps" + (spec.Verifies != null ? " — " + spec.Verifies : "") +
                (_opt.StartIndex > 0 ? $"; from step {_opt.StartIndex + 1}" : "") + (_opt.LoadSlot != null ? $" after loading {_opt.LoadSlot}" : "") +
                (_opt.Loadout != null ? $"; loadout {_opt.Loadout}" : "") + (_checkpointMinutes > 0 ? $"; checkpoints every {_checkpointMinutes:0} min" : "") +
                $"; autodialog {(_autoDialog ? "on" : "off")}");
            if (loadoutNote != null) Log(loadoutNote);
            _watchdog.Reset(Time.realtimeSinceStartup);
            StartProgress(spec);
            _run = StartCoroutine(RunAll(spec));
            return $"started {spec.Name} ({spec.Steps.Count} steps" + (_opt.StartIndex > 0 ? $", from {_opt.StartIndex + 1}" : "") +
                   "); poll Myrtana.Unity.Dev.Scenario.Status";
        }

        /// <summary>Seconds the steps paused their own clocks or added to their time; the scenario timeout grows by them
        /// (<see cref="ScenarioPacing.TimedOut"/>, fix-41).</summary>
        float _clockCredit;

        internal void CreditClock(float seconds)
        {
            if (seconds > 0f) _clockCredit += seconds;
        }

        public void StopRun(string why)
        {
            if (_run != null) StopCoroutine(_run);
            _run = null;
            ReleaseInputs();
            Finish(why);
        }

        IEnumerator RunAll(ScenarioSpec spec)
        {
            // fix-54: a resume loads the checkpoint slot and puts the dev state back; then the loadout (only its style after a load)
            if (_opt.LoadSlot != null)
            {
                _status = $"running {spec.Name}: load {_opt.LoadSlot}";
                yield return Guarded(LoadSlot(_opt.LoadSlot), "load " + _opt.LoadSlot);
            }
            if (_failures.Count == 0 && _opt.Vars != null) RestoreDevState(_opt.Vars);
            if (_failures.Count == 0 && _opt.Loadout != null)
            {
                _status = $"running {spec.Name}: loadout {_opt.Loadout}";
                _currentOp = "loadout";
                yield return Guarded(ApplyLoadout(_opt.Loadout, styleOnly: _opt.LoadSlot != null), "loadout " + _opt.Loadout);
                _currentOp = null;
            }
            // fix-119: a scenario file pins the session random at its start (a resume keeps the checkpoint's own rolls)
            if (_failures.Count == 0 && spec.Seed.HasValue && _opt.LoadSlot == null && _session != null)
            {
                _session.Reseed(spec.Seed.Value);
                Log($"random seed pinned to {spec.Seed.Value} (session seed was {_session.RandomSeed}); loads in the run roll anew");
            }
            if (_opt.StartIndex > 0) Log($"steps 1..{_opt.StartIndex} skipped (start at step {_opt.StartIndex + 1}: {spec.Steps[_opt.StartIndex].Source})");
            if (_failures.Count > 0) _stepIndex = spec.Steps.Count;
            for (; _stepIndex < spec.Steps.Count; _stepIndex++)
            {
                yield return Guarded(CheckpointIfDue(_stepIndex), "checkpoint");
                if (ScenarioPacing.TimedOut(Time.time - _started, spec.TimeoutSeconds, _clockCredit))
                {
                    Fail(FormattableString.Invariant($"timeout {spec.TimeoutSeconds:0} s") +
                         (_clockCredit > 0f ? FormattableString.Invariant($" (+{_clockCredit:0} s paused/added by steps, ran {Time.time - _started:0} s)") : ""));
                    break;
                }
                var missing = new List<string>();
                var step = spec.Steps[_stepIndex].Map(v => ScenarioVars.Resolve(v, _vars, missing));
                _status = $"running {_stepIndex + 1}/{spec.Steps.Count} {spec.Name}: {step.Source}";
                if (_progress != null) { _progress.Step = _stepIndex + 1; WriteProgress(); }
                if (missing.Count > 0)
                {
                    Fail($"step {_stepIndex + 1} '{step.Source}': unknown {string.Join(",", missing)}" +
                         (_opt.StartIndex > 0 ? " (set by a skipped step: resume from a checkpoint or start earlier)" : ""));
                    break;
                }
                if (SkipByUnless(step, out bool badUnless)) continue;
                if (badUnless) break;
                yield return Guarded(AroundStep(step), "dialog before " + step.Source);
                _currentOp = step.Op;
                _stepAnswers = step.Get(OpSpec.Answer);
                int failuresBefore = _failures.Count;
                IEnumerator body = null;
                var culture = EnterInvariant();
                try { body = Execute(step); }
                catch (Exception e) { Fail($"step {_stepIndex + 1} '{step.Source}': {e.GetType().Name}: {e.Message}"); }
                finally { CultureInfo.CurrentCulture = culture; }
                if (body != null) yield return Guarded(body, $"step {_stepIndex + 1} '{step.Source}'");
                _currentOp = null;
                _stepAnswers = null;
                if (_failures.Count > failuresBefore && step.Op != "expect") break;
            }
            ReleaseInputs();
            // fix-54: a passed run is saved to its own slot, so a sequence of scenarios can go on from there after a crash
            if (_failures.Count == 0 && _checkpointMinutes > 0f && _opt.Track && _session.Combat.Vitals.StateOf(ActorId.Player) == VitalState.Alive)
            {
                // fix-65 (R32-6, r32 ch07/08/09): a fight after the last step keeps the pass save waiting (up to
                // ScenarioCheckpoints.CombatWaitSeconds) instead of losing it to the next fresh Play; never written = logged
                yield return Guarded(SaveSlot(ScenarioCheckpoints.PassSlot(spec.Name), 10f, 0, "passed", soft: true,
                                              combatWaitSeconds: ScenarioCheckpoints.CombatWaitSeconds), "pass save");
                if (_progress != null && _lastSaveOk) { _progress.Slot = ScenarioCheckpoints.PassSlot(spec.Name); _progress.ResumeStep = 0; }
            }
            _run = null;
            Finish(null);
        }

        /// <summary>
        /// Runs an op and its nested enumerators here, catching exceptions per MoveNext (no try around yield): an exception
        /// fails the step instead of killing the run's coroutine (which would leave the status "running" for ever).
        /// </summary>
        IEnumerator Guarded(IEnumerator body, string label)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(body);
            while (stack.Count > 0)
            {
                object current;
                var culture = EnterInvariant();
                try
                {
                    if (!stack.Peek().MoveNext()) { stack.Pop(); continue; }
                    current = stack.Peek().Current;
                }
                catch (Exception e)
                {
                    Fail($"{label}: {e.GetType().Name}: {e.Message}");
                    yield break;
                }
                finally { CultureInfo.CurrentCulture = culture; }
                if (current is IEnumerator nested) { stack.Push(nested); continue; }
                yield return current;
            }
        }

        void Finish(string why)
        {
            if (_spec == null) return;
            bool ok = _failures.Count == 0 && why == null;
            string line = $"DONE {(ok ? "ok" : "FAIL")} {_spec.Name} {Math.Min(_stepIndex, _spec.Steps.Count)}/{_spec.Steps.Count} steps {F(Time.time - _started)} s" +
                          (why != null ? " | " + why : "") +
                          (_failures.Count > 0 ? " | FAILED: " + string.Join("; ", _failures) : "") +
                          (_results.Count > 0 ? " | " + string.Join("; ", _results) : "");
            _status = line;
            Debug.Log(Scenario.Tag + line);
            if (_progress != null)
            {
                _progress.State = ok ? "ok" : why != null ? "stopped" : "fail";
                _progress.Result = line.Length > 2000 ? line.Substring(0, 2000) : line;
                WriteProgress();
                _progress = null;
            }
            _currentOp = null;
            _spec = null;
            if (!_langSticky) RestoreLanguage();
            RestoreTurnKeys();
            RestoreDecorFocus();
            RestoreCookFire();
            RestoreChestMarkers();
            RestoreMapFilters();
            RestoreShieldBeasts();
            RestoreForgiveHits();
            RestoreLateTrophies();
            RestoreForcedRelease();
            RestoreWindowCam();
            ClearLosGeometry(false);
            _pinnedObject = null; // fix-105
        }

        /// <summary>fix-73: sets the turn-keys option for this scenario only (the menu's stored value comes back at its end).</summary>
        internal void SetTurnKeys(bool orbit)
        {
            _turnKeysBefore ??= G3Player.TurnKeysOrbitCamera;
            G3Player.TurnKeysOrbitCamera = orbit;
        }

        /// <summary>fix-79: sets the "Focus decorative objects" option for this scenario only (the stored value comes back at its end).</summary>
        internal void SetDecorFocus(bool on)
        {
            _decorFocusBefore ??= PlayerInteraction.FocusDecorativeObjects;
            PlayerInteraction.FocusDecorativeObjects = on;
        }

        void RestoreDecorFocus()
        {
            if (_decorFocusBefore == null) return;
            PlayerInteraction.FocusDecorativeObjects = _decorFocusBefore.Value;
            _decorFocusBefore = null;
        }

        void RestoreTurnKeys()
        {
            if (_turnKeysBefore == null) return;
            G3Player.TurnKeysOrbitCamera = _turnKeysBefore.Value;
            _turnKeysBefore = null;
        }

        /// <summary>Remembers the languages before a scenario's first 'lang'; its changes are not stored in the preferences.</summary>
        internal void KeepLanguageBefore()
        {
            if (_textBefore != null) return;
            var l = _session.Settings.Languages;
            _textBefore = l.TextLanguage;
            _voiceBefore = l.VoiceLanguage;
            LanguagePreferences.Transient = true;
        }

        /// <summary>Puts back the languages a scenario's 'lang' changed (scenario end, reset, Play exit).</summary>
        void RestoreLanguage()
        {
            if (_textBefore == null) return;
            var l = _session?.Settings?.Languages;
            if (l != null)
            {
                l.TextLanguage = _textBefore;
                l.VoiceLanguage = _voiceBefore;
                Log($"lang restored: voice={l.VoiceLanguage} text={l.TextLanguage}");
            }
            _textBefore = _voiceBefore = null;
            LanguagePreferences.Transient = false;
        }

        internal void Log(string text)
        {
            _lines.Add(text);
            Debug.Log(Scenario.Tag + text);
        }

        /// <summary>A line that also goes into the final result line.</summary>
        internal void Result(string text)
        {
            _results.Add(text);
            Log(text);
        }

        internal void Fail(string text)
        {
            _failures.Add(text);
            Debug.LogWarning(Scenario.Tag + "FAIL " + text);
            _lines.Add("FAIL " + text);
        }

        // ---------------------------------------------------------------- per-frame dev state

        void Update()
        {
            if (_session == null) return;
            if (_run != null)
            {
                var at = Hero != null ? Hero.transform.position : Vector3.zero;
                _watchdog.Observe(Time.realtimeSinceStartup, _stepIndex, _lines.Count, at.x, at.z);
            }
            DialogTick();
            GetUpWhenSitting();
            HookGod();
            if (_frozen.Count > 0 && Time.time >= _nextFreezeTick)
            {
                _nextFreezeTick = Time.time + 1f;
                foreach (var id in _frozen) _session.Npcs.FullStop(id);
            }
        }

        int _sitPressed;  // PlayerCombat.SitCount the get-up was pressed for
        int _sitScripted; // PlayerCombat.SitCount of the 'sit' op's own sit (the scenario presses itself)

        /// <summary>The 'sit' op knocked the hero down: that sit is the scenario's to get up from.</summary>
        internal void ScriptedSit() { if (Combat != null) _sitScripted = Combat.SitCount; }

        float _sitPressAt;  // when the runner last pressed the get-up
        int _sitPresses;    // presses for the current sit

        /// <summary>
        /// G3: the hero sits after a heavy hit until a press (ZS_SitKnockDown_Loop, no timeout for the player; owner confirmed).
        /// A scenario that fights, escorts or waits would hang there, so the runner presses at once: LMB (GetUpAttack), or
        /// RMB (GetUpParade) when it holds the block. A press during the fall waits for the sit (G3 queue). fix-41: while he
        /// still sits with no get-up pending the press is repeated, and after a few the dev get-up frees him
        /// (<see cref="ScenarioPacing.NextGetUp"/>; r28: one press per sit was lost, he sat 20-249 s with empty hands).
        /// </summary>
        void GetUpWhenSitting()
        {
            if (_spec == null || Combat == null || Combat.Fighter == null || !Combat.Fighter.SittingKnockedDown) return;
            int sit = Combat.SitCount;
            if (sit == _sitScripted) return;
            bool newSit = sit != _sitPressed;
            bool pending = Combat.Fighter.PendingGetUp != CombatAction.None;
            var next = ScenarioPacing.NextGetUp(true, newSit, pending, Time.time - _sitPressAt, newSit ? 0 : _sitPresses);
            if (next == GetUpAction.None) return;
            if (newSit) { _sitPressed = sit; _sitPresses = 0; }
            _sitPressAt = Time.time;
            if (next == GetUpAction.Force)
            {
                Log($"hero still sits after {_sitPresses} get-up presses ({Combat.SitStateText}) -> {Combat.ForceGetUp()}");
                _sitPresses = 0;
                return;
            }
            _sitPresses++;
            string again = newSit ? "" : $" again ({_sitPresses})";
            // fix-64: with a shield in the left hand the get-up is the parade (RMB), as G3's NPCs answer an attacker
            bool shield = string.Equals(Combat.Fighter.Animator.OffHand, G3CombatMotions.Shield, StringComparison.OrdinalIgnoreCase);
            if (ScenarioPacing.GetUpWithParade(Combat.SecondaryHeld, shield))
            {
                Log("hero sat down -> get-up parade" + (shield ? " (shield)" : "") + again);
                bool blockHeld = Combat.SecondaryHeld;
                Combat.SecondaryDown(); // a new press edge; a held block stays held (the scenario releases it)
                if (!blockHeld) Combat.SecondaryUp();
                return;
            }
            Log("hero sat down -> get-up attack" + again);
            bool held = Combat.PrimaryHeld;
            Combat.PrimaryDown();
            if (!held) Combat.PrimaryUp();
        }

        void LateUpdate()
        {
            if (_focus == null || Hero == null) return;
            if (!TryActorPosition(_focus.Value, out var p, out var view))
            {
                _focus = null;
                return;
            }
            Face(p, 8f);
            var fighter = view != null ? view.GetComponent<CombatFighter>() : null;
            if (fighter != null && Combat != null && Combat.Fighter != null && Combat.Fighter.Animator != null && Combat.Fighter.Animator.Drawn && Combat.Focus != fighter)
                Combat.SetFocus(fighter);
        }

        /// <summary>
        /// A short damage immunity (a tp, a drop: the hero may land in a fight or a fall). fix-132: like 'god' it is only
        /// <see cref="Myrtana.Rules.ActorVitals.PlayerInvulnerable"/>, a damage filter; it never writes the HP attribute (it
        /// used to set 99999/99999, and from the dev console nothing put the real HP back).
        /// </summary>
        internal void GodFor(float seconds) => _godUntil = Mathf.Max(_godUntil, Time.time + seconds);

        /// <summary>The dev god / temporary immunity right now.</summary>
        internal bool HeroInvulnerable => _god || Time.time < _godUntil;

        Myrtana.Rules.ActorVitals _godVitals;

        /// <summary>Installs the damage filter on the session's vitals (once per session).</summary>
        void HookGod()
        {
            var v = _session.Combat.Vitals;
            if (ReferenceEquals(v, _godVitals)) return;
            _godVitals = v;
            v.PlayerInvulnerable = () => HeroInvulnerable;
        }

        /// <summary>fix-65 (R32-2): an op that sets the hero's HP (hp, ko, kill) ends god mode and the temporary god of a tp.</summary>
        void GodOff()
        {
            _god = false;
            _godUntil = 0;
        }

        void SetCalm(IEnumerable<ActorId> ids, bool freeze)
        {
            if (_paralysedBefore == null)
            {
                _paralysedBefore = _session.NpcCombat.Paralysed ?? (_ => false);
                var before = _paralysedBefore;
                _session.NpcCombat.Paralysed = id => _calm.Contains(id) || _frozen.Contains(id) || before(id);
                // frozen NPCs make no new plans: a FullStop alone was undone by the next routine tick (a walking NPC went on)
                _heldBefore = _session.Npcs.Held;
                var heldBefore = _heldBefore;
                _session.Npcs.Held = id => _frozen.Contains(id) || (heldBefore != null && heldBefore(id));
            }
            foreach (var id in ids)
            {
                _calm.Add(id);
                if (freeze) { _frozen.Add(id); _session.Npcs.FullStop(id); }
                _session.NpcCombat.Get(id)?.Disengage();
            }
        }

        void ClearCalm(bool frozenOnly)
        {
            if (frozenOnly)
            {
                foreach (var id in _frozen) _calm.Remove(id);
                _frozen.Clear();
            }
            else
            {
                _calm.Clear();
                _frozen.Clear();
            }
            if (_calm.Count == 0 && _frozen.Count == 0 && _paralysedBefore != null)
            {
                _session.NpcCombat.Paralysed = _paralysedBefore;
                _paralysedBefore = null;
                _session.Npcs.Held = _heldBefore;
                _heldBefore = null;
            }
        }

        public void ResetState()
        {
            _god = false;
            _godUntil = 0;
            _focus = null;
            ClearCalm(false);
            StopTouchWatch(log: true);
            if (_weatherForced) G3WeatherDirector.Force(null);
            _weatherForced = false;
            ReleaseInputs();
            RestoreLanguage();
        }

        void ReleaseInputs()
        {
            if (Hero != null) { Hero.scriptedMove = Vector2.zero; Hero.scriptedKeyTurn = 0f; }
            if (Combat != null)
            {
                Combat.PrimaryUp();
                Combat.SecondaryUp();
            }
        }

        // ---------------------------------------------------------------- shared helpers

        /// <summary>
        /// Scenario ops run with the invariant culture, so every number they print has a decimal point (a Russian system
        /// printed "12,5 s"); returns the culture to put back once the op yields.
        /// </summary>
        static CultureInfo EnterInvariant()
        {
            var before = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = Inv;
            return before;
        }

        internal static string F(float v) => v.ToString("0.0", Inv);
        internal static string F0(float v) => v.ToString("0", Inv);
        internal static string V(Vector3 v) => $"({F(v.x)},{F(v.y)},{F(v.z)})";

        internal static bool IsHero(string actor) =>
            string.IsNullOrEmpty(actor) || actor.Equals("hero", StringComparison.OrdinalIgnoreCase) || actor.Equals("player", StringComparison.OrdinalIgnoreCase);

        internal static ActorId Actor(string text)
        {
            if (IsHero(text)) return ActorId.Player;
            if (text.StartsWith("npc:", StringComparison.OrdinalIgnoreCase)) text = text.Substring(4);
            return new ActorId(text);
        }

        /// <summary>Position of an actor: its view if it has one, else the simulation pose.</summary>
        internal bool TryActorPosition(ActorId id, out Vector3 position, out Transform view)
        {
            view = null;
            position = default;
            if (id.IsPlayer)
            {
                if (Hero == null) return false;
                position = Hero.transform.position;
                view = Hero.transform;
                return true;
            }
            if (Views != null && Views.TryGetView(id, out view) && view != null)
            {
                position = view.position;
                return true;
            }
            if (_session.Npcs.TryGet(id, out var agent))
            {
                var pose = _session.Npcs.PoseOf(agent);
                position = new Vector3(pose.X, pose.Y, pose.Z);
                return true;
            }
            return TrySpeakerPosition(id, out position);
        }

        internal float ActorYaw(ActorId id, Transform view)
        {
            if (view != null) return view.eulerAngles.y;
            return _session.Npcs.TryGet(id, out var agent) ? _session.Npcs.PoseOf(agent).Yaw : 0f;
        }

        internal void Face(Vector3 target, float pitch = 5f)
        {
            var d = target - Hero.transform.position;
            if (d.sqrMagnitude < 1e-4f) return;
            Hero.SetLook(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, pitch);
        }

        internal CombatFighter FighterOf(ActorId id) =>
            Views != null && Views.TryGetView(id, out var v) && v != null ? v.GetComponent<CombatFighter>() : null;

        /// <summary>
        /// A target: "hero", "npc:Id" or an actor id (also a Speaker object name), "spot:ref" / "?tags" (spot catalogue),
        /// "place:Id", "zone:Name", "obj:Name" (story targets), "x,y,z" or "x,z".
        /// </summary>
        internal bool TryTarget(string text, out Vector3 position, out bool hasY, out ActorId? actor, out TestSpot spot, out string error)
        {
            position = default; hasY = true; actor = null; spot = null; error = null;
            if (string.IsNullOrEmpty(text)) { error = "no target"; return false; }
            if (ScenarioStep.TryVector(text, out var xyz))
            {
                hasY = xyz.Length == 3;
                position = hasY ? new Vector3(xyz[0], xyz[1], xyz[2]) : new Vector3(xyz[0], 0, xyz[1]);
                return true;
            }
            if (text.StartsWith("spot:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("?"))
            {
                string reference = text.StartsWith("?") ? text : text.Substring(5);
                var catalog = Scenario.SpotsCatalog;
                var hp = Hero != null ? Hero.transform.position : Vector3.zero;
                if (catalog == null || !catalog.TryResolve(reference, new[] { hp.x, hp.y, hp.z }, out spot, out var point))
                {
                    error = $"no spot '{reference}'";
                    return false;
                }
                position = new Vector3(point[0], point[1], point[2]);
                return true;
            }
            if (StoryScenario.TryParseTarget(text, out _, out _)) return TryStoryTarget(text, out position, out hasY, out error);
            var id = Actor(text);
            if (!TryActorPosition(id, out position, out _))
            {
                error = $"no actor '{text}'";
                return false;
            }
            actor = id;
            return true;
        }
    }
}
#endif
