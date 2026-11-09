using System;

namespace G3.Player
{
    /// <summary>
    /// Pure ground rules of the hero controller (no engine types; tested in Tools/G3Tests). Gothic 3 values are the
    /// hero template's gCCharacterMovement_PS (Docs/hero.md "Slopes, steps, sliding"): a floor steeper than
    /// SteepGroundAngleMax (49°) is not walkable — the hero cannot walk onto it and slides down it; a floor counts as
    /// reachable when it lies at most StepHeight (65 cm) above the feet. Vectors are passed as plain floats, y up.
    /// </summary>
    public static class SlopeRules
    {
        /// <summary>gCCharacterMovement_PS.SteepGroundAngleMax of the hero: steeper floor is "slide" mode.</summary>
        public const float WalkableAngleMax = 49f;
        /// <summary>gCCharacterMovement_PS.StepHeight of the hero, metres (65 cm).</summary>
        public const float StepHeight = 0.65f;
        /// <summary>gCCharacterMovement_PS.SlideSpeedMax of the hero, m/s (800 cm/s).</summary>
        public const float SlideSpeedMax = 8f;
        /// <summary>Script_Game OnSlide: the jump key leaves a slide only after this many seconds in it.</summary>
        public const float SlideJumpDelay = 0.6f;

        /// <summary>Angle between a surface normal and world up, degrees (0 = flat floor, 90 = vertical wall).</summary>
        public static float AngleFromUp(float nx, float ny, float nz)
        {
            float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-6f) return 0f;
            float c = Math.Clamp(ny / len, -1f, 1f);
            return MathF.Acos(c) * (180f / MathF.PI);
        }

        /// <summary>Whether a floor of this slope can be stood and walked on.</summary>
        public static bool IsWalkable(float angleDeg, float limitDeg) => angleDeg <= limitDeg;

        /// <summary>What the hero does at a steep face he walks into (<see cref="DecideStep"/>).</summary>
        public enum StepKind
        {
            /// <summary>A wall, a cliff or a face with no walkable floor within step height behind it: he stops.</summary>
            Blocked,
            /// <summary>A step: nothing steep above step height, walkable floor on top; he steps up (as onto a stair).</summary>
            Step,
            /// <summary>
            /// A landscape fold: a thin landscape face sticking out of the ground with walkable floor within step height
            /// behind it. G3 judges the floor only (a vertical ray), so the hero walks on as if it were not there.
            /// </summary>
            PassFold,
        }

        /// <summary>
        /// A landscape face that reaches at most this far (m) above the floor behind it is a fold, not a cliff: the
        /// landscape render mesh has thin near-vertical triangles that stick 0.1-1.2 m out of gentle slopes (fix-48/49,
        /// Nordmar Hammerclan path: 71-88 deg faces on 18-33 deg ground).
        /// </summary>
        public const float FoldHeightMax = 1.25f;

        /// <summary>What the hero's probes found at a steep face he walks into (heights in m relative to the feet).</summary>
        public struct StepProbe
        {
            /// <summary>A floor found by a vertical ray at the landing point past the face (G3's floor test).</summary>
            public bool FloorFound;
            /// <summary>Height of that floor above the feet (negative: below).</summary>
            public float FloorRise;
            /// <summary>Its angle from up, degrees.</summary>
            public float FloorAngle;
            /// <summary>
            /// How much the walkable ground the hero stands on rises by itself up to the landing (its plane; negative
            /// downhill). G3 corrects the height every frame, so the slope does not count against the step height.
            /// </summary>
            public float SlopeRise;
            /// <summary>Highest steep hit of something that is not landscape (walls, rocks, props, NPCs) on the way; negative: none.</summary>
            public float WallTop;
            /// <summary>Highest steep landscape hit on the way; negative: none.</summary>
            public float FoldTop;
            /// <summary>The hero's capsule fits at the landing point.</summary>
            public bool Fits;
            /// <summary>
            /// The landing floor is no landscape and lies under the landscape (a landscape surface above it): inside a hill,
            /// on a cave's ceiling or a cave floor behind the hillside (fix-99). G3's vertical floor ray comes from above and finds the
            /// landscape there, so it is never a fold to walk past.
            /// </summary>
            public bool UnderLandscape;
            /// <summary>
            /// fix-214: the hero's capsule, lifted onto the step, sweeps over it to the landing (straight across the face,
            /// his own colliders and characters aside) without touching anything. Then a non-landscape hit above step
            /// height on the probe rays (a door jamb or a stone arch met by the oblique centre-line ray, a door leaf beside
            /// the way) lies beside his path, not in it: a doorway threshold is a step. A wall or a rock face straight
            /// ahead stops the sweep, so it still blocks.
            /// </summary>
            public bool BodyClear;
        }

