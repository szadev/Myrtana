using System;
using System.IO;
using System.Linq;
using G3.Formats.Animation;
using G3.Formats.Pak;
using Myrtana.Rules;
using NUnit.Framework;

namespace G3Tests.Formats
{
    /// <summary>
    /// fix-189 "the hero walks into the hookah and sits inside it": an animated object's user stands on the actor's
    /// <see cref="ObjectUseParts.ExactDummy"/>, not on the object's origin. The water pipe's dummy lies 99.1 cm in front of
    /// the pipe (= its world entities' gCNavOffset_PS.OffsetPose (0, 0, 99.1), where NPCs sit), the pipe's mesh round its
    /// origin; the orc boulder's dummy is its origin, the stone 95 cm behind. Reads the installed game's
    /// _compiledAnimation volume (G3_DATA or the Steam path); ignored when it is absent.
    /// </summary>
    [TestFixture]
    public class ExactUsePointTests
    {
        const float CoreRadius = 15f; // cm, UseSpotClearance.CoreRadius
        const float Top = 195f;       // cm, the hero's height

        static G3FileSystem _fs;

        [OneTimeSetUp]
        public void Open()
        {
            string data = Environment.GetEnvironmentVariable("G3_DATA") ?? @"C:\Program Files (x86)\Steam\steamapps\common\Gothic 3\Data";
            if (!File.Exists(Path.Combine(data, "_compiledAnimation.pak"))) Assert.Ignore("Gothic 3 data not installed");
            _fs = new G3FileSystem(data, "_compiledAnimation");
        }

        [OneTimeTearDown]
        public void Close() => _fs?.Dispose();

        /// <summary>The dummy's bind position, the smallest horizontal distance from it to a mesh vertex below the hero's
        /// head, and whether the origin lies within the mesh's horizontal bounds (cm, actor space).</summary>
        static (V3 dummy, float atDummy, bool originInside) Measure(string actorName)
        {
            var actor = Xact.Read(_fs.GetByName(actorName + ".xact").Read()).Actor;
            var bind = ActorPose.Bind(actor);
            int d = actor.Nodes.FindIndex(n => string.Equals(n.Name, ObjectUseParts.ExactDummy, StringComparison.OrdinalIgnoreCase));
            Assert.That(d, Is.GreaterThanOrEqualTo(0), actorName + " has no " + ObjectUseParts.ExactDummy);
            var dummy = bind[d].Pos;
            var verts = actor.Meshes.Where(m => !m.IsCollisionMesh).SelectMany(m => m.Positions).Where(p => p.Y < Top).ToList();
            float near = verts.Min(p => (float)Math.Sqrt((p.X - dummy.X) * (p.X - dummy.X) + (p.Z - dummy.Z) * (p.Z - dummy.Z)));
            bool inside = verts.Min(p => p.X) < 0f && verts.Max(p => p.X) > 0f && verts.Min(p => p.Z) < 0f && verts.Max(p => p.Z) > 0f;
            return (dummy, near, inside);
        }

        [Test]
        public void WaterPipeUserSitsInFrontOfThePipe()
        {
            var (dummy, atDummy, originInside) = Measure("G3_Waterpipe_Body_Waterpipe");
            Assert.That(dummy.Z, Is.EqualTo(99.13f).Within(0.1f), "the OffsetPose (0, 0, 99.1) of the world's water pipes");
            Assert.That(Math.Abs(dummy.X) + Math.Abs(dummy.Y), Is.LessThan(0.1f));
            Assert.That(atDummy, Is.GreaterThan(CoreRadius * 3), "the stand point is clear of the pipe");
            Assert.That(originInside, Is.True, "the origin lies inside the pipe (the old stand point)");
        }

        [Test]
        public void BoulderUserStandsOnTheOriginBeforeTheStone()
        {
            var (dummy, atDummy, _) = Measure("G3_OrcBoulder_Body_OrcBoulder");
            Assert.That(Math.Abs(dummy.X) + Math.Abs(dummy.Y) + Math.Abs(dummy.Z), Is.LessThan(0.1f));
            Assert.That(atDummy, Is.GreaterThan(CoreRadius), "the stand point is clear of the stone");
        }

        /// <summary>
        /// fix-243 "the hero kneels inside the chest while picking it": all five chest actors (the world's 1,066 chests)
        /// have the dummy <see cref="ChestUsePose.KneelDistance"/> in front of their origin, and the kneeling body (pelvis
        /// ahead of the hero's origin in the lockpick and open motions) plus the hero's core stays in front of the chest's face.
        /// </summary>
        [TestCase("G3_Chest_Body_Chest")]
        [TestCase("G3_Chest_Body_Chest_02")]
        [TestCase("G3_Chest_Body_Chest_03")]
        [TestCase("G3_Chest_Body_ChestPod_01")]
        [TestCase("G3_Chest_Body_ChestPod_02")]
        public void ChestUserKneelsOnTheDummyInFrontOfTheChest(string actorName)
        {
            var (dummy, _, originInside) = Measure(actorName);
            Assert.That(dummy.Z, Is.EqualTo(ChestUsePose.KneelDistance * 100f).Within(0.1f), "ChestUsePose.KneelDistance");
            Assert.That(Math.Abs(dummy.X) + Math.Abs(dummy.Y), Is.LessThan(0.1f));
            Assert.That(originInside, Is.True);
            var actor = Xact.Read(_fs.GetByName(actorName + ".xact").Read()).Actor;
            float face = actor.Meshes.Where(m => !m.IsCollisionMesh).SelectMany(m => m.Positions).Max(p => p.Z);
            foreach (var motion in new[] { "Hero_Kneel_None_None_P0_Lockpick_Loop_N_Fwd_00_%_00_P0_0", "Hero_Kneel_None_None_P0_OpenChest_Loop_N_Fwd_00_%_00_P0_0" })
            {
                var m = Xmot.Read(_fs.GetByName(motion + ".xmot").Read());
                var pelvis = m.Tracks.First(t => t.Node == "Hero_Spine_Spine_1");
                float lean = -pelvis.PosePosition.Z; // Hero_ROOT is turned 180 deg: its -Z is the hero's forward
                Assert.That(lean, Is.GreaterThan(25f).And.LessThan(40f), motion + ": the kneeling pelvis leans forward");
                Assert.That(dummy.Z - face, Is.GreaterThan(lean + CoreRadius), motion + ": the kneeling body stays out of " + actorName);
            }
        }
    }
}
