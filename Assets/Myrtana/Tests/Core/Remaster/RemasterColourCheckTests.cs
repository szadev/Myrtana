using G3.Formats.Remaster;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>A/B colour fidelity check of the remaster test scene (Docs/remaster-pipeline.md §9.6).</summary>
    public class RemasterColourCheckTests
    {
        const int W = 40, H = 30;

        static byte[] Fill(byte r, byte g, byte b)
        {
            var img = new byte[W * H * 3];
            for (int i = 0; i < img.Length; i += 3) { img[i] = r; img[i + 1] = g; img[i + 2] = b; }
            return img;
        }

        /// <summary>Paints the "character" (a 20×20 square at 10,5) into a copy of the background.</summary>
        static byte[] Character(byte[] bg, byte r, byte g, byte b)
        {
            var img = (byte[])bg.Clone();
            for (int y = 5; y < 25; y++)
                for (int x = 10; x < 30; x++)
                {
                    int i = (y * W + x) * 3;
                    img[i] = r; img[i + 1] = g; img[i + 2] = b;
                }
            return img;
        }

        [Test]
        public void SameColourPassesAndIgnoresBackground()
        {
            var bg = Fill(80, 85, 95);
            var a = Character(bg, 160, 110, 80);
            var r = RemasterColourCheck.Compare("face", a, (byte[])a.Clone(), bg, W, H, 0, 0, W, H, maxDeltaE: 4);
            Assert.That(r.Pixels, Is.EqualTo(400));
            Assert.That(r.MeanA[0], Is.EqualTo(160).Within(1e-9));
            Assert.That(r.DeltaE, Is.EqualTo(0).Within(1e-9));
            Assert.That(r.Judged && r.Pass, Is.True);
        }

        [Test]
        public void BlueShiftFails()
        {
            var bg = Fill(80, 85, 95);
            var a = Character(bg, 70, 51, 37);   // r20 hero face A
            var b = Character(bg, 77, 63, 55);   // r20 hero face B (bluer, greyer): ΔE ≈ 7.2
            var r = RemasterColourCheck.Compare("face", a, b, bg, W, H, 0, 0, W, H);
            Assert.That(r.DeltaE, Is.EqualTo(7.2).Within(0.2));
            Assert.That(r.LabB[2] - r.LabA[2], Is.LessThan(-3));
            Assert.That(r.Pass, Is.False);
            StringAssert.Contains("FAIL", r.ToString());
        }

        [Test]
        public void SmallRegionIsSkipped()
        {
            var bg = Fill(80, 85, 95);
            var a = Character(bg, 160, 110, 80);
            var b = Character(bg, 60, 110, 200);
            var r = RemasterColourCheck.Compare("face", a, b, bg, W, H, 10, 5, 15, 10); // 25 px, clamped rect
            Assert.That(r.Pixels, Is.EqualTo(25));
            Assert.That(r.Judged, Is.False);
            Assert.That(r.Pass, Is.True);
            StringAssert.Contains("skipped", r.ToString());
        }

        [Test]
        public void LabOfWhiteAndGrey()
        {
            var white = RemasterColourCheck.SrgbToLab(255, 255, 255);
            Assert.That(white[0], Is.EqualTo(100).Within(0.05));
            Assert.That(white[1], Is.EqualTo(0).Within(0.05));
            Assert.That(white[2], Is.EqualTo(0).Within(0.05));
            var grey = RemasterColourCheck.SrgbToLab(119, 119, 119);
            Assert.That(grey[0], Is.EqualTo(50).Within(0.5));
        }
    }
}
