using System;
using System.Collections.Generic;

namespace G3.Unity.Characters
{
    /// <summary>
    /// The items NPCs hold while they relax (G3 <c>_AI_Enjoy</c>, Docs/formats-animation.md §8.7): which one an NPC takes,
    /// and the hand field its use type puts into the motion names (<c>Orc_SitBench_None_Cup_P0_Use_Begin</c>). The "Fake"
    /// templates are show items that never enter a real inventory.
    /// </summary>
    public static class G3HandItems
    {
        /// <summary>Template → use type (gCInteraction_PS.UseType of the template) as it appears in motion names.</summary>
        static readonly Dictionary<string, string> MotionField = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["It_FakeWine"] = "Potion",
            ["It_FakeCup_Gold"] = "Cup",
            ["It_FakeCup_Silver"] = "Cup",
            ["It_FakeCup_Clay"] = "Cup",
            ["It_FakeOrcPipe"] = "OrcPipe",
            ["It_FakeJoint"] = "Smoke",
            ["It_Joint"] = "Smoke",
            ["It_FakeBread"] = "Bread",
            ["It_FakeApple"] = "Fruit",
            ["It_FakeRice"] = "Bowl",
            ["It_Meat"] = "Meat",
            // fix-153: the letter NPCs read when an info has them "Use It_Letter" (FakeItems, UseType Letter = 44;
            // Hero/Orc_Stand_None_Letter_P0_HoldRight|Use|Ambient motions). Not a relaxing pick (PickEnjoyItem).
            ["It_Letter"] = "Letter",
        };

        // CP 1.75 picks by species: orcs one of four, humans one of nine (the joint three times). The orcs' fourth case and
        // the nomads' own list (clay cup, joint, rice, bread; political alignment 8) are not reproduced.
        static readonly string[] Orc = { "It_FakeWine", "It_FakeCup_Gold", "It_FakeOrcPipe" };
        static readonly string[] Human =
        {
            "It_FakeWine", "It_FakeRice", "It_FakeJoint", "It_FakeCup_Silver", "It_FakeJoint", "It_Meat", "It_FakeApple",
            "It_FakeBread", "It_FakeJoint",
        };

        /// <summary>A relaxing item for an NPC of <paramref name="rig"/> ("Orc", "Hero" …); <paramref name="roll"/> in [0, 1).</summary>
        public static string PickEnjoyItem(string rig, double roll)
        {
            var list = string.Equals(rig, "Orc", StringComparison.OrdinalIgnoreCase) ? Orc : Human;
            return list[Math.Min(list.Length - 1, (int)(roll * list.Length))];
        }

        /// <summary>Hand field of <paramref name="template"/> in motion names ("Cup", "Smoke" …), or null if unknown.</summary>
        public static string FieldOf(string template) =>
            template != null && MotionField.TryGetValue(template, out var f) ? f : null;

        public static IEnumerable<string> EnjoyFields => new HashSet<string>(MotionField.Values);

        /// <summary>Every template an NPC may hold: the tools of <see cref="G3InteractionMotions"/> and the relaxing items.</summary>
        public static IEnumerable<string> AllTemplates()
        {
            var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pose in G3InteractionMotions.All)
            {
                if (pose.LeftProp != null) all.Add(pose.LeftProp);
                if (pose.RightProp != null) all.Add(pose.RightProp);
            }
            all.UnionWith(MotionField.Keys);
            return all;
        }
    }
}
