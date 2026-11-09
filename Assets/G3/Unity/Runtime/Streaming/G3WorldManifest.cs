using System;
using System.Collections.Generic;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// The streamable world grid written by the world importer (G3.Unity.Editor.G3WorldBatch):
    /// one additive scene per square cell, entities assigned to the cell that contains their AABB centre.
    /// </summary>
    [CreateAssetMenu(menuName = "G3/World Manifest")]
    public sealed class G3WorldManifest : ScriptableObject
    {
        public enum CellKind { Empty, Land, Ocean }

        [Serializable]
        public sealed class Cell
        {
            public int ix, iz;
            public CellKind kind;
            [Tooltip("Myrtana / Varant / Nordmar — majority of the landscape meshes, else the nearest land cell.")]
            public string region;
            [Tooltip("Assets/Scenes/World/Cells/Cell_ix_iz.unity (empty if the cell has no scene).")]
            public string scenePath;
            [Tooltip("Union of the AABBs of the cell's content (metres). Big terrain meshes can reach out of the cell.")]
            public Bounds contentBounds;
            public int entities, meshes, trees, lights, plants;
            [Tooltip("Cells whose terrain overlaps this cell's square. The low-poly backdrop of this square is hidden only when all of them (and this cell) are loaded.")]
            public int[] coverers = Array.Empty<int>();
            public bool imported;

            public bool HasScene => !string.IsNullOrEmpty(scenePath);
        }

        public Vector2 origin = new Vector2(-4250, -4250);
        public float cellSize = 250;
        public int countX = 28, countZ = 26;
        public List<Cell> cells = new List<Cell>();

        [Tooltip("Master scene (environment, player/camera, streamer, low-poly backdrop).")]
        public string masterScenePath = "Assets/Scenes/World/G3World_Master.unity";

        public int Index(int ix, int iz) => ix >= 0 && iz >= 0 && ix < countX && iz < countZ ? iz * countX + ix : -1;

        public Cell Get(int ix, int iz)
        {
            int i = Index(ix, iz);
            return i >= 0 && i < cells.Count ? cells[i] : null;
        }

        public Vector2Int CellOf(Vector3 worldPos) =>
            new Vector2Int(Mathf.FloorToInt((worldPos.x - origin.x) / cellSize), Mathf.FloorToInt((worldPos.z - origin.y) / cellSize));

        public Rect CellRect(int ix, int iz) => new Rect(origin.x + ix * cellSize, origin.y + iz * cellSize, cellSize, cellSize);

        public static string SceneName(int ix, int iz) => $"Cell_{ix}_{iz}";

        /// <summary>Region of the cell under a world position (null outside the grid).</summary>
        public string RegionAt(Vector3 worldPos)
        {
            var c = CellOf(worldPos);
            return Get(c.x, c.y)?.region;
        }

        /// <summary>Horizontal distance from a point to the cell's content (or its square if it has no content bounds).</summary>
        public float Distance(Cell cell, Vector3 p)
        {
            Rect r;
            if (cell.contentBounds.size.sqrMagnitude > 0)
            {
                var b = cell.contentBounds;
                r = Rect.MinMaxRect(Mathf.Min(b.min.x, origin.x + cell.ix * cellSize), Mathf.Min(b.min.z, origin.y + cell.iz * cellSize),
                                    Mathf.Max(b.max.x, origin.x + (cell.ix + 1) * cellSize), Mathf.Max(b.max.z, origin.y + (cell.iz + 1) * cellSize));
            }
            else r = CellRect(cell.ix, cell.iz);
            float dx = Mathf.Max(0, Mathf.Max(r.xMin - p.x, p.x - r.xMax));
            float dz = Mathf.Max(0, Mathf.Max(r.yMin - p.z, p.z - r.yMax));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
