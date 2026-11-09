using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Myrtana.Core;

namespace Myrtana.Session
{
    /// <summary>
    /// fix-222 [owner: the title and the Save/Load page opened with a delay]: the headers of the saves in a directory,
    /// cached. Listing used to open every <c>.sav</c> for its header on each menu refresh (≈900 saves: 2.6 s from a cold
    /// disk, 0.4 s warm). The index keeps each slot's header with the size and write time of its file and backup
    /// (<see cref="FileStamp"/>); a <see cref="Scan"/> lists the directory (no file opened) and reads only the slots whose
    /// stamps changed. It is persisted next to the saves (<see cref="CacheFileName"/>), so a start-up scan opens only the
    /// files written since the last run. A slot written or deleted by the game is refreshed at once (<see cref="Update"/>).
    /// Thread-safe: the first scan runs on a worker thread at start-up; the menus read the last snapshot.
    /// </summary>
    public sealed class SaveIndex
    {
        public const string CacheFileName = "saves.index";
        const int Magic = 0x58444953; // "SIDX"
        const int CacheVersion = 1;

        /// <summary>A file's size and last write time; <see cref="Missing"/> when there is no such file.</summary>
        public readonly struct FileStamp : IEquatable<FileStamp>
        {
            public FileStamp(long length, long writeTicks)
            {
                Length = length;
                WriteTicks = writeTicks;
            }

            public static readonly FileStamp Missing = new FileStamp(-1, 0);
            public long Length { get; }
            public long WriteTicks { get; }
            public bool Exists => Length >= 0;
            public bool Equals(FileStamp other) => Length == other.Length && WriteTicks == other.WriteTicks;
            public override bool Equals(object obj) => obj is FileStamp s && Equals(s);
            public override int GetHashCode() => unchecked((int)Length * 397 ^ WriteTicks.GetHashCode());
            public static bool operator ==(FileStamp a, FileStamp b) => a.Equals(b);
            public static bool operator !=(FileStamp a, FileStamp b) => !a.Equals(b);
        }

        sealed class Entry
        {
            public FileStamp Main, Backup;
            /// <summary>The file's header, else the backup's; null when neither is readable.</summary>
            public SaveHeader Header;
        }

        readonly string _directory, _extension;
        readonly Func<string, SaveHeader> _readHeader;
        readonly Func<string, FileStamp> _stat;
        readonly Func<string, IEnumerable<KeyValuePair<string, FileStamp>>> _listFiles;
        readonly object _gate = new object();   // one scan or update at a time
        readonly object _lock = new object();   // the snapshot swap
        Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        bool _dirty;

        /// <param name="directory">The save directory.</param>
        /// <param name="extension">The save files' extension (".sav").</param>
        /// <param name="readHeader">Reads a file's header (null: unreadable); default <see cref="ReadHeaderFile"/>.</param>
        /// <param name="listFiles">File names with stamps in a directory (tests); default the file system.</param>
        /// <param name="stat">One file's stamp (tests); default the file system.</param>
        public SaveIndex(string directory, string extension, Func<string, SaveHeader> readHeader = null,
                         Func<string, IEnumerable<KeyValuePair<string, FileStamp>>> listFiles = null,
                         Func<string, FileStamp> stat = null)
        {
            _directory = directory;
            _extension = extension;
            _readHeader = readHeader ?? ReadHeaderFile;
            _listFiles = listFiles ?? ListFiles;
            _stat = stat ?? Stat;
        }

        /// <summary>Headers read by the last <see cref="Scan"/> (the rest came from the index).</summary>
        public int LastScanReads { get; private set; }
        /// <summary>Slots in the index (readable or not).</summary>
        public int Count { get { lock (_lock) return _entries.Count; } }
        /// <summary>Raised (on the scanning thread) for a slot whose file and backup are both unreadable, once per change.</summary>
        public Action<string> Unreadable { get; set; }

        string PathOf(string slot) => Path.Combine(_directory, slot + _extension);

