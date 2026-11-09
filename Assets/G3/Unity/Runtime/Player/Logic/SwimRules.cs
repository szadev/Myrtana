using System;

namespace G3.Player
{
    /// <summary>How a character stands in water (G3 movement modes: walk with wet feet, WADE 10, SWIM 7).</summary>
    public enum WaterMode
    {
        /// <summary>Not in water (or the water is below the feet).</summary>
        None,
        /// <summary>Walking with the feet in water (deeper than 1 cm): normal locomotion, water footsteps.</summary>
        Shallow,
        /// <summary>Chest-deep water: the Wade motions (arms raised), land speeds.</summary>
        Wade,
        /// <summary>Swimming at the surface: the Swim motions, no weapons, no fighting.</summary>
        Swim,
    }

    /// <summary>
    /// Water rules of the hero (pure; tested in Tools/G3Tests). Values: PC_Hero gCCharacterMovement_PS (SwimDepth 155 cm,
    /// StepHeight 65 cm) and Game.dll gCCharacterMovement_PS::CheckChangedMovementStates / PutToGround (CP 1.75), read
    /// for understanding only; Docs/hero.md "Water and swimming". Depths in metres, positive = below the surface:
    /// body depth = surface − feet, column depth = surface − floor under the character.
    /// </summary>
    public static class SwimRules
    {
        /// <summary>gCCharacterMovement_PS.SwimDepth of the hero: a swimmer's feet hang this far below the surface.</summary>
        public const float HeroSwimDepth = 1.55f;
        /// <summary>
        /// SwimDepth of the world's humans, orcs, goblins and most creatures (gCCharacterMovement_PS of every placed NPC:
        /// 200 cm; the code default 160 cm is always overridden — `G3Cli objects swimdepth`, polish-3).
        /// </summary>
        public const float HumanSwimDepth = 2.00f;
        /// <summary>SwimDepth of golems, demons and ogres (250 cm).</summary>
        public const float BigSwimDepth = 2.50f;
        /// <summary>SwimDepth of trolls, T-rexes (Nemora) and dragons (300 cm).</summary>
        public const float HugeSwimDepth = 3.00f;

        /// <summary>
        /// The SwimDepth of an NPC by its species (the placed NPCs' gCCharacterMovement_PS values, one per species).
        /// Bloodflies have 200 but treat water as solid (<see cref="TreatsWaterAsSolid"/>).
        /// </summary>
        public static float NpcSwimDepth(string species)
        {
            switch (species)
            {
                case "Golem": case "FireGolem": case "IceGolem": case "Demon": case "Ogre": return BigSwimDepth;
                case "Troll": case "Trex": case "Dragon": return HugeSwimDepth;
                default: return HumanSwimDepth;
            }
        }

        /// <summary>
        /// gCCharacterMovement_PS.TreatWaterAsSolid: water counts as floor (FindFloorWaterCeiling), the body moves over the
        /// surface. Set on every placed bloodfly; PS_Normal / OnMovementModeChanged clear it for all other bodies.
        /// </summary>
        public static bool TreatsWaterAsSolid(string species) => species == "Bloodfly";

        /// <summary>
        /// Body depth from which an NPC wades (CheckChangedMovementStates: max(SwimDepth − 0.5, 0.85) m; NPCs lack the hero's
        /// step-height floor). Humans 1.5 m, golems 2 m, trolls 2.5 m. Wading has no AI state of its own (FullStop /
        /// ContinueRoutine): only the stance of the motions changes.
        /// </summary>
        public static float NpcWadeDepth(float npcSwimDepth) => WadeDepth(npcSwimDepth, 0f, useStepHeight: false);

        /// <summary>The NPC wades at this body depth (feet → surface, m).</summary>
        public static bool NpcWades(float bodyDepth, float npcSwimDepth) =>
            !float.IsNaN(bodyDepth) && bodyDepth >= NpcWadeDepth(npcSwimDepth);

