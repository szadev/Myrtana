using System;
using System.Collections.Generic;

namespace G3.Unity.Characters
{
    /// <summary>
    /// How Gothic 3 animates the use of a routine point, by the point's interaction (gEUseType name, Docs/npc-simulation.md):
    /// the animation state (stance, items in the hands, action) whose motion names the engine composes
    /// (Docs/formats-animation.md §5). Standing work plays <c>…_Stand_&lt;L&gt;_&lt;R&gt;_P0_&lt;Action&gt;_{Begin,Loop,End}</c>;
    /// sitting and sleeping enter their stance with <c>…_Stand_None_None_P0_&lt;Stance&gt;_Begin</c>, loop in the stance and
    /// leave with <c>…_&lt;Stance&gt;_None_None_P0_Stand_Begin</c>. Missing motions fall back to the plain stance loop, the enter
    /// motion held at its end (a stance without a loop: the orc's SleepGround), or idle.
    /// </summary>
    public static class G3InteractionMotions
    {
        public sealed class Pose
        {
            public Pose(string stance, string left, string right, string action)
            {
                Stance = stance;
                Left = left;
                Right = right;
                Action = action;
            }

            public string Stance { get; }
            public string Left { get; }
            public string Right { get; }
            public string Action { get; }
            public bool IsStanding => Stance == "Stand";

            /// <summary>Item template held in the left / right hand during the use (null = empty hand), as the G3 routine task holds it.</summary>
            public string LeftProp { get; private set; }
            public string RightProp { get; private set; }

            /// <summary>Relaxing use (G3 <c>_AI_Enjoy</c>): the NPC takes a drink, food or a pipe into the right hand and uses it now and then.</summary>
            public bool Enjoy { get; private set; }

            public Pose Holding(string left, string right) => new Pose(Stance, Left, Right, Action) { LeftProp = left, RightProp = right };
            public Pose Enjoying() => new Pose(Stance, Left, Right, Action) { Enjoy = true };
        }

        static Pose Stand(string action, string left = "None", string right = "None") => new Pose("Stand", left, right, action);
        static Pose In(string stance, string action = "Ambient") => new Pose(stance, "None", "None", action);

