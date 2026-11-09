using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G3.Audio;
using G3.Formats.Animation;
using G3.Formats.Audio;
using G3.Formats.Pak;
using Myrtana.Rules;
using NUnit.Framework;

namespace G3Tests.Audio
{
    /// <summary>
    /// fix-61 audit against the installed CP 1.75 data (G3_DATA or the Steam path; ignored when absent): which object uses
    /// have an object part (<c>_I_</c> motions that move the object's bones) and which sound events the hero's use motions
    /// carry, every one resolving to a g3.efm effect with samples (Docs/interactive-objects.md §8).
    /// </summary>
    [TestFixture]
    public class UseMotionDataTests
    {
        static string Data()
        {
            string data = Environment.GetEnvironmentVariable("G3_DATA") ?? @"C:\Program Files (x86)\Steam\steamapps\common\Gothic 3\Data";
            if (!File.Exists(Path.Combine(data, "_compiledAnimation.pak")) || !File.Exists(Path.Combine(data, "Library.pak")))
                Assert.Ignore("Gothic 3 data not installed");
            return data;
        }

        // hero use motion (ten-field key) → the effects of its track (lower case); the audit table of the docs
        static readonly (string motion, string[] effects)[] Table =
        {
            ("Hero_Stand_1H_None_P0_GrindStone_Loop_N_Fwd_00", new[] { "eff_ani_sharpeningstone_footsqueak_01", "eff_ani_sharpeningstone_wheel_01", "eff_ani_sharpeningstone_sword_01" }),
            ("Hero_Stand_1H_None_P0_GrindStone_Begin_N_Fwd_00", new string[0]),
            ("Hero_Stand_1H_None_P0_GrindStone_End_N_Fwd_00", new string[0]),
            ("Hero_Stand_1H_Tool_P0_Anvil_Loop_N_Fwd_00", new[] { "eff_ani_anvil_01", "eff_ani_anvil_soft_01" }),
            ("Hero_Stand_1H_Tool_P0_Anvil_Ambient_N_Fwd_01", new[] { "eff_ani_anvil_soft_01" }),
            ("Hero_Stand_None_Tool_P0_Cauldron_Loop_N_Fwd_00", new[] { "eff_ani_couldron_bubbles_01", "eff_ani_couldron_stir_01" }),
            ("Hero_Stand_None_Potion_P0_Alchemy_Loop_N_Fwd_00", new string[0]),
            ("Hero_Stand_None_None_P0_Barbecue_Loop_N_Fwd_00", new[] { "eff_ani_bbq_squeak_01" }),
            ("Hero_Stand_None_Tool_P0_SawLog_Loop_N_Fwd_00", new[] { "eff_ani_saw_pull_01", "eff_ani_saw_push_01" }),
            ("Hero_Stand_Tool_Tool_P0_OrcDrum_Loop_N_Fwd_00", new[] { "eff_ani_orcdrum_hard_01", "eff_ani_orcdrum_soft_01" }),
            ("Hero_SitGround_None_None_P0_Waterpipe_Loop_N_Fwd_00", new[] { "eff_ani_waterpipe_breathe_in_01", "eff_ani_waterpipe_bubbles_01", "eff_ani_waterpipe_breathe_out_01" }),
            ("Hero_Stand_2H_None_P0_PickOre_Loop_N_Fwd_00", new[] { "eff_ani_pickore_01" }),
            ("Hero_Stand_Staff_None_P0_DigGround_Loop_N_Fwd_00", new[] { "eff_ani_shovel_push_01", "eff_ani_shovel_swing_01" }),
            ("Hero_Stand_None_None_P0_DrinkWaterBarrel_Loop_N_Fwd_00", new[] { "eff_ani_drinkwater_01", "eff_ani_drink_01" }),
            ("Hero_Kneel_None_None_P0_OpenChest_Begin_N_Fwd_00", new[] { "eff_ani_chest_open_01" }),
            ("Hero_Kneel_None_None_P0_OpenChest_End_N_Fwd_00", new[] { "eff_ani_chest_close_01" }),
            ("Hero_Stand_None_None_P0_SleepBed_Begin_N_Left_00", new[] { "eff_ani_bed_squeak_01" }),
            ("Hero_Stand_None_None_P0_OpenDoor_Begin_N_Fwd_00", new string[0]),
            ("Hero_Stand_None_None_P0_SitBench_Begin_N_Fwd_00", new string[0]),
            ("Hero_Stand_None_None_P0_TiltOrcBoulder_Begin_N_Fwd_00", new string[0]),
        };

