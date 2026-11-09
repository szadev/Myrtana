using System;

namespace Myrtana.Rules
{
    /// <summary>
    /// Where the attacker of a finishing move stands so that its blade meets the lying victim (Docs/combat.md §5.5,
    /// "Placement"). Plain geometry on the ground plane: metres, x east / z north, yaw in degrees clockwise from +z
    /// (Unity's eulerAngles.y).
    /// <para>
    /// G3 [gCScriptProcessingUnit::sAICombatMoveStart]: the number at the end of a combat motion's name is a distance
    /// (cm) the body travels over that motion, towards the target, at a constant speed (distance / motion time); the body
    /// also turns to face the target over each phase. The FinishingAttack Hit is the one exception: there the body travels
    /// (2D distance to the target − the name's distance), so the Hit ends with the attacker that far from the victim's
    /// origin (Hero 1H 200, 2H 100, Staff 50; Orc 2H/Staff 50). The motions' own pelvis travel (1H: 1.2 m forward)
    /// plays on top. Measured offline (G3Cli anim reach) the contact point at the stab frame lies, in front of the
    /// attacker's origin: Hero 1H blade tip 1.95 m (end of the Hit), 2H blade on the ground 0.53–1.87 m, Staff butt
    /// 0.2–0.6 m, Orc Staff butt 0.6 m, Orc 2H axe/sword head 1.85–2.1 m. So G3's distances agree except the orc 2H.
    /// </para>
    /// <para>
    /// We aim at the victim's torso (pelvis–chest midpoint of its lying pose: from 0 to 0.75 m off its origin, depending
    /// on the down stance) instead of its origin, use the measured reach, end the slide at the stab frame instead of the
    /// Hit's end, and, when the spot is blocked, approach from another side around the body [interp].
    /// </para>
    /// </summary>
    public static class FinishingPlacement
    {
        /// <summary>Approach directions tried around the victim when the straight one is blocked (degrees from it).</summary>
        static readonly float[] Detours = { 0f, 30f, -30f, 60f, -60f, 90f, -90f, 120f, -120f, 150f, -150f, 180f };

        /// <summary>
        /// The finishing motion set's reach: null when the rig has no finishing motion for the hand field (fists, other
        /// rigs). <paramref name="rig"/> is the motion library's rig ("Hero" for humans, "Orc"); <paramref name="handField"/>
        /// the right-hand field ("1H", "2H", "Staff").
        /// </summary>
        public static FinishingReach? ReachOf(string rig, string handField)
        {
            bool orc = string.Equals(rig, "Orc", StringComparison.OrdinalIgnoreCase);
            switch (handField)
            {
                case "1H": return orc ? (FinishingReach?)null : new FinishingReach(1.95f, 2.00f, 0.95f);
                case "2H": return orc ? new FinishingReach(2.00f, 0.50f, 0.88f) : new FinishingReach(1.00f, 1.00f, 0.95f);
                case "Staff": return orc ? new FinishingReach(0.55f, 0.50f, 0.67f) : new FinishingReach(0.50f, 0.50f, 0.72f);
                default: return null;
            }
        }

        /// <summary>
        /// Picks the attacker's spot: <paramref name="reach"/> metres from the victim's torso on the attacker's side, facing
        /// it. If <paramref name="free"/> rejects that spot, the approach turns around the torso (±30°, ±60° … 180°); if
        /// every spot is blocked the straight spot is taken anyway (<see cref="FinishingSpot.Forced"/>): G3 slides into
        /// whatever is there and stops, and staying put would put the stab past a body lying closer than the reach.
        /// </summary>
        public static FinishingSpot Choose(float ax, float az, float ayaw, float tx, float tz, float reach, Func<float, float, bool> free) =>
            Pick(ax, az, ayaw, tx, tz, reach, free == null ? (Func<float, float, float>)null : (x, z) => free(x, z) ? 0f : -1f);

