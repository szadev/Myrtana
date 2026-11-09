using System;
using System.Collections.Generic;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// The scatter's assets (Docs/remaster-world.md section 18.4), written by the editor's <c>G3ScatterSetBuilder.Build()</c>
    /// (menu G3/Remaster/World/Build Scatter Set) to <c>Assets/G3Imported/Remaster/Scatter/Resources/G3ScatterSet.asset</c>
    /// (git-ignored: it references G3's meshes and the locally generated cards/rocks in GameData/Remaster/Scatter).
    /// <see cref="ScatterCatalog"/> sources resolve here first; G3 sources missing from the set are found at runtime in the
    /// loaded scenes and vegetation regions.
    /// </summary>
    public sealed class G3ScatterSet : ScriptableObject
    {
        public const string ResourceName = "G3ScatterSet";

        [Serializable]
        public struct Entry
        {
            /// <summary>The catalog source: a G3 mesh name or <c>gen:&lt;name&gt;</c>.</summary>
            public string source;
            public Mesh mesh;
            /// <summary>Optional cheaper mesh beyond the LOD distance.</summary>
            public Mesh lod1;
            /// <summary>G3's material of the mesh (its atlas and alpha), or null for generated sources.</summary>
            public Material material;
            /// <summary>Generated card texture (alpha), or null.</summary>
            public Texture2D texture;
            /// <summary>Slice + 1 of the card in <see cref="cardArray"/> (0 = not in it).</summary>
            public int arraySlice1;
        }

        public List<Entry> entries = new List<Entry>();

        /// <summary>Every generated card as one Texture2DArray (imported from a flipbook strip: compression and the texture
        /// quality's mipmap limit as any texture): all cards share one material and one draw call (pass 2).</summary>
        public Texture2DArray cardArray;

        public bool TryGet(string source, out Entry e)
        {
            foreach (var x in entries)
                if (x.source == source) { e = x; return true; }
            e = default;
            return false;
        }
    }
}