        /// <summary>
        /// Lists the directory and brings the index up to date: slots whose file or backup changed are read again, gone
        /// ones dropped. Returns the number of headers read.
        /// </summary>
        public int Scan()
        {
            lock (_gate)
            {
                var stamps = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in _listFiles(_directory) ?? Enumerable.Empty<KeyValuePair<string, FileStamp>>())
                    stamps[kv.Key] = kv.Value;
                Dictionary<string, Entry> old;
                lock (_lock) old = _entries;
                var fresh = new Dictionary<string, Entry>(StringComparer.Ordinal);
                int reads = 0;
                foreach (var slot in SaveSlotFiles.Slots(stamps.Keys, _extension))
                {
                    var main = stamps.TryGetValue(slot + _extension, out var m) ? m : FileStamp.Missing;
                    var backup = stamps.TryGetValue(slot + _extension + AtomicFile.BackupSuffix, out var b) ? b : FileStamp.Missing;
                    if (old.TryGetValue(slot, out var known) && known.Main == main && known.Backup == backup) fresh[slot] = known;
                    else
                    {
                        fresh[slot] = Read(slot, main, backup);
                        reads++;
                    }
                }
                lock (_lock)
                {
                    if (reads > 0 || fresh.Count != old.Count) _dirty = true;
                    _entries = fresh;
                }
                LastScanReads = reads;
                return reads;
            }
        }

        /// <summary>The slot was written or deleted: its entry is read again from the disk (or dropped).</summary>
        public void Update(string slot)
        {
            if (string.IsNullOrEmpty(slot)) return;
            lock (_gate)
            {
                string path = PathOf(slot);
                var main = _stat(path);
                var backup = _stat(AtomicFile.BackupOf(path));
                Entry entry = main.Exists || backup.Exists ? Read(slot, main, backup) : null;
                lock (_lock)
                {
                    var next = new Dictionary<string, Entry>(_entries, StringComparer.Ordinal);
                    if (entry != null) next[slot] = entry;
                    else next.Remove(slot);
                    _entries = next;
                    _dirty = true;
                }
            }
        }

        /// <summary>
        /// fix-251: a save of <paramref name="header"/> is being written to the slot (worker thread): the lists show it
        /// at once. The entry carries no file stamps, so the <see cref="Update"/> after the write (or any scan) reads the
        /// file again.
        /// </summary>
        public void Expect(string slot, SaveHeader header)
        {
            if (string.IsNullOrEmpty(slot) || header == null) return;
            lock (_lock)
            {
                var next = new Dictionary<string, Entry>(_entries, StringComparer.Ordinal)
                {
                    [slot] = new Entry { Main = FileStamp.Missing, Backup = FileStamp.Missing, Header = header },
                };
                _entries = next;
            }
        }

        Entry Read(string slot, FileStamp main, FileStamp backup)
        {
            string path = PathOf(slot);
            var header = (main.Exists ? _readHeader(path) : null) ?? (backup.Exists ? _readHeader(AtomicFile.BackupOf(path)) : null);
            if (header == null) Unreadable?.Invoke(slot);
            return new Entry { Main = main, Backup = backup, Header = header };
        }

        /// <summary>The slot's header from the index (null: not there or unreadable).</summary>
        public SaveHeader Header(string slot)
        {
            lock (_lock) return slot != null && _entries.TryGetValue(slot, out var e) ? e.Header : null;
        }

        /// <summary>Every readable slot with its header, newest first (slots of the same time by name).</summary>
        public List<KeyValuePair<string, SaveHeader>> List()
        {
            Dictionary<string, Entry> snapshot;
            lock (_lock) snapshot = _entries;
            return snapshot.Where(kv => kv.Value.Header != null)
                           .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                           .Select(kv => new KeyValuePair<string, SaveHeader>(kv.Key, kv.Value.Header))
                           .OrderByDescending(kv => kv.Value.SavedAtUtc)
                           .ToList();
        }

        // ---- persistence ----

        /// <summary>The index changed since it was last loaded or written.</summary>
        public bool Dirty { get { lock (_lock) return _dirty; } }

