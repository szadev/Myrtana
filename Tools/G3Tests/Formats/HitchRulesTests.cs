using G3.Unity;
using NUnit.Framework;

namespace G3Tests.Formats
{
    /// <summary>fix-251: the always-on long-frame log (owner: the game froze for seconds, the overlay showed max 6872.8 ms).</summary>
    [TestFixture]
    public class HitchRulesTests
    {
        [Test]
        public void Classify_TellsBackgroundPausesFromFreezes()
        {
            Assert.That(HitchRules.Classify(16, false), Is.EqualTo(HitchKind.None));
            Assert.That(HitchRules.Classify(HitchRules.ThresholdMs, false), Is.EqualTo(HitchKind.None));
            Assert.That(HitchRules.Classify(6872.8, false), Is.EqualTo(HitchKind.Hitch));
            Assert.That(HitchRules.Classify(6872.8, true), Is.EqualTo(HitchKind.Background));
            Assert.That(HitchRules.Classify(double.NaN, false), Is.EqualTo(HitchKind.None));
        }

        [Test]
        public void RateLimit_PerWindow_AndReportsSuppressed()
        {
            var r = new HitchRules();
            for (int i = 0; i < HitchRules.LinesPerWindow; i++) Assert.That(r.Allow(1.0 + i * 0.1), Is.True);
            Assert.That(r.Allow(2.0), Is.False);
            Assert.That(r.Suppressed, Is.EqualTo(1));
            Assert.That(r.Allow(1.0 + HitchRules.WindowSeconds), Is.True, "a new window");
            string line = r.Line(HitchKind.Hitch, 900, "18:15:02", 10, 0, 1, 0, 0, 512, "ctx");
            Assert.That(line, Does.Contain("(1 earlier lines suppressed)"));
            Assert.That(r.Suppressed, Is.Zero);
        }

        [Test]
        public void Marks_CoverThePreviousAndTheCurrentFrame()
        {
            var r = new HitchRules();
            r.Mark("loaded Cell_12_11");
            r.EndFrame();                  // frame N ends; its length shows in frame N+1
            r.Mark("save quick");
            Assert.That(r.Marks, Is.EqualTo("loaded Cell_12_11; save quick"));
            r.EndFrame();
            r.EndFrame();
            Assert.That(r.Marks, Is.EqualTo("-"));
        }

        [Test]
        public void Line_NamesABackgroundPause()
        {
            var r = new HitchRules();
            string line = r.Line(HitchKind.Background, 6872.8, "18:17:40", 99, 6.8, 0, 0, 0, 300, null);
            Assert.That(line, Does.Contain("window in the background 6.8 s"));
            Assert.That(line, Does.StartWith("[G3Hitch] 18:17:40 frame 99: 6873 ms"));
        }
    }
}
