using System;
using System.Collections.Generic;
using Myrtana.Ui.Skin;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public class LoadingSlideShowTests
    {
        [Test]
        public void FullLoad_ShowsEverySlideOnce()
        {
            var show = new LoadingSlideShow(30, new Random(7));
            var seen = new List<int> { show.Current };
            for (int p = 1; p <= LoadingSlideShow.Range; p++)
                if (show.Advance(p / 100f)) seen.Add(show.Current);
            Assert.That(seen.Count, Is.EqualTo(30));
            Assert.That(new HashSet<int>(seen).Count, Is.EqualTo(30));
        }

        [Test]
        public void Progress_OnlyGrows()
        {
            var show = new LoadingSlideShow(30, new Random(1));
            show.Advance(0.5f);
            int slide = show.Current;
            Assert.That(show.Advance(0.2f), Is.False);
            Assert.That(show.Position, Is.EqualTo(50));
            Assert.That(show.Current, Is.EqualTo(slide));
        }

        [Test]
        public void SlideChangesEvery34Percent()
        {
            var show = new LoadingSlideShow(30, new Random(3));
            Assert.That(show.Advance(0.03f), Is.False, "3 % is still the first slide");
            Assert.That(show.Advance(0.04f), Is.True, "30·4/101 = 1");
        }

        [Test]
        public void NoSlides_GivesMinusOne()
        {
            var show = new LoadingSlideShow(0, new Random(1));
            Assert.That(show.Current, Is.EqualTo(-1));
            Assert.That(show.Advance(1f), Is.False);
            Assert.That(LoadingSlideShow.SlideName(7), Is.EqualTo("G3_HUD_Slide_07.tga"));
        }
    }
}
