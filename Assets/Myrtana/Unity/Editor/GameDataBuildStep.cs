using System;
using System.Diagnostics;
using System.IO;
using Myrtana.Core;
using Myrtana.Import.G3;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using Debug = UnityEngine.Debug;

namespace Myrtana.Unity.Editor
{
    /// <summary>
    /// After a Windows/Linux player build, puts the project's <c>GameData/</c> (voice, outside Assets — see
    /// <see cref="GameDataLayout"/>) next to the exe, where <see cref="ContentLocations"/> looks for it. By default the
    /// changed files are copied (a re-build copies only what changed); with "Link GameData into builds" on (menu
    /// Myrtana/Content, or env MYRTANA_GAMEDATA_LINK=1) a directory junction to the project folder is made instead —
    /// instant for dev builds, but such a build only runs on this machine.
    /// </summary>
    public sealed class GameDataBuildStep : IPostprocessBuildWithReport
    {
        const string LinkKey = "Myrtana.LinkGameDataIntoBuilds";
        const string LinkMenu = "Myrtana/Content/Link GameData into builds (no copy)";

        public int callbackOrder => 0;

        /// <summary>Set by builds that leave this machine (the demo build, <see cref="DemoBuild"/>): always copy, never link.</summary>
        internal static bool ForceCopy;

        static bool Link => !ForceCopy && (EditorPrefs.GetBool(LinkKey, false) || Environment.GetEnvironmentVariable("MYRTANA_GAMEDATA_LINK") == "1");

        [MenuItem(LinkMenu)]
        static void ToggleLink() => EditorPrefs.SetBool(LinkKey, !EditorPrefs.GetBool(LinkKey, false));

        [MenuItem(LinkMenu, true)]
        static bool ToggleLinkValidate()
        {
            Menu.SetChecked(LinkMenu, EditorPrefs.GetBool(LinkKey, false));
            return true;
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            var target = report.summary.platform;
            if (target != BuildTarget.StandaloneWindows64 && target != BuildTarget.StandaloneWindows && target != BuildTarget.StandaloneLinux64)
            {
                Debug.LogWarning($"[Myrtana] GameData not added to the {target} build (only Windows/Linux players are supported)");
                return;
            }
            string source = GameDataLayout.Root(ContentLocations.GameDataBase);
            if (!Directory.Exists(source))
            {
                Debug.LogWarning($"[Myrtana] no {source}: the build has no voice (extract it with G3Cli content voice --all)");
                return;
            }
            string exeDir = Path.GetDirectoryName(Path.GetFullPath(report.summary.outputPath));
            string dest = Path.Combine(exeDir, GameDataLayout.FolderName);
            if (Link) { Debug.Log("[Myrtana] " + Put(source, dest, true)); return; }
            // A copy holds only what the player reads: GameData/Voice. GameData/Remaster (glb/texture sources, work files)
            // is import-time input for the editor and never shipped.
            if (Directory.Exists(dest) && new DirectoryInfo(dest).Attributes.HasFlag(FileAttributes.ReparsePoint))
                Directory.Delete(dest);                           // an old junction: remove the link only, not the data
            string voice = Path.Combine(source, GameDataLayout.VoiceFolder);
            if (!Directory.Exists(voice)) { Debug.LogWarning($"[Myrtana] no {voice}: the build has no voice"); return; }
            Debug.Log("[Myrtana] " + Put(voice, Path.Combine(dest, GameDataLayout.VoiceFolder), false));
        }

        /// <summary>Copies (changed files only) or junctions <paramref name="source"/> to <paramref name="target"/>.</summary>
        public static string Put(string source, string target, bool link)
        {
            bool isLink = Directory.Exists(target) && new DirectoryInfo(target).Attributes.HasFlag(FileAttributes.ReparsePoint);
            if (link)
            {
                if (isLink) Directory.Delete(target);               // removes the junction only, not the data
                else if (Directory.Exists(target)) Directory.Delete(target, true);
                if (MakeJunction(target, source)) return $"GameData linked: {target} -> {source}";
                Debug.LogWarning("[Myrtana] junction failed, copying GameData instead");
            }
            else if (isLink) Directory.Delete(target);
            var (copied, unchanged) = GameDataFiles.Mirror(source, target);
            return $"GameData copied to {target}: {copied} files copied, {unchanged} unchanged";
        }

        static bool MakeJunction(string link, string target)
        {
            var psi = UnityEngine.Application.platform == UnityEngine.RuntimePlatform.WindowsEditor
                ? new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                : new ProcessStartInfo("ln", $"-s \"{target}\" \"{link}\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using var p = Process.Start(psi);
            p.WaitForExit();
            return p.ExitCode == 0 && Directory.Exists(link);
        }
    }
}
