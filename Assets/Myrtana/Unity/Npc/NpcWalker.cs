using System.Collections.Generic;
using UnityEngine;
using G3.Player;
using UnityEngine.AI;

namespace Myrtana.Unity
{
    /// <summary>
    /// Walks an NPC view to a destination: along a NavMesh path (<see cref="CellNavMeshes"/>: one NavMesh over the cells
    /// around the player, so paths cross cell borders), otherwise in a straight line. Without a complete path (the cells
    /// are not in the NavMesh yet, or the path is partial) it plans again every <see cref="ReplanSeconds"/>, so a walker
    /// switches to the NavMesh as soon as it is there. The feet follow the ground (ray down onto the cell
    /// collision); the body turns towards where it goes. Gives up after <see cref="StuckSeconds"/> without progress
    /// (the caller then puts the NPC at its destination, as G3 does with NPCs that cannot reach their point).
    /// A path that ends partial at a terrace edge gets a one-way drop link (<see cref="NavDropLinks"/>: G3 bodies fall
    /// off edges with no floor within the step height); the walker crosses a link segment as a fall
    /// (<see cref="NavDropRules.FallY"/>, at most <see cref="NavDropRules.MaxDrop"/>); every other drop deeper than
    /// <see cref="MaxStepDown"/> is still refused.
    /// </summary>
    public sealed class NpcWalker
    {
        const float ArriveDistance = 0.2f;
        const float StuckSeconds = 4f;
        const float StuckProgress = 0.5f;
        const float TurnSpeed = 360f;
        const float NavSampleDistance = 2f;
        const float ReplanSeconds = 1f;
        /// <summary>Lowest step down per move (m); a steeper drop is a ledge the body does not walk off.</summary>
        const float MaxStepDown = 1.5f;
        /// <summary>Steepest walkable descent (drop per metre walked) when a long frame covers more ground.</summary>
        const float MaxDescentPerMetre = 1.2f;
        /// <summary>Below the last NavMesh point by more than this (and near it): the body fell, put it back.</summary>
        const float FallMetres = 6f, FallRecoverRadius = 40f, FallCheckSeconds = 0.5f;
        /// <summary>A far destination off the NavMesh: plan to a NavMesh point this far towards it instead (m).</summary>
        static readonly float[] Waypoints = { 40f, 25f, 12f };
        const float WaypointSample = 6f;
        /// <summary>
        /// A walker searches drop links at most this often (s): every walker with a partial path re-plans each second and a
        /// failed search (a fan of NavMesh and physics rays) was repeated whenever the NavMesh changed (fix-22 perf).
        /// </summary>
        const float LinkSearchSeconds = 6f;
        float _nextLinkSearch;
        /// <summary>
        /// A straight walk (<see cref="SetDirect"/>) feels for a wall this high above the feet (above the 65 cm step
        /// height: steps and stair risers pass under it) and this far beyond the body's front (its radius).
        /// </summary>
        const float WallProbeHeight = 0.9f, WallProbeReach = 0.35f;

        readonly List<Vector3> _corners = new List<Vector3>();
        /// <summary>Per corner: the segment that ends there is a drop link (from the corner before it).</summary>
        readonly List<bool> _drops = new List<bool>();
        Vector3 _planStart;
        NavMeshPath _path; // created on first use: Unity forbids it in constructors of MonoBehaviour fields
        Vector3 _destination;
        int _corner;
        float _replanTimer;
        float _stuckTimer;
        Vector3 _stuckFrom;
        Vector3 _lastOnMesh;
        bool _hasLastOnMesh;
        float _fallCheck;

        /// <summary>
        /// The body treats water as solid ground (G3 PSCharacterMovement.TreatWaterAsSolid: bloodflies, which the scripts
        /// exempt from clearing the flag): it crosses deep water at the surface instead of stopping at its edge.
        /// </summary>
        public bool OverWater { get; set; }

        /// <summary>The body's gCCharacterMovement_PS SwimDepth (m, <see cref="G3.Player.SwimRules.NpcSwimDepth"/>).</summary>
        public float SwimDepth { get; set; } = G3.Player.SwimRules.HumanSwimDepth;

        public bool HasDestination { get; private set; }
        /// <summary>True when the last plan used a NavMesh path (false = straight line).</summary>
        public bool OnNavMesh { get; private set; }
        /// <summary>True when the last plan reached the destination on the NavMesh.</summary>
        public bool PathComplete { get; private set; }
        /// <summary>The last plan started on the NavMesh (the body stood on it; false = its cell is not built yet).</summary>
        public bool StartOnNavMesh { get; private set; }

        /// <summary>
        /// The last NavMesh point of the plan (where a partial path ends, before the straight rest to the destination);
        /// false when the plan used no NavMesh path (fix-142: the combat chase judges whether that point is in reach).
        /// </summary>
        public bool TryNavEnd(out Vector3 end)
        {
            end = default;
            if (!HasDestination || !OnNavMesh || _corners.Count < 2) return false;
            end = _corners[_corners.Count - 2];
            return true;
        }

