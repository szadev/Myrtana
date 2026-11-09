using System;
using System.Collections.Generic;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// Imported Gothic 3 vegetation (eCVegetation_PS grass / flowers / small bushes) for one region.
    /// Instances live in a binary TextAsset (40 bytes each, see <see cref="InstanceStride"/>), sorted by chunk,
    /// then by mesh; <see cref="batches"/> index into it in runs of at most <see cref="MaxBatch"/> instances.
    /// </summary>
    public sealed class G3VegetationData : ScriptableObject
    {
        /// <summary>pos xyz (m), rotation xyzw, scaleWidth, scaleHeight (f32 each), colour 0xAARRGGBB (u32).</summary>
        public const int InstanceStride = 40;
        /// <summary>Instanced draw limit for constant-buffer instancing.</summary>
        public const int MaxBatch = 1023;

        [Serializable]
        public sealed class MeshEntry
        {
            public string name;
            public Mesh mesh;
            public Material material;
            /// <summary>Largest instance height of this mesh in the region, metres (used for the shadow threshold).</summary>
            public float maxHeight;
            /// <summary>Original eCVegetation_PS ViewRange, metres (largest of all property sets using this mesh).</summary>
            public float viewRange = 50;
        }

        [Serializable]
        public struct Batch
        {
            public int mesh;
            public int start;
            public int count;
        }

        [Serializable]
        public struct Chunk
        {
            public Bounds bounds;
            public int firstBatch;
            public int batchCount;
            public int instanceCount;
            public float viewRange;   // metres, max of the meshes in the chunk
        }

        public Vector3 center;
        public float halfSize;
        public float cellSize = 50;
        public int instanceCount;
        public List<MeshEntry> meshes = new List<MeshEntry>();
        public List<Chunk> chunks = new List<Chunk>();
        public List<Batch> batches = new List<Batch>();
        public TextAsset instances;
    }
}
