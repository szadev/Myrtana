using System;

namespace Myrtana.Rules
{
    /// <summary>
    /// How far and where a combat motion carries the body (Docs/combat.md §5.6). Plain geometry on the ground plane:
    /// metres, x east / z north, yaw in degrees clockwise from +z (Unity's eulerAngles.y).
    /// <para>
    /// G3 [Game.dll gCScriptProcessingUnit::sAICombatMoveStart + gCCharacterMovement_PS]: every combat motion that a
    /// script plays as a combat move (attacks, jump back, parade, stumbles, the knock-down/knock-out falls, pierced
    /// death) reads the last number of its motion name (word 13, e.g. <c>…_Attack_Hit_…_P1_118_R</c>) as a
    /// <b>distance in cm</b>. At the motion's start the engine sets a constant horizontal velocity = distance / (motion
    /// time / speed factor) along one direction fixed for the whole motion:
    /// towards the move's other entity (the attack target) for the attack actions (Attack, PowerAttack, QuickAttack/R/L,
    /// SimpleWhirl, SprintAttack, WhirlAttack, PierceAttack, JumpAttack, RamAttack, HackAttack, FinishingAttack,
    /// GetUpAttack), away from it for every other action (jump back: from the target; stumbles and falls: from the
    /// hitter). Without another entity: the body's forward for attacks, its back for the rest (the player in first
    /// person: the camera's direction, not ported). Over each motion the body also turns at a constant rate (angle /
    /// motion time) to face the other entity (QuickStumble: no turn).
    /// The movement runs only while that motion plays: Raise and Hit are separate combat moves (Raise motions carry 0),
    /// the Recover is played after the move ended, with no combat movement (Recover motions carry 0 too). It stops for
    /// good (until the motion ends) once the body touches the target's body (attacks only, OnTouch → contact), and for a
    /// frame when the point 0.15 s ahead is not walkable ground (ledges); walls stop it as any movement. There is no
    /// other cap: the full distance is travelled when nothing is touched. The FinishingAttack Hit is the one exception:
    /// it travels (2D distance to the target's origin − the number), see <see cref="FinishingPlacement"/>.
    /// The motion's own pelvis travel plays on top (bone tracks).
    /// </para>
    /// </summary>
    public static class CombatLunge
    {
        /// <summary>G3 tests the position this many seconds ahead for walkable ground before moving.</summary>
        public const float LookAheadSeconds = 0.15f;
        /// <summary>A look-ahead point is ground when its height is within this of the body's, plus the slope allowance [interp].</summary>
        public const float GroundStep = 0.6f;
        /// <summary>Height the ground may rise or fall per metre of look-ahead (tan 50°: G3's walkable 49° slopes) [interp].</summary>
        public const float GroundSlope = 1.19f;

