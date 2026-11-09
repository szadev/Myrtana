using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// The hero's maps (Docs/map-teleport.md §1.1): which map items he carries, and which one the map page shows — the one
    /// picked in the list; else the one shown last while he still carries it; else, of the carried maps that contain
    /// his position, the largest; else none (the page shows an empty view).
    /// </summary>
    public sealed class WorldMaps
    {
        readonly Dictionary<string, MapDef> _maps;
        readonly Func<Inventory> _hero;

        /// <param name="hero">The hero's inventory (looked up each time; a loaded game has new inventories).</param>
        public WorldMaps(IEnumerable<MapDef> maps, Func<Inventory> hero)
        {
            _maps = (maps ?? Enumerable.Empty<MapDef>()).ToDictionary(m => m.Id, StringComparer.Ordinal);
            _hero = hero ?? throw new ArgumentNullException(nameof(hero));
        }

        /// <summary>Id of the map shown last (null = none); kept by the save.</summary>
        public string Shown { get; private set; }

        public bool TryGet(string item, out MapDef map) => _maps.TryGetValue(item ?? string.Empty, out map);

        public bool IsMap(string item) => item != null && _maps.ContainsKey(item);

        /// <summary>Every map of the content.</summary>
        public IEnumerable<MapDef> All => _maps.Values;

        /// <summary>The carried maps in inventory order.</summary>
        public IReadOnlyList<MapDef> Owned()
        {
            var inventory = _hero();
            if (inventory == null) return Array.Empty<MapDef>();
            return inventory.Stacks.Where(s => s.Amount > 0 && _maps.ContainsKey(s.Item))
                            .Select(s => _maps[s.Item]).Distinct().ToList();
        }

        /// <summary>
        /// Picks the map to show for a hero at (x, z) and remembers it. <paramref name="picked"/> is the map selected in
        /// the list (null = none). Returns null when no carried map fits.
        /// </summary>
        public MapDef Choose(string picked, float x, float z)
        {
            var owned = Owned();
            var map = owned.FirstOrDefault(m => m.Id == picked)
                      ?? owned.FirstOrDefault(m => m.Id == Shown)
                      ?? owned.Where(m => m.Projection.IsValid && m.Projection.Contains(x, z))
                              .OrderByDescending(m => m.Projection.DiagonalSquared).FirstOrDefault();
            Shown = map?.Id;
            return map;
        }

        /// <summary>
        /// fix-107: where (x, z) is, for the save list ("… · Faring"): the label of the nearest visible marker that has a text
        /// (<paramref name="named"/>) on the largest map containing the point — carried or not, so it is the world map's
        /// towns and camps. Empty when no map contains the point.
        /// </summary>
        public TextId PlaceNear(float x, float z, Func<TextId, bool> named)
        {
            var map = _maps.Values.Where(m => m.Projection.IsValid && m.Projection.Contains(x, z))
                           .OrderByDescending(m => m.Projection.DiagonalSquared).ThenBy(m => m.Id, StringComparer.Ordinal).FirstOrDefault();
            if (map == null) return default;
            TextId best = default;
            float bestDistance = float.MaxValue;
            foreach (var marker in map.VisibleMarkers)
            {
                if (marker.Label.IsEmpty || (named != null && !named(marker.Label))) continue;
                float dx = marker.X - x, dz = marker.Z - z, d = dx * dx + dz * dz;
                if (d < bestDistance) { bestDistance = d; best = marker.Label; }
            }
            return best;
        }

        /// <summary>Restores the shown map from a save.</summary>
        public void Restore(string shown) => Shown = shown;
    }
}
