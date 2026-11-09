using System.Collections.Generic;
using G3.Player;
using UnityEngine;
using UnityEngine.AI;

namespace Myrtana.Unity
{
    /// <summary>
    /// fix-128 (owner: "again in a cave, a wolf is somewhere inside the rock"): inside caves NPC bodies stand only on true
    /// cave floors (<see cref="CaveFloorRules"/>). Montera_Wolf_01#0c6fefe2 stood at (82.2, 65.5, 36.9), 4 m behind the
    /// wall of Dungeon_V01_S01_05 inside the hill: its simulated wander point (spawn + a random offset at the spawn's
    /// height) lay in the rock, the shown pose found no floor in its column and kept that height, and the capsule test
    /// (<see cref="NpcPlacement"/>) cannot see "inside" a hollow two-sided mesh. The judge here: inside a cave's bounds a
    /// floor within <see cref="CaveFloorRules.FloorReach"/> under the feet, and the first cave sheet / landscape over it a
    /// ceiling, not the underside of the hill's top (an odd number of landscape surfaces up to the sky). <see cref="TrySnap"/> finds the
    /// nearest true floor on the NavMesh, never up through a ceiling (<see cref="GroundGuard"/>'s rule for the hero).
    /// </summary>
    public static class CaveGround
    {
        /// <summary>A cave sheet this close to a place puts it within the cave's bounds, m.</summary>
        const float CaveProbe = 24f;
        const int MaxAbove = 8, MaxHits = 16;
        /// <summary>The column over a place is followed this far up (to the sky over the highest hill), m.</summary>
        const float SkyReach = 600f;
        /// <summary>The in-cave answer per 8 m cell is kept this long (cells stream in and out), s.</summary>
        const float CacheSeconds = 10f;
        const float CacheCell = 8f;
        static readonly float[] Rings = { 0f, 1.5f, 3f, 5f, 8f, 12f, 16f };
        const int Directions = 8;

        static readonly Collider[] _near = new Collider[64];
        static readonly RaycastHit[] _hits = new RaycastHit[16];
        static readonly List<(float dy, CaveFloorRules.Kind kind)> _all = new List<(float, CaveFloorRules.Kind)>(MaxHits);
        static readonly List<CaveFloorRules.Above> _above = new List<CaveFloorRules.Above>(MaxAbove);
        static readonly Dictionary<Collider, CaveFloorRules.Kind> _kinds = new Dictionary<Collider, CaveFloorRules.Kind>();
        static readonly Dictionary<(int, int, int), (bool cave, float at)> _inCave = new Dictionary<(int, int, int), (bool, float)>();
        static readonly List<(float dx, float dy, float dz, bool ok)> _candidates = new List<(float, float, float, bool)>();
        static readonly List<Vector3> _points = new List<Vector3>();

        /// <summary>Bodies put onto a true cave floor since the start (runner probe <c>cavesnaps</c>).</summary>
        public static int Snaps;
        /// <summary>Places judged a roof top / in the rock since the start (runner probe <c>caverefusals</c>).</summary>
        public static int Refusals;

        /// <summary>What a collider is: a cave sheet (G3 *_Dungeon_* mesh), the landscape, or anything else.</summary>
        public static CaveFloorRules.Kind KindOf(Collider c)
        {
            if (c == null) return CaveFloorRules.Kind.Other;
            if (_kinds.TryGetValue(c, out var k)) return k;
            if (c is TerrainCollider) k = CaveFloorRules.Kind.Landscape;
            else
            {
                k = CaveFloorRules.KindOf(c.name);
                if (k == CaveFloorRules.Kind.Other && c.transform.parent != null) k = CaveFloorRules.KindOf(c.transform.parent.name); // "G3Collision" children
            }
            if (_kinds.Count > 20000) _kinds.Clear();
            _kinds[c] = k;
            return k;
        }

        /// <summary>A cave sheet lies within <see cref="CaveProbe"/> m of <paramref name="p"/> (cached per 8 m cell).</summary>
        public static bool InCave(Vector3 p)
        {
            var key = (Mathf.FloorToInt(p.x / CacheCell), Mathf.FloorToInt(p.y / CacheCell), Mathf.FloorToInt(p.z / CacheCell));
            float now = Time.time;
            if (_inCave.TryGetValue(key, out var v) && now - v.at < CacheSeconds) return v.cave;
            bool cave = false;
            int n = Physics.OverlapSphereNonAlloc(p, CaveProbe, _near, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n && !cave; i++)
                if (_near[i] is MeshCollider && KindOf(_near[i]) == CaveFloorRules.Kind.Cave) cave = true;
            if (_inCave.Count > 4096) _inCave.Clear();
            _inCave[key] = (cave, now);
            return cave;
        }