        /// <summary>
        /// G3's floor rule at a steep face (gCCharacterMovement_PS: PutToGround / GetMovementModeAtPosition look at the
        /// floor below the predicted position only): the floor past the face must be walkable and within
        /// <paramref name="stepHeight"/> above or below the feet (the slope's own rise not counted), and the hero must fit
        /// there. Something steep that is not landscape (walls, rocks, props, NPCs) reaching above step height blocks;
        /// within it, it is a <see cref="StepKind.Step"/> (stairs, kerbs). A landscape face reaching at most
        /// <paramref name="foldMax"/> above that floor is a <see cref="StepKind.PassFold"/> (G3 has no side collision for
        /// it); a higher one is a cliff.
        /// </summary>
        public static StepKind DecideStep(in StepProbe p, float stepHeight, float limitDeg, float foldMax = FoldHeightMax)
        {
            if (!p.FloorFound || !p.Fits) return StepKind.Blocked;
            float stepUp = p.FloorRise - p.SlopeRise;
            if (stepUp > stepHeight || stepUp < -stepHeight || !IsWalkable(p.FloorAngle, limitDeg)) return StepKind.Blocked;
            float stepTop = stepHeight + MathF.Max(p.SlopeRise, 0f);
            if (p.WallTop > stepTop && !p.BodyClear) return StepKind.Blocked;
            if (p.FoldTop < 0f) return StepKind.Step;
            if (p.UnderLandscape) return StepKind.Blocked; // a landing inside the hill, not behind a fold (fix-99)
            return p.FoldTop - MathF.Max(p.FloorRise, 0f) <= foldMax ? StepKind.PassFold : StepKind.Blocked;
        }

        /// <summary>
        /// Rise (m) of the ground plane with normal (nx, ny, nz) over <paramref name="distance"/> m along the horizontal
        /// unit direction (dx, dz); the plane's slope is capped at <paramref name="limitDeg"/>.
        /// </summary>
        public static float PlaneRise(float nx, float ny, float nz, float dx, float dz, float distance, float limitDeg)
        {
            if (ny <= 1e-4f) return 0f;
            float grade = -(nx * dx + nz * dz) / ny;
            float max = MathF.Tan(limitDeg * (MathF.PI / 180f));
            return Math.Clamp(grade, -max, max) * distance;
        }

        /// <summary>
        /// Where past a steep face the floor is probed (m from the capsule axis along the move): just past the capsule
        /// standing beyond the contact, then a little further (a slanted face may cover the first point).
        /// </summary>
        public static float[] LandingDistances(float contactDist, float radius) =>
            new[] { MathF.Max(contactDist, 0f) + radius + 0.1f, MathF.Max(contactDist, 0f) + radius + 0.35f,
                    MathF.Max(contactDist, 0f) + NearLanding };

        /// <summary>
        /// fix-148: the last landing lies just past the face (the capsule's centre over the step's front edge, its back half
        /// over the floor he comes from): steep open stairs whose tread is shallower than the capsule (Gotha great tower:
        /// 0.57 m deep, the next tread 0.33 m over it) have room for him only there. It is a step only, never a fold pass.
        /// </summary>
        public const float NearLanding = 0.1f;
        /// <summary>Index of the near landing in <see cref="LandingDistances"/>.</summary>
        public const int NearLandingIndex = 2;

        /// <summary>A steep face hit by a horizontal probe ray: its height above the feet, its distance and whether it is landscape.</summary>
        public struct FaceHit
        {
            public float Height, Distance;
            public bool Landscape;
            public FaceHit(float height, float distance, bool landscape) { Height = height; Distance = distance; Landscape = landscape; }
        }

