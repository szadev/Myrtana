using System;

namespace G3.Unity
{
    /// <summary>fix-171 [owner]: quality of the remaster's Lighting part (Options -> Remaster, "Lighting quality").</summary>
    public enum LightingQuality { Low, Medium, High, Ultra }

    /// <summary>
    /// fix-171 (Docs/remaster-world.md section 19): what each <see cref="LightingQuality"/> level changes in the look settings.
    /// Ultra is G3WorldLook.json as authored (the look of build 21a); the lower levels scale only the expensive pieces and
    /// keep every effect that defines the look (sun/ambient balance, fog, shafts, clouds, sky occlusion, SSAO, grade):
    /// <list type="bullet">
    /// <item>shadows: distance, cascades, GRD small-caster culling (the CPU cost: every caster is culled and submitted per
    /// cascade; 90 m added ~750 casters at Faring);</item>
    /// <item>motion vectors of plants (the TAA's wind motion pass) by distance;</item>
    /// <item>sky occlusion map: resolution and the rebuild interval for streamed cells;</item>
    /// <item>ray-march steps of the contact shadows, the fog/shafts and the cloud slab;</item>
    /// <item>sky reflection cube: size and refresh interval; cloud shadow map: frames between renders;</item>
    /// <item>GI volume (Modern lighting) only from High.</item>
    /// </list>
    /// Values are only lowered (never raised above the JSON), so a lighter JSON stays light. Engine-free (tests).
    /// </summary>
    public static class LightingQualityLevels
    {
        public const string PrefKey = "G3.Remaster.LightingQuality";
        public const string EnvVar = "G3_REMASTER_LIGHTING_QUALITY";

        /// <summary>Parses "low|medium|high|ultra" or "0..3"; null when not one of them.</summary>
        public static LightingQuality? Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            value = value.Trim();
            if (int.TryParse(value, out int i) && i >= 0 && i <= 3) return (LightingQuality)i;
            foreach (LightingQuality q in Enum.GetValues(typeof(LightingQuality)))
                if (string.Equals(q.ToString(), value, StringComparison.OrdinalIgnoreCase)) return q;
            return null;
        }

        /// <summary>
        /// The default level from the game's Performance preset (0 Low, 1 Medium, 2 High, 3 Very high, 4 Custom) and, for
        /// Custom, its shadow quality (0 off .. 4 very high): one step below the preset, so the default costs about what the
        /// rest of the preset does (Very high -> High); Ultra only by choice.
        /// </summary>
        public static LightingQuality FromPreset(int preset, int shadowQuality)
        {
            switch (preset)
            {
                case 0: case 1: return LightingQuality.Low;
                case 2: return LightingQuality.Medium;
                case 3: return LightingQuality.High;
                default: return shadowQuality >= 4 ? LightingQuality.High : shadowQuality == 3 ? LightingQuality.Medium : LightingQuality.Low;
            }
        }

