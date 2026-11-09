#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Myrtana.Session;
using Myrtana.Session.Dev;
using UnityEngine;

namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// Dev-only scenario toolkit for editor runners (Docs/dev-scenarios.md). Compiled into the Editor and development
    /// builds only. Entry points (all return one short line, so they fit `Tools/eval_job.sh` / `unity command eval`):
    /// <list type="bullet">
    /// <item><c>Scenario.Run("bloodfly_water")</c> — runs Tools/Scenarios/bloodfly_water.json in the background;</item>
    /// <item><c>Scenario.Exec("safe; god; tp npc:Gorn dist=2; talk Gorn")</c> — the same ops written inline;</item>
    /// <item><c>Scenario.Status</c> — "running 3/12 tp ..." or the final "DONE ok ..." / "DONE FAIL ..." line;</item>
    /// <item><c>Scenario.Ops()</c>, <c>Scenario.List()</c>, <c>Scenario.Spots("?water")</c> — help and catalogues.</item>
    /// </list>
    /// Data lives outside Assets in Tools/Scenarios (scenario files, spots.json from `G3Cli spots`, spots.manual.json).
    /// A development player looks in &lt;build&gt;/Scenarios or the -scenarios &lt;dir&gt; command-line argument.
    /// </summary>
    public static class Scenario
    {
        /// <summary>Prefix of every log line the toolkit writes (grep Logs/Editor.log for it).</summary>
        public const string Tag = "[scn] ";

        static SpotCatalog _spots;
        static DateTime _spotsStamp;

        // ---------------------------------------------------------------- entry points

        /// <summary>Starts the named scenario file; returns "started ..." or the parse errors.</summary>
        /// <param name="from">1-based step to start at (fix-54, <c>scenario.sh run name --from N</c>); the $variables of the
        /// skipped steps come from the progress file of an earlier run when it has them.</param>
        /// <param name="loadout">A <see cref="ScenarioLoadout"/> applied first (fix-54, <c>--loadout</c>).</param>
        /// <param name="checkpointMinutes">Checkpoint saves every that many minutes and after a pass (fix-54); negative = the
        /// scenario file's "checkpoint" (default none).</param>
        public static string Run(string name, int from = 1, string loadout = null, float checkpointMinutes = -1f)
        {
            string path = ScenarioFile(name);
            if (path == null) return $"no scenario '{name}' in {DataDir ?? "(no data dir)"}; have: {List()}";
            var spec = ScenarioParser.ParseJson(File.ReadAllText(path), name);
            if (!spec.Ok) return "errors: " + string.Join(" | ", spec.Errors);
            if (!ScenarioCheckpoints.TryStartIndex(from, spec.Steps.Count, out int index, out var error)) return "errors: " + error;
            var opt = new RunOptions
            {
                StartIndex = index, Loadout = string.IsNullOrEmpty(loadout) ? null : loadout, Track = true,
                CheckpointMinutes = checkpointMinutes >= 0f ? checkpointMinutes : (float?)null,
            };
            if (index > 0)
            {
                var before = ScenarioRunner.ReadProgress(spec.Name);
                if (before?.Vars != null && before.Vars.Count > 0) opt.Vars = before.Vars;
            }
            return Start(spec, opt);
        }

        /// <summary>
        /// fix-54: continues a scenario run from its last checkpoint (Work/scenario-runs/&lt;name&gt;.json): loads the checkpoint
        /// slot, puts back the $variables and the dev state (god, calm, freeze), the loadout's attack style, and runs from the
        /// step after the save. After an editor crash: enter Play first (<c>scenario.sh resume name</c> does).
        /// </summary>
        public static string Resume(string name, float checkpointMinutes = -1f)
        {
            string path = ScenarioFile(name);
            if (path == null) return $"no scenario '{name}'";
            var progress = ScenarioRunner.ReadProgress(name);
            if (progress == null || !progress.CanResume)
                return $"no checkpoint for {name} ({(progress == null ? "no progress file" : $"state {progress.State}, step {progress.Step}/{progress.Steps}")}): run it again";
            var spec = ScenarioParser.ParseJson(File.ReadAllText(path), name);
            if (!spec.Ok) return "errors: " + string.Join(" | ", spec.Errors);
            if (!ScenarioCheckpoints.TryStartIndex(progress.ResumeStep, spec.Steps.Count, out int index, out var error)) return "errors: " + error;
            return Start(spec, new RunOptions
            {
                StartIndex = index, Vars = progress.Vars, LoadSlot = progress.Slot, Loadout = progress.Loadout, Resumed = true, Track = true,
                CheckpointMinutes = checkpointMinutes >= 0f ? checkpointMinutes : (float?)null,
            });
        }

        /// <summary>fix-54: the progress of a scenario's last run ("state step/steps slot resume=N saved ...") or "none".</summary>
        public static string Progress(string name)
        {
            var p = ScenarioRunner.ReadProgress(name);
            if (p == null) return "none";
            return $"{p.Scenario} {p.State} step {p.Step}/{p.Steps}" + (p.Loadout != null ? $" loadout {p.Loadout}" : "") +
                   (p.Slot != null ? $" slot {p.Slot}" + (p.ResumeStep > 0 ? $" resume={p.ResumeStep} saved {p.SavedUtc}" : "") : " no checkpoint") +
                   $" updated {p.UpdatedUtc}";
        }

        /// <summary>fix-54: is a dialog open now, and who opened it (one line).</summary>
        public static string Dialog() => ScenarioRunner.Ensure()?.DialogNow() ?? "no session";

        /// <summary>Runs inline ops ("safe; tp npc:Gorn; talk Gorn"); returns "started ..." or the parse errors.</summary>
        /// <param name="looseNames">The dev console: a name that matches several items takes the first (scenario files fail).</param>
        public static string Exec(string ops, float timeoutSeconds = 120f, bool looseNames = false)
        {
            var spec = ScenarioParser.ParseInline(ops);
            spec.TimeoutSeconds = timeoutSeconds;
            spec.LooseNames = looseNames;
            return Start(spec);
        }

        /// <summary>Checks a scenario file or inline text without running it.</summary>
        public static string Check(string nameOrOps)
        {
            string path = ScenarioFile(nameOrOps);
            var spec = path != null ? ScenarioParser.ParseJson(File.ReadAllText(path), nameOrOps) : ScenarioParser.ParseInline(nameOrOps);
            return spec.Ok ? $"ok {spec.Name}: {spec.Steps.Count} steps" : "errors: " + string.Join(" | ", spec.Errors);
        }

        /// <summary>The running scenario's progress, or the result line of the last one.</summary>
        public static string Status => ScenarioRunner.Instance != null ? ScenarioRunner.Instance.StatusLine : "idle";

        /// <summary>
        /// fix-57: what <c>scenario.sh</c> polls: the status line, for a running scenario with "| quiet N s" (seconds since its
        /// step advanced, it logged or the hero moved 2 m), or "STALLED after N s without progress: …" once quiet for
        /// <paramref name="stallMinutes"/> (the shell then stops it). The suite waits for DONE however long a run takes.
        /// </summary>
        public static string Heartbeat(float stallMinutes = ScenarioWatchdog.DefaultStallMinutes) =>
            ScenarioRunner.Instance != null ? ScenarioRunner.Instance.Heartbeat(stallMinutes) : "idle";

        /// <summary>All log lines of the last/current run (short).</summary>
        public static string Report => ScenarioRunner.Instance != null ? string.Join("\n", ScenarioRunner.Instance.Lines) : "";

        /// <summary>Stops the running scenario and releases held inputs (calm/freeze/god stay until 'Reset').</summary>
        public static string Stop()
        {
            var r = ScenarioRunner.Instance;
            if (r == null) return "idle";
            r.StopRun("stopped");
            return "stopped";
        }

        /// <summary>Clears calm/freeze/god/focus/weather override.</summary>
        public static string Reset()
        {
            var r = ScenarioRunner.Instance;
            if (r != null) r.ResetState();
            return "reset";
        }

        /// <summary>Scenario files available.</summary>
        public static string List()
        {
            var dir = DataDir;
            if (dir == null) return "(no Tools/Scenarios)";
            var names = Directory.GetFiles(dir, "*.json").Select(Path.GetFileNameWithoutExtension)
                                 .Where(n => !n.StartsWith("spots", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n, StringComparer.Ordinal);
            return string.Join(", ", names);
        }

        /// <summary>Op usage lines (optionally one op).</summary>
        public static string Ops(string op = null)
        {
            if (op != null && ScenarioOps.TryGet(op, out var spec)) op = spec.Name; // an alias (fly, teleport, heal): its op
            return string.Join("\n", ScenarioOps.All.Where(o => op == null || string.Equals(o.Name, op, StringComparison.OrdinalIgnoreCase)).Select(o => o.Usage));
        }

        /// <summary>Spots matching a reference ("?water+deep", "deep_water_bloodfly"), nearest to the hero first; max lines.</summary>
        public static string Spots(string query = null, int max = 12)
        {
            var c = SpotsCatalog;
            if (c == null) return "no spots.json";
            var q = SpotQuery.Parse(query, out _);
            var hero = ScenarioRunner.HeroPosition();
            if (hero.HasValue && q.Id == null) q.Near = new[] { hero.Value.x, hero.Value.y, hero.Value.z };
            return string.Join("\n", c.Select(q).Take(max).Select(s =>
                $"{s.Id} [{s.Kind}{(s.Tags.Count > 0 ? " " + string.Join(",", s.Tags) : "")}] {SpotCatalog.Format(s.Pos)}" +
                (s.Npc != null ? " npc=" + s.Npc : "") + (s.Note != null ? " — " + s.Note : "")));
        }

        // ---------------------------------------------------------------- composable calls (same ops, one at a time)

        /// <summary>Runs one op now (as a one-step scenario); e.g. Scenario.Do("tp npc:Gorn dist=2").</summary>
        public static string Do(string op) => Exec(op, 60f);

        public static string Teleport(string target) => Do("tp " + Quote(target));
        public static string SetTime(float hour) => Do("time " + hour.ToString(System.Globalization.CultureInfo.InvariantCulture));
        public static string SetWeather(string state) => Do("weather " + Quote(state ?? ""));
        public static string Spawn(string template, string attitude = "hostile", float dist = 6f) =>
            Do($"spawn {Quote(template)} attitude={attitude} dist={dist.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        public static string Calm(float radius = 40f) => Do("calm " + radius.ToString(System.Globalization.CultureInfo.InvariantCulture));
        public static string God(bool on = true) => Do("god " + (on ? "on" : "off"));
        public static string Probe(string actor = "hero") => ScenarioRunner.Ensure()?.ProbeNow(actor) ?? "no session";
        public static string Shot(string name, string look = null) => Do("shot " + Quote(name) + (look != null ? " look=" + Quote(look) : ""));

        /// <summary>Evaluates a condition now ("swimming", "state:Grok=Dead") → "true (actual)" / "false (actual)".</summary>
        public static string Test(string condition) => ScenarioRunner.Ensure()?.TestNow(condition) ?? "no session";

        static string Quote(string s) => s.IndexOf(' ') >= 0 ? "\"" + s + "\"" : s;

        // ---------------------------------------------------------------- data

        static string Start(ScenarioSpec spec, RunOptions options = null)
        {
            if (!spec.Ok) return "errors: " + string.Join(" | ", spec.Errors);
            var runner = ScenarioRunner.Ensure();
            if (runner == null) return "no session (enter Play and wait for the loading hold)";
            return runner.StartRun(spec, options);
        }

        internal static SpotCatalog SpotsCatalog
        {
            get
            {
                var dir = DataDir;
                if (dir == null) return _spots;
                var files = new[] { "spots.json", "spots.manual.json" }.Select(f => Path.Combine(dir, f)).Where(File.Exists).ToList();
                var stamp = files.Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
                if (_spots != null && stamp == _spotsStamp) return _spots;
                var c = new SpotCatalog();
                foreach (var f in files)
                {
                    try { c.Merge(SpotCatalog.Parse(File.ReadAllText(f))); }
                    catch (Exception e) { Debug.LogWarning(Tag + "spots file " + f + ": " + e.Message); }
                }
                _spots = c;
                _spotsStamp = stamp;
                return c;
            }
        }

        internal static string ScenarioFilePath(string name) => ScenarioFile(name);

        static string ScenarioFile(string name)
        {
            var dir = DataDir;
            if (dir == null || string.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[] { ';', ' ', '\n' }) >= 0) return null;
            string path = Path.Combine(dir, name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? name : name + ".json");
            return File.Exists(path) ? path : null;
        }

        /// <summary>Project root (the folder holding Assets) in the Editor, the build folder in a player.</summary>
        internal static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);

        internal static string DataDir
        {
            get
            {
                var args = Environment.GetCommandLineArgs();
                int i = Array.IndexOf(args, "-scenarios");
                if (i >= 0 && i + 1 < args.Length && Directory.Exists(args[i + 1])) return args[i + 1];
                foreach (var d in new[] { Path.Combine(ProjectRoot, "Tools", "Scenarios"), Path.Combine(ProjectRoot, "Scenarios") })
                    if (Directory.Exists(d)) return d;
                return null;
            }
        }
    }
}
#endif
