using System;
using System.Collections.Generic;
using System.IO;

namespace Myrtana.Session
{
    /// <summary>
    /// fix-155 [owner]: crash-safe files (save games and their pictures). A write goes to <c>&lt;file&gt;.tmp</c>, is flushed
    /// to the disk (<see cref="FileStream.Flush(bool)"/> true: through the OS cache, so a power loss after the swap cannot
    /// leave a hole) and then replaces the file in one step (<see cref="File.Replace(string,string,string,bool)"/>), the
    /// old one kept as <c>&lt;file&gt;.bak</c>. A reader takes the file if it is valid, else the backup: a crash at any
    /// moment leaves either the new or the previous version readable, never neither.
    /// </summary>
    public static class AtomicFile
    {
        public const string TempSuffix = ".tmp";
        public const string BackupSuffix = ".bak";

        public static string TempOf(string path) => path + TempSuffix;
        public static string BackupOf(string path) => path + BackupSuffix;

        /// <summary>Writes <paramref name="path"/> through a flushed temporary file; the previous file becomes the backup.</summary>
        public static void Write(string path, Action<Stream> write)
        {
            if (write == null) throw new ArgumentNullException(nameof(write));
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temp = TempOf(path);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                write(stream);
                stream.Flush(true);
            }
            Commit(temp, path);
        }

        public static void WriteBytes(string path, byte[] data) =>
            Write(path, s => s.Write(data ?? throw new ArgumentNullException(nameof(data)), 0, data.Length));

        /// <summary>As <see cref="WriteBytes(string, byte[])"/>, the first <paramref name="count"/> bytes (a reused buffer, fix-166).</summary>
        public static void WriteBytes(string path, byte[] data, int count)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (count < 0 || count > data.Length) throw new ArgumentOutOfRangeException(nameof(count));
            Write(path, s => s.Write(data, 0, count));
        }

        /// <summary>The flushed temporary file takes the place of <paramref name="path"/>; the old file becomes the backup.</summary>
        static void Commit(string temp, string path)
        {
            if (!File.Exists(path))
            {
                File.Move(temp, path);
                return;
            }
            try
            {
                File.Replace(temp, path, BackupOf(path), ignoreMetadataErrors: true);
            }
            catch (PlatformNotSupportedException)
            {
                ReplaceByCopy(temp, path);
            }
            catch (IOException) when (File.Exists(temp) && File.Exists(path))
            {
                ReplaceByCopy(temp, path); // a file system without ReplaceFile (some network / FAT drives)
            }
        }

        static void ReplaceByCopy(string temp, string path)
        {
            File.Copy(path, BackupOf(path), overwrite: true);
            File.Delete(path);
            File.Move(temp, path);
        }

        /// <summary>
        /// The contents of <paramref name="path"/> when <paramref name="isValid"/> accepts them, else those of its backup;
        /// null when neither is there and valid. <paramref name="fromBackup"/> tells which one it was.
        /// </summary>
        public static byte[] ReadValid(string path, Func<byte[], bool> isValid, out bool fromBackup)
        {
            fromBackup = false;
            foreach (var (data, backup) in ReadAll(path, isValid))
            {
                fromBackup = backup;
                return data;
            }
            return null;
        }

        /// <summary>The valid versions of <paramref name="path"/>, the file first, then the backup (a caller whose load fails on the first tries the next).</summary>
        public static IEnumerable<(byte[] data, bool backup)> ReadAll(string path, Func<byte[], bool> isValid)
        {
            var main = TryRead(path);
            if (main != null && (isValid == null || isValid(main))) yield return (main, false);
            var bak = TryRead(BackupOf(path));
            if (bak != null && (isValid == null || isValid(bak))) yield return (bak, true);
        }

        /// <summary>The file or its backup exists.</summary>
        public static bool Exists(string path) => File.Exists(path) || File.Exists(BackupOf(path));

        /// <summary>Removes the file, its backup and a leftover temporary file.</summary>
        public static void Delete(string path)
        {
            foreach (var p in new[] { path, BackupOf(path), TempOf(path) })
                if (File.Exists(p)) File.Delete(p);
        }

        static byte[] TryRead(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>A whole JPEG: starts with SOI (FF D8) and ends with EOI (FF D9), as Unity's EncodeToJPG writes it.</summary>
        public static bool IsJpeg(byte[] data) =>
            data != null && data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8 &&
            data[data.Length - 2] == 0xFF && data[data.Length - 1] == 0xD9;
    }

    /// <summary>fix-155: the slot names in a save directory — every <c>&lt;slot&gt;.sav</c> and every lone backup <c>&lt;slot&gt;.sav.bak</c>.</summary>
    public static class SaveSlotFiles
    {
        /// <summary>The distinct slot names among <paramref name="files"/> (paths or names) with <paramref name="extension"/> (".sav") or its backup, sorted.</summary>
        public static IReadOnlyList<string> Slots(IEnumerable<string> files, string extension)
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            string backup = extension + AtomicFile.BackupSuffix;
            foreach (var file in files ?? Array.Empty<string>())
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) && name.Length > extension.Length)
                    names.Add(name.Substring(0, name.Length - extension.Length));
                else if (name.EndsWith(backup, StringComparison.OrdinalIgnoreCase) && name.Length > backup.Length)
                    names.Add(name.Substring(0, name.Length - backup.Length));
            }
            return new List<string>(names);
        }
    }
}
