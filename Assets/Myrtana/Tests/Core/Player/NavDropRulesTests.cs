using G3.Player;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public class NavDropRulesTests
    {
        [Test]
        public void DropsAreHigherThanTheStepAndAtMostFourMetres()
        {
            Assert.That(NavDropRules.IsDrop(10f, 9.6f), Is.False, "a step the NavMesh climbs itself");
            Assert.That(NavDropRules.IsDrop(10f, 9.2f), Is.True, "80 cm ledge");
            Assert.That(NavDropRules.IsDrop(38.6f, 35.8f), Is.True, "Geldern terrace, 2.8 m");
            Assert.That(NavDropRules.IsDrop(10f, 6f), Is.True, "4 m");
            Assert.That(NavDropRules.IsDrop(10f, 5.9f), Is.False, "deeper than MaxDrop");
            Assert.That(NavDropRules.IsDrop(10f, 11f), Is.False, "up is never a drop");
        }

        [Test]
        public void FallStaysOnTheLedgeThenFallsToTheLanding()
        {
            Assert.That(NavDropRules.FallY(10f, 7f, 0f), Is.EqualTo(10f));
            Assert.That(NavDropRules.FallY(10f, 7f, 0.3f), Is.EqualTo(10f));
            Assert.That(NavDropRules.FallY(10f, 7f, 1f), Is.EqualTo(7f).Within(1e-4f));
            Assert.That(NavDropRules.FallY(10f, 7f, 2f), Is.EqualTo(7f).Within(1e-4f));
            float prev = 10f;
            for (float t = 0; t <= 1f; t += 0.05f)
            {
                float y = NavDropRules.FallY(10f, 7f, t);
                Assert.That(y, Is.LessThanOrEqualTo(prev + 1e-5f));
                prev = y;
            }
        }

        [Test]
        public void LinkMustImproveTheWay()
        {
            Assert.That(NavDropRules.Improves(true, 0f, 1f), Is.True);
            Assert.That(NavDropRules.Improves(false, 40f, 50f), Is.True);
            Assert.That(NavDropRules.Improves(false, 49f, 50f), Is.False);
        }

        [Test]
        public void IslandsGroupJoinedPoints()
        {
            var islands = new Islands(5);
            islands.Join(0, 2);
            islands.Join(3, 4);
            islands.Join(4, 2);
            var groups = islands.Groups();
            Assert.That(groups.Count, Is.EqualTo(2));
            Assert.That(groups[0], Is.EquivalentTo(new[] { 0, 2, 3, 4 }));
            Assert.That(groups[1], Is.EquivalentTo(new[] { 1 }));
        }
    }
}
