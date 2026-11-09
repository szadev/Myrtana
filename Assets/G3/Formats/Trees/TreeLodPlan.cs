using System;
using System.Collections.Generic;

namespace G3.Formats.Trees
{
    /// <summary>
    /// LODGroup layout of one original tree (engine-free, used by G3SpeedTreeImporter, tested in Tools/G3Tests).
    /// Input: the start of each built geometry level as t ∈ [0, 1] between the engine's LodNear and LodFar (× tree
    /// height), optionally followed by the billboard. Output: switch distances (m) and the LODGroup screen-relative
    /// transition heights (size = tree height, at the reference field of view), plus the dithered cross-fade width of
    /// every level.
    /// <para>Cross-fade is distance-based (LODGroup.fadeTransitionWidth), never time-animated: the GPU Resident Drawer
    /// excludes renderers of LODGroups with animateCrossFading (GRDExclusionReason.LODAnimateCrossFading); they fall
    /// back to one SRP-batcher draw per renderer, sub-mesh and pass (Docs/performance.md, "Original trees and the GPU
    /// Resident Drawer").</para>
    /// </summary>
    public sealed class TreeLodPlan
    {
        public sealed class Settings
        {
            public float LodDistanceScale = 1f;
            public float MinFirstTransition = 25f;
            public float MinLodStep = 10f;
            public float MinBillboardDistance = 120f;
            public float CullScreenHeight = 0.003f;
            public float ReferenceFieldOfView = 60f;
            /// <summary>Fraction of each level's screen-height band over which it dithers into the next (0 = pop).</summary>
            public float CrossFadeWidth = 0.15f;
        }

        /// <summary>Distance (m) at which each geometry level starts (level 0 starts at the near distance).</summary>
        public float[] Distances;
        /// <summary>Distance (m) at which the billboard takes over (only meaningful with a billboard).</summary>
        public float BillboardDistance;
        /// <summary>Screen-relative transition height per LOD (geometry levels, then the billboard if any); strictly
        /// decreasing, below 1, the last one is the cull height.</summary>
        public float[] Heights;
        /// <summary>LODGroup fadeTransitionWidth per LOD.</summary>
        public float[] FadeWidths;

        /// <param name="levelStarts">t of each built geometry level, ascending, first = 0.</param>
        /// <param name="billboard">A billboard LOD follows the geometry levels.</param>
        /// <param name="heightM">Tree height (m) = LODGroup size.</param>
        /// <param name="lodNearM">Engine LodNear × height (m), before LodDistanceScale.</param>
        /// <param name="lodFarM">Engine LodFar × height (m), before LodDistanceScale.</param>
        public static TreeLodPlan Build(IReadOnlyList<float> levelStarts, bool billboard, float heightM, float lodNearM, float lodFarM, Settings s)
        {
            if (levelStarts == null || levelStarts.Count == 0) throw new ArgumentException("no geometry level", nameof(levelStarts));
            float near = lodNearM * s.LodDistanceScale, far = lodFarM * s.LodDistanceScale;
            int geom = levelStarts.Count;
            var distances = new float[geom];
            for (int j = 0; j < geom; j++)
            {
                float d = near + levelStarts[j] * (far - near);
                if (j > 0) d = Math.Max(d, Math.Max(s.MinFirstTransition + (j - 1) * s.MinLodStep, distances[j - 1] + s.MinLodStep));
                distances[j] = d;
            }
            float billboardAt = Math.Max(far, Math.Max(s.MinBillboardDistance, distances[geom - 1] + s.MinLodStep));

            float size = Math.Max(heightM, 0.5f);
            double tanHalf = Math.Tan(s.ReferenceFieldOfView * 0.5 * Math.PI / 180.0);
            float Height(float distance) => (float)(size / (2.0 * Math.Max(distance, 0.1f) * tanHalf));

            int count = geom + (billboard ? 1 : 0);
            var heights = new float[count];
            // Height at which LOD j hands over to j + 1; the last one is the cull height. Strictly decreasing.
            heights[count - 1] = s.CullScreenHeight;
            for (int j = count - 2; j >= 0; j--)
            {
                float h = j < geom - 1 ? Height(distances[j + 1]) : Height(billboardAt);
                heights[j] = Math.Max(h, heights[j + 1] * 1.1f);
            }
            if (heights[0] >= 1f)
            {
                float k = 0.95f / heights[0];
                for (int j = 0; j < count; j++) heights[j] *= k;
            }

            var widths = new float[count];
            float w = Math.Min(Math.Max(s.CrossFadeWidth, 0f), 1f);
            for (int j = 0; j < count; j++) widths[j] = w;

            return new TreeLodPlan { Distances = distances, BillboardDistance = billboardAt, Heights = heights, FadeWidths = widths };
        }
    }
}
