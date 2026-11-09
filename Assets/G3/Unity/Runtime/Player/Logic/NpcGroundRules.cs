using System;
using System.Collections.Generic;

namespace G3.Player
{
    /// <summary>
    /// Pure rules for the floor an NPC body is put on (no engine types; tested in Tools/G3Tests), fix-108. G3
    /// (gCCharacterMovement_PS, Docs/hero.md "Slopes, steps, sliding") puts a body on the floor of a vertical ray under its
    /// origin (PutToGround), corrects the height by at most StepHeight per move and lets it slide down floor steeper than
    /// SteepGroundAngleMax. Our views put bodies at heights that come from elsewhere (a simulated pose whose height is the
    /// spawn's, a NavMesh point, a G3 spawn under the landscape): a single ray from a fixed height above that reference
    /// misses the floor when the reference is below it (a deer's wander point 8 m up a 60° slope kept the spawn's height)
    /// and finds a cave roof's top when it starts too high (fix-99). These rules pick the body's own floor from every
    /// surface of the vertical column instead:
    /// 1. a floor at the NavMesh's height near the body (the NavMesh is one-sided since fix-99: none on cave roof backs);
    /// 2. else the surface nearest the reference within a step below and a slope-dependent reach above it
    ///    (<see cref="UpReach"/>: the step height plus the rise of the floor's slope over the body's half length, so a long
    ///    animal on a 40° slope still finds the ground under its middle, while a flat roof top 2 m up does not count);
    /// 3. else the first surface below (fix-60: a body put in the air above a tower top or a street comes down);
    /// 4. else, with nothing below at all (the body is under the landscape), the lowest surface above within
    ///    <see cref="BuriedReach"/>.
    /// </summary>
    public static class NpcGroundRules
    {
        /// <summary>Floor steeper than this is a wall face, not something a body stands on (deg).</summary>
        public const float MaxFloorSlope = 75f;
        /// <summary>Highest floor above the reference the slope reach may accept (m).</summary>
        public const float MaxUpReach = 3f;
        /// <summary>A surface this close to the NavMesh's height is the floor the NavMesh lies on (m; Recast detail error on slopes).</summary>
        public const float NavTolerance = 0.6f;
        /// <summary>A body with no surface below it at all comes up onto a surface at most this far above (m).</summary>
        public const float BuriedReach = 2.5f;
        /// <summary>Margin added to the body's half length for the slope reach (the column ray is not exactly under the feet).</summary>
        const float ReachMargin = 0.25f;

        /// <summary>One surface of the vertical column: its height and the slope of its face.</summary>
        public readonly struct Surface
        {
            public readonly float Y;
            public readonly float SlopeDeg;

            public Surface(float y, float slopeDeg)
            {
                Y = y;
                SlopeDeg = slopeDeg;
            }
        }

        /// <summary>
        /// How far above the reference a floor of <paramref name="slopeDeg"/> may lie and still be the body's own floor:
        /// <see cref="SlopeRules.StepHeight"/> plus the slope's rise over the body's half length, at most <see cref="MaxUpReach"/>.
        /// </summary>
        public static float UpReach(float halfLength, float slopeDeg)
        {
            float s = Math.Clamp(slopeDeg, 0f, MaxFloorSlope) * (MathF.PI / 180f);
            float reach = SlopeRules.StepHeight + (Math.Max(0f, halfLength) + ReachMargin) * MathF.Tan(s);
            return Math.Min(MaxUpReach, reach);
        }

