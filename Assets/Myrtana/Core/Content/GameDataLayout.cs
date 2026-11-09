using System;
using System.IO;

namespace Myrtana.Core
{
    /// <summary>
    /// Where the game-derived runtime data (voice clips with their lip-sync tracks) lives: <c>&lt;base&gt;/GameData/</c>,
    /// outside <c>Assets/</c> so the Unity editor never imports (or writes .meta files for) its ~50k files. The base is the
    /// project folder in the editor and in G3Cli, and the folder of the exe in a player build (the build step copies or
    /// links it there). <see cref="OverrideVariable"/> points all of them at another folder.
    /// </summary>
    public static class GameDataLayout
    {
        public const string FolderName = "GameData";
        public const string VoiceFolder = "Voice";
        /// <summary>Environment variable: an absolute GameData folder that wins over the default location.</summary>
        public const string OverrideVariable = "MYRTANA_GAMEDATA";

        /// <summary>The GameData folder for a project folder or an exe folder.</summary>
        public static string Root(string baseDirectory)
        {
            string over = Environment.GetEnvironmentVariable(OverrideVariable);
            return string.IsNullOrWhiteSpace(over) ? Path.Combine(baseDirectory, FolderName) : over;
        }

        /// <summary><c>&lt;GameData&gt;/Voice/&lt;Language&gt;/&lt;clip key&gt;.ogg</c> (+ <c>.lip</c>).</summary>
        public static string VoiceRoot(string baseDirectory) => Path.Combine(Root(baseDirectory), VoiceFolder);

        /// <summary>The old location inside the Unity project (before the move); only for the migration and a fallback.</summary>
        public static string LegacyVoiceRoot(string projectDirectory) =>
            Path.Combine(projectDirectory, "Assets", "StreamingAssets", VoiceFolder);
    }
}
