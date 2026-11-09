using Myrtana.Core;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>r31 D1: the tutorial box's pause must hold the game at speed 0 until the last owner lets go.</summary>
    public class PauseScaleTests
    {
        [Test]
        public void FirstOwnerStopsLastOwnerRestores()
        {
            var p = new PauseScale();
            object tip = new object(), intro = new object();
            Assert.AreEqual(0f, p.Set(intro, true, 1f));
            Assert.IsNull(p.Set(tip, true, 0f));       // nested: no change
            Assert.IsNull(p.Set(intro, false, 0f));    // the tip still holds
            Assert.IsTrue(p.IsPaused);
            Assert.AreEqual(1f, p.Set(tip, false, 0f));
            Assert.IsFalse(p.IsPaused);
        }

        [Test]
        public void KeepsTheSpeedFromBeforeThePause()
        {
            var p = new PauseScale();
            object a = new object();
            Assert.AreEqual(0f, p.Set(a, true, 2f));
            Assert.AreEqual(2f, p.Set(a, false, 0f));
            Assert.AreEqual(0f, p.Set(a, true, 0f));   // already 0 outside a pause: comes back as 1
            Assert.AreEqual(1f, p.Set(a, false, 0f));
        }

        [Test]
        public void GuardPutsTheScaleBackToZeroWhilePaused()
        {
            var p = new PauseScale();
            Assert.IsNull(p.Guard(1f));                // not paused: anybody's speed stands
            p.Set(new object(), true, 1f);
            Assert.AreEqual(0f, p.Guard(1f));          // a dev step wrote 1 under the open tip
            Assert.IsNull(p.Guard(0f));
        }

        [Test]
        public void NormalSpeedNeverUnpausesBehindAnOwner()
        {
            var p = new PauseScale();
            object tip = new object();
            p.Set(tip, true, 0.5f);
            Assert.IsNull(p.NormalSpeed());            // paused: kept for later
            Assert.AreEqual(1f, p.Set(tip, false, 0f));
            Assert.AreEqual(1f, p.NormalSpeed());      // running: at once
        }

        [Test]
        public void ClearRestoresOnlyWhenPaused()
        {
            var p = new PauseScale();
            Assert.IsNull(p.Clear());
            p.Set(new object(), true, 1f);
            Assert.AreEqual(1f, p.Clear());
            Assert.IsFalse(p.IsPaused);
        }
    }
}
