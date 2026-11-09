using System.Linq;
using Myrtana.Rules;
using Myrtana.Session;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-250 (owner: Fabio's locked door in Lago showed no focus name): door focus-name fallbacks.</summary>
    public sealed class DoorFocusNameTests
    {
        static InteractiveObjectDef Obj(string name, InteractiveKind kind) =>
            new InteractiveObjectDef("id-" + name, name, kind, kind == InteractiveKind.Door ? "Door" : "Chest", name + ".FXA",
                                     null, null, null, 1, default, null, null,
                                     kind == InteractiveKind.Door ? new DoorDef(true, null, null) : null, null);

        static string[] Ids(InteractiveObjectDef d) => HudFocus.FocusNameIds(d).Select(t => t.ToString()).ToArray();

        [Test]
        public void FabioDoor_FallsBackToTheGenericDoorText()
        {
            Assert.That(Ids(Obj("G3_Object_Interact_Animated_Door_Fabio", InteractiveKind.Door)),
                Is.EqualTo(new[] { "FO_G3_Object_Interact_Animated_Door_Fabio", HudFocus.GenericDoorText }));
        }

        [Test]
        public void TempleTester_TriesItsTemplateTextFirst()
        {
            Assert.That(Ids(Obj("MoraSul_TempleDoor_Tester_01", InteractiveKind.Door)),
                Is.EqualTo(new[] { "FO_MoraSul_TempleDoor_Tester_01", "FO_MoraSul_TempleDoor_Tester", HudFocus.GenericDoorText }));
        }

        [Test]
        public void OtherObjects_KeepG3sSingleText()
        {
            Assert.That(Ids(Obj("G3_Object_Treasure_01", InteractiveKind.Chest)), Is.EqualTo(new[] { "FO_G3_Object_Treasure_01" }));
        }

        [Test]
        public void PlacementSuffix()
        {
            Assert.That(HudFocus.WithoutPlacementSuffix("A_Tester_01"), Is.EqualTo("A_Tester"));
            Assert.That(HudFocus.WithoutPlacementSuffix("Door_Fabio"), Is.EqualTo("Door_Fabio"));
            Assert.That(HudFocus.WithoutPlacementSuffix("Door_"), Is.EqualTo("Door_"));
            Assert.That(HudFocus.WithoutPlacementSuffix("_01"), Is.EqualTo("_01"));
        }
    }
}
