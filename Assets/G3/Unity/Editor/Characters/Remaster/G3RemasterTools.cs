using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using G3.Formats.Remaster;
using UnityEditor;
using UnityEngine;

namespace G3.Unity.Editor.Characters
{
    /// <summary>
    /// Remaster checks and rebuilds from the editor (Docs/remaster-pipeline.md). The same check runs without Unity:
    /// <c>G3Cli remaster check</c> and <c>Tools/Blender/remaster/validate.py</c>.
    /// </summary>
    public static class G3RemasterTools
    {
        /// <summary>
        /// Contract check of every override (and its R2 LOD files) against its exported original (Source/&lt;stem&gt;.glb);
        /// reports go to GameData/Remaster/Reports/&lt;stem&gt;[_LOD&lt;n&gt;].txt. Returns one PASS/FAIL line per file.
        /// </summary>
        public static string CheckAll()
        {
            var lines = new List<string>();
            string project = G3RemasterOverride.ProjectDir;
            foreach (var stem in G3RemasterOverride.Overrides())
            {
                lines.Add(Check(stem));
                for (int level = 1; level <= RemasterPaths.LodLevels; level++)
                    if (File.Exists(RemasterPaths.LodFile(project, stem, level))) lines.Add(Check(stem, level));
            }
            return lines.Count > 0 ? string.Join("\n", lines) : "no overrides in " + RemasterPaths.OverrideDir(G3RemasterOverride.ProjectDir);
        }

        /// <summary>Check of one override (level 0) or of one of its R2 LOD files (level 1..2) against the original.</summary>
        public static string Check(string stem, int level = 0)
        {
            string project = G3RemasterOverride.ProjectDir;
            string src = RemasterPaths.SourceFile(project, stem);
            string dst = level == 0 ? RemasterPaths.OverrideFile(project, stem) : RemasterPaths.LodFile(project, stem, level);
            if (level > 0) stem += RemasterLods.Suffix + level;
            if (!File.Exists(src)) return $"{stem}: no exported original {src} (run G3RemasterExporter first)";
            if (!File.Exists(dst)) return $"{stem}: no override {dst}";
            var report = RigCheck.Compare(Glb.Read(src), Glb.Read(dst));
            string dir = RemasterPaths.ReportDir(project);
            Directory.CreateDirectory(dir);
            string text = report.ToText();
            File.WriteAllText(Path.Combine(dir, stem + ".txt"), text);
            var sb = new StringBuilder($"{stem}: {(report.Passed ? "PASS" : "FAIL")} ({report.Failures} failed, {report.Warnings} warnings)");
            foreach (var l in report.Lines.Where(l => l.Level == RigCheck.Level.Fail || l.Level == RigCheck.Level.Warn)) sb.Append("\n  ").Append(l);
            return sb.ToString();
        }

        /// <summary>
        /// Rebuilds what bakes character meshes in after the override switch or a file changed: the hero prefab, the
        /// talking-head kits and every NPC prefab whose skinned meshes differ (<see cref="G3NpcImporter.RebuildStalePrefabs"/>).
        /// Slow (all NPC visuals are re-checked); run by the editor-runner.
        /// </summary>
        public static string RebuildCharacters()
        {
            var sb = new StringBuilder($"remaster overrides {(G3RemasterSettings.Enabled ? "ON" : "OFF")}: {string.Join(", ", G3RemasterOverride.Overrides())}\n");
            sb.AppendLine(G3HeroImporter.Build(false));
            sb.AppendLine(G3FaceKitImporter.Build());
            sb.Append(G3NpcImporter.RebuildStalePrefabs());
            return sb.ToString();
        }

        [MenuItem("G3/Remaster/Check Overrides", priority = 220)]
        static void MenuCheck() => Debug.Log(CheckAll());

        [MenuItem("G3/Remaster/Rebuild Characters (after toggling)", priority = 221)]
        static void MenuRebuild() => Debug.Log(RebuildCharacters());
    }
}
