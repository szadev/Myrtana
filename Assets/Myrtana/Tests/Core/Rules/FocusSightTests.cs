using System.Numerics;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-104: G3's focus line of sight (gCFocus_PS::FocusCheck → eCEntity::IsFreeLOS).</summary>
    public sealed class FocusSightTests
    {
        [Test]
        public void Point_LowBox_IsThreeQuartersUp()
        {
            // a man: 0.8 x 1.8 x 0.8 m on the ground -> 1.35 m (centre 0.9 + a quarter of 1.8)
            var p = FocusSight.Point(new Vector3(-0.4f, 0f, -0.4f), new Vector3(0.4f, 1.8f, 0.4f));
            Assert.That(p.X, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(p.Y, Is.EqualTo(1.35f).Within(1e-5f));
            Assert.That(p.Z, Is.EqualTo(0f).Within(1e-5f));
        }

        [Test]
        public void Point_TallBox_IsTheCentre()
        {
            // 350 cm and more: the centre (G3 compares the height with 350)
            var p = FocusSight.Point(new Vector3(0f, 10f, 0f), new Vector3(2f, 14f, 2f));
            Assert.That(p.Y, Is.EqualTo(12f).Within(1e-5f));
            Assert.That(FocusSight.Point(Vector3.Zero, new Vector3(1f, 3.5f, 1f)).Y, Is.EqualTo(1.75f).Within(1e-5f));
            Assert.That(FocusSight.Point(Vector3.Zero, new Vector3(1f, 3.4f, 1f)).Y, Is.EqualTo(1.7f + 0.85f).Within(1e-5f));
        }

        [Test]
        public void Point_NoBox_IsThePosition()
        {
            var p = FocusSight.Point(new Vector3(1f, 2f, 3f));
            Assert.That(Vector3.Distance(p, new Vector3(1f, 2f, 3f)), Is.LessThan(0.001f));
        }

        [Test]
        public void HeroUpstairs_NpcBelow_RayThroughTheFloorBlocks()
        {
            // hero on a floor 3 m up, NPC 2 m away below: the floor slab is hit well before the NPC's point
            var hero = FocusSight.Point(new Vector3(-0.4f, 3f, -0.4f), new Vector3(0.4f, 4.8f, 0.4f));
            var npc = FocusSight.Point(new Vector3(1.6f, 0f, -0.4f), new Vector3(2.4f, 1.8f, 0.4f));
            float length = Vector3.Distance(hero, npc);
            float floorHit = length * (hero.Y - 3f) / (hero.Y - npc.Y); // where the ray crosses y = 3
            Assert.That(FocusSight.Blocks(FocusSightHit.World, floorHit, length), Is.True);
        }

        [TestCase(FocusSightHit.Self)]
        [TestCase(FocusSightHit.Target)]
        [TestCase(FocusSightHit.TargetGeometry)]
        [TestCase(FocusSightHit.Character)]
        [TestCase(FocusSightHit.Item)]
        [TestCase(FocusSightHit.Foliage)]
        public void OnlyWorldGeometryBlocks(FocusSightHit hit)
        {
            Assert.That(FocusSight.Blocks(hit, 1f, 3f), Is.False);
            Assert.That(FocusSight.Blocks(FocusSightHit.World, 1f, 3f), Is.True);
        }

        [Test]
        public void HitAtTheEndOfTheRay_IsTheTarget()
        {
            // a point sunk a few cm into the ground or its own mesh
            Assert.That(FocusSight.Blocks(FocusSightHit.World, 2.95f, 3f), Is.False);
            Assert.That(FocusSight.Blocks(FocusSightHit.World, 2.85f, 3f), Is.True);
        }

        [Test]
        public void CellObject_FocusPointInsideItsBase_IsNotBlockedByIt()
        {
            Assert.That(FocusSight.Blocks(FocusSightHit.World, 2.7f, 3f, FocusSight.CellObjectEndToleranceM), Is.False);
            Assert.That(FocusSight.Blocks(FocusSightHit.World, 2.5f, 3f, FocusSight.CellObjectEndToleranceM), Is.True);
        }

        [Test]
        public void OwnGeometry_ByMeshPivot()
        {
            // a bed of the cell scene: its collider's mesh sits at the bed's pivot; the house around it does not
            var bed = new Vector3(100f, 20f, 50f);
            Assert.That(FocusSight.IsOwnGeometry(bed + new Vector3(0.05f, 0f, -0.05f), bed), Is.True);
            Assert.That(FocusSight.IsOwnGeometry(bed + new Vector3(3f, -1f, 2f), bed), Is.False);
        }

        // fix-105: the r40 cases (editor measurements, Unity metres)

        [Test]
        public void FaringHearth_ItsOwnMeshInFront_DoesNotBlock()
        {
            // G3_Object_Fireplace_04 in a Faring house: pivot at the back wall, the stone reaches 1.4 m out; the hero 1.9 m in
            // front sees it through its own mesh (the collider's object sits at the pivot), not through the house's
            var hearth = new Vector3(566.65f, 58.75f, 566.12f);
            Assert.That(FocusSight.IsOwnGeometry(new Vector3(566.65f, 58.75f, 566.12f), hearth), Is.True);
            Assert.That(FocusSight.IsOwnGeometry(new Vector3(556.96f, 58.75f, 563.47f), hearth), Is.False, "House_2_Story_02");
            Assert.That(FocusSight.IsOwnGeometry(new Vector3(566.81f, 60.76f, 566.18f), hearth), Is.False, "the hood is its own entity");
            Assert.That(FocusSight.Blocks(FocusSightHit.TargetGeometry, 0.4f, 2.2f, FocusSight.CellObjectEndToleranceM), Is.False);
        }

        [Test]
        public void SignpostBoard_PointIsOnTheBoard_NotHalfAMetreOverItsPivot()
        {
            // Sign_Faring (Cell_18_18): its own box collider 0.80 x 0.26 x 1.07 m centred on the pivot at y 56.21 -> G3 point
            // 56.21 + 0.26 / 4 (the old focus point pivot + 0.5 m lay over the board)
            var p = FocusSight.Point(new Vector3(349.67f, 56.08f, 409.44f), new Vector3(350.47f, 56.34f, 410.51f));
            Assert.That(p.Y, Is.EqualTo(56.275f).Within(1e-3f));
            Assert.That(p.X, Is.EqualTo(350.07f).Within(1e-3f));
        }

        [Test]
        public void HangedCorpseBeforeTheBoard_Blocks()
        {
            // G3_Object_Corpse_Hanged_01 hangs right in front of that board (its own entity with collision): hit 0.38 m before
            // the end of a 2.31 m ray -> no focus from the front (G3 IsFreeLOS traces all shapes, NX_ALL_SHAPES 0x1f)
            Assert.That(FocusSight.Blocks(FocusSightHit.World, 1.93f, 2.31f, FocusSight.CellObjectEndToleranceM), Is.True);
        }

        [Test]
        public void HoldsItem_TheShelfBoxAroundAnItem_DoesNotBlock()
        {
            // fix-114: Kap Dun shed, G3_Object_Shelf_01 is one 1.08 x 2.26 x 1.46 m box with the teleport stone inside it
            Assert.That(FocusSight.HoldsItem(solidVolume: true, containsCentre: true, gapM: 0f, supportSizeM: 2.26f), Is.True);
            Assert.That(FocusSight.Blocks(FocusSightHit.Support, 1f, 2f), Is.False, "a support hit never blocks");
            // an item in a rock's hollow inside the rock's large convex hull
            Assert.That(FocusSight.HoldsItem(true, true, 0f, 11f), Is.True);
        }

        [Test]
        public void HoldsItem_FurnitureTouchingTheItem_Holds_ButNotABuildingOrASurface()
        {
            Assert.That(FocusSight.HoldsItem(true, false, 0.01f, 1.2f), Is.True, "a crate the item lies on");
            Assert.That(FocusSight.HoldsItem(true, false, 0.01f, 6f), Is.False, "a large box that only touches the item: a wall");
            Assert.That(FocusSight.HoldsItem(true, false, 0.3f, 1.2f), Is.False, "a crate 30 cm away");
            Assert.That(FocusSight.HoldsItem(false, true, 0f, 1f), Is.False, "terrain / triangle meshes are surfaces");
        }

        [Test]
        public void CharacterFocus_G3ScanRange_ReachesTheBragaSleeper_AndAimsAtALyingBody()
        {
            // fix-247, owner's save754 (Braga, 01:26): the hero 3.66 m from Nafalem's bed slot (G3_Object_Interact_Bed_04)
            // got no focus with the old 3.5 m reach; G3 PC_Hero NormalModeCharacterScanRange is 500 cm
            var hero = new Vector2(-865.5f, -543.5f);
            var slot = new Vector2(-865.786926f, -547.174622f);
            float d = Vector2.Distance(hero, slot);
            Assert.That(d, Is.GreaterThan(3.5f), "out of the old reach");
            Assert.That(d, Is.LessThanOrEqualTo(CharacterFocus.ScanRangeM), "in G3's");
            Assert.That(CharacterFocus.ScanRangeM, Is.EqualTo(5f));
            Assert.That(CharacterFocus.AimsAtBody(lying: true), Is.True, "a sleeper is also measured at its body in the bed");
            Assert.That(CharacterFocus.AimsAtBody(lying: false), Is.False);
        }
    }
}