        /// <summary>
        /// Stance field of an NPC's locomotion set (<c>&lt;Rig&gt;_&lt;Stance&gt;_&lt;Left&gt;_&lt;Right&gt;_P0_Move</c>): "Wade"
        /// while wading when the rig's library has a wade set for the hands it holds, else "Stand".
        /// </summary>
        public static string NpcLocomotionStance(bool wading, bool hasWadeSet) => wading && hasWadeSet ? "Wade" : "Stand";

        /// <summary>The locomotion set key an NPC plays (the stance goes into the stance field, never the phase).</summary>
        public static string NpcMoveSet(string rig, bool wading, bool hasWadeSet, string leftItem = "None", string rightItem = "None") =>
            $"{rig}_{NpcLocomotionStance(wading, hasWadeSet)}_{leftItem}_{rightItem}_P0_Move";
        /// <summary>Feet deeper than this in water: the floor material becomes water (water footsteps).</summary>
        public const float WetFeetDepth = 0.01f;
        /// <summary>A swimmer keeps swimming until the body depth drops below SwimDepth minus this.</summary>
        public const float SwimHysteresis = 0.10f;
        /// <summary>Wade threshold = SwimDepth minus this (but at least the step height / 85 cm).</summary>
        public const float WadeBelowSwim = 0.50f;
        /// <summary>Lower bound of the wade threshold for characters without the second check (Game.dll constant 85 cm).</summary>
        public const float WadeDepthFloor = 0.85f;
        /// <summary>Burning ends when submerged deeper than this (CP OnTick, Docs/gameplay-logic.md §2.3).</summary>
        public const float QuenchDepth = 0.75f;
        /// <summary>Swim speeds from the motion names: Hero_Swim_…_Walk_N_Fwd/Back_00_…_150, Run_N_Fwd/Back_00_…_400 (cm/s).</summary>
        public const float SwimWalkSpeed = 1.5f, SwimRunSpeed = 4.0f;
        /// <summary>A player falling longer than this into water learns Perk_Acrobatic for free (OnMovementModeChanged).</summary>
        public const float AcrobaticFallSeconds = 7.45f;
        /// <summary>Followers re-check every second whether the swimming hero left the water (ZS_WaitForPlayer_Loop).</summary>
        public const float WaitRecheckSeconds = 1f;
        /// <summary>Enemies wait on the shore while the swimming target is within 10 m, else give up (ZS_Attack_WaitOnShore).</summary>
        public const float WaitOnShoreMetres = 10f;

        /// <summary>
        /// Body depth from which the character wades: max(SwimDepth − 0.5, StepHeight) (G3 checks property set 0x16,
        /// the hero has it), else max(SwimDepth − 0.5, 0.85). Hero: 1.05 m.
        /// </summary>
        public static float WadeDepth(float swimDepth, float stepHeight, bool useStepHeight = true) =>
            Math.Max(swimDepth - WadeBelowSwim, useStepHeight ? stepHeight : WadeDepthFloor);

        /// <summary>
        /// The water mode for this frame (gCCharacterMovement_PS::CheckChangedMovementStates):
        /// <list type="bullet">
        /// <item>falling with the feet under the surface: swim if the water column is at least SwimDepth deep (dive in),
        /// otherwise keep falling (returns <see cref="WaterMode.None"/>);</item>
        /// <item>swim if (column ≥ SwimDepth and body ≥ wade depth) or body ≥ SwimDepth;</item>
        /// <item>a swimmer keeps swimming while body ≥ SwimDepth − 0.1;</item>
        /// <item>wade if body ≥ wade depth; wet feet if body &gt; 1 cm.</item>
        /// </list>
        /// </summary>
        /// <param name="current">Mode of the last frame.</param>
        /// <param name="falling">In the air (not a swimmer).</param>
        /// <param name="bodyDepth">Surface − feet, m (negative = feet above the water).</param>
        /// <param name="columnDepth">Surface − floor, m (+∞ when no floor was found).</param>
        public static WaterMode Classify(WaterMode current, bool falling, float bodyDepth, float columnDepth,
            float swimDepth, float wadeDepth)
        {
            if (float.IsNaN(bodyDepth)) return WaterMode.None;
            if (falling && bodyDepth > 0 && current != WaterMode.Swim)
                return columnDepth >= swimDepth ? WaterMode.Swim : WaterMode.None;
            if ((columnDepth >= swimDepth && bodyDepth >= wadeDepth) || bodyDepth >= swimDepth) return WaterMode.Swim;
            if (current == WaterMode.Swim && bodyDepth >= swimDepth - SwimHysteresis) return WaterMode.Swim;
            if (bodyDepth >= wadeDepth) return WaterMode.Wade;
            if (bodyDepth > WetFeetDepth) return WaterMode.Shallow;
            return WaterMode.None;
        }

