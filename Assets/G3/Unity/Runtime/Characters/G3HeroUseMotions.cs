namespace G3.Unity.Characters
{
    /// <summary>
    /// The hero's own interaction motions (Docs/interactive-objects.md): kneeling at a chest, picking a lock, opening a
    /// door, lying down in a bed and working at a station. All are motion keys of the <c>Hero</c> rig
    /// (Docs/formats-animation.md §5); <see cref="Prefixes"/> lists what the hero importer adds to his motion library.
    /// </summary>
    public static class G3HeroUseMotions
    {
        public static readonly string[] Prefixes =
        {
            "Hero_Stand_None_None_P0_Kneel_",
            "Hero_Kneel_None_None_P0_",
            "Hero_Stand_None_None_P0_LockPick_",
            "Hero_Stand_None_None_P0_OpenDoor_",
            "Hero_Stand_None_None_P0_SleepBed_",
            "Hero_SleepBed_None_None_P0_",
            "Hero_Stand_1H_Tool_P0_Anvil_",
            "Hero_Stand_1H_None_P0_GrindStone_",
            "Hero_Stand_None_Tool_P0_Cauldron_",
            "Hero_Stand_None_Potion_P0_Alchemy_",
            "Hero_Stand_None_None_P0_BookStand_",
            "Hero_Stand_None_None_P0_Barbecue_",
            "Hero_Stand_None_None_P0_TiltOrcBoulder_",
            "Hero_TiltOrcBoulder_None_None_P0_",
            "Hero_HoldOrcBoulder_None_None_P0_",
            "Hero_LiftOrcBoulder_None_None_P0_",
            // par-interact: seats, ore veins, treasure spots, water barrels, eating and drinking
            "Hero_Stand_None_None_P0_SitBench_",
            "Hero_Stand_None_None_P0_SitStool_",
            "Hero_Stand_None_None_P0_SitThrone_",
            "Hero_SitBench_None_None_P0_",
            "Hero_SitStool_None_None_P0_",
            "Hero_SitThrone_None_None_P0_",
            "Hero_Stand_2H_None_P0_PickOre_",
            "Hero_Stand_Staff_None_P0_DigGround_",
            "Hero_Stand_None_None_P0_DrinkWaterBarrel_",
            "Hero_Stand_None_Potion_P0_Use_",
            "Hero_Stand_None_Meat_P0_Use_",
            "Hero_Stand_None_Fruit_P0_Use_",
            "Hero_Stand_None_Bread_P0_Use_",
            "Hero_Stand_None_Cup_P0_Use_",
            "Hero_Stand_None_Bowl_P0_Use_",
            "Hero_Stand_Torch_Potion_P0_Use_",
            // par-misc: looping activities (PS_Interact_UseWaterpipe / SawLog / OrcDrum) and the campfire (PS_Interact_Campfire)
            "Hero_Stand_None_None_P0_SitGround_",
            "Hero_SitGround_None_None_P0_",
            "Hero_SitGround_Meat_Tool_P0_",
            "Hero_Stand_None_Tool_P0_SawLog_",
            "Hero_Stand_Tool_Tool_P0_OrcDrum_",
        };

        static G3MotionKey Key(string stance, string left, string right, string action, string phase, string dir = "Fwd") =>
            new G3MotionKey("Hero", action, phase, dir, stance, left, right);

        public static string Kneel => Key("Stand", "None", "None", "Kneel", "Begin").ToString();
        public static string StandUpFromKneel => Key("Kneel", "None", "None", "Stand", "Begin").ToString();
        public static string KneelLockpick(string phase) => Key("Kneel", "None", "None", "Lockpick", phase).ToString();
        public static string StandLockpick(string phase) => Key("Stand", "None", "None", "LockPick", phase).ToString();
        public static string OpenChest(string phase) => Key("Kneel", "None", "None", "OpenChest", phase).ToString();
        public static string OpenDoor(string phase, string dir = "Fwd") => Key("Stand", "None", "None", "OpenDoor", phase, dir).ToString();
        public static string LieDownInBed(string side) => Key("Stand", "None", "None", "SleepBed", "Begin", side).ToString();
        public static string SleepInBed(string side) => Key("SleepBed", "None", "None", "Ambient", "Loop", side).ToString();
        public static string GetUpFromBed(string side) => Key("SleepBed", "None", "None", "Stand", "Begin", side).ToString();

        /// <summary>
        /// Seats (G3 PS_Interact_SitBench/SitStool/SitThrone, ani states SitBench/SitStool/SitThrone): <paramref name="stance"/>
        /// is the pose's stance ("SitBench" …). Sit down, sit (loop), stand up.
        /// </summary>
        public static string SitDown(string stance) => Key("Stand", "None", "None", stance, "Begin").ToString();
        public static string Sitting(string stance) => Key(stance, "None", "None", "Ambient", "Loop").ToString();
        public static string StandUpFrom(string stance) => Key(stance, "None", "None", "Stand", "Begin").ToString();

        /// <summary>
        /// Eating or drinking an item (G3 <c>_AI_UseInventoryItem</c>, action Use): the item's use type is the right-hand
        /// field ("Potion", "Meat", "Fruit", "Bread", "Cup", "Bowl"), the left hand "None" or "Torch".
        /// </summary>
        public static string Consume(string left, string field, string phase) => Key("Stand", left ?? "None", field, "Use", phase).ToString();

        /// <summary>
        /// Orc boulder stages (G3 PS_Interact_Tilt/Hold/LiftOrcBoulder): <paramref name="stance"/> "Stand" → TiltOrcBoulder →
        /// HoldOrcBoulder → LiftOrcBoulder. <see cref="BoulderNext"/> goes one stage up, <see cref="BoulderLoop"/> holds the
        /// stage, <see cref="BoulderLetGo"/> puts the boulder down and stands up.
        /// </summary>
        public static string BoulderNext(string stance, string next) => Key(stance, "None", "None", next, "Begin").ToString();
        public static string BoulderLoop(string stance) => Key(stance, "None", "None", "Ambient", "Loop").ToString();
        public static string BoulderLetGo(string stance) => Key(stance, "None", "None", "Stand", "Begin").ToString();

        // The interacted object's own part of a hero use motion (the _I_ variant: grindstone wheel, chest lid, barbecue spit,
        // water pipe, orc boulder) is Myrtana.Rules.ObjectUseParts (fix-61).

        /// <summary>
        /// The motions of a looping use (G3 PS_Interact_Loop: Begin, the Loop with a 10 % chance of the Ambient variant per
        /// cycle, End when there is one), entered from and left to standing through <see cref="Enter"/>/<see cref="Leave"/>
        /// when the pose sits (the water pipe sits on the ground: SitGround stance, Waterpipe action).
        /// </summary>
        public sealed class LoopUse
        {
            public string Enter, Begin, Loop, Ambient, End, Leave;
        }

        /// <summary>The looping use of an interaction pose (<see cref="G3InteractionMotions"/>), or null when unknown.</summary>
        public static LoopUse Looping(string useType)
        {
            if (!G3InteractionMotions.TryGet(useType, out var pose)) return null;
            var use = new LoopUse
            {
                Begin = Key(pose.Stance, pose.Left, pose.Right, pose.Action, "Begin").ToString(),
                Loop = Key(pose.Stance, pose.Left, pose.Right, pose.Action, "Loop").ToString(),
                Ambient = Key(pose.Stance, pose.Left, pose.Right, pose.Action, "Ambient").ToString(),
                End = Key(pose.Stance, pose.Left, pose.Right, pose.Action, "End").ToString(),
            };
            if (!pose.IsStanding)
            {
                use.Enter = SitDown(pose.Stance);
                use.Leave = StandUpFrom(pose.Stance);
            }
            return use;
        }

        /// <summary>
        /// G3 PS_Interact_Campfire: the hero sits on the ground (SitGround), takes meat and stick (_AI_HoldInventoryItems
        /// It_Meat / It_Stick: <c>Hold_Begin</c>), fries (<c>Fry_Begin</c>, <c>Fry_Loop</c> while the page is open), puts
        /// them away (<c>Hold_End</c>) and gets up. Hero motions <c>Hero_SitGround_Meat_Tool_P0_*</c>.
        /// </summary>
        public static LoopUse Campfire => new LoopUse
        {
            Enter = SitDown("SitGround"),
            Begin = Key("SitGround", "Meat", "Tool", "Hold", "Begin").ToString(),
            Loop = Key("SitGround", "Meat", "Tool", "Fry", "Loop").ToString(),
            Ambient = Key("SitGround", "Meat", "Tool", "Fry", "Begin").ToString(),
            End = Key("SitGround", "Meat", "Tool", "Hold", "End").ToString(),
            Leave = StandUpFrom("SitGround"),
        };

        /// <summary>The items the hero holds at the campfire (left, right).</summary>
        public static readonly (string left, string right) CampfireProps = ("It_Meat", "It_Stick");

        /// <summary>Begin / Loop / End / Ambient keys of a station use (G3 interaction pose of the use type), or null when unknown.</summary>
        public static string[] Station(string useType)
        {
            if (!G3InteractionMotions.TryGet(useType, out var pose) || !pose.IsStanding) return null;
            return new[]
            {
                Key("Stand", pose.Left, pose.Right, pose.Action, "Begin").ToString(),
                Key("Stand", pose.Left, pose.Right, pose.Action, "Loop").ToString(),
                Key("Stand", pose.Left, pose.Right, pose.Action, "End").ToString(),
                Key("Stand", pose.Left, pose.Right, pose.Action, "Ambient").ToString(),
            };
        }
    }
}