        /// <summary>
        /// fix-148: the highest wall and fold tops (-1: none) among <paramref name="hits"/> on the way to a landing
        /// <paramref name="landing"/> m ahead: faces beyond the landing's centre are not on the way (the next tread of a
        /// stair; whether the capsule fits there is the room test's question). Before, every face within the farthest
        /// landing counted, so the third tread of steep stairs (0.9 m up) blocked the first step.
        /// </summary>
        public static (float wallTop, float foldTop) TopsBefore(FaceHit[] hits, int count, float landing)
        {
            float wall = -1f, fold = -1f;
            for (int i = 0; i < count; i++)
            {
                if (hits[i].Distance > landing) continue;
                if (hits[i].Landscape) fold = MathF.Max(fold, hits[i].Height);
                else wall = MathF.Max(wall, hits[i].Height);
            }
            return (wall, fold);
        }

        /// <summary>
        /// fix-148: how far the step-up moves on (m): the frame's walk (<paramref name="frameMove"/>), at least 0.25 m, at
        /// most to the landing; to the near landing (<paramref name="near"/>) all the way, so the centre ends over the step.
        /// </summary>
        public static float StepOnward(float frameMove, float toLanding, bool near) =>
            near ? MathF.Max(toLanding, 0.05f) : Math.Clamp(frameMove, 0.25f, MathF.Max(0.25f, toLanding));

        /// <summary>
        /// The hero wanted to walk (<paramref name="wishSpeed"/> m/s) but the controller moved him less than a third of
        /// that in <paramref name="dt"/>: he is held by something.
        /// </summary>
        public static bool Stalled(float wishSpeed, float moved, float dt) =>
            wishSpeed > 0.5f && dt > 0f && moved < wishSpeed * dt * 0.35f;

        /// <summary>
        /// A step-up (controller moved up by <paramref name="lift"/>, on, down) counts when the lift reached the landing
        /// floor (<paramref name="lifted"/> &gt;= <paramref name="landingRise"/>; a ceiling stops it), the move on made at
        /// least 10 cm (a wall above the step stops it), the down move ended on the ground and the hero rose no more than
        /// the lift. Otherwise it is undone.
        /// </summary>
        public static bool StepUpLanded(float lifted, float landingRise, float onward, bool grounded, float gained, float lift) =>
            lifted >= landingRise - 0.02f && onward >= 0.1f && grounded && gained <= lift + 0.01f;

        /// <summary>
        /// Removes the part of a horizontal move (wx, wz) that pushes into a surface whose horizontal normal is
        /// (nx, nz) (pointing out of the surface, need not be normalised). Moves away from or along it are kept.
        /// </summary>
        public static (float x, float z) BlockInto(float wx, float wz, float nx, float nz)
        {
            float len = MathF.Sqrt(nx * nx + nz * nz);
            if (len < 1e-5f) return (wx, wz);
            nx /= len; nz /= len;
            float d = wx * nx + wz * nz;
            if (d >= 0f) return (wx, wz);
            return (wx - d * nx, wz - d * nz);
        }

        /// <summary>Slide speed after <paramref name="dt"/>: accelerates with accel·g·sin(slope), capped at max.</summary>
        public static float SlideSpeed(float speed, float slopeDeg, float gravity, float accel, float max, float dt)
        {
            float sin = MathF.Sin(slopeDeg * (MathF.PI / 180f));
            return MathF.Min(speed + gravity * accel * sin * dt, max);
        }

        /// <summary>
        /// Slide state with hysteresis. Starts after <paramref name="enterDelay"/> s grounded on floor steeper than the
        /// limit (no walkable contact); ends after <paramref name="exitDelay"/> s on floor at least
        /// <paramref name="exitMargin"/>° flatter than the limit, or after <paramref name="airExit"/> s in the air.
        /// </summary>
        public struct SlideState
        {
            public bool Sliding;
            public float SteepTime, FlatTime, Time;

            public bool Update(bool grounded, float groundAngle, float airTime, float limit, float dt,
                float enterDelay = 0.15f, float exitDelay = 0.2f, float exitMargin = 3f, float airExit = 0.4f)
            {
                bool steep = grounded && groundAngle > limit;
                bool flat = grounded && groundAngle < limit - exitMargin;
                SteepTime = steep ? SteepTime + dt : 0f;
                FlatTime = flat || (!grounded && airTime > airExit) ? FlatTime + dt : 0f;
                if (!Sliding && SteepTime > enterDelay) { Sliding = true; Time = 0f; }
                else if (Sliding && FlatTime > exitDelay) Sliding = false;
                if (Sliding) Time += dt;
                return Sliding;
            }

            /// <summary>G3 lets the jump key end a slide only after <see cref="SlideJumpDelay"/> s.</summary>
            public bool CanJump => Sliding && Time > SlideJumpDelay;
        }
    }
}