        /// <summary>
        /// Where a swimmer's feet are held (PutToGround while swimming: surface − SwimDepth). Where the floor is higher the
        /// character stands on it, and the next frame classifies again (wading).
        /// </summary>
        public static float SwimFeetY(float surfaceY, float swimDepth, float floorY) => Math.Max(surfaceY - swimDepth, floorY);

        /// <summary>
        /// Local swim velocity (x right, z forward) for the movement keys: forward/back at the Swim motion speeds (walk or
        /// run), no sideways swimming and no sprint (no Swim Left/Right or Sprint motion exists; sprint-swimming is a fan
        /// tweak, Docs/gameplay-logic.md §13).
        /// </summary>
        public static (float x, float z) SwimWish(float inputX, float inputY, bool walk)
        {
            float speed = walk ? SwimWalkSpeed : SwimRunSpeed;
            return (0f, Math.Clamp(inputY, -1f, 1f) * speed);
        }

        /// <summary>
        /// Whether a walking NPC may go to a spot: not where it would have to swim (G3: an NPC that enters swimming mode
        /// dies, OnMovementModeChanged → ZS_RagDollDead). Wading is allowed. Column depth as in <see cref="Classify"/>.
        /// </summary>
        public static bool NpcCanWalk(float columnDepth, float npcSwimDepth = HumanSwimDepth) =>
            !(columnDepth >= npcSwimDepth - SwimHysteresis);

        /// <summary>
        /// Whether an NPC at this body / column depth enters G3's SWIM movement mode and so drowns
        /// (OnMovementModeChanged → ZS_RagDollDead; NPCs have no swim state): body ≥ SwimDepth, or body ≥ the NPC wade depth
        /// max(SwimDepth − 50 cm, 85 cm) with a water column ≥ SwimDepth. SwimDepth per species from the placed NPCs' data
        /// (<see cref="NpcSwimDepth"/>: 200/250/300 cm; the 160 cm code default is never used). Bodies that treat water as
        /// solid (bloodflies) never drown. NaN depth (no water) = false.
        /// </summary>
        public static bool NpcDrowns(float bodyDepth, float columnDepth, float npcSwimDepth = HumanSwimDepth) =>
            Classify(WaterMode.None, false, bodyDepth, float.IsNaN(columnDepth) ? bodyDepth : columnDepth,
                     npcSwimDepth, NpcWadeDepth(npcSwimDepth)) == WaterMode.Swim;

        /// <summary>
        /// A follower of a swimming hero waits where it is (OnFollowPlayer → ZS_WaitForPlayer) and follows again when he no
        /// longer swims (ZS_WaitForPlayer_Loop, checked every second).
        /// </summary>
        public static bool FollowerWaits(bool heroSwimming) => heroSwimming;

        /// <summary>ZS_Attack_WaitOnShore: keep waiting while the swimming target is within 10 m, else cancel the attack.</summary>
        public static bool AttackerKeepsWaiting(float distanceMetres) => distanceMetres <= WaitOnShoreMetres;
    }
}
