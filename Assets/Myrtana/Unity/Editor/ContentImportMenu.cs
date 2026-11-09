using System.IO;
using Myrtana.Import.G3;
using UnityEditor;
using UnityEngine;

namespace Myrtana.Unity.Editor
{
    /// <summary>
    /// Editor entry points of the G3 import (same code as <c>G3Cli content import</c> / <c>content voice</c>): builds the
    /// "Gothic3" pack in StreamingAssets/Content and extracts voice clips to GameData/Voice (outside Assets: never imported
    /// by the editor, so no refresh). Both outputs are generated from the game files and git-ignored.
    /// </summary>
    public static class ContentImportMenu
    {
        const string DataPathKey = "Myrtana.G3DataPath";
        static string DefaultDataPath => G3.Formats.G3Install.DataDirOrHint;
        /// <summary>Faring (centre x/z and half size in metres, G3 axes) — the region of the first playable slice.</summary>
        static readonly float[] FaringRegion = { 655f, 586f, 150f };

        static string DataPath => (G3.Formats.G3Install.Pref(EditorPrefs.GetString(DataPathKey, "")) ?? DefaultDataPath);
        static string PackPath => Path.Combine(ContentLocations.ContentRoot, ContentLocations.BasePack);

        [MenuItem("Myrtana/Content/Import Gothic 3 pack")]
        public static string ImportPack()
        {
            var report = G3ContentImporter.Run(DataPath, PackPath);
            AssetDatabase.Refresh();
            string summary = report.ToString().Split('\n')[0];
            Debug.Log($"[Myrtana] Gothic 3 pack imported to {PackPath}: {summary} (details: {G3ContentImporter.ReportFile})");
            return summary;
        }

        [MenuItem("Myrtana/Content/Extract voice (Faring, Russian + English)")]
        public static string ExtractFaringVoice()
        {
            var owners = VoiceExtractor.OwnersInRegion(DataPath, FaringRegion[0], FaringRegion[1], FaringRegion[2]);
            string summary = VoiceExtractor.Extract(DataPath, PackPath, ContentLocations.VoiceRoot, owners, G3ContentImporter.VoiceLanguages);
            Debug.Log("[Myrtana] voice: " + summary);
            return summary;
        }

        /// <summary>
        /// The voice of every speaker (all dialog owners, every NPC voice set, the hero), Russian and English; clips already
        /// extracted are kept. Same as G3Cli "content voice --all". Never commit the result (game-derived).
        /// </summary>
        [MenuItem("Myrtana/Content/Extract voice (all speakers, Russian + English)")]
        public static string ExtractAllVoice()
        {
            var owners = VoiceExtractor.AllOwners(PackPath);
            string summary = VoiceExtractor.Extract(DataPath, PackPath, ContentLocations.VoiceRoot, owners, G3ContentImporter.VoiceLanguages);
            Debug.Log("[Myrtana] voice: " + summary);
            return summary;
        }
    }
}
