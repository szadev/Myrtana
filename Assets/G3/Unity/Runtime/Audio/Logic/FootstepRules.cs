using System;

namespace G3.Audio
{
    public enum FootstepGait { None, Walk, Run, Sprint }

    /// <summary>
    /// Footstep decisions: gait and cadence from speed, surface token from a material/object name, effect name
    /// (g3.efm has eff_step_human_{walk,run,sprint}_{left,right}_{earth,grass,sand,snow,stone,water,wood}_01,
    /// Docs/formats-audio.md §4.1).
    /// </summary>
    public static class FootstepRules
    {
        public static readonly string[] Surfaces = { "earth", "grass", "sand", "snow", "stone", "water", "wood" };

        /// <summary>Ordered keyword → surface table (first match wins; "sandstone" must precede "sand").</summary>
        static readonly (string key, string surface)[] Keywords =
        {
            ("water", "water"), ("river", "water"), ("ocean", "water"),
            ("snow", "snow"), ("_ice", "snow"), ("glacier", "snow"),
            ("sandstone", "stone"),
            ("sand", "sand"), ("dune", "sand"),
            ("wood", "wood"), ("plank", "wood"), ("bridge", "wood"), ("timber", "wood"), ("board", "wood"),
            ("grass", "grass"), ("forest", "grass"), ("farn", "grass"), ("moss", "grass"), ("meadow", "grass"), ("leaf", "grass"),
            ("stone", "stone"), ("rock", "stone"), ("marble", "stone"), ("plate", "stone"), ("tile", "stone"),
            ("cobble", "stone"), ("brick", "stone"), ("wall", "stone"), ("city", "stone"), ("crystal", "stone"),
            ("metal", "stone"), ("dungeon", "stone"), ("cave", "stone"),
            ("earth", "earth"), ("path", "earth"), ("ground", "earth"), ("mud", "earth"), ("dirt", "earth"), ("gravel", "earth"),
        };

        public static FootstepGait GaitFor(float speed, float walkSpeed, float runSpeed)
        {
            if (speed < 0.6f) return FootstepGait.None;
            if (speed < (walkSpeed + runSpeed) * 0.5f) return FootstepGait.Walk;
            if (speed < runSpeed * 1.2f) return FootstepGait.Run;
            return FootstepGait.Sprint;
        }

        /// <summary>Seconds between two steps.</summary>
        public static float StepInterval(FootstepGait gait)
        {
            switch (gait)
            {
                case FootstepGait.Walk: return 0.52f;
                case FootstepGait.Run: return 0.34f;
                case FootstepGait.Sprint: return 0.29f;
                default: return float.PositiveInfinity;
            }
        }

        /// <summary>
        /// eEShapeMaterial (G3's floor material) → footstep surface token of g3.efm; the same table as
        /// G3.Formats.Audio.Footsteps.MaterialToken (Metal/Glass → stone, Ice → snow, Foliage → grass, else earth [inferred]).
        /// </summary>
        public static string SurfaceFromShapeMaterial(int shapeMaterial)
        {
            switch (shapeMaterial)
            {
                case 1: return "wood";
                case 3: return "water";
                case 2: case 4: case 9: return "stone";
                case 6: case 11: return "snow";
                case 13: case 15: return "grass";
                case 20: return "sand";
                default: return "earth";
            }
        }

        /// <summary>
        /// Surface token for a material/object name, or null. Blend materials ("A_2_B") are classified by their first
        /// part (the base layer).
        /// </summary>
        public static string SurfaceFromName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var n = name.ToLowerInvariant();
            int blend = n.IndexOf("_2_", StringComparison.Ordinal);
            if (blend > 0) n = n.Substring(0, blend);
            foreach (var (key, surface) in Keywords)
                if (n.Contains(key)) return surface;
            return null;
        }

        /// <summary>Fallback when nothing could be classified: Varant sand, Nordmar snow, otherwise earth.</summary>
        public static string RegionDefault(string region)
        {
            var r = (region ?? "").ToLowerInvariant();
            if (r.Contains("varant")) return "sand";
            if (r.Contains("nordmar")) return "snow";
            return "earth";
        }

        public static string EffectName(FootstepGait gait, bool left, string surface) =>
            $"eff_step_human_{gait.ToString().ToLowerInvariant()}_{(left ? "left" : "right")}_{surface ?? "earth"}_01";
    }
}
