using System;
using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Simulation
{
    /// <summary>A claimable place: one slot of a routine point.</summary>
    public readonly struct Station : IEquatable<Station>
    {
        public static readonly Station None = default;

        public Station(Id<RoutinePointDef> point, int slot)
        {
            Point = point;
            Slot = slot;
        }

        public Id<RoutinePointDef> Point { get; }
        public int Slot { get; }
        public bool IsNone => Point.IsEmpty;

        public bool Equals(Station other) => Point == other.Point && Slot == other.Slot;
        public override bool Equals(object obj) => obj is Station other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Point, Slot);
        public override string ToString() => IsNone ? "none" : $"{Point}#{Slot}";
    }

    /// <summary>The routine points and anchors of the content, with each anchor's member points.</summary>
    public sealed class RoutinePointCatalog
    {
        readonly Dictionary<Id<RoutinePointDef>, RoutinePointDef> _points = new Dictionary<Id<RoutinePointDef>, RoutinePointDef>();
        readonly Dictionary<Id<AnchorDef>, AnchorDef> _anchors = new Dictionary<Id<AnchorDef>, AnchorDef>();
        readonly Dictionary<Id<AnchorDef>, List<RoutinePointDef>> _members = new Dictionary<Id<AnchorDef>, List<RoutinePointDef>>();

        public RoutinePointCatalog(IEnumerable<RoutinePointDef> points, IEnumerable<AnchorDef> anchors)
        {
            foreach (var a in anchors) _anchors[a.Id] = a;
            foreach (var p in points)
            {
                _points[p.Id] = p;
                if (p.Anchor.IsEmpty) continue;
                if (!_members.TryGetValue(p.Anchor, out var list)) _members[p.Anchor] = list = new List<RoutinePointDef>();
                list.Add(p);
            }
            foreach (var list in _members.Values) list.Sort((a, b) => string.CompareOrdinal(a.Id.Name, b.Id.Name));
        }

        public static RoutinePointCatalog From(IContentDatabase content) =>
            new RoutinePointCatalog(content.All<RoutinePointDef>(), content.All<AnchorDef>());

        public int PointCount => _points.Count;

        readonly Dictionary<string, List<RoutinePointDef>> _byInteraction = new Dictionary<string, List<RoutinePointDef>>(StringComparer.Ordinal);

        /// <summary>Every point whose interaction is <paramref name="interaction"/> ("Listener", "CleanFloor" …), in a stable order.</summary>
        public IReadOnlyList<RoutinePointDef> PointsWith(string interaction)
        {
            if (interaction == null) return Array.Empty<RoutinePointDef>();
            if (!_byInteraction.TryGetValue(interaction, out var list))
            {
                list = new List<RoutinePointDef>();
                foreach (var p in _points.Values) if (p.Interaction == interaction) list.Add(p);
                list.Sort((a, b) => string.CompareOrdinal(a.Id.Name, b.Id.Name));
                _byInteraction[interaction] = list;
            }
            return list;
        }

        public bool TryGetPoint(Id<RoutinePointDef> id, out RoutinePointDef point) => _points.TryGetValue(id, out point);

        public bool TryGetAnchor(Id<AnchorDef> id, out AnchorDef anchor) => _anchors.TryGetValue(id, out anchor);

        /// <summary>Points of an anchor in a stable order (empty for unknown anchors).</summary>
        public IReadOnlyList<RoutinePointDef> Members(Id<AnchorDef> anchor) =>
            _members.TryGetValue(anchor, out var list) ? list : (IReadOnlyList<RoutinePointDef>)Array.Empty<RoutinePointDef>();

        /// <summary>Total slots of an anchor's points, capped by its MaxUsers (0 = no cap).</summary>
        public int Capacity(Id<AnchorDef> anchor)
        {
            // Cached: the catalog does not change, and the planner asks once per candidate slot (O(members²) per Select on a
            // large anchor, ~27 us for an NPC whose anchor is full, perf-9).
            if (_capacity.TryGetValue(anchor, out int cached)) return cached;
            int slots = 0;
            var members = Members(anchor);
            for (int i = 0; i < members.Count; i++) slots += members[i].Slots.Count;
            int capacity = TryGetAnchor(anchor, out var a) && a.MaxUsers > 0 ? Math.Min(a.MaxUsers, slots) : slots;
            _capacity[anchor] = capacity;
            return capacity;
        }

        readonly Dictionary<Id<AnchorDef>, int> _capacity = new Dictionary<Id<AnchorDef>, int>();

        /// <summary>Where the user of <paramref name="station"/> stands; false for unknown points or slots.</summary>
        public bool TryGetPose(Station station, out WorldPose pose)
        {
            pose = default;
            if (!_points.TryGetValue(station.Point, out var p) || station.Slot < 0 || station.Slot >= p.Slots.Count) return false;
            pose = p.Slots[station.Slot];
            return true;
        }
    }
}
