using System.Collections.Generic;
using G3.Player;
using UnityEngine;
using UnityEngine.AI;

namespace Myrtana.Unity
{
    /// <summary>
    /// One-way NavMesh links down terrace steps (<see cref="NavDropRules"/>: G3 bodies fall off any edge with no floor
    /// within the step height and land; they never climb it). Unity's runtime NavMesh builder makes no drop links, so a
    /// terrace higher than the agent climb is a NavMesh island; the links are made on demand where a way ends at such
    /// an edge: when a walker's (or the escort hero's) path is partial, <see cref="TryLinkTowards"/> probes the NavMesh
    /// edges between the partial path's end and the target (a fan of directions), finds lower NavMesh 0.8–4 m below just past
    /// the edge with nothing in the way, and adds the link whose landing gets closest to the target (or reaches it). Path
    /// queries then route over it (<c>NavMesh.CalculatePath</c> uses links); <see cref="NpcWalker"/> walks a link
    /// segment as a fall (<see cref="NavDropRules.FallY"/>). Links over cells that left the NavMesh are dropped by
    /// <see cref="CellNavMeshes"/> (<see cref="Prune"/>).
    /// </summary>
    public static class NavDropLinks
    {
        sealed class Link
        {
            public Vector3 start, end;
            public NavMeshLinkInstance instance;
        }

        const int MaxLinks = 400;
        const float SameLink = 1.5f, LinkMatch = 0.35f, SearchReach = 30f, RetrySeconds = 8f;

        static readonly List<Link> _links = new List<Link>();
        static readonly Dictionary<long, float> _failed = new Dictionary<long, float>();
        static readonly RaycastHit[] _hits = new RaycastHit[8];
        static NavMeshPath _path;

        /// <summary>Links made so far (alive).</summary>
        public static int Count => _links.Count;

        /// <summary>Whether a path segment from <paramref name="a"/> to <paramref name="b"/> crosses one of the drop links.</summary>
        public static bool IsLink(Vector3 a, Vector3 b)
        {
            foreach (var l in _links)
                if ((l.start - a).sqrMagnitude < LinkMatch * LinkMatch && (l.end - b).sqrMagnitude < LinkMatch * LinkMatch) return true;
            return false;
        }

        /// <summary>
        /// Looks for a drop link that continues the way from <paramref name="from"/> (on the NavMesh) towards
        /// <paramref name="destination"/> where the NavMesh path ends partial; adds it and returns true (the caller plans
        /// again). A place that found nothing is not searched again for <see cref="RetrySeconds"/>.
        /// </summary>
        public static bool TryLinkTowards(Vector3 from, Vector3 destination, int agentTypeId = 0)
        {
            using var marker = NavStats.LinkMarker.Auto();
            long t0 = NavStats.Now;
            NavStats.LinkSearches++;
            try { return Search(from, destination, agentTypeId); }
            finally { NavStats.LinkTicks += NavStats.Now - t0; }
        }

        static bool Search(Vector3 from, Vector3 destination, int agentTypeId)
        {
            _path ??= new NavMeshPath();
            var filter = new NavMeshQueryFilter { agentTypeID = agentTypeId, areaMask = NavMesh.AllAreas };
            if (!NavMesh.SamplePosition(from, out var fromHit, 2f, filter)) return false;
            var start = fromHit.position;
            bool destOnMesh = NavMesh.SamplePosition(destination, out var destHit, 4f, filter);
            var dest = destOnMesh ? destHit.position : destination;

            // where the connected NavMesh ends: the partial path's last corner (closest reachable point to the target)
            var end = start;
            if (destOnMesh && NavMesh.CalculatePath(start, dest, filter, _path))
            {
                if (_path.status == NavMeshPathStatus.PathComplete) return false;
                var c = _path.corners;
                if (c.Length > 0) end = c[c.Length - 1];
            }
            float currentLeft = Flat(dest - end);
            if (currentLeft < 1.5f) return false;

            long key = Key(end, dest);
            if (_failed.TryGetValue(key, out float until) && Time.time < until) return false;

            float bestLeft = float.MaxValue;
            bool bestComplete = false;
            Vector3 bestEdge = default, bestLanding = default;
            foreach (var origin in new[] { end, start })
            {
                var toward = dest - origin;
                toward.y = 0;
                float dist = toward.magnitude;
                if (dist < 0.5f) continue;
                toward /= dist;
                float reach = Mathf.Min(SearchReach, dist);
                foreach (float deg in NavDropRules.FanDegrees)
                {
                    var dir = Quaternion.Euler(0, deg, 0) * toward;
                    if (!NavMesh.Raycast(origin, origin + dir * reach, out var edgeHit, filter)) continue; // no edge that way
                    var edge = edgeHit.position;
                    if (Mathf.Abs(edge.y - origin.y) > 6f) continue;
                    if (!FindLanding(edge, dir, filter, out var landing)) continue;
                    bool complete;
                    float left;
                    if (destOnMesh && NavMesh.CalculatePath(landing, dest, filter, _path) && _path.status != NavMeshPathStatus.PathInvalid)
                    {
                        complete = _path.status == NavMeshPathStatus.PathComplete;
                        var c = _path.corners;
                        left = complete ? 0f : Flat(dest - c[c.Length - 1]);
                    }
                    else
                    {
                        complete = false;
                        left = Flat(dest - landing);
                    }
                    if (!NavDropRules.Improves(complete, left, currentLeft)) continue;
                    if (complete && !bestComplete || (complete == bestComplete && left < bestLeft))
                    {
                        bestComplete = complete;
                        bestLeft = left;
                        bestEdge = edge;
                        bestLanding = landing;
                    }
                }
                if (bestComplete) break;
            }
            if (bestLeft != float.MaxValue && Add(bestEdge, bestLanding, agentTypeId)) return true;
            // nothing, or the best link is there already (the way stays partial beyond it): do not search here again soon
            if (_failed.Count > 2000) _failed.Clear();
            _failed[key] = Time.time + RetrySeconds;
            return false;
        }

