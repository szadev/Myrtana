using System.Numerics;
using G3.Player;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-208: what of the hero the first-person camera never draws (FirstPersonBodyRules).</summary>
    public class FirstPersonBodyTests
    {
        static string[] Chain(params string[] selfThenParents) => selfThenParents;

        [Test]
        public void HeadSubtree_IsHead()
        {
            Assert.That(FirstPersonBodyRules.IsHeadNode(Chain("Hero_Head_Head_1", "Hero_Head_Head_ROOT", "Hero_Head_Neck_END", "Hero_ROOT")), Is.True);
            Assert.That(FirstPersonBodyRules.IsHeadNode(Chain("Hero_Head_Head_ROOT", "Hero_Head_Neck_END")), Is.True);
            Assert.That(FirstPersonBodyRules.IsHeadNode(Chain("FACIAL_C_Jaw", "Hero_Head_Head_1", "Hero_Head_Head_ROOT")), Is.True, "a remaster face bone below the head");
        }

        [TestCase("Slot_Helmet")]
        [TestCase("Slot_Hair")]
        [TestCase("Slot_Beard")]
        public void HeadSlots_AndWhatHangsOnThem_AreHead(string slot)
        {
            Assert.That(FirstPersonBodyRules.IsHeadNode(Chain(slot, "Hero_Head_Head_1")), Is.True);
            Assert.That(FirstPersonBodyRules.IsHeadNode(Chain("Helmet mesh", slot, "Hero_Spine_Spine_5")), Is.True, "a static mesh under the slot");
        }

        [Test]
        public void NeckBones_AreHead_ButNotTheirOtherChildren()
        {
            Assert.That(FirstPersonBodyRules.IsHeadNode(Chain("Hero_Head_Neck_2", "Hero_Head_Neck_1", "Hero_Head_Neck_ROOT")), Is.True);
            Assert.That(FirstPersonBodyRules.IsHeadNode(Chain("Hero_Left_Arm_Shoulder_1", "Hero_Head_Neck_1", "Hero_Spine_Spine_5")), Is.False,
                        "a shoulder under a neck bone is body");
        }

        [TestCase("Hero_Right_Hand_Hand_1")]
        [TestCase("Slot_RightHand_Weapon")]
        [TestCase("Slot_MeleeBackLeft")]
        [TestCase("Hero_Spine_Spine_5")]
        [TestCase("Hero_Left_Arm_Shoulder_1")]
        public void BodyBones_HandsAndWeapons_AreNotHead(string bone)
        {
            Assert.That(FirstPersonBodyRules.IsHeadNode(Chain(bone, "Hero_Spine_Spine_4", "Hero_ROOT")), Is.False);
            Assert.That(FirstPersonBodyRules.IsHeadNode(Chain("Weapon", bone, "Hero_ROOT")), Is.False, "a weapon in the hand stays drawn");
        }

        [Test]
        public void HeadMeshNames()
        {
            Assert.That(FirstPersonBodyRules.IsHeadMeshName("G3_Head_Hero_Hero_01"), Is.True);
            Assert.That(FirstPersonBodyRules.IsHeadMeshName("G3_Hero_Body_Player"), Is.False);
            Assert.That(FirstPersonBodyRules.IsHeadMeshName(null), Is.False);
        }

        [Test]
        public void Vertex_IsHead_FromHalfItsWeight()
        {
            Assert.That(FirstPersonBodyRules.IsHeadVertex(1f, 1f), Is.True);
            Assert.That(FirstPersonBodyRules.IsHeadVertex(0.5f, 1f), Is.True);
            Assert.That(FirstPersonBodyRules.IsHeadVertex(0.49f, 1f), Is.False);
            Assert.That(FirstPersonBodyRules.IsHeadVertex(0f, 0f), Is.False, "an unweighted vertex is body");
        }

        [Test]
        public void Cut_DropsTrianglesTouchingTheHead()
        {
            // two quads: 0-1-2 / 1-3-2 body, 2-3-4 touches the head vertex 4
            var tris = new[] { 0, 1, 2, 1, 3, 2, 2, 3, 4 };
            var head = new[] { false, false, false, false, true };
            var cut = FirstPersonBodyRules.CutTriangles(tris, head, out int removed);
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(cut, Is.EqualTo(new[] { 0, 1, 2, 1, 3, 2 }));
        }

        [Test]
        public void Classify_FromTheCut()
        {
            Assert.That(FirstPersonBodyRules.ClassifyTriangles(0, 100), Is.EqualTo(FirstPersonPart.Body), "plain body");
            Assert.That(FirstPersonBodyRules.ClassifyTriangles(100, 100), Is.EqualTo(FirstPersonPart.Head), "a head / hood mesh");
            Assert.That(FirstPersonBodyRules.ClassifyTriangles(30, 100), Is.EqualTo(FirstPersonPart.Mixed), "armour with its helmet");
            Assert.That(FirstPersonBodyRules.ClassifyTriangles(0, 0), Is.EqualTo(FirstPersonPart.Body));
        }

        [Test]
        public void UnreadableMesh_HeadOnlyWhenAllBonesAreHead()
        {
            Assert.That(FirstPersonBodyRules.ClassifyByBones(new[] { true, true }), Is.EqualTo(FirstPersonPart.Head));
            Assert.That(FirstPersonBodyRules.ClassifyByBones(new[] { true, false }), Is.EqualTo(FirstPersonPart.Body));
            Assert.That(FirstPersonBodyRules.ClassifyByBones(new bool[0]), Is.EqualTo(FirstPersonPart.Body));
        }

        [Test]
        public void NearPlane_ClipsTheLensButNotTheHands()
        {
            Assert.That(FirstPersonBodyRules.NearClip, Is.GreaterThan(0.05f).And.LessThanOrEqualTo(0.15f));
        }

        [TestCase("Hero_Left_Arm_Shoulder_1", FirstPersonBone.Shoulder)]
        [TestCase("Hero_Right_Arm_Shoulder_ROOT", FirstPersonBone.Shoulder)]
        [TestCase("Hero_Left_Arm_Arm_ROOT", FirstPersonBone.Shoulder)]
        [TestCase("Hero_Left_Arm_Arm_1", FirstPersonBone.Shoulder)]
        [TestCase("clavicle_l", FirstPersonBone.Shoulder)]
        [TestCase("upperarm_twist_01_r", FirstPersonBone.Shoulder)]
        [TestCase("Bip01 L UpperArm", FirstPersonBone.Shoulder)]
        [TestCase("Hero_Left_Arm_Arm_2", FirstPersonBone.Limb)]
        [TestCase("Hero_Left_Arm_Arm_END", FirstPersonBone.Limb)]
        [TestCase("Bip01 L Forearm", FirstPersonBone.Limb)]
        [TestCase("lowerarm_l", FirstPersonBone.Limb)]
        [TestCase("Hero_Right_Hand_Finger1_2", FirstPersonBone.Limb)]
        [TestCase("Slot_RightHand_Weapon", FirstPersonBone.Limb)]
        [TestCase("Hero_Left_Leg_Leg_1", FirstPersonBone.Limb)]
        [TestCase("Hero_Spine_Spine_5", FirstPersonBone.Spine)]
        [TestCase("Hero_Pelvis", FirstPersonBone.Other)]
        [TestCase(null, FirstPersonBone.Other)]
        public void BoneKinds(string bone, FirstPersonBone kind)
        {
            Assert.That(FirstPersonBodyRules.BoneKind(bone), Is.EqualTo(kind));
        }

        [Test]
        public void ShoulderJoints_AreTheUpperArmJoints()
        {
            Assert.That(FirstPersonBodyRules.ShoulderJointSide("Hero_Left_Arm_Arm_1", out int a), Is.EqualTo(1));
            Assert.That(FirstPersonBodyRules.ShoulderJointSide("Bip01 L UpperArm", out int b), Is.EqualTo(1));
            Assert.That(a, Is.GreaterThan(b));
            Assert.That(FirstPersonBodyRules.ShoulderJointSide("Hero_Right_Arm_Arm_1", out _), Is.EqualTo(2));
            Assert.That(FirstPersonBodyRules.ShoulderJointSide("Hero_Left_Arm_Arm_ROOT", out _), Is.EqualTo(0), "next to the neck");
            Assert.That(FirstPersonBodyRules.ShoulderJointSide("upperarm_r", out _), Is.EqualTo(2));
            Assert.That(FirstPersonBodyRules.ShoulderJointSide("upperarm_twist_01_r", out _), Is.EqualTo(0));
            Assert.That(FirstPersonBodyRules.ShoulderJointSide("Hero_Left_Arm_Shoulder_1", out _), Is.EqualTo(0));
            Assert.That(FirstPersonBodyRules.ShoulderJointSide("Hero_Left_Arm_Arm_2", out _), Is.EqualTo(0));
        }

        // a rig like the G3 hero's (A-pose): shoulder joints at 1.45 m, 0.18 m out; head joint at 1.6 m
        static readonly CollarFrame TPose = new CollarFrame(new Vector3(0, 1.6f, 0), new Vector3(0.18f, 1.45f, 0), new Vector3(-0.18f, 1.45f, 0));

        static bool Collar(FirstPersonBone kind, float x, float y, float z)
        {
            TPose.Measure(new Vector3(x, y, z), out float h, out float l, out float d);
            return FirstPersonBodyRules.IsCollarVertex(kind, h, l, d, TPose.HalfSpan);
        }

        [Test]
        public void Frame_MeasuresHeightAndLateral()
        {
            Assert.That(TPose.Valid, Is.True);
            Assert.That(TPose.HalfSpan, Is.EqualTo(0.18f).Within(1e-5f));
            TPose.Measure(new Vector3(0.3f, 1.55f, 0f), out float h, out float l, out float d);
            Assert.That(h, Is.EqualTo(0.1f).Within(1e-5f));
            Assert.That(l, Is.EqualTo(0.3f).Within(1e-5f));
            Assert.That(d, Is.EqualTo(new Vector3(0.3f, -0.05f, 0f).Length()).Within(1e-5f));
            Assert.That(new CollarFrame(Vector3.One, Vector3.One, Vector3.One).Valid, Is.False, "degenerate frame: no cut");
        }

        [Test]
        public void Pauldron_IsCut()
        {
            Assert.That(Collar(FirstPersonBone.Shoulder, 0.2f, 1.52f, 0f), Is.True, "pad on top of the shoulder");
            Assert.That(Collar(FirstPersonBone.Shoulder, 0.3f, 1.5f, 0.05f), Is.True, "pad rim out beyond the joint");
            Assert.That(Collar(FirstPersonBone.Shoulder, 0.1f, 1.4f, 0.08f), Is.True, "clavicle next to the neck");
            Assert.That(Collar(FirstPersonBone.Shoulder, 0.42f, 1.5f, 0f), Is.True, "outer rim of a wide pauldron");
        }

        [Test]
        public void UpperArmOut_AndChest_Stay()
        {
            Assert.That(Collar(FirstPersonBone.Shoulder, 0.6f, 1.47f, 0f), Is.False, "upper arm held out (bind pose)");
            Assert.That(Collar(FirstPersonBone.Shoulder, 0.4f, 1.4f, 0f), Is.False, "below the shoulder line, far from the head");
            Assert.That(Collar(FirstPersonBone.Spine, 0f, 1.35f, 0.12f), Is.False, "chest");
            Assert.That(Collar(FirstPersonBone.Spine, 0.1f, 1.48f, 0.1f), Is.False, "top of the chest at the shoulder line");
        }

        [Test]
        public void HoodAndBuiltInHelmet_OnTheSpine_AreCut()
        {
            Assert.That(Collar(FirstPersonBone.Spine, 0f, 1.75f, -0.1f), Is.True, "hood behind the head");
            Assert.That(Collar(FirstPersonBone.Spine, 0.08f, 1.9f, 0f), Is.True, "helmet top");
            Assert.That(Collar(FirstPersonBone.Spine, 0.05f, 1.55f, 0.08f), Is.True, "standing collar");
            Assert.That(Collar(FirstPersonBone.Spine, 0.35f, 1.55f, 0f), Is.True, "mantle collar flap over the shoulder");
            Assert.That(Collar(FirstPersonBone.Spine, 0.35f, 1.47f, 0f), Is.False, "top of the shoulder at the line");
            Assert.That(Collar(FirstPersonBone.Spine, 0.1f, 1.5f, -0.05f), Is.True, "hood lying beside the neck");
            Assert.That(Collar(FirstPersonBone.Spine, 0f, 1.42f, 0.12f), Is.False, "top of the chest in front");
        }

        [Test]
        public void HandsAndWeapons_NeverCut_EvenAtTheHead()
        {
            Assert.That(Collar(FirstPersonBone.Limb, 0f, 1.65f, 0.1f), Is.False, "a hand raised in front of the face");
            Assert.That(Collar(FirstPersonBone.Other, 0f, 1.6f, 0f), Is.False);
        }
    }
}
