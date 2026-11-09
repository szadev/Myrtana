using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Myrtana.Unity.Editor
{
    /// <summary>
    /// fix-152: the demo's <c>G3World.exe</c> is a native launcher (Tools/Launcher/G3Launcher.c, Docs/game-start.md
    /// "Start-up splash"): it shows G3's splash.bmp on the desktop like Gothic3.exe and starts the Unity player
    /// <see cref="PlayerExe"/> (with <c>G3WorldPlayer_Data</c>; Unity names the data folder after the exe). Plain Win32 C
    /// with the static CRT: nothing to install on the player's PC, ~130 KB, the logo is up in milliseconds.
    /// <see cref="Build"/> compiles it into the build folder with Tools/Launcher/build.cmd (MSVC through vswhere, else
    /// MinGW gcc) and gives it Gothic 3's icon (fix-165, <see cref="GameIcon"/>; else the player's). fix-167: the
    /// launcher is the only exe in the build root; it starts <c>bin\G3WorldPlayer.exe</c> in <c>bin</c> (<see cref="BinDir"/>).
    /// </summary>
    public static class LauncherBuild
    {
        public const string LauncherExe = Myrtana.Core.BuildFolderLayout.LauncherExe;
        public const string PlayerExe = Myrtana.Core.BuildFolderLayout.PlayerExe;
        public const string PlayerData = Myrtana.Core.BuildFolderLayout.PlayerData;
        /// <summary>fix-167: the player and everything it needs is in this subfolder of the build folder; the launcher in the root.</summary>
        public const string BinDir = Myrtana.Core.BuildFolderLayout.BinFolder;

        static string ProjectDir => Path.GetDirectoryName(Application.dataPath);
        static string Script => Path.Combine(ProjectDir, "Tools", "Launcher", "build.cmd");

        /// <summary>The C compiler build.cmd will use, or null (preflight).</summary>
        public static string FindCompiler()
        {
            string vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
            if (File.Exists(vswhere))
            {
                string vs = Run(vswhere, "-latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath", out _).Trim();
                if (vs.Length > 0 && File.Exists(Path.Combine(vs, "VC", "Auxiliary", "Build", "vcvars64.bat"))) return "MSVC (" + vs + ")";
            }
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try { if (dir.Length > 0 && File.Exists(Path.Combine(dir.Trim('"'), "gcc.exe"))) return "gcc (" + dir + ")"; }
                catch (ArgumentException) { }
            }
            return null;
        }

        /// <summary>Compiles the launcher to <paramref name="outputDir"/>/G3World.exe and copies the player's icon onto it.</summary>
        public static bool Build(string outputDir, out string report)
        {
            string exe = Path.Combine(outputDir, LauncherExe);
            string output = Run(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", $"/d /c \"\"{Script}\" \"{exe}\"\"", out int code);
            if (code != 0 || !File.Exists(exe))
            {
                report = $"launcher NOT built (build.cmd exit {code}): {output.Trim()}";
                return false;
            }
            // fix-165: Gothic 3's icon (as on the player, GameIconBuildStep); without the install the player's is copied
            string player = Path.Combine(outputDir, BinDir, PlayerExe);
            if (!File.Exists(player)) player = Path.Combine(outputDir, PlayerExe); // a build of the flat layout before fix-167
            string ico = GameIconBuildStep.Ensure(out string why), icon;
            if (ico != null && GameIcon.Apply(exe, ico, out string applied)) icon = "G3 " + applied;
            else icon = (ico == null ? why : "G3 icon failed") + "; " + (File.Exists(player) ? CopyIcon(player, exe) : "no player exe for the icon");
            report = $"{LauncherExe} launcher ({new FileInfo(exe).Length / 1024} KB, {LastLine(output)}; icon: {icon})";
            return true;
        }

        static string LastLine(string text)
        {
            string[] lines = text.Trim().Split('\n');
            return lines[lines.Length - 1].Trim();
        }

        static string Run(string file, string args, out int code)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = ProjectDir,
            };
            using var p = Process.Start(psi);
            var err = p.StandardError.ReadToEndAsync();
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(180000);
            code = p.HasExited ? p.ExitCode : -1;
            return o + err.Result;
        }

        // ---- icon: RT_GROUP_ICON + its RT_ICON images from the Unity player exe into the launcher ----
        const uint LoadAsData = 0x00000002 | 0x00000020; // LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE
        static readonly IntPtr RtIcon = (IntPtr)3, RtGroupIcon = (IntPtr)14;
        delegate bool EnumNameProc(IntPtr module, IntPtr type, IntPtr name, IntPtr param);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool EnumResourceNamesW(IntPtr module, IntPtr type, EnumNameProc proc, IntPtr param);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindResourceW(IntPtr module, IntPtr name, IntPtr type);
        [DllImport("kernel32.dll")] static extern IntPtr LoadResource(IntPtr module, IntPtr res);
        [DllImport("kernel32.dll")] static extern IntPtr LockResource(IntPtr data);
        [DllImport("kernel32.dll")] static extern uint SizeofResource(IntPtr module, IntPtr res);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr BeginUpdateResourceW(string path, bool deleteExisting);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool UpdateResourceW(IntPtr update, IntPtr type, IntPtr name, ushort lang, byte[] data, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool EndUpdateResourceW(IntPtr update, bool discard);

        static byte[] Resource(IntPtr module, IntPtr name, IntPtr type)
        {
            IntPtr res = FindResourceW(module, name, type);
            if (res == IntPtr.Zero) return null;
            IntPtr ptr = LockResource(LoadResource(module, res));
            if (ptr == IntPtr.Zero) return null;
            var data = new byte[SizeofResource(module, res)];
            Marshal.Copy(ptr, data, 0, data.Length);
            return data;
        }

        /// <summary>The first icon group of <paramref name="from"/> as icon group 1 of <paramref name="to"/>.</summary>
        public static string CopyIcon(string from, string to)
        {
            IntPtr module = LoadLibraryExW(from, IntPtr.Zero, LoadAsData);
            if (module == IntPtr.Zero) return "player not loadable (" + Marshal.GetLastWin32Error() + ")";
            try
            {
                IntPtr group = IntPtr.Zero;
                EnumResourceNamesW(module, RtGroupIcon, (m, t, n, p) => { group = n; return false; }, IntPtr.Zero);
                byte[] dir = group == IntPtr.Zero ? null : Resource(module, group, RtGroupIcon);
                if (dir == null || dir.Length < 6) return "no icon in the player";
                int count = BitConverter.ToUInt16(dir, 4);
                var images = new List<(ushort id, byte[] data)>();
                for (int i = 0; i < count && 6 + i * 14 + 14 <= dir.Length; i++)
                {
                    ushort id = BitConverter.ToUInt16(dir, 6 + i * 14 + 12);
                    byte[] img = Resource(module, (IntPtr)id, RtIcon);
                    if (img != null) images.Add((id, img));
                }
                IntPtr update = BeginUpdateResourceW(to, false);
                if (update == IntPtr.Zero) return "launcher not writable (" + Marshal.GetLastWin32Error() + ")";
                bool ok = true;
                foreach (var (id, data) in images) ok &= UpdateResourceW(update, RtIcon, (IntPtr)id, 0, data, (uint)data.Length);
                ok &= UpdateResourceW(update, RtGroupIcon, (IntPtr)1, 0, dir, (uint)dir.Length);
                if (!EndUpdateResourceW(update, !ok) || !ok) return "update failed (" + Marshal.GetLastWin32Error() + ")";
                return $"{images.Count} images from {Path.GetFileName(from)}";
            }
            finally { FreeLibrary(module); }
        }
    }
}
