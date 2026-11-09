using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Story
{
    /// <summary>
    /// A party topic is available with the actor (G3 condition types Join, MobJoin, SlaveJoin, Dismiss, MobDismiss, Wait,
    /// Companion; <see cref="PlayerParty.Offers"/>).
    /// </summary>
    public sealed class PartyAllows : Condition
    {
        [JsonConstructor]
        public PartyAllows(ActorRef actor, PartyOffer offer)
        {
            Actor = actor;
            Offer = offer;
        }

        public ActorRef Actor { get; }
        public PartyOffer Offer { get; }

        public override bool Evaluate(in ConditionContext context)
        {
            var party = context.Services.Party;
            return party != null && party.Offers(context.Resolve(Actor), Offer);
        }
    }

    /// <summary>Every listed actor is led by the player (CP CondPlayerPartyMember; BringNpc delivery "led by the player").</summary>
    public sealed class InPlayerParty : Condition
    {
        [JsonConstructor]
        public InPlayerParty(IReadOnlyList<ActorRef> actors) => Actors = actors ?? Array.Empty<ActorRef>();

        public IReadOnlyList<ActorRef> Actors { get; }

        public override bool Evaluate(in ConditionContext context)
        {
            var party = context.Services.Party;
            if (party == null) return false;
            foreach (var a in Actors)
                if (!party.IsLedByPlayer(context.Resolve(a))) return false;
            return true;
        }
    }

    /// <summary>
    /// The actor is within <see cref="Metres"/> of the named entity (an actor or a named place): G3 CondOwnerNearEntity
    /// (5 m), the BringNpc delivery test (20 m) and the FollowNpc delivery test (5 m). False when a position is unknown.
    /// </summary>
    public sealed class Near : Condition
    {
        [JsonConstructor]
        public Near(ActorRef actor, string entity, float metres)
        {
            Actor = actor;
            Entity = entity;
            Metres = metres;
        }

        public ActorRef Actor { get; }
        public string Entity { get; }
        public float Metres { get; }

        public override bool Evaluate(in ConditionContext context)
        {
            var actor = context.Resolve(Actor);
            if (string.Equals(actor.Name, Entity, StringComparison.Ordinal)) return true;
            return context.Services.Positions.TryGetDistance(actor, Entity, out float d) && d <= Metres;
        }
    }
}
