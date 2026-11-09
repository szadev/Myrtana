using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;

namespace Myrtana.Story
{
    /// <summary>Which automatic entries a topic list may start with.</summary>
    public enum AutoPlay
    {
        /// <summary>Menu choices only (after a refused pre-check).</summary>
        None,
        /// <summary>News entries, then the menu (after an entry has run).</summary>
        News,
        /// <summary>The first Refuse, the first Important, all News, then the menu (conversation start).</summary>
        All,
    }

    /// <summary>
    /// Builds the ordered list of what a conversation offers next: automatic entries first, then menu choices by
    /// <see cref="DialogEntryDef.Order"/>, one entry per <see cref="DialogEntryDef.UniqueGroup"/>, and fallback
    /// entries only when nothing else is left to choose. Entries owned by the player join every conversation.
    /// </summary>
    public sealed class TopicListBuilder
    {
        readonly DialogCatalog _catalog;
        readonly DialogMemory _memory;

        public TopicListBuilder(DialogCatalog catalog, DialogMemory memory)
        {
            _catalog = catalog;
            _memory = memory;
        }

        public List<DialogEntryDef> TopLevel(in ConditionContext context, AutoPlay autoPlay)
        {
            var candidates = _catalog.TopLevelOf(context.Partner)
                .Concat(_catalog.TopLevelOf(ActorId.Player))
                .OrderBy(e => e.Order).ThenBy(e => e.Id.Name, StringComparer.Ordinal);
            return Build(candidates, context, autoPlay);
        }

        /// <summary>
        /// An Important entry is available and no Refuse entry of the NPC itself (G3 HasImportantInfo: the hero's own
        /// refusals do not count).
        /// </summary>
        public bool HasImportantInfo(in ConditionContext context)
        {
            bool important = false;
            foreach (var e in _catalog.TopLevelOf(context.Partner).Concat(_catalog.TopLevelOf(ActorId.Player)))
            {
                if (e.Kind == DialogEntryKind.Refuse && e.Owner != ActorId.Player)
                {
                    if (IsAvailable(e, context)) return false;
                }
                else if (e.Kind == DialogEntryKind.Important && !important && IsAvailable(e, context)) important = true;
            }
            return important;
        }

        /// <summary>
        /// The partner itself (not the hero) has an available News entry, played first when a conversation starts
        /// (fix-206: an invisible talking object speaks it on approach, <c>Myrtana.Rules.SpeakerApproach</c>).
        /// </summary>
        public bool HasNews(in ConditionContext context)
        {
            foreach (var e in _catalog.TopLevelOf(context.Partner))
                if (e.Kind == DialogEntryKind.News && IsAvailable(e, context)) return true;
            return false;
        }

        /// <summary>
        /// G3 <c>gCInfoManager_PS::HasInfos</c> (Game 0x101cea00, fix-158): the partner has something to talk about — an
        /// available top-level entry of its own or of the player (BuildMapInfosByOwners 0x101cd2e0 appends every PC_Hero
        /// info to each owner's list, and an owner without infos uses the PC_Hero list), other than a comment, an "End" or a
        /// "Back" (CondType 51-53, InfoType Comment). Trade, teaching, joining, "nothing to say" … all count.
        /// </summary>
        public bool HasInfos(in ConditionContext context)
        {
            foreach (var e in _catalog.TopLevelOf(context.Partner))
                if (CountsAsInfo(e) && IsAvailable(e, context)) return true;
            foreach (var e in _catalog.TopLevelOf(ActorId.Player))
                if (CountsAsInfo(e) && IsAvailable(e, context)) return true;
            return false;
        }

        static bool CountsAsInfo(DialogEntryDef e) =>
            e.Kind != DialogEntryKind.Comment && e.Kind != DialogEntryKind.Exit && e.Kind != DialogEntryKind.Back;

        public List<DialogEntryDef> SubChoices(DialogEntryDef parent, in ConditionContext context) => SubChoices(parent.Id, context);

        public List<DialogEntryDef> SubChoices(Id<DialogEntryDef> parent, in ConditionContext context) =>
            Build(_catalog.ChildrenOf(parent), context, AutoPlay.News);

        public bool IsAvailable(DialogEntryDef entry, in ConditionContext context)
        {
            if (entry.Kind == DialogEntryKind.Comment) return false;
            if (!entry.Repeatable && _memory.WasGiven(entry.Id)) return false;
            if (entry.Condition != null && !entry.Condition.Evaluate(context)) return false;
            return !entry.NeedsAvailableChild || HasAvailableChild(entry, context);
        }

        bool HasAvailableChild(DialogEntryDef entry, in ConditionContext context)
        {
            foreach (var child in _catalog.ChildrenOf(entry.Id))
                if (child.Kind != DialogEntryKind.Back && IsAvailable(child, context)) return true;
            return false;
        }

        List<DialogEntryDef> Build(IEnumerable<DialogEntryDef> candidates, in ConditionContext context, AutoPlay autoPlay)
        {
            var available = new List<DialogEntryDef>();
            foreach (var e in candidates)
                if (IsAvailable(e, context)) available.Add(e);

            var result = new List<DialogEntryDef>();
            if (autoPlay == AutoPlay.All)
            {
                AddFirst(result, available, DialogEntryKind.Refuse);
                AddFirst(result, available, DialogEntryKind.Important);
            }
            if (autoPlay != AutoPlay.None) result.AddRange(available.Where(e => e.Kind == DialogEntryKind.News));
            result.AddRange(MenuChoices(available));
            return result;
        }

        static void AddFirst(List<DialogEntryDef> result, List<DialogEntryDef> available, DialogEntryKind kind)
        {
            var first = available.FirstOrDefault(e => e.Kind == kind);
            if (first != null) result.Add(first);
        }

        static IEnumerable<DialogEntryDef> MenuChoices(List<DialogEntryDef> available)
        {
            var groups = new HashSet<string>(StringComparer.Ordinal);
            var menu = available
                .Where(e => e.Kind == DialogEntryKind.Choice || e.Kind == DialogEntryKind.Back || e.Kind == DialogEntryKind.Exit || e.Kind == DialogEntryKind.Fallback)
                .Where(e => string.IsNullOrEmpty(e.UniqueGroup) || groups.Add(e.UniqueGroup))
                .ToList();
            bool hasRealChoice = menu.Any(e => e.Kind == DialogEntryKind.Choice);
            return hasRealChoice ? menu.Where(e => e.Kind != DialogEntryKind.Fallback) : menu;
        }
    }
}
