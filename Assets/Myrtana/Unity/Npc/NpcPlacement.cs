using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Myrtana.Unity
{
    /// <summary>
    /// NPC bodies put somewhere (spawn, catch-up, teleport fallback) must not stand inside a static collider: NPCs walk by
    /// the ground ray (<see cref="NpcWalker"/>), and inside a two-sided rock mesh that ray hits the rock's inner faces, so
    /// the body never moves although its NavMesh path is complete (editor-runner-24: Geldern_Slave_01 inside
    /// G3_Object_Rockpile_02/03). A body whose capsule overlaps a static collider moves to the nearest free NavMesh point
    /// around it (rings up to <see cref="MaxRadius"/> m; logged). General rule, not per object [interp].
    /// </summary>
    public static class NpcPlacement
    {
        /// <summary>
        /// A free spot may lie at most <see cref="MaxDrop"/> below the body and <see cref="MaxRise"/> above it (fix-99: the
        /// same floor or the one below, never up onto a rock or a cave ceiling: "moved 2.8 m to the free spot" put
        /// creatures on top of the stone they spawned in).
        /// </summary>
        const float Radius = 0.3f, Lift = 0.45f, Height = 1.7f, MaxRadius = 8f, MaxDrop = 3f, MaxRise = 1f;
        static readonly float[] Rings = { 0.8f, 1.5f, 2.5f, 3.5f, 5f, 6.5f, MaxRadius };
        const int Directions = 12;
        static readonly Collider[] _overlaps = new Collider[16];

        /// <summary>Resolves an overlap of the body at <paramref name="feet"/>; true (and the free spot) when it moved.</summary>
        public static bool TryFreeSpot(Vector3 feet, Transform self, out Vector3 free, out string blocker)
        {
            free = feet;
            blocker = Blocker(feet, self);
            if (blocker == null) return false;
            foreach (float r in Rings)
                for (int k = 0; k < Directions; k++)
                {
                    float a = (k + (r > 1f ? 0.5f : 0f)) * Mathf.PI * 2f / Directions;
                    var c = feet + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * r;
                    Vector3 p;
                    if (NavMesh.SamplePosition(c, out var hit, 1.2f, NavMesh.AllAreas))
                    {
                        // fix-108: on the collision floor under the NavMesh point, not at the NavMesh's own height (Recast's
                        // detail mesh lies up to a few decimetres off a slope: spawned deer stood 0.2 m in the air)
                        p = hit.position;
                        if (NpcGround.TryFloor(p, 0.35f, out var floor, MaxDrop, useNavMesh: false) && Mathf.Abs(floor.Y - p.y) <= 0.6f) p.y = floor.Y;
                    }
                    else if (NpcWalker.TryGroundHeight(c, out float y, MaxRise)) p = new Vector3(c.x, y, c.z);
                    else continue;
                    if (p.y - feet.y > MaxRise || feet.y - p.y > MaxDrop) continue;
                    if (Blocker(p, self) != null) continue;
                    if (!CaveGround.MayStand(p)) continue; // fix-128: never onto a cave roof / wall top inside the hill
                    free = p;
                    return true;
                }
            return false;
        }

        /// <summary>
        /// Puts <paramref name="body"/> out of a static collider it stands in (<see cref="TryFreeSpot"/>); logs why.
        /// </summary>
        public static bool Resolve(Transform body, string why)
        {
            var at = body.position;
            if (!TryFreeSpot(at, body, out var free, out string blocker))
            {
                if (blocker != null) Debug.LogWarning($"[npc] {body.name} {why}: inside {blocker} at {at}, no free spot within {MaxRadius} m");
                // fix-128: not in a collider, but on a cave roof / wall top or in the hollow rock around a cave (the capsule
                // test sees no triangle there): onto the nearest true cave floor
                return CaveGround.Rescue(body, why);
            }
            body.position = free;
            Debug.Log($"[npc] {body.name} {why}: inside {blocker} at {at}, moved {Vector3.Distance(at, free):F1} m to the free spot {free}");
            return true;
        }

        /// <summary>The body's capsule at <paramref name="feet"/> overlaps no static collider.</summary>
        public static bool IsFree(Vector3 feet, Transform self) => Blocker(feet, self) == null;

        /// <summary>fix-151 (console <c>enemies</c>): the static collider the body's capsule at <paramref name="feet"/> is in (null: none).</summary>
        public static string BlockerAt(Vector3 feet, Transform self) => Blocker(feet, self);

        /// <summary>The name of a static collider the body's capsule at <paramref name="feet"/> overlaps (null = free).</summary>
        static string Blocker(Vector3 feet, Transform self)
        {
            var bottom = feet + Vector3.up * (Lift + Radius);
            var top = feet + Vector3.up * (Height - Radius);
            int n = Physics.OverlapCapsuleNonAlloc(bottom, top, Radius, _overlaps, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = _overlaps[i];
                if (c == null || c is CharacterController || c is TerrainCollider || c.attachedRigidbody != null) continue;
                if (self != null && c.transform.IsChildOf(self)) continue;
                if (c.GetComponentInParent<NpcView>() != null || c.GetComponentInParent<G3.Unity.G3Player>() != null) continue; // a body
                return c.name;
            }
            return null;
        }
    }
}
