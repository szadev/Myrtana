using System.Collections.Generic;
using G3.Audio;
using NUnit.Framework;

namespace G3Tests.Audio
{
    /// <summary>
    /// fix-61: the motion effect track → effect mapping (G3.Audio.MotionEventWindow, used by G3MotionPlayer for the sounds of
    /// G3MotionSounds and the visuals of G3EffectSystem) and the priority order that keeps those sounds audible.
    /// </summary>
    [TestFixture]
    public class MotionEventWindowTests
    {
        // Hero_Stand_1H_None_P0_GrindStone_Loop_N (2.0 s, 25 fps): frame 0 footsqueak + wheel + sword (sparks), frame 30 footsqueak
        static readonly MotionEventWindow.TrackEvent[] GrindLoop =
        {
            new MotionEventWindow.TrackEvent(0f, "EFF_Ani_Sharpeningstone_Footsqueak_01"),
            new MotionEventWindow.TrackEvent(0f, "EFF_Ani_Sharpeningstone_Wheel_01"),
            new MotionEventWindow.TrackEvent(0f, "EFF_Ani_Sharpeningstone_Sword_01"),
            new MotionEventWindow.TrackEvent(1.2f, "EFF_Ani_Sharpeningstone_Footsqueak_01"),
        };

        static List<string> Run(IReadOnlyList<MotionEventWindow.TrackEvent> track, float length, bool loop, bool reverse, float seconds, float dt = 1f / 30)
        {
            var fired = new List<string>();
            float phase = 0f; // as G3MotionPlayer: the first step starts at 0, a loop's range runs past 1 within a step
            for (float t = 0; t < seconds - 1e-4f; t += dt)
            {
                float from = phase, to = phase + dt / length;
                if (!loop) to = System.Math.Min(to, 1f);
                MotionEventWindow.Collect(track, length, from, to, loop, reverse, fired);
                phase = loop ? to - (float)System.Math.Floor(to) : to;
            }
            return fired;
        }

        [Test]
        public void Grindstone_loop_fires_wheel_footsqueak_and_sparks_every_cycle()
        {
            var fired = Run(GrindLoop, 2f, loop: true, reverse: false, seconds: 6f);
            Assert.AreEqual(3, fired.FindAll(e => e == "EFF_Ani_Sharpeningstone_Wheel_01").Count, "one wheel sound per 2 s cycle");
            Assert.AreEqual(3, fired.FindAll(e => e == "EFF_Ani_Sharpeningstone_Sword_01").Count, "sparks + sword scrape per cycle");
            Assert.AreEqual(6, fired.FindAll(e => e == "EFF_Ani_Sharpeningstone_Footsqueak_01").Count, "two pedal squeaks per cycle");
            Assert.AreEqual("EFF_Ani_Sharpeningstone_Footsqueak_01", fired[0], "the frame-0 events fire on the first step");
        }

        [Test]
        public void A_step_crossing_the_loop_end_fires_the_frame_0_events()
        {
            var fired = new List<string>();
            MotionEventWindow.Collect(GrindLoop, 2f, 0.98f, 1.02f, loop: true, reverse: false, fired);
            CollectionAssert.AreEquivalent(new[] { "EFF_Ani_Sharpeningstone_Footsqueak_01", "EFF_Ani_Sharpeningstone_Wheel_01",
                                                   "EFF_Ani_Sharpeningstone_Sword_01" }, fired);
            fired.Clear();
            Assert.AreEqual(0, MotionEventWindow.Collect(GrindLoop, 2f, 0.4f, 0.4f, true, false, fired), "an empty step fires nothing");
            Assert.AreEqual(1, MotionEventWindow.Collect(GrindLoop, 2f, 0.59f, 0.61f, true, false, fired), "frame 30 = phase 0.6");
        }

