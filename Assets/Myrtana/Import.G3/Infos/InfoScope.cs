using System;
using System.Collections.Generic;
using G3.Formats.Story;
using Myrtana.Core;

namespace Myrtana.Import.G3
{
    /// <summary>
    /// Resolves the entity names an info uses (G3 <c>FindEntityFromString</c>: "npc", "player", "self", "other" or an
    /// NPC name) to actor references, and back to the G3 speaker name that voice files are named after.
    /// </summary>
    public sealed class InfoScope
    {
        readonly IReadOnlyDictionary<string, ActorId> _actors;
        readonly ImportReport _report;

        public InfoScope(Info info, IReadOnlyDictionary<string, ActorId> actorsByG3Name, ImportReport report)
        {
            Info = info;
            _actors = actorsByG3Name;
            _report = report;
            OwnerIsPlayer = string.Equals(info.Owner, NpcImporter.G3HeroName, StringComparison.OrdinalIgnoreCase);
            Owner = OwnerIsPlayer ? ActorId.Player : Actor(info.Owner);
        }

        public Info Info { get; }
        public ActorId Owner { get; }
        /// <summary>The hero's own topics are offered in every conversation; "npc" then means the conversation partner.</summary>
        public bool OwnerIsPlayer { get; }

        /// <summary>The NPC side of the conversation: the owner, or the partner role in the hero's own topics.</summary>
        public ActorRef Npc => OwnerIsPlayer ? ActorRef.Partner : ActorRef.To(Owner);

        public ActorRef Resolve(string entity)
        {
            if (string.IsNullOrEmpty(entity)) return Npc;
            switch (entity.ToLowerInvariant())
            {
                case "npc":
                case "self":
                case "owner":
                case "other":
                    return Npc;
                case "player":
                    return ActorRef.Player;
            }
            if (string.Equals(entity, NpcImporter.G3HeroName, StringComparison.OrdinalIgnoreCase)) return ActorRef.Player;
            return ActorRef.To(Actor(entity));
        }

        /// <summary>
        /// A world entity named by a condition (CondOwnerNearEntity, a quest's DestinationEntity): the actor id of an
        /// imported NPC, "player" for the hero, otherwise the name itself (a named place).
        /// </summary>
        public string EntityName(string g3Name)
        {
            if (string.IsNullOrEmpty(g3Name)) return g3Name;
            if (string.Equals(g3Name, NpcImporter.G3HeroName, StringComparison.OrdinalIgnoreCase)) return ActorId.Player.Name;
            return _actors.TryGetValue(g3Name, out var id) ? id.Name : g3Name;
        }

        /// <summary>
        /// The name is an NPC (the hero or an imported NPC), not a place: G3 gCInfo_PS::GetDistOwnerToEntity scales the
        /// distance to an entity with an NPC property set (gCNPC_PS, id 0x1e) by 0.25 before the 5 m test.
        /// </summary>
        public bool IsNpcEntity(string g3Name) =>
            !string.IsNullOrEmpty(g3Name)
            && (string.Equals(g3Name, NpcImporter.G3HeroName, StringComparison.OrdinalIgnoreCase) || _actors.ContainsKey(g3Name));

        /// <summary>The info's owner as an actor reference (the hero for his own topics).</summary>
        public ActorRef OwnerRef => OwnerIsPlayer ? ActorRef.Player : ActorRef.To(Owner);

        /// <summary>The G3 name voice files use for this actor ("PC_Hero", "Gnar"); null for the partner role.</summary>
        public string G3SpeakerName(ActorRef actor)
        {
            if (actor.IsPartner) return null;
            return actor.Equals(ActorRef.Player) ? NpcImporter.G3HeroName : actor.Value;
        }

        ActorId Actor(string g3Name)
        {
            if (_actors.TryGetValue(g3Name, out var id)) return id;
            _report.Count("entity is not an imported NPC (kept by name)", g3Name);
            return new ActorId(g3Name);
        }
    }
}
