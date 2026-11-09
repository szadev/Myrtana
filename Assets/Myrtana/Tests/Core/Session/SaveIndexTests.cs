using System;
using System.IO;
using System.Linq;
using Myrtana.Core;
using Myrtana.Session;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-222 [owner]: the cached save list — headers read once, re-read only for changed files, persisted.</summary>
    public class SaveIndexTests
    {
        static readonly DateTime T0 = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "g3-saveindex-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        void WriteSave(string file, string title, double minutes, int padding = 0)
        {
            using (var s = File.Create(Path.Combine(_dir, file)))
            {
                SaveFile.Write(s, new SaveHeader(new[] { new PackStamp("Gothic3", "h1") }, 60, title, T0.AddMinutes(minutes)),
                               Array.Empty<ISaveParticipant>());
                s.Write(new byte[padding], 0, padding);
            }
        }

        SaveIndex NewIndex() => new SaveIndex(_dir, ".sav");

        [Test]
        public void Scan_ReadsEachHeaderOnce_ThenOnlyChangedFiles()
        {
            WriteSave("save001.sav", "a", 1);
            WriteSave("save002.sav", "b", 2);
            WriteSave("quick.sav", "q", 3);
            File.WriteAllBytes(Path.Combine(_dir, "save001.jpg"), new byte[] { 1, 2, 3 });
            var index = NewIndex();
            Assert.That(index.Scan(), Is.EqualTo(3));
            Assert.That(index.Scan(), Is.Zero, "nothing changed: no file opened");
            WriteSave("save002.sav", "b2", 5, padding: 17);
            Assert.That(index.Scan(), Is.EqualTo(1), "only the rewritten save is read again");
            Assert.That(index.Header("save002").Title, Is.EqualTo("b2"));
            File.Delete(Path.Combine(_dir, "save001.sav"));
            index.Scan();
            Assert.That(index.List().Select(kv => kv.Key), Is.EqualTo(new[] { "save002", "quick" }), "newest first, the deleted one gone");
        }

        [Test]
        public void Expect_ListsAQueuedSave_UntilTheWrittenFileReplacesIt()
        {
            // fix-251: a save is written on a worker; the save page lists it at once from its header
            WriteSave("save001.sav", "a", 1);
            var index = NewIndex();
            index.Scan();
            index.Expect("save002", new SaveHeader(new[] { new PackStamp("Gothic3", "h1") }, 60, "queued", T0.AddMinutes(7)));
            Assert.That(index.List().Select(kv => kv.Key), Is.EqualTo(new[] { "save002", "save001" }));
            Assert.That(index.Header("save002").Title, Is.EqualTo("queued"));
            WriteSave("save002.sav", "written", 7);
            index.Update("save002");
            Assert.That(index.Header("save002").Title, Is.EqualTo("written"), "the file is read after the write");
            index.Expect("save003", new SaveHeader(new[] { new PackStamp("Gothic3", "h1") }, 60, "lost", T0));
            index.Scan();
            Assert.That(index.Header("save003"), Is.Null, "an expected save whose write never landed goes at the next scan");
        }

        [Test]
        public void List_IsNewestFirst_AndSkipsUnreadable()
        {
            WriteSave("save001.sav", "a", 1);
            WriteSave("save002.sav", "b", 9);
            WriteSave("auto.sav", "c", 4);
            File.WriteAllBytes(Path.Combine(_dir, "broken.sav"), new byte[] { 9, 9, 9 });
            string unreadable = null;
            var index = NewIndex();
            index.Unreadable = slot => unreadable = slot;
            index.Scan();
            Assert.That(index.List().Select(kv => kv.Key), Is.EqualTo(new[] { "save002", "auto", "save001" }));
            Assert.That(unreadable, Is.EqualTo("broken"));
            unreadable = null;
            index.Scan();
            Assert.That(unreadable, Is.Null, "an unchanged unreadable file is not read again");
        }

        [Test]
        public void LoneBackup_IsListedByIt_AndADamagedFileFallsBackToTheBackup()
        {
            WriteSave("save003.sav.bak", "bak", 2);
            File.WriteAllBytes(Path.Combine(_dir, "save004.sav"), new byte[] { 1 });
            WriteSave("save004.sav.bak", "older", 1);
            var index = NewIndex();
            index.Scan();
            Assert.That(index.Header("save003").Title, Is.EqualTo("bak"));
            Assert.That(index.Header("save004").Title, Is.EqualTo("older"));
        }

        [Test]
        public void Update_RefreshesOneSlot_WrittenOrDeleted()
        {
            WriteSave("save001.sav", "a", 1);
            var index = NewIndex();
            index.Scan();
            WriteSave("save005.sav", "new", 3);
            index.Update("save005");
            Assert.That(index.List().First().Key, Is.EqualTo("save005"), "a fresh save shows without a scan");
            File.Delete(Path.Combine(_dir, "save001.sav"));
            index.Update("save001");
            Assert.That(index.List().Select(kv => kv.Key), Is.EqualTo(new[] { "save005" }));
            Assert.That(index.Scan(), Is.Zero, "the update left the index in step with the disk");
        }

        [Test]
        public void Cache_RoundTrips_SoTheNextRunOpensNoUnchangedSave()
        {
            WriteSave("save001.sav", "a", 1);
            WriteSave("save002.sav", "b", 2);
            var first = NewIndex();
            first.Scan();
            string cache = Path.Combine(_dir, SaveIndex.CacheFileName);
            Assert.That(first.SaveCache(cache), Is.True);
            Assert.That(first.SaveCache(cache), Is.False, "unchanged: not written again");

            var second = NewIndex();
            Assert.That(second.LoadCache(cache), Is.True);
            Assert.That(second.Scan(), Is.Zero, "every header came from the cache");
            var header = second.Header("save002");
            Assert.That(header.Title, Is.EqualTo("b"));
            Assert.That(header.SavedAtUtc, Is.EqualTo(T0.AddMinutes(2)));
            Assert.That(header.Packs.Single().ContentHash, Is.EqualTo("h1"));
            Assert.That(second.List().Select(kv => kv.Key), Is.EqualTo(new[] { "save002", "save001" }));
        }

        [Test]
        public void DamagedCache_IsIgnored()
        {
            WriteSave("save001.sav", "a", 1);
            string cache = Path.Combine(_dir, SaveIndex.CacheFileName);
            File.WriteAllBytes(cache, new byte[] { 0x53, 0x49, 0x44, 0x58, 1, 0, 0, 0, 5, 0 });
            var index = NewIndex();
            Assert.That(index.LoadCache(cache), Is.False);
            Assert.That(index.Scan(), Is.EqualTo(1));
            Assert.That(index.List().Single().Key, Is.EqualTo("save001"), "the cache file itself is no slot");
        }
    }
}
