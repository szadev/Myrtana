using System;

namespace Myrtana.Rules
{
    /// <summary>
    /// NPC footwork geometry and speeds in a fight (pure; Unity's left-handed frame: x right, z forward, y up).
    /// <list type="bullet">
    /// <item>ZS_Attack_TurnLeft/TurnRight (CP, Script_Game 0x10016ab0 / OnAttack_Turn 0x10016e50): <c>sAIGotoInstr</c> around the
    /// current target with walk mode 2 (Run) and direction 3 (Left) / 4 (Right), ended by OnAttack_Turn after 0.6 s state time,
    /// then ZS_Attack_Loop. Here: a run sideways around the target at the current radius, facing it [interp: the engine
    /// goto's path is not known; tangential at a constant radius].</item>
    /// <item>ZS_Attack_Sprint (Move): goto with walk mode 3 (Sprint), direction 1.</item>
    /// <item>gCCharacterMovement_PS on every template (1185 of 1186): TurnSpeedMax 200°/s, TurnAcceleration 1e6 (no ramp),
    /// TurnSpeedModifier 0.5 while sprinting, StrafeSpeedMax 280 cm/s [data: G3Cli items survey gCCharacterMovement_PS].</item>
    /// </list>
    /// </summary>
    public static class CombatFootwork
    {
        /// <summary>NPC turn speed in a fight, degrees per second (TurnSpeedMax, as the hero's).</summary>
        public const float TurnSpeedMax = CombatAlignment.TurnSpeedMax;

        /// <summary>The goto sideways never runs faster than StrafeSpeedMax, m/s [data: 280 cm/s].</summary>
        public const float StrafeSpeedMax = 2.8f;

        /// <summary>Sprint speed when the rig has no sprint motion: the run speed times this [interp].</summary>
        public const float SprintFallbackFactor = 1.4f;

        /// <summary>The turn speed of a fighting NPC (°/s): TurnSpeedMax, × TurnSpeedModifier while sprinting.</summary>
        public static float TurnSpeed(bool sprinting) => TurnSpeedMax * (sprinting ? CombatAlignment.SprintTurnModifier : 1f);

        /// <summary>
        /// The horizontal unit direction to self's left (<paramref name="left"/>) or right while it faces the target:
        /// facing f = (fx, fz) → right = (fz, −fx), left = (−fz, fx). Zero when both stand on one spot.
        /// </summary>
        public static void Side(float selfX, float selfZ, float targetX, float targetZ, bool left, out float dirX, out float dirZ)
        {
            float fx = targetX - selfX, fz = targetZ - selfZ;
            float len = (float)Math.Sqrt(fx * fx + fz * fz);
            if (len < 1e-5f)
            {
                dirX = dirZ = 0;
                return;
            }
            fx /= len;
            fz /= len;
            dirX = left ? -fz : fz;
            dirZ = left ? fx : -fx;
        }

        /// <summary>
        /// One step of the circling run (TurnLeft = to self's left as it faces the target): <paramref name="step"/> metres
        /// sideways, then back onto the circle of the current radius around the target, so the distance holds.
        /// </summary>
        public static void CircleStep(float selfX, float selfZ, float targetX, float targetZ, bool left, float step,
                                      out float x, out float z)
        {
            float rx = selfX - targetX, rz = selfZ - targetZ;
            float radius = (float)Math.Sqrt(rx * rx + rz * rz);
            Side(selfX, selfZ, targetX, targetZ, left, out float sx, out float sz);
            if (radius < 1e-5f || step <= 0)
            {
                x = selfX;
                z = selfZ;
                return;
            }
            float nx = rx + sx * step, nz = rz + sz * step;
            float n = (float)Math.Sqrt(nx * nx + nz * nz);
            x = targetX + nx / n * radius;
            z = targetZ + nz / n * radius;
        }

        /// <summary>The circling run's speed (m/s): the rig's sideways run, at most StrafeSpeedMax; StrafeSpeedMax without one.</summary>
        public static float CircleSpeed(float sideRunSpeed) => sideRunSpeed > 0 ? Math.Min(sideRunSpeed, StrafeSpeedMax) : StrafeSpeedMax;

        /// <summary>The Move (ZS_Attack_Sprint) speed: the rig's sprint, else the run × <see cref="SprintFallbackFactor"/>.</summary>
        public static float SprintSpeed(float sprintClipSpeed, float runSpeed) =>
            sprintClipSpeed > 0 ? sprintClipSpeed : runSpeed * SprintFallbackFactor;
    }
}