        // effects that exist without a sound command in CP 1.75 (silent in G3 too)
        static readonly HashSet<string> SilentInG3 = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "eff_ani_orcdrum_soft_01" };

        static MotionResource Motion(G3FileSystem fs, string key)
        {
            var e = fs.Files.FirstOrDefault(f => Path.GetFileName(f.Path).StartsWith(key + "_", StringComparison.OrdinalIgnoreCase) &&
                                                 f.Path.EndsWith(".xmot", StringComparison.OrdinalIgnoreCase));
            Assert.That(e, Is.Not.Null, key);
            return Xmot.Read(e.Read(), Path.GetFileName(e.Path));
        }

        [Test]
        public void Use_motion_sound_events_resolve_to_effects_with_samples()
        {
            string data = Data();
            using var anims = new G3FileSystem(data, "_compiledAnimation");
            using var lib = new G3FileSystem(data, "Library");
            var map = EffectMap.Read(lib.GetByName("g3.efm").Read());
            foreach (var (key, expected) in Table)
            {
                var m = Motion(anims, key);
                var names = m.Effects.Select(e => MotionEventWindow.EffectFor(e.Name, reverse: false)?.ToLowerInvariant())
                                     .Where(n => n != null).Distinct().ToArray();
                CollectionAssert.AreEquivalent(expected, names, key);
                foreach (var n in names)
                {
                    var fx = map.Get(n);
                    Assert.That(fx, Is.Not.Null, $"{key}: {n} in g3.efm");
                    if (SilentInG3.Contains(n)) continue;
                    Assert.That(fx.Sounds.Sum(s => s.Samples.Count), Is.GreaterThan(0), $"{key}: {n} has samples");
                    Assert.IsTrue(MotionEventWindow.PlaysFromTrack(n), n);
                }
            }
        }

        [Test]
        public void Grindstone_loop_events_fire_every_cycle_from_the_real_track()
        {
            using var anims = new G3FileSystem(Data(), "_compiledAnimation");
            var m = Motion(anims, "Hero_Stand_1H_None_P0_GrindStone_Loop_N_Fwd_00");
            var track = m.Effects.Select(e => new MotionEventWindow.TrackEvent(Math.Min(e.KeyFrame * m.KeyStep, m.Duration), e.Name)).ToArray();
            var fired = new List<string>();
            float phase = 0, dt = 1f / 60;
            for (int i = 0; i < (int)(3 * m.Duration / dt); i++)
            {
                float to = phase + dt / m.Duration;
                MotionEventWindow.Collect(track, m.Duration, phase, to, true, false, fired);
                phase = to - (float)Math.Floor(to);
            }
            Assert.AreEqual(3, fired.Count(f => f.Equals("EFF_Ani_Sharpeningstone_Wheel_01", StringComparison.OrdinalIgnoreCase)));
            Assert.AreEqual(3, fired.Count(f => f.Equals("EFF_Ani_Sharpeningstone_Sword_01", StringComparison.OrdinalIgnoreCase)));
        }

