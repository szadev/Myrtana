using G3.Audio;
using NUnit.Framework;

namespace G3Tests.Audio
{
    /// <summary>
    /// fix-199: a 3D sound's side relative to the camera's listener (G3.Audio.ListenerPan, Unity's left-handed axes) and the
    /// choice of the speech voice that says a speaker's line (G3.Audio.SpeakerVoiceSlots).
    /// </summary>
    [TestFixture]
    public class ListenerPanTests
    {
        static readonly V3 Up = new V3(0, 1, 0);
        static readonly V3 North = new V3(0, 0, 1);

        static V3 Yaw(float degrees)
        {
            double r = degrees * System.Math.PI / 180;
            return new V3((float)System.Math.Sin(r), 0, (float)System.Math.Cos(r)); // Unity: yaw +90 faces +x
        }

        [Test]
        public void Right_of_a_listener_facing_plus_z_is_plus_x()
        {
            var r = ListenerPan.Right(North, Up);
            Assert.That(r.x, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(r.z, Is.EqualTo(0f).Within(1e-5f));
        }

        [Test]
        public void Source_on_the_right_pans_right_at_every_distance()
        {
            var cam = new V3(10, 2, 10);
            foreach (float d in new[] { 1f, 5f, 20f, 60f, 200f })
            {
                var src = new V3(10 + d, 2, 10);
                Assert.That(ListenerPan.Pan(cam, North, Up, src), Is.EqualTo(1f).Within(1e-4f), $"{d} m");
                Assert.That(ListenerPan.Azimuth(cam, North, Up, src), Is.EqualTo(90f).Within(1e-3f), $"{d} m");
                Assert.That(ListenerPan.Side(ListenerPan.Azimuth(cam, North, Up, src)), Is.EqualTo("right"));
            }
        }

        [Test]
        public void Source_on_the_left_pans_left()
        {
            var src = new V3(-30, 0, 0);
            Assert.That(ListenerPan.Pan(new V3(0, 0, 0), North, Up, src), Is.EqualTo(-1f).Within(1e-4f));
            Assert.That(ListenerPan.Side(ListenerPan.Azimuth(new V3(0, 0, 0), North, Up, src)), Is.EqualTo("left"));
        }

        [Test]
        public void Pan_follows_the_listener_yaw_not_world_x()
        {
            // The camera faces +x (yaw 90): a source at -z is on its right, one at +x straight ahead.
            var cam = new V3(0, 0, 0);
            var east = Yaw(90);
            Assert.That(ListenerPan.Pan(cam, east, Up, new V3(0, 0, -25)), Is.EqualTo(1f).Within(1e-4f));
            Assert.That(ListenerPan.Pan(cam, east, Up, new V3(25, 0, 0)), Is.EqualTo(0f).Within(1e-4f));
            // Turned round (yaw 180): world +x is on the left.
            Assert.That(ListenerPan.Pan(cam, Yaw(180), Up, new V3(25, 0, 0)), Is.EqualTo(-1f).Within(1e-4f));
        }

        [Test]
        public void Third_person_camera_behind_the_hero_hears_his_right_side_on_the_right()
        {
            // Hero at the origin facing yaw 30, camera 3 m behind him and 2.3 m up, pitched 10 degrees down at him.
            var heroForward = Yaw(30);
            var cam = new V3(-heroForward.x * 3, 2.3f, -heroForward.z * 3);
            var pitchDown = (heroForward + new V3(0, -0.18f, 0)).Normalized;
            var camUp = ListenerPan.Cross(pitchDown, ListenerPan.Right(pitchDown, Up)); // forward × right = up (left-handed)
            var heroRight = ListenerPan.Right(heroForward, Up);
            foreach (float d in new[] { 2f, 10f, 25f })
            {
                var orc = heroRight * d + heroForward * 2f + new V3(0, 1.7f, 0);
                float pan = ListenerPan.Pan(cam, pitchDown, camUp, orc);
                Assert.That(pan, Is.GreaterThan(0.3f), $"orc {d} m to the hero's right");
                if (d >= 10f) Assert.That(ListenerPan.Side(ListenerPan.Azimuth(cam, pitchDown, camUp, orc)), Is.EqualTo("right"), $"{d} m");
                var left = heroRight * -d + heroForward * 2f + new V3(0, 1.7f, 0);
                Assert.That(ListenerPan.Pan(cam, pitchDown, camUp, left), Is.LessThan(-0.3f), $"orc {d} m to the hero's left");
            }
        }

        [Test]
        public void Behind_and_ahead_are_centred()
        {
            var cam = new V3(0, 0, 0);
            Assert.That(ListenerPan.Pan(cam, North, Up, new V3(0, 0, 40)), Is.EqualTo(0f).Within(1e-4f));
            Assert.That(ListenerPan.Pan(cam, North, Up, new V3(0, 0, -40)), Is.EqualTo(0f).Within(1e-4f));
            Assert.That(ListenerPan.Side(ListenerPan.Azimuth(cam, North, Up, new V3(0, 0, -40))), Is.EqualTo("behind"));
            Assert.That(ListenerPan.Pan(cam, North, Up, cam), Is.EqualTo(0f));
        }

        // SpeakerVoiceSlots

        [Test]
        public void A_speaker_keeps_his_own_voice_for_a_new_line()
        {
            var speakers = new[] { "Orc_A", "Orc_B", null };
            var busy = new[] { true, true, false };
            var started = new[] { 1f, 2f, 0f };
            Assert.That(SpeakerVoiceSlots.Choose(speakers, busy, started, "Orc_B", false), Is.EqualTo(1));
        }

        [Test]
        public void A_loading_voice_is_not_taken_by_another_speaker()
        {
            // Voice 0 still loads Orc_A's line (busy): Orc_B's line takes the free voice 1, not voice 0 at Orc_A's place.
            var speakers = new[] { "Orc_A", null };
            var busy = new[] { true, false };
            Assert.That(SpeakerVoiceSlots.Choose(speakers, busy, new[] { 5f, 0f }, "Orc_B", true), Is.EqualTo(1));
        }

        [Test]
        public void All_busy_ambient_lines_are_skipped_reactions_take_the_oldest()
        {
            var speakers = new[] { "A", "B", "C" };
            var busy = new[] { true, true, true };
            var started = new[] { 3f, 1f, 2f };
            Assert.That(SpeakerVoiceSlots.Choose(speakers, busy, started, "D", false), Is.EqualTo(-1));
            Assert.That(SpeakerVoiceSlots.Choose(speakers, busy, started, "D", true), Is.EqualTo(1));
        }

        [Test]
        public void A_finished_voice_of_the_same_speaker_is_just_a_free_one()
        {
            var speakers = new[] { "A", "B" };
            var busy = new[] { false, false };
            Assert.That(SpeakerVoiceSlots.Choose(speakers, busy, new[] { 0f, 0f }, "B", false), Is.EqualTo(0));
            Assert.That(SpeakerVoiceSlots.Choose(new string[0], new bool[0], new float[0], "B", true), Is.EqualTo(-1));
        }
    }
}
