using System;
using System.Collections.Generic;
using System.Numerics;

namespace G3.Player
{
    /// <summary>How a renderer of the hero is drawn for the first-person camera (fix-208).</summary>
    public enum FirstPersonPart
    {
        /// <summary>Drawn as in third person (torso, arms, legs, hands, weapons).</summary>
        Body,
        /// <summary>Never drawn for the camera, shadow only (head, hair, beard, eyes, helmets, hoods, MetaHuman head).</summary>
        Head,
        /// <summary>One mesh with head and body vertices (a full armour with its helmet or hood): the original only casts
        /// the shadow, a copy without the head triangles is drawn.</summary>
        Mixed,
    }

    /// <summary>What a bone of a body / armour mesh is for the first-person collar cut (fix-208).</summary>
    public enum FirstPersonBone
    {
        /// <summary>Pelvis, root and anything unknown: never cut.</summary>
        Other,
        /// <summary>Forearm, hand, fingers, legs, slots and weapons: never cut (hands and weapons stay in view when raised).</summary>
        Limb,
        /// <summary>Clavicle (G3 &lt;Rig&gt;_Arm_Shoulder_*) and upper arm (_Arm_Arm_ROOT / _Arm_Arm_1): pauldrons hang on them.</summary>
        Shoulder,
        /// <summary>Spine bones: an armour without head bones skins its collar, hood or built-in helmet to the top of the spine.</summary>
        Spine,
    }

    /// <summary>
    /// Bind-pose frame of the collar cut (fix-208): the head joint and both shoulder joints in the mesh's space. Up runs from
    /// the middle of the shoulders to the head; a vertex's height is along it, its lateral distance is from that axis.
    /// </summary>
    public readonly struct CollarFrame
    {
        public readonly Vector3 Head, Middle, Up;
        public readonly float HalfSpan;
        public readonly bool Valid;

        public CollarFrame(Vector3 head, Vector3 leftShoulder, Vector3 rightShoulder)
        {
            Head = head;
            Middle = (leftShoulder + rightShoulder) * 0.5f;
            var up = head - Middle;
            float len = up.Length();
            Valid = len > 1e-4f;
            Up = Valid ? up / len : Vector3.UnitY;
            HalfSpan = Vector3.Distance(leftShoulder, rightShoulder) * 0.5f;
        }

        /// <summary>Height above the shoulder line, lateral distance from the body axis and distance to the head joint.</summary>
        public void Measure(Vector3 v, out float height, out float lateral, out float headDistance)
        {
            var d = v - Middle;
            height = Vector3.Dot(d, Up);
            lateral = (d - Up * height).Length();
            headDistance = Vector3.Distance(v, Head);
        }
    }

    /// <summary>
    /// Pure rules of the hero's first-person body (fix-208; no engine types, tested in Tools/G3Tests). The first-person
    /// camera sits in the hero's head at a fixed eye height while the animations move the head (swings, falls, swimming),
    /// so the head and everything on it would block the view: the head bone's subtree (Hero_Head_Head_ROOT, Slot_Hair,
    /// Slot_Beard, Slot_Helmet and whatever the remaster hangs there) and the neck bones are the "head"; a renderer on
    /// them is <see cref="FirstPersonPart.Head"/>, a skinned mesh weighted to them is cut per triangle.
    /// </summary>
    public static class FirstPersonBodyRules
    {
        /// <summary>A vertex belongs to the head when at least this share of its skin weight is on head bones.</summary>
        public const float HeadWeightShare = 0.5f;
        /// <summary>Near clip plane of the first-person camera (m): body parts pressed against the lens (shoulder pads, an
        /// arm raised in a swing) are clipped; hands and weapons in view stay further away.</summary>
        public const float NearClip = 0.1f;

