using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-139: G3 _AI_Action / _AI_LoopAction, the tool works of ore veins and treasure spots (command time, not a count).</summary>
    public sealed class TimedActionTests
    {
        [Test]
        public void Window_starts_at_the_truncated_state_time_and_lasts_the_command_seconds()
        {
            var a = new TimedAction(0.8f, 10);
            Assert.That(a.Start, Is.EqualTo(0), "_ftol of the float state time");
            Assert.That(a.Deadline, Is.EqualTo(10));
            Assert.That(a.PlaysAgain(8.8f), Is.True);
            Assert.That(a.PlaysAgain(10f), Is.True, "GetStateTime <= start + duration");
            Assert.That(a.PlaysAgain(10.01f), Is.False);
            Assert.That(new TimedAction(1.9f, 10).Deadline, Is.EqualTo(11));
        }

        [Test]
        public void Hero_pickaxe_strikes_five_times_and_the_shovel_digs_four()
        {
            // Hero_Stand_2H_None_P0_PickOre: Begin 0.8 s, Loop 2.0 s; Hero_Stand_Staff_None_P0_DigGround: Begin 0.8 s, Loop 2.4 s
            Assert.That(TimedAction.Cycles(0.8f, 2.0f, ObjectUseRules.Default.WorkSeconds), Is.EqualTo(5));
            Assert.That(TimedAction.Cycles(0.8f, 2.4f, ObjectUseRules.Default.WorkSeconds), Is.EqualTo(4));
        }

        [Test]
        public void Strikes_do_not_depend_on_a_late_start_within_the_second()
        {
            // the window starts at the whole second, so a later Begin end leaves the count unchanged
            foreach (float begin in new[] { 0.1f, 0.5f, 0.8f, 0.99f, 1.3f, 2.7f })
                Assert.That(TimedAction.Cycles(begin, 2.0f, 10), Is.EqualTo(5), begin.ToString());
        }

        [Test]
        public void Ten_is_a_command_time_not_a_loop_count()
        {
            Assert.That(ObjectUseRules.Default.WorkSeconds, Is.EqualTo(10), "PS_Interact_PickOre: _AI_Action(self, 0x62, 10)");
            Assert.That(TimedAction.Cycles(0.8f, 2.0f, 10), Is.LessThan(10), "fix-139: before, the loop played 10 times (20+ s)");
        }

        [Test]
        public void Ambient_variant_one_cycle_in_ten()
        {
            Assert.That(TimedAction.PicksAmbient(0), Is.True);
            Assert.That(TimedAction.PicksAmbient(9), Is.True);
            Assert.That(TimedAction.PicksAmbient(10), Is.False);
            Assert.That(TimedAction.PicksAmbient(99), Is.False);
        }
    }
}
