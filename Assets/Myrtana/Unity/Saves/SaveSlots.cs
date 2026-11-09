using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Myrtana.Core;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// Save games on disk: <c>&lt;persistentDataPath&gt;/Saves/&lt;slot&gt;.sav</c> and its picture <c>.jpg</c>. fix-155: every
    /// write (manual, quick, auto, the hidden crash-recovery save, the pictures) is atomic (<see cref="Session.AtomicFile"/>):
    /// a flushed temporary file replaces the slot, the previous version stays as <c>.sav.bak</c> / <c>.jpg.bak</c>; a slot
    /// whose file is cut short or unreadable is read (listed, loaded) from that backup.
    /// </summary>
    public sealed class SaveSlots
    {
        public const string QuickSlot = Session.SaveGameList.QuickSlot;
        public const string AutoSlot = Session.SaveGameList.AutoSlot;
        const string Extension = ".sav";
        /// <summary>The save's picture (<see cref="SaveThumbnail"/>), next to it.</summary>
        const string ThumbnailExtension = ".jpg";

        public SaveSlots(string directory = null)
        {
            Directory = directory ?? Path.Combine(Application.persistentDataPath, "Saves");
            _index = new Session.SaveIndex(Directory, Extension)
            {
                Unreadable = slot => Debug.LogWarning($"[Myrtana] save {PathOf(slot)} is not readable"),
            };
        }

        public string Directory { get; }

        // fix-222: the slot list from a cached index (headers read once, persisted), built on a worker thread at start-up
        readonly Session.SaveIndex _index;
        System.Threading.Tasks.Task _indexing;
        bool _migrated;

        string CachePath => Path.Combine(Directory, Session.SaveIndex.CacheFileName);

        /// <summary>
        /// fix-222: the legacy-slot migration, then the index of the saves on a worker thread (the persisted index plus a
        /// directory listing; only saves written since the last run are opened). Call as early as possible (start-up logos).
        /// </summary>
        public void StartIndexing()
        {
            if (_indexing != null) return;
            MigrateLegacySlots();
            _indexing = System.Threading.Tasks.Task.Run(() =>
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                bool cached = _index.LoadCache(CachePath);
                double cacheMs = watch.Elapsed.TotalMilliseconds;
                int reads = _index.Scan();
                double scanMs = watch.Elapsed.TotalMilliseconds - cacheMs;
                bool written = _index.SaveCache(CachePath);
                Debug.Log($"[Myrtana] save index: {_index.Count} slots, {reads} headers read ({(cached ? "cache" : "no cache")} {cacheMs:0} ms, scan {scanMs:0} ms" +
                          $"{(written ? ", cache written" : "")}, worker thread)");
            });
        }

        /// <summary>Waits for the start-up index (rare: only when a list is wanted before it is done).</summary>
        void EnsureIndexed()
        {
            if (_indexing == null) StartIndexing();
            if (_indexing.IsCompleted) return;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try { _indexing.Wait(); }
            catch (AggregateException ex) { Debug.LogWarning($"[Myrtana] save index: {ex.GetBaseException().Message}"); }
            Debug.Log($"[Myrtana] save index: waited {watch.Elapsed.TotalMilliseconds:0} ms for the start-up scan");
        }

        /// <summary>The slot was written or removed: its index entry follows (any thread).</summary>
        void Changed(string slot)
        {
            _index.Update(slot);
            // the persisted index catches up off the main thread (a lost write only costs a re-read next run)
            System.Threading.Tasks.Task.Run(() => _index.SaveCache(CachePath));
        }

        public string PathOf(string slot) => Path.Combine(Directory, slot + Extension);

        // fix-251: saves and pictures are written on a worker, one after the other (atomic as before); every reader of the
        // slots waits for the queued writes first, so a load, a list or a delete never sees a save half-way.
        readonly object _writeGate = new object();
        System.Threading.Tasks.Task _writes = System.Threading.Tasks.Task.CompletedTask;

        /// <summary>Queues <paramref name="work"/> after the writes already queued (worker thread; exceptions are the work's own).</summary>
        public System.Threading.Tasks.Task Enqueue(Action work)
        {
            lock (_writeGate)
            {
                _writes = _writes.ContinueWith(_ => work(), System.Threading.Tasks.TaskScheduler.Default);
                return _writes;
            }
        }

        /// <summary>fix-251: a save with <paramref name="header"/> is queued for the slot: the index lists it already.</summary>
        public void Expect(string slot, SaveHeader header) => _index.Expect(slot, header);

        /// <summary>True while a queued save or picture is still being written.</summary>
        public bool Writing { get { lock (_writeGate) return !_writes.IsCompleted; } }

        /// <summary>Waits for the queued writes (readers; the quit). Logs a wait that was needed.</summary>
        public void WaitForWrites(string why = null)
        {
            System.Threading.Tasks.Task t;
            lock (_writeGate) t = _writes;
            if (t.IsCompleted) return;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try { t.Wait(); }
            catch (AggregateException ex) { Debug.LogWarning($"[Myrtana] save write: {ex.GetBaseException().Message}"); }
            Debug.Log($"[Myrtana] save: waited {watch.Elapsed.TotalMilliseconds:0.0} ms for the queued writes{(why != null ? " (" + why + ")" : "")}");
        }

        public bool Exists(string slot) { WaitForWrites("exists"); return Session.AtomicFile.Exists(PathOf(slot)); }
        public string ThumbnailPathOf(string slot) => Path.Combine(Directory, slot + ThumbnailExtension);

        /// <summary>Stores the save's picture; null removes an old one so it never shows another game's view.</summary>
        public void WriteThumbnail(string slot, byte[] jpeg)
        {
            string path = ThumbnailPathOf(slot);
            try
            {
                if (jpeg == null) { Session.AtomicFile.Delete(path); return; }
                Session.AtomicFile.WriteBytes(path, jpeg);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Debug.LogWarning($"[Myrtana] save picture {path}: {ex.Message}");
            }
        }

        /// <summary>The save's picture, or null.</summary>
        public byte[] ReadThumbnail(string slot)
        {
            WaitForWrites("picture");
            return Session.AtomicFile.ReadValid(ThumbnailPathOf(slot), Session.AtomicFile.IsJpeg, out _);
        }

        /// <summary>Writes the slot atomically (fix-155): the previous save becomes the slot's backup.</summary>
        public void Write(string slot, Action<Stream> write)
        {
            try { Session.AtomicFile.Write(PathOf(slot), write); }
            finally { Changed(slot); }
        }

        /// <summary>Writes a save already serialized (the crash-recovery snapshot; safe on a worker thread).</summary>
        public void WriteBytes(string slot, byte[] save)
        {
            try { Session.AtomicFile.WriteBytes(PathOf(slot), save); }
            finally { Changed(slot); }
        }

        /// <summary>The first <paramref name="count"/> bytes of <paramref name="save"/> (the recovery snapshot's reused buffer, fix-166).</summary>
        public void WriteBytes(string slot, byte[] save, int count)
        {
            try { Session.AtomicFile.WriteBytes(PathOf(slot), save, count); }
            finally { Changed(slot); }
        }

        /// <summary>The slot's save: the file when whole (<see cref="SaveFile.IsComplete"/>), else its backup; throws when neither is.</summary>
        public byte[] Read(string slot)
        {
            WaitForWrites("load");
            foreach (var version in Versions(slot)) return version;
            throw new InvalidDataException($"save {slot}: neither the file nor its backup is complete");
        }

        /// <summary>
        /// fix-155: the slot's whole saves to try, the file first, then its backup (<c>.sav.bak</c>, the version before the
        /// last write); a load that fails on the first falls back to the next.
        /// </summary>
        public IEnumerable<byte[]> Versions(string slot)
        {
            WaitForWrites("load");
            foreach (var (data, backup) in Session.AtomicFile.ReadAll(PathOf(slot), SaveFile.IsComplete))
            {
                if (backup) Debug.LogWarning($"[Myrtana] save {slot}: the file is damaged or missing, its backup ({Session.AtomicFile.BackupOf(PathOf(slot))}) is used");
                yield return data;
            }
        }

        /// <summary>The save time in the slot's header (the backup's when the file is unreadable); null when neither is readable.</summary>
        public DateTime? SavedAt(string slot) => Header(slot)?.SavedAtUtc;

        /// <summary>The slot's header: the file's, else its backup's; null when neither is readable.</summary>
        public SaveHeader Header(string slot)
        {
            WaitForWrites("header");
            string path = PathOf(slot);
            return ReadHeader(path) ?? ReadHeader(Session.AtomicFile.BackupOf(path));
        }

        static SaveHeader ReadHeader(string path) => Session.SaveIndex.ReadHeaderFile(path);

        /// <summary>fix-107: one quicksave and one autosave file again; the newer of fix-82's two files keeps the slot's name (<see cref="Session.SaveSlotMigration"/>).</summary>
        public void MigrateLegacySlots()
        {
            if (_migrated) return;
            _migrated = true;
            try
            {
                foreach (var slot in Session.SaveSlotMigration.Run(Directory, SavedAt, Extension, ThumbnailExtension))
                    Debug.Log($"[Myrtana] save {slot}: the newer {Session.SaveSlotMigration.Legacy(slot)} took its place (the older file stays as {Session.SaveSlotMigration.Legacy(slot)})");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Debug.LogWarning($"[Myrtana] save slot migration: {ex.Message}");
            }
        }

        /// <summary>Removes the slot with its backup and picture.</summary>
        public void Delete(string slot)
        {
            WaitForWrites("delete");
            try
            {
                Session.AtomicFile.Delete(PathOf(slot));
                Session.AtomicFile.Delete(ThumbnailPathOf(slot));
            }
            finally { Changed(slot); }
        }

        /// <summary>
        /// Every readable slot with its header, newest first (fix-155: a slot whose file is gone or unreadable is listed by
        /// its backup). Includes the hidden crash-recovery save; the menu lists and Continue leave it out. fix-222: from the
        /// index (no file opened); <paramref name="rescan"/> lists the directory first (files changed outside the game; only
        /// those are opened).
        /// </summary>
        public List<KeyValuePair<string, SaveHeader>> List(bool rescan = false)
        {
            EnsureIndexed();
            if (rescan && _index.Scan() > 0) System.Threading.Tasks.Task.Run(() => _index.SaveCache(CachePath));
            return _index.List();
        }
    }
}
