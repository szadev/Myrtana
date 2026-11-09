using System;

namespace G3.Unity
{
    /// <summary>
    /// perf-22d (Docs/performance.md, "Far trees: billboard only"): a tree LODGroup (geometry levels LOD0..LODn + one
    /// billboard / impostor level) can only show its last level beyond the distance where the screen height of the
    /// tree falls below the last geometry level's transition height. Its geometry renderers are switched off there, so
    /// the GPU Resident Drawer culls one instance per far tree instead of ~5 for every culling view (camera, sun cascades,
    /// six faces per shadowed point light). Same LOD maths as Unity (and <c>TreeLodPlan</c>): screen height =
    /// size / (2 · distance · tan(fov / 2)), scaled by <c>QualitySettings.lodBias</c>. A margin keeps the geometry on well
    /// before the cross-fade band; a hysteresis band keeps trees at the edge from flipping. Engine-free (Tools/G3Tests).
    /// </summary>
    public static class TreeFarLod
    {
        /// <summary>Geometry stays on up to this factor of the billboard distance ...</summary>
        public const float Margin = 1.25f;
        /// <summary>... plus these metres (fast riding / a camera swing between two sweeps).</summary>
        public const float MarginMetres = 20f;
        /// <summary>Switched off again only beyond the on distance times this ...</summary>
        public const float Hysteresis = 1.15f;
        /// <summary>... plus these metres.</summary>
        public const float HysteresisMetres = 10f;

        /// <summary>
        /// Per tree constant k = worldSize / (2 · lastGeometryTransition): the billboard distance is k · lodBias / tan(fov/2).
        /// Infinity (never billboard only) for a missing transition.
        /// </summary>
        public static float Coefficient(float worldSize, float lastGeometryTransition) =>
            lastGeometryTransition > 0f && worldSize > 0f ? worldSize / (2f * lastGeometryTransition) : float.PositiveInfinity;

        /// <summary>lodBias / tan(fov/2) for a perspective camera (fov in degrees, vertical).</summary>
        public static float CameraFactor(float fovDegrees, float lodBias)
        {
            double tanHalf = Math.Tan(Math.Max(1f, Math.Min(179f, fovDegrees)) * 0.5 * Math.PI / 180.0);
            return (float)(Math.Max(lodBias, 0.01f) / tanHalf);
        }

        /// <summary>Distance beyond which only the billboard level can be selected.</summary>
        public static float BillboardDistance(float k, float cameraFactor) => k * cameraFactor;

        /// <summary>Closer than this the geometry must be on.</summary>
        public static float OnDistance(float k, float cameraFactor) => BillboardDistance(k, cameraFactor) * Margin + MarginMetres;

        /// <summary>Farther than this the geometry may go off.</summary>
        public static float OffDistance(float k, float cameraFactor) => OnDistance(k, cameraFactor) * Hysteresis + HysteresisMetres;

        /// <summary>The wanted geometry state given the current one and the camera distance (hysteresis between on and off).</summary>
        public static bool WantGeometry(bool geometryOn, float distance, float k, float cameraFactor) =>
            geometryOn ? !(distance > OffDistance(k, cameraFactor)) : distance < OnDistance(k, cameraFactor);

        /// <summary>
        /// The geometry is needed this frame (the camera is inside the billboard distance with a small margin): switching
        /// it on must not wait for the per-frame cap, else the tree would show nothing.
        /// </summary>
        public static bool Urgent(float distance, float k, float cameraFactor) => distance < BillboardDistance(k, cameraFactor) * 1.05f + 5f;
    }
}