        /// <summary>Judges the place <paramref name="feet"/> (<see cref="CaveFloorRules.Classify"/>); <paramref name="ceiling"/>:
        /// the first surface over the feet, m (null: none within the cave height).</summary>
        public static CaveFloorRules.Stand Judge(Vector3 feet, out float? ceiling)
        {
            ceiling = null;
            if (!InCave(feet)) return CaveFloorRules.Stand.Open;
            bool back = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true; // the hill and one-sided sheets seen from below
            try
            {
                float? drop = null;
                var floorKind = CaveFloorRules.Kind.Other;
                if (Nearest(feet + Vector3.up * 0.3f, Vector3.down, CaveFloorRules.FloorReach + 0.3f, out var floor))
                {
                    drop = feet.y - floor.point.y;
                    floorKind = KindOf(floor.collider);
                }
                float baseY = drop.HasValue ? feet.y - drop.Value : feet.y;
                // every surface up to the sky: a landscape surface faces up (the hill's top seen from inside the rock) when
                // an odd number of landscape surfaces lie at and above it (the hill is closed: its top, an overhang's
                // underside, ...); a cave sheet counts as a ceiling (which side of a sheet is walkable needs its render
                // mesh: CellNavMeshes; stacked caves farther apart than MaxCaveHeight are still told apart)
                _all.Clear();
                var origin = new Vector3(feet.x, baseY + CaveFloorRules.SameSheet, feet.z);
                float left = SkyReach;
                while (left > 0f && _all.Count < MaxHits && Nearest(origin, Vector3.up, left, out var h))
                {
                    _all.Add((h.point.y - baseY, KindOf(h.collider)));
                    float step = h.distance + 0.03f;
                    origin += Vector3.up * step;
                    left -= step;
                }
                if (_all.Count > 0) ceiling = baseY + _all[0].dy - feet.y;
                _above.Clear();
                int landAbove = 0;
                for (int i = _all.Count - 1; i >= 0; i--) if (_all[i].kind == CaveFloorRules.Kind.Landscape) landAbove++;
                for (int i = 0; i < _all.Count && _above.Count < MaxAbove; i++)
                {
                    var (dy, kind) = _all[i];
                    bool up = kind == CaveFloorRules.Kind.Landscape && landAbove % 2 == 1;
                    if (kind == CaveFloorRules.Kind.Landscape) landAbove--;
                    _above.Add(new CaveFloorRules.Above(dy, kind, up));
                }
                var st = CaveFloorRules.Classify(drop, floorKind, true, _above);
                if (!CaveFloorRules.MayStand(st)) Refusals++;
                return st;
            }
            finally { Physics.queriesHitBackfaces = back; }
        }

        /// <summary>A body may stand at <paramref name="feet"/> (no roof top, not in the rock).</summary>
        public static bool MayStand(Vector3 feet)
        {
            if (!InCave(feet)) return true;
            // per 1 m column and 0.5 m of height for a few seconds: a walking body's floor probe asks every frame
            var key = (Mathf.FloorToInt(feet.x), Mathf.FloorToInt(feet.y * 2f), Mathf.FloorToInt(feet.z));
            float now = Time.time;
            if (_may.TryGetValue(key, out var v) && now - v.at < MayStandSeconds) return v.ok;
            bool ok = CaveFloorRules.MayStand(Judge(feet, out _));
            if (_may.Count > 8192) _may.Clear();
            _may[key] = (ok, now);
            return ok;
        }

        /// <summary>
        /// fix-160 (<see cref="CaveFloorRules.StepAllowed"/>): a step from <paramref name="from"/> to <paramref name="to"/>
        /// never rises into the rock / onto a roof top inside a cave. Cheap off a rise and outside caves (cached judges).
        /// </summary>
        public static bool StepAllowed(Vector3 from, Vector3 to)
        {
            float rise = to.y - from.y;
            if (rise < CaveFloorRules.StepRiseChecked || !InCave(to)) return true;
            bool toOk = MayStand(to);
            if (toOk) return true;
            bool ok = CaveFloorRules.StepAllowed(rise, MayStand(from), toOk);
            if (!ok) StepRefusals++;
            return ok;
        }

        /// <summary>Steps refused by <see cref="StepAllowed"/> since the start.</summary>
        public static int StepRefusals;

