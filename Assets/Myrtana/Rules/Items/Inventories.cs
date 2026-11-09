using System;
using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>Items changed hands between two actors (quests count deliveries and plunder from it).</summary>
    public readonly struct ItemsTransferred : IGameEvent
    {
        public ItemsTransferred(ActorId from, ActorId to, string item, int amount)
        {
            From = from;
            To = to;
            Item = item;
            Amount = amount;
        }

        /// <summary>Empty when the items came from the world (picked up, created).</summary>
        public ActorId From { get; }
        /// <summary>Empty when the items left the game (used, destroyed).</summary>
        public ActorId To { get; }
        public string Item { get; }
        public int Amount { get; }
    }

    /// <summary>
    /// The inventories of all actors, created on first use. Moving items between actors goes through
    /// <see cref="Transfer"/> so that every move is published once.
    /// </summary>
    public sealed class Inventories : IActorStore
    {
        readonly Dictionary<ActorId, Inventory> _byActor = new Dictionary<ActorId, Inventory>();
        readonly IEventBus _events;

        /// <summary>The actor left the game (<see cref="ActorRemoval"/>): its entries go.</summary>
        public void Forget(ActorId actor)
        {
            if (!actor.IsPlayer && _byActor.Remove(actor)) _sorted = null;
        }

        public bool Holds(ActorId actor) => !actor.IsPlayer && _byActor.ContainsKey(actor);

        public Inventories(IEventBus events) => _events = events;

        public Inventory Of(ActorId actor)
        {
            if (!_byActor.TryGetValue(actor, out var inventory))
            {
                _byActor[actor] = inventory = new Inventory();
                _sorted = null;
            }
            return inventory;
        }

        public IEnumerable<KeyValuePair<ActorId, Inventory>> All => _byActor;

        List<KeyValuePair<ActorId, Inventory>> _sorted;

        /// <summary>
        /// fix-171: every inventory (empty ones too) ordered by the actor's name (ordinal), kept until an actor is added or
        /// forgotten (the save writes them in this order; sorting the thousands of NPC inventories on every 2-minute recovery
        /// snapshot was a third of its "rules.items" time).
        /// </summary>
        public IReadOnlyList<KeyValuePair<ActorId, Inventory>> ByName
        {
            get
            {
                if (_sorted == null)
                {
                    _sorted = new List<KeyValuePair<ActorId, Inventory>>(_byActor);
                    _sorted.Sort((a, b) => string.CompareOrdinal(a.Key.Name, b.Key.Name));
                }
                return _sorted;
            }
        }

        /// <summary>
        /// Moves items; returns the amount moved. With <paramref name="createMissing"/> the giver first gets what it
        /// lacks (G3's dialog "Give" does this for every giver).
        /// </summary>
        public int Transfer(ActorId from, ActorId to, string item, int amount, bool createMissing = false, int quality = 0)
        {
            if (amount <= 0) return 0;
            var source = Of(from);
            if (createMissing && source.Count(item) < amount) source.Add(item, amount - source.Count(item), quality);
            int moved = source.Remove(item, amount);
            if (moved == 0) return 0;
            Of(to).Add(item, moved, quality);
            _events.Publish(new ItemsTransferred(from, to, item, moved));
            return moved;
        }

        /// <summary>Items enter the game into an actor's inventory (pickup, loot, quest rewards).</summary>
        public void Give(ActorId to, string item, int amount, int quality = 0)
        {
            if (amount <= 0) return;
            Of(to).Add(item, amount, quality);
            _events.Publish(new ItemsTransferred(default, to, item, amount));
        }

        /// <summary>Items leave the game (used up). Returns the amount removed.</summary>
        public int Consume(ActorId from, string item, int amount)
        {
            int removed = Of(from).Remove(item, amount);
            if (removed > 0) _events.Publish(new ItemsTransferred(from, default, item, removed));
            return removed;
        }

        internal void Clear()
        {
            foreach (var inventory in _byActor.Values) inventory.Clear();
        }
    }
}
