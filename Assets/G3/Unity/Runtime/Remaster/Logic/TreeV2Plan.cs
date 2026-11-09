using System;
using System.Collections.Generic;

namespace G3.Unity
{
    /// <summary>
    /// Engine-free plan of the world remaster v2 trees (Docs/remaster-world.md 10.15, switch <c>v2TreeReplace</c>):
    /// which G3 SpeedTree (.spt stem) is replaced by which species and model variants, how a model is fitted to the G3
    /// tree it replaces, the LOD transitions and the impostor frame maths the G3/SpeedTree shader mirrors.
    /// The models come from Tools/Remaster/trees (own procedural generator, CC0 bark and leaf scans); trees without a
    /// rule keep their G3 SpeedTree.
    /// </summary>
    public static class TreeV2Plan
    {
        public sealed class Rule
        {
            public string Contains;    // case-insensitive substring of the .spt stem
            public string Species;
            public int[] Variants;     // allowed model variants (null = all)
        }

        /// <summary>Checked in order; the first match wins. XXL trees (40 m UmbrellaThorn) stay G3.</summary>
        public static readonly Rule[] Rules =
        {
            new Rule { Contains = "_XXL_", Species = null },
            // Nordmar first: the winter firs share "DouglasFir" with Myrtana's
            new Rule { Contains = "DouglasFir_01_winter", Species = "firwinter", Variants = new[] { 0 } },
            new Rule { Contains = "DouglasFir_02_winter", Species = "firwinter", Variants = new[] { 0 } },
            new Rule { Contains = "DouglasFir_03_winter", Species = "firwinter", Variants = new[] { 1 } },
            new Rule { Contains = "DouglasFir_04_winter", Species = "firwinter", Variants = new[] { 1 } },
            new Rule { Contains = "_bush_0", Species = "bushwinter" },
            new Rule { Contains = "_S_DouglasFir_", Species = "fir", Variants = new[] { 0 } },
            new Rule { Contains = "DouglasFir_", Species = "fir", Variants = new[] { 1, 2 } },
            new Rule { Contains = "_XS_CarolinaBuckthorn_", Species = "buckthorn", Variants = new[] { 1, 2 } },
            new Rule { Contains = "CarolinaBuckthorn_", Species = "buckthorn" },
            new Rule { Contains = "BulfordHolly_", Species = "holly" },
            new Rule { Contains = "CoconutPalm_", Species = "palm", Variants = new[] { 0, 1 } },
            new Rule { Contains = "CurlyPalm_", Species = "palm", Variants = new[] { 0, 1 } },
            new Rule { Contains = "DatePalm_", Species = "palm", Variants = new[] { 2 } },
            new Rule { Contains = "FanPalm_", Species = "fanpalm" },
            new Rule { Contains = "UmbrellaThorn_", Species = "deadwood" },
            new Rule { Contains = "Gimlet_", Species = "deadwood" },
        };

        /// <summary>The rule for a .spt stem (with or without ".spt"), or null when the G3 tree stays.</summary>
        public static Rule Resolve(string sptName)
        {
            if (string.IsNullOrEmpty(sptName)) return null;
            foreach (var r in Rules)
                if (sptName.IndexOf(r.Contains, StringComparison.OrdinalIgnoreCase) >= 0)
                    return r.Species == null ? null : r;
            return null;
        }

        /// <summary>Deterministic variant per placement (hash of the position in 0.5 m cells) among the allowed ones.</summary>
        public static int PickVariant(Rule rule, int modelVariants, float x, float z)
        {
            if (modelVariants <= 0) return -1;
            uint h = unchecked((uint)(int)Math.Floor(x * 2) * 73856093u ^ (uint)(int)Math.Floor(z * 2) * 19349663u);
            h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
            if (rule?.Variants == null || rule.Variants.Length == 0) return (int)(h % (uint)modelVariants);
            int v = rule.Variants[(int)(h % (uint)rule.Variants.Length)];
            return v < modelVariants ? v : (int)(h % (uint)modelVariants);
        }

        /// <summary>
        /// Local scale of a model (authored height/half width) under a G3 tree whose generated size (scale 1) is
        /// refHeight × refWidth: the height matches exactly, the width follows within ±25 % of the uniform scale (the
        /// silhouette keeps the species' proportions).
        /// </summary>
        public static (float xz, float y) Fit(float refHeight, float refWidth, float modelHeight, float modelHalfWidth)
        {
            float y = refHeight / Math.Max(modelHeight, 0.01f);
            if (refWidth <= 0) return (y, y);
            float xz = refWidth / Math.Max(2 * modelHalfWidth, 0.01f);
            return (Math.Min(Math.Max(xz, 0.8f * y), 1.25f * y), y);
        }

