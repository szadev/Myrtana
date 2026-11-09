using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Story
{
    /// <summary>What completes a quest (quests.json "types"). Counters per target complete the quest when all are reached.</summary>
    public enum QuestObjectiveKind
    {
        /// <summary>Ends only through dialog commands.</summary>
        None,
        /// <summary>Talk to the targets (a Delivery dialog entry of each target counts).</summary>
        Report,
        /// <summary>Give the target items to the destination.</summary>
        HasItems,
        /// <summary>Take the target items from the destination.</summary>
        Plunder,
        Kill,
        /// <summary>Kill or knock out.</summary>
        Defeat,
        DriveAway,
        BringNpc,
        FollowNpc,
        EnterArea,
        FreeEnclave,
        Arena,
        Sparring,
        SpellCast,
    }

    public sealed class QuestObjective
    {
        public static readonly QuestObjective None = new QuestObjective(QuestObjectiveKind.None, null, default);

        [JsonConstructor]
        public QuestObjective(QuestObjectiveKind kind, IReadOnlyList<QuestTarget> targets, ActorId destination)
        {
            Kind = kind;
            Targets = targets ?? Array.Empty<QuestTarget>();
            Destination = destination;
        }

        public QuestObjectiveKind Kind { get; }
        public IReadOnlyList<QuestTarget> Targets { get; }
        /// <summary>The NPC items are given to (HasItems) or taken from (Plunder).</summary>
        public ActorId Destination { get; }

        public int IndexOf(string target)
        {
            for (int i = 0; i < Targets.Count; i++)
                if (string.Equals(Targets[i].Target, target, StringComparison.Ordinal)) return i;
            return -1;
        }
    }

    /// <summary>An NPC, item, area or enclave with the amount needed.</summary>
    public sealed class QuestTarget
    {
        [JsonConstructor]
        public QuestTarget(string target, int amount)
        {
            Target = target;
            Amount = amount;
        }

        public string Target { get; }
        public int Amount { get; }
    }

    /// <summary>Rewards on success, applied in this order (quests.json "rewardOrder"): fame changes, attribute, XP.</summary>
    public sealed class QuestRewards
    {
        public static readonly QuestRewards None = new QuestRewards(null, null, 0);

        [JsonConstructor]
        public QuestRewards(IReadOnlyList<FameChange> fame, AttributeGain attribute, int xp)
        {
            Fame = fame ?? Array.Empty<FameChange>();
            Attribute = attribute;
            Xp = xp;
        }

        /// <summary>Political fame changes come before enclave fame changes; the attribute goes between them.</summary>
        public IReadOnlyList<FameChange> Fame { get; }
        /// <summary>Null = none.</summary>
        public AttributeGain Attribute { get; }
        /// <summary>Final XP (G3's ×5 script multiplier is applied at import).</summary>
        public int Xp { get; }
    }

    public sealed class FameChange
    {
        [JsonConstructor]
        public FameChange(FameKind kind, string group, int amount)
        {
            Kind = kind;
            Group = group;
            Amount = amount;
        }

        public FameKind Kind { get; }
        public string Group { get; }
        /// <summary>Negative for the failure side (PoliticalFailure / EnclaveFailure).</summary>
        public int Amount { get; }
    }
}