        /// <summary>
        /// The floor a body whose feet are at <paramref name="refY"/> stands on, from the column's surfaces (any order).
        /// </summary>
        /// <param name="surfaces">The column's surfaces (faces up), e.g. from a ray down from above the body.</param>
        /// <param name="halfLength">Half the body's length (a long animal) or its radius, m.</param>
        /// <param name="maxDown">Deepest first surface below that still counts (rule 3), m.</param>
        /// <param name="navY">The NavMesh's height near the body, NaN when there is none.</param>
        /// <param name="index">The chosen surface.</param>
        /// <param name="upLimit">Overrides the slope reach above the reference when ≥ 0 (a point whose height is only
        /// a guess, e.g. a wander point at the spawn's height: any surface up to this far above).</param>
        public static bool Pick(float refY, IReadOnlyList<Surface> surfaces, float halfLength, float maxDown, float navY, out int index,
                                float upLimit = -1f)
        {
            index = -1;
            if (surfaces == null || surfaces.Count == 0) return false;
            float Above(Surface s) => upLimit >= 0f ? upLimit : UpReach(halfLength, s.SlopeDeg);

            // 1. the floor the NavMesh lies on (near the body: within the same window as rule 2 or the first one below)
            if (!float.IsNaN(navY) && navY - refY <= MaxUpReach + NavTolerance && refY - navY <= Math.Max(maxDown, SlopeRules.StepHeight))
            {
                float best = float.MaxValue;
                for (int i = 0; i < surfaces.Count; i++)
                {
                    var s = surfaces[i];
                    if (s.SlopeDeg > MaxFloorSlope) continue;
                    float d = Math.Abs(s.Y - navY);
                    if (d <= NavTolerance && d < best) { best = d; index = i; }
                }
                if (index >= 0) return true;
            }

            // 2. nearest to the reference within a step below / the slope reach above
            {
                float best = float.MaxValue;
                for (int i = 0; i < surfaces.Count; i++)
                {
                    var s = surfaces[i];
                    if (s.SlopeDeg > MaxFloorSlope) continue;
                    float dy = s.Y - refY;
                    if (dy < -SlopeRules.StepHeight || dy > Above(s)) continue;
                    float d = Math.Abs(dy);
                    if (d < best) { best = d; index = i; }
                }
                if (index >= 0) return true;
            }

            // 3. the first surface below (however far, up to maxDown)
            {
                float best = float.NegativeInfinity;
                bool anyBelow = false;
                for (int i = 0; i < surfaces.Count; i++)
                {
                    var s = surfaces[i];
                    if (s.Y > refY) continue;
                    anyBelow = true;
                    if (s.SlopeDeg > MaxFloorSlope || refY - s.Y > maxDown) continue;
                    if (s.Y > best) { best = s.Y; index = i; }
                }
                if (index >= 0) return true;
                if (anyBelow) return false;
            }

            // 4. nothing below: under the ground; up onto the lowest surface within reach
            {
                float best = float.PositiveInfinity;
                for (int i = 0; i < surfaces.Count; i++)
                {
                    var s = surfaces[i];
                    if (s.SlopeDeg > MaxFloorSlope || s.Y - refY > BuriedReach) continue;
                    if (s.Y < best) { best = s.Y; index = i; }
                }
                return index >= 0;
            }
        }

        // ------------------------------------------------------------------ sliding (gCCharacterMovement_PS slide mode)

        /// <summary>Gravity, m/s².</summary>
        public const float Gravity = 9.81f;

        /// <summary>
        /// The body stands on floor too steep to stand on (G3 GetMovementModeAtPosition: steeper than SteepGroundAngleMax
        /// is slide mode) and slides down it; faces steeper than <see cref="MaxFloorSlope"/> are walls, not floor.
        /// </summary>
        public static bool Slides(float slopeDeg, float limitDeg = SlopeRules.WalkableAngleMax) => slopeDeg > limitDeg && slopeDeg <= 89f;

        /// <summary>
        /// One slide tick: the speed grows with the slope's pull (g·sin), at most <see cref="SlopeRules.SlideSpeedMax"/>; the
        /// result is the horizontal distance to move downhill this tick (m). On walkable floor the speed is reset and the
        /// result is 0.
        /// </summary>
        public static float SlideStep(ref float speed, float slopeDeg, float dt, float limitDeg = SlopeRules.WalkableAngleMax)
        {
            if (!Slides(slopeDeg, limitDeg) || dt <= 0f)
            {
                speed = 0f;
                return 0f;
            }
            float a = slopeDeg * (MathF.PI / 180f);
            speed = Math.Min(SlopeRules.SlideSpeedMax, speed + Gravity * MathF.Sin(a) * dt);
            return speed * MathF.Cos(a) * dt; // along the slope -> horizontal
        }

        /// <summary>
        /// A walk step that rises onto floor too steep to walk (its face is steeper than <paramref name="limitDeg"/> and the
        /// step itself climbs steeper than that): G3's ProcessControledTranslation stops such a move (no climbing slide faces).
        /// </summary>
        public static bool ClimbsSteep(float rise, float run, float floorSlopeDeg, float limitDeg = SlopeRules.WalkableAngleMax)
        {
            if (rise <= 0.02f || floorSlopeDeg <= limitDeg) return false;
            return rise > Math.Max(run, 1e-4f) * MathF.Tan(limitDeg * (MathF.PI / 180f));
        }
    }
}
