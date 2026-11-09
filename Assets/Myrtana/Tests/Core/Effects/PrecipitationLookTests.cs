using G3.Weather;
using NUnit.Framework;

namespace Myrtana.Tests.Effects
{
    /// <summary>fix-224: thin, velocity-length rain streaks, near fade and the pixel-coverage floor (owner: thick blobby rain).</summary>
    public class PrecipitationLookTests
    {
        // CP 1.75 WeatherStates.g3ws: Rainy size 1 cm, DirectionScale 14, 800..1000 cm/s; Snowy(Nordmar) 3 cm, 0, 80..100 cm/s
        static PrecipitationLook Rain => PrecipitationLook.For(1f, 14f, 800f, 1000f);
        static PrecipitationLook Snow => PrecipitationLook.For(3f, 0f, 80f, 100f);
        static readonly float Px1080 = PrecipitationLook.PixelsPerMeter(60f, 1080f);

        [Test]
        public void Rain_IsAThinStreak_WithG3LengthAtTheMeanSpeed()
        {
            var r = Rain;
            Assert.That(r.Streak, Is.True);
            Assert.That(r.Width, Is.EqualTo(0.0025f).Within(1e-5f));
            Assert.That(r.Length(9f), Is.EqualTo(0.14f).Within(1e-4f), "G3: size 1 cm x DirectionScale 14 at 9 m/s");
            Assert.That(r.Length(10f), Is.GreaterThan(r.Length(8f)), "length follows the fall speed");
            Assert.That(r.StreakSeconds, Is.LessThan(1f / 30f), "about one frame of motion");
        }

        [Test]
        public void Rain_IsAtMostTwoPixelsWide_WhereItIsVisible()
        {
            var r = Rain;
            for (float d = r.NearFadeStart; d <= 10f; d += 0.05f)
            {
                if (r.NearFade(d) < 0.5f) continue;   // mostly faded out
                PrecipitationLook.Coverage(r.Width, d, Px1080, r.MinPixels, out float drawn);
                float px = drawn * Px1080 / d;
                Assert.That(px, Is.LessThanOrEqualTo(2.0f + 1e-3f), $"at {d} m");
                Assert.That(px, Is.GreaterThanOrEqualTo(r.MinPixels - 1e-3f), $"at {d} m");
            }
        }

        [Test]
        public void SubPixelDrops_AreWidenedToOnePixel_AndFadeByCoverage()
        {
            float a = PrecipitationLook.Coverage(0.0025f, 5f, Px1080, 1f, out float drawn);
            Assert.That(drawn * Px1080 / 5f, Is.EqualTo(1f).Within(1e-3f));
            Assert.That(a, Is.EqualTo(0.0025f * Px1080 / 5f).Within(1e-3f));
            Assert.That(PrecipitationLook.Coverage(0.0025f, 8f, Px1080, 1f, out _), Is.LessThan(a), "farther = fainter");
            Assert.That(PrecipitationLook.Coverage(0.0025f, 0.5f, Px1080, 1f, out float near), Is.EqualTo(1f));
            Assert.That(near, Is.EqualTo(0.0025f), "wider than a pixel: drawn as it is");
        }

        [Test]
        public void NearFade_HidesDropsInFrontOfTheLens()
        {
            var r = Rain;
            Assert.That(r.NearFade(0.3f), Is.EqualTo(0f));
            Assert.That(r.NearFade(r.NearFadeStart), Is.EqualTo(0f));
            Assert.That(r.NearFade(2.5f), Is.EqualTo(1f));
            Assert.That(r.NearFade(1.4f), Is.InRange(0.01f, 0.99f));
            Assert.That(r.NearFadeEnd, Is.InRange(1f, 2.5f));
        }

        [Test]
        public void Snow_KeepsG3FlakeSize_AsABillboard()
        {
            var s = Snow;
            Assert.That(s.Streak, Is.False);
            Assert.That(s.Width, Is.EqualTo(0.03f).Within(1e-5f));
            Assert.That(s.Length(1f), Is.EqualTo(s.Width));
            Assert.That(s.NearFadeEnd, Is.LessThan(Rain.NearFadeEnd));
        }

        [Test]
        public void Rain_IsFainterThanSnow_AndSplashesFollowTheirDrops()
        {
            Assert.That(Rain.Opacity, Is.LessThan(Snow.Opacity));
            var sp = PrecipitationLook.Splash(Rain);
            Assert.That(sp.Streak, Is.False);
            Assert.That(sp.NearFadeStart, Is.EqualTo(Rain.NearFadeStart));
            Assert.That(sp.Opacity, Is.LessThanOrEqualTo(Rain.Opacity));
        }

        [Test]
        public void DropHalf_CoversHalfTheStreak()
        {
            var r = Rain;
            Assert.That(r.DropHalf(10f), Is.EqualTo(0.5f * r.Length(10f) + 0.05f).Within(1e-5f));
        }
    }
}
