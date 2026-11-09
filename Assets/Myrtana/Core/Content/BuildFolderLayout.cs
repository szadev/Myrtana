using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Myrtana.Core
{
    /// <summary>
    /// fix-167: the demo build folder (Docs/build.md "Folder layout"). Its root holds only what the owner looks at:
    /// the launcher <see cref="LauncherExe"/>, README_RU/EN, BUILD_INFO.txt and Screenshots/. Everything the game itself
    /// needs (the Unity player <see cref="PlayerExe"/> with its <c>_Data</c> folder, UnityPlayer.dll, the crash handler,
    /// MonoBleedingEdge, D3D12, dstorage, splash.bmp, GameData, Scenarios) is in <see cref="BinFolder"/>/. The player
    /// resolves all its paths from its own folder (<c>Application.dataPath</c>'s parent: GameData, Scenarios, logs), the
    /// launcher starts it with that folder as the working directory. <see cref="Relayout"/> converts a build of the old
    /// flat layout (player next to the launcher) by moving; it deletes nothing.
    /// </summary>
    public static class BuildFolderLayout
    {
        public const string BinFolder = "bin";
        public const string LauncherExe = "G3World.exe";
        public const string PlayerExe = "G3WorldPlayer.exe";
        public const string PlayerData = "G3WorldPlayer_Data";
        public const string BuildInfo = "BUILD_INFO.txt";
        public const string ScreenshotsFolder = "Screenshots";

        /// <summary>The files that stay in the build root.</summary>
        public static readonly string[] RootFiles = { LauncherExe, "README_RU.md", "README_EN.md", BuildInfo };
        /// <summary>The folders that stay in the build root (besides <see cref="BinFolder"/>).</summary>
        public static readonly string[] RootFolders = { ScreenshotsFolder };

        /// <summary>
        /// What a Unity Windows player build and the packaging put next to the player exe (DemoBuild deletes these from the
        /// root before a rebuild into a folder of the old layout, so no stale copy stays there). <c>G3World_Data</c> is the
        /// player's data folder from before the launcher (fix-152).
        /// </summary>
        public static readonly string[] PlayerEntries =
        {
            PlayerExe, PlayerData, "UnityPlayer.dll", "UnityCrashHandler64.exe", "MonoBleedingEdge", "D3D12",
            "dstorage.dll", "dstoragecore.dll", "splash.bmp", "GameData", "Scenarios", "G3World_Data",
        };

        public static string BinDir(string buildDir) => Path.Combine(buildDir, BinFolder);
        public static string PlayerPath(string buildDir) => Path.Combine(BinDir(buildDir), PlayerExe);

        static bool Is(string name, IEnumerable<string> names) => names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>True for a root entry that stays in the root (the launcher, READMEs, BUILD_INFO, Screenshots, bin).</summary>
        public static bool BelongsInRoot(string name, bool isDirectory) =>
            isDirectory ? Is(name, RootFolders) || string.Equals(name, BinFolder, StringComparison.OrdinalIgnoreCase) : Is(name, RootFiles);

        /// <summary>Root entries (names) that belong in bin/.</summary>
        public static List<string> Misplaced(string buildDir)
        {
            var list = new List<string>();
            if (!Directory.Exists(buildDir)) return list;
            foreach (var d in Directory.GetDirectories(buildDir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                if (!BelongsInRoot(Path.GetFileName(d), true)) list.Add(Path.GetFileName(d));
            foreach (var f in Directory.GetFiles(buildDir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                if (!BelongsInRoot(Path.GetFileName(f), false)) list.Add(Path.GetFileName(f));
            return list;
        }

        /// <summary>
        /// Deletes the known player entries (<see cref="PlayerEntries"/>, also <c>dstorage*.dll</c> and Unity's
        /// <c>*_DoNotShip</c> folders) from the build root;
        /// before a player build into <c>bin/</c> of a folder that still has the old flat layout. Returns the names.
        /// </summary>
        public static List<string> DeleteStalePlayer(string buildDir)
        {
            var removed = new List<string>();
            if (!Directory.Exists(buildDir)) return removed;
            foreach (var name in Misplaced(buildDir))
            {
                bool known = Is(name, PlayerEntries) ||
                             (name.StartsWith("dstorage", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) ||
                             name.EndsWith("_DoNotShip", StringComparison.Ordinal) || name.EndsWith("_ButDontShipItWithYourGame", StringComparison.Ordinal);
                if (!known) continue;
                string path = Path.Combine(buildDir, name);
                if (Directory.Exists(path)) DeleteTree(path); else File.Delete(path);
                removed.Add(name);
            }
            return removed;
        }

        /// <summary>
        /// Deletes a folder tree; a directory junction or symlink is unlinked, never followed (GameData may be a junction
        /// in a dev build folder).
        /// </summary>
        static void DeleteTree(string path)
        {
            var info = new DirectoryInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) { info.Delete(false); return; }
            foreach (var sub in info.GetDirectories()) DeleteTree(sub.FullName);
            foreach (var file in info.GetFiles()) { file.Attributes = FileAttributes.Normal; file.Delete(); }
            info.Delete(false);
        }

        /// <summary>
        /// Moves every root entry that belongs in <c>bin/</c> there (a rename on the same volume: instant also for GBs).
        /// An entry already present in <c>bin/</c> is left where it is and reported as a conflict. Returns a report.
        /// </summary>
        public static string Relayout(string buildDir, out int moved, out int conflicts)
        {
            moved = conflicts = 0;
            var sb = new StringBuilder();
            if (!Directory.Exists(buildDir)) return "no folder " + buildDir;
            string bin = BinDir(buildDir);
            Directory.CreateDirectory(bin);
            foreach (var name in Misplaced(buildDir))
            {
                string from = Path.Combine(buildDir, name), to = Path.Combine(bin, name);
                if (File.Exists(to) || Directory.Exists(to))
                {
                    conflicts++;
                    sb.AppendLine("  conflict: bin/" + name + " exists, " + name + " left in the root");
                    continue;
                }
                if (Directory.Exists(from)) Directory.Move(from, to); else File.Move(from, to);
                moved++;
                sb.AppendLine("  moved " + name + " -> bin/" + name);
            }
            return $"relayout {buildDir}: {moved} moved, {conflicts} conflicts\n" + sb.ToString().TrimEnd();
        }

        /// <summary>Problems of a finished build folder (empty = the layout is right).</summary>
        public static List<string> Check(string buildDir)
        {
            var problems = new List<string>();
            if (!Directory.Exists(buildDir)) { problems.Add("no folder " + buildDir); return problems; }
            if (!File.Exists(Path.Combine(buildDir, LauncherExe))) problems.Add("no " + LauncherExe + " in the root");
            if (!File.Exists(PlayerPath(buildDir))) problems.Add("no " + BinFolder + "/" + PlayerExe);
            if (!Directory.Exists(Path.Combine(BinDir(buildDir), PlayerData))) problems.Add("no " + BinFolder + "/" + PlayerData);
            foreach (var name in Misplaced(buildDir)) problems.Add(name + " in the root (belongs in " + BinFolder + "/)");
            return problems;
        }

        /// <summary>One line for BUILD_INFO.txt.</summary>
        public static string Describe() =>
            $"layout: root = {LauncherExe} (start this), README_RU/EN.md, {BuildInfo}, {ScreenshotsFolder}/; " +
            $"{BinFolder}/ = the game ({PlayerExe} + {PlayerData}, UnityPlayer.dll, GameData, Scenarios, splash.bmp, ...)";
    }
}