        const float MayStandSeconds = 5f;
        static readonly Dictionary<(int, int, int), (bool ok, float at)> _may = new Dictionary<(int, int, int), (bool, float)>();

        /// <summary>
        /// The nearest true floor for a body at <paramref name="feet"/> that may not stand there: NavMesh points around it
        /// (rings up to 16 m), each judged and free of static colliders (<see cref="NpcPlacement.IsFree"/>), picked by
        /// <see cref="CaveFloorRules.Snap"/> (same level first, never up through a ceiling; a body in the rock: nearest).
        /// </summary>
        public static bool TrySnap(Vector3 feet, Transform self, out Vector3 to) => TrySnap(feet, self, out to, out _);

        /// <param name="stands">The body may stand at <paramref name="feet"/> (nothing to snap).</param>
        public static bool TrySnap(Vector3 feet, Transform self, out Vector3 to, out bool stands)
        {
            to = feet;
            var st = Judge(feet, out float? ceiling);
            stands = CaveFloorRules.MayStand(st);
            if (stands) return false;
            bool hasFloor = st != CaveFloorRules.Stand.NoFloor;
            _candidates.Clear();
            _points.Clear();
            int best = -1;
            foreach (float r in Rings)
            {
                int dirs = r <= 0f ? 1 : Directions;
                for (int k = 0; k < dirs; k++)
                {
                    float a = (k + (r > 2f ? 0.5f : 0f)) * Mathf.PI * 2f / Directions;
                    var c = feet + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r;
                    if (!NavMesh.SamplePosition(c, out var hit, 4f, NavMesh.AllAreas)) continue;
                    var p = hit.position;
                    if (NpcWalker.TryGroundHeight(p, out float gy, 0.6f) && Mathf.Abs(gy - p.y) <= 0.6f) p.y = gy;
                    bool ok = CaveFloorRules.MayStand(Judge(p, out _)) && NpcPlacement.IsFree(p, self);
                    _points.Add(p);
                    _candidates.Add((p.x - feet.x, p.y - feet.y, p.z - feet.z, ok));
                }
                best = CaveFloorRules.Snap(_candidates, ceiling, hasFloor);
                if (best >= 0) break; // the nearest ring with a true floor
            }
            if (best < 0) return false;
            to = _points[best];
            return true;
        }

        /// <summary>Puts <paramref name="body"/> onto the nearest true cave floor when it stands on a roof top or in the
        /// rock (<see cref="TrySnap"/>); logs it. True when it moved.</summary>
        public static bool Rescue(Transform body, string why) => Rescue(body, why, true) == Outcome.Moved;

        public enum Outcome
        {
            /// <summary>The body may stand where it is.</summary>
            Fine = 0,
            /// <summary>Moved onto a true cave floor.</summary>
            Moved = 1,
            /// <summary>On a roof top / in the rock with no true floor within reach: left where it is.</summary>
            NoFloor = 2,
        }

        /// <summary><see cref="Rescue(Transform, string)"/> with the log left to the caller (fix-133: the cave guard rate-limits
        /// it per NPC, <see cref="CaveGuardBudget"/>).</summary>
        public static Outcome Rescue(Transform body, string why, bool log)
        {
            var at = body.position;
            if (!TrySnap(at, body, out var to, out bool stands))
            {
                if (stands) return Outcome.Fine;
                if (log) Debug.LogWarning($"[npc] {body.name} {why}: on a cave roof / in the rock at {at}, no true cave floor within 16 m");
                return Outcome.NoFloor;
            }
            body.position = to;
            Physics.SyncTransforms();
            Snaps++;
            if (log) Debug.Log($"[npc] {body.name} {why}: on a cave roof / in the rock at {at}, moved {Vector3.Distance(at, to):F1} m to the cave floor {to}");
            return Outcome.Moved;
        }

        /// <summary>The nearest static hit along the ray (characters and loose bodies skipped).</summary>
        static bool Nearest(Vector3 origin, Vector3 dir, float length, out RaycastHit best)
        {
            best = default;
            NavStats.GroundRays++;
            int n = Physics.RaycastNonAlloc(origin, dir, _hits, length, ~0, QueryTriggerInteraction.Ignore);
            int bi = -1;
            for (int i = 0; i < n; i++)
            {
                var h = _hits[i];
                if (h.collider is CharacterController || h.rigidbody != null) continue;
                if (bi < 0 || h.distance < _hits[bi].distance) bi = i;
            }
            if (bi < 0) return false;
            best = _hits[bi];
            return true;
        }
    }
}
