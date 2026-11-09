using System.Collections.Generic;
using G3.Player;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-163 (owner, Vengard yard: a body half in the ground): station slots stand on the floor under them, sunk bodies are
    /// lifted onto the floor directly above their feet — never onto a table or a storey above, never down.
    /// </summary>
    public class BodySinkRulesTests
    {
        static List<NpcGroundRules.Surface> Column(params float[] ys)
        {
            var l = new List<NpcGroundRules.Surface>();
            foreach (var y in ys) l.Add(new NpcGroundRules.Surface(y, 5f));
            return l;
        }

        [Test]
        public void BodyHalfInTheYardIsLifted()
        {
            // Orc_Raider_Scout_04#5f753adf: origin 5.92, landscape 6.51 (owner quick.sav)
            float lift = BodySinkRules.SinkLift(5.92f, Column(6.51f), out int i);
            Assert.That(lift, Is.EqualTo(0.59f).Within(1e-4f));
            Assert.That(i, Is.EqualTo(0));
        }

        [Test]
        public void SlightDipIsNotLifted() =>
            Assert.That(BodySinkRules.SinkLift(6.30f, Column(6.51f), out _), Is.EqualTo(0f));

        [Test]
        public void StandingOnTheFloorUnderATableIsNotLifted() =>
            // feet on the floor (0.0), a table top 0.75 m over them
            Assert.That(BodySinkRules.SinkLift(0.02f, Column(0.75f, 0f), out _), Is.EqualTo(0f));

        [Test]
        public void StoreyOrRoofFarAboveIsNotTheBodysFloor() =>
            // sunk 2 m under a floor: another storey or a roof, not lifted (the cave and placement rules own that)
            Assert.That(BodySinkRules.SinkLift(0f, Column(2f), out _), Is.EqualTo(0f));

        [Test]
        public void FloatingBodyIsNeverLowered()
        {
            Assert.That(BodySinkRules.SinkLift(1f, Column(0f), out _), Is.EqualTo(0f));
            Assert.That(BodySinkRules.Gap(1f, Column(0f), out _), Is.EqualTo(-1f).Within(1e-4f));
        }

        [Test]
        public void SteepFaceIsNoFloor()
        {
            var c = new List<NpcGroundRules.Surface> { new NpcGroundRules.Surface(0.5f, 80f) };
            Assert.That(BodySinkRules.SinkLift(0f, c, out _), Is.EqualTo(0f));
            Assert.That(float.IsNaN(BodySinkRules.Gap(0f, c, out int i)) && i < 0, Is.True);
        }

        [Test]
        public void SunkUnderAWoodenFloorOverTheGroundTakesTheFloorAbove()
        {
            // slot 0.4 m under a house floor, landscape 0.3 m below the slot: the body is in the house floor
            Assert.That(BodySinkRules.SinkLift(0f, Column(0.4f, -0.3f), out int i), Is.EqualTo(0.4f).Within(1e-4f));
            Assert.That(i, Is.EqualTo(0));
        }

        [Test]
        public void StationSlotStandsOnTheHighestFloorWithinAStep()
        {
            Assert.That(BodySinkRules.StationFloor(0f, Column(0.4f, -0.3f), 0.35f, out int i), Is.True);
            Assert.That(i, Is.EqualTo(0), "the house floor 0.4 m over the slot, not the ground under it");
            Assert.That(BodySinkRules.StationFloor(0f, Column(-0.2f), 0.35f, out i), Is.True, "a slot in the air comes down a step");
            Assert.That(BodySinkRules.StationFloor(0f, Column(3f, -2f), 0.35f, out _), Is.False, "nothing within a step: keeps its height");
        }

        [TestCase("Bed", true)]
        [TestCase("bench", true)]
        [TestCase("Stool", true)]
        [TestCase("Throne", true)]
        [TestCase("SitGround", false)]
        [TestCase("SleepGround", false)]
        [TestCase(null, false)]
        public void FurnitureKeepsItsSlotHeight(string interaction, bool furniture) =>
            Assert.That(BodySinkRules.OnFurniture(interaction), Is.EqualTo(furniture));

        [Test]
        public void CorpseLiftOnlyWhenSunkDeeperThanTheTolerance()
        {
            Assert.That(BodySinkRules.CorpseLift(-0.1f), Is.EqualTo(0f));
            Assert.That(BodySinkRules.CorpseLift(-0.6f), Is.EqualTo(0.6f).Within(1e-4f));
            Assert.That(BodySinkRules.CorpseLift(-5f, 2f), Is.EqualTo(2f));
            Assert.That(BodySinkRules.CorpseLift(float.NaN), Is.EqualTo(0f));
        }

        [Test]
        public void LogIsRateLimited()
        {
            int count = 0;
            float window = -100f;
            int written = 0;
            for (int k = 0; k < 20; k++) if (BodySinkRules.MayLog(1f, ref count, ref window, burst: 8, window: 10f)) written++;
            Assert.That(written, Is.EqualTo(8));
            Assert.That(BodySinkRules.MayLog(12f, ref count, ref window, burst: 8, window: 10f), Is.True, "a new window");
        }
    }
}
