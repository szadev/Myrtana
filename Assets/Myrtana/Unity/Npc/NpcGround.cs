using System.Collections.Generic;
using G3.Player;
using UnityEngine;
using UnityEngine.AI;

namespace Myrtana.Unity
{
    /// <summary>
    /// The floor an NPC body put somewhere stands on (fix-108, rules: <see cref="NpcGroundRules"/>): every static surface
    /// of the vertical column from above the body's reach down (one ray per surface: a ray returns one hit per collider,
    /// and a landscape cell holds a cave's roof and floor), the NavMesh height near it, then the pick. Characters, triggers
    /// and loose bodies are not floors. Used where a body is placed rather than walked (spawn, the shown simulated pose,
    /// wander points, free spots); the walker keeps its short step ray (<see cref="NpcWalker.StepProbe"/>).
    /// </summary>
    public static class NpcGround
    {
        const int MaxSurfaces = 12;
        const float NavSample = 2f;
        static readonly RaycastHit[] _hits = new RaycastHit[16];
        static readonly List<NpcGroundRules.Surface> _surfaces = new List<NpcGroundRules.Surface>(MaxSurfaces);
        static readonly List<Vector3> _normals = new List<Vector3>(MaxSurfaces);
        static readonly List<Collider> _colliders = new List<Collider>(MaxSurfaces);

        /// <summary>The picked floor: its height, face normal and slope (deg) and collider.</summary>
        public struct Floor
        {
            public float Y;
            public Vector3 Normal;
            public float SlopeDeg;
            public Collider Collider;
        }

        /// <summary>
        /// The floor under <paramref name="p"/> (<see cref="NpcGroundRules.Pick"/>); false when the column has none the rules accept.
        /// </summary>
        /// <param name="halfLength">Half the body's length (long animals) or its radius, m.</param>
        /// <param name="maxDown">Deepest first surface below that counts.</param>
        /// <param name="upLimit">≥ 0: any surface up to this far above (a point whose height is a guess); else the slope reach.</param>
        public static bool TryFloor(Vector3 p, float halfLength, out Floor floor, float maxDown = NpcWalker.FloorReach, float upLimit = -1f,
                                    bool useNavMesh = true)
        {
            floor = default;
            float above = Mathf.Max(upLimit, NpcGroundRules.MaxUpReach, NpcGroundRules.BuriedReach) + 0.05f;
            float navY = float.NaN;
            if (useNavMesh && NavMesh.SamplePosition(p, out var nav, NavSample, NavMesh.AllAreas)) navY = nav.position.y;
            // deeper than the first surface below this the rules never pick: stop the column there
            float stop = p.y - G3.Player.SlopeRules.StepHeight;
            if (!float.IsNaN(navY)) stop = Mathf.Min(stop, navY - NpcGroundRules.NavTolerance);
            Column(p, above, maxDown, stop);
            int i;
            for (int tries = 0; ; tries++)
            {
                if (!NpcGroundRules.Pick(p.y, _surfaces, halfLength, maxDown, navY, out i, upLimit)) return false;
                if (tries >= 4 || Allowed(p, _surfaces[i].Y)) break;
                _surfaces.RemoveAt(i);
                _normals.RemoveAt(i);
                _colliders.RemoveAt(i);
            }
            floor = new Floor { Y = _surfaces[i].Y, Normal = _normals[i], SlopeDeg = _surfaces[i].SlopeDeg, Collider = _colliders[i] };
            return true;
        }

        /// <summary>
        /// fix-128: a picked floor is never up through a ceiling (<see cref="GroundGuard"/>'s rule for the hero: something
        /// solid between the feet and a floor more than <see cref="CeilingCheckRise"/> above them; less is a body sunk into a
        /// thick floor or a slope, lifted as before) and never a cave roof / wall top inside the
        /// hill (<see cref="CaveGround.MayStand"/>).
        /// </summary>
        const float CeilingCheckRise = 1f;

        static bool Allowed(Vector3 p, float y)
        {
            float rise = y - p.y;
            if (rise > CeilingCheckRise)
            {
                bool back = Physics.queriesHitBackfaces;
                Physics.queriesHitBackfaces = true;
                try
                {
                    NavStats.GroundRays++;
                    int n = Physics.RaycastNonAlloc(p + Vector3.up * 0.05f, Vector3.up, _hits, rise - 0.2f, ~0, QueryTriggerInteraction.Ignore);
                    for (int k = 0; k < n; k++)
                        if (!(_hits[k].collider is CharacterController) && _hits[k].rigidbody == null) return false;
                }
                finally { Physics.queriesHitBackfaces = back; }
            }
            return CaveGround.MayStand(new Vector3(p.x, y, p.z));
        }

