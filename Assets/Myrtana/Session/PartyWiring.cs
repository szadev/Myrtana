using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;
using Myrtana.Story;

namespace Myrtana.Session
{
    /// <summary>
    /// The hero's party in a session (Docs/party.md): the <see cref="PlayerParty"/>, the FollowNpc guides, the named places
    /// and the ports the story and the simulation see it through. Reacts to deaths (the dead leave the party), teleports
    /// (members wait behind) and enclave alarms (that enclave's companions leave, CP).
    /// </summary>
    public sealed class PartyServices : IDisposable
    {
        readonly List<IDisposable> _subscriptions = new List<IDisposable>();

        internal PartyServices(PlayerParty party, FollowQuestGuides guides, SessionPositions positions, SessionEscorts escorts)
        {
            Members = party;
            Guides = guides;
            Positions = positions;
            Escorts = escorts;
        }

        /// <summary>Who is in the hero's party and who guides him.</summary>
        public PlayerParty Members { get; }
        public FollowQuestGuides Guides { get; }
        /// <summary>Positions of actors and named places (story conditions).</summary>
        public SessionPositions Positions { get; }
        /// <summary>The party as the NPC simulation and the presentation see it.</summary>
        public SessionEscorts Escorts { get; }

        internal void Add(IDisposable subscription) => _subscriptions.Add(subscription);

        // ------------------------------------------------------------------------------------------- simple API

        /// <summary>
        /// The NPC joins the hero as a companion (G3 JoinPlayerParty) without a dialog, e.g. at a game start; the NPC's
        /// PartyEnabled flag is set so the hero can dismiss it and ask it again.
        /// </summary>
        public void JoinCompanion(ActorId npc, PartyMemberType type = PartyMemberType.Party)
        {
            if (type == PartyMemberType.Party) Roles.Set(npc, ActorRole.Party, true);
            Members.Join(npc, type);
        }

        /// <summary>The member leaves the hero (G3 DismissPlayerParty).</summary>
        public void Dismiss(ActorId npc) => Members.Dismiss(npc);

        internal ActorRoles Roles { get; set; }

        public void Dispose()
        {
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
            Guides.Dispose();
        }
    }

    public static class PartyWiring
    {
        public static PartyServices Create(IContentDatabase content, PlayerParty party, ItemServices items, QuestService quests,
                                           NpcSimulation npcs, Func<IPerceptionWorld> world, IEventBus events)
        {
            var places = content.All<WorldPlaceDef>().ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
            var positions = new SessionPositions(npcs, world, places);
            var escorts = new SessionEscorts(party, positions);
            var guides = new FollowQuestGuides(quests, party, content.All<QuestDef>(), events);
            var services = new PartyServices(party, guides, positions, escorts) { Roles = items.Roles };
            npcs.Escorts = escorts;
            party.InProcessingRange = id => !npcs.TryGet(id, out var agent) || agent.Detail != DetailLevel.Abstract;
            services.Add(events.Subscribe<ActorDefeated>(e =>
            {
                if (e.Killed) party.MemberDied(e.Victim);
            }));
            services.Add(events.Subscribe<TeleportRequested>(_ => party.PlayerTeleported()));
            return services;
        }
    }

    /// <summary>
    /// Positions for story conditions: the hero as the presentation reported him, NPCs from the simulation (interpolated
    /// travel, the body's pose when on screen), named places from content.
    /// </summary>
    public sealed class SessionPositions : IWorldPositions
    {
        readonly NpcSimulation _npcs;
        readonly Func<IPerceptionWorld> _world;
        readonly Dictionary<string, WorldPlaceDef> _places;

        public SessionPositions(NpcSimulation npcs, Func<IPerceptionWorld> world, Dictionary<string, WorldPlaceDef> places)
        {
            _npcs = npcs ?? throw new ArgumentNullException(nameof(npcs));
            _world = world ?? (() => null);
            _places = places ?? new Dictionary<string, WorldPlaceDef>(StringComparer.OrdinalIgnoreCase);
        }

        public int PlaceCount => _places.Count;

        public bool TryGetPlace(string name, out WorldPose pose)
        {
            if (!string.IsNullOrEmpty(name) && _places.TryGetValue(name, out var p) && p.Position.Length >= 3)
            {
                pose = new WorldPose(p.Position[0], p.Position[1], p.Position[2], p.Yaw);
                return true;
            }
            pose = default;
            return false;
        }

        /// <summary>Where an actor or a named place is.</summary>
        public bool TryGetPose(string entity, out WorldPose pose)
        {
            pose = default;
            if (string.IsNullOrEmpty(entity)) return false;
            var actor = new ActorId(entity);
            if (actor.IsPlayer)
            {
                var world = _world();
                return world != null && world.TryGetPose(actor, out pose);
            }
            if (_npcs.TryGet(actor, out var agent))
            {
                pose = _npcs.PoseOf(agent);
                return true;
            }
            return TryGetPlace(entity, out pose);
        }

        public bool TryGetDistance(ActorId actor, string entity, out float metres)
        {
            metres = float.MaxValue;
            if (actor.IsEmpty || !TryGetPose(actor.Name, out var a) || !TryGetPose(entity, out var b)) return false;
            metres = a.DistanceTo(b);
            return true;
        }
    }

    /// <summary>The party for the NPC simulation and the presentation (<see cref="IEscorts"/>).</summary>
    public sealed class SessionEscorts : IEscorts
    {
        readonly PlayerParty _party;
        readonly SessionPositions _positions;

        public SessionEscorts(PlayerParty party, SessionPositions positions)
        {
            _party = party ?? throw new ArgumentNullException(nameof(party));
            _positions = positions ?? throw new ArgumentNullException(nameof(positions));
        }

        public EscortMode ModeOf(ActorId npc)
        {
            if (_party.IsGuiding(npc)) return EscortMode.Guide;
            if (!_party.TryGet(npc, out var member)) return EscortMode.None;
            return member.Waiting ? EscortMode.Wait : EscortMode.Follow;
        }

        public float FollowDistance(ActorId npc) => _party.FollowDistance(npc);

        public bool TryGetDestination(ActorId guide, out WorldPose destination)
        {
            destination = default;
            return _party.IsGuiding(guide) && _positions.TryGetPose(_party.Guide.Destination, out destination);
        }

        public bool TryGetLeader(out WorldPose leader) => _positions.TryGetPose(ActorId.Player.Name, out leader);
    }
}
