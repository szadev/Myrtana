using G3.Player;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-101 "bow → sword reacts slowly": the switch timeline per weapon pair against G3 CP 1.75 (put-away pair, then the
    /// draw pair, no gap; <see cref="WeaponSwitchTimeline"/>) and when a weapon or quick-slot key acts (G3's one-slot action
    /// queue, read only by the loop states; <see cref="WeaponSwitchInput"/>). The motion lengths are checked against the
    /// game data in Tools/G3Tests HeroWeaponSwitchTimelineTests.
    /// </summary>
    public class WeaponSwitchTimingTests
    {
        const float Eps = 1e-4f;

        // ------------------------------------------------------------------ the timeline (G3 numbers)

        [TestCase("bow", "1H", 0.36f, 0.76f, 0.96f, 1.52f)]
        [TestCase("1H", "bow", 0.56f, 0.76f, 1.16f, 1.52f)]
        [TestCase("1H", "spell", 0.56f, 0.76f, 1.04f, 1.32f)]
        [TestCase("1H", "2H", 0.56f, 0.76f, 1.12f, 1.52f)]
        [TestCase("2H", "1H", 0.40f, 0.76f, 0.96f, 1.52f)]
        [TestCase("spell", "1H", 0.28f, 0.56f, 0.76f, 1.32f)]
        [TestCase("1H", "fist", 0.56f, 0.76f, 1.12f, 1.52f)]
        [TestCase("bow", "spell", 0.36f, 0.76f, 1.04f, 1.32f)]
        [TestCase("none", "1H", 0f, 0f, 0.20f, 0.76f)]
        public void G3Switch_PutAwayThenDraw_NoGap(string from, string to, float oldOff, float putAway, float newIn, float ready)
        {
            var t = WeaponSwitchTimeline.G3(from, to);
            Assert.That(t.Accepted, Is.EqualTo(0f));
            Assert.That(t.SheatheHand, Is.EqualTo(oldOff).Within(Eps), "the old weapon leaves the hand after the reversed End");
            Assert.That(t.SheatheEnd, Is.EqualTo(putAway).Within(Eps));
            Assert.That(t.DrawStart, Is.EqualTo(putAway).Within(Eps), "the draw starts in the tick the put-away ends");
            Assert.That(t.DrawHand, Is.EqualTo(newIn).Within(Eps), "the new weapon enters after the Begin");
            Assert.That(t.Ready, Is.EqualTo(ready).Within(Eps));
        }

        [Test]
        public void OursBefore_OneFrameBetweenPutAwayAndDraw_Now_None()
        {
            // Before: the waiting draw ran in PlayerCombat.Update, which runs before the animator that ended the sheathe.
            var before = WeaponSwitchTimeline.Build("bow", "1H", 0f, WeaponSwitchTimeline.Frame60);
            var g3 = WeaponSwitchTimeline.G3("bow", "1H");
            Assert.That(before.Ready - g3.Ready, Is.EqualTo(WeaponSwitchTimeline.Frame60).Within(Eps));
            Assert.That(WeaponSwitchTimeline.Build("bow", "1H", 0f, 0f).Ready, Is.EqualTo(g3.Ready).Within(Eps), "now: the draw starts at the sheathe's end");
            Assert.That(WeaponSwitchTimeline.Build("none", "1H", 0f, 1f).Ready, Is.EqualTo(0.76f).Within(Eps), "nothing to put away: no join");
        }

        // ------------------------------------------------------------------ when the key acts

        static WeaponSwitchState Idle => default;

        [Test]
        public void LoopStates_ActNow()
        {
            Assert.That(WeaponSwitchInput.Decide(Idle), Is.EqualTo(WeaponSwitchVerdict.Now), "PS_*_Loop / PS_Ranged_Walk_Loop");
            Assert.That(WeaponSwitchInput.Decide(new WeaponSwitchState { AimHeld = true }), Is.EqualTo(WeaponSwitchVerdict.EndAimThenNow),
                        "PS_Ranged_Aim_Loop calls OnPlayerUseQuickSlot too");
        }

        [Test]
        public void OtherStates_Wait()
        {
            foreach (var s in new[]
                     {
                         new WeaponSwitchState { MoveRuns = true }, new WeaponSwitchState { Reload = true },
                         new WeaponSwitchState { Shot = true }, new WeaponSwitchState { AimRaising = true },
                         new WeaponSwitchState { Cast = true }, new WeaponSwitchState { SwitchRuns = true },
                         new WeaponSwitchState { AimHeld = true, Shot = true },
                     })
                Assert.That(WeaponSwitchInput.Decide(s), Is.EqualTo(WeaponSwitchVerdict.Wait));
        }

        [Test]
        public void Queue_OneSlot_LatestKeyWins_ButtonPressClears()
        {
            var q = new WeaponSwitchQueue<string>();
            var reload = new WeaponSwitchState { Reload = true };
            Assert.That(q.Offer("sword", reload, 1.0), Is.EqualTo(WeaponSwitchVerdict.Wait));
            Assert.That(q.Offer("spell", reload, 1.1), Is.EqualTo(WeaponSwitchVerdict.Wait));
            Assert.That(q.Waiting, Is.EqualTo("spell"), "FUN_10007940: clear + add");
            Assert.That(q.Poll(reload, out _), Is.Null, "still reloading");
            Assert.That(q.Poll(Idle, out var v), Is.EqualTo("spell"));
            Assert.That(v, Is.EqualTo(WeaponSwitchVerdict.Now));
            Assert.That(q.Poll(Idle, out _), Is.Null, "taken once");
            q.Offer("sword", reload, 2.0);
            Assert.That(q.Clear(), Is.True, "a later attack press replaces it");
            Assert.That(q.Poll(Idle, out _), Is.Null);
            Assert.That(q.Offer("sword", Idle, 3.0), Is.EqualTo(WeaponSwitchVerdict.Now));
            Assert.That(q.Waiting, Is.Null, "acted at once: nothing waits");
        }

        /// <summary>
        /// Bow → sword pressed 0.5 s after a shot (G3: Aim_Recover 0.40 s, then the reload Bow_None Reload_Begin 0.28 +
        /// Bow_Arrow Reload_End 0.28): G3 takes the key at the reload's end (0.96 s) and the sword is ready at 2.48 s. Ours
        /// before: the key was refused during the reload — lost.
        /// </summary>
        [Test]
        public void BowToSword_PressedDuringTheReload_ActsAtTheReloadsEnd()
        {
            const float recoverEnd = 0.40f, reloadEnd = 0.96f, press = 0.5f;
            var q = new WeaponSwitchQueue<string>();
            float acted = -1f;
            for (int frame = 0; frame * WeaponSwitchTimeline.Frame60 < 3f && acted < 0; frame++)
            {
                float t = frame * WeaponSwitchTimeline.Frame60;
                var state = new WeaponSwitchState { Shot = t < recoverEnd, Reload = t >= recoverEnd && t < reloadEnd };
                if (t >= press && t < press + WeaponSwitchTimeline.Frame60 && q.Offer("sword", state, t) != WeaponSwitchVerdict.Wait) acted = t;
                else if (q.Poll(state, out _) != null) acted = t;
            }
            Assert.That(acted, Is.EqualTo(reloadEnd).Within(WeaponSwitchTimeline.Frame60 + Eps), "in the first frame after the reload");
            var g3 = WeaponSwitchTimeline.G3("bow", "1H", reloadEnd);
            Assert.That(g3.Ready, Is.EqualTo(2.48f).Within(Eps));
            var ours = WeaponSwitchTimeline.Build("bow", "1H", acted, 0f);
            Assert.That(ours.Ready - g3.Ready, Is.LessThanOrEqualTo(WeaponSwitchTimeline.Frame60 + Eps));
        }

        [Test]
        public void BowToSword_WhileTheAimIsHeld_ActsAtOnce()
        {
            var q = new WeaponSwitchQueue<string>();
            Assert.That(q.Offer("sword", new WeaponSwitchState { AimHeld = true }, 0), Is.EqualTo(WeaponSwitchVerdict.EndAimThenNow));
            Assert.That(WeaponSwitchTimeline.G3("bow", "1H").Ready, Is.EqualTo(1.52f).Within(Eps));
        }

        [Test]
        public void SwordToBow_PressedDuringAnAttack_ActsWhenTheMoveEnds()
        {
            var q = new WeaponSwitchQueue<string>();
            Assert.That(q.Offer("bow", new WeaponSwitchState { MoveRuns = true }, 0), Is.EqualTo(WeaponSwitchVerdict.Wait));
            Assert.That(q.Poll(new WeaponSwitchState { MoveRuns = true }, out _), Is.Null);
            Assert.That(q.Poll(Idle, out _), Is.EqualTo("bow"), "PS_Melee_Loop after the move (a Recover tail is the loop)");
        }
    }
}