        /// <summary>
        /// Interaction (gEUseType name) → animation state and the items in the hands. The hand fields of the motion name are the
        /// use types of the held items (left first); the items are those the CP 1.75 routine tasks (<c>ZS_Smith_Anvil</c>,
        /// <c>ZS_Digger_PickGround</c> …) put into the hands with <c>_AI_HoldItems</c> (Docs/formats-animation.md §8.7).
        /// </summary>
        static readonly Dictionary<string, Pose> Table = new Dictionary<string, Pose>(StringComparer.OrdinalIgnoreCase)
        {
            // Work
            ["Anvil"] = Stand("Anvil", "1H", "Tool").Holding("It_Sword", "It_Hammer_Smith"),
            ["Forge"] = Stand("Forge", "1H", "Tool").Holding("It_Sword", "It_Hammer_Smith"),
            ["GrindStone"] = Stand("GrindStone", "1H").Holding("It_Sword", null),
            ["Cauldron"] = Stand("Cauldron", right: "Tool").Holding(null, "It_Scoop"),
            ["Barbecue"] = Stand("Barbecue"),
            ["Alchemy"] = Stand("Alchemy", right: "Potion").Holding(null, "It_Potion"),
            ["Bookstand"] = Stand("BookStand"),
            ["Bookshelf"] = Stand("BookStand"),
            ["PickOre"] = Stand("PickOre", "2H").Holding("It_Pickaxe", null),
            ["PickGround"] = Stand("PickGround", "2H").Holding("It_Pickaxe", null),
            ["DigGround"] = Stand("DigGround", "Staff").Holding("It_Shovel", null),
            ["Field"] = Stand("FieldWork"),
            ["Repair"] = Stand("Repair", right: "Tool").Holding(null, "It_Hammer"),
            ["SawLog"] = Stand("SawLog", right: "Tool").Holding(null, "It_Saw"),
            ["Lumberjack"] = Stand("Lumberjack", "2H").Holding("It_2H_Axe_Lumberjack", null),
            ["TakeStone"] = Stand("Take"),
            ["DropStone"] = Stand("Drop"),
            ["CleanFloor"] = Stand("CleanFloor", "Staff").Holding("It_Broom", null),
            ["FanBoss"] = Stand("Fan", "Staff").Holding("It_Fan", null),
            ["Dance"] = Stand("Dance"),
            ["OrcDance"] = Stand("OrcDance"),
            ["OrcDrum"] = Stand("OrcDrum", "Tool", "Tool").Holding("It_DrumStick", "It_DrumStick"),
            ["Stoneplate"] = Stand("Stoneplate"),
            ["WaterBarrel"] = Stand("DrinkWaterBarrel"),
            ["DrinkWater"] = Stand("DrinkWaterBarrel"),
            // Standing around
            ["Guard"] = Stand("ArmsCrossed"),
            ["Boss"] = Stand("HandsOnHips"),
            ["Pace"] = Stand("HandsOnHips"),
            ["Trader"] = Stand("HandsOnHips"),
            ["Smalltalk"] = Stand("Say"),
            ["Preach"] = Stand("Say"),
            ["Listener"] = Stand("Listen"),
            ["Spectator"] = Stand("Cheer"),
            ["Pee"] = Stand("Pee"),
            ["Stand"] = Stand("Ambient").Enjoying(),
            ["Bard"] = Stand("Use", "Lute").Holding("It_Lute", null),
            // Sitting, kneeling and sleeping
            ["Stool"] = In("SitStool").Enjoying(),
            ["Bench"] = In("SitBench").Enjoying(),
            ["Throne"] = In("SitThrone").Enjoying(),
            ["SitGround"] = In("SitGround").Enjoying(),
            ["SitCampfire"] = In("SitGround"),
            ["Campfire"] = In("SitGround"),
            ["Waterpipe"] = In("SitGround", "Waterpipe"),
            ["Shrine"] = In("Kneel"),
            ["Bed"] = In("SleepBed"),
            ["SleepGround"] = In("SleepGround"),
            // Animals
            ["EatGround"] = Stand("EatGround"),
            // ZS_Threaten's threat (G3 action Warn): the creature rigs have no Warn clip; their Say clip is the growl [interp]
            ["Warn"] = Stand("Say"),
            ["Ambient"] = Stand("Ambient"), // ZS_Ambient's idle action of creatures without points
        };

        static readonly string[] Directions = { "Fwd", "Left", "Right" };

        static IEnumerable<string> Preferring(string direction)
        {
            if (!string.IsNullOrEmpty(direction)) yield return direction;
            foreach (var d in Directions) if (d != direction) yield return d;
        }

        public static bool TryGet(string interaction, out Pose pose)
        {
            pose = null;
            return !string.IsNullOrEmpty(interaction) && Table.TryGetValue(interaction, out pose);
        }

        public static IEnumerable<Pose> All => Table.Values;

        /// <summary>The motion keys a rig may need for <paramref name="pose"/>, in the order they are tried.</summary>
        public static Clips Resolve(string rig, Pose pose, Func<string, bool> exists, string direction = "Fwd")
        {
            string First(params G3MotionKey[] keys)
            {
                foreach (var k in keys)
                {
                    string s = k.ToString();
                    if (exists(s)) return s;
                }
                return null;
            }

            G3MotionKey K(string stance, string left, string right, string action, string phase, string dir = "Fwd") =>
                new G3MotionKey(rig, action, phase, dir, stance, left, right);

            string s = pose.Stance, l = pose.Left, r = pose.Right, a = pose.Action;
            if (pose.IsStanding)
                return new Clips(
                    First(K(s, l, r, a, "Begin")),
                    First(K(s, l, r, a, "Loop"), K(s, l, r, a, "Ambient"), K(s, "None", "None", a, "Loop"), K(s, "None", "None", a, "Ambient")),
                    First(K(s, l, r, a, "End")));
            // Beds come in Left/Right variants (the side the user lies down on); enter, loop and leave use the same side.
            foreach (var dir in Preferring(direction))
            {
                string loop = First(K(s, l, r, a, "Loop", dir), K(s, l, r, a, "Ambient", dir), K(s, "None", "None", "Ambient", "Loop", dir));
                if (loop == null) continue;
                return new Clips(First(K("Stand", "None", "None", s, "Begin", dir)), loop, First(K(s, "None", "None", "Stand", "Begin", dir)));
            }
            // fix-217: a rig with the way into and out of the stance but no loop (the orc lies down on a SleepGround point
            // and gets up, G3 has no Orc_SleepGround loop) holds the enter motion's last frame instead of standing [interp].
            // fix-231: only for lying down to sleep (SleepGround, SleepBed); a sit or kneel without a loop keeps standing
            if (!HoldsEnterWithoutLoop(s)) return default;
            foreach (var dir in Preferring(direction))
            {
                string enter = First(K("Stand", "None", "None", s, "Begin", dir));
                if (enter == null) continue;
                return new Clips(enter, enter, First(K(s, "None", "None", "Stand", "Begin", dir)), holdsEnter: true);
            }
            return default;
        }