        /// <summary>How the last plan goes (for the logs: why a walker stands).</summary>
        public string Plan { get; private set; } = "none";

        /// <summary>The point the walker heads for now (G3 GetCurrentDestinationPoint): the current path corner, else the destination.</summary>
        public Vector3 CurrentPoint => HasDestination && _corner >= 0 && _corner < _corners.Count ? _corners[_corner] : _destination;

        /// <param name="keepProgress">A re-plan towards the same goal (an escort's periodic re-path): the stuck rule keeps
        /// counting, so a walker that cannot get on is still noticed (editor-runner-20: Xardas re-planned every 0.5 s and
        /// stood for 8 minutes).</param>
        public void SetDestination(Vector3 from, Vector3 destination, bool keepProgress = false)
        {
            _direct = false;
            using var marker = NavStats.PlanMarker.Auto();
            long t0 = NavStats.Now;
            NavStats.Plans++;
            PlanPath(from, destination, keepProgress);
            NavStats.PlanTicks += NavStats.Now - t0;
        }

        bool TryLink(Vector3 from, Vector3 destination)
        {
            if (Time.time < _nextLinkSearch) return false;
            _nextLinkSearch = Time.time + LinkSearchSeconds;
            return NavDropLinks.TryLinkTowards(from, destination);
        }

