using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Tests;
using NUnit.Framework;

namespace G3Tests.Saves
{
    /// <summary>
    /// fix-155 [owner]: the cost of the crash-recovery snapshot on the real content - a new game over the imported Gothic 3
    /// pack is serialized into memory (the main-thread part of <c>SaveGameController.WriteRecovery</c>) and written
    /// atomically (the worker part). Needs the imported pack (as <see cref="Scenarios.RepoScenarioContentTests"/>); ignored
    /// without it. The numbers go to the test output.
    /// </summary>
    public class RecoverySnapshotCostTests
    {
        static string PackDir()
        {
            const string rel = "Assets/StreamingAssets/Content/Gothic3";
            for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, rel, "pack.json"))) return Path.Combine(d.FullName, rel);
            foreach (var root in new[] { Environment.GetEnvironmentVariable("G3WORLD"), "." })
                if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, rel, "pack.json"))) return Path.Combine(root, rel);
            return null;
        }

        [Test]
        public void Snapshot_OfARealGame_SerializesInAFewMilliseconds()
        {
            string pack = PackDir();
            if (pack == null) Assert.Ignore("content pack not found (import it, or set G3WORLD)");
            var content = GameContent.Load(new IContentSource[] { new DirectoryContentSource(pack) });
            var ports = new SessionPorts(new RecordingPresenter(), new ListLog(), Array.Empty<ISaveParticipant>(), Array.Empty<ISaveBlocker>());
            var game = GameSession.NewGame(content, ports, new GameSettings(new LanguageSettings("Russian", "Russian"), randomSeed: 7));
            for (int i = 0; i < 20; i++) game.Tick(0.5f);

            var times = new double[21];
            byte[] bytes = null;
            for (int i = 0; i < times.Length; i++)
            {
                var watch = Stopwatch.StartNew();
                using (var memory = new MemoryStream(1 << 20))
                {
                    game.Save(memory, "Day 1, 08:00", DateTime.UtcNow);
                    bytes = memory.ToArray();
                }
                times[i] = watch.Elapsed.TotalMilliseconds;
            }
            var warm = times.Skip(1).OrderBy(t => t).ToArray();
            // the heaviest sections (diagnostics only: the session's private section list, skipped if it is renamed)
            var field = typeof(GameSession).GetField("_saveSections", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var sections = field?.GetValue(game) as System.Collections.IEnumerable ?? Array.Empty<ISaveParticipant>();
            var costs = new System.Collections.Generic.List<(string name, double ms, long size)>();
            var body = new MemoryStream(1 << 20);
            var bw = new BinarySaveWriter(body, buffered: true);
            foreach (ISaveParticipant p in sections)
            {
                // the best of 20 (the machine is often busy: the minimum is the section's own cost)
                double best = double.MaxValue;
                for (int k = 0; k < 20; k++)
                {
                    body.SetLength(0);
                    var w = Stopwatch.StartNew();
                    p.Write(bw);
                    bw.Flush();
                    best = Math.Min(best, w.Elapsed.TotalMilliseconds);
                }
                costs.Add((p.Section, best, body.Length));
            }
            foreach (var c in costs.OrderByDescending(c => c.ms).Take(8))
                TestContext.WriteLine($"fix-155 section {c.name}: {c.ms:0.00} ms, {c.size / 1024.0:0.0} KB");
            TestContext.WriteLine($"fix-155 sections total (best of 20 each): {costs.Sum(c => c.ms):0.00} ms, {costs.Sum(c => c.size) / 1024.0:0.0} KB");
            double median = warm[warm.Length / 2], max = warm[warm.Length - 1], min = warm[0];

            string dir = Path.Combine(Path.GetTempPath(), "myrtana_cost_" + Guid.NewGuid().ToString("N"));
            try
            {
                string path = Path.Combine(dir, CrashRecovery.Slot + ".sav");
                AtomicFile.WriteBytes(path, bytes);
                var write = Stopwatch.StartNew();
                AtomicFile.WriteBytes(path, bytes);
                double writeMs = write.Elapsed.TotalMilliseconds;
                TestContext.WriteLine($"fix-155 real-content snapshot: {bytes.Length / 1024} KB; serialize first {times[0]:0.0} ms, " +
                                      $"then median {median:0.0} ms, min {min:0.0} ms, max {max:0.0} ms (main thread); atomic write with flush {writeMs:0.0} ms (worker)");
                Assert.That(SaveFile.IsComplete(bytes), Is.True);
                Assert.That(AtomicFile.ReadValid(path, SaveFile.IsComplete, out _), Is.EqualTo(bytes));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            Assert.That(median, Is.LessThan(50), "main-thread serialization of a new game on the real content (target ~5 ms)");
        }

        static BinarySaveWriter _wb2; static Stream _wb2s;
        static BinarySaveWriter wb2(Stream s) { if (_wb2s != s) { _wb2 = new BinarySaveWriter(s, buffered: true); _wb2s = s; } return _wb2; }

        /// <summary>The sections of a save file: name -> body bytes (format: magic, version, header, count, sections).</summary>
        static System.Collections.Generic.Dictionary<string, byte[]> Sections(byte[] file)
        {
            var map = new System.Collections.Generic.Dictionary<string, byte[]>(StringComparer.Ordinal);
            using var ms = new MemoryStream(file);
            SaveFile.ReadHeader(ms);   // magic, version, header
            var r = new BinarySaveReader(ms);
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string name = r.ReadString();
                r.ReadInt32();
                int length = r.ReadInt32();
                var body = new byte[length];
                int got = 0;
                while (got < length) got += ms.Read(body, got, length - got);
                map[name] = body;
            }
            return map;
        }

        /// <summary>
        /// fix-171 [owner, build 21a]: the snapshot of the owner's late-game saves (Player.log: 721 KB, 18 ms, npcs 6.6 /
        /// rules.items 5.9 ms). Loads every .sav in <c>G3_COST_SAVES</c> (default Work/scratch/f171/saves) on the real content,
        /// then times each section (best of 20) and the whole serialization (median of 20). Ignored without saves or pack.
        /// </summary>
        [Test]
        public void Snapshot_OfLateGameSaves_SectionCosts()
        {
            string pack = PackDir();
            if (pack == null) Assert.Ignore("content pack not found (import it, or set G3WORLD)");
            string dir = Environment.GetEnvironmentVariable("G3_COST_SAVES");
            if (string.IsNullOrEmpty(dir)) dir = Path.GetFullPath(Path.Combine(pack, "../../../../Work/scratch/f171/saves"));
            if (!Directory.Exists(dir) || Directory.GetFiles(dir, "*.sav").Length == 0) Assert.Ignore("no saves in " + dir);
            var content = GameContent.Load(new IContentSource[] { new DirectoryContentSource(pack) });
            foreach (var file in Directory.GetFiles(dir, "*.sav").OrderBy(f => f, StringComparer.Ordinal))
            {
                var ports = new SessionPorts(new RecordingPresenter(), new ListLog(), Array.Empty<ISaveParticipant>(), Array.Empty<ISaveBlocker>());
                GameSession game;
                using (var s = File.OpenRead(file)) game = GameSession.Load(content, ports, new GameSettings(new LanguageSettings("Russian", "Russian"), randomSeed: 7), s);
                var field = typeof(GameSession).GetField("_saveSections", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var sections = field?.GetValue(game) as System.Collections.IEnumerable ?? Array.Empty<ISaveParticipant>();
                var body = new MemoryStream(1 << 20);
                var bw = new BinarySaveWriter(body, buffered: true);
                var costs = new System.Collections.Generic.List<(string name, double ms, long size)>();
                var oldw = new StreamWriterBaseline(body);
                double oldTotal = 0;
                foreach (ISaveParticipant p in sections)
                {
                    double best = double.MaxValue, bestOld = double.MaxValue;
                    for (int k = 0; k < 20; k++)
                    {
                        body.SetLength(0);
                        var w = Stopwatch.StartNew();
                        p.Write(oldw);
                        oldw.Flush();
                        bestOld = Math.Min(bestOld, w.Elapsed.TotalMilliseconds);
                        body.SetLength(0);
                        w.Restart();
                        p.Write(bw);
                        bw.Flush();
                        best = Math.Min(best, w.Elapsed.TotalMilliseconds);
                    }
                    oldTotal += bestOld;
                    costs.Add((p.Section, best, body.Length));
                }
                TestContext.WriteLine($"fix-171 {Path.GetFileName(file)}: sections with the old BinaryWriter writer {oldTotal:0.00} ms");
                var times = new double[20];
                long length = 0;
                var memory = new MemoryStream(1 << 20);
                for (int i = 0; i < times.Length; i++)
                {
                    memory.SetLength(0);
                    var watch = Stopwatch.StartNew();
                    game.Save(memory, "Day 9, 12:00", DateTime.UtcNow);
                    times[i] = watch.Elapsed.TotalMilliseconds;
                    length = memory.Length;
                }
                Array.Sort(times);
                // fix-171: the snapshot's split save (main-thread prepare + worker WriteTo) writes the same file
                var bodies = new MemoryStream(1 << 20);
                var prep = new double[20]; byte[] split = null;
                var fixedTime = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
                for (int i = 0; i < prep.Length; i++)
                {
                    var watch = Stopwatch.StartNew();
                    var pending = game.PrepareSave("Day 9, 12:00", fixedTime, bodies);
                    prep[i] = watch.Elapsed.TotalMilliseconds;
                    var outp = new MemoryStream(); pending.WriteTo(outp); split = outp.ToArray();
                }
                Array.Sort(prep);
                var whole = new MemoryStream(); game.Save(whole, "Day 9, 12:00", fixedTime);
                Assert.That(split, Is.EqualTo(whole.ToArray()), "PrepareSave + WriteTo = Save");
                TestContext.WriteLine($"fix-171 {Path.GetFileName(file)}: split save identical; main-thread prepare median {prep[prep.Length / 2]:0.00} ms (min {prep[0]:0.00}) vs whole save median {times[times.Length / 2]:0.00} ms");
                // byte identity: every section the load does not touch is written back exactly as the old writer wrote it
                var original = Sections(File.ReadAllBytes(file));
                var now = Sections(memory.ToArray());
                var differ = now.Where(kv => !original.TryGetValue(kv.Key, out var o) || !o.AsSpan().SequenceEqual(kv.Value)).Select(kv => kv.Key).ToList();
                TestContext.WriteLine($"fix-171 {Path.GetFileName(file)}: sections differing from the file after load+save (the load migrates): {(differ.Count == 0 ? "none" : string.Join(", ", differ))}");
                // rules.items: the cached orders give exactly the bytes of the pre-fix-171 LINQ writer on the same state
                foreach (ISaveParticipant p in sections)
                {
                    if (p is not Myrtana.Rules.ItemsSaveSection items) continue;
                    var a = new MemoryStream(); var wa = new StreamWriterBaseline(a); OldItems.Write(items, wa); wa.Flush();
                    var b = new MemoryStream(); var wb = new BinarySaveWriter(b, buffered: true); items.Write(wb); wb.Flush();
                    Assert.That(b.ToArray(), Is.EqualTo(a.ToArray()), "rules.items bytes = the old writer's");
                    double tOld = double.MaxValue, tNew = double.MaxValue, tOldW = double.MaxValue;
                    for (int k = 0; k < 100; k++)
                    {
                        a.SetLength(0); var sw = Stopwatch.StartNew(); OldItems.Write(items, wa); wa.Flush(); tOld = Math.Min(tOld, sw.Elapsed.TotalMilliseconds);
                        a.SetLength(0); sw.Restart(); var w2 = wb2(a); OldItems.Write(items, w2); w2.Flush(); tOldW = Math.Min(tOldW, sw.Elapsed.TotalMilliseconds);
                        b.SetLength(0); sw.Restart(); items.Write(wb); wb.Flush(); tNew = Math.Min(tNew, sw.Elapsed.TotalMilliseconds);
                    }
                    TestContext.WriteLine($"fix-171 {Path.GetFileName(file)}: rules.items identical to the old writer ({b.Length} bytes); best of 100: old {tOld:0.000} ms, old logic + new writer {tOldW:0.000} ms, new {tNew:0.000} ms");
                }
                TestContext.WriteLine($"fix-171 {Path.GetFileName(file)}: {length / 1024} KB, serialize median {times[times.Length / 2]:0.00} ms (min {times[0]:0.00}); " +
                                      $"sections {costs.Sum(c => c.ms):0.00} ms: " +
                                      string.Join(", ", costs.OrderByDescending(c => c.ms).Take(6).Select(c => $"{c.name} {c.ms:0.00} ({c.size / 1024} KB)")));
            }
        }
    }

    /// <summary>The save writer before fix-171 (a BinaryWriter call per value), for the cost comparison.</summary>
    sealed class StreamWriterBaseline : ISaveWriter
    {
        readonly BinaryWriter _w;
        public StreamWriterBaseline(Stream output) => _w = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        public void WriteBool(bool value) => _w.Write(value);
        public void WriteInt32(int value) => _w.Write(value);
        public void WriteUInt64(ulong value) => _w.Write(value);
        public void WriteDouble(double value) => _w.Write(value);
        public void WriteSingle(float value) => _w.Write(value);
        public void WriteString(string value) { _w.Write(value != null); if (value != null) _w.Write(value); }
        public void Flush() => _w.Flush();
    }

    /// <summary>The "rules.items" writer before fix-171 (LINQ sorts per write), for the byte comparison.</summary>
    static class OldItems
    {
        static object F(object o, string name) => o.GetType().GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(o);

        public static void Write(Myrtana.Rules.ItemsSaveSection section, ISaveWriter w)
        {
            var _inventories = (Myrtana.Rules.Inventories)F(section, "_inventories");
            var _equipment = (Myrtana.Rules.HeroEquipment)F(section, "_equipment");
            var _world = (Myrtana.Rules.WorldItems)F(section, "_world");
            var _roles = (Myrtana.Rules.ActorRoles)F(section, "_roles");
            var _trade = (Myrtana.Rules.TradeService)F(section, "_trade");
            var _npcLoot = (Myrtana.Rules.NpcLoot)F(section, "_npcLoot");
            var inventories = _inventories.All.Where(kv => kv.Value.Stacks.Count > 0).OrderBy(kv => kv.Key.Name, StringComparer.Ordinal).ToList();
            w.WriteInt32(inventories.Count);
            foreach (var kv in inventories)
            {
                w.WriteString(kv.Key.Name);
                w.WriteInt32(kv.Value.Stacks.Count);
                foreach (var s in kv.Value.Stacks)
                {
                    w.WriteString(s.Item);
                    w.WriteInt32(s.Amount);
                    w.WriteInt32(s.Quality);
                    w.WriteInt32(s.Acquired);
                }
            }
            var worn = _equipment.Worn.OrderBy(kv => kv.Key).ToList();
            w.WriteInt32(worn.Count);
            foreach (var kv in worn)
            {
                w.WriteInt32((int)kv.Key);
                w.WriteString(kv.Value);
            }
            w.WriteSortedStrings(_world.Taken);
            foreach (ActorRole role in Enum.GetValues(typeof(ActorRole))) w.WriteSortedStrings(_roles.All(role).Select(a => a.Name));
            var merchants = _trade.States.OrderBy(kv => kv.Key.Name, StringComparer.Ordinal).ToList();
            w.WriteInt32(merchants.Count);
            foreach (var kv in merchants)
            {
                w.WriteString(kv.Key.Name);
                w.WriteBool(kv.Value.Generated);
                w.WriteDouble(kv.Value.LastContactHours);
                w.WriteInt32(kv.Value.Merchandise.Count);
                foreach (var m in kv.Value.Merchandise)
                {
                    w.WriteString(m.Key);
                    w.WriteInt32(m.Value);
                }
            }
            var dropped = _world.AllDropped.ToList();
            w.WriteInt32(_world.NextDrop);
            w.WriteInt32(dropped.Count);
            foreach (var d in dropped)
            {
                w.WriteString(d.Id);
                w.WriteString(d.Item);
                w.WriteInt32(d.Amount);
                w.WriteInt32(d.Quality);
                w.WriteString(d.Owner.Name ?? "");
                for (int i = 0; i < 3; i++) w.WriteDouble(d.Position[i]);
                w.WriteDouble(d.Yaw);
                w.WriteBool(d.Spent); // fix-195 (rules.items v7)
                w.WriteBool(d.Rotation != null);
                if (d.Rotation != null) for (int i = 0; i < 4; i++) w.WriteDouble(d.Rotation[i]);
            }
            w.WriteSortedStrings((_npcLoot?.Generated ?? Enumerable.Empty<ActorId>()).Select(a => a.Name));
            var covered = (_npcLoot?.TrophiesCovered ?? new Dictionary<ActorId, HashSet<string>>())
                          .OrderBy(kv => kv.Key.Name, StringComparer.Ordinal).ToList();
            w.WriteInt32(covered.Count);
            foreach (var kv in covered)
            {
                w.WriteString(kv.Key.Name);
                w.WriteSortedStrings(kv.Value);
            }
        }

    }
}
