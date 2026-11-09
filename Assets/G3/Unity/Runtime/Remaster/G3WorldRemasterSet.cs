using System;
using System.Collections.Generic;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// Original world material → remaster copy (Docs/remaster-world.md §4), written by the editor's
    /// <c>G3WorldRemasterBuilder.Build()</c> to <c>Assets/G3Imported/Remaster/World/Resources/G3WorldRemasterSet.asset</c>
    /// and read at runtime by <see cref="G3WorldRemaster"/>.
    /// </summary>
    public sealed class G3WorldRemasterSet : ScriptableObject
    {
        public const string ResourceName = "G3WorldRemasterSet";

        [Serializable]
        public struct Pair
        {
            public Material original;
            public Material remaster;
            public int part;   // WorldParts flag (fix-71; 0 = built before: classified at runtime, WorldRemasterParts)
        }

        public List<Pair> pairs = new List<Pair>();

        /// <summary>v2 copies (G3/Remaster/WorldV2, section 10), original → v2 material; written by G3WorldV2Builder.</summary>
        [Serializable]
        public struct V2Pair
        {
            public Material original;
            public Material v2;
            public int kind;   // 1 terrain (v2Terrain), 2 buildings and rocks (v2Surfaces)
            public int part;   // WorldParts flag (fix-71; 0 = built before: classified at runtime, WorldRemasterParts)
        }

        public List<V2Pair> v2pairs = new List<V2Pair>();

        /// <summary>v2 replacement trees (G3TreeV2, section 10.15): CC0/procedural models, written by G3TreeV2Builder.</summary>
        public G3TreeV2Set treeV2;

        /// <summary>
        /// sand-2 (section 22.8): the CC0 sand micro-detail tile (Tools/Remaster/sand_scans.py, RGBA as SandDetail: normal xy,
        /// height, albedo factor), its real size and parallax height (m); written by G3WorldV2Builder. Null = procedural grain only.
        /// </summary>
        public Texture2D sandMicro;
        public float sandMicroSize = 2f, sandMicroHeight = 0.006f;

        /// <summary>
        /// sand-3 (section 22.10): the scan-based sand base set (Tools/Remaster/sand_scans.py --set): per slot (fine, mid, coarse)
        /// an albedo image (sRGB RGB, A height) and a normal image (linear RG normal, B roughness, A occlusion), the scans' real
        /// sizes (m) and heights (m) as x y z; written by G3WorldV2Builder. Null = sand-2 look (procedural grain only).
        /// </summary>
        public Texture2D sandFineA, sandFineN, sandMidA, sandMidN, sandCoarseA, sandCoarseN;
        public Vector3 sandScanSize = new Vector3(2.14f, 1.8f, 2.48f), sandScanHeight = new Vector3(0.004f, 0.008f, 0.012f);

        /// <summary>The whole scan set is there.</summary>
        public bool HasSandScans => sandFineA != null && sandFineN != null && sandMidA != null && sandMidN != null && sandCoarseA != null && sandCoarseN != null;
    }
}
