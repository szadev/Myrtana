using System;
using System.Collections.Generic;

namespace G3.Player
{
    /// <summary>
    /// fix-123 (owner, Demo_2026-10-18f: "carried into the cave ceiling", then a fall through the whole world): pure rules
    /// that keep the hero on the floor he walks on. The collision is two-sided (G3CollisionBuilder: G3 blocks from both
    /// sides), so the top of a cave ceiling is as good a floor for a ray as the cave floor; only the NPC NavMesh is
    /// one-sided there (<see cref="NavFacingRules"/>). Every placement that is not a walk therefore keeps the same floor
    /// level or goes down to the next floor, never up through a ceiling [interp: G3 has no such placements].
    /// </summary>
    public static class GroundGuard
    {
        /// <summary>
        /// A floor this far above the feet at the start of the move still counts as the floor he sank through (a frame
        /// sinks less than the 0.2 m the recovery waits for, so two frames may add up), m.
        /// </summary>
        public const float SunkTolerance = 0.35f;
        /// <summary>The sink recovery looks for floor at least this far above the feet, m.</summary>
        public const float SunkMinDepth = 0.2f;

        /// <summary>
        /// The sink recovery (a move that started above a floor and ended below it) may put the feet back on the floor at
        /// <paramref name="surfaceY"/> only when the feet were on or above it before the move (<paramref name="beforeY"/>):
        /// a ceiling or an overhang above the hero is never "sunk into" — he was always below it.
        /// </summary>
        public static bool ShouldLiftSunk(float surfaceY, float feetY, float beforeY, float surfaceAngle, float slopeLimit) =>
            surfaceY >= feetY + SunkMinDepth && surfaceAngle <= slopeLimit && surfaceY <= beforeY + SunkTolerance;

        /// <summary>A ceiling top the hero stands on lies at least this far above the floor under it (he fits below), m.</summary>
        public const float MinCeilingDrop = 2f;
        /// <summary>The floor under a ceiling top is looked for this far down, m.</summary>
        public const float MaxCeilingDrop = 12f;
        /// <summary>Something above the hero within this many metres: he is under the hill (a cave), not in the open.</summary>
        public const float EnclosedProbe = 80f;

        /// <summary>
        /// Standing on the top of a cave ceiling, inside the rock of the hill: no NavMesh where he stands
        /// (<paramref name="navHere"/>; the NavMesh keeps only the walkable side), something above him
        /// (<paramref name="enclosed"/>: the landscape over the cave), and a NavMesh floor <paramref name="dropToNavFloor"/>
        /// m straight below (the cave floor, at least <see cref="MinCeilingDrop"/> down: a crate or a ledge he climbed is
        /// lower). Null drop: no such floor (no NavMesh in this place at all): never judged.
        /// </summary>
        public static bool OnCeiling(bool navHere, bool enclosed, float? dropToNavFloor) =>
            !navHere && enclosed && dropToNavFloor.HasValue &&
            dropToNavFloor.Value >= MinCeilingDrop && dropToNavFloor.Value <= MaxCeilingDrop;

        /// <summary>A placement may rise at most this much above the feet (a step), m.</summary>
        public const float MaxRise = SlopeRules.StepHeight;
        /// <summary>A placement may drop at most this far below the feet (the next floor down), m.</summary>
        public const float MaxDrop = MaxCeilingDrop;

        public enum SpotTier { SameLevel = 0, Below = 1, Refused = 2 }

        /// <summary>
        /// How good a free spot at (<paramref name="dx"/>, <paramref name="dy"/>, <paramref name="dz"/>) from the feet is:
        /// on the same floor (within a step up or down) first, then a floor below; anything higher than a step is refused
        /// (it lies on or above a ceiling over him).
        /// </summary>
        public static SpotTier Tier(float dy)
        {
            if (dy > MaxRise) return SpotTier.Refused;
            if (dy >= -MaxRise) return SpotTier.SameLevel;
            return dy >= -MaxDrop ? SpotTier.Below : SpotTier.Refused;
        }

        /// <summary>
        /// The best free spot among <paramref name="candidates"/> (offsets from the feet, x y z): the same floor level
        /// first by horizontal distance, then the floor below by drop then distance; -1 when none is allowed.
        /// </summary>
        public static int Best(IReadOnlyList<(float dx, float dy, float dz)> candidates)
        {
            int best = -1;
            SpotTier bestTier = SpotTier.Refused;
            float bestKey = float.MaxValue, bestDist = float.MaxValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                var (dx, dy, dz) = candidates[i];
                var tier = Tier(dy);
                if (tier == SpotTier.Refused) continue;
                float dist = MathF.Sqrt(dx * dx + dz * dz);
                float key = tier == SpotTier.SameLevel ? dist : -dy;
                if (best < 0 || tier < bestTier || tier == bestTier && (key < bestKey - 1e-4f || MathF.Abs(key - bestKey) <= 1e-4f && dist < bestDist))
                {
                    best = i;
                    bestTier = tier;
                    bestKey = key;
                    bestDist = dist;
                }
            }
            return best;
        }

        /// <summary>
        /// Where a hero caught out of the world goes: his last valid standing place when he has one not too far off
        /// (<paramref name="lastSafeDistance"/>, m; null: none since his last teleport), else the lowest surface above him
        /// (the one he fell through, <see cref="WorldCatch.LowestSurfaceAbove"/>), else the last safe place however far,
        /// else nowhere (false).
        /// </summary>
        public static CatchTarget ChooseCatch(float? lastSafeDistance, bool surfaceAbove)
        {
            if (lastSafeDistance.HasValue && lastSafeDistance.Value <= MaxCatchBack) return CatchTarget.LastSafe;
            if (surfaceAbove) return CatchTarget.SurfaceAbove;
            return lastSafeDistance.HasValue ? CatchTarget.LastSafe : CatchTarget.None;
        }

        /// <summary>The last safe place is preferred within this horizontal distance of the fall, m.</summary>
        public const float MaxCatchBack = 150f;

        public enum CatchTarget { None, LastSafe, SurfaceAbove }

        /// <summary>
        /// fix-130 (owner, Demo_2026-10-18f: "climbing these crates throws me onto the roof", a mill whose loft lies under
        /// a low two-sided thatched roof): a move or placement that raises the feet by more than this is checked for a
        /// free path from where he was (m); walking, jumping and stepping up never pass through geometry.
        /// </summary>
        public const float PathCheckRise = 0.02f;

        /// <summary>Whether a rise from <paramref name="fromY"/> to <paramref name="toY"/> has to be checked for a free path.</summary>
        public static bool NeedsPathCheck(float fromY, float toY) => toY - fromY > PathCheckRise;

        /// <summary>
        /// Heights above the feet of the path segments between the old and the new capsule: its middle and the centre of
        /// its head sphere (a ceiling between the old head and the new one, a floor between the old middle and the new).
        /// </summary>
        public static float[] PathHeights(float height, float radius) => new[] { height * 0.5f, Math.Max(height * 0.5f, height - radius) };

        /// <summary>Refused rises in a row (within half a second) before the hero is put on the nearest free floor on his
        /// level or below instead of where he was.</summary>
        public const int MaxRiseRefusals = 4;
    }
}
