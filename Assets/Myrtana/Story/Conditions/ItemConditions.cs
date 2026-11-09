using System.Text.Json.Serialization;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Story
{
    /// <summary>
    /// The actor carries at least <see cref="Amount"/> of the item; an amount of 0 means the actor must not carry any
    /// (dialog.json "CondItems").
    /// </summary>
    public sealed class HasItems : Condition
    {
        [JsonConstructor]
        public HasItems(ActorRef holder, string item, int amount)
        {
            Holder = holder;
            Item = item;
            Amount = amount;
        }

        public ActorRef Holder { get; }
        public string Item { get; }
        public int Amount { get; }

        public override bool Evaluate(in ConditionContext context)
        {
            int count = context.Services.Items.Inventories.Of(context.Resolve(Holder)).Count(Item);
            return Amount == 0 ? count == 0 : count >= Amount;
        }
    }

    /// <summary>How much of what a HasItems quest still lacks the hero must carry (<see cref="CarriesQuestItems"/>).</summary>
    public enum QuestItemShare
    {
        /// <summary>Every item in full: carried + delivered ≥ amount (G3 gCQuest_PS::HaveNPCsAllRequiredItems).</summary>
        All,
        /// <summary>At least one piece of an item not yet delivered in full (G3 gCQuest_PS::HasNPCpartOfRequiredItems).</summary>
        Some,
    }

    /// <summary>
    /// The hero carries what a HasItems quest still lacks, counting what the destination already got (the quest's
    /// counters): the Delivery (<see cref="QuestItemShare.All"/>) and PartDelivery (<see cref="QuestItemShare.Some"/>)
    /// tests of G3 gCInfo_PS::AreConditionsFulfilled [CP Game.dll 0x101c3370 case 8/9; 0x101576d0, 0x10157610]. fix-183:
    /// plain item checks with the full amounts offered Jensgar's PartDelivery to a hero with 19 ice wolf furs (all 30
    /// delivered) and none of the 5 bison furs still missing.
    /// </summary>
    public sealed class CarriesQuestItems : Condition
    {
        [JsonConstructor]
        public CarriesQuestItems(Id<QuestDef> quest, QuestItemShare share)
        {
            Quest = quest;
            Share = share;
        }

        public Id<QuestDef> Quest { get; }
        public QuestItemShare Share { get; }

        public override bool Evaluate(in ConditionContext context)
        {
            var quests = context.World.Quests;
            if (!quests.TryGetDefinition(Quest, out var quest) || quest.Objective.Kind != QuestObjectiveKind.HasItems) return false;
            var counters = quests.ProgressOf(Quest)?.Counters;
            var hero = context.Services.Items.Inventories.Of(ActorId.Player);
            var targets = quest.Objective.Targets;
            for (int i = 0; i < targets.Count; i++)
            {
                int counted = counters != null && i < counters.Length ? counters[i] : 0;
                int carried = hero.Count(targets[i].Target);
                if (Share == QuestItemShare.All && carried + counted < targets[i].Amount) return false;
                if (Share == QuestItemShare.Some && counted < targets[i].Amount && carried > 0) return true;
            }
            return Share == QuestItemShare.All;
        }
    }

    /// <summary>The actor currently trades / teaches.</summary>
    public sealed class HasRole : Condition
    {
        [JsonConstructor]
        public HasRole(ActorRef actor, ActorRole role)
        {
            Actor = actor;
            Role = role;
        }

        public ActorRef Actor { get; }
        public ActorRole Role { get; }

        public override bool Evaluate(in ConditionContext context) => context.Services.Items.Roles.Has(context.Resolve(Actor), Role);
    }

    /// <summary>The conversation partner teaches this lesson now and the hero does not know the skill yet.</summary>
    public sealed class LessonOffered : Condition
    {
        [JsonConstructor]
        public LessonOffered(Lesson lesson) => Lesson = lesson;

        public Lesson Lesson { get; }

        public override bool Evaluate(in ConditionContext context) => context.Services.Items.Teaching.Offers(context.Partner, Lesson);
    }
}
