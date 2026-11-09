using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Story
{
    public enum DialogEntryKind
    {
        /// <summary>A topic the player picks from the menu.</summary>
        Choice,
        /// <summary>A menu choice that leaves the conversation ("End").</summary>
        Exit,
        /// <summary>A sub-choice that returns to the top-level topics ("Back"); it does not keep its parent available.</summary>
        Back,
        /// <summary>"Nothing to say": offered only when no other choice except exits is available.</summary>
        Fallback,
        /// <summary>The NPC refuses to talk; played automatically at the start (only the first available one).</summary>
        Refuse,
        /// <summary>The NPC has something urgent; played automatically at the start (only the first available one).</summary>
        Important,
        /// <summary>The NPC speaks first; every available one plays automatically whenever the top level is shown.</summary>
        News,
        /// <summary>An ambient remark outside conversations; never part of a conversation.</summary>
        Comment,
    }

    /// <summary>
    /// One topic of a conversation: who offers it, when it is available, how it is ordered and what it does.
    /// Sub-choices name their <see cref="Parent"/>; they are offered right after the parent has run.
    /// </summary>
    public sealed class DialogEntryDef : IDefinition
    {
        [JsonConstructor]
        public DialogEntryDef(Id<DialogEntryDef> id, ActorId owner, Id<DialogEntryDef> parent, DialogEntryKind kind,
                              long order, TextId title, bool repeatable, bool needsAvailableChild, bool closesSiblings,
                              string uniqueGroup, Condition condition, IReadOnlyList<PreCheck> preChecks,
                              IReadOnlyList<DialogCommand> commands, Id<QuestDef> logQuest = default)
        {
            Id = id;
            Owner = owner;
            Parent = parent;
            Kind = kind;
            Order = order;
            Title = title;
            Repeatable = repeatable;
            NeedsAvailableChild = needsAvailableChild;
            ClosesSiblings = closesSiblings;
            UniqueGroup = uniqueGroup;
            Condition = condition;
            PreChecks = preChecks ?? Array.Empty<PreCheck>();
            Commands = commands ?? Array.Empty<DialogCommand>();
            LogQuest = logQuest;
        }

        public Id<DialogEntryDef> Id { get; }
        /// <summary>The actor whose topic this is. Entries owned by the player are offered in every conversation.</summary>
        public ActorId Owner { get; }
        /// <summary>Empty for top-level entries.</summary>
        public Id<DialogEntryDef> Parent { get; }
        public DialogEntryKind Kind { get; }
        /// <summary>Menu position; lower first. Computed by the importer from the source's own ordering rules.</summary>
        public long Order { get; }
        /// <summary>Menu text.</summary>
        public TextId Title { get; }
        /// <summary>False: the entry disappears once it has run.</summary>
        public bool Repeatable { get; }
        /// <summary>Offered only if at least one of its sub-choices (other than <see cref="DialogEntryKind.Back"/>) is available.</summary>
        public bool NeedsAvailableChild { get; }
        /// <summary>Running it removes its sibling sub-choices for good.</summary>
        public bool ClosesSiblings { get; }
        /// <summary>Of the available entries with the same non-empty group, only the first is listed.</summary>
        public string UniqueGroup { get; }
        /// <summary>Null = always available.</summary>
        public Condition Condition { get; }
        public IReadOnlyList<PreCheck> PreChecks { get; }
        public IReadOnlyList<DialogCommand> Commands { get; }
        /// <summary>The quest whose log records the lines spoken in this entry; empty for none.</summary>
        public Id<QuestDef> LogQuest { get; }

        [JsonIgnore]
        public bool IsSubChoice => !Parent.IsEmpty;
        [JsonIgnore]
        public bool PlaysAutomatically => Kind == DialogEntryKind.Refuse || Kind == DialogEntryKind.Important || Kind == DialogEntryKind.News;

        string IDefinition.Key => Id.Name;
    }
}
