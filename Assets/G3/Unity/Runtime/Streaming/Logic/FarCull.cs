using System;

namespace G3.Unity
{
    /// <summary>
    /// perf-2 (Docs/performance.md, "Far objects: no shadow views, no culling"): the rules of <c>G3FarCull</c>, engine-free
    /// (Tools/G3Tests). The GPU Resident Drawer tests every registered instance against every culling view each frame (camera,
    /// the sun's cascades, six faces per shadowed point light). Two cheap early-outs exist in its CullingJob: an instance
    /// whose shadow casting is off skips every light view, and a renderer that is not rendering (forceRenderingOff) skips
    /// every view. So:
    /// <list type="bullet">
    /// <item><b>Far shadows off</b>: an object farther than the shadow distance plus a margin for its own shadow length
    /// (<see cref="ShadowOnDistance"/>) cannot cast into a visible cascade, nor into a shadowed point light (those are within
    /// ~20 m of the camera). Its shadow casting is switched off there.</item>
    /// <item><b>Small far objects hidden</b>: the drawer's own small-mesh culling (<c>smallMeshScreenPercentage</c>, the
    /// Object detail option) already drops an object without a LODGroup when it covers less than that share of the screen
    /// height (distance = size · lodBias / (2 · h · tan(fov/2)), the same maths as <see cref="TreeFarLod"/>). Beyond that
    /// distance plus <see cref="TreeFarLod"/>'s margin the renderer is switched off: no pixel changes.</item>
    /// </list>
    /// Both with hysteresis so objects at the edge do not flip.
    /// </summary>
    public static class FarCull
    {
        /// <summary>Metres added to the shadow distance (a shadow cast towards the camera from just beyond it).</summary>
        public const float ShadowMarginMetres = 30f;
        /// <summary>Shadow length per metre of object size (sun 14° high: 4 m of shadow per metre of height).</summary>
        public const float ShadowLengthPerSize = 4f;
        /// <summary>Shadow distance assumed at least (shadowed point lights near the camera, shadows switched off).</summary>
        public const float MinShadowDistance = 40f;
        /// <summary>Shadows go off only beyond the on distance times this plus <see cref="ShadowHysteresisMetres"/>.</summary>
        public const float ShadowHysteresis = 1.1f;
        public const float ShadowHysteresisMetres = 10f;

        /// <summary>Closer than this (camera to the bounds' centre) the object must cast shadows.</summary>
        public static float ShadowOnDistance(float size, float shadowDistance) =>
            Math.Max(shadowDistance, MinShadowDistance) + ShadowMarginMetres + Math.Max(size, 0f) * ShadowLengthPerSize;

        /// <summary>Farther than this the object's shadow may go off.</summary>
        public static float ShadowOffDistance(float size, float shadowDistance) =>
            ShadowOnDistance(size, shadowDistance) * ShadowHysteresis + ShadowHysteresisMetres;

        /// <summary>The wanted shadow state given the current one and the camera distance.</summary>
        public static bool WantShadow(bool shadowOn, float distance, float size, float shadowDistance) =>
            shadowOn ? !(distance > ShadowOffDistance(size, shadowDistance)) : distance < ShadowOnDistance(size, shadowDistance);

        /// <summary>
        /// Per-object constant of the small-mesh distance for a screen share <paramref name="screenPercentage"/> (0..100, the
        /// URP asset's / option's value): distance = k · lodBias / tan(fov/2) (<see cref="TreeFarLod.BillboardDistance"/>).
        /// Infinity (never hidden) when small-mesh culling is off.
        /// </summary>
        public static float SmallMeshCoefficient(float size, float screenPercentage) =>
            TreeFarLod.Coefficient(size, screenPercentage * 0.01f);

        /// <summary>
        /// Screen share below which a small <b>transparent</b> object (spider webs, crystals: blended, so the drawer does not
        /// draw or small-mesh cull them; ~800 webs up to 800 m away were drawn one by one at Faring, 1.3 ms of main thread)
        /// is hidden: four times the option's share, at least <see cref="MinTransparentPercentage"/> %; 0 (option off) stays 0.
        /// </summary>
        public static float TransparentPercentage(float screenPercentage) =>
            screenPercentage > 0f ? Math.Max(screenPercentage * 4f, MinTransparentPercentage) : 0f;

        public const float MinTransparentPercentage = 2f;

        /// <summary>The wanted render state (true = rendered) given the current one; <see cref="TreeFarLod"/>'s margins.</summary>
        public static bool WantRendered(bool rendered, float distance, float k, float cameraFactor) =>
            float.IsInfinity(k) || TreeFarLod.WantGeometry(rendered, distance, k, cameraFactor);

        /// <summary>Must be rendered this frame (inside the drawer's own small-mesh distance): not held back by the per-frame cap.</summary>
        public static bool UrgentRender(float distance, float k, float cameraFactor) =>
            float.IsInfinity(k) || TreeFarLod.Urgent(distance, k, cameraFactor);

        /// <summary>Must cast this frame (inside the shadow distance plus half the margin): not held back by the cap.</summary>
        public static bool UrgentShadow(float distance, float size, float shadowDistance) =>
            distance < Math.Max(shadowDistance, MinShadowDistance) + ShadowMarginMetres * 0.5f + Math.Max(size, 0f) * ShadowLengthPerSize;
    }
}
