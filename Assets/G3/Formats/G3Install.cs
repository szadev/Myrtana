using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace G3.Formats
{
    /// <summary>
    /// Finds the Gothic 3 installation on this machine, so no tool carries a machine-specific default path.
    /// Order: env <c>G3_DATA</c> (the Data folder), env <c>G3_DIR</c> (the install folder), the Steam libraries listed in
    /// <c>libraryfolders.vdf</c>, then the usual Steam / GOG / retail folders on every fixed drive.
    /// A folder counts as an install when <c>Data/Templates.pak</c> exists next to <c>Gothic3.exe</c> or <c>Gothic3Final.exe</c>.
    /// </summary>
    public static class G3Install
    {
        public const string DataEnv = "G3_DATA", DirEnv = "G3_DIR";

        static string _cached;
        static bool _searched;

        /// <summary>The install folder (the one holding Gothic3.exe and Data/), or null when none was found.</summary>
        public static string Dir
        {
            get
            {
                if (!_searched) { _cached = Find(); _searched = true; }
                return _cached;
            }
        }

        /// <summary>The Data folder of the install, or null.</summary>
        public static string DataDir => Dir == null ? null : Path.Combine(Dir, "Data");

        /// <summary><see cref="DataDir"/>, or a placeholder that names the env variable (for messages and old callers that need a string).</summary>
        public static string DataDirOrHint => DataDir ?? "<Gothic 3 not found: set " + DataEnv + " to its Data folder>";

        /// <summary>A stored path setting, or null when it is unset/empty (Unity's EditorPrefs returns "" for a missing key).</summary>
        public static string Pref(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

        public static bool IsInstall(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            if (!File.Exists(Path.Combine(dir, "Data", "Templates.pak"))) return false;
            return File.Exists(Path.Combine(dir, "Gothic3.exe")) || File.Exists(Path.Combine(dir, "Gothic3Final.exe"));
        }

        static string Find()
        {
            string data = System.Environment.GetEnvironmentVariable(DataEnv);
            if (!string.IsNullOrEmpty(data))
            {
                string d = Path.GetDirectoryName(Path.GetFullPath(data.TrimEnd('\\', '/')));
                if (IsInstall(d)) return d;
            }
            string dir = System.Environment.GetEnvironmentVariable(DirEnv);
            if (IsInstall(dir)) return Path.GetFullPath(dir);
            foreach (string c in Candidates())
                if (IsInstall(c)) return c;
            return null;
        }

        /// <summary>Folders that may hold Gothic 3, most likely first.</summary>
        public static IEnumerable<string> Candidates()
        {
            var steamRoots = new List<string>();
            string pf86 = System.Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            string pf = System.Environment.GetEnvironmentVariable("ProgramFiles");
            if (!string.IsNullOrEmpty(pf86)) steamRoots.Add(Path.Combine(pf86, "Steam"));
            if (!string.IsNullOrEmpty(pf)) steamRoots.Add(Path.Combine(pf, "Steam"));

            foreach (string root in steamRoots)
            {
                string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                string text;
                try { text = File.ReadAllText(vdf); } catch { continue; }
                foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
                    yield return Path.Combine(m.Groups[1].Value.Replace("\\\\", "\\"), "steamapps", "common", "Gothic 3");
                yield return Path.Combine(root, "steamapps", "common", "Gothic 3");
            }

            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); } catch { yield break; }
            foreach (var drive in drives)
            {
                bool ready;
                try { ready = drive.DriveType == DriveType.Fixed && drive.IsReady; } catch { ready = false; }
                if (!ready) continue;
                string r = drive.RootDirectory.FullName;
                yield return Path.Combine(r, "SteamLibrary", "steamapps", "common", "Gothic 3");
                yield return Path.Combine(r, "Steam", "steamapps", "common", "Gothic 3");
                yield return Path.Combine(r, "Program Files (x86)", "Steam", "steamapps", "common", "Gothic 3");
                yield return Path.Combine(r, "GOG Games", "Gothic 3");
                yield return Path.Combine(r, "Program Files (x86)", "GOG Galaxy", "Games", "Gothic 3");
                yield return Path.Combine(r, "Games", "Gothic 3");
                yield return Path.Combine(r, "Program Files (x86)", "JoWooD", "Gothic III");
                yield return Path.Combine(r, "Program Files (x86)", "Gothic 3");
            }
        }
    }
}
