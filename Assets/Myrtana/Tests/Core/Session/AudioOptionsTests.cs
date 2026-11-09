using Myrtana.Session;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public class AudioOptionsTests
    {
        [Test]
        public void Subtitles_DefaultOn_AndRaiseChanged()
        {
            var options = new PlayerOptions();
            int changed = 0;
            options.Changed += () => changed++;
            Assert.That(options.Subtitles, Is.True);
            options.Subtitles = false;
            options.Subtitles = false;
            Assert.That(options.Subtitles, Is.False);
            Assert.That(changed, Is.EqualTo(1));
        }

        [TestCase(0, 0)]
        [TestCase(1, 1)]
        [TestCase(7, 7)]
        [TestCase(8, 0)]
        [TestCase(-3, 0)]
        public void SpeakerMode_KeepsG3Range_ElseUnset(int set, int stored)
        {
            var options = new PlayerOptions { SpeakerMode = set };
            Assert.That(options.SpeakerMode, Is.EqualTo(stored));
        }

        [TestCase(0, 6, 6)]
        [TestCase(0, 0, 2)]
        [TestCase(5, 2, 5)]
        public void SpeakerSlider_ShowsTheRunningModeUntilChosen(int stored, int running, int position)
        {
            Assert.That(PlayerOptions.SpeakerSliderPosition(stored, running), Is.EqualTo(position));
        }

        [Test]
        public void SpeakerNames_FollowG3Table()
        {
            Assert.That(PlayerOptions.SpeakerModeNames[1], Is.EqualTo("Mono"));
            Assert.That(PlayerOptions.SpeakerModeNames[2], Is.EqualTo("Stereo"));
            Assert.That(PlayerOptions.SpeakerModeName(2, "English"), Is.EqualTo("Stereo"));
            Assert.That(PlayerOptions.SpeakerModeName(2, "Russian"), Is.EqualTo("Стерео"));   // r31: localised (no G3 string)
            Assert.That(PlayerOptions.SpeakerModeName(6, "Russian"), Is.EqualTo("7.1"));
            Assert.That(PlayerOptions.SpeakerModeNames[7], Is.EqualTo("Dolby ProLogic"));
        }
    }
}
