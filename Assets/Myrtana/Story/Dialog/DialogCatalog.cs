using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;

namespace Myrtana.Story
{
    /// <summary>The dialog entries of the content, indexed by owner and by parent and sorted by <see cref="DialogEntryDef.Order"/>.</summary>
    public sealed class DialogCatalog
    {
        static readonly IReadOnlyList<DialogEntryDef> None = new DialogEntryDef[0];

        readonly Dictionary<ActorId, List<DialogEntryDef>> _topLevel = new Dictionary<ActorId, List<DialogEntryDef>>();
        readonly Dictionary<Id<DialogEntryDef>, List<DialogEntryDef>> _children = new Dictionary<Id<DialogEntryDef>, List<DialogEntryDef>>();
        readonly HashSet<ActorId> _withTopics = new HashSet<ActorId>();

        public DialogCatalog(IEnumerable<DialogEntryDef> entries)
        {
            foreach (var e in entries.OrderBy(e => e.Order).ThenBy(e => e.Id.Name, System.StringComparer.Ordinal))
            {
                if (e.IsSubChoice) Add(_children, e.Parent, e);
                else Add(_topLevel, e.Owner, e);
                if (!e.IsSubChoice && e.Kind != DialogEntryKind.Comment) _withTopics.Add(e.Owner);
            }
        }

        public IReadOnlyList<DialogEntryDef> TopLevelOf(ActorId owner) => _topLevel.TryGetValue(owner, out var l) ? l : None;

        public IReadOnlyList<DialogEntryDef> ChildrenOf(Id<DialogEntryDef> parent) => _children.TryGetValue(parent, out var l) ? l : None;

        /// <summary>True if the actor owns at least one conversation topic (comments do not count). Asked every frame for
        /// every NPC in reach, so it is precomputed.</summary>
        public bool HasTopics(ActorId owner) => _withTopics.Contains(owner);

        static void Add<TKey>(Dictionary<TKey, List<DialogEntryDef>> index, TKey key, DialogEntryDef entry)
        {
            if (!index.TryGetValue(key, out var list)) index[key] = list = new List<DialogEntryDef>();
            list.Add(entry);
        }
    }
}
