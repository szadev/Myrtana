using System;
using System.IO;
using System.Linq;

namespace Myrtana.Import.G3
{
    /// <summary>
    /// File moves for the GameData folder (<see cref="Myrtana.Core.GameDataLayout"/>): the one-shot migration out of
    /// <c>Assets/StreamingAssets</c> and the incremental copy into a player build.
    /// </summary>
    public static class GameDataFiles
    {
        /// <summary>
        /// Moves the tree <paramref name="from"/> to <paramref name="to"/> and deletes its Unity <c>.meta</c> files (and the
        /// <c>.meta</c> of <paramref name="from"/> itself). A missing target is one directory rename; otherwise files are
        /// moved one by one, replacing older copies. The emptied source tree is removed.
        /// </summary>
        public static string Move(string from, string to)
        {
            from = Path.GetFullPath(from).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            to = Path.GetFullPath(to).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(from)) return $"nothing to move: {from} does not exist";
            DeleteIfExists(from + ".meta");
            int metas = 0;
            foreach (var meta in Directory.EnumerateFiles(from, "*.meta", SearchOption.AllDirectories).ToList())
            {
                File.Delete(meta);
                metas++;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(to));
            if (!Directory.Exists(to) && string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase))
            {
                Directory.Move(from, to);
                return $"moved {from} -> {to} (directory rename), {metas} .meta files deleted";
            }
            int moved = 0;
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).ToList())
            {
                string target = Path.Combine(to, file.Substring(from.Length + 1));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                DeleteIfExists(target);
                File.Move(file, target);
                moved++;
            }
            Directory.Delete(from, true);
            return $"moved {moved} files {from} -> {to}, {metas} .meta files deleted";
        }

        /// <summary>
        /// Copies every file of <paramref name="from"/> to <paramref name="to"/> that is missing there or differs in size or
        /// write time (a re-build copies only what changed). Returns (copied, unchanged).
        /// </summary>
        public static (int copied, int unchanged) Mirror(string from, string to)
        {
            from = Path.GetFullPath(from).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            int copied = 0, unchanged = 0;
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                string target = Path.Combine(to, file.Substring(from.Length + 1));
                var src = new FileInfo(file);
                var dst = new FileInfo(target);
                if (dst.Exists && dst.Length == src.Length && dst.LastWriteTimeUtc == src.LastWriteTimeUtc) { unchanged++; continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
                File.SetLastWriteTimeUtc(target, src.LastWriteTimeUtc);
                copied++;
            }
            return (copied, unchanged);
        }

        static void DeleteIfExists(string file)
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }
}
