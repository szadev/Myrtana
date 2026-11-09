using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Myrtana.Core;
using Myrtana.Session;
using Myrtana.Story;
using Myrtana.Ui.Skin;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-155 [owner]: crash recovery — atomic save writes with a backup and the reader's fallback, the session marker, the
    /// title's restore decision (newest choice), the hidden snapshot kept out of the lists and Continue, its schedule and
    /// key moments, the port option, and the cost of serializing a game into memory.
    /// </summary>
    public class CrashRecoveryTests
    {
        static readonly DateTime T0 = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

        string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "myrtana_crash_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        static byte[] SaveBytes(double minutes, int sections = 3)
        {
            var parts = Enumerable.Range(0, sections).Select(i => (ISaveParticipant)new Part("s" + i, i * 37 + 5)).ToList();
            var memory = new MemoryStream();
            SaveFile.Write(memory, new SaveHeader(new[] { new PackStamp("Gothic3", "h") }, 60, "1|08:00|Faring", T0.AddMinutes(minutes)), parts);
            return memory.ToArray();
        }

        sealed class Part : ISaveParticipant
        {
            readonly int _size;
            public Part(string section, int size) { Section = section; _size = size; }
            public string Section { get; }
            public int Version => 1;
            public void Write(ISaveWriter w) { for (int i = 0; i < _size; i++) w.WriteInt32(i); }
            public void Read(ISaveReader r, int version) { }
        }

        static KeyValuePair<string, SaveHeader> Header(string slot, double minutes) =>
            new KeyValuePair<string, SaveHeader>(slot, new SaveHeader(null, 60, "1|08:00|Faring", T0.AddMinutes(minutes)));

        // ------------------------------------------------------------------------------------------- atomic writes

        [Test]
        public void AtomicWrite_ReplacesTheFile_AndKeepsThePreviousAsBackup()
        {
            string path = Path.Combine(_dir, "quick.sav");
            AtomicFile.WriteBytes(path, new byte[] { 1, 2, 3 });
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(File.Exists(AtomicFile.BackupOf(path)), Is.False, "the first write has nothing to back up");

            AtomicFile.WriteBytes(path, new byte[] { 4, 5 });
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(new byte[] { 4, 5 }));
            Assert.That(File.ReadAllBytes(AtomicFile.BackupOf(path)), Is.EqualTo(new byte[] { 1, 2, 3 }), "the previous version");
            Assert.That(File.Exists(AtomicFile.TempOf(path)), Is.False, "no temporary file stays");

            AtomicFile.WriteBytes(path, new byte[] { 6 });
            Assert.That(File.ReadAllBytes(AtomicFile.BackupOf(path)), Is.EqualTo(new byte[] { 4, 5 }), "one backup: the version before the last write");
        }

        [Test]
        public void AtomicWrite_ThatFailsMidway_LeavesTheOldFileUntouched()
        {
            string path = Path.Combine(_dir, "auto.sav");
            AtomicFile.WriteBytes(path, new byte[] { 9, 9, 9 });
            Assert.Throws<InvalidOperationException>(() => AtomicFile.Write(path, s =>
            {
                s.WriteByte(1);
                throw new InvalidOperationException("crash while serializing");
            }));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(new byte[] { 9, 9, 9 }), "the slot keeps the last whole save");
        }

        [Test]
        public void Reader_FallsBackToTheBackup_WhenTheFileIsTruncated()
        {
            string path = Path.Combine(_dir, "save001.sav");
            byte[] older = SaveBytes(1), newer = SaveBytes(2);
            AtomicFile.WriteBytes(path, older);
            AtomicFile.WriteBytes(path, newer);

            var read = AtomicFile.ReadValid(path, SaveFile.IsComplete, out bool fromBackup);
            Assert.That(read, Is.EqualTo(newer));
            Assert.That(fromBackup, Is.False);

            File.WriteAllBytes(path, newer.Take(newer.Length / 2).ToArray()); // power loss: the file system kept half
            read = AtomicFile.ReadValid(path, SaveFile.IsComplete, out fromBackup);
            Assert.That(read, Is.EqualTo(older), "the backup is the last whole save");
            Assert.That(fromBackup, Is.True);

            File.WriteAllBytes(path, new byte[newer.Length]); // zero-filled
            Assert.That(AtomicFile.ReadValid(path, SaveFile.IsComplete, out _), Is.EqualTo(older));

            File.Delete(path); // only the backup is left
            Assert.That(AtomicFile.Exists(path), Is.True);
            Assert.That(AtomicFile.ReadValid(path, SaveFile.IsComplete, out fromBackup), Is.EqualTo(older));
            Assert.That(fromBackup, Is.True);

            File.WriteAllBytes(AtomicFile.BackupOf(path), new byte[3]);
            Assert.That(AtomicFile.ReadValid(path, SaveFile.IsComplete, out _), Is.Null, "neither is whole");
        }

        [Test]
        public void Reader_OffersTheBackupNext_WhenTheFileFailsToLoad()
        {
            string path = Path.Combine(_dir, "quick.sav");
            AtomicFile.WriteBytes(path, SaveBytes(1));
            AtomicFile.WriteBytes(path, SaveBytes(2));
            var versions = AtomicFile.ReadAll(path, SaveFile.IsComplete).ToList();
            Assert.That(versions.Select(v => v.backup), Is.EqualTo(new[] { false, true }), "file first, then backup");
        }

        [Test]
        public void Delete_RemovesFileBackupAndLeftoverTemp()
        {
            string path = Path.Combine(_dir, "auto.sav");
            AtomicFile.WriteBytes(path, new byte[] { 1 });
            AtomicFile.WriteBytes(path, new byte[] { 2 });
            File.WriteAllBytes(AtomicFile.TempOf(path), new byte[] { 3 });
            AtomicFile.Delete(path);
            Assert.That(Directory.GetFiles(_dir), Is.Empty);
            Assert.That(AtomicFile.Exists(path), Is.False);
        }

        [Test]
        public void Thumbnail_IsValidOnlyWhenTheJpegIsWhole()
        {
            var jpeg = new byte[] { 0xFF, 0xD8, 0x01, 0x02, 0xFF, 0xD9 };
            Assert.That(AtomicFile.IsJpeg(jpeg), Is.True);
            Assert.That(AtomicFile.IsJpeg(jpeg.Take(4).ToArray()), Is.False, "cut short");
            Assert.That(AtomicFile.IsJpeg(new byte[6]), Is.False, "zero-filled");
            string path = Path.Combine(_dir, "quick.jpg");
            AtomicFile.WriteBytes(path, jpeg);
            AtomicFile.WriteBytes(path, new byte[] { 0xFF, 0xD8, 0x07, 0xFF, 0xD9 });
            File.WriteAllBytes(path, new byte[] { 0xFF, 0xD8 });
            Assert.That(AtomicFile.ReadValid(path, AtomicFile.IsJpeg, out _), Is.EqualTo(jpeg));
        }

        [Test]
        public void IsComplete_AcceptsAWholeSave_RejectsEveryCutAndTrailingBytes()
        {
            var whole = SaveBytes(0);
            Assert.That(SaveFile.IsComplete(whole), Is.True);
            for (int cut = 0; cut < whole.Length; cut++)
                Assert.That(SaveFile.IsComplete(whole.Take(cut).ToArray()), Is.False, $"cut at {cut} of {whole.Length}");
            Assert.That(SaveFile.IsComplete(whole.Concat(new byte[] { 0 }).ToArray()), Is.False, "trailing garbage");
            Assert.That(SaveFile.IsComplete(null), Is.False);
        }

        [Test]
        public void SlotNames_IncludeLoneBackups_NotTempFiles()
        {
            var names = SaveSlotFiles.Slots(new[] { "a/quick.sav", "a/quick.sav.bak", "a/save001.sav.bak", "a/auto.sav.tmp", "a/quick.jpg", "a/x.txt" }, ".sav");
            Assert.That(names, Is.EqualTo(new[] { "quick", "save001" }));
        }

        // ------------------------------------------------------------------------------------------- session marker

        [Test]
        public void Marker_TellsAnUncleanExit_OnlyWhenTheLastRunLeftIt()
        {
            string path = Path.Combine(_dir, CrashRecovery.MarkerFile);
            var first = new SessionMarker(path);
            Assert.That(first.Begin(T0, 100), Is.False, "first start: no marker");
            Assert.That(File.Exists(path), Is.True, "this run's marker");

            var crashed = new SessionMarker(path);
            Assert.That(crashed.Begin(T0.AddMinutes(30), 200), Is.True, "the marker of run 100 stayed: it crashed");
            crashed.End();
            Assert.That(File.Exists(path), Is.False, "a normal quit removes it");

            Assert.That(new SessionMarker(path).Begin(T0.AddHours(1), 300), Is.False, "after a normal quit");
        }

        [Test]
        public void Marker_OfTheSameProcess_IsNoCrash()
        {
            string path = Path.Combine(_dir, CrashRecovery.MarkerFile);
            new SessionMarker(path).Begin(T0, 42);
            Assert.That(new SessionMarker(path).Begin(T0.AddMinutes(1), 42), Is.False, "the editor's next Play in the same process");
            Assert.That(new SessionMarker(path).Begin(T0.AddMinutes(2), 0), Is.True, "no process id: a leftover marker counts");
            Assert.That(SessionMarker.ProcessOf(SessionMarker.Content(T0, 1234)), Is.EqualTo(1234));
            Assert.That(SessionMarker.ProcessOf("garbage"), Is.EqualTo(0));
        }

        // ------------------------------------------------------------------------------------------- offer decision

        [Test]
        public void Offer_OnlyAfterAnUncleanExit_WhenTheSnapshotIsNewerThanEveryNormalSave()
        {
            DateTime snap = T0.AddMinutes(10);
            Assert.That(CrashRecovery.ShouldOffer(true, true, snap, T0), Is.True);
            Assert.That(CrashRecovery.ShouldOffer(true, true, snap, null), Is.True, "no normal save at all");
            Assert.That(CrashRecovery.ShouldOffer(true, true, snap, snap), Is.False, "a save of the same time holds the same progress");
            Assert.That(CrashRecovery.ShouldOffer(true, true, snap, snap.AddSeconds(1)), Is.False, "the player saved after it");
            Assert.That(CrashRecovery.ShouldOffer(true, false, snap, T0), Is.False, "a normal quit");
            Assert.That(CrashRecovery.ShouldOffer(false, true, snap, T0), Is.False, "the option is off");
            Assert.That(CrashRecovery.ShouldOffer(true, true, null, T0), Is.False, "no readable snapshot");
        }

        [Test]
        public void NewestNormal_IgnoresTheHiddenSnapshot()
        {
            var saves = new[] { Header(CrashRecovery.Slot, 30), Header("quick", 5), Header("save002", 12), Header("auto", 9) };
            Assert.That(CrashRecovery.NewestNormal(saves), Is.EqualTo(T0.AddMinutes(12)));
            Assert.That(CrashRecovery.NewestNormal(new[] { Header(CrashRecovery.Slot, 30) }), Is.Null);
        }

        [Test]
        public void PromptText_RussianAndEnglish_WithTheSnapshotsLocalTime()
        {
            var local = new DateTime(2026, 10, 19, 21, 7, 0);
            Assert.That(CrashRecovery.PromptText("Russian", local), Is.EqualTo("Игра завершилась некорректно. Восстановить прогресс от 19.10.2026 21:07?"));
            Assert.That(CrashRecovery.PromptText("English", local), Is.EqualTo("The game did not exit properly. Restore the progress from 19.10.2026 21:07?"));
        }

        // ------------------------------------------------------------------------------------------- lists, Continue

        [Test]
        public void HiddenSnapshot_IsNeitherListedNorContinued_TheAcceptedOneIs()
        {
            var saves = new[] { Header(CrashRecovery.Slot, 30), Header("quick", 5), Header("auto", 9), Header("save001", 2) };
            Assert.That(ContinueGame.Newest(saves), Is.EqualTo("auto"), "Continue never loads the hidden snapshot silently");
            Assert.That(ContinueGame.Candidates(saves), Has.No.Member(CrashRecovery.Slot));
            var rows = SaveGameList.For(false, saves, new Strings(), "Russian");
            Assert.That(rows.Select(r => r.Slot), Is.EqualTo(new[] { "quick", "auto", "save001" }));

            var accepted = new[] { Header(CrashRecovery.RecoveredSlot, 30), Header("quick", 5), Header("auto", 9), Header("save001", 2) };
            Assert.That(ContinueGame.Newest(accepted), Is.EqualTo(CrashRecovery.RecoveredSlot), "Yes made it a normal save");
            rows = SaveGameList.For(false, accepted, new Strings(), "Russian");
            Assert.That(rows.Select(r => r.Slot), Is.EqualTo(new[] { "quick", "auto", CrashRecovery.RecoveredSlot, "save001" }));
            var recovered = rows.Single(r => r.Slot == CrashRecovery.RecoveredSlot);
            Assert.That(recovered.Kind, Is.EqualTo(SaveSlotKind.Recovered));
            Assert.That(recovered.Name, Is.EqualTo("Восстановление"));
            Assert.That(recovered.DeletableByKey, Is.False, "Del deletes only numbered saves");
            Assert.That(SaveGameList.For(true, accepted, new Strings()).Select(r => r.Slot), Has.No.Member(CrashRecovery.RecoveredSlot), "not a save target");
            Assert.That(CrashRecovery.RecoveredName("English"), Is.EqualTo("Recovered game"));
        }

        [Test]
        public void ConfirmBox_InThePortsWords_NoRunsTheDeclinedAction()
        {
            var menu = new GameMenuState();
            menu.Open();
            int yes = 0, no = 0;
            menu.Ask(new MenuRequest(() => "restore?", () => yes++, () => no++));
            Assert.That(menu.Request.MessageText(), Is.EqualTo("restore?"));
            Assert.That(menu.Request.IsNotice, Is.False, "Yes / No");
            menu.Back(); // Esc = No
            Assert.That((yes, no), Is.EqualTo((0, 1)));
            menu.Ask(new MenuRequest(() => "restore?", () => yes++, () => no++));
            menu.Answer(true);
            Assert.That((yes, no), Is.EqualTo((1, 1)));
        }

        // ------------------------------------------------------------------------------------------- schedule, moments

        [Test]
        public void Schedule_EveryTwoMinutes_WaitsWhileSavingIsBlocked()
        {
            var s = new RecoverySchedule();
            s.Restart(0);
            Assert.That(s.IntervalSeconds, Is.EqualTo(120f));
            Assert.That(s.ShouldSave(119, null, false), Is.False);
            Assert.That(s.ShouldSave(120, "fight", false), Is.False, "blocked: waits");
            Assert.That(s.ShouldSave(150, null, true), Is.False, "menu / loading: waits");
            Assert.That(s.ShouldSave(151, null, false), Is.True, "the first allowed moment");
            s.Saved(151);
            Assert.That(s.Due, Is.EqualTo(271));
        }

        [Test]
        public void KeyMoment_BringsTheSnapshotForward_SettledAndSpaced()
        {
            var s = new RecoverySchedule();
            s.Restart(0);
            s.KeyMoment(5);
            Assert.That(s.Due, Is.EqualTo(20), "not sooner than 20 s after the last snapshot (here: the start)");
            s.KeyMoment(50);
            Assert.That(s.Due, Is.EqualTo(20), "an earlier due time stays");
            Assert.That(s.ShouldSave(20, null, false), Is.True);
            s.Saved(20);
            s.KeyMoment(60);
            Assert.That(s.Due, Is.EqualTo(61), "settles one second");
            s.KeyMoment(60.5);
            Assert.That(s.Due, Is.EqualTo(61), "a burst gives one snapshot");
            s.Saved(61);
            s.KeyMoment(62);
            Assert.That(s.Due, Is.EqualTo(81), "spaced 20 s from the last");
        }

        [Test]
        public void Moments_QuestStatusAndTeleport_AreKeyMoments()
        {
            var bus = new EventBus();
            var seen = new List<string>();
            using (new RecoveryMoments(bus, null, seen.Add))
            {
                bus.Publish(new QuestStatusChanged(new Id<QuestDef>("Gnar_EnterCastle"), QuestStatus.Running, QuestStatus.Success));
                bus.Publish(new Myrtana.Rules.TeleportRequested(null, "It_Teleport"));
                bus.Publish(new Myrtana.Rules.WorldItemTaken("w1", "It_Apple", 1, ActorId.Player)); // no content: not a quest item
            }
            bus.Publish(new QuestStatusChanged(new Id<QuestDef>("Late"), QuestStatus.Running, QuestStatus.Failed));
            Assert.That(seen, Is.EqualTo(new[] { "quest Gnar_EnterCastle Success", "teleport" }), "disposed: no more moments");
        }

        // ------------------------------------------------------------------------------------------- option

        [Test]
        public void Option_DefaultsOn_RowTexts()
        {
            var port = new PortOptions();
            Assert.That(port.CrashRecovery, Is.True, "[owner] default on");
            int changed = 0;
            port.Changed += () => changed++;
            port.CrashRecovery = false;
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(PortOptions.RowText(PortOptions.Row.CrashRecovery, "Russian"), Is.EqualTo("Восстановление после вылета"));
            Assert.That(PortOptions.RowText(PortOptions.Row.CrashRecovery, "English"), Is.EqualTo("Crash recovery"));
            Assert.That(PortOptions.ValueText(PortOptions.Row.CrashRecovery, false, "Russian"), Is.EqualTo("Нет (оригинал)"), "position 0 = original");
            Assert.That(PortOptions.ValueText(PortOptions.Row.CrashRecovery, true, "English"), Is.EqualTo("Yes"));
            Assert.That(VideoPortRowsLayout.CrashRecoveryTop, Is.EqualTo(252f));
            Assert.That(VideoPortRowsLayout.RowBottom(VideoPortRowsLayout.CrashRecoveryTop),
                        Is.LessThanOrEqualTo(VideoPortRowsLayout.ContinueButtonTop + VideoPortRowsLayout.SliderOffset), "no overlap with Continue button");
        }

        // ------------------------------------------------------------------------------------------- cost

        [Test]
        public void Snapshot_SerializesAGameIntoMemory_Whole_AndFast()
        {
            using (var f = new SessionFixture())
            {
                var game = f.NewGame();
                game.Tick(5f);
                game.Save(new MemoryStream(), "warm-up", T0); // JIT
                var watch = Stopwatch.StartNew();
                byte[] bytes;
                using (var memory = new MemoryStream())
                {
                    game.Save(memory, "1|08:00|Faring", T0);
                    bytes = memory.ToArray();
                }
                watch.Stop();
                TestContext.WriteLine($"fix-155 snapshot of the fixture game: {bytes.Length} bytes, {watch.Elapsed.TotalMilliseconds:0.00} ms");
                Assert.That(SaveFile.IsComplete(bytes), Is.True);
                Assert.That(watch.Elapsed.TotalMilliseconds, Is.LessThan(50), "main-thread serialization of a small game");

                string path = Path.Combine(_dir, CrashRecovery.Slot + ".sav");
                var write = Stopwatch.StartNew();
                AtomicFile.WriteBytes(path, bytes);
                TestContext.WriteLine($"fix-155 atomic write with flush: {write.Elapsed.TotalMilliseconds:0.00} ms");
                Assert.That(AtomicFile.ReadValid(path, SaveFile.IsComplete, out _), Is.EqualTo(bytes));
            }
        }

        sealed class Strings : ILocalizer
        {
            static readonly Dictionary<string, string> Texts = new Dictionary<string, string>
            {
                ["HUD_File_SaveGame_Empty"] = "New Savegame",
                ["HUD_File_SaveGame_Format"] = "$(day). day\\n$(hour):$(minute)",
                ["HUD_File_SaveGame_Format2"] = "Savegame No. ",
                ["HUD_QuickSave_Game"] = "Quick save game",
            };

            public string Text(TextId id) => TryText(id, out var t) ? t : "[" + id.Value + "]";
            public bool TryText(TextId id, out string text) => Texts.TryGetValue(id.Value, out text);
            public string Stage(TextId id) => null;
        }
    }
}