        /// <summary>The G3 actions whose combat movement goes towards the target (attacks); all others go away from it.</summary>
        public static bool GoesTowards(string g3Action)
        {
            switch (g3Action)
            {
                case "Attack":
                case "PowerAttack":
                case "QuickAttack":
                case "QuickAttackR":
                case "QuickAttackL":
                case "SimpleWhirl":
                case "SprintAttack":
                case "WhirlAttack":
                case "TwirlAttack": // our fallback for whirls; no gEAction of its own
                case "PierceAttack":
                case "JumpAttack":
                case "RamAttack":
                case "HackAttack":
                case "FinishingAttack":
                case "GetUpAttack":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The movement of one combat motion. <paramref name="nameDistance"/>: the motion name's number in metres (cm / 100);
        /// <paramref name="stage"/>: the motion's phase word ("Raise", "Hit", "Recover", "Begin" …); (x, z, yaw): the body;
        /// <paramref name="hasOther"/> and (ox, oz): the target of a move or the hitter of a reaction;
        /// <paramref name="turn"/>: the body turns to face the other over the motion (the caller's choice: moves only).
        /// </summary>
        public static LungePlan Plan(string g3Action, string stage, float nameDistance, float x, float z, float yaw,
                                     bool hasOther, float ox, float oz, bool turn)
        {
            if (stage == "Recover") return LungePlan.None(yaw); // played after the combat move ended: no movement
            bool towards = GoesTowards(g3Action);
            float dx, dz, otherDist = 0;
            if (hasOther && Unit(ox - x, oz - z, out dx, out dz, out otherDist)) { }
            else
            {
                hasOther = false;
                double r = yaw * Math.PI / 180.0;
                dx = (float)Math.Sin(r);
                dz = (float)Math.Cos(r);
            }
            // The facing the body turns to: the other entity (or no turn without one).
            float face = hasOther ? FinishingPlacement.YawTowards(x, z, ox, oz, yaw) : yaw;
            bool turns = turn && hasOther && g3Action != "QuickStumble";
            float distance = nameDistance;
            if (g3Action == "FinishingAttack" && stage == "Hit")
            {
                // G3: the Hit ends that far from the target's origin (backwards when closer); nothing without a target.
                distance = hasOther ? otherDist - nameDistance : 0f;
                if (distance < 0) { distance = -distance; towards = !towards; }
            }
            if (distance < 0) { distance = -distance; towards = !towards; }
            if (!towards) { dx = -dx; dz = -dz; }
            return new LungePlan(dx, dz, distance, face, turns, stopsAtContact: towards && hasOther && GoesTowards(g3Action), towards);
        }

        /// <summary>
        /// How much of a <paramref name="step"/> along (dx, dz) from (x, z) stays outside the circle of radius
        /// <paramref name="radius"/> around (cx, cz) (two bodies' footprints): the step, shortened to the contact point.
        /// A body already inside may only move away (0 when the step goes further in).
        /// </summary>
        public static float Allowed(float x, float z, float dx, float dz, float step, float cx, float cz, float radius)
        {
            if (step <= 0) return 0;
            float px = cx - x, pz = cz - z;
            float b = px * dx + pz * dz;          // distance along the step to the closest approach
            float c = px * px + pz * pz - radius * radius;
            if (c <= 0) return b > 1e-5f ? 0f : step; // inside: only away
            if (b <= 0) return step;                 // moving away
            float disc = b * b - c;
            if (disc < 0) return step;               // passes by
            float t = b - (float)Math.Sqrt(disc);
            return t >= step ? step : t < 0 ? 0 : t;
        }

        /// <summary>G3's ledge test: the ground under the look-ahead point is walkable from the body's height.</summary>
        public static bool GroundAhead(float bodyY, bool hasGround, float groundY, float lookAhead) =>
            hasGround && Math.Abs(groundY - bodyY) <= GroundStep + GroundSlope * Math.Abs(lookAhead);

        /// <summary>Distance (m) the look-ahead point lies ahead for a <paramref name="step"/> taken in <paramref name="dt"/> seconds.</summary>
        public static float LookAhead(float step, float dt) => dt > 1e-5f ? step / dt * LookAheadSeconds : 0f;

        /// <summary>Yaw at <paramref name="progress"/> (0..1) of a motion that turns from <paramref name="from"/> to <paramref name="to"/> at a constant rate.</summary>
        public static float TurnYaw(float from, float to, float progress) => FinishingPlacement.TurnYaw(from, to, progress);

        static bool Unit(float dx, float dz, out float ux, out float uz, out float len)
        {
            len = (float)Math.Sqrt(dx * dx + dz * dz);
            if (len < 1e-3f) { ux = uz = 0; return false; }
            ux = dx / len;
            uz = dz / len;
            return true;
        }
    }

    /// <summary>The combat movement of one motion: direction (unit), distance (m, ≥ 0), the yaw to face, and its stops.</summary>
    public readonly struct LungePlan
    {
        public LungePlan(float dirX, float dirZ, float distance, float faceYaw, bool turns, bool stopsAtContact, bool towards)
        {
            DirX = dirX;
            DirZ = dirZ;
            Distance = distance;
            FaceYaw = faceYaw;
            Turns = turns;
            StopsAtContact = stopsAtContact;
            Towards = towards;
        }

        public static LungePlan None(float yaw) => new LungePlan(0, 0, 0, yaw, false, false, false);

        public float DirX { get; }
        public float DirZ { get; }
        /// <summary>Metres travelled over the whole motion at a constant speed (when nothing stops the body).</summary>
        public float Distance { get; }
        public float FaceYaw { get; }
        /// <summary>The body turns from its yaw at the motion's start to <see cref="FaceYaw"/> by the motion's end.</summary>
        public bool Turns { get; }
        /// <summary>An attack on a target: the movement ends for good when the bodies touch.</summary>
        public bool StopsAtContact { get; }
        /// <summary>Towards the other entity (or forward); false: away from it (or backward).</summary>
        public bool Towards { get; }
        /// <summary>Metres travelled by <paramref name="progress"/> (0..1) of the motion: linear (G3's constant velocity).</summary>
        public float At(float progress) => Distance * (progress <= 0 ? 0 : progress >= 1 ? 1 : progress);
    }
}
