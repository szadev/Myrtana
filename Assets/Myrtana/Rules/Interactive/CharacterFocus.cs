namespace Myrtana.Rules
{
    /// <summary>
    /// How far and at what the hero's normal-mode focus (empty hands, PS_Normal) looks for characters to talk to, rob or loot.
    /// G3 (PC_Hero gCFocus_PS, CP 1.75) [data]: NormalModeCharacterScanRange 500 cm (items 300, interactive objects 350).
    /// A character's sight point is the centre of its entity's world box raised by a quarter of its height
    /// (<see cref="FocusSight.Point(System.Numerics.Vector3, System.Numerics.Vector3)"/>): for an NPC lying in its bed
    /// or on the ground that box is the lying body, not the standing man at its routine slot beside the bed. fix-247 (owner:
    /// a sleeper in his bed could not be talked to): ours measured the slot (the view's root, which stays beside the bed
    /// while the animation lays the body in it) and only within 3.5 m, so from the bed's end the sleeper was out of reach;
    /// now a lying NPC is also measured at its body (<see cref="AimsAtBody"/>) and the reach is G3's 5 m.
    /// </summary>
    public static class CharacterFocus
    {
        /// <summary>NormalModeCharacterScanRange of PC_Hero, metres [data 500 cm].</summary>
        public const float ScanRangeM = 5f;

        /// <summary>
        /// The body point is tested as well as the root (whichever the hero faces more directly wins): an NPC that lies —
        /// asleep in its bed or on the ground, or held by the sleep spell (G3 AIMode 7) — has its visible body away from
        /// its root.
        /// </summary>
        public static bool AimsAtBody(bool lying) => lying;

        /// <summary>The lift of a lying body's sight point over its spine bone, metres (as the ragdoll pelvis test) [interp].</summary>
        public const float BodyPointLiftM = 0.2f;
    }
}
