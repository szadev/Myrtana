using System;
using System.IO;

namespace G3.Formats.Remaster
{
    /// <summary>
    /// Where remaster files live (Docs/remaster-pipeline.md): under the git-ignored GameData folder next to Assets
    /// (same root and override variable as the voice data, <c>Myrtana.Core.GameDataLayout</c>), never inside Assets.
    /// <list type="bullet">
    /// <item><c>GameData/Remaster/Source/&lt;actor&gt;.glb</c>: originals exported from the importer (Blender input, check reference)</item>
    /// <item><c>GameData/Remaster/Characters/&lt;actor&gt;.glb</c>: remastered replacements the importer uses when the option is on</item>
    /// <item><c>GameData/Remaster/Characters/LOD/&lt;actor&gt;_LOD1|2.glb</c>: optional lower LODs of a replacement (R2)</item>
    /// <item><c>GameData/Remaster/Maps/&lt;image&gt;.png</c>: 4x map pictures for the GUI (map-hd)</item>
    /// <item><c>GameData/Remaster/Reports/</c>: validation reports and before/after screenshots</item>
    /// </list>
    /// </summary>
    public static class RemasterPaths
    {
        public const string GameDataFolder = "GameData";
        public const string GameDataVariable = "MYRTANA_GAMEDATA";
        public const string Extension = ".glb";
        /// <summary>
        /// Suffix of a part exported from (and overriding it in) the talking-head rig, whose skeleton also carries the
        /// facial actor's face nodes: <c>G3_Hero_Body_Player__talk.glb</c> next to the static rig's <c>G3_Hero_Body_Player.glb</c>.
        /// </summary>
        public const string TalkSuffix = "__talk";

        public static string GameData(string projectDirectory)
        {
            string over = System.Environment.GetEnvironmentVariable(GameDataVariable);
            return string.IsNullOrWhiteSpace(over) ? Path.Combine(projectDirectory, GameDataFolder) : over;
        }

        public static string Root(string projectDirectory) => Path.Combine(GameData(projectDirectory), "Remaster");
        public static string SourceDir(string projectDirectory) => Path.Combine(Root(projectDirectory), "Source");
        public static string OverrideDir(string projectDirectory) => Path.Combine(Root(projectDirectory), "Characters");
        public static string ReportDir(string projectDirectory) => Path.Combine(Root(projectDirectory), "Reports");
        /// <summary>map-hd: 4x map pictures (<c>Tools/Remaster/maphd.py</c>), copied into the GUI import as HD sprites.</summary>
        public static string MapsDir(string projectDirectory) => Path.Combine(Root(projectDirectory), "Maps");

        /// <summary>Actor or mesh stem without extension (<c>G3_Hero_Body_Player.xact</c> → <c>G3_Hero_Body_Player</c>).</summary>
        public static string Stem(string name) => Path.GetFileNameWithoutExtension(name ?? "");

        public static string SourceFile(string projectDirectory, string actor) => Path.Combine(SourceDir(projectDirectory), Stem(actor) + Extension);
        public static string OverrideFile(string projectDirectory, string actor) => Path.Combine(OverrideDir(projectDirectory), Stem(actor) + Extension);

        /// <summary>The stem of a part in the talking-head rig (<see cref="TalkSuffix"/>).</summary>
        public static string TalkStem(string actor) => Stem(actor) + TalkSuffix;
        /// <summary>LOD levels the R2 pipeline writes besides the override itself (= LOD0).</summary>
        public const int LodLevels = 2;
        /// <summary><c>Characters/LOD/</c>: kept out of <see cref="OverrideDir"/> so the override list stays one file per part.</summary>
        public static string LodDir(string projectDirectory) => Path.Combine(OverrideDir(projectDirectory), "LOD");
        /// <summary><c>Characters/LOD/&lt;actor&gt;_LOD&lt;level&gt;.glb</c>, level 1..<see cref="LodLevels"/>.</summary>
        public static string LodFile(string projectDirectory, string actor, int level) =>
            Path.Combine(LodDir(projectDirectory), Stem(actor) + RemasterLods.Suffix + level + Extension);
    }
}