        void PlanPath(Vector3 from, Vector3 destination, bool keepProgress)
        {
            float stuckTimer = _stuckTimer;
            var stuckFrom = _stuckFrom;
            bool keep = keepProgress && HasDestination;
            _destination = destination;
            _corners.Clear();
            _drops.Clear();
            OnNavMesh = false;
            PathComplete = false;
            _replanTimer = ReplanSeconds;
            _path ??= new NavMeshPath();
            bool onMesh = NavMesh.SamplePosition(from, out var a, NavSampleDistance, NavMesh.AllAreas);
            StartOnNavMesh = onMesh;
            _planStart = onMesh ? a.position : from;
            bool destOnMesh = NavMesh.SamplePosition(destination, out var b, NavSampleDistance, NavMesh.AllAreas);
            bool planned = onMesh && destOnMesh && NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, _path) &&
                           _path.status != NavMeshPathStatus.PathInvalid;
            // Partial at a terrace edge (a NavMesh island: editor-runner-20 Xardas, Geldern_Slave_01, Harek): a drop link
            // down the step, then the same query again (it routes over the link).
            if (planned && _path.status == NavMeshPathStatus.PathPartial && TryLink(a.position, b.position))
                planned = NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, _path) && _path.status != NavMeshPathStatus.PathInvalid;
            if (planned)
            {
                AddCorners(_path.corners);
                OnNavMesh = true;
                PathComplete = _path.status == NavMeshPathStatus.PathComplete;
                int links = 0;
                foreach (bool d in _drops) if (d) links++;
                Plan = (PathComplete ? "path" : $"partial path ({_path.corners.Length} corners)") + (links > 0 ? $", {links} drop link(s)" : "");
            }
            else if (onMesh && TryPlanTowards(a.position, destination))
            {
                // The destination lies beyond the loaded NavMesh (a guide's far target): follow the NavMesh to a point on
                // the way instead of a straight line that can lead off a plateau (editor-runner-18: Xardas). Re-planned.
                AddCorners(_path.corners);
                OnNavMesh = true;
            }
            else Plan = onMesh ? "straight line (no NavMesh way towards the target)" : "straight line (off the NavMesh)";
            // No NavMesh here yet (the cell is still being added) or a partial path: the rest is a straight line until the
            // next plan (Step re-plans every ReplanSeconds).
            _corners.Add(destination);
            _drops.Add(false);
            _corner = 0;
            _stuckTimer = keep ? stuckTimer : 0;
            _stuckFrom = keep ? stuckFrom : from;
            HasDestination = true;
        }

        void AddCorners(Vector3[] corners)
        {
            for (int i = 1; i < corners.Length; i++)
            {
                _corners.Add(corners[i]);
                _drops.Add(NavDropLinks.IsLink(corners[i - 1], corners[i]));
            }
        }

        /// <summary>
        /// Walks straight to <paramref name="destination"/> without the NavMesh: a leg of a G3 navigation route along its
        /// path ribbon or across its zone (<see cref="Myrtana.Simulation.NavRouteFollower.Ribbon"/>). The feet follow the
        /// ground and may go down any slope the designer's path takes (no ledge refusal); deep water still stops the body.
        /// </summary>
        public void SetDirect(Vector3 from, Vector3 destination, bool keepProgress = false)
        {
            float stuckTimer = _stuckTimer;
            var stuckFrom = _stuckFrom;
            bool keep = keepProgress && HasDestination;
            _direct = true;
            _destination = destination;
            _corners.Clear();
            _drops.Clear();
            _corners.Add(destination);
            _drops.Add(false);
            _corner = 0;
            _planStart = from;
            OnNavMesh = false;
            PathComplete = true; // no NavMesh re-plans
            Plan = "straight along the G3 nav route";
            _stuckTimer = keep ? stuckTimer : 0;
            _stuckFrom = keep ? stuckFrom : from;
            HasDestination = true;
        }

        bool _direct;

        /// <summary>The current destination was set by <see cref="SetDirect"/>.</summary>
        public bool Direct => HasDestination && _direct;

        /// <summary>Stops walking; the caller may place the body anywhere (teleport), so the fall check starts afresh.</summary>
        public void Clear()
        {
            HasDestination = false;
            _hasLastOnMesh = false;
        }

        /// <summary>A complete NavMesh path from <paramref name="from"/> to a NavMesh point at the body's height on the way to <paramref name="destination"/>.</summary>
        bool TryPlanTowards(Vector3 from, Vector3 destination)
        {
            var flat = new Vector3(destination.x - from.x, 0, destination.z - from.z);
            float dist = flat.magnitude;
            if (dist < 1f) return false;
            var dir = flat / dist;
            foreach (float ahead in Waypoints)
            {
                if (ahead >= dist) continue;
                var probe = from + dir * ahead;
                if (NavMesh.SamplePosition(probe, out var w, WaypointSample, NavMesh.AllAreas) &&
                    NavMesh.CalculatePath(from, w.position, NavMesh.AllAreas, _path) && _path.status == NavMeshPathStatus.PathComplete)
                {
                    Plan = $"towards a NavMesh point {ahead:0} m on";
                    return true;
                }
            }
            // No connected point on the straight way (a NavMesh island, editor-runner-20: Xardas' plateau): a drop link down
            // the terrace towards the target, then a point on the way again.
            if (TryLink(from, destination))
                foreach (float ahead in Waypoints)
                {
                    if (ahead >= dist) continue;
                    if (NavMesh.SamplePosition(from + dir * ahead, out var w, WaypointSample * 2, NavMesh.AllAreas) &&
                        NavMesh.CalculatePath(from, w.position, NavMesh.AllAreas, _path) && _path.status == NavMeshPathStatus.PathComplete)
                    {
                        Plan = $"towards a NavMesh point {ahead:0} m on, over a drop link";
                        return true;
                    }
                }
            // Else go as far as the connected NavMesh goes towards the target (the partial path's end, the reachable point
            // closest to it).
            float best = float.MaxValue;
            Vector3 bestEnd = from;
            foreach (float ahead in Waypoints)
            {
                if (ahead >= dist) continue;
                var probe = from + dir * ahead;
                if (!NavMesh.SamplePosition(probe, out var w, WaypointSample * 2, NavMesh.AllAreas) ||
                    !NavMesh.CalculatePath(from, w.position, NavMesh.AllAreas, _path) || _path.status == NavMeshPathStatus.PathInvalid)
                    continue;
                var c = _path.corners;
                if (c.Length < 2) continue;
                var end = c[c.Length - 1];
                float left = new Vector2(destination.x - end.x, destination.z - end.z).magnitude;
                if (left < best) { best = left; bestEnd = end; }
            }
            if (best < dist - 1f && NavMesh.CalculatePath(from, bestEnd, NavMesh.AllAreas, _path) && _path.status != NavMeshPathStatus.PathInvalid)
            {
                Plan = $"to the connected NavMesh's closest point, {best:0} m from the target";
                return true;
            }
            return false;
        }

        /// <summary>
        /// Every <see cref="FallCheckSeconds"/> while walking: a body that dropped more than <see cref="FallMetres"/> below
        /// the NavMesh point recorded at the last check (no walk descends that fast; it fell off a ledge or was snapped
        /// onto a lower surface) walks on from the NavMesh where it landed, or is put back at that point when there is no
        /// NavMesh below it (fix-99); otherwise its NavMesh point is recorded. True when it moved the body.
        /// </summary>
        bool RecoverFall(Transform body, float dt)
        {
            if ((_fallCheck -= dt) > 0) return false;
            _fallCheck = FallCheckSeconds;
            var p = body.position;
            bool nearMesh = NavMesh.SamplePosition(p, out var hit, NavSampleDistance, NavMesh.AllAreas);
            if (_hasLastOnMesh && p.y < _lastOnMesh.y - FallMetres &&
                new Vector2(p.x - _lastOnMesh.x, p.z - _lastOnMesh.z).sqrMagnitude <= FallRecoverRadius * FallRecoverRadius)
            {
                // Landed on walkable ground (NavMesh at its own level): G3 bodies stay where they fell (fix-99: putting
                // them back up lifted a varan from the cave floor into the ceiling, whose back had NavMesh). Only a
                // body with no NavMesh around it (fell through the world, into a pit) goes back.
                // fix-128: not where the cave rules forbid it (a roof or wall top inside the hill: put back instead)
                if (nearMesh && Mathf.Abs(hit.position.y - p.y) <= NavSampleDistance && CaveGround.MayStand(p))
                {
                    Debug.Log($"[npc] {body.name} fell {_lastOnMesh.y - p.y:F1} m: walks on from the NavMesh below at {hit.position}");
                    _lastOnMesh = hit.position;
                    SetDestination(p, _destination);
                    return false;
                }
                Debug.Log($"[npc] {body.name} fell {_lastOnMesh.y - p.y:F1} m: back to the NavMesh at {_lastOnMesh}");
                body.position = _lastOnMesh;
                // fix-108: never back into a collider (a NavMesh point recorded beside a rock or under a cave wall): out to
                // the nearest free spot (NpcPlacement, the capsule test)
                Physics.SyncTransforms();
                NpcPlacement.Resolve(body, "fall recovery");
                SetDestination(body.position, _destination);
                return true;
            }
            if (nearMesh)
            {
                _lastOnMesh = hit.position;
                _hasLastOnMesh = true;
            }
            return false;
        }

        /// <summary>How far above the feet the floor ray of a move of <paramref name="move"/> metres starts (fix-99).</summary>
        public static float StepProbe(float move) => Mathf.Clamp(Mathf.Max(G3.Player.SlopeRules.StepHeight, move * MaxDescentPerMetre), 0f, 1.5f);

        public enum Result { Walking, Arrived, Stuck }

        /// <summary>The walker heads along a NavMesh path segment now (not the straight rest towards the destination).</summary>
        bool OnNavSegment => OnNavMesh && (_corner < _corners.Count - 1 || PathComplete);

        /// <summary>Moves <paramref name="body"/> one frame at <paramref name="speed"/> m/s.</summary>
        /// <param name="walksTo">The character the walk goes to (a follower's leader, a watched fighter): G3 never evades
        /// the entity an NPC walks to (CheckAllControlledDCCs skips it); null = none.</param>
        public Result Step(Transform body, float speed, float dt, Transform walksTo = null)
        {
            if (!HasDestination) return Result.Arrived;
            using var marker = s_stepMarker.Auto();
            RecoverFall(body, dt);
            if (!PathComplete && (_replanTimer -= dt) <= 0)
            {
                float stuck = _stuckTimer;
                var stuckFrom = _stuckFrom;
                SetDestination(body.position, _destination);
                _stuckTimer = stuck; // re-planning is not progress
                _stuckFrom = stuckFrom;
            }
            var pos = body.position;
            var target = _corners[_corner];
            var flat = new Vector3(target.x - pos.x, 0, target.z - pos.z);
            float dist = flat.magnitude;
            float step = speed * dt;
            while (dist <= Mathf.Max(step, ArriveDistance) && _corner < _corners.Count - 1)
            {
                _corner++;
                target = _corners[_corner];
                flat = new Vector3(target.x - pos.x, 0, target.z - pos.z);
                dist = flat.magnitude;
            }
            if (_corner == _corners.Count - 1 && dist <= Mathf.Max(step, ArriveDistance))
            {
                HasDestination = false;
                return Result.Arrived;
            }

            var dir = flat / Mathf.Max(dist, 1e-4f);
            // G3 NPC evasion (fix-93, NpcEvadeRules): a standing character in the way -> walk round it via the inter pose.
            bool evading = Evade(body, pos, step, dt, walksTo, ref dir, ref dist);
            var from = pos;
            pos += dir * Mathf.Min(step, dist);
            // Never snap onto a much lower surface: past a ledge (the ray finds nothing within reach, or ground far
            // below) the body stays where it is; the stuck rule then lets the caller decide (editor-runner-18: Xardas
            // walked a straight line off his plateau, y 245 -> 84, and waited below forever).
            // The floor ray starts no higher than a body can step up (G3 StepHeight 65 cm, more on a long frame up a slope):
            // from 1.5 m it found the top of a low cave ceiling or overhang above the body and lifted it into the rock
            // (fix-99: a Reddock varan in the cave roof).
            bool ground = TryGroundHeight(pos, out float groundY, out float groundSlope, StepProbe(Mathf.Min(step, dist)));
            float maxDown = Mathf.Max(MaxStepDown, Mathf.Min(step, dist) * MaxDescentPerMetre);
            // No floor under the next step (off a tower top, a wall walk or a cliff, or collision not loaded there): G3 bodies
            // keep their feet on collision (gCCharacterMovement_PS: gravity, step height 65 cm), so the step is refused
            // instead of carrying the path corner's or G3 route point's height on into the air (fix-60: Faring guards
            // floated above the street and hung beside the round tower). Rules: G3.Player.NpcFootRules.
            float carried = _direct ? from.y + (target.y - from.y) * Mathf.Clamp01(step / Mathf.Max(dist, 1e-4f)) : target.y;
            bool moves = NpcFootRules.TryStep(from.y, ground, groundY, _direct, OverWater, maxDown, carried, out float y);
            if (_drops[_corner])
            {
                // a drop link: off the edge and down as a fall (G3: no floor within the step height -> fall), never
                // below the landing or the ground
                var segStart = _corner > 0 ? _corners[_corner - 1] : _planStart;
                float len = new Vector2(target.x - segStart.x, target.z - segStart.z).magnitude;
                float left = new Vector2(target.x - pos.x, target.z - pos.z).magnitude;
                float t = len > 1e-3f ? 1f - left / len : 1f;
                float fall = NavDropRules.FallY(segStart.y, target.y, t);
                pos.y = ground && groundY > fall ? groundY : Mathf.Max(fall, target.y);
            }
            else if (!moves) pos = from;
            else if (_direct && !OverWater && WallAhead(from, dir, Mathf.Min(step, dist))) pos = from;
            // fix-108: off the NavMesh (a straight line: a wander point, no NavMesh yet) a step never climbs a face steeper
            // than walkable (G3 ProcessControledTranslation stops a move into slide-mode floor): deer walked up 60-70°
            // mountain faces and stood there. NavMesh segments are walkable by construction (agentSlope 49°).
            else if (!OverWater && !_direct && !OnNavSegment &&
                     NpcGroundRules.ClimbsSteep(y - from.y, Mathf.Min(step, dist), groundSlope)) pos = from;
            // fix-160: never up into the rock / onto a roof top inside a cave (the wall's foot lifted a minecrawler 1.3 m
            // into Dungeon_SP11 step by step; the cave guard gave up after 5 rescues and it stayed there, unseen)
            else if (!OverWater && !CaveGround.StepAllowed(from, new Vector3(pos.x, y, pos.z))) pos = from;
            else pos.y = y;
            // NPCs do not swim (G3: an NPC that enters swimming mode dies): stop at the edge of deep water; wading is fine.
            // The NavMesh already avoids it (CellNavMeshes); this covers straight-line walks. Stuck → the caller decides.
            // A body that treats water as solid (bloodfly) goes on over the surface (its path's rest is a straight line).
            if (OverWater) pos.y = SurfaceOrGround(pos);
            else if (IsDeepWater(pos, SwimDepth) && !IsDeepWater(from, SwimDepth)) pos = from;
            // Characters collide (G3 character controllers): the walk never enters the hero; it stops at contact or slides
            // round him, nobody is shoved (BodyBlockRules, as the hero's own walk against NPCs, fix-86).
            bool held = ClipAtHero(from, ref pos, Mathf.Min(step, dist));
            body.position = pos;
            body.rotation = Quaternion.RotateTowards(body.rotation, Quaternion.LookRotation(dir), TurnSpeed * dt);

            // Waiting for a character to clear the way is not being stuck (the caller would put the NPC at its goal).
            if (held || evading)
            {
                _stuckFrom = pos;
                _stuckTimer = 0;
                return Result.Walking;
            }
            _stuckTimer += dt;
            if (_stuckTimer >= StuckSeconds)
            {
                bool progressed = (pos - _stuckFrom).sqrMagnitude >= StuckProgress * StuckProgress;
                _stuckTimer = 0;
                _stuckFrom = pos;
                if (!progressed) return Result.Stuck;
            }
            return Result.Walking;
        }

        // ------------------------------------------------------------------------------- evasion (fix-93, G3 NPCEvadeSwitch)

        EvadeState _evade;
        CombatBody _self;
        Transform _selfOf;
        float _ownRadius = 0.35f;
        readonly List<EvadeBody> _near = new List<EvadeBody>(8);
        readonly List<BodyCircle> _heroCircle = new List<BodyCircle>(1);

        /// <summary>Times this walker started a detour round a character / was held back at the hero (runner probes).</summary>
        public int Detours { get; private set; }
        public int Holds { get; private set; }
        /// <summary>The walk now goes round a character (G3 NPCEvadeSwitch set).</summary>
        public bool Evading => _evade.Active;
        /// <summary>+1 round the left of the evaded body, -1 round its right, 0 none.</summary>
        public int EvadeSide => _evade.Side;

        /// <summary>Total detours / holds of all walkers since start (runner probes <c>npcdetours</c>, <c>npcholds</c>).</summary>
        public static int AllDetours, AllHolds;

        /// <summary>
        /// Gathers the characters near the walk (the hero, standing and walking NPCs, creatures; not lying or dead ones,
        /// not the walk's <paramref name="target"/>) and lets <see cref="NpcEvadeRules"/> steer: on a detour the step
        /// heads for the inter pose. True while the walk goes round someone or waits for the way.
        /// </summary>
        static readonly global::Unity.Profiling.ProfilerMarker s_stepMarker = new global::Unity.Profiling.ProfilerMarker("Walk.Step"),
            s_evadeMarker = new global::Unity.Profiling.ProfilerMarker("Walk.Evade");

        bool Evade(Transform body, Vector3 pos, float step, float dt, Transform target, ref Vector3 dir, ref float dist)
        {
            using var marker = s_evadeMarker.Auto();
            _near.Clear();
            _heroCircle.Clear();
            if (_selfOf != body)
            {
                _selfOf = body;
                _self = body.GetComponentInChildren<CombatBody>();
            }
            if (_self != null) _ownRadius = Mathf.Max(0.2f, _self.Footprint);
            float reach = _ownRadius + step + 3f;
            var live = CombatBody.Live;
            // mt-1 C (-g3mt bit 1): bodies far beyond reach even from where the last LateUpdate snapshot saw them (plus the way
            // any body can have gone since) are out without a transform read; the rest take the old test on their live position
            bool lateCull = G3.Unity.G3Mt.On(G3.Unity.MtBits.BodySnapshot) && CombatBody.Snapshot.ReadFrame >= Time.frameCount - 1;
            bool verify = lateCull && G3.Unity.G3Mt.Verifying;
            float far = reach + CombatBody.MaxReachFromPosition;
            float lateFar = far + LateCullMargin(lateCull ? Time.timeAsDouble - CombatBody.Snapshot.ReadTime : 0);
            // mt-1 E.1 (-g3mt bit 4): only the bodies of the grid cells around the walker (built once per frame over the same
            // snapshot), in Live order; the others are beyond lateFar, which the late cull below drops as well
            bool grid = lateCull && G3.Unity.G3Mt.On(G3.Unity.MtBits.EvadeGrid);
            if (grid) EvadeGrid.Query(pos, lateFar, _gridHits);
            int count = grid ? _gridHits.Count : live.Count;
            for (int k = 0; k < count; k++)
            {
                int i = grid ? _gridHits[k] : k;
                var b = live[i];
                if (b == null || b == _self || b.Fighter == null) continue;
                if (lateCull && b.TryLatePosition(out var lp, out _))
                {
                    float gx = lp.x - pos.x, gz = lp.z - pos.z;
                    if (gx * gx + gz * gz > lateFar * lateFar)
                    {
                        if (verify)
                        {
                            var vp = b.Position;
                            float vx = vp.x - pos.x, vz = vp.z - pos.z;
                            G3.Unity.G3Mt.Check(vx * vx + vz * vz > far * far, "walker", $"walker far cull dropped {b.name} at {vp} (late {lp}, walker {pos})");
                        }
                        continue;
                    }
                }
                // perf-3: far bodies out first by their position (one read; WalkBlock + Center cost several engine calls and
                // a walker tested all ~350 live bodies of a camp every step: 0.45 ms for one walking NPC in the editor)
                var bp = b.Position;
                float fx = bp.x - pos.x, fz = bp.z - pos.z;
                if (fx * fx + fz * fz > far * far) continue;
                float r = b.WalkBlock;
                if (r <= 0f) continue;
                var c = b.Center;
                if (Mathf.Abs(c.y - (pos.y + 0.9f)) > 1.6f) continue; // another floor
                float dx = c.x - pos.x, dz = c.z - pos.z;
                if (dx * dx + dz * dz > (reach + r) * (reach + r)) continue;
                bool player = b.Fighter.Actor.IsPlayer;
                if (player) _heroCircle.Add(new BodyCircle(c.x, c.z, r)); // his body holds every walk, the target's too
                if (target != null && b.transform.IsChildOf(target)) continue; // G3: the entity walked to is not evaded
                _near.Add(new EvadeBody(b.Serial, c.x, c.z, r, b.PlanarSpeed > NpcEvadeRules.MovingSpeed, player));
            }
            if (_near.Count == 0)
            {
                _evade.Active = false;
                return false;
            }
            bool was = _evade.Active;
            var from = pos;
            var kind = NpcEvadeRules.Steer(pos.x, pos.z, _ownRadius, dir.x, dir.z, step, _destination.x, _destination.z, _near, ref _evade, dt,
                                           (px, pz) => Walkable(from, px, pz));
            if (kind == EvadeStep.Detour)
            {
                if (!was) { Detours++; AllDetours++; }
                var to = new Vector3(_evade.ToX - pos.x, 0, _evade.ToZ - pos.z);
                float d = to.magnitude;
                if (d > 1e-3f)
                {
                    dir = to / d;
                    dist = d;
                }
                return true;
            }
            return kind == EvadeStep.Wait;
        }

        readonly List<int> _gridHits = new List<int>(32);

        /// <summary>
        /// mt-1 E.1: <see cref="CombatBody.Live"/> indices by 20 m ground cell, from the last LateUpdate snapshot; bodies the
        /// snapshot does not hold are always returned. Rebuilt when the frame, the snapshot or the Live list changed.
        /// </summary>
        static class EvadeGrid
        {
            const float Cell = 20f;
            static readonly G3.Unity.IndexGrid s_grid = new G3.Unity.IndexGrid();
            static readonly List<int> s_extra = new List<int>();
            static int s_frame = -1, s_read = -1, s_version = -1;
            static readonly global::Unity.Profiling.ProfilerMarker s_marker = new global::Unity.Profiling.ProfilerMarker("Walk.EvadeGrid");

            public static void Query(Vector3 pos, float radius, List<int> into)
            {
                Ensure();
                s_grid.Query(pos.x, pos.z, radius, into);
                if (s_extra.Count == 0) return;
                into.AddRange(s_extra);
                into.Sort();
            }

            static void Ensure()
            {
                if (s_frame == Time.frameCount && s_read == CombatBody.Snapshot.ReadFrame && s_version == CombatBody.LiveVersion) return;
                using var marker = s_marker.Auto();
                s_frame = Time.frameCount;
                s_read = CombatBody.Snapshot.ReadFrame;
                s_version = CombatBody.LiveVersion;
                s_grid.Clear(Cell);
                s_extra.Clear();
                var live = CombatBody.Live;
                for (int i = 0; i < live.Count; i++)
                {
                    var b = live[i];
                    if (b != null && b.TryLatePosition(out var p, out _)) s_grid.Add(i, p.x, p.z);
                    else s_extra.Add(i);
                }
            }
        }

        /// <summary>
        /// mt-1: how far (m) a body may have moved since the last frame's LateUpdate snapshot, <paramref name="age"/> seconds old: 1 m plus
        /// 40 m/s (several times any run, charge or knock-back speed). A body put somewhere else in between (a teleport, a spawn
        /// at the walker) is the one case this misses for a frame; <see cref="G3.Unity.MtBits.Verify"/> counts it.
        /// </summary>
        internal static float LateCullMargin(double age) => 1f + 40f * (float)System.Math.Max(0.0, age);

        /// <summary>
        /// G3 TestNPCToCorDCCStretch: the walker can stand at the inter pose (on the NavMesh, or on ground when there is
        /// none) and get there straight (no wall, no ledge).
        /// </summary>
        static bool Walkable(Vector3 from, float px, float pz)
        {
            var p = new Vector3(px, from.y, pz);
            if (NavMesh.SamplePosition(p, out var hit, 0.5f, NavMesh.AllAreas))
            {
                if (Mathf.Abs(hit.position.y - from.y) > 1f) return false;
                if (NavMesh.SamplePosition(from, out var start, 0.5f, NavMesh.AllAreas) &&
                    NavMesh.Raycast(start.position, hit.position, out _, NavMesh.AllAreas)) return false;
            }
            else if (!TryGroundHeight(p, out float y) || Mathf.Abs(y - from.y) > 1f) return false;
            var flat = new Vector3(px - from.x, 0, pz - from.z);
            float l = flat.magnitude;
            return l < 1e-3f || !WallAhead(from, flat / l, l);
        }

        /// <summary>The step stops at the hero's body and slides round it (never into him). True when he held it back.</summary>
        bool ClipAtHero(Vector3 from, ref Vector3 pos, float planned)
        {
            if (_heroCircle.Count == 0) return false;
            float dx = pos.x - from.x, dz = pos.z - from.z;
            if (!BodyBlockRules.Clip(from.x, from.z, _ownRadius, ref dx, ref dz, _heroCircle)) return false;
            pos.x = from.x + dx;
            pos.z = from.z + dz;
            bool held = dx * dx + dz * dz < 0.25f * planned * planned;
            if (held) { Holds++; AllHolds++; }
            return held;
        }

        /// <summary>
        /// A wall in the way of a straight walk: static collision at <see cref="WallProbeHeight"/> above the feet within the
        /// step plus <see cref="WallProbeReach"/>, whose surface is steeper than walkable (49°). The body stops there (the
        /// stuck rule then skips the leg / gives the route up) instead of walking on through the rock: fix-35, Xardas went
        /// on the ribbon through the cliff below his tower into the mountain (no ground under him, so his height was only
        /// carried along) and the escort hero never reached him. Characters, triggers and loose bodies do not count.
        /// </summary>
        static bool WallAhead(Vector3 feet, Vector3 dir, float step)
        {
            NavStats.GroundRays++;
            int n = Physics.RaycastNonAlloc(feet + Vector3.up * WallProbeHeight, dir, _hits, step + WallProbeReach, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var h = _hits[i];
                if (h.collider is CharacterController || h.rigidbody != null) continue;
                if (Vector3.Angle(h.normal, Vector3.up) > G3.Player.SlopeRules.WalkableAngleMax) return true;
            }
            return false;
        }

        /// <summary>Water at <paramref name="feet"/> (on the ground) too deep for a walking human (<see cref="G3.Player.SwimRules.NpcCanWalk"/>).</summary>
        public static bool IsDeepWater(Vector3 feet, float swimDepth = G3.Player.SwimRules.HumanSwimDepth)
        {
            if (!G3.Unity.G3Water.TrySurface(feet, out float surface)) return false;
            return !G3.Player.SwimRules.NpcCanWalk(surface - feet.y, swimDepth);
        }

        /// <summary>
        /// Height a body that treats water as solid stands on at <paramref name="feet"/>: the water surface where water covers
        /// the ground, else <paramref name="feet"/>' height (the caller's ground). The feet may be on the ground under the water
        /// or above the surface (a bloodfly that came over the water from higher ground): both end on the surface, as G3's
        /// TreatWaterAsSolid body stands on the water plane (editor-runner-12: the fly hovered 5.4 m over a swimming hero
        /// because only a surface above the feet counted).
        /// </summary>
        public static float SurfaceOrGround(Vector3 feet)
        {
            if (!G3.Unity.G3Water.TrySurface(feet, out float surface)) return feet.y;
            // Land standing out of the water's footprint (a shore inside the zone's box): solid ground above the surface
            // wins. Characters (the swimming hero's CharacterController) and triggers are not ground.
            float top = Mathf.Max(feet.y, surface) + 1.5f;
            int n = Physics.RaycastNonAlloc(new Vector3(feet.x, top, feet.z), Vector3.down, _hits, top - surface, ~0,
                                            QueryTriggerInteraction.Ignore);
            float ground = float.NegativeInfinity;
            for (int i = 0; i < n; i++)
                if (!(_hits[i].collider is CharacterController) && _hits[i].point.y > ground) ground = _hits[i].point.y;
            return ground > surface ? ground : surface;
        }

        static readonly RaycastHit[] _hits = new RaycastHit[16];

        /// <summary>Height of the ground under <paramref name="p"/>; <paramref name="fallback"/> when nothing is hit.</summary>
        public static float GroundHeight(Vector3 p, float fallback) => TryGroundHeight(p, out float y) ? y : fallback;

        /// <summary>Height of the ground under <paramref name="p"/>; false when nothing is hit.</summary>
        public static bool TryGroundHeight(Vector3 p, out float y, float above = 1.5f) => TryGroundHeight(p, out y, out _, above);

        /// <summary>Height and face slope (deg) of the ground under <paramref name="p"/>; false when nothing is hit.</summary>
        public static bool TryGroundHeight(Vector3 p, out float y, out float slopeDeg, float above = 1.5f)
        {
            NavStats.GroundRays++;
            bool hit = Physics.Raycast(p + Vector3.up * above, Vector3.down, out var info, above + 3f, ~0, QueryTriggerInteraction.Ignore);
            y = hit ? info.point.y : 0f;
            slopeDeg = hit ? Vector3.Angle(info.normal, Vector3.up) : 0f;
            return hit;
        }

        /// <summary>
        /// The floor a body at <paramref name="p"/> stands on under gravity (fix-60): the first static surface below
        /// <paramref name="p"/> + <paramref name="above"/>, however far down (<see cref="FloorReach"/>). Character
        /// controllers (the hero) and loose bodies are not floors. False when nothing is below (collision not loaded).
        /// Used for bodies that are put somewhere rather than walked: an interpolated pose (Near level), a spawn on a
        /// travel line, a catch-up spot, a wander point. G3 bodies in the processing range always stand on collision.
        /// </summary>
        public static bool TryFloorHeight(Vector3 p, out float y, float above = 1.5f)
        {
            NavStats.GroundRays++;
            int n = Physics.RaycastNonAlloc(p + Vector3.up * above, Vector3.down, _hits, above + FloorReach, ~0, QueryTriggerInteraction.Ignore);
            float best = float.PositiveInfinity;
            y = p.y;
            for (int i = 0; i < n; i++)
            {
                var h = _hits[i];
                if (h.collider is CharacterController || h.rigidbody != null) continue;
                if (h.distance < best) { best = h.distance; y = h.point.y; }
            }
            return !float.IsPositiveInfinity(best);
        }

        /// <summary>Floor height under <paramref name="p"/> (<see cref="TryFloorHeight"/>); <paramref name="fallback"/> when there is none.</summary>
        public static float FloorHeight(Vector3 p, float fallback) => TryFloorHeight(p, out float y) ? y : fallback;

        /// <summary>How far below a placed body its floor is searched (m): towers, wall walks, cliffs.</summary>
        public const float FloorReach = 150f;
    }
}
