using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// fix-222: the Resources the game build loads, requested asynchronously at start-up (behind the logos): the item
    /// visual catalogue references ~740 prefabs, meshes and materials, which a synchronous <c>Resources.Load</c> in the
    /// build pulled in on the main thread. The build's own <c>Load()</c> calls then find them loaded (one still in flight
    /// is finished by the synchronous call, never loaded twice).
    /// </summary>
    public static class StartupAssets
    {
        static ResourceRequest _items, _objects;

        public static void Begin()
        {
            if (_items != null) return;
            _items = Resources.LoadAsync<ItemVisualCatalog>(ItemVisualCatalog.ResourcePath);
            _objects = Resources.LoadAsync<InteractiveVisualCatalog>(InteractiveVisualCatalog.ResourcePath);
            double started = Time.realtimeSinceStartupAsDouble;
            _items.completed += _ => Debug.Log($"[Myrtana] start-up {Time.realtimeSinceStartupAsDouble:0.00} s: item visuals loaded in the background ({Time.realtimeSinceStartupAsDouble - started:0.00} s)");
        }

        /// <summary>Both catalogues are loaded.</summary>
        public static bool Done => _items == null || (_items.isDone && _objects.isDone);
    }
}