        /// <summary>
        /// LODGroup screen-relative transition heights for LOD0, LOD1, LOD2, impostor (the last is the cull height).
        /// A 15 m tree at 60° vertical FOV: LOD0 to ≈ 43 m, LOD1 to ≈ 108 m, LOD2 to ≈ 216 m, then the impostor (cull at
        /// ≈ 2.2 km). Runner-32 measured +2.1..3.9 ms GPU in the forest with {0.25, 0.10, 0.04}: earlier LOD1 and impostor. Faring estimate (G3's density, 9 m trees): ≈ 0.3 M triangles of v2 trees in view against ≈ 4.5 M if
        /// every tree kept G3-like 500-triangle geometry to the cull distance; the cost is alpha-tested overdraw, not triangles.
        /// </summary>
        public static readonly float[] LodHeights = { 0.30f, 0.12f, 0.06f, 0.006f };

        /// <summary>Cross-fade (dithered) width per LOD, fraction of the LOD's screen-height range.</summary>
        public const float FadeWidth = 0.15f;

        /// <summary>Distance (m) at which a tree of the given height leaves LOD k (vertical field of view in degrees).</summary>
        public static float SwitchDistance(float height, int lod, float fovDegrees = 60, float lodBias = 1)
        {
            float half = (float)Math.Tan(fovDegrees * Math.PI / 360);
            return height * lodBias / (2 * half * LodHeights[lod]);
        }

        /// <summary>
        /// Impostor frame (0..frames-1) seen from the object-space horizontal direction (dx, dz) pointing from the tree to
        /// the viewer. Frame f was rendered from yaw f × 360°/frames, yaw 0 = viewer on +z, counter-clockwise from above
        /// (towards +x). Same maths as G3TreeImpostorFrame in G3SpeedTree.hlsl.
        /// </summary>
        public static int ImpostorFrame(float dx, float dz, int frames)
        {
            if (frames <= 1) return 0;
            double yaw = Math.Atan2(dx, dz);                       // 0 at +z, +90° at +x
            double f = yaw / (2 * Math.PI) * frames;
            int i = (int)Math.Floor(f + 0.5);
            return ((i % frames) + frames) % frames;
        }

        // ------------------------------------------------------------------ G3 data

        /// <summary>
        /// Generated size of every replaced G3 tree at scale 1 (m, from Tools/SptBake: bounds height, max horizontal
        /// extent); the swap multiplies by the placement's own scale (G3 root transform).
        /// </summary>
        public static readonly Dictionary<string, (float height, float width)> G3Size =
            new Dictionary<string, (float, float)>(StringComparer.OrdinalIgnoreCase)
            {
                ["G3_Tree_L_DouglasFir_01"] = (28.68f, 13.03f), ["G3_Tree_L_DouglasFir_02"] = (21.49f, 9.08f),
                ["G3_Tree_L_DouglasFir_01_winter"] = (21.65f, 9.02f), ["G3_Tree_L_DouglasFir_02_winter"] = (13.19f, 5.28f),
                ["G3_Tree_L_DouglasFir_03_winter"] = (27.98f, 11.97f), ["G3_Tree_L_DouglasFir_04_winter"] = (31.94f, 13.65f),
                ["G3_Tree_M_DouglasFir_01"] = (14.26f, 6.94f), ["G3_Tree_M_DouglasFir_02"] = (14.98f, 6.92f),
                ["G3_Tree_M_DouglasFir_04"] = (17.40f, 7.32f), ["G3_Tree_S_DouglasFir_02"] = (8.14f, 4.54f),
                ["G3_Tree_M_CoconutPalm_01"] = (9.10f, 5.69f), ["G3_Tree_M_CoconutPalm_02"] = (14.61f, 10.37f),
                ["G3_Tree_M_CoconutPalm_03"] = (11.83f, 6.35f), ["G3_Tree_M_CurlyPalm_01"] = (14.36f, 14.78f),
                ["G3_Tree_M_CurlyPalm_02"] = (11.08f, 12.18f), ["G3_Tree_M_DatePalm_01"] = (12.01f, 10.13f),
                ["G3_Tree_M_DatePalm_02"] = (9.65f, 7.75f), ["G3_Tree_M_FanPalm_01"] = (11.11f, 5.58f),
                ["G3_Tree_M_Gimlet_01"] = (6.69f, 6.06f), ["G3_Tree_M_Gimlet_02"] = (8.56f, 8.61f),
                ["G3_Tree_M_Gimlet_03"] = (8.58f, 5.99f), ["G3_Tree_M_Gimlet_04"] = (11.32f, 8.72f),
                ["G3_Tree_M_UmbrellaThorn_01"] = (9.37f, 8.78f), ["G3_Tree_M_UmbrellaThorn_02"] = (7.96f, 5.31f),
                ["G3_Tree_M_UmbrellaThorn_03"] = (18.97f, 14.10f), ["G3_Tree_M_UmbrellaThorn_04"] = (16.73f, 13.93f),
                ["G3_Tree_S_BulfordHolly_01"] = (4.64f, 4.16f), ["G3_Tree_S_BulfordHolly_02"] = (7.63f, 9.84f),
                ["G3_Tree_XS_BulfordHolly_01"] = (3.36f, 4.22f),
                ["G3_Tree_S_CarolinaBuckthorn_01"] = (5.27f, 3.55f), ["G3_Tree_S_CarolinaBuckthorn_02"] = (8.06f, 8.25f),
                ["G3_Tree_S_CarolinaBuckthorn_03"] = (6.65f, 5.58f), ["G3_Tree_S_CarolinaBuckthorn_04"] = (7.18f, 5.30f),
                ["G3_Tree_XS_CarolinaBuckthorn_01"] = (2.51f, 1.85f), ["G3_Tree_XS_CarolinaBuckthorn_02"] = (2.70f, 2.20f),
                ["G3_Tree_XS_CarolinaBuckthorn_03"] = (2.43f, 2.30f),
                ["G3_Tree_bush_01_winter"] = (3.65f, 4.69f), ["G3_Tree_bush_02_winter"] = (6.57f, 8.98f),
                ["G3_Tree_bush_03_winter"] = (3.97f, 3.83f), ["G3_Tree_bush_04_winter"] = (5.24f, 6.25f),
                ["G3_Tree_bush_05_winter"] = (6.54f, 7.92f), ["G3_Tree_bush_06_winter"] = (5.03f, 6.05f),
            };

