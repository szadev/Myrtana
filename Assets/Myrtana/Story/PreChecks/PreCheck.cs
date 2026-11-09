using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Story
{
    /// <summary>
    /// A requirement checked when the player picks an entry (the entry is offered regardless). If it fails, the
    /// <see cref="Refusal"/> commands run instead of the entry, which stays available for later.
    /// </summary>
    public abstract class PreCheck
    {
        protected PreCheck(IReadOnlyList<DialogCommand> refusal) => Refusal = refusal ?? Array.Empty<DialogCommand>();

        public IReadOnlyList<DialogCommand> Refusal { get; }

        public abstract bool Passes(in PreCheckContext context);
    }

    public readonly struct PreCheckContext
    {
        public PreCheckContext(StoryWorld world, ActorId partner)
        {
            World = world;
            Partner = partner;
        }

        public StoryWorld World { get; }
        public ActorId Partner { get; }

        public IReputation Reputation => World.Reputation;
        public StoryServices Services => World.Services;

        public ActorId Resolve(ActorRef actor) => actor.Resolve(Partner);
    }

    /// <summary>The player needs at least <see cref="Minimum"/> fame with the enclave of <see cref="Member"/>.</summary>
    public sealed class MinEnclaveFame : PreCheck
    {
        [JsonConstructor]
        public MinEnclaveFame(ActorRef member, int minimum, IReadOnlyList<DialogCommand> refusal) : base(refusal)
        {
            Member = member;
            Minimum = minimum;
        }

        public ActorRef Member { get; }
        public int Minimum { get; }

        public override bool Passes(in PreCheckContext context) =>
            context.Reputation.EnclaveFameOf(Member.Resolve(context.Partner)) >= Minimum;
    }

    /// <summary>
    /// The player needs at least <see cref="Minimum"/> fame with the political faction of <see cref="Member"/>
    /// (G3 PoliticalFriend).
    /// </summary>
    public sealed class MinPoliticalFame : PreCheck
    {
        [JsonConstructor]
        public MinPoliticalFame(ActorRef member, int minimum, IReadOnlyList<DialogCommand> refusal) : base(refusal)
        {
            Member = member;
            Minimum = minimum;
        }

        public ActorRef Member { get; }
        public int Minimum { get; }

        public override bool Passes(in PreCheckContext context) =>
            context.Reputation.PoliticalFameOf(Member.Resolve(context.Partner)) >= Minimum;
    }

    /// <summary>The hero carries at least <see cref="Amount"/> gold (G3 info GoldCost; the entry's script pays it).</summary>
    public sealed class MinGold : PreCheck
    {
        [JsonConstructor]
        public MinGold(int amount, IReadOnlyList<DialogCommand> refusal) : base(refusal) => Amount = amount;

        public int Amount { get; }

        public override bool Passes(in PreCheckContext context)
        {
            var items = context.Services.Items;
            return items.Inventories.Of(ActorId.Player).Has(items.GoldItem, Amount);
        }
    }

    /// <summary>
    /// One teaching check (requirements, learning points or gold, as G3 checks them in this order with one refusal
    /// each); <see cref="Failure"/> names the result this check refuses.
    /// </summary>
    public sealed class LessonCheck : PreCheck
    {
        [JsonConstructor]
        public LessonCheck(Lesson lesson, int gold, TeachResult failure, IReadOnlyList<DialogCommand> refusal) : base(refusal)
        {
            Lesson = lesson;
            Gold = gold;
            Failure = failure;
        }

        public Lesson Lesson { get; }
        public int Gold { get; }
        public TeachResult Failure { get; }

        public override bool Passes(in PreCheckContext context) =>
            context.Services.Items.Teaching.Check(Lesson, Gold) != Failure;
    }
}

namespace Myrtana.Story
{
    /// <summary>
    /// G3 CanJoinPlayerParty (Join pre-check): refused while another Party companion follows the hero within the
    /// processing range (<see cref="Myrtana.Rules.PlayerParty.HasRoomFor"/>).
    /// </summary>
    public sealed class PartyHasRoom : PreCheck
    {
        [JsonConstructor]
        public PartyHasRoom(ActorRef actor, IReadOnlyList<DialogCommand> refusal) : base(refusal) => Actor = actor;

        public ActorRef Actor { get; }

        public override bool Passes(in PreCheckContext context)
        {
            var party = context.Services.Party;
            return party == null || party.HasRoomFor(context.Resolve(Actor));
        }
    }
}