        /// <summary>A bone whose subtree is the head: G3 rigs name it &lt;Rig&gt;_Head_Head_ROOT / _Head_Head_1, the head
        /// slots hang below it.</summary>
        public static bool IsHeadRootName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.IndexOf("_Head_Head", StringComparison.OrdinalIgnoreCase) >= 0
                || name.StartsWith("Slot_Hair", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Slot_Beard", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Slot_Helmet", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Bip01 Head", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A neck bone (&lt;Rig&gt;_Head_Neck_*): head by itself, its subtree is not (only the head is below it).</summary>
        public static bool IsNeckName(string name) =>
            !string.IsNullOrEmpty(name) && (name.IndexOf("_Head_Neck", StringComparison.OrdinalIgnoreCase) >= 0
                                            || string.Equals(name, "Bip01 Neck", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Whether a transform is part of the head. <paramref name="chain"/> = its own name first, then its parents' up
        /// to the character root: a neck bone itself, or anything at or below a head root.
        /// </summary>
        public static bool IsHeadNode(IReadOnlyList<string> chain)
        {
            if (chain == null || chain.Count == 0) return false;
            if (IsNeckName(chain[0])) return true;
            for (int i = 0; i < chain.Count; i++)
                if (IsHeadRootName(chain[i])) return true;
            return false;
        }

        /// <summary>A vertex with <paramref name="headWeight"/> of its <paramref name="totalWeight"/> on head bones.</summary>
        public static bool IsHeadVertex(float headWeight, float totalWeight) =>
            totalWeight > 0f && headWeight >= HeadWeightShare * totalWeight - 1e-5f;

        /// <summary>The triangle list without the triangles that touch a head vertex (<paramref name="headVertex"/>).</summary>
        public static int[] CutTriangles(int[] triangles, IReadOnlyList<bool> headVertex, out int removed)
        {
            removed = 0;
            if (triangles == null) return Array.Empty<int>();
            var keep = new List<int>(triangles.Length);
            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                if (Head(headVertex, a) || Head(headVertex, b) || Head(headVertex, c)) { removed++; continue; }
                keep.Add(a); keep.Add(b); keep.Add(c);
            }
            return keep.ToArray();
        }

        static bool Head(IReadOnlyList<bool> v, int i) => i >= 0 && i < v.Count && v[i];

        /// <summary>The part of a skinned mesh from its triangle counts: none cut = body, all cut = head, else mixed.</summary>
        public static FirstPersonPart ClassifyTriangles(int removed, int total)
        {
            if (total <= 0 || removed <= 0) return FirstPersonPart.Body;
            return removed >= total ? FirstPersonPart.Head : FirstPersonPart.Mixed;
        }

        /// <summary>Fallback for a mesh whose weights cannot be read: head only when every bone it uses is a head bone.</summary>
        public static FirstPersonPart ClassifyByBones(IReadOnlyList<bool> boneIsHead)
        {
            if (boneIsHead == null || boneIsHead.Count == 0) return FirstPersonPart.Body;
            for (int i = 0; i < boneIsHead.Count; i++) if (!boneIsHead[i]) return FirstPersonPart.Body;
            return FirstPersonPart.Head;
        }

        /// <summary>Collar cut: a shoulder vertex this close to the head joint (m, bind pose) is cut (pauldrons, collars).</summary>
        public const float CollarRadius = 0.3f;
        /// <summary>Collar cut: a shoulder or spine vertex above the shoulder line is cut up to this far (m) beyond the
        /// shoulder joints sideways (a pauldron's or a mantle collar's rim; an upper arm held out in the bind pose stays).</summary>
        public const float PauldronReach = 0.3f;
        /// <summary>Collar cut: a spine vertex this high (m) above the shoulder line is cut (hood, standing collar, mantle,
        /// built-in helmet of an armour without head bones); the chest and the top of the shoulders below stay.</summary>
        public const float SpineCollarHeight = 0.06f;
        /// <summary>Collar cut: a spine vertex this close to the head joint (m) is cut whatever its height (the neck ring of
        /// a hood or mantle lying on the shoulders right beside the camera); the chest below stays.</summary>
        public const float SpineCollarRadius = 0.18f;

        /// <summary>The kind of a bone for the collar cut, from its name (G3 rigs, Bip01, MetaHuman / UE names).</summary>
        public static FirstPersonBone BoneKind(string name)
        {
            if (string.IsNullOrEmpty(name)) return FirstPersonBone.Other;
            if (Has(name, "Slot_") || Has(name, "Hand") || Has(name, "Finger") || Has(name, "Thumb") || Has(name, "Forearm")
                || Has(name, "LowerArm") || Has(name, "_Arm_Arm_2") || Has(name, "_Arm_Arm_END") || Has(name, "Leg")
                || Has(name, "Thigh") || Has(name, "Calf") || Has(name, "Foot") || Has(name, "Toe") || Has(name, "Weapon"))
                return FirstPersonBone.Limb;
            if (Has(name, "Shoulder") || Has(name, "Clavicle") || Has(name, "UpperArm") || Has(name, "_Arm_Arm_1")
                || Has(name, "_Arm_Arm_ROOT"))
                return FirstPersonBone.Shoulder;
            if (Has(name, "Spine") || Has(name, "Chest")) return FirstPersonBone.Spine;
            return FirstPersonBone.Other;
        }

        static bool Has(string s, string part) => s.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>A shoulder joint of the frame: the upper arm's joint (G3 _Arm_Arm_1, the ball of the shoulder; the
        /// _Arm_Arm_ROOT / _Arm_Shoulder_1 sit next to the neck), else UpperArm (UE, Bip01).
        /// Returns 0 = not one, 1 = left, 2 = right; <paramref name="rank"/> prefers the G3 bone.</summary>
        public static int ShoulderJointSide(string name, out int rank)
        {
            rank = 0;
            if (string.IsNullOrEmpty(name)) return 0;
            if (Has(name, "_Arm_Arm_1")) rank = 2;
            else if (Has(name, "UpperArm") && !Has(name, "Twist")) rank = 1;
            else return 0;
            if (Has(name, "Left") || name.EndsWith("_l", StringComparison.OrdinalIgnoreCase) || Has(name, " L ")) return 1;
            if (Has(name, "Right") || name.EndsWith("_r", StringComparison.OrdinalIgnoreCase) || Has(name, " R ")) return 2;
            rank = 0;
            return 0;
        }

        /// <summary>
        /// Collar cut (fix-208): the first-person camera looks down onto the shoulders, and an armour's pauldrons, collar,
        /// hood or built-in helmet fill the view as a dark blob. A vertex goes when its dominant bone is a shoulder bone and
        /// it is near the head joint or above the shoulder line, or a spine bone and it stands clearly above the shoulder
        /// line (both no further out than a pauldron's rim). Forearms, hands and weapons are never cut.
        /// </summary>
        public static bool IsCollarVertex(FirstPersonBone dominant, float height, float lateral, float headDistance, float halfSpan)
        {
            switch (dominant)
            {
                case FirstPersonBone.Shoulder:
                    return headDistance < CollarRadius || (height > 0f && lateral < halfSpan + PauldronReach);
                case FirstPersonBone.Spine:
                    return (height > SpineCollarHeight && lateral < halfSpan + PauldronReach) || headDistance < SpineCollarRadius;
                default:
                    return false;
            }
        }

        /// <summary>Head mesh by name: the head actor's meshes (G3_Head_*), as G3HeroImporter lists them.</summary>
        public static bool IsHeadMeshName(string meshName) =>
            !string.IsNullOrEmpty(meshName) && meshName.StartsWith("G3_Head_", StringComparison.OrdinalIgnoreCase);
    }
}