        /// <summary>A copy of <paramref name="json"/> at <paramref name="q"/> (Ultra: an unchanged copy).</summary>
        public static WorldLookSettings Apply(WorldLookSettings json, LightingQuality q)
        {
            var s = json.Clamped();
            if (q == LightingQuality.Ultra) return s;
            switch (q)
            {
                case LightingQuality.High:
                    s.v2ShadowDistance = Math.Min(s.v2ShadowDistance, 80f);
                    s.v2MotionDistance = Math.Min(s.v2MotionDistance, 60f);
                    s.v2SkyOccInterval = Math.Max(s.v2SkyOccInterval, 4f);
                    s.v2ContactSteps = Math.Min(s.v2ContactSteps, 10);
                    s.v2ShaftSteps = Math.Min(s.v2ShaftSteps, 12);
                    s.v2CloudSteps = Math.Min(s.v2CloudSteps, 12);
                    s.v2SkyReflectionInterval = Math.Max(s.v2SkyReflectionInterval, 2f);
                    s.v2CloudShadowEvery = Math.Max(s.v2CloudShadowEvery, 2);
                    break;
                case LightingQuality.Medium:
                    s.v2ShadowDistance = Math.Min(s.v2ShadowDistance, 65f);
                    s.v2ShadowCascades = Math.Min(s.v2ShadowCascades, 3);
                    s.v2ShadowSmallMesh = Math.Max(s.v2ShadowSmallMesh, 0.5f);
                    s.v2MotionDistance = Math.Min(s.v2MotionDistance, 35f);
                    s.v2SkyOccResolution = Math.Min(s.v2SkyOccResolution, 128);
                    s.v2SkyOccInterval = Math.Max(s.v2SkyOccInterval, 6f);
                    s.v2ContactSteps = Math.Min(s.v2ContactSteps, 8);
                    s.v2ShaftSteps = Math.Min(s.v2ShaftSteps, 10);
                    s.v2CloudSteps = Math.Min(s.v2CloudSteps, 10);
                    s.v2SkyReflectionSize = Math.Min(s.v2SkyReflectionSize, 64);
                    s.v2SkyReflectionInterval = Math.Max(s.v2SkyReflectionInterval, 4f);
                    s.v2CloudShadowEvery = Math.Max(s.v2CloudShadowEvery, 4);
                    s.v2GiStrength = 0;   // GI volume / Modern lighting only from High
                    s.v2Taa = Math.Min(s.v2Taa, 1);   // URP TAA instead of STP (~0.3 ms less at 1080p)
                    break;
                default:   // Low
                    s.v2Shadows = false;  // the URP asset's / Performance menu's shadows
                    s.v2MotionDistance = Math.Min(s.v2MotionDistance, 15f);
                    s.v2SkyOccResolution = Math.Min(s.v2SkyOccResolution, 128);
                    s.v2SkyOccInterval = Math.Max(s.v2SkyOccInterval, 10f);
                    s.v2Contact = false;
                    s.v2Bounce = 0;
                    s.v2ShaftSteps = Math.Min(s.v2ShaftSteps, 8);
                    s.v2CloudSteps = Math.Min(s.v2CloudSteps, 8);
                    s.v2SkyReflectionSize = Math.Min(s.v2SkyReflectionSize, 64);
                    s.v2SkyReflectionInterval = Math.Max(s.v2SkyReflectionInterval, 8f);
                    s.v2CloudShadows = false;
                    s.v2GiStrength = 0;
                    s.v2Taa = Math.Min(s.v2Taa, 1);
                    break;
            }
            return s.Clamped();
        }

        /// <summary>The Options row name.</summary>
        public static string RowName(string language) => language == "Russian" ? "Качество освещения" : "Lighting quality";

        /// <summary>The Options row value.</summary>
        public static string Value(LightingQuality q, string language)
        {
            bool ru = language == "Russian";
            switch (q)
            {
                case LightingQuality.Low: return ru ? "Низкое" : "Low";
                case LightingQuality.Medium: return ru ? "Среднее" : "Medium";
                case LightingQuality.High: return ru ? "Высокое" : "High";
                default: return ru ? "Ультра" : "Ultra";
            }
        }

        /// <summary>One line: what the level changes against the JSON (runner, log).</summary>
        public static string Describe(WorldLookSettings s, LightingQuality q) =>
            $"{q}: shadows {(s.v2Shadows ? $"{s.v2ShadowDistance:0} m/{s.v2ShadowCascades}c/{s.v2ShadowResolution}{(s.v2ShadowSmallMesh >= 0 ? $"/small {s.v2ShadowSmallMesh:0.#}%" : "")}" : "asset's")}, " +
            $"aa {(s.v2Taa == 2 ? "stp" : s.v2Taa == 1 ? "taa" : "off")}, mv {s.v2MotionDistance:0} m, sky occ {s.v2SkyOccResolution}/{s.v2SkyOccInterval:0.#} s, contact {(s.v2Contact ? s.v2ContactSteps.ToString() : "off")}, " +
            $"bounce {s.v2Bounce:0.#}, shafts {s.v2ShaftSteps}, clouds {s.v2CloudSteps}, reflection {s.v2SkyReflectionSize}/{s.v2SkyReflectionInterval:0.#} s, " +
            $"cloud shadows {(s.v2CloudShadows ? $"1/{s.v2CloudShadowEvery}" : "off")}, gi {(s.v2GiStrength > 0 ? "allowed" : "off")}";
    }
}
