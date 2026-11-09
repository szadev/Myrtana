using Myrtana.Core;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public class WorldHoldTests
    {
        [Test]
        public void HoldSetNestsAndReportsOnlyChanges()
        {
            var set = new HoldSet();
            object a = new object(), b = new object();
            Assert.IsTrue(set.Set(a, true));
            Assert.IsFalse(set.Set(a, true));      // twice: no change
            Assert.IsFalse(set.Set(b, true));
            Assert.IsFalse(set.Set(a, false));     // b still holds
            Assert.IsTrue(set.IsHeld);
            Assert.IsFalse(set.Set(a, false));     // not in the set
            Assert.IsTrue(set.Set(b, false));
            Assert.IsFalse(set.IsHeld);
        }

        [Test]
        public void SignalsBeginAndEndOnce()
        {
            var hold = new WorldHold();
            Assert.AreEqual(WorldHoldChange.Began, hold.Observe(LoadingSignal.StartupLoading | LoadingSignal.LoadingScreen, 1));
            Assert.AreEqual(WorldHoldChange.None, hold.Observe(LoadingSignal.LoadingScreen, 2));   // one reason left
            Assert.AreEqual(WorldHoldChange.None, hold.Observe(LoadingSignal.FlowLoading, 3));     // handed over, no gap
            Assert.AreEqual(WorldHoldChange.Ended, hold.Observe(LoadingSignal.None, 4.5));
            Assert.AreEqual(3.5, hold.LastDuration, 1e-9);
            Assert.AreEqual(WorldHoldChange.None, hold.Observe(LoadingSignal.None, 5));
        }

        [Test]
        public void OwnersAndSignalsNest()
        {
            var hold = new WorldHold();
            object load = new object();
            Assert.AreEqual(WorldHoldChange.Began, hold.Acquire(load, 0));
            Assert.AreEqual(WorldHoldChange.None, hold.Observe(LoadingSignal.LoadingScreen, 1));
            Assert.AreEqual(WorldHoldChange.None, hold.Release(load, 2));      // the screen still holds
            Assert.IsTrue(hold.IsHeld);
            Assert.AreEqual(WorldHoldChange.None, hold.Acquire(load, 3));
            Assert.AreEqual(WorldHoldChange.None, hold.Observe(LoadingSignal.None, 4)); // the owner still holds
            Assert.AreEqual(WorldHoldChange.Ended, hold.Release(load, 5));
            Assert.AreEqual(5, hold.LastDuration, 1e-9);
        }

        [Test]
        public void ReleaseAllEndsEverything()
        {
            var hold = new WorldHold();
            hold.Acquire(new object(), 0);
            hold.Observe(LoadingSignal.StreamerWait, 0);
            Assert.AreEqual(WorldHoldChange.Ended, hold.ReleaseAll(1));
            Assert.IsFalse(hold.IsHeld);
            Assert.AreEqual(WorldHoldChange.None, hold.ReleaseAll(2));
        }

        [Test]
        public void Title_StreamerSelfHoldIsNoLoad()
        {
            // fix-98: at the title no cells stream, so the streamer holds the scene's hero for ever; not a load
            Assert.AreEqual(LoadingSignal.None, LoadingSignals.Of(false, false, streamerWait: true, false, atTitle: true));
            Assert.AreEqual(LoadingSignal.None, LoadingSignals.Of(startupLoading: true, false, false, false, atTitle: true));
            // a load from the title (loading screen, flow loading) still counts
            Assert.AreEqual(LoadingSignal.LoadingScreen | LoadingSignal.FlowLoading,
                            LoadingSignals.Of(false, true, true, true, atTitle: true));
            // in a game the streamer's wait is a load
            Assert.AreEqual(LoadingSignal.StreamerWait | LoadingSignal.StartupLoading,
                            LoadingSignals.Of(true, false, true, false, atTitle: false));
        }
    }
}
