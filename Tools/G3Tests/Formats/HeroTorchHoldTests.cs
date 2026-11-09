using System;
using System.IO;
using System.Linq;
using G3.Formats.Animation;
using G3.Formats.Pak;
using G3.Player;
using NUnit.Framework;

namespace G3Tests.Formats
{
    /// <summary>
    /// fix-105: the torch's take / put-away pairs (G3 _AI_HoldLeftInventoryItem_Base: HoldLeft Begin + End of the Torch set,
    /// the full-body "N" and the moving "O" variant, empty right hand or a 1H weapon) from the installed game's motions are
    /// all far shorter than <see cref="HoldMotionRules.OverlayGiveUpSeconds"/>, after which HeroTorchView gives a running
    /// overlay up (the owner's Demo_2026-10-18c: an overlay that never started held the torch keys for good).
    /// </summary>
    [TestFixture]
    public class HeroTorchHoldTests
    {
        static G3FileSystem _fs;

        [OneTimeSetUp]
        public void Open()
        {
            string data = Environment.GetEnvironmentVariable("G3_DATA") ?? @"C:\Program Files (x86)\Steam\steamapps\common\Gothic 3\Data";
            if (!File.Exists(Path.Combine(data, "_compiledAnimation.pak"))) Assert.Ignore("Gothic 3 data not installed");
            _fs = new G3FileSystem(data, "_compiledAnimation");
        }

        [OneTimeTearDown]
        public void Close() => _fs?.Dispose();

        float? Length(string set, string phase, string variant)
        {
            string prefix = $"Hero_Stand_{set}_P0_HoldLeft_{phase}_{variant}_Fwd_00_%_00_";
            var f = _fs.Files.FirstOrDefault(x => Path.GetFileName(x.Path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                                                  x.Path.EndsWith(".xmot", StringComparison.OrdinalIgnoreCase));
            return f == null ? (float?)null : Xmot.Read(f.Read(), Path.GetFileNameWithoutExtension(f.Path)).Duration;
        }

        [Test]
        public void TorchHoldPairs_EndLongBeforeTheGiveUp()
        {
            int pairs = 0;
            foreach (var set in new[] { "Torch_None", "Torch_1H" })
                foreach (var variant in new[] { "N", "O" })
                {
                    var begin = Length(set, "Begin", variant);
                    var end = Length(set, "End", variant);
                    if (begin == null || end == null) continue;
                    float pair = begin.Value + end.Value;
                    TestContext.Out.WriteLine($"{set} {variant}: Begin {begin:0.000} + End {end:0.000} = {pair:0.000} s");
                    Assert.That(pair, Is.LessThan(HoldMotionRules.OverlayGiveUpSeconds / 2f), $"{set} {variant}");
                    Assert.That(HoldMotionRules.OverlayStuck(0f, pair + 0.25f), Is.False, $"{set} {variant} with a slow frame");
                    pairs++;
                }
            Assert.That(pairs, Is.GreaterThan(0), "no Torch HoldLeft motions in the game data");
        }
    }
}
