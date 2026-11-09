using System;
using System.Collections.Generic;

namespace G3.Player
{
    /// <summary>
    /// Pure rules for a body (alive, knocked out or dead) whose feet ended up in the floor (fix-163; no engine types,
    /// tested in Tools/G3Tests). G3 puts every body on the floor of a vertical ray from above its origin
    /// (gCCharacterMovement_PS PutToGround; a corpse gets ForceGroundAlignment), whatever height its routine point or
    /// spawn carries. Ours places a station NPC at its slot's authored height and a corpse where the last settling left
    /// its origin, so a slot authored under a yard's floor, or a corpse whose settling failed (no ground collider yet),
    /// showed the body half in the ground (owner, Vengard yard). The surfaces are the column's up-facing faces at the
    /// body's origin (any order), as <see cref="NpcGroundRules.Surface"/>.
    /// <list type="bullet">
    /// <item><see cref="StationFloor"/>: a station slot stands on the highest floor within a step above or below it
    /// (PutToGround from a step over the slot); furniture slots (bed, bench, stool, throne) keep their height.</item>
    /// <item><see cref="SinkLift"/>: the safety net. A body not standing on anything (no floor within
    /// <see cref="Support"/> of its feet) whose feet are more than <see cref="Tolerance"/> under the nearest floor above
    /// them (within <see cref="Reach"/>: deeper is another storey or a roof) is lifted onto it.</item>
    /// </list>
    /// </summary>
    public static class BodySinkRules
    {
        /// <summary>Feet deeper than this under the floor above them are sunk and lifted (m).</summary>
        public const float Tolerance = 0.25f;
        /// <summary>A floor this close to the feet (above or below) carries the body: not sunk (m).</summary>
        public const float Support = 0.15f;
        /// <summary>Farthest floor above the feet that is still the body's own (m): deeper is under a roof or storey.</summary>
        public const float Reach = 1.2f;
        /// <summary>Furniture interactions whose slot height is the seat / bed, not a floor.</summary>
        public static readonly string[] FurnitureInteractions = { "Bed", "Bench", "Stool", "Throne" };

        /// <summary>The interaction places the body on furniture (its slot height is kept).</summary>
        public static bool OnFurniture(string interaction)
        {
            if (string.IsNullOrEmpty(interaction)) return false;
            foreach (var f in FurnitureInteractions)
                if (string.Equals(f, interaction, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static bool Floor(NpcGroundRules.Surface s) => s.SlopeDeg <= NpcGroundRules.MaxFloorSlope;

        /// <summary>
        /// The signed height of the body's own floor over its feet: 0 when a floor carries it (within
        /// <see cref="Support"/>), &gt; 0 when the feet are under the nearest floor above them (within <see cref="Reach"/>),
        /// &lt; 0 when it hangs over the nearest floor below, NaN with neither. <paramref name="index"/>: that floor, -1 for NaN.
        /// </summary>
        public static float Gap(float feetY, IReadOnlyList<NpcGroundRules.Surface> surfaces, out int index)
        {
            index = -1;
            if (surfaces == null) return float.NaN;
            int support = -1, above = -1, below = -1;
            for (int i = 0; i < surfaces.Count; i++)
            {
                var s = surfaces[i];
                if (!Floor(s)) continue;
                float dy = s.Y - feetY;
                if (Math.Abs(dy) <= Support) { if (support < 0 || Math.Abs(dy) < Math.Abs(surfaces[support].Y - feetY)) support = i; }
                else if (dy > 0) { if (dy <= Reach && (above < 0 || s.Y < surfaces[above].Y)) above = i; }
                else if (below < 0 || s.Y > surfaces[below].Y) below = i;
            }
            if (support >= 0) { index = support; return 0f; }
            if (above >= 0) { index = above; return surfaces[above].Y - feetY; }
            if (below >= 0) { index = below; return surfaces[below].Y - feetY; }
            return float.NaN;
        }

        /// <summary>
        /// How far a body whose feet are at <paramref name="feetY"/> is lifted: the depth under the floor above its feet when it
        /// is sunk deeper than <see cref="Tolerance"/> (not standing on anything), else 0. Never lowers.
        /// </summary>
        public static float SinkLift(float feetY, IReadOnlyList<NpcGroundRules.Surface> surfaces, out int index)
        {
            float gap = Gap(feetY, surfaces, out index);
            if (float.IsNaN(gap) || gap <= Tolerance) { index = -1; return 0f; }
            return gap;
        }

        /// <summary>
        /// The floor a station slot authored at <paramref name="slotY"/> stands on: the highest floor within a step
        /// (<see cref="SlopeRules.StepHeight"/>) above or below it, else the slope reach of
        /// <see cref="NpcGroundRules.UpReach"/> above. False when none: the slot keeps its height.
        /// </summary>
        public static bool StationFloor(float slotY, IReadOnlyList<NpcGroundRules.Surface> surfaces, float halfLength, out int index)
        {
            index = -1;
            if (surfaces == null) return false;
            for (int i = 0; i < surfaces.Count; i++)
            {
                var s = surfaces[i];
                if (!Floor(s)) continue;
                float dy = s.Y - slotY;
                if (dy < -SlopeRules.StepHeight || dy > Math.Max(SlopeRules.StepHeight, NpcGroundRules.UpReach(halfLength, s.SlopeDeg))) continue;
                if (index < 0 || s.Y > surfaces[index].Y) index = i;
            }
            return index >= 0;
        }

        /// <summary>
        /// A lying body measured against the ground under it (<paramref name="lowGap"/>: its low skin percentile's height
        /// over the ground, m, negative = in the ground): the lift that puts it back on the ground when it is sunk deeper
        /// than <see cref="Tolerance"/>, at most <paramref name="maxLift"/>; else 0.
        /// </summary>
        public static float CorpseLift(float lowGap, float maxLift = 2f) =>
            float.IsNaN(lowGap) || lowGap >= -Tolerance ? 0f : Math.Min(maxLift, -lowGap);

        /// <summary>
        /// Rate limit for the safety net's log lines: true when a line may be written at <paramref name="now"/> (s), at most
        /// <paramref name="burst"/> lines per <paramref name="window"/> seconds. <paramref name="count"/> and
        /// <paramref name="windowStart"/> are the caller's state.
        /// </summary>
        public static bool MayLog(float now, ref int count, ref float windowStart, int burst = 8, float window = 10f)
        {
            if (now - windowStart >= window || now < windowStart) { windowStart = now; count = 0; }
            if (count >= burst) return false;
            count++;
            return true;
        }
    }
}