        /// <summary>
        /// The way off a NavMesh island (editor-runner-24: Xardas on his tower plateau, whose neighbour waypoints were all at
        /// its level or higher, so no waypoint edge asked for a drop link): from <paramref name="from"/> the island's edge is
        /// looked for in every direction (<see cref="NavRoutePlanner.IslandExitDegrees"/>, up to <see cref="IslandReach"/> m),
        /// with lower NavMesh within the drop limit past it (<see cref="FindLanding"/>) that the island's own NavMesh does not
        /// reach. The exit closest to <paramref name="goal"/> by its NavMesh way (<see cref="NavRoutePlanner.BetterExit"/>) gets
        /// a drop link (or has one already); <paramref name="landing"/> is where it lands.
        /// </summary>
        public static bool TryLinkOffIsland(Vector3 from, Vector3 goal, out Vector3 landing, int agentTypeId = 0)
        {
            using var marker = NavStats.LinkMarker.Auto();
            long t0 = NavStats.Now;
            NavStats.LinkSearches++;
            try { return SearchIslandExit(from, goal, agentTypeId, out landing); }
            finally { NavStats.LinkTicks += NavStats.Now - t0; }
        }

        const float IslandReach = 60f;

        static bool SearchIslandExit(Vector3 from, Vector3 goal, int agentTypeId, out Vector3 landing)
        {
            landing = default;
            _path ??= new NavMeshPath();
            var filter = new NavMeshQueryFilter { agentTypeID = agentTypeId, areaMask = NavMesh.AllAreas };
            if (!NavMesh.SamplePosition(from, out var fromHit, 2f, filter)) return false;
            var start = fromHit.position;
            long key = Key(start, goal) ^ 0x5A5A5A5AL;
            if (_failed.TryGetValue(key, out float until) && Time.time < until) return false;
            bool destOnMesh = NavMesh.SamplePosition(goal, out var destHit, 4f, filter);
            var dest = destOnMesh ? destHit.position : goal;
            var toward = dest - start;
            toward.y = 0;
            toward = toward.sqrMagnitude > 0.01f ? toward.normalized : Vector3.forward;
            bool found = false, bestComplete = false;
            float bestLeft = float.MaxValue, bestEdgeDistance = float.MaxValue;
            Vector3 bestEdge = default, bestLanding = default;
            foreach (float deg in NavRoutePlanner.IslandExitDegrees())
            {
                var dir = Quaternion.Euler(0, deg, 0) * toward;
                if (!NavMesh.Raycast(start, start + dir * IslandReach, out var edgeHit, filter)) continue; // no edge that way
                var edge = edgeHit.position;
                if (Mathf.Abs(edge.y - start.y) > 12f) continue;
                if (!FindLanding(edge, dir, filter, out var land)) continue;
                // lower ground the island's own NavMesh already reaches is no exit (the waypoint search covers it)
                if (NavMesh.CalculatePath(start, land, filter, _path) && _path.status == NavMeshPathStatus.PathComplete && !IsLinkedPath()) continue;
                bool complete = false;
                float left = Flat(dest - land);
                if (destOnMesh && NavMesh.CalculatePath(land, dest, filter, _path) && _path.status != NavMeshPathStatus.PathInvalid)
                {
                    complete = _path.status == NavMeshPathStatus.PathComplete;
                    var c = _path.corners;
                    if (c.Length > 0) left = complete ? 0f : Flat(dest - c[c.Length - 1]);
                }
                float edgeDistance = Flat(edge - start);
                if (found && !NavRoutePlanner.BetterExit(complete, left, edgeDistance, bestComplete, bestLeft, bestEdgeDistance)) continue;
                found = true;
                bestComplete = complete;
                bestLeft = left;
                bestEdgeDistance = edgeDistance;
                bestEdge = edge;
                bestLanding = land;
            }
            if (found)
            {
                Add(bestEdge, bestLanding, agentTypeId); // false: that link is there already, still the way off
                landing = bestLanding;
                Debug.Log(FormattableStringInv($"[nav] island exit from {start}: edge {bestEdge} -> {bestLanding}, {(bestComplete ? "complete way on" : $"{bestLeft:0} m short of the goal")}"));
                return true;
            }
            if (_failed.Count > 2000) _failed.Clear();
            _failed[key] = Time.time + RetrySeconds;
            return false;
        }