        /// <summary>
        /// fix-163 (<see cref="BodySinkRules.StationFloor"/>): the floor a station slot at <paramref name="slot"/> stands on
        /// (G3 PutToGround: the highest floor within a step over or under it), never a cave roof's top. False: keep the slot's height.
        /// </summary>
        public static bool TryStationFloor(Vector3 slot, float halfLength, out float y)
        {
            y = slot.y;
            Column(slot, NpcGroundRules.MaxUpReach + 0.05f, SlopeRules.StepHeight + 0.05f, slot.y - SlopeRules.StepHeight);
            if (!BodySinkRules.StationFloor(slot.y, _surfaces, halfLength, out int i)) return false;
            if (!CaveGround.MayStand(new Vector3(slot.x, _surfaces[i].Y, slot.z))) return false;
            y = _surfaces[i].Y;
            return true;
        }

        /// <summary>
        /// fix-163 safety net (<see cref="BodySinkRules.SinkLift"/>): how far a body whose feet are at <paramref name="feet"/>
        /// must come up because they are in the floor directly above them (0: standing on something, or not sunk deeper than
        /// the tolerance). <paramref name="what"/>: the floor's collider name.
        /// </summary>
        public static float SinkLift(Vector3 feet, out string what)
        {
            what = null;
            Column(feet, BodySinkRules.Reach + 0.05f, 1f, feet.y - 1f);
            float lift = BodySinkRules.SinkLift(feet.y, _surfaces, out int i);
            if (lift <= 0f || i < 0) return 0f;
            if (!CaveGround.MayStand(feet + Vector3.up * lift)) return 0f;
            what = _colliders[i] != null ? _colliders[i].name : "?";
            return lift;
        }

        /// <summary>The floor's height under <paramref name="p"/> (<see cref="TryFloor"/>), else <see cref="NpcWalker.TryFloorHeight"/>, else <paramref name="fallback"/>.</summary>
        public static float Height(Vector3 p, float halfLength, float fallback, float upLimit = -1f)
        {
            if (TryFloor(p, halfLength, out var f, NpcWalker.FloorReach, upLimit)) return f.Y;
            return NpcWalker.FloorHeight(p, fallback);
        }

        /// <summary>
        /// Collects the column's up-facing static surfaces from <paramref name="above"/> over p down to <paramref name="below"/>
        /// under it, ending with the first surface lower than <paramref name="stopY"/>.
        /// </summary>
        static void Column(Vector3 p, float above, float below, float stopY)
        {
            _surfaces.Clear();
            _normals.Clear();
            _colliders.Clear();
            var origin = p + Vector3.up * above;
            float left = above + Mathf.Min(below, NpcWalker.FloorReach);
            while (left > 0f && _surfaces.Count < MaxSurfaces)
            {
                NavStats.GroundRays++;
                int n = Physics.RaycastNonAlloc(origin, Vector3.down, _hits, left, ~0, QueryTriggerInteraction.Ignore);
                int best = -1;
                for (int i = 0; i < n; i++)
                {
                    var h = _hits[i];
                    if (h.collider is CharacterController || h.rigidbody != null) continue;
                    if (best < 0 || h.distance < _hits[best].distance) best = i;
                }
                if (best < 0) break;
                var hit = _hits[best];
                if (hit.normal.y > 0f)
                {
                    _surfaces.Add(new NpcGroundRules.Surface(hit.point.y, Vector3.Angle(hit.normal, Vector3.up)));
                    _normals.Add(hit.normal);
                    _colliders.Add(hit.collider);
                    if (hit.point.y < stopY) break;
                }
                float step = hit.distance + 0.02f;
                origin += Vector3.down * step;
                left -= step;
            }
        }

        /// <summary>Half the length of <paramref name="body"/>'s fighter capsule (long animals), else its radius; 0.35 m without one.</summary>
        public static float HalfLengthOf(Component body)
        {
            var b = body != null ? body.GetComponentInChildren<CombatBody>() : null;
            return b != null ? b.HalfLength : 0.35f;
        }
    }
}
