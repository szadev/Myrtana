using System.Threading.Tasks;
using Myrtana.Core;
using Myrtana.Session;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// Parses the content packs (<see cref="GameContent.Load"/>, plain C#, ~1.5 s) on a worker thread, started before
    /// the first scene loads, so it overlaps the master scene load and the loading screen instead of blocking the first
    /// frame (Docs/performance.md, "Master scene, first frame"). <see cref="GameBootstrap"/> takes the result.
    /// </summary>
    static class ContentPreload
    {
        static Task<GameContent> _pending;

        /// <summary>Seconds the last parse took on its worker thread (start-up timeline, fix-115).</summary>
        public static double ParseSeconds { get; private set; }

        static Task<GameContent> Run(System.Collections.Generic.IReadOnlyList<IContentSource> packs) => Task.Run(() =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var content = GameContent.Load(packs);
            ParseSeconds = watch.Elapsed.TotalSeconds;
            return content;
        });

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Start()
        {
            _pending = null;   // domain reload is off in the editor: a previous Play mode's load is not reused
            var packs = ContentLocations.Packs();
            if (packs.Count > 0) _pending = Run(packs);
        }

        /// <summary>The load started before the scene (or a new one); null when there is no content pack.</summary>
        public static Task<GameContent> Take()
        {
            var task = _pending;
            _pending = null;
            if (task != null) return task;
            var packs = ContentLocations.Packs();
            return packs.Count > 0 ? Run(packs) : null;
        }
    }
}