        /// <summary>
        /// Highest the attacker's ground may lie above the victim's (m) for the blade to reach the torso: every measured stab
        /// ends 0.15–0.25 m above the attacker's feet and the lying torso is ~0.13 m above the victim's, so an attacker on
        /// higher ground stabs above the body (1.95 m on a 12° slope = 0.4 m) [interp].
        /// </summary>
        public const float MaxAbove = 0.10f;
        /// <summary>Lowest the attacker's ground may lie below the victim's (m): further down the blade ends under the body [interp].</summary>
        public const float MaxBelow = 0.30f;

        /// <summary>How far (m) the attacker's ground (<paramref name="dy"/> = attacker − victim) lies outside the level band.</summary>
        public static float HeightMiss(float dy) => dy > MaxAbove ? dy - MaxAbove : dy < -MaxBelow ? -MaxBelow - dy : 0f;

        /// <summary>
        /// <see cref="Choose"/> on uneven ground: <paramref name="groundAt"/> gives a free spot's ground height (null: blocked).
        /// The first approach (straight, +30°, −30° …) whose ground is level with the victim's (<see cref="HeightMiss"/> 0)
        /// wins; when none is, the free one with the smallest miss (earlier on a tie); none free → the straight spot (forced). G3 places
        /// by distance only and so stabs above a body lying downhill [interp].
        /// </summary>
        public static FinishingSpot ChooseLevel(float ax, float az, float ayaw, float tx, float tz, float reach,
                                                Func<float, float, float?> groundAt, float victimGroundY) =>
            Pick(ax, az, ayaw, tx, tz, reach, groundAt == null ? (Func<float, float, float>)null : (x, z) =>
            {
                var g = groundAt(x, z);
                return g == null ? -1f : HeightMiss(g.Value - victimGroundY);
            });

        /// <summary>Tries the approaches in order; <paramref name="miss"/>: &lt;0 blocked, 0 good (taken at once), &gt;0 usable.</summary>
        static FinishingSpot Pick(float ax, float az, float ayaw, float tx, float tz, float reach, Func<float, float, float> miss)
        {
            // Direction from the torso towards the attacker; the attacker's back direction when they coincide.
            float dx = ax - tx, dz = az - tz;
            float len = (float)Math.Sqrt(dx * dx + dz * dz);
            if (len < 1e-3f)
            {
                double r = ayaw * Math.PI / 180.0;
                dx = -(float)Math.Sin(r);
                dz = -(float)Math.Cos(r);
            }
            else
            {
                dx /= len;
                dz /= len;
            }
            FinishingSpot? best = null;
            float bestMiss = float.MaxValue;
            foreach (float detour in Detours)
            {
                double r = detour * Math.PI / 180.0;
                float c = (float)Math.Cos(r), s = (float)Math.Sin(r);
                float ux = dx * c + dz * s, uz = -dx * s + dz * c; // rotated clockwise (seen from above) by detour
                float x = tx + ux * reach, z = tz + uz * reach;
                float m = miss == null ? 0f : miss(x, z);
                if (m < 0f || m >= bestMiss) continue;
                var spot = new FinishingSpot(x, z, YawTowards(x, z, tx, tz, ayaw), ux, uz, detour, moved: true);
                if (m == 0f) return spot;
                best = spot;
                bestMiss = m;
            }
            if (best != null) return best.Value;
            // Every spot blocked: slide straight anyway; the mover stops at whatever is really in the way (combat-6: the
            // old "stay and turn" left a body closer than the reach under the hero's feet, the blade past it).
            float fx = tx + dx * reach, fz = tz + dz * reach;
            return new FinishingSpot(fx, fz, YawTowards(fx, fz, tx, tz, ayaw), dx, dz, 0f, moved: true, forced: true);
        }

