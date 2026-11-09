using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace G3.Unity.Characters
{
    /// <summary>
    /// Motion libraries per rig, loaded on demand from Resources/<see cref="ResourceFolder"/>/&lt;Rig&gt;
    /// (built by G3NpcMotionImporter).
    /// </summary>
    public static class G3MotionLibraries
    {
        public const string ResourceFolder = "G3Motions";
        static readonly Dictionary<string, G3MotionLibrary> Cache = new Dictionary<string, G3MotionLibrary>();

        /// <summary>The library of <paramref name="rig"/>, or null when it was not imported.</summary>
        public static G3MotionLibrary Load(string rig)
        {
            if (string.IsNullOrEmpty(rig)) return null;
            if (!Cache.TryGetValue(rig, out var lib)) Cache[rig] = lib = Resources.Load<G3MotionLibrary>($"{ResourceFolder}/{rig}");
            return lib;
        }

        static readonly Dictionary<string, ResourceRequest> Pending = new Dictionary<string, ResourceRequest>();

        /// <summary>
        /// perf-5: true when <see cref="Load"/> of <paramref name="rig"/> returns at once; otherwise starts loading it
        /// asynchronously (a rig's library is hundreds of clips: a synchronous first load was a 0.1-1 s frame when the first
        /// NPC of a new rig came into view).
        /// </summary>
        public static bool Prefetch(string rig)
        {
            if (string.IsNullOrEmpty(rig) || Cache.ContainsKey(rig)) return true;
            if (!Pending.TryGetValue(rig, out var req)) Pending[rig] = req = Resources.LoadAsync<G3MotionLibrary>($"{ResourceFolder}/{rig}");
            if (req != null && !req.isDone) return false;
            Pending.Remove(rig);
            Cache[rig] = req != null ? req.asset as G3MotionLibrary : null;
            return true;
        }
    }

    /// <summary>
    /// NPC prefabs by visual key (actor + parts + material switches, Docs/formats-animation.md §8.5), loaded on demand from
    /// Resources/<see cref="ResourceFolder"/> (built by G3NpcImporter).
    /// </summary>
    public static class G3NpcPrefabs
    {
        public const string ResourceFolder = "G3Npc";

        /// <summary>File name (without extension) of the prefab of <paramref name="visualKey"/>.</summary>
        public static string FileName(string visualKey)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) visualKey = visualKey.Replace(c, '_');
            return visualKey;
        }

        public static GameObject Load(string visualKey) =>
            string.IsNullOrEmpty(visualKey) ? null : Resources.Load<GameObject>($"{ResourceFolder}/{FileName(visualKey)}");

        public static ResourceRequest LoadAsync(string visualKey) =>
            Resources.LoadAsync<GameObject>($"{ResourceFolder}/{FileName(visualKey)}");
    }
}
