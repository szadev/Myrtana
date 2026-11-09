using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Myrtana.Core;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// Where content packs and voice clips live: <c>StreamingAssets/Content/&lt;Pack&gt;/</c> (the generated "Gothic3" pack
    /// first, then any other pack in name order) and <c>GameData/Voice/&lt;Language&gt;/&lt;clip key&gt;.ogg</c> outside
    /// Assets (<see cref="GameDataLayout"/>): the project folder in the editor, the exe folder in a player.
    /// </summary>
    public static class ContentLocations
    {
        public const string BasePack = "Gothic3";

        public static string ContentRoot => Path.Combine(Application.streamingAssetsPath, "Content");
        /// <summary>
        /// The project folder in the editor (parent of Assets), the exe folder in a Windows/Linux player (parent of
        /// &lt;Name&gt;_Data) — both are the parent of <see cref="Application.dataPath"/>.
        /// </summary>
        public static string GameDataBase => Path.GetDirectoryName(Application.dataPath);

        /// <summary>
        /// <c>GameData/Voice</c>; until the one-shot migration has run, an old <c>StreamingAssets/Voice</c> is still used.
        /// </summary>
        public static string VoiceRoot => _voiceRoot ??= ResolveVoiceRoot();

        static string _voiceRoot;

        static string ResolveVoiceRoot()
        {
            string root = GameDataLayout.VoiceRoot(GameDataBase);
            string legacy = Path.Combine(Application.streamingAssetsPath, GameDataLayout.VoiceFolder);
            if (!Directory.Exists(root) && Directory.Exists(legacy))
            {
                Debug.LogWarning($"[Myrtana] voice still in {legacy}; run 'G3Cli content voice --migrate' (editor closed) to move it to {root}");
                return legacy;
            }
            return root;
        }

        public static IReadOnlyList<IContentSource> Packs()
        {
            if (!Directory.Exists(ContentRoot)) return Array.Empty<IContentSource>();
            return Directory.GetDirectories(ContentRoot)
                .Where(d => File.Exists(Path.Combine(d, ContentPackManifest.FileName)))
                .OrderBy(d => Path.GetFileName(d) == BasePack ? 0 : 1)
                .ThenBy(d => Path.GetFileName(d), StringComparer.Ordinal)
                .Select(d => (IContentSource)new DirectoryContentSource(d))
                .ToList();
        }
    }
}