        /// <summary>World placements of the most used G3 trees (G3Cli spt usage, 57,315 enabled in total).</summary>
        public const int G3TotalPlacements = 57315;

        public static readonly Dictionary<string, int> G3Placements = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["G3_Tree_S_CarolinaBuckthorn_02"] = 10171, ["G3_Tree_S_DouglasFir_02"] = 6956, ["G3_Tree_M_DouglasFir_01"] = 3676,
            ["G3_Tree_M_DouglasFir_02"] = 3669, ["G3_Tree_S_CarolinaBuckthorn_03"] = 2729, ["G3_Tree_bush_05_winter"] = 2232,
            ["G3_Tree_bush_01_winter"] = 2094, ["G3_Tree_M_DouglasFir_04"] = 1953, ["G3_Tree_bush_04_winter"] = 1879,
            ["G3_Tree_M_CoconutPalm_02"] = 1713, ["G3_Tree_L_DouglasFir_02"] = 1436, ["G3_Tree_bush_02_winter"] = 1234,
            ["G3_Tree_bush_03_winter"] = 1001, ["G3_Tree_XS_CarolinaBuckthorn_02"] = 977, ["G3_Tree_L_DouglasFir_01_winter"] = 975,
            ["G3_Tree_L_DouglasFir_01"] = 961, ["G3_Tree_L_DouglasFir_02_winter"] = 889, ["G3_Tree_XS_CarolinaBuckthorn_03"] = 850,
            ["G3_Tree_bush_06_winter"] = 808, ["G3_Tree_S_CarolinaBuckthorn_01"] = 794, ["G3_Tree_L_DouglasFir_03_winter"] = 731,
            ["G3_Tree_L_DouglasFir_04_winter"] = 705, ["G3_Tree_XS_BulfordHolly_01"] = 654, ["G3_Tree_M_CoconutPalm_03"] = 641,
            ["G3_Tree_S_BulfordHolly_01"] = 596, ["G3_Tree_XS_CarolinaBuckthorn_01"] = 548, ["G3_Tree_M_FanPalm_01"] = 534,
            ["G3_Tree_S_BulfordHolly_02"] = 495, ["G3_Tree_M_CoconutPalm_01"] = 400, ["G3_Tree_M_LongleafPine_03"] = 326,
            ["G3_Tree_M_CurlyPalm_01"] = 305, ["G3_Tree_S_Linden_01"] = 272, ["G3_Tree_M_CurlyPalm_02"] = 239,
            ["G3_Tree_M_Gimlet_03"] = 207, ["G3_Tree_L_HoneyLocust_01"] = 202, ["G3_Tree_S_GSApple_01"] = 201,
            ["G3_Tree_S_CarolinaBuckthorn_04"] = 198, ["G3_Tree_L_PinOak_01"] = 191, ["G3_Tree_M_Gimlet_04"] = 185,
            ["G3_Tree_M_JoshuaTree_01"] = 180, ["G3_Tree_S_EuropeanAspen_01"] = 171, ["G3_Tree_M_DatePalm_01"] = 148,
            ["G3_Tree_M_UmbrellaThorn_02"] = 147, ["G3_Tree_M_JoshuaTree_02"] = 122, ["G3_Tree_M_CrepeMyrtle_01"] = 115,
            ["G3_Tree_M_UmbrellaThorn_01"] = 114, ["G3_Tree_L_ItalianCypress_02"] = 103, ["G3_Tree_M_Gimlet_01"] = 98,
        };

        /// <summary>Placements replaced per species and in total (of <see cref="G3TotalPlacements"/>), from the table above.</summary>
        public static (Dictionary<string, int> perSpecies, int replaced) Coverage()
        {
            var d = new Dictionary<string, int>();
            int total = 0;
            foreach (var kv in G3Placements)
            {
                var r = Resolve(kv.Key);
                if (r == null) continue;
                d.TryGetValue(r.Species, out int n);
                d[r.Species] = n + kv.Value;
                total += kv.Value;
            }
            return (d, total);
        }
    }
}