        [Test]
        public void One_shots_fire_once_and_an_event_on_the_last_frame_still_fires()
        {
            // Hero_Stand_1H_Tool_P0_Anvil_Loop_N would loop; a one-shot (OpenChest_Begin, 0.8 s, chest open at 0.16 s) does not
            var chestOpen = new[] { new MotionEventWindow.TrackEvent(0.16f, "EFF_Ani_Chest_Open_01") };
            Assert.AreEqual(new[] { "EFF_Ani_Chest_Open_01" }, Run(chestOpen, 0.8f, loop: false, reverse: false, seconds: 3f).ToArray());
            var atEnd = new[] { new MotionEventWindow.TrackEvent(0.8f, "EFF_Ani_Chest_Close_01") };
            Assert.AreEqual(1, Run(atEnd, 0.8f, loop: false, reverse: false, seconds: 2f).Count);
        }

        [Test]
        public void Direction_marks_choose_forward_or_reverse_playback()
        {
            Assert.AreEqual("eff_ani_fight_draw_1hsword_01", MotionEventWindow.EffectFor("[F]eff_ani_fight_draw_1hsword_01", reverse: false));
            Assert.IsNull(MotionEventWindow.EffectFor("[F]eff_ani_fight_draw_1hsword_01", reverse: true));
            Assert.AreEqual("eff_ani_fight_undraw_1hsword_01", MotionEventWindow.EffectFor("[R]eff_ani_fight_undraw_1hsword_01", reverse: true));
            Assert.IsNull(MotionEventWindow.EffectFor("[R]eff_ani_fight_undraw_1hsword_01", reverse: false));
            Assert.AreEqual("EFF_Ani_Anvil_01", MotionEventWindow.EffectFor("EFF_Ani_Anvil_01", reverse: true), "unmarked: both ways");
            Assert.IsNull(MotionEventWindow.EffectFor("", false));
            Assert.IsNull(MotionEventWindow.EffectFor("[F]", false));

            var track = new[] { new MotionEventWindow.TrackEvent(0.1f, "[F]a"), new MotionEventWindow.TrackEvent(0.1f, "[R]b") };
            var fwd = new List<string>();
            MotionEventWindow.Collect(track, 1f, 0f, 1f, false, false, fwd);
            Assert.AreEqual(new[] { "a" }, fwd.ToArray());
            var rev = new List<string>();
            MotionEventWindow.Collect(track, 1f, 0.85f, 0.95f, false, true, rev); // reversed: 0.1 s of a 1 s clip comes at phase 0.9
            Assert.AreEqual(new[] { "b" }, rev.ToArray());
        }

        [Test]
        public void Human_steps_are_left_to_the_footsteps_everything_else_plays_from_the_track()
        {
            Assert.IsFalse(MotionEventWindow.PlaysFromTrack("eff_step_human_stone_01"));
            Assert.IsFalse(MotionEventWindow.PlaysFromTrack("EFF_Step_Human_Grass_Run_01"));
            Assert.IsTrue(MotionEventWindow.PlaysFromTrack("eff_step_creature01"));
            Assert.IsTrue(MotionEventWindow.PlaysFromTrack("EFF_Ani_Sharpeningstone_Wheel_01"));
            Assert.IsFalse(MotionEventWindow.PlaysFromTrack(null));
        }

        [Test]
        public void Gameplay_sounds_outrank_the_worlds_placed_loops()
        {
            // 0 = most important; Faring has 77 placed loops within 60 m of the grindstone and 32 real voices
            Assert.Less(SoundPriority.HeroMotion, SoundPriority.Default, "the hero's tools above speech/steps");
            Assert.Less(SoundPriority.Default, SoundPriority.Effect);
            Assert.Less(SoundPriority.Effect, SoundPriority.WorldEmitter);
            Assert.Less(SoundPriority.CharacterMotion, SoundPriority.WorldEmitter);
            Assert.Less(SoundPriority.AmbientOneShot, SoundPriority.WorldEmitter);
            Assert.Less(SoundPriority.Music, SoundPriority.AmbientBed);
            Assert.LessOrEqual(SoundPriority.WorldEmitter, 256);
        }
    }
}
