using System;
using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Simulation
{
    /// <summary>
    /// Who uses which station, and how many users each anchor has. Derived state: it is rebuilt from the agents after a
    /// load, so it is not saved itself. The hero's use of an object is a claim like an NPC's (<see cref="TryOccupy"/>), so
    /// NPCs and the hero keep each other off the same object (G3 <c>gCInteraction_PS.User</c>, Docs/interactive-objects.md §9).
    /// </summary>
    public sealed class StationClaims
    {
        readonly RoutinePointCatalog _catalog;
        readonly Dictionary<Station, ActorId> _users = new Dictionary<Station, ActorId>();
        readonly Dictionary<Id<AnchorDef>, int> _anchorUsers = new Dictionary<Id<AnchorDef>, int>();

        public StationClaims(RoutinePointCatalog catalog) => _catalog = catalog;

        /// <summary>
        /// The actor is down or dead (G3 IsFreeDestination: a user in AIMode Down or Dead does not hold its object). Set by
        /// the session; null = every user holds.
        /// </summary>
        public Func<ActorId, bool> UserGone { get; set; }

        /// <summary>
        /// The actor is not in the world (an NPC of a switched-off sector, fix-67 R33-3: the Faring rebel smith that shares
        /// Zakosh's anvil before the liberation): it has no body, so it cannot be a user. Set by the NPC simulation; null =
        /// every user is present.
        /// </summary>
        public Func<ActorId, bool> UserAbsent { get; set; }

        /// <summary>The user does not hold its station now (down, dead or not in the world).</summary>
        public bool IsGone(ActorId user) => (UserGone != null && UserGone(user)) || (UserAbsent != null && UserAbsent(user));

        public bool TryGetUser(Station station, out ActorId user) => _users.TryGetValue(station, out user);

        public int AnchorUsers(Id<AnchorDef> anchor) => _anchorUsers.TryGetValue(anchor, out int n) ? n : 0;

        /// <summary>
        /// The station can be taken by <paramref name="actor"/>: not used by someone else (at a single-user object: no
        /// other slot of it either), not owned by someone else, and its anchor still has room (a user already counted for
        /// the anchor keeps its place).
        /// </summary>
        public bool IsFreeFor(Station station, ActorId actor, Station current)
        {
            if (!_catalog.TryGetPoint(station.Point, out var point)) return false;
            if (!point.Owner.IsEmpty && point.Owner != actor) return false;
            if (HeldByOther(station, actor)) return false;
            if (!ObjectOccupancy.IsShared(point.Interaction) && HeldByOther(point, actor, except: station.Slot)) return false;
            if (point.Anchor.IsEmpty) return true;
            bool alreadyCounted = _catalog.TryGetPoint(current.Point, out var cur) && cur.Anchor == point.Anchor;
            if (alreadyCounted) return true;
            int capacity = _catalog.Capacity(point.Anchor);
            // the counter includes gone users (down, dead, absent): only a full anchor is recounted without them
            return AnchorUsers(point.Anchor) < capacity || HoldingUsers(point.Anchor) < capacity;
        }

        /// <summary>
        /// Someone else uses the object, so <paramref name="actor"/> cannot (G3: the hero cannot even focus it). A shared
        /// object (the campfire) is never occupied; an object without routine data has no other users.
        /// </summary>
        public bool IsOccupiedFor(Id<RoutinePointDef> point, ActorId actor) =>
            _catalog.TryGetPoint(point, out var p) && !ObjectOccupancy.IsShared(p.Interaction) && HeldByOther(p, actor, except: -1);

        /// <summary>The actors holding a slot of the object now (gone users left out; dev runner, fix-188).</summary>
        public List<ActorId> UsersOf(Id<RoutinePointDef> point)
        {
            var users = new List<ActorId>();
            if (!_catalog.TryGetPoint(point, out var p)) return users;
            for (int i = 0; i < p.Slots.Count; i++)
                if (_users.TryGetValue(new Station(p.Id, i), out var user) && !IsGone(user) && !users.Contains(user)) users.Add(user);
            return users;
        }

        /// <summary>
        /// <paramref name="actor"/> (the hero) starts using the object from (<paramref name="x"/>, <paramref name="z"/>):
        /// he takes its free slot nearest to him (G3 PS_Interact → _AI_Goto_Ext → SetCurrentDestinationPoint: he becomes its
        /// user, the anchor counts him). False when someone else uses it. True without a slot (<see cref="Station.None"/>)
        /// for an object without routine data and at a campfire whose every slot is taken. Owners and anchor limits are NPC
        /// rules: the hero may use an owned bed (that is the crime module's business).
        /// </summary>
        public bool TryOccupy(Id<RoutinePointDef> point, ActorId actor, float x, float z, out Station taken)
        {
            taken = Station.None;
            if (!_catalog.TryGetPoint(point, out var p)) return true;
            if (IsOccupiedFor(point, actor)) return false;
            int best = -1;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < p.Slots.Count; i++)
            {
                if (HeldByOther(new Station(p.Id, i), actor)) continue;
                float dx = p.Slots[i].X - x, dz = p.Slots[i].Z - z, d = dx * dx + dz * dz;
                if (d < bestDistance) { bestDistance = d; best = i; }
            }
            if (best < 0) return true;
            taken = new Station(p.Id, best);
            Claim(taken, actor);
            return true;
        }

        public void Claim(Station station, ActorId actor)
        {
            if (station.IsNone) return;
            if (_users.TryGetValue(station, out var previous))
            {
                if (previous == actor) return;
                Release(station, previous); // a down or dead user gives the slot up
            }
            _users[station] = actor;
            var anchor = AnchorOf(station);
            if (!anchor.IsEmpty) _anchorUsers[anchor] = AnchorUsers(anchor) + 1;
        }

        public void Release(Station station, ActorId actor)
        {
            if (station.IsNone || !_users.TryGetValue(station, out var user) || user != actor) return;
            _users.Remove(station);
            var anchor = AnchorOf(station);
            if (!anchor.IsEmpty) _anchorUsers[anchor] = Math.Max(0, AnchorUsers(anchor) - 1);
        }

        public void Clear()
        {
            _users.Clear();
            _anchorUsers.Clear();
        }

        bool HeldByOther(Station station, ActorId actor) =>
            _users.TryGetValue(station, out var user) && user != actor && !IsGone(user);

        /// <summary>Users of the anchor that hold their place (<see cref="AnchorUsers"/> without the gone ones).</summary>
        int HoldingUsers(Id<AnchorDef> anchor)
        {
            if (UserGone == null && UserAbsent == null) return AnchorUsers(anchor);
            int n = 0;
            foreach (var p in _catalog.Members(anchor))
                for (int i = 0; i < p.Slots.Count; i++)
                    if (_users.TryGetValue(new Station(p.Id, i), out var user) && !IsGone(user)) n++;
            return n;
        }

        bool HeldByOther(RoutinePointDef point, ActorId actor, int except)
        {
            for (int i = 0; i < point.Slots.Count; i++)
                if (i != except && HeldByOther(new Station(point.Id, i), actor)) return true;
            return false;
        }

        Id<AnchorDef> AnchorOf(Station station) =>
            _catalog.TryGetPoint(station.Point, out var p) ? p.Anchor : default;
    }
}
