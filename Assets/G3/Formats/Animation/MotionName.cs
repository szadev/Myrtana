using System;

namespace G3.Formats.Animation
{
    /// <summary>
    /// The structured motion file name the engine composes from the NPC's animation state (Docs/formats-animation.md §5):
    /// <c>Rig_Stance_LeftItem_RightItem_Pose_Action_Phase_Variant_Direction_Index_%_Weight_Pose2_Speed[_Suffix]</c>,
    /// e.g. <c>Hero_Stand_None_None_P0_Move_Walk_N_Fwd_00_%_00_P0_160</c>.
    /// </summary>
    public sealed class MotionName
    {
        public string Rig, Stance, LeftItem, RightItem, Pose, Action, Phase, Variant, Direction, Pose2, Suffix;
        public int Index, Weight;
        /// <summary>Root speed in cm/s encoded in the name (0 for in-place clips).</summary>
        public int Speed;

        public static MotionName Parse(string fileName)
        {
            if (fileName == null) return null;
            string s = fileName;
            int dot = s.LastIndexOf('.');
            if (dot > 0 && s.Substring(dot).Equals(".xmot", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, dot);
            var p = s.Split('_');
            if (p.Length < 14 || p[10] != "%") return null;
            if (!int.TryParse(p[9], out int idx) || !int.TryParse(p[11], out int w) || !int.TryParse(p[13], out int speed)) return null;
            return new MotionName
            {
                Rig = p[0], Stance = p[1], LeftItem = p[2], RightItem = p[3], Pose = p[4], Action = p[5], Phase = p[6],
                Variant = p[7], Direction = p[8], Index = idx, Weight = w, Pose2 = p[12], Speed = speed,
                Suffix = p.Length > 14 ? string.Join("_", p, 14, p.Length - 14) : null,
            };
        }

        public override string ToString() =>
            $"{Rig}_{Stance}_{LeftItem}_{RightItem}_{Pose}_{Action}_{Phase}_{Variant}_{Direction}_{Index:00}_%_{Weight:00}_{Pose2}_{Speed}" +
            (Suffix != null ? "_" + Suffix : "");
    }
}