        /// <summary>Writes the index (slots, stamps, headers).</summary>
        public void WriteTo(Stream output)
        {
            Dictionary<string, Entry> snapshot;
            lock (_lock)
            {
                snapshot = _entries;
                _dirty = false;
            }
            using (var w = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                w.Write(Magic);
                w.Write(CacheVersion);
                w.Write(snapshot.Count);
                foreach (var kv in snapshot.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    var e = kv.Value;
                    w.Write(kv.Key);
                    w.Write(e.Main.Length);
                    w.Write(e.Main.WriteTicks);
                    w.Write(e.Backup.Length);
                    w.Write(e.Backup.WriteTicks);
                    w.Write(e.Header != null);
                    if (e.Header == null) continue;
                    w.Write(e.Header.PlaySeconds);
                    w.Write(e.Header.Title ?? string.Empty);
                    w.Write(e.Header.SavedAtUtc.Ticks);
                    w.Write(e.Header.Packs.Count);
                    foreach (var p in e.Header.Packs)
                    {
                        w.Write(p.Id ?? string.Empty);
                        w.Write(p.ContentHash ?? string.Empty);
                    }
                }
            }
        }

        /// <summary>Takes the entries of a written index (<see cref="WriteTo"/>); false (index unchanged) when it is not one.</summary>
        public bool ReadFrom(Stream input)
        {
            var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
            try
            {
                using (var r = new BinaryReader(input, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    if (r.ReadInt32() != Magic || r.ReadInt32() != CacheVersion) return false;
                    int count = r.ReadInt32();
                    if (count < 0) return false;
                    for (int i = 0; i < count; i++)
                    {
                        string slot = r.ReadString();
                        var e = new Entry
                        {
                            Main = new FileStamp(r.ReadInt64(), r.ReadInt64()),
                            Backup = new FileStamp(r.ReadInt64(), r.ReadInt64()),
                        };
                        if (r.ReadBoolean())
                        {
                            double play = r.ReadDouble();
                            string title = r.ReadString();
                            long ticks = r.ReadInt64();
                            int packs = r.ReadInt32();
                            if (packs < 0 || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return false;
                            var stamps = new PackStamp[packs];
                            for (int k = 0; k < packs; k++) stamps[k] = new PackStamp(r.ReadString(), r.ReadString());
                            e.Header = new SaveHeader(stamps, play, title, new DateTime(ticks, DateTimeKind.Utc));
                        }
                        entries[slot] = e;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is FormatException || ex is OverflowException)
            {
                return false;
            }
            lock (_gate)
            lock (_lock)
            {
                _entries = entries;
                _dirty = false;
            }
            return true;
        }

        /// <summary>Loads the persisted index from <paramref name="path"/>; false when there is none or it is damaged.</summary>
        public bool LoadCache(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16)) return ReadFrom(s);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>Writes the index to <paramref name="path"/> when it changed (atomically); false when nothing was written.</summary>
        public bool SaveCache(string path)
        {
            lock (_cacheGate)
            {
                if (!Dirty) return false;
                try
                {
                    AtomicFile.Write(path, WriteTo);
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    return false;
                }
            }
        }
        readonly object _cacheGate = new object();

        // ---- file system ----

        /// <summary>A save file's header; null when it is missing, cut short or no save.</summary>
        public static SaveHeader ReadHeaderFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096))
                    return SaveFile.ReadHeader(s);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException ||
                                       ex is OverflowException || ex is ArgumentException)
            {
                return null;
            }
        }

        static IEnumerable<KeyValuePair<string, FileStamp>> ListFiles(string directory)
        {
            var list = new List<KeyValuePair<string, FileStamp>>();
            try
            {
                var dir = new DirectoryInfo(directory);
                if (!dir.Exists) return list;
                foreach (var f in dir.EnumerateFiles())
                {
                    try
                    {
                        list.Add(new KeyValuePair<string, FileStamp>(f.Name, new FileStamp(f.Length, f.LastWriteTimeUtc.Ticks)));
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        // removed while listing (a save being replaced): its slot is listed by the other file or not at all
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // an unreadable directory lists as empty
            }
            return list;
        }

        static FileStamp Stat(string path)
        {
            try
            {
                var f = new FileInfo(path);
                return f.Exists ? new FileStamp(f.Length, f.LastWriteTimeUtc.Ticks) : FileStamp.Missing;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return FileStamp.Missing;
            }
        }
    }
}
