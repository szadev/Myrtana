using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public class StatTests
    {
        // core.json "modifierOperations" on a vital of 100/200
        [TestCase(ModifierOp.AddToVal, 30, 130, 200)]
        [TestCase(ModifierOp.AddToVal, 500, 200, 200)]
        [TestCase(ModifierOp.AddToMax, 4, 104, 204)]
        [TestCase(ModifierOp.AddPercentageToVal, 50, 200, 200)]
        [TestCase(ModifierOp.AddPercentageToVal, 25, 150, 200)]
        [TestCase(ModifierOp.AddPercentageToMax, 10, 120, 220)]
        [TestCase(ModifierOp.SetVal, 10, 10, 200)]
        [TestCase(ModifierOp.SetMax, 250, 150, 250)]
        [TestCase(ModifierOp.SetMax, 50, 50, 50)]
        [TestCase(ModifierOp.SetValToPercentage, 30, 60, 200)]
        [TestCase(ModifierOp.SetMaxToPercentage, 150, 200, 300)]
        [TestCase(ModifierOp.SetValToMax, 0, 200, 200)]
        public void VitalOperations(ModifierOp op, int amount, int value, int max)
        {
            var result = ModifierMath.Apply(new StatValue(100, 200), op, amount, hasMaximum: true);
            Assert.That(result, Is.EqualTo(new StatValue(value, max)));
        }

        [TestCase(ModifierOp.AddToVal, 2, 102)]
        [TestCase(ModifierOp.SetVal, 7, 7)]
        [TestCase(ModifierOp.AddToMax, 7, 100)]
        public void PlainAttributes_SupportAddAndSetOnly(ModifierOp op, int amount, int value) =>
            Assert.That(ModifierMath.Apply(new StatValue(100), op, amount, hasMaximum: false).Value, Is.EqualTo(value));

        [Test]
        public void EquipmentBonus_RaisesAttributesAndVitalMaxima()
        {
            var stats = new StatBlock(id => id == "HP");
            stats.Set("STR", new StatValue(100));
            stats.Set("HP", new StatValue(200, 200));
            stats.SetBonus("STR", 10);
            stats.SetBonus("HP", 50);
            Assert.That(stats.Value("STR"), Is.EqualTo(110));
            Assert.That(stats.Maximum("HP"), Is.EqualTo(250));
            Assert.That(stats.Value("HP"), Is.EqualTo(200));

            stats.Apply("HP", ModifierOp.SetValToMax, 0);
            Assert.That(stats.Value("HP"), Is.EqualTo(250));
            stats.ClearBonuses();
            Assert.That(stats.Value("HP"), Is.EqualTo(200), "the value is capped to the maximum without the bonus");
            Assert.That(stats.Value("STR"), Is.EqualTo(100));
        }

        [Test]
        public void Balanced_PicksTheVariantOfTheOption()
        {
            var sp = new Balanced<int>(100, 150);
            Assert.That(sp.For(new RuleOptions(alternativeBalancing: true)), Is.EqualTo(150));
            Assert.That(sp.For(new RuleOptions(alternativeBalancing: false)), Is.EqualTo(100));
            Balanced<int> plain = 7;
            Assert.That(plain.For(RuleOptions.Default), Is.EqualTo(7));
        }
    }
}
