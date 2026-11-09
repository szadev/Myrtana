using G3.Formats.World;
using NUnit.Framework;

namespace G3Tests.Formats
{
    /// <summary>
    /// Collision rules shared by G3CollisionBuilder and `G3Cli collision audit` (collision agent): player-blocking shape
    /// groups, pre-scaled .xnvmsh names, and two-sided triangle lists (hut/house sub-meshes cooked facing inwards let
    /// the hero walk through walls and sink through floors with one-sided MeshColliders).
    /// </summary>
    [TestFixture]
    public class CollisionRulesTests
    {
        [Test]
        public void BlocksPlayer_StaticDynamicTrunkDoor_NotTriggersOrCamera()
        {
            foreach (int g in new[] { 0, 1, 2, 9, 20 })
                Assert.IsTrue(CollisionRules.BlocksPlayer(new CollisionShape { Group = g }), $"group {g}");
            foreach (int g in new[] { 6, 7, 11, 17, 18, 19 })
                Assert.IsFalse(CollisionRules.BlocksPlayer(new CollisionShape { Group = g }), $"group {g}");
            Assert.IsFalse(CollisionRules.BlocksPlayer(new CollisionShape { Group = 1, DisableCollision = true }));
            Assert.IsFalse(CollisionRules.BlocksPlayer(new CollisionShape { Group = 1, IsTrigger = true }));
        }

        [Test]
        public void HasPlayerCollision_EmptyShapeListMeansLazyMesh()
        {
            Assert.IsFalse(CollisionRules.HasPlayerCollision(new WorldEntity()));
            Assert.IsTrue(CollisionRules.HasPlayerCollision(new WorldEntity { CollisionShapes = new System.Collections.Generic.List<CollisionShape>() }));
        }

        [Test]
        public void IsPrescaled_PerAxisAndUniformSuffixes()
        {
            Assert.IsTrue(CollisionRules.IsPrescaled("G3_Myrtana_Ruins_Wall_01_COL_SCX_0_7954_SCY_1_0000_SCZ_1_0000.xnvmsh"));
            Assert.IsTrue(CollisionRules.IsPrescaled("G3_Myrtana_Objects_Stoneplateu_01_SC_1_2960.xnvmsh"));
            Assert.IsTrue(CollisionRules.IsPrescaled("G3_Myrtana_Objects_Stoneplateu_01_SC_1_2960"));
            Assert.IsFalse(CollisionRules.IsPrescaled("G3_Myrtana_Ruins_Wall_01_COL.xnvmsh"));
            Assert.IsFalse(CollisionRules.IsPrescaled("G3_Myrtana_House_Hut_02_COL.xnvmsh"));
        }

        [Test]
        public void IsWaterMaterial_ShaderOrG3WaterToken_NotAnyWaterSubstring()
        {
            Assert.IsTrue(CollisionRules.IsWaterMaterial("G3_Nature_Water_River_01_A", null));
            Assert.IsTrue(CollisionRules.IsWaterMaterial("G3_Myrtana_Ocean_01", "G3/Water"));
            Assert.IsTrue(CollisionRules.IsWaterMaterial("Fall", "G3/WaterFall"));
            // fix-83: the fishing house beams and the water lilies are solid.
            Assert.IsFalse(CollisionRules.IsWaterMaterial("G3_Architecture_Cutwater_01_A", "G3/Default"));
            Assert.IsFalse(CollisionRules.IsWaterMaterial("G3_Nature_Plant_WaterLily_01_A", "G3/Default"));
        }

        [Test]
        public void IsWaterSurface_OnlyWhenEveryDrawnMaterialIsWater()
        {
            // G3_Architecture_Fishinghouse_01 (the hut at the start beach): no water at all.
            Assert.IsFalse(CollisionRules.IsWaterSurface(new[] { ("G3_Architecture_Cutwater_01_A", "G3/Default"),
                ("G3_Architecture_Roofshingle_01_A", "G3/Default"), ("G3_Architecture_Wood_04_C", "G3/Default"), ("G3_Architecture_Woodwall_01_A", "G3/Default") }));
            // G3_Object_Interact_Barrel_01 / Fountain_01: a water part inside a solid body keeps the G3 collision.
            Assert.IsFalse(CollisionRules.IsWaterSurface(new[] { ("G3_Nature_Water_River_01_A", "G3/Water"), ("G3_Objects_Barrelmetal_01_A", "G3/Default") }));
            // River / ocean meshes: water only.
            Assert.IsTrue(CollisionRules.IsWaterSurface(new[] { ("G3_Nature_Water_River_01_A", "G3/Water") }));
            Assert.IsTrue(CollisionRules.IsWaterSurface(new (string, string)[] { (null, null), ("G3_Sea_01", "G3/Water") }));
            Assert.IsFalse(CollisionRules.IsWaterSurface(new (string, string)[0]));
        }

        [Test]
        public void TwoSided_AddsReversedCopy_DropsDegenerates()
        {
            var p = new float[] { 0, 0, 0, 0, 0, 1, 1, 0, 0, 2, 0, 0 };
            // (0,1,2) real; (0,0,1) repeated index; (0,2,3) collinear (zero area).
            var t = CollisionRules.TwoSided(p, new[] { 0, 1, 2, 0, 0, 1, 0, 2, 3 });
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 0, 2, 1 }, t);
        }
    }
}
