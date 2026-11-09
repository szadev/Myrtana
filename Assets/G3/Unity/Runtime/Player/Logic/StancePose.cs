using System;

namespace G3.Player
{
    /// <summary>
    /// Pose field (P0…P3) of the hero's locomotion set in a stance. G3 motion names end with the pose the motion leaves
    /// the body in; with a shield every walk and run ends in P1 (<c>Hero_Stand_Shield_1H_P0_Move_Run_N_Fwd_…_P1_400</c>),
    /// so a moving shield-bearer stands in the P1 guard (shield in front, <c>Hero_Stand_Shield_1H_P1_Ambient_Loop</c>). The
    /// P0 stand pose is the plain 1H fencing pose with the left hand behind the back, which carries the shield flat
    /// across the back. The shield parade exists only in P1 (G3 IsInParadePose: <c>Hero_Parade_Shield_1H_P1_Move_…</c>).
    /// Jumps and falls keep P0 (no P1 falls). Other hands keep <paramref name="pose"/>.
    /// </summary>
    public static class StancePose
    {
        public const string Shield = "Shield", ShieldPose = "P1";

        public static string For(string stance, string leftItem, string pose)
        {
            if (!string.Equals(leftItem, Shield, StringComparison.OrdinalIgnoreCase)) return pose;
            return string.Equals(stance, "Stand", StringComparison.OrdinalIgnoreCase) || string.Equals(stance, "Parade", StringComparison.OrdinalIgnoreCase)
                ? ShieldPose : pose;
        }

        /// <summary>
        /// fix-58: the pose whose Move motions stand in for the gaits the stance's own pose set lacks, or null. The shield
        /// guard set <c>Hero_Stand_Shield_1H_P1_Move_…</c> has Walk and Run but no Sprint; G3 names its motions with the
        /// requested pose P0 (<c>gCNPC_PS::GetPrimaryPoseExt</c> answers P0 for moves), and the P0 set carries
        /// <c>Sprint_N_Fwd_…_P1_500</c> and <c>Sprint_N_Left/Right_…_P1_400</c>, which end in the same P1 guard. Without
        /// the fallback a drawn shield sprinted at the unarmed 6 m/s (runner-31 drawn_gaits step 45).
        /// </summary>
        public static string MoveFallbackPose(string stance, string leftItem, string pose)
        {
            if (!string.Equals(stance, "Stand", StringComparison.OrdinalIgnoreCase)) return null;
            return For(stance, leftItem, pose) != pose ? pose : null;
        }
    }
}
