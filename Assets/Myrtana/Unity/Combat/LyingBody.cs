using System.Collections.Generic;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// fix-241: the hit shape of a lying (knocked-out) body for projectiles: a capsule from the head bone through the pelvis
    /// to the legs, measured from the current pose, so arrows and spell missiles touch the body where it lies (the upright
    /// movement capsule of <see cref="CombatBody"/> does not fit it). Without those bones: a low disc round the origin.
    /// </summary>
    public static class LyingBody
    {
        /// <summary>Radius of the lying capsule (m) [interp: a lying torso's half-thickness plus an arrow's slack].</summary>
        public const float Radius = 0.3f;
        /// <summary>How far past the pelvis the capsule reaches towards the legs, as a share of the head-pelvis distance [interp].</summary>
        public const float LegReach = 1.1f;

        static readonly Dictionary<CombatFighter, (Transform visual, Transform head, Transform pelvis)> Bones =
            new Dictionary<CombatFighter, (Transform, Transform, Transform)>();

        public static bool Capsule(CombatFighter f, out Vector3 a, out Vector3 b, out float radius)
        {
            radius = Radius;
            var root = f.Visual != null ? f.Visual : f.transform;
            if (!Bones.TryGetValue(f, out var bones) || bones.visual != root || bones.head == null || bones.pelvis == null)
            {
                if (Bones.Count > 256) Bones.Clear();
                bones = (root, FindSuffix(root, "_Head_Head_1"), FindSuffix(root, "Spine_Spine_1"));
                Bones[f] = bones;
            }
            if (bones.head != null && bones.pelvis != null)
            {
                a = bones.head.position;
                var pelvis = bones.pelvis.position;
                b = pelvis + (pelvis - a) * LegReach;
                return true;
            }
            var p = f.transform.position + Vector3.up * 0.3f;
            a = p;
            b = p;
            radius = 0.7f;
            return true;
        }

        static Transform FindSuffix(Transform root, string suffix)
        {
            if (root.name.EndsWith(suffix, System.StringComparison.OrdinalIgnoreCase)) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var t = FindSuffix(root.GetChild(i), suffix);
                if (t != null) return t;
            }
            return null;
        }
    }
}
