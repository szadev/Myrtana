using System.Linq;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-91 (owner: "animals hit from a bit further than they can, you can't hit them or interrupt them"): a creature
    /// strikes with its animated jaw or claws (G3: the attacker's own body touches the victim's group-7 trigger), not with
    /// a capsule as long as the AI's attack range. Jaw paths are G3's (`G3Cli anim bone G3_Wolf_Body_01
    /// Wolf_Stand_None_Fist_P0_Attack_Hit_N_Fwd_00_%_00_P0_200_L Mouth_END`, root frame, cm → m; the motion's 2 m lunge
    /// is the root's own displacement on top of it in both versions).
    /// </summary>
    public class CreatureStrikeTests
    {
        static readonly WeaponTrigger Hero = MeleeHitGeometry.Trigger(0.8f, 1.95f, 0.5f, 0f);

        // Wolf Attack Hit, Wolf_Head_Mouth_END per 0.05 s (x, y, z), m.
        static readonly HitPoint[] WolfJaw =
        {
            new HitPoint(-0.01f, 0.09f, 0.85f), new HitPoint(-0.03f, 0.86f, 1.21f), new HitPoint(-0.10f, 1.33f, 1.22f),
            new HitPoint(-0.17f, 1.30f, 1.27f), new HitPoint(-0.06f, 1.18f, 1.33f), new HitPoint(0.01f, 0.95f, 1.34f),
            new HitPoint(-0.05f, 0.60f, 1.27f), new HitPoint(-0.06f, 0.28f, 1.14f), new HitPoint(-0.06f, 0.11f, 1.06f),
        };

        /// <summary>A point of the attacker's frame in the frame of a hero standing <paramref name="d"/> m ahead, facing it.</summary>
        static HitPoint InHero(HitPoint p, float d) => new HitPoint(-p.X, p.Y, d - p.Z);

        static bool JawTouches(float d, float radius)
        {
            for (int i = 1; i < WolfJaw.Length; i++)
            {
                var a = InHero(WolfJaw[i - 1], d);
                var b = InHero(WolfJaw[i], d);
                if (MeleeHitGeometry.SweepTouches(Hero, a, a, b, b, radius)) return true;
            }
            return false;
        }

        [Test]
        public void Biters_StrikeWithTheJaw_ClawersWithTheFrontLimbs_TheBloodflyWithItsTail()
        {
            var wolf = new[] { "Wolf_Head_Head_1", "Wolf_Head_Head_END", "Wolf_Head_Mouth_1", "Wolf_Head_Mouth_END", "Wolf_Left_Hand_Hand_END", "Wolf_Right_Hand_Hand_END", "Wolf_Tail_Tail_END" };
            Assert.That(CreatureStrike.Select(wolf), Is.EqualTo(new[] { "Wolf_Head_Mouth_END" }));
            var crawler = new[] { "Minecrawler_Head_Head_END", "Minecrawler_Left_Hand_Hand_END", "Minecrawler_Right_Hand_Hand_END" };
            Assert.That(CreatureStrike.Select(crawler), Is.EquivalentTo(crawler), "no jaw: the claws and the head");
            var troll = new[] { "Troll_Left_Hand_Hand_END", "Slot_LeftHand_Weapon", "Troll_Right_Hand_Hand_END", "Slot_RightHand_Weapon", "Troll_Head_Head_END" };
            Assert.That(CreatureStrike.Select(troll), Is.EquivalentTo(new[] { "Troll_Left_Hand_Hand_END", "Troll_Right_Hand_Hand_END", "Troll_Head_Head_END" }));
            var fly = new[] { "Bloodfly_Spine_Spine_END", "Bloodfly_Head_Head_END", "Bloodfly_Left_Claws_Claw_END", "Bloodfly_Right_Claws_Claw_END", "Bloodfly_Tail_Tail_END", "Bloodfly_Left_Leg1_Leg_END" };
            Assert.That(CreatureStrike.Select(fly), Is.EquivalentTo(new[] { "Bloodfly_Head_Head_END", "Bloodfly_Left_Claws_Claw_END", "Bloodfly_Right_Claws_Claw_END", "Bloodfly_Tail_Tail_END" }));
            Assert.That(CreatureStrike.Select(new[] { "Pelvis", "Spine" }), Is.Empty, "no striking bones: the fallback capsule");
        }

        [Test]
        public void StrikeRadius_ScalesWithTheBody_WithinBounds()
        {
            Assert.That(CreatureStrike.Radius(1.0f), Is.EqualTo(CreatureStrike.MinRadius), "wolf, boar");
            Assert.That(CreatureStrike.Radius(1.7f), Is.EqualTo(0.17f).Within(1e-4), "scavenger");
            Assert.That(CreatureStrike.Radius(6f), Is.EqualTo(CreatureStrike.MaxRadius), "troll");
            Assert.That(CreatureStrike.FallbackReach(0.56f), Is.EqualTo(0.96f).Within(1e-4), "the body's front, not the AI range");
        }

        /// <summary>
        /// The wolf's jaw reaches a hero whose centre is up to ~2.1 m in front of the wolf's root (1.34 m jaw + 0.12 m +
        /// the hero's 0.64 m trigger); the old capsule (chest height, 0.2 m to the AI's 2.9 m Attack reach, r 0.25) bit from 3.75 m.
        /// </summary>
        [Test]
        public void WolfBite_ReachesNoFurtherThanItsJaw_FixNinetyOne()
        {
            float r = CreatureStrike.Radius(1.0f);
            Assert.That(JawTouches(1.9f, r), Is.True, "in contact");
            Assert.That(JawTouches(2.05f, r), Is.True, "at the jaw's limit");
            Assert.That(JawTouches(2.25f, r), Is.False, "beyond the jaw");
            // The old capsule, for the record: the same hero 3.5 m away was bitten.
            var oldA = InHero(new HitPoint(0, 0.6f, 0.2f), 3.5f);
            var oldB = InHero(new HitPoint(0, 0.6f, 2.9f), 3.5f);
            Assert.That(MeleeHitGeometry.Gap(Hero, oldA, oldB, 0.25f), Is.LessThanOrEqualTo(0f));
        }
    }
}
