using System;
using System.Collections.Generic;

namespace Myrtana.Rules
{
    /// <summary>
    /// What a creature strikes with (Docs/combat.md §9.10, fix-91). G3 [OnAI_Attack, Script_Game 0x10038580]: after the
    /// wind-up the attacker's weapon item goes to shape group 7 and touches the victim's weapon trigger; an animal holds the
    /// shapeless PhysicalFist, so the touch comes from its own animated body. We take the skeleton's striking ends
    /// [interp]: the jaw (<c>…_Head_Mouth_END</c>) of every biter (wolf, boar, scavenger, snapper, lurker, ripper,
    /// sabertooth, shadowbeast, rhino, bison, alligator — their mouth leads every attack motion: wolf Attack mouth up to
    /// 1.34 m in front of the root, boar 1.60 m, scavenger QuickAttack 1.14 m, `G3Cli anim bone`); without a jaw the front
    /// limbs and the head (minecrawler claws, troll and golem fists); with neither the head, claws and tail (the bloodfly
    /// stings with its tail). Before, a static capsule reached the AI's attack range (species range + move length: wolf
    /// 2.9 m, boar 3.0 m, scavenger 2.35 m) from the root, so a bite landed from 3.4–3.9 m, far beyond its jaw and the
    /// hero's sword.
    /// </summary>
    public static class CreatureStrike
    {
        public const string Mouth = "_Head_Mouth_END", Head = "_Head_Head_END", Hand = "_Hand_Hand_END", Claw = "_Claw_END", Tail = "_Tail_Tail_END";

        /// <summary>Strike radius per metre of body top, and its bounds (m).</summary>
        public const float RadiusPerTop = 0.1f, MinRadius = 0.12f, MaxRadius = 0.4f;

        /// <summary>The skeleton nodes (by name) whose segment from the parent joint strikes.</summary>
        public static List<string> Select(IEnumerable<string> boneNames)
        {
            var mouth = new List<string>();
            var hands = new List<string>();
            var head = new List<string>();
            var rest = new List<string>();
            foreach (var n in boneNames)
            {
                if (string.IsNullOrEmpty(n)) continue;
                if (Ends(n, Mouth)) mouth.Add(n);
                else if (Ends(n, Hand)) hands.Add(n);
                else if (Ends(n, Head)) head.Add(n);
                else if (Ends(n, Claw) || Ends(n, Tail)) rest.Add(n);
            }
            if (mouth.Count > 0) return mouth;
            var result = new List<string>(head);
            result.AddRange(hands.Count > 0 ? hands : rest);
            return result;
        }

        /// <summary>Radius of the striking capsules for a body whose top is <paramref name="top"/> m above the feet.</summary>
        public static float Radius(float top)
        {
            float r = top * RadiusPerTop;
            return r < MinRadius ? MinRadius : r > MaxRadius ? MaxRadius : r;
        }

        /// <summary>
        /// Forward reach of the fallback capsule for a rig without striking bones: the body's front (its footprint) plus
        /// 0.4 m, never the AI's attack range.
        /// </summary>
        public static float FallbackReach(float footprint) => Math.Max(0.4f, footprint + 0.4f);

        static bool Ends(string s, string suffix) => s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }
}