        /// <summary>
        /// A stance whose enter motion is held at its last frame when the rig has no loop for it (fix-217): the sleeping
        /// stances only, where the enter motion ends lying and the NPC sleeps there in the simulation (fix-231).
        /// </summary>
        public static bool HoldsEnterWithoutLoop(string stance) =>
            stance != null && stance.StartsWith("Sleep", StringComparison.Ordinal);

        /// <summary>Every key <see cref="Resolve"/> may ask for (for the importer that builds a rig's motion library).</summary>
        public static IEnumerable<string> CandidateKeys(string rig)
        {
            var keys = new List<string>();
            foreach (var pose in All)
            {
                Resolve(rig, pose, k => { keys.Add(k); return false; }); // every loop candidate
                if (pose.Enjoy)
                    foreach (var field in G3HandItems.EnjoyFields)
                        ResolveEnjoy(rig, pose, field, k => { keys.Add(k); return false; });
                if (pose.IsStanding) continue;
                foreach (var dir in Directions)
                {
                    keys.Add(new G3MotionKey(rig, pose.Stance, "Begin", dir).ToString());
                    keys.Add(new G3MotionKey(rig, "Stand", "Begin", dir, pose.Stance).ToString());
                }
            }
            return keys;
        }

        /// <summary>
        /// Motion keys of using a relaxing item of hand field <paramref name="field"/> ("Cup" …) in the stance of
        /// <paramref name="pose"/>: take it out, hold it (loop), raise it and lower it, put it away; null where missing.
        /// </summary>
        public static EnjoyClips ResolveEnjoy(string rig, Pose pose, string field, Func<string, bool> exists)
        {
            string K(string action, string phase)
            {
                string k = new G3MotionKey(rig, action, phase, "Fwd", pose.Stance, "None", field).ToString();
                return exists(k) ? k : null;
            }
            return new EnjoyClips(K("HoldRight", "Begin"), K("Ambient", "Loop"), K("Use", "Begin"), K("Use", "End"), K("HoldRight", "End"));
        }

        /// <summary>Motion keys of one relaxing item use (see <see cref="ResolveEnjoy"/>); usable when <see cref="Hold"/> exists.</summary>
        public readonly struct EnjoyClips
        {
            public EnjoyClips(string takeOut, string hold, string raise, string lower, string putAway)
            {
                TakeOut = takeOut;
                Hold = hold;
                Raise = raise;
                Lower = lower;
                PutAway = putAway;
            }

            public string TakeOut { get; }
            public string Hold { get; }
            public string Raise { get; }
            public string Lower { get; }
            public string PutAway { get; }
        }

        /// <summary>Motion keys of one interaction use: enter (one-shot), loop, leave (one-shot); null where missing.</summary>
        public readonly struct Clips
        {
            public Clips(string enter, string loop, string leave, bool holdsEnter = false)
            {
                Enter = enter;
                Loop = loop;
                Leave = leave;
                HoldsEnter = holdsEnter;
            }

            /// <summary>No loop motion: <see cref="Loop"/> is <see cref="Enter"/>, held at its last frame (fix-217).</summary>
            public bool HoldsEnter { get; }

            public string Enter { get; }
            public string Loop { get; }
            public string Leave { get; }
        }
    }
}
