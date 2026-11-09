using G3.Player;
using NUnit.Framework;
using L = G3.Player.CaveGuardBudget.Log;

namespace Myrtana.Tests
{
    /// <summary>fix-133: the cave guard's log is bounded per NPC and the guard gives up on a body that keeps going back
    /// (Editor.log: Goblin#97dd86cd rescued ~12 times from the same spot in the rock of Reddock cave V07_S04_05).</summary>
    public class CaveGuardBudgetTests
    {
        [Test]
        public void Rescue_IsLoggedOncePerWindow()
        {
            var b = new CaveGuardBudget();
            Assert.That(b.Rescued(0), Is.EqualTo(L.Once));
            Assert.That(b.Rescued(2), Is.EqualTo(L.None));
            Assert.That(b.Rescued(CaveGuardBudget.LogWindow + 3), Is.EqualTo(L.Once), "a new window logs again");
            Assert.That(b.Active, Is.True);
        }

        [Test]
        public void RepeatedRescues_InOneWindow_GiveUpWithOneWarning()
        {
            var b = new CaveGuardBudget();
            int warnings = 0;
            for (int i = 0; i < 12; i++)
                if (b.Rescued(i * 2) == L.GiveUp) warnings++;
            Assert.That(warnings, Is.EqualTo(1));
            Assert.That(b.Active, Is.False);
            Assert.That(b.Rescues, Is.EqualTo(CaveGuardBudget.MaxRescues + 1));
            Assert.That(b.Failed(), Is.EqualTo(L.None), "off: silent");
            b.Reset();
            Assert.That(b.Active, Is.True, "a new binding guards again");
            Assert.That(b.Rescued(100), Is.EqualTo(L.Once));
        }

        [Test]
        public void NoFloor_WarnsOnce_ThenGivesUpAfterMaxFailures()
        {
            var b = new CaveGuardBudget();
            Assert.That(b.Failed(), Is.EqualTo(L.Once));
            for (int i = 1; i < CaveGuardBudget.MaxFailures - 1; i++) Assert.That(b.Failed(), Is.EqualTo(L.None));
            Assert.That(b.Failed(), Is.EqualTo(L.GiveUp));
            Assert.That(b.Active, Is.False);
        }

        [Test]
        public void Rescue_ClearsFailures()
        {
            var b = new CaveGuardBudget();
            for (int i = 0; i < CaveGuardBudget.MaxFailures - 1; i++) b.Failed();
            b.Rescued(0);
            Assert.That(b.Failures, Is.EqualTo(0));
            Assert.That(b.Failed(), Is.EqualTo(L.Once), "a new run of failures warns again");
            Assert.That(b.Active, Is.True);
        }
    }
}
