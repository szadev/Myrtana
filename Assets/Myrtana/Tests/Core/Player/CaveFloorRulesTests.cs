using System.Collections.Generic;
using G3.Player;
using NUnit.Framework;
using K = G3.Player.CaveFloorRules.Kind;
using S = G3.Player.CaveFloorRules.Stand;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-128 (owner: "again in a cave, a wolf is somewhere inside the rock"): inside caves a body stands only on a true
    /// cave floor (<see cref="CaveFloorRules"/>). Columns from G3Cli collision column at the Montera wolf cave
    /// (Dungeon_V01_S01_05 / S02_05 under Landscape_Cell_173).
    /// </summary>
    public class CaveFloorRulesTests
    {
        static CaveFloorRules.Above A(float dy, K kind, bool up = false) => new CaveFloorRules.Above(dy, kind, up);

        [Test]
        public void CaveFloor_UnderItsOwnCeiling_IsATrueFloor()
        {
            // (79.6, 32.4): floor 65.22, roof 70.47, hill 100.14
            var above = new List<CaveFloorRules.Above> { A(5.25f, K.Cave), A(34.92f, K.Landscape, up: true) };
            Assert.That(CaveFloorRules.Classify(0f, K.Cave, true, above), Is.EqualTo(S.TrueFloor));
        }

        [Test]
        public void RoofTop_UnderTheHill_IsRefused()
        {
            // the same column standing on the roof at 70.47: the hill's top (faces up) is the first thing above
            var above = new List<CaveFloorRules.Above> { A(29.67f, K.Landscape, up: true) };
            Assert.That(CaveFloorRules.Classify(0f, K.Cave, true, above), Is.EqualTo(S.RoofTop));
            Assert.That(CaveFloorRules.MayStand(S.RoofTop), Is.False);
        }

        [Test]
        public void OwnerWolf_InTheRockBehindTheWall_HasNoFloor()
        {
            // Montera_Wolf_01#0c6fefe2 at (82.19, 65.53, 36.86): nothing under it, the hill 33.7 m above
            var above = new List<CaveFloorRules.Above> { A(33.71f, K.Landscape, up: true) };
            Assert.That(CaveFloorRules.Classify(null, K.Other, true, above), Is.EqualTo(S.NoFloor));
        }

        [Test]
        public void NoFloor_UnderACaveCeiling_IsAirNotRock()
        {
            // a swimmer over a deep cave lake, a falling body, a bloodfly: not judged
            var above = new List<CaveFloorRules.Above> { A(6f, K.Cave), A(40f, K.Landscape, up: true) };
            Assert.That(CaveFloorRules.Classify(null, K.Other, true, above), Is.EqualTo(S.Open));
            Assert.That(CaveFloorRules.Classify(5f, K.Cave, true, above), Is.EqualTo(S.Open));
        }

        [Test]
        public void StackedCaves_TheLowerRoofTop_IsRefusedWhenTheUpperFloorIsFar()
        {
            // (98.27, -12.2): V01 floor 64.74, V01 roof 72.14, V07 floor 90.60: the V01 roof top has the V07 floor 18.5 m up
            // (a cave sheet counts as a ceiling at runtime; beyond MaxCaveHeight it is another cave)
            var near = new List<CaveFloorRules.Above> { A(18.46f, K.Cave), A(23.3f, K.Cave), A(41.9f, K.Landscape, true) };
            Assert.That(CaveFloorRules.Classify(0f, K.Cave, true, near), Is.EqualTo(S.TrueFloor), "within MaxCaveHeight: a hall");
            var far = new List<CaveFloorRules.Above> { A(CaveFloorRules.MaxCaveHeight + 1f, K.Cave) };
            Assert.That(CaveFloorRules.Classify(0f, K.Cave, true, far), Is.EqualTo(S.RoofTop));
            // with the facing known (G3Cli --facing): the underside of the upper cave's floor
            var faced = new List<CaveFloorRules.Above> { A(18.46f, K.Cave, up: true) };
            Assert.That(CaveFloorRules.Classify(0f, K.Cave, true, faced), Is.EqualTo(S.RoofTop));
        }

        [Test]
        public void PropsAbove_NeverDecide()
        {
            // a bridge 3 m over the cave floor, the cave's roof above it
            var above = new List<CaveFloorRules.Above> { A(3f, K.Other), A(9f, K.Cave) };
            Assert.That(CaveFloorRules.Classify(0f, K.Cave, true, above), Is.EqualTo(S.TrueFloor));
            // a prop on a roof top inside the hill
            var roof = new List<CaveFloorRules.Above> { A(2f, K.Other), A(20f, K.Landscape, true) };
            Assert.That(CaveFloorRules.Classify(0f, K.Other, true, roof), Is.EqualTo(S.RoofTop));
        }

        [Test]
        public void OpenSky_AndPlacesAwayFromCaves_AreNotJudged()
        {
            var none = new List<CaveFloorRules.Above>();
            Assert.That(CaveFloorRules.Classify(0f, K.Cave, true, none), Is.EqualTo(S.Open), "a cave mouth's floor under the sky");
            Assert.That(CaveFloorRules.Classify(null, K.Other, false, none), Is.EqualTo(S.Open), "not a cave place");
            var hill = new List<CaveFloorRules.Above> { A(10f, K.Landscape, true) };
            Assert.That(CaveFloorRules.Classify(0f, K.Landscape, true, hill), Is.EqualTo(S.TrueFloor), "a landscape overhang / fold");
        }

        [Test]
        public void TheOwnSheet_IsSkipped()
        {
            var above = new List<CaveFloorRules.Above> { A(0.01f, K.Cave), A(4f, K.Cave) };
            Assert.That(CaveFloorRules.Classify(0f, K.Cave, true, above), Is.EqualTo(S.TrueFloor));
        }

        [Test]
        public void Names()
        {
            Assert.That(CaveFloorRules.KindOf("G3_Myrtana_Dungeon_V01_S01_05"), Is.EqualTo(K.Cave));
            Assert.That(CaveFloorRules.KindOf("G3_Myrtana_Reddock_Level_Dungeon_02"), Is.EqualTo(K.Cave));
            Assert.That(CaveFloorRules.KindOf("G3_Myrtana_Landscape_01_175"), Is.EqualTo(K.Landscape));
            Assert.That(CaveFloorRules.KindOf("G3_Object_Stone_02"), Is.EqualTo(K.Other));
            Assert.That(CaveFloorRules.KindOf(null), Is.EqualTo(K.Other));
        }

        [Test]
        public void Snap_InTheRock_TakesTheNearestTrueFloor()
        {
            // the owner's wolf: the cave floor 1.7 m up and 6 m away, the hill 33 m up; a refused (roof top) spot nearer
            var c = new List<(float, float, float, bool)> { (1f, 0f, 0f, false), (6f, 1.7f, -0.6f, true), (9f, -2f, 0f, true) };
            Assert.That(CaveFloorRules.Snap(c, ceilingAbove: 33.7f, hasFloor: false), Is.EqualTo(1));
        }

        [Test]
        public void Snap_FromARoofTop_GoesDownNeverUpThroughACeiling()
        {
            // on the roof top at 70.47: the cave floor 5.25 m below; a spot on the hill 29 m above is refused
            var c = new List<(float, float, float, bool)> { (2f, 29f, 0f, true), (3f, -5.25f, 1f, true) };
            Assert.That(CaveFloorRules.Snap(c, ceilingAbove: 29.67f, hasFloor: true), Is.EqualTo(1));
            // nothing allowed: -1
            var none = new List<(float, float, float, bool)> { (2f, 3f, 0f, true), (1f, 0f, 0f, false) };
            Assert.That(CaveFloorRules.Snap(none, ceilingAbove: 2.5f, hasFloor: true), Is.EqualTo(-1));
        }

        [Test]
        public void Snap_WithAFloor_KeepsToItsLevelFirst()
        {
            var c = new List<(float, float, float, bool)> { (1f, -4f, 0f, true), (5f, 0.2f, 0f, true) };
            Assert.That(CaveFloorRules.Snap(c, null, hasFloor: true), Is.EqualTo(1));
        }

        // fix-160: DiggerCamp_Minecrawler_01 stepped from the floor (742.00, 174.69, 1331.40) up the wall's foot to
        // (742.32, 176.01, 1332.39) inside Dungeon_SP11 (Player.log, Demo_2026-10-19b)
        [Test]
        public void Step_UpIntoTheRock_FromATrueFloor_IsRefused() =>
            Assert.That(CaveFloorRules.StepAllowed(0.4f, fromMayStand: true, toMayStand: false), Is.False);

        [Test]
        public void Step_UpOntoATrueFloor_IsAllowed() =>
            Assert.That(CaveFloorRules.StepAllowed(0.4f, true, true), Is.True);

        [Test]
        public void Step_LevelOrDown_IsNeverJudged()
        {
            Assert.That(CaveFloorRules.StepAllowed(0.05f, true, false), Is.True);
            Assert.That(CaveFloorRules.StepAllowed(-0.5f, true, false), Is.True);
        }

        [Test]
        public void Step_OfABodyAlreadyInTheRock_IsAllowed() =>
            Assert.That(CaveFloorRules.StepAllowed(0.4f, fromMayStand: false, toMayStand: false), Is.True);
    }
}
