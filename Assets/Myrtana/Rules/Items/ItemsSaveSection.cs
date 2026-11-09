using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// Save section "rules.items": every inventory, what the hero wears, which world items were taken, which NPCs
    /// trade or teach, each merchant's stock state, the items dropped into the world and whose plunder was rolled.
    /// </summary>
    public sealed class ItemsSaveSection : ISaveParticipant
    {
        readonly Inventories _inventories;
        readonly HeroEquipment _equipment;
        readonly WorldItems _world;
        readonly ActorRoles _roles;
        readonly TradeService _trade;
        readonly NpcLoot _npcLoot;

        public ItemsSaveSection(Inventories inventories, HeroEquipment equipment, WorldItems world, ActorRoles roles, TradeService trade,
                                NpcLoot npcLoot = null)
        {
            _npcLoot = npcLoot;
            _inventories = inventories;
            _equipment = equipment;
            _world = world;
            _roles = roles;
            _trade = trade;
        }

        public string Section => "rules.items";
        /// <summary>
        /// 2: the Party, Mob and Slavery roles. 3: each merchant's merchandise. 4: dropped items, rolled plunder (and the
        /// NPCs' static stacks are in the inventories; older saves get them on load). 5: each stack's acquisition number
        /// (<see cref="ItemStack.Acquired"/>; older saves number the stacks in their saved order). 6: each rolled body's decided
        /// trophy perks (fix-143, <see cref="NpcLoot.TrophiesCovered"/>; older saves: the perks known at the load). 7: each
        /// dropped item's spent-ammo mark and exact rotation (fix-195, <see cref="DroppedItem.Spent"/>, <see cref="DroppedItem.Rotation"/>).
        /// </summary>
        public int Version => 7;

        public void Write(ISaveWriter w)
        {
            // fix-171: the inventories in the cached name order (no LINQ, no sort per snapshot), the empty ones skipped
            var inventories = _inventories.ByName;
            int filled = 0;
            for (int i = 0; i < inventories.Count; i++) if (inventories[i].Value.Stacks.Count > 0) filled++;
            w.WriteInt32(filled);
            for (int i = 0; i < inventories.Count; i++)
            {
                var kv = inventories[i];
                if (kv.Value.Stacks.Count == 0) continue;
                w.WriteString(kv.Key.Name);
                w.WriteInt32(kv.Value.Stacks.Count);
                foreach (var s in kv.Value.Stacks)
                {
                    w.WriteString(s.Item);
                    w.WriteInt32(s.Amount);
                    w.WriteInt32(s.Quality);
                    w.WriteInt32(s.Acquired);
                }
            }
            var worn = _equipment.Worn.OrderBy(kv => kv.Key).ToList();
            w.WriteInt32(worn.Count);
            foreach (var kv in worn)
            {
                w.WriteInt32((int)kv.Key);
                w.WriteString(kv.Value);
            }
            var taken = _world.TakenSorted;
            w.WriteInt32(taken.Count);
            for (int i = 0; i < taken.Count; i++) w.WriteString(taken[i]);
            foreach (ActorRole role in Enum.GetValues(typeof(ActorRole))) w.WriteSortedStrings(_roles.All(role).Select(a => a.Name));
            var merchants = _trade.States.OrderBy(kv => kv.Key.Name, StringComparer.Ordinal).ToList();
            w.WriteInt32(merchants.Count);
            foreach (var kv in merchants)
            {
                w.WriteString(kv.Key.Name);
                w.WriteBool(kv.Value.Generated);
                w.WriteDouble(kv.Value.LastContactHours);
                w.WriteInt32(kv.Value.Merchandise.Count);
                foreach (var m in kv.Value.Merchandise)
                {
                    w.WriteString(m.Key);
                    w.WriteInt32(m.Value);
                }
            }
            var dropped = _world.AllDropped.ToList();
            w.WriteInt32(_world.NextDrop);
            w.WriteInt32(dropped.Count);
            foreach (var d in dropped)
            {
                w.WriteString(d.Id);
                w.WriteString(d.Item);
                w.WriteInt32(d.Amount);
                w.WriteInt32(d.Quality);
                w.WriteString(d.Owner.Name ?? "");
                for (int i = 0; i < 3; i++) w.WriteDouble(d.Position[i]);
                w.WriteDouble(d.Yaw);
                w.WriteBool(d.Spent);
                w.WriteBool(d.Rotation != null);
                if (d.Rotation != null) for (int i = 0; i < 4; i++) w.WriteDouble(d.Rotation[i]);
            }
            // fix-171: the rolled bodies in the cached name order; the covered perks (at most the three trophy perks) sorted in place
            var bodies = _npcLoot != null ? _npcLoot.GeneratedByName : (IReadOnlyList<ActorId>)Array.Empty<ActorId>();
            w.WriteInt32(bodies.Count);
            for (int i = 0; i < bodies.Count; i++) w.WriteString(bodies[i].Name);
            var covered = _npcLoot?.TrophiesCovered;
            int withCover = 0;
            if (covered != null) for (int i = 0; i < bodies.Count; i++) if (covered.ContainsKey(bodies[i])) withCover++;
            if (covered != null && withCover != covered.Count) { WriteCoveredSlow(w, covered); return; }   // not expected: keys = bodies
            w.WriteInt32(withCover);
            for (int i = 0; i < bodies.Count && covered != null; i++)
            {
                if (!covered.TryGetValue(bodies[i], out var perks)) continue;
                w.WriteString(bodies[i].Name);
                WriteSorted(w, perks);
            }
        }

        readonly List<string> _perkBuffer = new List<string>(4);

        void WriteSorted(ISaveWriter w, HashSet<string> values)
        {
            _perkBuffer.Clear();
            foreach (var v in values) _perkBuffer.Add(v);
            _perkBuffer.Sort(StringComparer.Ordinal);
            w.WriteInt32(_perkBuffer.Count);
            for (int i = 0; i < _perkBuffer.Count; i++) w.WriteString(_perkBuffer[i]);
        }

        static void WriteCoveredSlow(ISaveWriter w, IReadOnlyDictionary<ActorId, HashSet<string>> covered)
        {
            var list = covered.OrderBy(kv => kv.Key.Name, StringComparer.Ordinal).ToList();
            w.WriteInt32(list.Count);
            foreach (var kv in list)
            {
                w.WriteString(kv.Key.Name);
                w.WriteSortedStrings(kv.Value);
            }
        }

        public void Read(ISaveReader r, int version)
        {
            _inventories.Clear();
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                var inventory = _inventories.Of(new ActorId(r.ReadString()));
                int stacks = r.ReadInt32();
                for (int s = 0; s < stacks; s++)
                {
                    string item = r.ReadString();
                    int amount = r.ReadInt32(), quality = r.ReadInt32();
                    inventory.Restore(item, amount, quality, version >= 5 ? r.ReadInt32() : 0);
                }
            }
            int worn = r.ReadInt32();
            var slots = new List<KeyValuePair<WornSlot, string>>(worn);
            for (int i = 0; i < worn; i++) slots.Add(new KeyValuePair<WornSlot, string>((WornSlot)r.ReadInt32(), r.ReadString()));
            _equipment.Restore(slots);
            _world.RestoreTaken(r.ReadStrings());
            _roles.ClearAll();
            foreach (ActorRole role in Enum.GetValues(typeof(ActorRole)))
            {
                if (version < 2 && role > ActorRole.Teach) break; // version 1 stored Trade and Teach only
                foreach (var actor in r.ReadStrings()) _roles.Set(new ActorId(actor), role, true);
            }
            int merchants = r.ReadInt32();
            for (int i = 0; i < merchants; i++)
            {
                var merchant = new ActorId(r.ReadString());
                bool generated = r.ReadBool();
                double lastContact = r.ReadDouble();
                var merchandise = new List<KeyValuePair<string, int>>();
                int count = version >= 3 ? r.ReadInt32() : 0;
                for (int m = 0; m < count; m++) merchandise.Add(new KeyValuePair<string, int>(r.ReadString(), r.ReadInt32()));
                _trade.Restore(merchant, generated, lastContact, merchandise);
            }
            if (version < 3) _trade.AssumeAllMerchandise();
            if (version < 4)
            {
                _world.RestoreDropped(Array.Empty<DroppedItem>(), 1);
                _npcLoot?.Restore(Array.Empty<ActorId>());
                _npcLoot?.SeedStartStacks();
                return;
            }
            int next = r.ReadInt32();
            int dropCount = r.ReadInt32();
            var drops = new List<DroppedItem>(dropCount);
            for (int i = 0; i < dropCount; i++)
            {
                string id = r.ReadString(), item = r.ReadString();
                int amount = r.ReadInt32(), quality = r.ReadInt32();
                string owner = r.ReadString();
                var position = new[] { (float)r.ReadDouble(), (float)r.ReadDouble(), (float)r.ReadDouble() };
                float yaw = (float)r.ReadDouble();
                bool spent = version >= 7 && r.ReadBool();
                float[] rotation = null;
                if (version >= 7 && r.ReadBool())
                    rotation = new[] { (float)r.ReadDouble(), (float)r.ReadDouble(), (float)r.ReadDouble(), (float)r.ReadDouble() };
                drops.Add(new DroppedItem(id, item, amount, quality, string.IsNullOrEmpty(owner) ? default : new ActorId(owner), position,
                                          yaw, rotation, spent));
            }
            _world.RestoreDropped(drops, next);
            var plundered = r.ReadStrings().Select(n => new ActorId(n)).ToList();
            List<KeyValuePair<ActorId, IEnumerable<string>>> covered = null;
            if (version >= 6)
            {
                int bodies = r.ReadInt32();
                covered = new List<KeyValuePair<ActorId, IEnumerable<string>>>(bodies);
                for (int i = 0; i < bodies; i++)
                    covered.Add(new KeyValuePair<ActorId, IEnumerable<string>>(new ActorId(r.ReadString()), r.ReadStrings().ToList()));
            }
            _npcLoot?.Restore(plundered, covered); // HeroSaveSection was read before: older saves use the perks known now
        }
    }
}
