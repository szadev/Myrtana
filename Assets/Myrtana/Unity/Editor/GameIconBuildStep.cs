using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Myrtana.Unity.Editor
{
    /// <summary>
    /// fix-165: every Windows player build gets Gothic 3's icon (<see cref="GameIcon"/>, Docs/build.md "Icon"). After
    /// Unity has written the player exe (with Unity's default cube icon: PlayerSettings has no icon, so nothing there
    /// points at a game-derived file), the exe's icon resources are replaced by the install's Gothic 3 "G" icon
    /// (<see cref="SourceExe"/>), extracted to <see cref="IcoPath"/> (Library: git-ignored, regenerated from the install). The running player's window and
    /// taskbar icon is the exe's (UnityPlayer loads the same group). No install → Unity's icon stays (preflight WARN).
    /// </summary>
    public sealed class GameIconBuildStep : IPostprocessBuildWithReport
    {
        public int callbackOrder => 100;

        static string ProjectDir => Path.GetDirectoryName(Application.dataPath);
        /// <summary>The install's exe with the Gothic 3 icon (first of <see cref="GameIcon.SourceNames"/> that exists).</summary>
        public static string SourceExe
        {
            get
            {
                foreach (string name in GameIcon.SourceNames)
                {
                    string path = Path.Combine(StartupSplash.GameRoot, name);
                    if (File.Exists(path)) return path;
                }
                return Path.Combine(StartupSplash.GameRoot, GameIcon.SourceNames[GameIcon.SourceNames.Length - 1]);
            }
        }

        /// <summary>The icon extracted from <paramref name="sourceExe"/> (game-derived, git-ignored Library).</summary>
        public static string IcoPath(string sourceExe) =>
            Path.Combine(ProjectDir, "Library", "Myrtana", Path.GetFileNameWithoutExtension(sourceExe) + ".ico");

        /// <summary>The extracted Gothic 3 icon, or null (<paramref name="report"/>: what it is or why there is none).</summary>
        public static string Ensure(out string report)
        {
            string exe = SourceExe;
            return GameIcon.Extract(exe, IcoPath(exe), out report);
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            var target = report.summary.platform;
            if (target != BuildTarget.StandaloneWindows64 && target != BuildTarget.StandaloneWindows) return;
            string exe = report.summary.outputPath;
            if (!File.Exists(exe)) { Debug.LogWarning("[Myrtana] game icon: no player exe at " + exe); return; }
            Debug.Log("[Myrtana] " + ApplyTo(exe));
        }

        /// <summary>Gives <paramref name="exe"/> the Gothic 3 icon; a one-line result.</summary>
        public static string ApplyTo(string exe)
        {
            string ico = Ensure(out string why);
            if (ico == null) return $"game icon: {why}; {Path.GetFileName(exe)} keeps its icon";
            return GameIcon.Apply(exe, ico, out string result)
                ? $"game icon of {Path.GetFileName(exe)}: G3 {result}"
                : $"game icon NOT set on {Path.GetFileName(exe)}: {result}";
        }
    }
}
