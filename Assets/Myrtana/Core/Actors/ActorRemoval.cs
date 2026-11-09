using System;
using System.Collections.Generic;

namespace Myrtana.Core
{
    /// <summary>A store that keeps runtime state per actor (vitals, fights, statuses, inventory …).</summary>
    public interface IActorStore
    {
        /// <summary>Drops everything the store keeps about <paramref name="actor"/> (the actor left the game). Must not throw for unknown actors.</summary>
        void Forget(ActorId actor);

        /// <summary>True while the store keeps anything about <paramref name="actor"/> (checks and tests).</summary>
        bool Holds(ActorId actor);
    }

    /// <summary>An actor left the game (a summon's body vanished, a dev spawn was despawned): views and caches drop it.</summary>
    public readonly struct ActorRemoved : IGameEvent
    {
        public ActorRemoved(ActorId actor) => Actor = actor;
        public ActorId Actor { get; }
    }

    /// <summary>
    /// The one path that takes a runtime actor out of the game: every registered <see cref="IActorStore"/> forgets it, in
    /// registration order (fights end first, the actor registry goes last), then <see cref="ActorRemoved"/> is published.
    /// Summons, the dev scenario's despawn and anything else that removes actors go through here, so no store keeps an
    /// entry whose definition is gone.
    /// </summary>
    public sealed class ActorRemoval
    {
        readonly List<IActorStore> _stores = new List<IActorStore>();
        readonly IEventBus _events;
        readonly HashSet<ActorId> _removing = new HashSet<ActorId>();

        public ActorRemoval(IEventBus events = null) => _events = events;

        public IReadOnlyList<IActorStore> Stores => _stores;

        /// <summary>The stores that still keep something about <paramref name="actor"/> (empty after <see cref="Remove"/>).</summary>
        public IEnumerable<IActorStore> Holding(ActorId actor)
        {
            foreach (var store in _stores)
                if (store.Holds(actor)) yield return store;
        }

        /// <summary>Adds a store (null and duplicates are ignored).</summary>
        public ActorRemoval Register(IActorStore store)
        {
            if (store != null && !_stores.Contains(store)) _stores.Add(store);
            return this;
        }

        /// <summary>Takes <paramref name="actor"/> out of every store; false for the player, an empty id or a removal already running.</summary>
        public bool Remove(ActorId actor)
        {
            if (actor.IsEmpty || actor.IsPlayer || !_removing.Add(actor)) return false;
            try
            {
                foreach (var store in _stores) store.Forget(actor);
            }
            finally
            {
                _removing.Remove(actor);
            }
            _events?.Publish(new ActorRemoved(actor));
            return true;
        }
    }
}
