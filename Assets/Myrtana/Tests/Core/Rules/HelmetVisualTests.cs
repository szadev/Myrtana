using Myrtana.Core;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-220: worn helmets on Slot_Helmet, hair hidden under real helmets, kept under a crown.</summary>
    public class HelmetVisualTests
    {
        static ItemDef Item(string id, EquipSlot slot, string mesh) =>
            new ItemDef(id, new TextId("FO_" + id), default, ItemCategory.Armor, slot == EquipSlot.Helmet ? "Helmet" : "Body", slot, 100, 0, null,
                        mesh != null ? new ItemVisual(mesh, null, 0) : null, null, null, null, null, null, null, ItemFlags.None);

        [Test]
        public void MeshKey_IsTheHelmetsStaticMesh()
        {
            Assert.That(HelmetVisualRules.MeshKey(Item("It_Helmet_Horn_01", EquipSlot.Helmet, "G3_Armor_Helm_Myrtana_06.xcmsh")),
                        Is.EqualTo("G3_Armor_Helm_Myrtana_06.xcmsh"));
        }

        /// <summary>fix-232 [owner] "Show helmet": off draws no helmet mesh (the item stays worn), on draws it as before.</summary>
        [Test]
        public void ShownMeshKey_ShowHelmetOffDrawsNothing()
        {
            var horn = Item("It_Helmet_Horn_01", EquipSlot.Helmet, "G3_Armor_Helm_Myrtana_06.xcmsh");
            Assert.That(HelmetVisualRules.ShownMeshKey(horn, true), Is.EqualTo("G3_Armor_Helm_Myrtana_06.xcmsh"));
            Assert.That(HelmetVisualRules.ShownMeshKey(horn, false), Is.Null);
            Assert.That(HelmetVisualRules.ShownMeshKey(null, true), Is.Null);
            Assert.That(HelmetVisualRules.ShownMeshKey(Item("It_Shield", EquipSlot.Shield, "G3_Shield_01.xcmsh"), true), Is.Null);
        }

        [Test]
        public void MeshKey_NoneForOtherSlotsMissingMeshOrNoItem()
        {
            Assert.That(HelmetVisualRules.MeshKey(Item("It_Shield", EquipSlot.Shield, "G3_Shield_01.xcmsh")), Is.Null);
            Assert.That(HelmetVisualRules.MeshKey(Item("It_Helmet_X", EquipSlot.Helmet, null)), Is.Null);
            Assert.That(HelmetVisualRules.MeshKey(null), Is.Null);
        }

        [TestCase(0.077f, false)] // G3_Armor_Helmet_Crone_01: a ring on top of the head
        [TestCase(0.292f, true)]  // G3_Armor_Helm_Orient_04 (turbans, leather helmets)
        [TestCase(0.412f, true)]  // G3_Armor_Helm_Myrtana_06 (horned helmet)
        public void HidesHair_ByHelmetHeight(float height, bool hides)
        {
            Assert.That(HelmetVisualRules.HidesHair(height), Is.EqualTo(hides));
        }

        [TestCase("Slot_Helmet", "Slot_Helmet", true)]
        [TestCase("slot_helmet", "Slot_Helmet", true)]
        [TestCase("Slot_Helmet.001", "Slot_Helmet", true)]
        [TestCase("Slot_Hair", "Slot_Helmet", false)]
        [TestCase("Slot_HairBack", "Slot_Hair", false)]
        [TestCase(null, "Slot_Hair", false)]
        public void IsSlot_MatchesTheNodeAndImporterDuplicates(string node, string slot, bool expected)
        {
            Assert.That(HelmetVisualRules.IsSlot(node, slot), Is.EqualTo(expected));
        }
    }
}
