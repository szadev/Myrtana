using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Simulation
{
    /// <summary>
    /// Chooses the station an NPC goes to for a day phase (gameplay-logic.md §5.5, G3 "SelectRoutineDestination"):
    /// the routine point of the phase (a missing one falls back to the working point), widened by its anchor —
    /// any free station of a local anchor, a random other point of a roam anchor, the next point of a patrol —
    /// and the first free slot among those candidates. Returns <see cref="Station.None"/> when nothing is free.
    /// </summary>
    public sealed class DestinationPlanner
    {
        readonly RoutinePointCatalog _catalog;
        readonly StationClaims _claims;
        readonly IRandom _random;
        readonly List<RoutinePointDef> _candidates = new List<RoutinePointDef>();

        public DestinationPlanner(RoutinePointCatalog catalog, StationClaims claims, IRandom random)
        {
            _catalog = catalog;
            _claims = claims;
            _random = random;
        }

        /// <param name="moveOn">the NPC has stayed long enough at its station: roam, local and patrol anchors advance</param>
        public Station Select(ActorId actor, NpcRoutineDef routine, DayPhase phase, Station current, bool moveOn)
        {
            if (routine == null) return Station.None;
            if (!_catalog.TryGetPoint(routine.PointFor(phase), out var point)) return Station.None;

            _candidates.Clear();
            if (point.Anchor.IsEmpty || !_catalog.TryGetAnchor(point.Anchor, out var anchor))
            {
                if (current.Point == point.Id) return current;
                _candidates.Add(point);
            }
            else
            {
                bool inAnchor = _catalog.TryGetPoint(current.Point, out var cur) && cur.Anchor == point.Anchor;
                if (inAnchor && !moveOn) return current;
                AddAnchorCandidates(anchor, point, inAnchor ? cur : null);
            }

            foreach (var candidate in _candidates)
            {
                for (int slot = 0; slot < candidate.Slots.Count; slot++)
                {
                    var station = new Station(candidate.Id, slot);
                    if (station.Equals(current) || _claims.IsFreeFor(station, actor, current)) return station;
                }
            }
            return Station.None;
        }

        void AddAnchorCandidates(AnchorDef anchor, RoutinePointDef own, RoutinePointDef current)
        {
            var members = _catalog.Members(anchor.Id);
            switch (anchor.Kind)
            {
                case AnchorKind.Roam:
                    foreach (var m in members) if (m != current) _candidates.Add(m);
                    Shuffle(_candidates);
                    break;
                case AnchorKind.Patrol:
                    int at = current != null ? IndexOf(members, current) : -1;
                    for (int i = 1; i <= members.Count; i++) _candidates.Add(members[(at + i + members.Count) % members.Count]);
                    if (at < 0) { _candidates.Remove(own); _candidates.Insert(0, own); }
                    break;
                default: // Local (and Event): the NPC's own point first, then the anchor's other stations
                    if (current == null) _candidates.Add(own);
                    var others = new List<RoutinePointDef>();
                    foreach (var m in members) if (m != own && m != current) others.Add(m);
                    Shuffle(others);
                    _candidates.AddRange(others);
                    if (current != null && current != own) _candidates.Insert(0, own);
                    break;
            }
            if (_candidates.Count == 0) _candidates.Add(own);
        }

        static int IndexOf(IReadOnlyList<RoutinePointDef> list, RoutinePointDef item)
        {
            for (int i = 0; i < list.Count; i++) if (list[i] == item) return i;
            return -1;
        }

        void Shuffle(List<RoutinePointDef> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }
}
