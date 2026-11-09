#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Myrtana.Rules;
using Myrtana.Session.Dev;
using Myrtana.Simulation;
using Myrtana.Story;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// In-game dev console (Editor and development builds only; Docs/dev-scenarios.md "Dev console"): F10 or the key
    /// left of 1 (` / ё) toggles it. Every line is a scenario statement run by <see cref="Scenario.Exec"/> ("give меч",
    /// "spawn Wolf attitude=hostile", "tp npc:Gorn", "time 22", "god", "scenario fire_night"), plus help/ops/spots/list/
    /// status/stop/reset/clear. Tab completes ops and item/NPC/quest/skill/spell/scenario/spot names (each further Tab
    /// puts in the next full match, Shift+Tab the previous), Up/Down walk the history (kept in PlayerPrefs). The hero's controls are locked while it is open. Plain IMGUI: no assets.
    /// </summary>
    public sealed class DevConsole : MonoBehaviour
    {
        const string HistoryKey = "Myrtana.DevConsole.History";
        const int MaxOutput = 14;

        static DevConsole _instance;

        /// <summary>
        /// While open, the game does not see the keyboard (<see cref="G3.Unity.G3Input.HoldKeyboard"/>: game hotkeys such
        /// as inventory or quick save do not fire while typing). The device stays enabled, so IMGUI typing and the close
        /// keys keep working (fix-40: disabling the device under "Input System only" starved them). Set false to let the
        /// game see the keys.
        /// </summary>
        public static bool BlockGameKeys = true;
        bool _keyboardBlocked;
        int _toggleFrame = -1;

        bool _open, _focusField;

        /// <summary>The console is open (it takes the mouse; fix-87).</summary>
        public static bool IsOpen => _instance != null && _instance._open;
        string _line = "";
        readonly List<string> _output = new List<string>();
        readonly List<string> _history = new List<string>();
        int _historyAt = -1;
        List<string> _suggestions = new List<string>();
        readonly DevTabCycle _tab = new DevTabCycle();
        int _seenLines;
        string _lastStatus;
        DevCompletion _completion;
        GameBootstrap _boot;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Create()
        {
            if (_instance != null) return;
            var go = new GameObject("[DevConsole]") { hideFlags = HideFlags.DontSave };
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<DevConsole>();
        }

        void Awake()
        {
            try
            {
                var saved = PlayerPrefs.GetString(HistoryKey, "");
                if (saved.Length > 0) _history.AddRange(saved.Split('\n'));
            }
            catch (Exception) { }
            _completion = new DevCompletion { Values = Values, Names = Names };
        }

        void Update()
        {
            // open and close from the device in Update (not from IMGUI events): reliable under "Input System only"
            var kb = Keyboard.current;
            if (kb != null && Time.frameCount != _toggleFrame)
            {
                bool toggle = kb.f10Key.wasPressedThisFrame || kb.backquoteKey.wasPressedThisFrame;
                if (!_open && toggle && !global::G3.Unity.G3Input.KeyboardHeld) Toggle(true);
                else if (_open && (toggle || kb.escapeKey.wasPressedThisFrame)) Toggle(false);
            }
            if (!_open) return;
            // mirror the scenario runner's log lines and final status into the console
            var r = ScenarioRunner.Instance;
            if (r != null)
            {
                if (_seenLines > r.Lines.Count) _seenLines = 0;
                for (; _seenLines < r.Lines.Count; _seenLines++) Print(r.Lines[_seenLines]);
                if (r.StatusLine != _lastStatus && r.StatusLine.StartsWith("DONE"))
                    Print(r.StatusLine.Length > 300 ? r.StatusLine.Substring(0, 300) + "…" : r.StatusLine);
                _lastStatus = r.StatusLine;
            }
        }

        void Toggle(bool open)
        {
            _open = open;
            _focusField = open;
            _toggleFrame = Time.frameCount;
            if (_boot == null) _boot = FindAnyObjectByType<GameBootstrap>();
            var controls = _boot != null ? _boot.Controls : null;
            if (controls != null)
            {
                if (open) controls.Acquire(this);
                else controls.Release(this);
            }
            if (open && BlockGameKeys)
            {
                global::G3.Unity.G3Input.HoldKeyboard(this);
                _keyboardBlocked = true;
            }
            else if (!open && _keyboardBlocked)
            {
                global::G3.Unity.G3Input.ReleaseKeyboard(this); // the game ignores the keyboard for the rest of this frame
                _keyboardBlocked = false;
            }
            if (open && _output.Count == 0) Print("dev console: 'help' lists commands, Tab completes, F10/`/Esc closes");
        }

        void OnDisable()
        {
            if (_open) Toggle(false);
        }

        void Print(string text)
        {
            foreach (var l in (text ?? "").Split('\n')) _output.Add(l);
            while (_output.Count > 200) _output.RemoveAt(0);
        }

        void OnGUI()
        {
            if (!_open) return;
            var e = Event.current;
            // the toggle key's character must not land in the text field
            if (e.type == EventType.KeyDown && (e.character == '`' || e.character == 'ё' || e.character == 'Ё' || e.keyCode == KeyCode.BackQuote || e.keyCode == KeyCode.F10 || e.keyCode == KeyCode.Escape))
            {
                // Update closes from the device; here the key and its character are only kept out of the text field.
                // Fallback: a close key that reached IMGUI but not the device (focus quirks) still closes.
                if (e.keyCode != KeyCode.None && Time.frameCount != _toggleFrame) Toggle(false);
                e.Use();
                return;
            }
            if (e.type == EventType.KeyDown) HandleKey(e);
            if (e.type == EventType.KeyDown && e.character == '\t') e.Use(); // Tab's character event: no tab in the line

            // fix-128 (owner: the 'p' of the hint line was cut off at the bottom): every line as tall as its style's
            // font line plus its padding and room for descenders, not a fixed 18 px
            var label = GUI.skin.label;
            var field = GUI.skin.textField;
            float w = Screen.width;
            float lineH = Mathf.Max(22f, Mathf.Ceil(label.lineHeight + label.padding.vertical + 5f));
            float fieldH = Mathf.Max(lineH + 4f, Mathf.Ceil(field.lineHeight + field.padding.vertical + 7f));
            float h = lineH * MaxOutput + fieldH + 20;
            GUI.Box(new Rect(0, 0, w, h + (_suggestions.Count > 0 ? lineH + 4 : 0)), GUIContent.none);
            // mouse close: works even when the keyboard device is blocked for the game (owner: keys did not close it)
            if (GUI.Button(new Rect(w - 34, 4, 28, 22), "X")) { Toggle(false); return; }
            int first = Math.Max(0, _output.Count - MaxOutput);
            for (int i = first; i < _output.Count; i++)
                GUI.Label(new Rect(8, 4 + (i - first) * lineH, w - 16, lineH), _output[i]);
            GUI.SetNextControlName("DevConsoleInput");
            _line = GUI.TextField(new Rect(8, h - fieldH - 8, w - 16, fieldH), _line ?? "");
            if (_suggestions.Count > 0)
                GUI.Label(new Rect(8, h, w - 16, lineH), string.Join("   ", _suggestions.Take(12)) + (_suggestions.Count > 12 ? "   …" : ""));
            if (_focusField)
            {
                GUI.FocusControl("DevConsoleInput");
                _focusField = false;
            }
        }

        void HandleKey(Event e)
        {
            switch (e.keyCode)
            {
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    Submit(_line);
                    _line = "";
                    _suggestions.Clear();
                    e.Use();
                    break;
                case KeyCode.Tab:
                    Complete(e.shift);
                    e.Use();
                    break;
                case KeyCode.UpArrow:
                    if (_history.Count > 0)
                    {
                        _historyAt = _historyAt < 0 ? _history.Count - 1 : Math.Max(0, _historyAt - 1);
                        _line = _history[_historyAt];
                        MoveCursorToEnd();
                    }
                    e.Use();
                    break;
                case KeyCode.DownArrow:
                    if (_historyAt >= 0)
                    {
                        _historyAt++;
                        _line = _historyAt < _history.Count ? _history[_historyAt] : "";
                        if (_historyAt >= _history.Count) _historyAt = -1;
                        MoveCursorToEnd();
                    }
                    e.Use();
                    break;
            }
        }

        void MoveCursorToEnd()
        {
            var editor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl);
            if (editor != null)
            {
                editor.text = _line;
                editor.MoveTextEnd();
            }
        }

        void Complete(bool backwards)
        {
            _line = _tab.Tab(_line, l => _completion.Complete(l), backwards);
            var m = _tab.Matches;
            // the list starts at the current match so it stays in view while cycling
            _suggestions = (m.Count <= 1 ? m.ToList() : m.Skip(_tab.Index).Concat(m.Take(_tab.Index)).ToList()).Select(_completion.Label).ToList();
            MoveCursorToEnd();
        }

        void Submit(string line)
        {
            line = (line ?? "").Trim();
            if (line.Length == 0) return;
            Print("> " + line);
            if (_history.Count == 0 || _history[_history.Count - 1] != line) _history.Add(line);
            while (_history.Count > 100) _history.RemoveAt(0);
            _historyAt = -1;
            try { PlayerPrefs.SetString(HistoryKey, string.Join("\n", _history)); } catch (Exception) { }

            var words = line.Split(new[] { ' ' }, 2);
            string head = words[0].ToLowerInvariant(), rest = words.Length > 1 ? words[1].Trim() : null;
            switch (head)
            {
                case "help":
                case "ops":
                    Print(rest != null ? Scenario.Ops(rest) : "ops: " + string.Join(" ", ScenarioOps.All.Select(o => o.Name)) +
                          "\nconsole: help <op>, spots [?tag], list, status, stop, reset, clear, combatlog on|off, fx [top]; several ops: a; b; c" +
                          "\nnoclip [on|off] [speed=] (alias fly): fly through walls, WASD + Space up / Ctrl down, Shift fast; off lands on the floor below" +
                          "\nenemies [on|off|list|fix|kill] (alias threats): who keeps the hero in combat (save block, fight music, party fights), markers through walls, fix = onto a free floor, kill");
                    return;
                case "clear": _output.Clear(); return;
                case "combatlog":
                    // the hero's melee clicks: queued / started / refused and when each combo step started (CombatInputLog)
                    bool on = rest == null ? !CombatInputLog.Enabled : rest.Equals("on", StringComparison.OrdinalIgnoreCase) || rest == "1";
                    Print(CombatInputLog.Set(on));
                    return;
                case "fx":
                    // fix-58: the live effects by name (who holds the part budget)
                    Print(G3.Unity.G3EffectSystem.ReportOrNone(int.TryParse(rest, out int fxTop) ? fxTop : 30));
                    return;
                case "spots": Print(Scenario.Spots(rest)); return;
                case "list": Print(Scenario.List()); return;
                case "status": Print(Scenario.Status); return;
                case "stop": Print(Scenario.Stop()); return;
                case "reset": Print(Scenario.Reset()); return;
                case "scenario":
                case "run":
                    if (rest != null && !rest.Contains(" ") && Scenario.ScenarioFilePath(rest) != null) { Print(Scenario.Run(rest)); return; }
                    break;
            }
            string started = Scenario.Exec(line, 600f, looseNames: true);
            if (!started.StartsWith("started")) Print(started);
            _seenLines = 0; // the run cleared the runner's lines
        }

        /// <summary>Completion value lists from the loaded content (session needed for items/NPCs).</summary>
        IEnumerable<string> Values(string kind)
        {
            if (_boot == null) _boot = FindAnyObjectByType<GameBootstrap>();
            var s = _boot != null ? _boot.Session : null;
            var db = s?.Content.Database;
            switch (kind)
            {
                case "op": return ScenarioOps.All.Select(o => o.Name);
                case "item": return db != null ? db.All<ItemDef>().Select(i => i.Id) : Enumerable.Empty<string>();
                case "spell": return db != null ? db.All<ItemDef>().Select(i => i.Id).Where(i => i.StartsWith("It_Spell_") || i.StartsWith("It_Scroll_")) : Enumerable.Empty<string>();
                case "skill": return db != null ? db.All<SkillDef>().Select(k => k.Id) : Enumerable.Empty<string>();
                case "quest": return db != null ? db.All<QuestDef>().Select(q => q.Id.ToString()) : Enumerable.Empty<string>();
                case "npc": return db != null ? DevLookup.TemplateNames(db.All<NpcDef>().Select(n => n.Id.Name)) : Enumerable.Empty<string>();
                case "target":
                    return (db != null ? DevLookup.TemplateNames(db.All<NpcDef>().Select(n => n.Id.Name)).Select(n => "npc:" + n) : Enumerable.Empty<string>())
                        .Concat(SpotIds().Select(i => "spot:" + i));
                case "spot": return SpotIds();
                case "scenario":
                    var dir = Scenario.DataDir;
                    return dir == null ? Enumerable.Empty<string>() : Directory.GetFiles(dir, "*.json").Select(Path.GetFileNameWithoutExtension).Where(n => !n.StartsWith("spots"));
                case "weather": return new[] { "Sunny", "Rainy", "Foggy", "Snowy" };
                default: return Enumerable.Empty<string>();
            }
        }

        /// <summary>Localized names (Russian, English) of items, spells and NPC templates for Tab completion ("give меч").</summary>
        IEnumerable<(string id, string name)> Names(string kind)
        {
            if (_boot == null) _boot = FindAnyObjectByType<GameBootstrap>();
            var s = _boot != null ? _boot.Session : null;
            var db = s?.Content.Database;
            if (db == null) return Enumerable.Empty<(string, string)>();
            switch (kind)
            {
                case "item": return db.All<ItemDef>().SelectMany(i => DevLookup.Names(i.Id, i.Name, s.Localizer));
                case "spell":
                    return db.All<ItemDef>().Where(i => i.Id.StartsWith("It_Spell_") || i.Id.StartsWith("It_Scroll_"))
                             .SelectMany(i => DevLookup.Names(i.Id, i.Name, s.Localizer));
                case "npc":
                    return db.All<NpcDef>().SelectMany(n => DevLookup.Names(DevLookup.TemplateNames(new[] { n.Id.Name }).First(), n.DisplayName, s.Localizer));
                default: return Enumerable.Empty<(string, string)>();
            }
        }

        static IEnumerable<string> SpotIds() => Scenario.SpotsCatalog?.Spots.Select(x => x.Id) ?? Enumerable.Empty<string>();
    }
}
#endif