        static bool HasMotion(G3FileSystem fs, string key) =>
            fs.Files.Any(f => Path.GetFileName(f.Path).StartsWith(key + "_", StringComparison.OrdinalIgnoreCase) &&
                              f.Path.EndsWith(".xmot", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// fix-139: the hero's tool works last the <c>_AI_Action</c> command time (10 s, <see cref="TimedAction"/>), so the
        /// real motion lengths give 5 pickaxe strikes (Begin 0.8 s + Loop 2.0 s, no End, no Ambient) and 4 digs.
        /// </summary>
        [Test]
        public void Pickaxe_work_strikes_five_times_and_the_shovel_digs_four_with_the_real_motions()
        {
            using var anims = new G3FileSystem(Data(), "_compiledAnimation");
            const string ore = "Hero_Stand_2H_None_P0_PickOre_", dig = "Hero_Stand_Staff_None_P0_DigGround_";
            var oreBegin = Motion(anims, ore + "Begin_N_Fwd_00");
            var oreLoop = Motion(anims, ore + "Loop_N_Fwd_00");
            Assert.That(oreBegin.Duration, Is.EqualTo(0.8f).Within(0.001f));
            Assert.That(oreLoop.Duration, Is.EqualTo(2.0f).Within(0.001f));
            Assert.IsFalse(HasMotion(anims, ore + "End_N_Fwd_00"), "no PickOre End: the hero goes back from the last strike");
            Assert.IsFalse(HasMotion(anims, ore + "Ambient_N_Fwd_00"), "no PickOre Ambient: every cycle is a strike");
            Assert.That(oreLoop.Effects.Count(e => e.Name.Equals("EFF_Ani_PickOre_01", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(1), "one hit sound per cycle");
            int seconds = ObjectUseRules.Default.WorkSeconds;
            Assert.That(TimedAction.Cycles(oreBegin.Duration, oreLoop.Duration, seconds), Is.EqualTo(5));
            var digBegin = Motion(anims, dig + "Begin_N_Fwd_00");
            var digLoop = Motion(anims, dig + "Loop_N_Fwd_00");
            Assert.That(TimedAction.Cycles(digBegin.Duration, digLoop.Duration, seconds), Is.EqualTo(4));
        }

        static bool Moves(MotionTrack t) =>
            !t.IsHelper && !t.Node.StartsWith("G3_Dummy_Offset", StringComparison.OrdinalIgnoreCase) &&
            !t.Node.StartsWith("G3_Object_", StringComparison.OrdinalIgnoreCase) && !t.Node.EndsWith("_ROOT", StringComparison.OrdinalIgnoreCase) &&
            (t.Rotation.Length > 1 || t.Position.Length > 1);

        [Test]
        public void Every_animated_use_type_has_moving_object_parts_and_the_others_have_none()
        {
            using var anims = new G3FileSystem(Data(), "_compiledAnimation");
            var parts = anims.Files.Select(f => Path.GetFileName(f.Path))
                             .Where(n => n.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".xmot", StringComparison.OrdinalIgnoreCase)
                                         && MotionName.Parse(n.Substring(0, n.Length - 5))?.Variant == ObjectUseParts.ObjectVariant)
                             .ToList();
            foreach (var useType in ObjectUseParts.AnimatedUseTypes)
            {
                var own = parts.Where(p => ObjectUseParts.HeroMotionPrefixes(useType).Any(pre => p.StartsWith(pre, StringComparison.OrdinalIgnoreCase))).ToList();
                Assert.That(own, Is.Not.Empty, useType);
                bool anyMoves = own.Any(p => Xmot.Read(anims.GetByName(p).Read(), p).Tracks.Any(Moves));
                Assert.IsTrue(anyMoves, $"{useType}: some part moves a bone of the object");
            }
            // every hero _I_ part belongs to one of these use types (anvil, cauldron, saw, drum, beds, seats: none in G3)
            foreach (var p in parts)
                Assert.IsTrue(ObjectUseParts.AnimatedUseTypes.Any(u => ObjectUseParts.HeroMotionPrefixes(u).Any(pre => p.StartsWith(pre, StringComparison.OrdinalIgnoreCase))), p);
            var wheel = Xmot.Read(anims.GetByName("Hero_Stand_1H_None_P0_GrindStone_Loop_I_Fwd_00_%_00_P0_0.xmot").Read()).Find("GrindStone_Wheel");
            Assert.That(wheel, Is.Not.Null);
            Assert.That(wheel.Rotation.Length, Is.GreaterThan(10), "the wheel turns through the loop");
        }
    }
}
