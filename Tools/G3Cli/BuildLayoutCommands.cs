using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Myrtana.Core;
using Myrtana.Unity.Editor;

static partial class Program
{
    /// <summary>
    /// fix-167: <c>build relayout &lt;buildDir&gt; [--no-launcher] [--no-docs]</c> converts a demo build of the old flat
    /// layout (player next to the launcher) to the current one (Docs/build.md §3a2): every root entry except G3World.exe,
    /// the READMEs, BUILD_INFO.txt and Screenshots moves into <c>bin/</c> (moves only, nothing is deleted); the launcher is
    /// rebuilt with Tools/Launcher/build.cmd (it must look for <c>bin\G3WorldPlayer.exe</c>) with the old launcher's icon;
    /// the READMEs' start section is replaced with the one of Docs/demo; BUILD_INFO gets the new start and layout lines.
    /// Exit code 0 = the layout check passes.
    /// </summary>
    static int BuildCommand(string[] args)
    {
        if (args.Length < 3 || args[1] != "relayout")
        {
            Console.WriteLine("usage: G3Cli build relayout <buildDir> [--no-launcher] [--no-docs]");
            return 1;
        }
        string dir = Path.GetFullPath(args[2]);
        bool launcher = !args.Contains("--no-launcher"), docs = !args.Contains("--no-docs");
        if (!Directory.Exists(dir)) { Console.WriteLine("no folder " + dir); return 1; }
        string repo = FindRepoRoot();
        string launcherPath = Path.Combine(dir, BuildFolderLayout.LauncherExe);

        // the old launcher's icon (Gothic 3's, fix-165) before it is replaced
        List<GameIcon.Image> icon = File.Exists(launcherPath) ? GameIcon.ReadExe(launcherPath, out _) : null;

        Console.WriteLine(BuildFolderLayout.Relayout(dir, out _, out int conflicts));

        bool launcherOk = true;
        if (launcher)
        {
            launcherOk = RebuildLauncher(repo, dir, launcherPath, icon, out string report);
            Console.WriteLine("launcher: " + report);
        }
        if (docs)
        {
            foreach (var (name, header) in new[] { ("README_RU.md", "## Запуск"), ("README_EN.md", "## Start") })
                Console.WriteLine(name + ": " + ReplaceSection(Path.Combine(dir, name), repo == null ? null : Path.Combine(repo, "Docs", "demo", name), header));
            Console.WriteLine(BuildFolderLayout.BuildInfo + ": " + UpdateBuildInfo(Path.Combine(dir, BuildFolderLayout.BuildInfo)));
        }

        var problems = BuildFolderLayout.Check(dir);
        if (problems.Count == 0) Console.WriteLine("layout ok: root = launcher + docs, the game in bin/");
        else foreach (var p in problems) Console.WriteLine("LAYOUT: " + p);
        return problems.Count == 0 && conflicts == 0 && launcherOk ? 0 : 2;
    }

    /// <summary>The repository root (holds Tools/Launcher/build.cmd), from the working directory or this exe up.</summary>
    static string FindRepoRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "Tools", "Launcher", "build.cmd"))) return d.FullName;
        return null;
    }

    static bool RebuildLauncher(string repo, string dir, string launcherPath, List<GameIcon.Image> icon, out string report)
    {
        if (repo == null) { report = "NOT rebuilt: Tools/Launcher/build.cmd not found (run from the repository)"; return false; }
        string tmp = Path.Combine(Path.GetTempPath(), "g3relayout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            string exe = Path.Combine(tmp, BuildFolderLayout.LauncherExe);
            string script = Path.Combine(repo, "Tools", "Launcher", "build.cmd");
            var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", $"/d /c \"\"{script}\" \"{exe}\"\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = repo,
            };
            using (var p = Process.Start(psi))
            {
                var err = p.StandardError.ReadToEndAsync();
                string o = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0 || !File.Exists(exe))
                {
                    report = $"NOT rebuilt (build.cmd exit {p.ExitCode}): {(o + err.Result).Trim()}";
                    return false;
                }
            }
            // icon: the old launcher's, else the player's (both carry Gothic 3's icon since fix-165)
            string iconFrom = "old launcher";
            if (icon == null)
            {
                icon = GameIcon.ReadExe(BuildFolderLayout.PlayerPath(dir), out _);
                iconFrom = "player";
            }
            string iconReport = "none";
            if (icon != null && icon.Count > 0)
            {
                string ico = Path.Combine(tmp, "launcher.ico");
                File.WriteAllBytes(ico, GameIcon.WriteIco(icon));
                iconReport = GameIcon.Apply(exe, ico, out string applied) ? applied + " from the " + iconFrom : "failed: " + applied;
            }
            File.Copy(exe, launcherPath, true);
            report = $"{BuildFolderLayout.LauncherExe} rebuilt ({new FileInfo(launcherPath).Length / 1024} KB, starts {BuildFolderLayout.BinFolder}\\{BuildFolderLayout.PlayerExe}; icon {iconReport})";
            return true;
        }
        finally { try { Directory.Delete(tmp, true); } catch (IOException) { } }
    }

    /// <summary>The section from <paramref name="header"/> to the next "## " heading, taken from the template.</summary>
    static string ReplaceSection(string path, string template, string header)
    {
        if (!File.Exists(path)) return "missing";
        if (template == null || !File.Exists(template)) return "kept (no template " + template + ")";
        string Section(string text, out int start, out int end)
        {
            start = text.IndexOf("\n" + header, StringComparison.Ordinal);
            if (start < 0) { end = -1; return null; }
            start++;
            end = text.IndexOf("\n## ", start + header.Length, StringComparison.Ordinal);
            end = end < 0 ? text.Length : end + 1;
            return text.Substring(start, end - start);
        }
        string current = File.ReadAllText(path), source = File.ReadAllText(template);
        bool crlf = current.Contains("\r\n");
        current = current.Replace("\r\n", "\n");
        string fresh = Section(source.Replace("\r\n", "\n"), out _, out _);
        string old = Section(current, out int s, out int e);
        if (fresh == null || old == null) return "kept (no '" + header + "' section)";
        if (old == fresh) return "start section already current";
        string text = current.Substring(0, s) + fresh + current.Substring(e);
        if (crlf) text = text.Replace("\n", "\r\n");
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return "start section replaced from " + template;
    }

    /// <summary>BUILD_INFO.txt: the start line for the launcher in the root, plus the layout line (before the sizes).</summary>
    static string UpdateBuildInfo(string path)
    {
        if (!File.Exists(path)) return "missing";
        var lines = File.ReadAllLines(path).Where(l => !l.StartsWith("start:", StringComparison.Ordinal) && !l.StartsWith("layout:", StringComparison.Ordinal)).ToList();
        int at = lines.FindIndex(l => l.StartsWith("sizes:", StringComparison.Ordinal));
        if (at < 0) at = lines.Count;
        lines.Insert(at, $"start: {BuildFolderLayout.LauncherExe} (launcher: G3 splash, then {BuildFolderLayout.BinFolder}\\{BuildFolderLayout.PlayerExe} in {BuildFolderLayout.BinFolder}; arguments passed through)");
        lines.Insert(at + 1, BuildFolderLayout.Describe() + " (converted by G3Cli build relayout " + DateTime.Now.ToString("yyyy-MM-dd") + ")");
        File.WriteAllLines(path, lines);
        return "start + layout lines";
    }
}
