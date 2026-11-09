using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// An item lying in the world at the start of a game (a coin on a table, a sword in a rack, a herb). Position in
    /// metres, orientation as forward and up vectors, in the presentation's axes (the importer converts from G3).
    /// </summary>
    public sealed class WorldItemDef : IDefinition
    {
        [JsonConstructor]
        public WorldItemDef(string id, string item, int amount, float[] position, float[] forward, float[] up, float scale, ActorId owner,
                            string sector = null)
        {
            Id = id;
            Item = item;
            Amount = amount;
            Position = position ?? new float[3];
            Forward = forward ?? new float[] { 0, 0, 1 };
            Up = up ?? new float[] { 0, 1, 0 };
            Scale = scale <= 0 ? 1 : scale;
            Owner = owner;
            Sector = string.IsNullOrEmpty(sector) ? null : sector;
        }

        /// <summary>The G3 entity GUID.</summary>
        public string Id { get; }
        public string Item { get; }
        public int Amount { get; }
        public float[] Position { get; }
        public float[] Forward { get; }
        public float[] Up { get; }
        public float Scale { get; }
        /// <summary>Taking it is theft from this NPC (empty = nobody's).</summary>
        public ActorId Owner { get; }
        /// <summary>
        /// The story-switchable world sector the item lies in (G3 SetSectorStatus); null = always there. While the sector
        /// is off the item is not in the world (<see cref="WorldItems.IsPresent"/>).
        /// </summary>
        public string Sector { get; }

        string IDefinition.Key => Id;
    }

    /// <summary>Published when the hero picks up a world item.</summary>
    public readonly struct WorldItemTaken : IGameEvent
    {
        public WorldItemTaken(string worldItem, string item, int amount, ActorId owner)
        {
            WorldItem = worldItem;
            Item = item;
            Amount = amount;
            Owner = owner;
        }

        public string WorldItem { get; }
        public string Item { get; }
        public int Amount { get; }
        public ActorId Owner { get; }
    }

    /// <summary>
    /// An item that came to lie in the world during the game (G3 DropHandItems: the weapon in the hand of a knocked-out
    /// or killed character becomes an item entity whose owner is that character). Position in metres; yaw in degrees.
    /// </summary>
    public sealed class DroppedItem
    {
        public DroppedItem(string id, string item, int amount, int quality, ActorId owner, float[] position, float yaw,
                           float[] rotation = null, bool spent = false)
        {
            Rotation = rotation != null && rotation.Length == 4 ? rotation : null;
            Spent = spent;
            Id = id;
            Item = item;
            Amount = amount;
            Quality = quality;
            Owner = owner;
            Position = position ?? new float[3];
            Yaw = yaw;
        }

        /// <summary>"drop:N", unique within the game.</summary>
        public string Id { get; }
        public string Item { get; }
        public int Amount { get; }
        public int Quality { get; }
        /// <summary>Who dropped it (taking it is never theft: Docs/perception-crime.md, dropped items are free).</summary>
        public ActorId Owner { get; }
        public float[] Position { get; }
        public float Yaw { get; }
        /// <summary>
        /// The exact world rotation (quaternion x, y, z, w) it is shown in, or null: it rests on the ground across
        /// <see cref="Yaw"/>. A spent arrow keeps the pose it stuck in (fix-195).
        /// </summary>
        public float[] Rotation { get; }
        /// <summary>A shot arrow or bolt that came to rest (<see cref="SpentAmmoRules"/>, fix-195).</summary>
        public bool Spent { get; }
    }

    /// <summary>
    /// The items lying in the world: the content's world items while nobody took them, and the items dropped during the
    /// game (<see cref="DroppedItem"/>). Picking one up moves it to the hero.
    /// </summary>
    public sealed class WorldItems
    {
        public const string DroppedPrefix = "drop:";

        readonly HashSet<string> _taken = new HashSet<string>(StringComparer.Ordinal);
        readonly SortedDictionary<string, DroppedItem> _dropped = new SortedDictionary<string, DroppedItem>(StringComparer.Ordinal);
        readonly IContentDatabase _content;
        readonly Inventories _inventories;
        readonly IEventBus _events;
        int _nextDrop = 1;

        public WorldItems(IContentDatabase content, Inventories inventories, IEventBus events)
        {
            _content = content;
            _inventories = inventories;
            _events = events;
        }

        /// <summary>A world item left the world (picked up, taken by an NPC).</summary>
        public event Action<string> Removed;
        /// <summary>An item was dropped into the world.</summary>
        public event Action<DroppedItem> Dropped;
        /// <summary>A dropped item was taken (the item, and who took it: the hero or an NPC).</summary>
        public event Action<DroppedItem, ActorId> DroppedTaken;

        public bool IsPresent(string worldItem) =>
            _dropped.ContainsKey(worldItem) ||
            (!_taken.Contains(worldItem) && _content.TryGet(worldItem, out WorldItemDef def) && InSector(def.Sector));

        /// <summary>
        /// Whether a story-switchable world sector is on (G3 SetSectorStatus; set by the session from its WorldSectors).
        /// Null = every sector is on. Items of a sector that is off are not in the world.
        /// </summary>
        public Func<string, bool> SectorEnabled { get; set; }

        bool InSector(string sector) => sector == null || SectorEnabled == null || SectorEnabled(sector);

        /// <summary>
        /// A world sector was switched: its items that are no longer present leave the world for the views
        /// (<see cref="Removed"/>); items of a sector switched on appear on the views' next refresh.
        /// </summary>
        public void OnSectorChanged(string sector)
        {
            if (string.IsNullOrEmpty(sector) || InSector(sector) || Removed == null) return;
            foreach (var def in _content.All<WorldItemDef>())
                if (string.Equals(def.Sector, sector, StringComparison.OrdinalIgnoreCase)) Removed(def.Id);
        }

        public IEnumerable<string> Taken => _taken;

        List<string> _takenSorted;

        /// <summary>fix-171: <see cref="Taken"/> sorted ordinally, kept until the next pick-up (the save's order).</summary>
        public IReadOnlyList<string> TakenSorted
        {
            get
            {
                if (_takenSorted == null)
                {
                    _takenSorted = new List<string>(_taken);
                    _takenSorted.Sort(StringComparer.Ordinal);
                }
                return _takenSorted;
            }
        }

        /// <summary>Every dropped item still lying in the world, by id.</summary>
        public IEnumerable<DroppedItem> AllDropped => _dropped.Values;

        public bool TryGetDropped(string id, out DroppedItem item) => _dropped.TryGetValue(id ?? "", out item);

        /// <summary>The items <paramref name="owner"/> dropped that still lie in the world.</summary>
        public IEnumerable<DroppedItem> DroppedBy(ActorId owner)
        {
            foreach (var d in _dropped.Values)
                if (d.Owner == owner) yield return d;
        }

        /// <summary>
        /// Puts an item into the world. The caller has taken it from the owner's inventory already (or it was gear that
        /// is not in an inventory).
        /// </summary>
        public DroppedItem Drop(ActorId owner, string item, int amount, int quality, float[] position, float yaw)
        {
            if (string.IsNullOrEmpty(item) || amount <= 0) return null;
            var d = new DroppedItem(DroppedPrefix + _nextDrop++, item, amount, quality, owner, position, yaw);
            _dropped[d.Id] = d;
            Dropped?.Invoke(d);
            return d;
        }

        /// <summary>
        /// A shot arrow or bolt of <paramref name="shooter"/> comes to rest in the world at <paramref name="position"/>
        /// in <paramref name="rotation"/> (fix-195, <see cref="SpentAmmoRules"/>): one item, pickable like any dropped
        /// item; beyond <paramref name="maxLying"/> spent ones the oldest leaves the world.
        /// </summary>
        public DroppedItem DropSpent(ActorId shooter, string item, int quality, float[] position, float[] rotation,
                                     int maxLying = SpentAmmoRules.MaxLying)
        {
            if (string.IsNullOrEmpty(item)) return null;
            var d = new DroppedItem(DroppedPrefix + _nextDrop++, item, 1, quality, shooter, position, 0f, rotation, spent: true);
            _dropped[d.Id] = d;
            Dropped?.Invoke(d);
            int lying = 0;
            DroppedItem oldest = null;
            int oldestNumber = int.MaxValue;
            foreach (var x in _dropped.Values)
            {
                if (!x.Spent) continue;
                lying++;
                int number = NumberOf(x.Id);
                if (number < oldestNumber) { oldestNumber = number; oldest = x; }
            }
            if (lying > Math.Max(1, maxLying) && oldest != null)
            {
                _dropped.Remove(oldest.Id);
                Removed?.Invoke(oldest.Id);
            }
            return d;
        }

        static int NumberOf(string id) =>
            id != null && id.StartsWith(DroppedPrefix, StringComparison.Ordinal) && int.TryParse(id.Substring(DroppedPrefix.Length), out int n) ? n : int.MaxValue;

        /// <summary>The hero picks a world item up.</summary>
        public bool PickUp(string worldItem)
        {
            if (_dropped.TryGetValue(worldItem, out var dropped))
            {
                _dropped.Remove(worldItem);
                _inventories.Give(ActorId.Player, dropped.Item, dropped.Amount, dropped.Quality);
                // No owner in the event: picking up a dropped item is never theft.
                _events.Publish(new WorldItemTaken(worldItem, dropped.Item, dropped.Amount, default));
                DroppedTaken?.Invoke(dropped, ActorId.Player);
                Removed?.Invoke(worldItem);
                return true;
            }
            if (!IsPresent(worldItem) || !_content.TryGet(worldItem, out WorldItemDef def)) return false;
            if (_taken.Add(worldItem)) _takenSorted = null;
            string item = _content.TryGet(def.Item, out ItemDef look) && look.TakenAs != null ? look.TakenAs : def.Item;
            _inventories.Give(ActorId.Player, item, def.Amount);
            _events.Publish(new WorldItemTaken(worldItem, item, def.Amount, def.Owner));
            Removed?.Invoke(worldItem);
            return true;
        }

        /// <summary>
        /// An NPC takes a dropped item (G3 _AI_TakeItem): into its inventory, or, with <paramref name="intoInventory"/>
        /// false, back into its gear (an NPC fetching its own weapon). False when it is no longer there.
        /// </summary>
        public bool TakeDropped(string id, ActorId by, bool intoInventory = true)
        {
            if (by.IsPlayer) return PickUp(id);
            if (!_dropped.TryGetValue(id ?? "", out var d)) return false;
            _dropped.Remove(id);
            if (intoInventory) _inventories.Give(by, d.Item, d.Amount, d.Quality);
            DroppedTaken?.Invoke(d, by);
            Removed?.Invoke(id);
            return true;
        }

        internal void RestoreTaken(IEnumerable<string> taken)
        {
            foreach (var id in taken) _taken.Add(id);
            _takenSorted = null;
        }

        internal int NextDrop => _nextDrop;

        internal void RestoreDropped(IEnumerable<DroppedItem> dropped, int nextDrop)
        {
            foreach (var id in new List<string>(_dropped.Keys)) Removed?.Invoke(id);
            _dropped.Clear();
            foreach (var d in dropped)
            {
                _dropped[d.Id] = d;
                Dropped?.Invoke(d);
            }
            _nextDrop = Math.Max(1, nextDrop);
        }
    }
}
