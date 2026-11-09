using Myrtana.Rules;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// The resting pose of an item dropped during the game (<see cref="ItemRestPose"/>, fix-75): on the ground under the
    /// drop point (characters, loose items and physics bodies are not ground), its thinnest mesh axis along the ground
    /// normal, its longest level along <c>yaw</c>, the underside on the ground and lifted where the ground under its ends
    /// is higher. Downed fighters' swords, spears and bows used to stand upright, half in the ground.
    /// </summary>
    public static class WorldItemRest
    {
        const float Above = 1.5f, Below = 3f;
        static readonly RaycastHit[] Hits = new RaycastHit[16];

        public static Pose Place(Bounds mesh, float scale, Vector3 at, float yaw)
        {
            var size = mesh.size * scale;
            ItemRestPose.Axes(size.x, size.y, size.z, out int thin, out int length);
            var n = Vector3.up;
            var ground = at;
            if (TryGround(at, out var point, out var normal))
            {
                ground = point;
                n = Vector3.Angle(normal, Vector3.up) <= ItemRestPose.MaxTilt ? normal : Vector3.RotateTowards(Vector3.up, normal, ItemRestPose.MaxTilt * Mathf.Deg2Rad, 0f);
            }
            var forward = Vector3.ProjectOnPlane(Quaternion.Euler(0, yaw, 0) * Vector3.forward, n);
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.ProjectOnPlane(Vector3.forward, n);
            var rotation = Quaternion.LookRotation(forward.normalized, n) * Quaternion.Inverse(Quaternion.LookRotation(Axis(length), Axis(thin)));
            float halfThin = size[thin] * 0.5f, halfLength = size[length] * 0.5f;
            var centre = ground + n * ItemRestPose.CentreHeight(size.x, size.y, size.z);
            // the ends: the item is straight, the ground may not be
            var along = rotation * Axis(length);
            var a = centre + along * halfLength;
            var b = centre - along * halfLength;
            float underA = a.y - halfThin * n.y, underB = b.y - halfThin * n.y;
            float groundA = TryGround(a, out var pa, out _) ? pa.y : float.NegativeInfinity;
            float groundB = TryGround(b, out var pb, out _) ? pb.y : float.NegativeInfinity;
            centre.y += ItemRestPose.Lift(groundA, underA, groundB, underB);
            return new Pose(centre - rotation * (mesh.center * scale), rotation);
        }

        /// <summary>The nearest ground below <paramref name="at"/> + 1.5 m (within 3 m below it), with its normal.</summary>
        public static bool TryGround(Vector3 at, out Vector3 point, out Vector3 normal)
        {
            point = at;
            normal = Vector3.up;
            int n = Physics.RaycastNonAlloc(at + Vector3.up * Above, Vector3.down, Hits, Above + Below, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var c = Hits[i].collider;
                if (c is CharacterController || c.attachedRigidbody != null || c.GetComponentInParent<CombatFighter>() != null || c.GetComponentInParent<WorldItemView>() != null) continue;
                if (Hits[i].distance < best)
                {
                    best = Hits[i].distance;
                    point = Hits[i].point;
                    normal = Hits[i].normal;
                }
            }
            return best < float.MaxValue;
        }

        static Vector3 Axis(int i) => i == 0 ? Vector3.right : i == 1 ? Vector3.up : Vector3.forward;
    }
}
