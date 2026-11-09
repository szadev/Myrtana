using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-254 follow-up: G3's item focus reach (PC_Hero NormalModeItemScanRange 300 cm, FocusCheck d² − h²).</summary>
    public sealed class ItemFocusTests
    {
        [Test]
        public void Range_IsG3Data300cm() => Assert.That(ItemFocus.ScanRangeM, Is.EqualTo(3f));

        [Test]
        public void PointItem_ReachesExactlyTheRange()
        {
            Assert.That(ItemFocus.ReachM(0f, 0f), Is.EqualTo(3f).Within(1e-5f));
            Assert.That(ItemFocus.InReach(2.99f, 0f, 0f), Is.True);
            Assert.That(ItemFocus.InReach(3.01f, 0f, 0f), Is.False);
        }

        [Test]
        public void TheOldPlaceholder_2p2m_WasShort()
        {
            // the diary 2.5 m from the hero: out of the old 2.2 m, in G3's reach
            Assert.That(ItemFocus.InReach(2.5f, 0.12f, 0.08f), Is.True);
        }

        [Test]
        public void LargerHorizontalHalfSide_ExtendsTheReach()
        {
            // a lying spear 2 m long: half side 1 m -> sqrt(9 + 1)
            Assert.That(ItemFocus.ReachM(1f, 0.05f), Is.EqualTo((float)System.Math.Sqrt(10)).Within(1e-5f));
            Assert.That(ItemFocus.ReachM(0.05f, 1f), Is.EqualTo((float)System.Math.Sqrt(10)).Within(1e-5f));
            Assert.That(ItemFocus.InReach(3.15f, 1f, 0.05f), Is.True);
            Assert.That(ItemFocus.InReach(3.2f, 1f, 0.05f), Is.False);
        }

        [Test]
        public void NegativeHalfSides_AreZero() => Assert.That(ItemFocus.ReachM(-1f, -2f), Is.EqualTo(3f).Within(1e-5f));
    }
}
