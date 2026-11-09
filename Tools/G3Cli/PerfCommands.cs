using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Myrtana.Core;
using Myrtana.Session;
using Myrtana.Simulation;

static partial class Program
{
    /// <summary>
    /// <c>perf tick [--pack dir] [--at x,y,z] [--frames n] [--active r] [--view r]</c>: a headless session at a town (default
    /// Faring's dev start), the NPCs around it Active/Near as NpcViewManager makes them, then <paramref name="args"/> frames of
    /// <see cref="GameSession.Tick"/> at 60 fps plus the per-frame session queries the NPC views and the perception scanner
    /// make. Prints ms per frame and bytes allocated per frame for each part (perf-9). Times are .NET (CoreCLR) times: the
    /// editor's Mono runs the same code ~2-4x slower.
    /// </summary>
    static int PerfCommand(string data, string[] args)
    {
        if (args.Length < 2 || args[1] != "tick")
        {
            Console.WriteLine("usage: perf tick [--pack dir] [--at x,y,z] [--frames n] [--fps f] [--active r] [--view r] [--detail] [--nav] [--alloc]");
            return 1;
        }
        string pack = Option(args, "--pack") ?? DefaultPackDir;
        var at = (Option(args, "--at") ?? "568,67.45,576").Split(',').Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        int frames = int.Parse(Option(args, "--frames") ?? "1200");
        float activeRadius = float.Parse(Option(args, "--active") ?? "70", System.Globalization.CultureInfo.InvariantCulture);
        float viewRadius = float.Parse(Option(args, "--view") ?? "160", System.Globalization.CultureInfo.InvariantCulture);

        var clock = Stopwatch.StartNew();
        var content = GameContent.Load(new IContentSource[] { new DirectoryContentSource(pack) });
        var session = GameSession.NewGame(content, new SessionPorts(new Myrtana.Session.Dev.ChapterDryRun.SilentPresenter(), null),
                                          new GameSettings(new LanguageSettings("English", "English"), randomSeed: 1));
        Console.WriteLine($"session {clock.Elapsed.TotalSeconds:0.0} s, {session.Npcs.Agents.Count} agents");
        var player = new WorldPose(at[0], at[1], at[2], 0);

        // NpcViewManager.Evaluate: Active within 70 m, Near within 160 m.
        var shown = new List<NpcAgent>();
        foreach (var agent in session.Npcs.Agents)
        {
            if (!session.Npcs.IsInWorld(agent)) continue;
            var pose = session.Npcs.PoseOf(agent);
            float d = pose.HorizontalDistanceTo(player);
            if (d > viewRadius) continue;
            session.Npcs.SetDetail(agent.Id, d <= activeRadius ? DetailLevel.Active : DetailLevel.Near);
            shown.Add(agent);
        }
        Console.WriteLine($"shown {shown.Count} ({shown.Count(a => a.Detail == DetailLevel.Active)} active)");

        var lines = new CountingLineOfSight();
        var scanner = new PerceptionScanner(session, () => 0.5) { LineOfSight = lines };
        var views = new List<PerceptionScanner.ViewInput>();
        double scanNow = session.PlaySeconds;
        float dt = 1f / float.Parse(Option(args, "--fps") ?? "60", System.Globalization.CultureInfo.InvariantCulture);
        var parts = new (string name, Action run)[]
        {
            ("Session.Tick", () =>
            {
                session.Crime.ReportPlayer(player);
                session.Tick(dt);
            }),
            ("  Npcs.Tick", () => session.Npcs.Tick()),
            ("    engaged only (budget 0)", () =>
            {
                int budget = session.Npcs.Settings.AbstractBudget;
                session.Npcs.Settings.AbstractBudget = 0;
                session.Npcs.Tick();
                session.Npcs.Settings.AbstractBudget = budget;
            }),
            ("  Life.Tick", () => session.Life.Tick()),
            ("  Vitals.Tick", () => session.Combat.Vitals.Tick(dt)),
            ("  Regeneration.Tick", () => session.Regeneration.Tick(dt)),
            ("  Crime.Tick", () => session.Crime.Tick()),
            ("  WorldState.Tick", () => session.WorldState.Tick(dt)),
            ("  Dialog.Tick", () => session.Dialog.Tick(dt)),
            ("view queries (per shown NPC)", () =>
            {
                foreach (var a in shown)
                {
                    session.Npcs.PoseOf(a);
                    session.Npcs.InteractionOf(a);
                    session.Npcs.SmalltalkPartnerOf(a);
                    session.Combat.Vitals.StateOf(a.Id);
                    session.NpcCombat.Get(a.Id);
                    session.Life.Tasks.TryGet(a.Id, out _);
                }
            }),
            ("PerceptionScanner.Frame", () =>
            {
                views.Clear();
                foreach (var a in shown) views.Add(new PerceptionScanner.ViewInput(a.Id, session.Npcs.PoseOf(a), 1, false));
                scanNow += dt; // the session's clock stands while this part runs alone
                scanner.Frame(PlayerSense(player), views, scanNow);
            }),
        };
        if (args.Contains("--nav"))
        {
            var navDefs = content.Database.All<NavSectorDef>().ToList();
            var graph = new NavGraph(navDefs, session.WorldState.Sectors.IsEnabled);
            // routine walks of the shown NPCs longer than NpcView.RoutineRouteMetres (40 m) go on the G3 route
            var walks = new List<(WorldPose from, WorldPose to)>();
            foreach (var a in session.Npcs.Agents)
            {
                if (walks.Count >= 200) break;
                var from = session.Npcs.PoseOf(a);
                if (from.HorizontalDistanceTo(player) > 300 || !session.Npcs.TryGetTarget(a, out var to)) continue;
                if (from.HorizontalDistanceTo(to) > 40f) walks.Add((from, to));
                else if (a.Routine != null && session.Npcs.Points.TryGetPose(a.Station, out var st) && from.HorizontalDistanceTo(st) > 40f) walks.Add((from, st));
            }
            // and a ring of far walks across the town
            for (int i = 0; i < 60; i++)
            {
                double ang = i * 0.7;
                walks.Add((new WorldPose(player.X + (float)Math.Cos(ang) * 60, player.Y, player.Z + (float)Math.Sin(ang) * 60, 0),
                           new WorldPose(player.X - (float)Math.Cos(ang) * 80, player.Y, player.Z - (float)Math.Sin(ang) * 80, 0)));
            }
            var navAlloc = args.Contains("--alloc") ? new AllocationTypes() : null;
            for (int pass = 0; pass < 2; pass++)
            {
                navAlloc?.TakeSummary();
                long b0 = GC.GetAllocatedBytesForCurrentThread();
                var times = new List<double>();
                int found = 0;
                foreach (var (f, t) in walks)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    var r = graph.FindRoute(f.X, f.Y, f.Z, t.X, t.Y, t.Z);
                    times.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                    if (r.Found) found++;
                }
                times.Sort();
                Console.WriteLine($"FindRoute x{walks.Count} ({found} found): mean {times.Average():0.000} p50 {times[times.Count / 2]:0.000} max {times[^1]:0.000} ms, {(GC.GetAllocatedBytesForCurrentThread() - b0) / walks.Count} B/route");
                if (navAlloc != null) Console.WriteLine("    allocated: " + navAlloc.TakeSummary());
            }
        }
        AllocationTypes allocTypes = args.Contains("--alloc") ? new AllocationTypes() : null;
        // warm-up (JIT, first scans spread over one interval)
        for (int i = 0; i < 120; i++) foreach (var p in parts) p.run();
        if (args.Contains("--detail"))
        {
            var sim = session.Npcs;
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            Delegate Private(string name, Type type) => Delegate.CreateDelegate(type, sim, typeof(NpcSimulation).GetMethod(name, flags));
            var stepParty = (Func<NpcAgent, double, bool>)Private("StepParty", typeof(Func<NpcAgent, double, bool>));
            var stepAtStation = (Func<NpcAgent, double, bool>)Private("StepAtStation", typeof(Func<NpcAgent, double, bool>));
            var phaseOf = (Func<NpcAgent, DayPhase>)Private("PhaseOf", typeof(Func<NpcAgent, DayPhase>));
            var engaged = shown.Where(a => a.Detail != DetailLevel.Abstract).ToList();
            double now = session.Clock.GameSeconds;
            var detail = new (string, Action<NpcAgent>)[]
            {
                ("EscortOf", a => sim.EscortOf(a.Id)),
                ("StepParty", a => stepParty(a, now)),
                ("ArenaPointOf", a => sim.ArenaPointOf?.Invoke(a.Id)),
                ("PhaseOf", a => phaseOf(a)),
                ("StepAtStation", a => { if (a.Activity == NpcActivity.AtStation) stepAtStation(a, now); }),
                ("IsInWorld", a => sim.IsInWorld(a)),
                ("Attitudes.Of x neighbours", a =>
                {
                    var pa = sim.PoseOf(a);
                    foreach (var b in engaged)
                        if (b != a && sim.PoseOf(b).HorizontalDistanceTo(pa) <= 20f) session.Crime.Attitudes.Of(a.Id, b.Id);
                }),
                ("PoseOf x engaged", a =>
                {
                    var pa = sim.PoseOf(a);
                    foreach (var b in engaged) sim.PoseOf(b).HorizontalDistanceTo(pa);
                }),
                ("TryGetProfile", a => session.Crime.Attitudes.Facts.TryGetProfile(a.Id, out _)),
            };
            var abstracts = sim.Agents.Where(a => a.Detail == DetailLevel.Abstract).Take(1000).ToList();
            var process = (Action<NpcAgent, double>)Private("Process", typeof(Action<NpcAgent, double>));
            detail = detail.Append(("Process (copy)", a => process(a, now))).ToArray();
            for (int pass = 0; pass < 2; pass++) foreach (var (name, run) in detail)
            {
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 300; i++) foreach (var a in engaged) run(a);
                double e = sw.Elapsed.TotalMilliseconds / 300 / engaged.Count * 1000;
                sw.Restart();
                if (!name.StartsWith("Attitudes") && !name.StartsWith("PoseOf x"))
                    for (int i = 0; i < 30; i++) foreach (var a in abstracts) run(a);
                double ab = sw.Elapsed.TotalMilliseconds / 30 / abstracts.Count * 1000;
                Console.WriteLine($"  {name,-26} {e,8:0.000} us/engaged NPC  {ab,8:0.000} us/abstract NPC");
            }
            {
                var buckets = new Dictionary<string, (int n, double ms)>();
                double now2 = session.Clock.GameSeconds;
                foreach (var a in sim.Agents.Where(x => x.Detail == DetailLevel.Abstract).ToList())
                {
                    string key = (a.Routine == null ? "noRoutine " : "routine ") + a.Activity + (now2 < a.RetryAt ? " retry" : "");
                    long t0 = Stopwatch.GetTimestamp();
                    process(a, now2);
                    double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    buckets.TryGetValue(key, out var b);
                    buckets[key] = (b.n + 1, b.ms + ms);
                }
                foreach (var kv in buckets.OrderByDescending(kv => kv.Value.ms))
                    Console.WriteLine($"  abstract {kv.Key,-36} n {kv.Value.n,5}  {kv.Value.ms * 1000 / kv.Value.n,7:0.00} us each, {kv.Value.ms:0.00} ms total");
            }
            int pairs = engaged.Sum(a => engaged.Count(b => b != a && sim.PoseOf(b).HorizontalDistanceTo(sim.PoseOf(a)) <= 20f));
            Console.WriteLine($"neighbour pairs within 20 m: {pairs} ({pairs / (double)engaged.Count:0.0} per NPC)");
            Console.WriteLine("activities: " + string.Join(", ", engaged.GroupBy(a => a.Activity).Select(g => g.Key + " " + g.Count())));
        }
        foreach (var (name, run) in parts)
        {
            long bytes0 = GC.GetAllocatedBytesForCurrentThread();
            var times = new double[frames];
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < frames; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                run();
                times[i] = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            }
            sw.Stop();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - bytes0;
            Array.Sort(times);
            Console.WriteLine($"{name,-32} mean {sw.Elapsed.TotalMilliseconds / frames,7:0.000}  p50 {times[frames / 2],7:0.000}  p90 {times[frames * 9 / 10],7:0.000} ms  {bytes / frames,6} B/frame");
            if (allocTypes != null) Console.WriteLine("    allocated: " + allocTypes.TakeSummary());
            if (args.Contains("--digest"))
            {
                // The simulation's state after this part, to compare two builds (same content, seeded randomness).
                long digest = 17;
                foreach (var a in session.Npcs.Agents)
                {
                    var pose = session.Npcs.PoseOf(a);
                    digest = digest * 31 + (a.Station.Point.Name ?? "").Length * 7 + a.Station.Slot;
                    digest = digest * 31 + (int)a.Activity;
                    digest = digest * 31 + (long)Math.Round(pose.X * 10) + (long)Math.Round(pose.Z * 10) * 7919;
                }
                Console.WriteLine($"    digest {digest:X16}; shown: " + string.Join(", ", shown.GroupBy(a => a.Activity).OrderBy(g => g.Key).Select(g => g.Key + " " + g.Count())));
            }
        }
        Console.WriteLine($"scans/frame {scanner.Stats.Scans / (double)Math.Max(1, scanner.Stats.Frames):0.00}, candidates/scan {scanner.Stats.Candidates / (double)Math.Max(1, scanner.Stats.Scans):0.0}, line-of-sight checks/scan {lines.Count / (double)Math.Max(1, scanner.Stats.Scans):0.00}");
        return 0;
    }

    /// <summary>The runtime's allocation samples (one per ~100 KB) by type name.</summary>
    sealed class AllocationTypes : System.Diagnostics.Tracing.EventListener
    {
        readonly Dictionary<string, int> _types = new Dictionary<string, int>();

        protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)0x1);
        }

        protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
        {
            if (e.EventName == null || !e.EventName.StartsWith("GCAllocationTick")) return;
            int i = e.PayloadNames.IndexOf("TypeName");
            string type = i >= 0 ? e.Payload[i] as string : "?";
            lock (_types) _types[type] = _types.TryGetValue(type, out int n) ? n + 1 : 1;
        }

        public string TakeSummary()
        {
            lock (_types)
            {
                string s = string.Join(", ", _types.OrderByDescending(kv => kv.Value).Take(6).Select(kv => kv.Key + " x" + kv.Value));
                _types.Clear();
                return s;
            }
        }
    }

    sealed class CountingLineOfSight : ILineOfSight
    {
        public long Count;
        public bool IsClear(WorldPose from, WorldPose to)
        {
            Count++;
            return true;
        }
    }

    static PerceptionScanner.PlayerInput PlayerSense(WorldPose pose) => new PerceptionScanner.PlayerInput
    {
        Target = new SenseTarget { Id = ActorId.Player, Pose = pose },
        Perceivable = true,
    };
}