        /// <summary>The last computed path goes over one of the drop links (a way off the island made before).</summary>
        static bool IsLinkedPath()
        {
            var c = _path.corners;
            for (int i = 1; i < c.Length; i++)
                if (IsLink(c[i - 1], c[i])) return true;
            return false;
        }

        /// <summary>
        /// The NavMesh floor below the edge, just past it in <paramref name="dir"/>: a drop (<see cref="NavDropRules.IsDrop"/>),
        /// not on the edge's own NavMesh region nearby, with free air from the edge out over it.
        /// </summary>
        static bool FindLanding(Vector3 edge, Vector3 dir, NavMeshQueryFilter filter, out Vector3 landing)
        {
            landing = default;
            var chest = edge + Vector3.up * 1.0f;
            foreach (float d in NavDropRules.LandingProbes)
            {
                var over = edge + dir * d;
                var overChest = new Vector3(over.x, chest.y, over.z);
                if (Physics.Linecast(chest, overChest, out _, ~0, QueryTriggerInteraction.Ignore)) return false; // a wall or fence
                float rayLen = 1.0f + NavDropRules.MaxDrop + 0.5f;
                int n = Physics.RaycastNonAlloc(overChest, Vector3.down, _hits, rayLen, ~0, QueryTriggerInteraction.Ignore);
                float ground = float.NegativeInfinity;
                for (int i = 0; i < n; i++)
                    if (!(_hits[i].collider is CharacterController) && _hits[i].point.y > ground) ground = _hits[i].point.y;
                if (float.IsNegativeInfinity(ground)) continue;              // nothing within reach: too deep here
                if (ground > edge.y - SlopeRules.StepHeight) continue;       // still the upper floor (or a step): further out
                if (!NavDropRules.IsDrop(edge.y, ground)) continue;
                if (!NavMesh.SamplePosition(new Vector3(over.x, ground, over.z), out var hit, 0.8f, filter)) continue;
                if (Mathf.Abs(hit.position.y - ground) > 0.5f || !NavDropRules.IsDrop(edge.y, hit.position.y)) continue;
                if (G3.Unity.G3Water.TrySurface(hit.position, out float surface) &&
                    !SwimRules.NpcCanWalk(surface - hit.position.y)) continue; // into deep water
                landing = hit.position;
                return true;
            }
            return false;
        }

        static bool Add(Vector3 edge, Vector3 landing, int agentTypeId)
        {
            foreach (var l in _links)
                if ((l.start - edge).sqrMagnitude < SameLink * SameLink && (l.end - landing).sqrMagnitude < SameLink * SameLink) return false;
            var data = new NavMeshLinkData
            {
                startPosition = edge,
                endPosition = landing,
                width = 0f,
                costModifier = -1f,
                bidirectional = false, // G3 bodies fall down, they never climb up
                area = 0,
                agentTypeID = agentTypeId,
            };
            var instance = NavMesh.AddLink(data);
            if (!instance.valid) return false;
            if (_links.Count >= MaxLinks) { _links[0].instance.Remove(); _links.RemoveAt(0); }
            _links.Add(new Link { start = edge, end = landing, instance = instance });
            Debug.Log(FormattableStringInv($"[nav] drop link {edge} -> {landing} ({edge.y - landing.y:0.0} m down), {_links.Count} links"));
            return true;
        }

        /// <summary>Removes the links whose edge lies outside <paramref name="bounds"/> (their cells left the NavMesh).</summary>
        public static void Prune(Bounds bounds)
        {
            for (int i = _links.Count - 1; i >= 0; i--)
            {
                var p = _links[i].start;
                if (p.x >= bounds.min.x && p.x <= bounds.max.x && p.z >= bounds.min.z && p.z <= bounds.max.z) continue;
                _links[i].instance.Remove();
                _links.RemoveAt(i);
            }
            _failed.Clear(); // the NavMesh changed: places may link now
        }

        public static void Clear()
        {
            foreach (var l in _links) l.instance.Remove();
            _links.Clear();
            _failed.Clear();
        }

        static long Key(Vector3 end, Vector3 dest)
        {
            long a = Mathf.RoundToInt(end.x / 2f) & 0xFFFF, b = Mathf.RoundToInt(end.z / 2f) & 0xFFFF;
            long c = Mathf.RoundToInt(dest.x / 10f) & 0xFFFF, d = Mathf.RoundToInt(dest.z / 10f) & 0xFFFF;
            return (a << 48) | (b << 32) | (c << 16) | d;
        }

        static float Flat(Vector3 v) => new Vector2(v.x, v.z).magnitude;

        static string FormattableStringInv(System.FormattableString s) => System.FormattableString.Invariant(s);
    }
}
