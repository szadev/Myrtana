using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// The floor height under one NPC body (<see cref="NpcGround.TryFloor"/>, fix-108: the column's surface nearest the
    /// shown pose within the body's slope reach, the NavMesh's floor, else the first surface below however far: fix-60, a
    /// Near body on its straight travel line from a tower top hung in the air), cast again only when the body has moved
    /// or <see cref="RefreshSeconds"/> passed: an NPC that stands still costs one probe every few seconds instead of one
    /// per frame (Docs/performance.md, NpcView), and a floor found before its cell's collision was complete does not stay
    /// for good (fix-108: grazing creatures never move, so their first height was kept). A moving body skips the NavMesh
    /// query (cost); a standing one uses it. A miss (no collider loaded there yet) is not kept, so the ground is found
    /// once its cell arrives.
    /// </summary>
    public sealed class GroundProbe
    {
        const float SameSpot = 0.005f; // m
        const float RefreshSeconds = 3f;

        bool _valid;
        float _x, _z, _y, _refY, _at;

        /// <summary>The floor's slope at the last probe (deg); 0 before the first or after a miss.</summary>
        public float SlopeDeg { get; private set; }
        /// <summary>The floor's face normal at the last probe (up before the first or after a miss).</summary>
        public Vector3 Normal { get; private set; } = Vector3.up;

        /// <param name="upLimit">fix-181: ≥ 0 when <paramref name="p"/>'s height is only a guess (a wanderer's simulated pose
        /// carries its spawn's height: on a slope the floor of a point 3-5 m away lies metres above it); any floor up to this far
        /// above counts (<see cref="NpcGround.TryFloor"/>). Else the slope reach.</param>
        public float HeightAt(Vector3 p, float fallback, float halfLength = 0.35f, float upLimit = -1f)
        {
            bool same = _valid && Mathf.Abs(p.x - _x) < SameSpot && Mathf.Abs(p.z - _z) < SameSpot && Mathf.Abs(p.y - _refY) < 0.5f;
            if (same && Time.time - _at < RefreshSeconds) return _y;
            _at = Time.time;
            if (NpcGround.TryFloor(p, halfLength, out var floor, NpcWalker.FloorReach, upLimit, useNavMesh: same || !_valid))
            {
                _valid = true;
                _y = floor.Y;
                SlopeDeg = floor.SlopeDeg;
                Normal = floor.Normal;
            }
            else
            {
                _valid = NpcWalker.TryFloorHeight(p, out _y);
                SlopeDeg = 0f;
                Normal = Vector3.up;
            }
            if (!_valid) return fallback;
            _x = p.x; _z = p.z; _refY = p.y;
            return _y;
        }

        /// <summary>Forgets the cached floor (the body was put somewhere else).</summary>
        public void Reset() => _valid = false;
    }
}
