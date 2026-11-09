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
    /// fix-101 "switching from the bow to the sword reacts slowly": the switch timeline from the installed game's motions.
    /// G3 CP 1.75 <c>_AI_HoldInventoryItems</c> (0x10043720) runs the put-away of the held hands to its end
    /// (<c>_AI_HoldLeftInventoryItem_Base(-1)</c> for a bow, <c>_AI_HoldInventoryItems_Base(-1, -1)</c> for both hands, …)
    /// and then the draw of the new stacks; each Base plays two motions around HoldStacks, each to its end
    /// (<c>sAIPlayAniInstr</c>). So bow → sword = bow HoldLeft End+Begin backwards, then 1H HoldRight Begin+End. The
    /// <see cref="WeaponSwitchTimeline"/> sums these lengths exactly; this checks the motion data and prints the table.
    /// </summary>
    [TestFixture]
    public class HeroWeaponSwitchTimelineTests
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

        float Length(string set, string hold, string phase, string variant)
        {
            string prefix = $"Hero_Stand_{set}_P0_{hold}_{phase}_{variant}_Fwd_00_%_00_";
            var f = _fs.Files.FirstOrDefault(x => Path.GetFileName(x.Path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                                                  x.Path.EndsWith(".xmot", StringComparison.OrdinalIgnoreCase));
            Assume.That(f, Is.Not.Null, prefix);
            return Xmot.Read(f.Read(), Path.GetFileNameWithoutExtension(f.Path)).Duration;
        }

        HoldPairLength Pair(string set, string hold, string variant) =>
            new HoldPairLength(Length(set, hold, "Begin", variant), Length(set, hold, "End", variant));

        static readonly (string name, string set, string hold)[] Hands =
        {
            ("bow", "Bow_None", "HoldLeft"), ("crossbow", "CrossBow_None", "HoldLeft"), ("1H", "None_1H", "HoldRight"),
            ("2H", "None_2H", "HoldRight"), ("staff", "None_Staff", "HoldRight"), ("fist", "None_Fist", "HoldRight"),
            ("spell", "None_Cast", "HoldRight"),
        };

        [Test]
        public void HoldPairs_MatchTheTimelineTable()
        {
            foreach (var (name, set, hold) in Hands)
                foreach (var variant in new[] { "N", "O" })
                {
                    var data = Pair(set, hold, variant);
                    var table = WeaponSwitchTimeline.Pair(name);
                    TestContext.Progress.WriteLine($"{name,-8} {variant}: Begin {data.Begin:F3} s, End {data.End:F3} s (table {table.Begin:F3} + {table.End:F3})");
                    Assert.That(data.Total, Is.EqualTo(table.Total).Within(0.02f), $"{name} {variant}: the pair's length");
                    if (variant == "O") continue; // the overlays split some pairs differently (1H 0.36 + 0.40)
                    Assert.That(data.Begin, Is.EqualTo(table.Begin).Within(0.02f), $"{name} {variant} Begin");
                    Assert.That(data.End, Is.EqualTo(table.End).Within(0.02f), $"{name} {variant} End");
                }
        }

        bool Has(string prefix) => _fs.Files.Any(x => Path.GetFileName(x.Path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                                                       x.Path.EndsWith(".xmot", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// fix-101 (owner: killed while drawing the bow, the hero stood under "Конец"): the hero has no death motion with a
        /// bow or crossbow in the hand, only the empty hands' one. G3 Kill → DropHandItems → HoldStacks(-1, -1) empties the
        /// hands first; ours now too (CombatFighter.Die), so the None_None LieDead plays.
        /// </summary>
        [Test]
        public void Death_OnlyWithEmptyHands()
        {
            Assert.That(Has("Hero_Stand_None_None_P0_LieDead_Begin_N_Fwd_"), Is.True);
            Assert.That(Has("Hero_LieDead_None_None_P0_Ambient_Loop_N_Fwd_"), Is.True);
            foreach (var set in new[] { "Bow_None", "Bow_Arrow", "CrossBow_None", "CrossBow_Bolt" })
            {
                Assert.That(Has($"Hero_Stand_{set}_P0_LieDead_Begin_"), Is.False, set);
                Assert.That(Has($"Hero_LieDead_{set}_P0_Ambient_Loop_"), Is.False, set);
            }
        }

        [TestCase("bow", "1H")]
        [TestCase("1H", "bow")]
        [TestCase("1H", "spell")]
        [TestCase("1H", "2H")]
        [TestCase("2H", "1H")]
        [TestCase("spell", "1H")]
        [TestCase("1H", "fist")]
        [TestCase("bow", "spell")]
        public void Switch_PrintsTheG3Timeline(string from, string to)
        {
            var t = WeaponSwitchTimeline.G3(from, to);
            TestContext.Progress.WriteLine($"{from} -> {to}: {t}");
            Assert.That(t.SheatheEnd, Is.EqualTo(t.SheatheHand + WeaponSwitchTimeline.Pair(from).Begin).Within(1e-4f), "the put-away: End backwards, then Begin backwards");
            Assert.That(t.DrawStart, Is.EqualTo(t.SheatheEnd), "G3 draws in the tick the put-away ended");
            Assert.That(t.Ready, Is.EqualTo(t.SheatheEnd + WeaponSwitchTimeline.Pair(to).Total).Within(1e-4f));
        }
    }
}
