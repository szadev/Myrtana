using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-254: decorative plant meshes do not block the focus ray (owner: the CP's lurker druid stone and Akor's diary in
    /// the hollow rotten trunk of the Varant coast cave could not be taken).
    /// </summary>
    public sealed class FoliageFocusTests
    {
        [TestCase("G3_Object_Plant_RottenTree_02")]
        [TestCase("G3_Object_Plant_RottenTree_01")]
        [TestCase("G3_Object_Plant_RottenTree_Snow_02")]
        [TestCase("G3_Object_Plant_Fern_01")]
        [TestCase("G3_Object_Plant_Reed_01")]
        [TestCase("G3_Object_Plant_Herculesplant_01")]
        [TestCase("G3_Myrtana_Okara_Objects_Plants_01")]
        [TestCase("g3_object_caves_roots_01")]
        public void PlantMeshes_AreFoliage(string mesh) => Assert.That(FocusSight.IsFoliageMesh(mesh), Is.True);

        [TestCase("G3_Object_TreeStump_01")]
        [TestCase("G3_Object_Logs_02")]
        [TestCase("G3_Object_Branch_03")]
        [TestCase("G3_Object_Corpse_Skeleton_03")]
        [TestCase("G3_Varant_Dungeon_V04_S03_05")]
        [TestCase("G3_Object_Shelf_01")]
        [TestCase("G3_Item_Food_Fake_Plant_01")]
        [TestCase("")]
        [TestCase(null)]
        public void OtherMeshes_AreNot(string mesh) => Assert.That(FocusSight.IsFoliageMesh(mesh), Is.False);

        [Test]
        public void TrunkWallBetweenHeroAndDiary_DoesNotBlock()
        {
            // owner's cave: hero north of the trunk, 1.3 m from the diary; the trunk's collision tube is hit 0.4 m along the ray
            Assert.That(FocusSight.Blocks(FocusSightHit.Foliage, 0.4f, 1.6f), Is.False);
            Assert.That(FocusSight.Blocks(FocusSightHit.World, 0.4f, 1.6f), Is.True, "any other mesh there still blocks");
        }
    }
}
