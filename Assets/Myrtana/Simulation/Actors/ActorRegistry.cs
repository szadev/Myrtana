using System;
using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Simulation
{
    /// <summary>Runtime state of one actor. Grows with each slice (pose, vitals, inventory, brain …).</summary>
    public sealed class ActorState
    {
        public ActorState(NpcDef definition) => Definition = definition;

        public NpcDef Definition { get; }
        public ActorId Id => Definition.Id;

        public bool TalkedToPlayer
        {
            get => _talkedToPlayer;
            set
            {
                _talkedToPlayer = value;
                Mark();
            }
        }

        bool _talkedToPlayer;
        bool? _immortalOverride;

        /// <summary>
        /// fix-157: a saved fact of this actor was set (talked, alignment, class, Immortal, worn items) since the game
        /// started. Sticky. The registry keeps the marked actors in a list, so save sections need not walk every actor.
        /// </summary>
        internal bool Marked { get; private set; }
        /// <summary>Told once, when <see cref="Marked"/> becomes true.</summary>
        internal Action<ActorState> FirstMark { get; set; }

        void Mark()
        {
            if (Marked) return;
            Marked = true;
            FirstMark?.Invoke(this);
        }

        /// <summary>The story changed the political alignment (G3 SetPAL); <see cref="Alignment"/> then replaces the content's.</summary>
        public bool AlignmentChanged { get; private set; }
        /// <summary>The alignment the story gave ("Reb", "Nom" …; null = none). Meaningful when <see cref="AlignmentChanged"/>.</summary>
        public string Alignment { get; private set; }

        public void ChangeAlignment(string alignment)
        {
            Mark();
            AlignmentChanged = true;
            Alignment = alignment;
        }

        /// <summary>The current alignment: the story's if it changed it, else <paramref name="contentAlignment"/>.</summary>
        public string AlignmentOr(string contentAlignment) => AlignmentChanged ? Alignment : contentAlignment;

        /// <summary>The story changed the NPC's class (G3 SetClass); <see cref="Class"/> then replaces the content's.</summary>
        public bool ClassChanged { get; private set; }
        public Myrtana.Rules.NpcClass Class { get; private set; }

        public void ChangeClass(Myrtana.Rules.NpcClass npcClass)
        {
            Mark();
            ClassChanged = true;
            Class = npcClass;
        }

        /// <summary>The story set the Immortal flag (CP Humanize); null = the content's.</summary>
        public bool? ImmortalOverride
        {
            get => _immortalOverride;
            set
            {
                _immortalOverride = value;
                Mark();
            }
        }

        readonly List<string> _worn = new List<string>();

        /// <summary>Items the story had the NPC put on (G3 Wear), oldest first.</summary>
        public IReadOnlyList<string> Worn => _worn;

        /// <summary>G3 Wear: the item is equipped from now on (the latest wins over older ones of the same kind).</summary>
        public void Wear(string item)
        {
            if (string.IsNullOrEmpty(item)) return;
            Mark();
            _worn.Remove(item);
            _worn.Add(item);
        }
    }

    /// <summary>Every actor of the game, created from the content at a new game. Owns actor state; views only display it.</summary>
    public sealed class ActorRegistry : IActorFacts, IActorStore
    {
        readonly Dictionary<ActorId, ActorState> _actors = new Dictionary<ActorId, ActorState>();
        readonly HashSet<ActorId> _runtime = new HashSet<ActorId>();
        readonly List<ActorState> _marked = new List<ActorState>();
        readonly Action<ActorState> _onFirstMark;

        public ActorRegistry(IEnumerable<NpcDef> definitions)
        {
            _onFirstMark = _marked.Add;
            foreach (var d in definitions) _actors[d.Id] = new ActorState(d) { FirstMark = _onFirstMark };
        }

        public int Count => _actors.Count;

        public IEnumerable<ActorState> All => _actors.Values;

        /// <summary>
        /// fix-157: the actors <paramref name="match"/> accepts among those with a saved fact set
        /// (<see cref="ActorState.Marked"/>: talked, alignment, class, Immortal, worn items), sorted by id (ordinal), into
        /// <paramref name="into"/> (cleared first). Save sections call it for every snapshot: it walks only the marked actors.
        /// </summary>
        public void CollectSorted(Func<ActorState, bool> match, List<ActorState> into)
        {
            into.Clear();
            foreach (var a in _marked)
                if (match(a)) into.Add(a);
            if (into.Count > 1) into.Sort(ById);
        }

        static readonly Comparison<ActorState> ById = (x, y) => string.CompareOrdinal(x.Id.Name, y.Id.Name);

        public bool TryGet(ActorId id, out ActorState actor) => _actors.TryGetValue(id, out actor);

        public bool Exists(ActorId actor) => _actors.ContainsKey(actor);

        /// <summary>An actor that appears during play (a summoned creature); false when the id is taken.</summary>
        public bool Add(NpcDef definition)
        {
            if (definition == null || _actors.ContainsKey(definition.Id)) return false;
            _actors[definition.Id] = new ActorState(definition) { FirstMark = _onFirstMark };
            _runtime.Add(definition.Id);
            return true;
        }

        /// <summary>The actor appeared during play (<see cref="Add"/>), not from the content.</summary>
        public bool IsRuntime(ActorId actor) => _runtime.Contains(actor);

        /// <summary>Forgets an actor (its body is gone). Prefer <see cref="ActorRemoval.Remove"/>, which also clears every other store.</summary>
        public bool Remove(ActorId actor)
        {
            _runtime.Remove(actor);
            if (!_actors.TryGetValue(actor, out var state)) return false;
            if (state.Marked) _marked.Remove(state);
            return _actors.Remove(actor);
        }

        /// <summary>A removed actor that appeared during play is forgotten; a content actor keeps its identity (definition).</summary>
        void IActorStore.Forget(ActorId actor)
        {
            if (_runtime.Contains(actor)) Remove(actor);
        }

        bool IActorStore.Holds(ActorId actor) => _runtime.Contains(actor);

        public bool HasTalkedToPlayer(ActorId actor) => _actors.TryGetValue(actor, out var a) && a.TalkedToPlayer;

        public void MarkTalkedToPlayer(ActorId actor)
        {
            if (_actors.TryGetValue(actor, out var a)) a.TalkedToPlayer = true;
        }
    }
}
