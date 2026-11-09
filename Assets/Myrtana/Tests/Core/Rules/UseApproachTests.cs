using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-75: the walk to a used object (G3 PS_Interact → _AI_Goto_Ext) and its cancel (action 0x66).</summary>
    public class UseApproachTests
    {
        const double Dt = 1.0 / 60;

        [Test]
        public void WalksUntilClose_ThenArrives()
        {
            var a = new UseApproach(1.6f);
            float d = 3f;
            double t = 0;
            ApproachStep step;
            int frames = 0;
            while ((step = a.Next(t, d, false, false)) == ApproachStep.Walk)
            {
                d -= 1.6f * (float)Dt;
                t += Dt;
                frames++;
            }
            Assert.That(step, Is.EqualTo(ApproachStep.Arrived));
            Assert.That(frames * Dt, Is.EqualTo((3 - UseApproach.ArriveDistance) / 1.6).Within(0.05), "a walk at the walk speed");
        }

        [Test]
        public void WaitsUpToHalfASecond_WhileTheHeroStillMoves()
        {
            var a = new UseApproach(1.6f);
            Assert.That(a.Next(0, 2f, true, false), Is.EqualTo(ApproachStep.Settle));
            Assert.That(a.Next(0.3, 2f, true, false), Is.EqualTo(ApproachStep.Settle));
            Assert.That(a.Next(0.51, 2f, true, false), Is.EqualTo(ApproachStep.Walk), "G3 sAIWaitInstr 500 ms");
            var b = new UseApproach(1.6f);
            Assert.That(b.Next(0, 2f, false, false), Is.EqualTo(ApproachStep.Walk), "standing: walks at once");
        }

        [Test]
        public void AlreadyThere_ArrivesAtOnce()
        {
            Assert.That(new UseApproach(1.6f).Next(0, 0.05f, false, false), Is.EqualTo(ApproachStep.Arrived));
        }

        [Test]
        public void NoProgress_GivesUp()
        {
            var a = new UseApproach(1.6f);
            Assert.That(a.Next(0, 1f, false, false), Is.EqualTo(ApproachStep.Walk));
            Assert.That(a.Next(0.5, 0.98f, false, false), Is.EqualTo(ApproachStep.Walk));
            Assert.That(a.Next(0.7, 0.98f, false, false), Is.EqualTo(ApproachStep.GiveUp), "blocked (the object's collider): the rest is placed");
        }

        [Test]
        public void TimeIsUp_GivesUp()
        {
            var a = new UseApproach(1.6f);
            double max = a.MaxSeconds(2f);
            Assert.That(max, Is.EqualTo(2 / 1.6 * 2 + 1).Within(1e-6));
            float d = 2f;
            ApproachStep step = ApproachStep.Walk;
            for (double t = 0; t <= max + 0.5 && step == ApproachStep.Walk; t += 0.25)
            {
                step = a.Next(t, d, false, false);
                d -= 0.06f; // slow but steady progress
            }
            Assert.That(step, Is.EqualTo(ApproachStep.GiveUp));
        }

        [Test]
        public void SecondaryButton_Cancels_AtAnyStep()
        {
            var a = new UseApproach(1.6f);
            Assert.That(a.Next(0, 2f, true, true), Is.EqualTo(ApproachStep.Cancelled));
            var b = new UseApproach(1.6f);
            b.Next(0, 2f, false, false);
            Assert.That(b.Next(0.5, 1.2f, false, true), Is.EqualTo(ApproachStep.Cancelled));
        }
    }
}
