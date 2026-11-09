using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G3.Formats.Animation;
using G3.Formats.Pak;
using G3.Player;
using NUnit.Framework;

namespace G3Tests.Formats
{
    /// <summary>
    /// fix-60 "drawing a weapon lowers the hero": per weapon class, the G3 drawn stand and its draw / sheathe motions keep
    /// the hero's planted foot on the ground (the root height does not change; only the pelvis and head crouch), and the
    /// camera pivot follows the head (<see cref="CameraPivotRules"/>, G3 GetHeadMat), so the head keeps its height relative
    /// to the camera across draw and sheathe. Reads the installed game's _compiledAnimation volume (G3_DATA or the Steam
    /// path); ignored when it is absent.
    /// </summary>
    [TestFixture]
    public class HeroStanceHeightTests
    {
        const string Actor = "G3_Hero_Body_Player";
        const float GroundTolerance = 4f; // cm

        static G3FileSystem _fs;
        static EmfxActor _actor;

        [OneTimeSetUp]
        public void Open()
        {
            string data = Environment.GetEnvironmentVariable("G3_DATA") ?? @"C:\Program Files (x86)\Steam\steamapps\common\Gothic 3\Data";
            if (!File.Exists(Path.Combine(data, "_compiledAnimation.pak"))) Assert.Ignore("Gothic 3 data not installed");
            _fs = new G3FileSystem(data, "_compiledAnimation");
            _actor = Xact.Read(_fs.GetByName(Actor + ".xact").Read()).Actor;
        }

        [OneTimeTearDown]
        public void Close() => _fs?.Dispose();

        /// <summary>Weapon class → (left, right, pose) of its drawn stand set (unarmed first).</summary>
        static readonly (string cls, string set, string pose)[] Classes =
        {
            ("unarmed", "None_None", "P0"), ("1H", "None_1H", "P0"), ("2H/axe", "None_2H", "P0"), ("staff", "None_Staff", "P0"),
            ("fists", "None_Fist", "P0"), ("bow", "Bow_None", "P0"), ("crossbow", "CrossBow_None", "P0"), ("dual", "1H_1H", "P0"),
            ("shield", "Shield_1H", "P1"), ("magic", "None_Cast", "P0"),
        };

        /// <summary>Lowest toe and the head (cm above the root) of the hero posed by <paramref name="motion"/> at its frames.</summary>
        static List<(float toe, float head)> Pose(string motion, int frames = 6)
        {
            var e = _fs.GetByName(motion + ".xmot");
            Assert.That(e, Is.Not.Null, motion);
            var mot = Xmot.Read(e.Read(), motion);
            var map = ActorPose.MapTracks(_actor, mot);
            var held = ActorPose.HeldHelpers(_actor, map);
            int Node(string n) => Enumerable.Range(0, _actor.Nodes.Count).First(i => _actor.Nodes[i].Name == n);
            int lt = Node("Hero_Left_Foot_Toe_1"), rt = Node("Hero_Right_Foot_Toe_1"), head = Node("Hero_Head_Head_1");
            var o = new List<(float, float)>();
            for (int f = 0; f <= frames; f++)
            {
                var p = ActorPose.Sample(_actor, map, held, mot.Duration * f / frames);
                o.Add((Math.Min(p[lt].Pos.Y, p[rt].Pos.Y), p[head].Pos.Y));
            }
            return o;
        }

        static string Stand(string set, string pose) => $"Hero_Stand_{set}_{pose}_Move_Stand_N_Fwd_00_%_00_{pose}_0";

        [Test]
        public void Drawn_stands_keep_the_feet_on_the_ground_and_the_head_still_on_screen()
        {
            var rest = Pose(Stand("None_None", "P0"))[0];
            Assert.That(rest.head / 100f, Is.EqualTo(CameraPivotRules.RestHeadHeight).Within(0.005f), "rest head height");
            foreach (var (cls, set, pose) in Classes)
            {
                var (toe, head) = Pose(Stand(set, pose))[0];
                Assert.That(toe - rest.toe, Is.EqualTo(0f).Within(GroundTolerance), $"{cls}: the drawn stand moves the feet (root) by {toe - rest.toe:F1} cm");
                // the camera pivot moves with the head: head above the pivot stays what it is in the unarmed stand
                float pivotShift = CameraPivotRules.HeadOffset(head / 100f) - CameraPivotRules.HeadOffset(rest.head / 100f);
                Assert.That((head - rest.head) / 100f - pivotShift, Is.EqualTo(0f).Within(0.002f), $"{cls}: head drop {rest.head - head:F1} cm not followed");
                TestContext.Progress.WriteLine($"{cls,-9} toe {toe,5:F1} cm, head {head,6:F1} cm (drop {rest.head - head,4:F1})");
            }
        }

        [Test]
        public void Draw_and_sheathe_motions_keep_the_planted_foot_on_the_ground()
        {
            int checkedMotions = 0;
            foreach (var (cls, set, pose) in Classes.Skip(1))
                foreach (string hold in new[] { "Hold", "HoldLeft", "HoldRight" })
                    foreach (string stage in new[] { "Begin", "End" })
                    {
                        string name = $"Hero_Stand_{set}_P0_{hold}_{stage}_N_Fwd_00_%_00_P0_0";
                        if (_fs.GetByName(name + ".xmot") == null) continue;
                        foreach (var (toe, _) in Pose(name))
                            Assert.That(toe, Is.EqualTo(0f).Within(GroundTolerance), $"{cls}: {name} lifts or sinks the planted foot to {toe:F1} cm");
                        checkedMotions++;
                    }
            Assert.That(checkedMotions, Is.GreaterThanOrEqualTo(10));
        }

        [Test]
        public void Pivot_offset_follows_the_head_within_limits()
        {
            Assert.That(CameraPivotRules.HeadOffset(CameraPivotRules.RestHeadHeight), Is.EqualTo(0f));
            Assert.That(CameraPivotRules.HeadOffset(1.685f), Is.EqualTo(-0.0936f).Within(1e-4f), "1H stance");
            Assert.That(CameraPivotRules.HeadOffset(0.3f), Is.EqualTo(-CameraPivotRules.MaxDrop), "lying: clamped");
            Assert.That(CameraPivotRules.HeadOffset(2.5f), Is.EqualTo(CameraPivotRules.MaxRise));
            float v = 0;
            for (int i = 0; i < 30; i++) v = CameraPivotRules.Follow(v, -0.1f, 1f / 60);
            Assert.That(v, Is.EqualTo(-0.1f).Within(0.01f), "a draw's crouch comes through within half a second");
            Assert.That(CameraPivotRules.Follow(0.05f, -0.1f, 0f), Is.EqualTo(0.05f), "no time, no change");
        }
    }
}
