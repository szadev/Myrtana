using G3.Unity;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-186: the relief's one-cell renderer waves are digested, big waves keep their own log line.</summary>
    public class WaveLogDigestTests
    {
        [Test]
        public void BigWaves_AreLoggedAtOnce()
        {
            var d = new WaveLogDigest();
            Assert.That(d.Finished("world remaster", 6239, 6238, "world remaster: 6239 items", 1f), Is.EqualTo("world remaster: 6239 items"));
            Assert.That(d.PendingWaves, Is.Zero);
        }

        [Test]
        public void SmallWaves_OneDigestPerPeriod()
        {
            var d = new WaveLogDigest();
            int lines = 0;
            // ~470 relief waves of 2 cells in 60 s (a start with the relief on)
            for (int i = 0; i < 470; i++)
                if (d.Finished("terrain relief", 2, 2, "terrain relief: 2 items", i * 60f / 470) != null) lines++;
            Assert.That(lines, Is.EqualTo(1), "one digest after 30 s, the rest waits");
            string last = d.Digest(60f);
            Assert.That(last, Does.Contain("terrain relief"));
            Assert.That(d.PendingWaves, Is.Zero);
            Assert.That(d.Digest(61f), Is.Null, "nothing pending");
        }
    }
}
