using System.Collections.Generic;
using System.Linq;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-61: the object's own part of the hero's use motions (G3's <c>_I_</c> variants: the grindstone wheel turns while
    /// the hero sharpens, the chest lid opens and shuts, the boulder lies down when the lift is cut short) and the action
    /// loop with its Ambient variant (PS_Interact_Loop).
    /// </summary>
    public sealed class ObjectUsePartTests
    {
        const string GrindBegin = "Hero_Stand_1H_None_P0_GrindStone_Begin_N_Fwd_00";
        const string GrindLoop = "Hero_Stand_1H_None_P0_GrindStone_Loop_N_Fwd_00";
        const string GrindAmbient = "Hero_Stand_1H_None_P0_GrindStone_Ambient_N_Fwd_00";
        const string GrindEnd = "Hero_Stand_1H_None_P0_GrindStone_End_N_Fwd_00";

        // the parts G3 ships (G3Cli anim list _I_): every phase of the grindstone, chest, barbecue, water pipe and boulder uses
        static readonly HashSet<string> Shipped = new HashSet<string>
        {
            "Hero_Stand_1H_None_P0_GrindStone_Begin_I_Fwd_00", "Hero_Stand_1H_None_P0_GrindStone_Loop_I_Fwd_00",
            "Hero_Stand_1H_None_P0_GrindStone_Ambient_I_Fwd_00", "Hero_Stand_1H_None_P0_GrindStone_End_I_Fwd_00",
            "Hero_Kneel_None_None_P0_OpenChest_Begin_I_Fwd_00", "Hero_Kneel_None_None_P0_OpenChest_Loop_I_Fwd_00",
            "Hero_Kneel_None_None_P0_OpenChest_End_I_Fwd_00",
            "Hero_Stand_None_None_P0_Barbecue_Begin_I_Fwd_00", "Hero_Stand_None_None_P0_Barbecue_Loop_I_Fwd_00",
            "Hero_Stand_None_None_P0_Barbecue_End_I_Fwd_00",
            "Hero_Stand_None_None_P0_TiltOrcBoulder_Begin_I_Fwd_00", "Hero_TiltOrcBoulder_None_None_P0_Ambient_Loop_I_Fwd_00",
            "Hero_TiltOrcBoulder_None_None_P0_Stand_Begin_I_Fwd_00", "Hero_TiltOrcBoulder_None_None_P0_HoldOrcBoulder_Begin_I_Fwd_00",
            "Hero_HoldOrcBoulder_None_None_P0_Ambient_Loop_I_Fwd_00", "Hero_HoldOrcBoulder_None_None_P0_Stand_Begin_I_Fwd_00",
        };

        static ObjectPartState Grindstone() => new ObjectPartState(Shipped.Contains);

        [Test]
        public void Part_key_is_the_I_variant_of_the_heros_motion()
        {
            Assert.AreEqual("Hero_Stand_1H_None_P0_GrindStone_Loop_I_Fwd_00", ObjectUseParts.PartOf(GrindLoop));
            Assert.AreEqual("Hero_Stand_1H_None_P0_GrindStone_Loop_I_Fwd_00",
                            ObjectUseParts.PartOf("Hero_Stand_1H_None_P0_GrindStone_Loop_N_Fwd_00_%_00_P0_0"), "a full motion name");
            Assert.IsNull(ObjectUseParts.PartOf("Hero_Stand"));
            Assert.IsNull(ObjectUseParts.PartOf(null));
            Assert.IsTrue(ObjectUseParts.Loops("Hero_Stand_1H_None_P0_GrindStone_Loop_I_Fwd_00"));
            Assert.IsTrue(ObjectUseParts.Loops("Hero_Stand_1H_None_P0_GrindStone_Ambient_I_Fwd_00"));
            Assert.IsFalse(ObjectUseParts.Loops("Hero_Stand_1H_None_P0_GrindStone_End_I_Fwd_00"));
        }

        [Test]
        public void Animated_use_types_are_those_with_parts_in_the_motion_data()
        {
            CollectionAssert.AreEquivalent(new[] { "Chest", "GrindStone", "Barbecue", "Waterpipe", "OrcBoulder" },
                                           ObjectUseParts.AnimatedUseTypes.ToArray());
            Assert.IsTrue(ObjectUseParts.Animated("grindstone"), "use types compare case-insensitively");
            foreach (var plain in new[] { "Anvil", "Cauldron", "Alchemy", "Campfire", "Bookstand", "SawLog", "OrcDrum", "Bed", "Bench",
                                          "Stool", "Throne", "PickOre", "WaterBarrel", "Door", null })
            {
                Assert.IsFalse(ObjectUseParts.Animated(plain), plain);
                Assert.IsEmpty(ObjectUseParts.HeroMotionPrefixes(plain));
            }
            Assert.AreEqual(new[] { "Hero_Stand_1H_None_P0_GrindStone_" }, ObjectUseParts.HeroMotionPrefixes("GrindStone").ToArray());
            Assert.AreEqual(4, ObjectUseParts.HeroMotionPrefixes("OrcBoulder").Count);
            foreach (var key in Shipped)
                Assert.IsTrue(ObjectUseParts.AnimatedUseTypes.Any(u => ObjectUseParts.HeroMotionPrefixes(u).Any(key.StartsWith)), key);
        }

        [Test]
        public void Grindstone_wheel_turns_while_the_hero_sharpens_and_runs_out_after()
        {
            var s = Grindstone();
            Assert.AreEqual(ObjectPartMode.Still, s.Mode);
            s.RestAt(GrindEnd);
            var begin = s.HeroPlays(GrindBegin);
            Assert.AreEqual(ObjectPartAction.Play, begin.Action);
            Assert.AreEqual("Hero_Stand_1H_None_P0_GrindStone_Begin_I_Fwd_00", begin.Part);
            Assert.IsFalse(s.Moving, "Begin runs once");

            var loop = s.HeroPlays(GrindLoop);
            Assert.AreEqual(new ObjectPartCommand(ObjectPartAction.Play, "Hero_Stand_1H_None_P0_GrindStone_Loop_I_Fwd_00").ToString(), loop.ToString());
            Assert.IsTrue(s.Moving, "in use: the wheel turns");

            Assert.AreEqual("Hero_Stand_1H_None_P0_GrindStone_Ambient_I_Fwd_00", s.HeroPlays(GrindAmbient).Part);
            Assert.IsTrue(s.Moving, "the Ambient variant turns it too");
            s.HeroPlays(GrindLoop);

            var end = s.HeroPlays(GrindEnd);
            Assert.AreEqual("Hero_Stand_1H_None_P0_GrindStone_End_I_Fwd_00", end.Part);
            Assert.IsFalse(s.Moving);
            Assert.AreEqual(ObjectPartAction.None, s.UseEnded().Action, "the End part holds its last frame");
            Assert.AreEqual(ObjectPartMode.Resting, s.Mode);
            Assert.AreEqual("Hero_Stand_1H_None_P0_GrindStone_End_I_Fwd_00", s.Part);
            Assert.AreEqual(ObjectPartAction.None, s.UseEnded().Action, "a second end changes nothing");
        }

        [Test]
        public void Grindstone_cut_short_rests_at_its_End_frame()
        {
            var s = Grindstone();
            s.RestAt(GrindEnd);
            s.HeroPlays(GrindBegin);
            s.HeroPlays(GrindLoop);
            var ended = s.UseEnded();
            Assert.AreEqual(ObjectPartAction.Rest, ended.Action);
            Assert.AreEqual("Hero_Stand_1H_None_P0_GrindStone_End_I_Fwd_00", ended.Part);
            Assert.IsFalse(s.Moving, "after use: the wheel stands still");
        }

        [Test]
        public void A_loop_without_closing_part_stops()
        {
            var s = new ObjectPartState(k => k.Contains("GrindStone_Loop_I"));
            s.RestAt(GrindEnd); // no End part on this visual
            Assert.IsNull(s.RestPart);
            s.HeroPlays(GrindLoop);
            Assert.AreEqual(ObjectPartAction.Stop, s.UseEnded().Action);
        }

        [Test]
        public void Chest_lid_follows_OpenChest_and_kneeling_leaves_it_alone()
        {
            var s = new ObjectPartState(Shipped.Contains);
            Assert.AreEqual(ObjectPartAction.None, s.HeroPlays("Hero_Stand_None_None_P0_Kneel_Begin_N_Fwd_00").Action, "no part of kneeling");
            Assert.AreEqual(ObjectPartAction.None, s.HeroPlays("Hero_Kneel_None_None_P0_Lockpick_Loop_N_Fwd_00").Action, "nor of the lock pick");
            s.RestAt("Hero_Kneel_None_None_P0_OpenChest_End_N_Fwd_00");
            Assert.AreEqual("Hero_Kneel_None_None_P0_OpenChest_Begin_I_Fwd_00", s.HeroPlays("Hero_Kneel_None_None_P0_OpenChest_Begin_N_Fwd_00").Part);
            Assert.AreEqual("Hero_Kneel_None_None_P0_OpenChest_Loop_I_Fwd_00", s.HeroPlays("Hero_Kneel_None_None_P0_OpenChest_Loop_N_Fwd_00").Part);
            // the loot page is closed by a fight: the lid shuts
            var cut = s.UseEnded();
            Assert.AreEqual(ObjectPartAction.Rest, cut.Action);
            Assert.AreEqual("Hero_Kneel_None_None_P0_OpenChest_End_I_Fwd_00", cut.Part);
        }

        [Test]
        public void Chest_used_through_ends_shut_and_standing_up_leaves_it()
        {
            var s = new ObjectPartState(Shipped.Contains);
            s.RestAt("Hero_Kneel_None_None_P0_OpenChest_End_N_Fwd_00");
            s.HeroPlays("Hero_Kneel_None_None_P0_OpenChest_Begin_N_Fwd_00");
            s.HeroPlays("Hero_Kneel_None_None_P0_OpenChest_Loop_N_Fwd_00");
            s.HeroPlays("Hero_Kneel_None_None_P0_OpenChest_End_N_Fwd_00");
            Assert.AreEqual(ObjectPartAction.None, s.HeroPlays("Hero_Kneel_None_None_P0_Stand_Begin_N_Fwd_00").Action);
            Assert.AreEqual("Hero_Kneel_None_None_P0_OpenChest_End_I_Fwd_00", s.Part);
            Assert.AreEqual(ObjectPartAction.None, s.UseEnded().Action);
        }

        [Test]
        public void Boulder_cut_short_lies_down_from_the_stage_reached()
        {
            var s = new ObjectPartState(Shipped.Contains);
            s.HeroPlays("Hero_Stand_None_None_P0_TiltOrcBoulder_Begin_N_Fwd_00");
            s.RestAt("Hero_TiltOrcBoulder_None_None_P0_Stand_Begin_N_Fwd_00");
            s.HeroPlays("Hero_TiltOrcBoulder_None_None_P0_Ambient_Loop_N_Fwd_00");
            s.HeroPlays("Hero_TiltOrcBoulder_None_None_P0_HoldOrcBoulder_Begin_N_Fwd_00");
            s.RestAt("Hero_HoldOrcBoulder_None_None_P0_Stand_Begin_N_Fwd_00");
            s.HeroPlays("Hero_HoldOrcBoulder_None_None_P0_Ambient_Loop_N_Fwd_00");
            var cut = s.UseEnded();
            Assert.AreEqual(ObjectPartAction.Rest, cut.Action);
            Assert.AreEqual("Hero_HoldOrcBoulder_None_None_P0_Stand_Begin_I_Fwd_00", cut.Part);
        }

        [Test]
        public void Objects_without_parts_never_move()
        {
            // anvil, cauldron, saw log, beds, seats: plain meshes (the anvil is a static mesh in G3)
            var s = new ObjectPartState(_ => false);
            s.RestAt("Hero_Stand_1H_Tool_P0_Anvil_End_N_Fwd_00");
            Assert.AreEqual(ObjectPartAction.None, s.HeroPlays("Hero_Stand_1H_Tool_P0_Anvil_Begin_N_Fwd_00").Action);
            Assert.AreEqual(ObjectPartAction.None, s.HeroPlays("Hero_Stand_1H_Tool_P0_Anvil_Loop_N_Fwd_00").Action);
            Assert.AreEqual(ObjectPartMode.Still, s.Mode);
            Assert.AreEqual(ObjectPartAction.None, s.UseEnded().Action);
            Assert.AreEqual(ObjectPartAction.None, new ObjectPartState(null).HeroPlays(GrindLoop).Action);
        }

        [Test]
        public void Action_loop_plays_one_Ambient_cycle_at_a_ten_percent_wrap()
        {
            var loop = new ActionLoop();
            loop.LoopStarted();
            Assert.AreEqual(ActionLoopStep.None, loop.Step(0.3f, false, 0.01), "no wrap: no draw");
            Assert.AreEqual(ActionLoopStep.None, loop.Step(0.9f, false, 0.01));
            Assert.AreEqual(ActionLoopStep.None, loop.Step(0.05f, false, 0.5), "a wrap, roll 0.5: the loop goes on");
            Assert.AreEqual(ActionLoopStep.None, loop.Step(0.95f, false, 0.0));
            Assert.AreEqual(ActionLoopStep.Ambient, loop.Step(0.02f, false, 0.09), "a wrap, roll < 0.1: Ambient");
            Assert.IsTrue(loop.InAmbient);
            // the Ambient motion starts at 0 and loops in the data: its first wrap ends it
            Assert.AreEqual(ActionLoopStep.None, loop.Step(0.4f, false, 0.0));
            Assert.AreEqual(ActionLoopStep.None, loop.Step(0.99f, false, 0.0));
            Assert.AreEqual(ActionLoopStep.Loop, loop.Step(0.01f, false, 0.0), "after one Ambient cycle: back to the loop");
            Assert.IsFalse(loop.InAmbient);
            Assert.AreEqual(ActionLoopStep.None, loop.Step(0.5f, false, 0.0));
        }

        [Test]
        public void Action_loop_leaves_a_one_shot_Ambient_when_it_finishes_and_skips_a_missing_one()
        {
            var loop = new ActionLoop();
            loop.Step(0.9f, false, 0.5);
            Assert.AreEqual(ActionLoopStep.Ambient, loop.Step(0.1f, false, 0.0));
            Assert.AreEqual(ActionLoopStep.None, loop.Step(0.6f, false, 0.0));
            Assert.AreEqual(ActionLoopStep.Loop, loop.Step(1f, true, 0.0), "a one-shot Ambient ends at its last frame");

            loop.Step(0.9f, false, 0.5);
            Assert.AreEqual(ActionLoopStep.Ambient, loop.Step(0.1f, false, 0.0));
            loop.AmbientMissing(); // the pose has no Ambient motion
            Assert.IsFalse(loop.InAmbient);
            Assert.AreEqual(ActionLoopStep.None, loop.Step(0.2f, false, 0.0));
        }
    }
}