        /// <summary>The spot again for a torso that moved (the body still settling): same approach direction.</summary>
        public static FinishingSpot Follow(FinishingSpot spot, float tx, float tz, float reach)
        {
            if (!spot.Moved) return new FinishingSpot(spot.X, spot.Z, YawTowards(spot.X, spot.Z, tx, tz, spot.Yaw), spot.DirX, spot.DirZ, 0f, false);
            float x = tx + spot.DirX * reach, z = tz + spot.DirZ * reach;
            return new FinishingSpot(x, z, YawTowards(x, z, tx, tz, spot.Yaw), spot.DirX, spot.DirZ, spot.Detour, true, spot.Forced);
        }

        /// <summary>
        /// How far along its slide the body is at <paramref name="progress"/> (0..1) of the Hit motion: linear (G3's constant
        /// speed), arriving at the stab frame (<see cref="FinishingReach.StabFraction"/>) and staying there.
        /// </summary>
        public static float SlideFraction(float progress, float stabFraction)
        {
            if (stabFraction <= 0f) return 1f;
            float f = progress / stabFraction;
            return f <= 0f ? 0f : f >= 1f ? 1f : f;
        }

        /// <summary>Point on the slide from (sx, sz) to the spot at <paramref name="fraction"/>.</summary>
        public static (float x, float z) Along(float sx, float sz, FinishingSpot spot, float fraction) =>
            (sx + (spot.X - sx) * fraction, sz + (spot.Z - sz) * fraction);

        /// <summary>Yaw turned from <paramref name="from"/> towards <paramref name="to"/> by <paramref name="fraction"/>, the short way round.</summary>
        public static float TurnYaw(float from, float to, float fraction)
        {
            float f = fraction <= 0f ? 0f : fraction >= 1f ? 1f : fraction;
            float y = from + CombatFocus.DeltaAngle(from, to) * f;
            y %= 360f;
            return y < 0f ? y + 360f : y;
        }

        /// <summary>Yaw facing from (x, z) to (tx, tz); <paramref name="fallback"/> when they coincide.</summary>
        public static float YawTowards(float x, float z, float tx, float tz, float fallback)
        {
            float dx = tx - x, dz = tz - z;
            if (dx * dx + dz * dz < 1e-6f) return fallback;
            float y = (float)(Math.Atan2(dx, dz) * 180.0 / Math.PI);
            return y < 0f ? y + 360f : y;
        }
    }

    /// <summary>One finishing motion set's geometry.</summary>
    public readonly struct FinishingReach
    {
        public FinishingReach(float strike, float g3Distance, float stabFraction)
        {
            Strike = strike;
            G3Distance = g3Distance;
            StabFraction = stabFraction;
        }

        /// <summary>Horizontal distance (m) from the attacker's origin to the blade's contact point at the stab frame (measured).</summary>
        public float Strike { get; }
        /// <summary>G3's distance (m) from the victim's origin at the end of the Hit (the motion name's number) [data].</summary>
        public float G3Distance { get; }
        /// <summary>The stab frame as a fraction of the Hit motion (measured: lowest blade point).</summary>
        public float StabFraction { get; }
    }

    /// <summary>The attacker's spot for a finishing move: position, yaw and the approach direction (unit, torso → attacker).</summary>
    public readonly struct FinishingSpot
    {
        public FinishingSpot(float x, float z, float yaw, float dirX, float dirZ, float detour, bool moved, bool forced = false)
        {
            Forced = forced;
            X = x;
            Z = z;
            Yaw = yaw;
            DirX = dirX;
            DirZ = dirZ;
            Detour = detour;
            Moved = moved;
        }

        public float X { get; }
        public float Z { get; }
        public float Yaw { get; }
        public float DirX { get; }
        public float DirZ { get; }
        /// <summary>Degrees the approach turned around the victim to find a free spot (0: straight).</summary>
        public float Detour { get; }
        /// <summary>False: the attacker stays where it is and only turns (a spot built that way by <see cref="FinishingPlacement.Follow"/>).</summary>
        public bool Moved { get; }
        /// <summary>Every approach was blocked: the straight spot was taken anyway (the slide stops at the obstacle).</summary>
        public bool Forced { get; }
    }
}
