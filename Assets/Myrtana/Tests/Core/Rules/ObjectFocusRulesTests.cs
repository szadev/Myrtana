using System.Numerics;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-79: the "Focus decorative objects" option's object list and G3's focus-name placement.</summary>
    public sealed class ObjectFocusRulesTests
    {
        static InteractiveObjectDef Def(InteractiveKind kind, string useType, string useFunc = null) =>
            new InteractiveObjectDef("id", "G3_Object_Interact_" + useType, kind, useType, "x.xcmsh", null, null, null, 1, default,
                                     null, null, null, null, useFunc);

        [TestCase(InteractiveKind.Seat, "Stool")]
        [TestCase(InteractiveKind.Seat, "Bench")]
        [TestCase(InteractiveKind.Seat, "Throne")]
        [TestCase(InteractiveKind.Activity, "Waterpipe")]
        [TestCase(InteractiveKind.Activity, "SawLog")]
        [TestCase(InteractiveKind.Activity, "OrcDrum")]
        public void Decorative_DropOutOnlyWhenTheOptionIsOff(InteractiveKind kind, string useType)
        {
            var def = Def(kind, useType);
            Assert.That(ObjectFocusRules.IsDecorative(def), Is.True);
            Assert.That(ObjectFocusRules.Focusable(def, focusDecorative: true), Is.True, "original: every object");
            Assert.That(ObjectFocusRules.Focusable(def, focusDecorative: false), Is.False);
        }

        [TestCase(InteractiveKind.Chest, "Chest")]
        [TestCase(InteractiveKind.Door, "Door")]
        [TestCase(InteractiveKind.Bed, "Bed")]
        [TestCase(InteractiveKind.Station, "Anvil")]
        [TestCase(InteractiveKind.Station, "GrindStone")]
        [TestCase(InteractiveKind.Station, "Cauldron")]
        [TestCase(InteractiveKind.Station, "Alchemy")]
        [TestCase(InteractiveKind.Station, "Campfire")]
        [TestCase(InteractiveKind.Station, "Barbecue")]
        [TestCase(InteractiveKind.Station, "Bookstand")]
        [TestCase(InteractiveKind.Speaker, "Shrine")]
        [TestCase(InteractiveKind.Boulder, "OrcBoulder")]
        [TestCase(InteractiveKind.OreVein, "PickOre")]
        [TestCase(InteractiveKind.WaterBarrel, "WaterBarrel")]
        [TestCase(InteractiveKind.Treasure, "PirateTreasure")]
        [TestCase(InteractiveKind.Switch, "Lever")]
        public void Useful_AlwaysFocusable(InteractiveKind kind, string useType)
        {
            var def = Def(kind, useType);
            Assert.That(ObjectFocusRules.IsDecorative(def), Is.False);
            Assert.That(ObjectFocusRules.Focusable(def, focusDecorative: false), Is.True);
        }

        [Test]
        public void ScriptHook_KeepsASeatFocusable()
        {
            Assert.That(ObjectFocusRules.Focusable(Def(InteractiveKind.Seat, "Bench", useFunc: "SomeQuestHook"), false), Is.True);
        }

        [Test]
        public void FocusName_EntityPoint_IsPivotPlusUpTimesHeightTimesScale()
        {
            // CalcTextRect: local (0, boundary height, 0) through the world matrix (CP 1.75 Game.dll 0x10079870)
            var chest = FocusNamePlacement.EntityPoint(new Vector3(5, 2, 7), Vector3.UnitY, 1f, 0.89f);
            Assert.That(chest.Y, Is.EqualTo(2.89f).Within(1e-5));
            Assert.That(chest.X, Is.EqualTo(5f));
            var scaled = FocusNamePlacement.EntityPoint(Vector3.Zero, Vector3.UnitY * 3f, 1.5f, 0.66f);
            Assert.That(scaled.Y, Is.EqualTo(0.99f).Within(1e-5), "the world matrix scales the local point again");
            var tilted = FocusNamePlacement.EntityPoint(Vector3.Zero, Vector3.Normalize(new Vector3(1, 1, 0)), 1f, 1f);
            Assert.That(tilted.X, Is.EqualTo(tilted.Y).Within(1e-5), "follows the entity's up axis");
        }

        [Test]
        public void FocusName_ViewLift_Is10CmAlongTheCameraUp()
        {
            Assert.That(FocusNamePlacement.ViewLift, Is.EqualTo(0.1f));
            var lifted = FocusNamePlacement.Lift(new Vector3(0, 1, 0), new Vector3(0, 0.9f, 0.3f));
            Assert.That((lifted - new Vector3(0, 1, 0)).Length(), Is.EqualTo(0.1f).Within(1e-5));
            Assert.That(lifted.Z, Is.GreaterThan(0f));
        }
    }
}
