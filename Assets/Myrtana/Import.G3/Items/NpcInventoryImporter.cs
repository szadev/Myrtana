using System;
using System.Collections.Generic;
using System.Linq;
using G3.Formats.Animation;
using Myrtana.Core;
using Myrtana.Rules;
using G3Slot = G3.Formats.Items.EquipSlot;

namespace Myrtana.Import.G3
{
    /// <summary>
    /// The NPCs' inventories (<see cref="NpcInventoryDef"/>) from their world entities' gCInventory_PS
    /// (Docs/formats-items.md §3.6): static stacks, the weaponry (first Weaponry treasure set + stacks in weapon slots,
    /// G3 EquipWeaponry), and the plunder sets (uses Plunder, Unique, Trophy, Mining: G3 GeneratePlunderInventory).
    /// Body parts (head, hair, beard, body) are not inventory items and drop out.
    /// </summary>
    public static class NpcInventoryImporter
    {
        static readonly HashSet<G3Slot> WeaponSlots = new HashSet<G3Slot>
        {
            G3Slot.RightHand, G3Slot.LeftHand, G3Slot.EquipLeftHand, G3Slot.BackLeft, G3Slot.BackRight, G3Slot.Bow, G3Slot.Crossbow,
            G3Slot.Ammo,
        };
        static readonly HashSet<TreasureUse> PlunderUses = new HashSet<TreasureUse>
        {
            TreasureUse.Plunder, TreasureUse.Unique, TreasureUse.Trophy, TreasureUse.Mining,
        };

        public static List<NpcInventoryDef> Import(IReadOnlyDictionary<ActorId, NpcSpawn> spawns, G3ItemContent items, ImportReport report)
        {
            var sets = items.TreasureSets.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
            var list = new List<NpcInventoryDef>();
            foreach (var kv in spawns.OrderBy(k => k.Key.Name, StringComparer.Ordinal))
            {
                var inv = kv.Value.Inventory;
                if (inv == null) continue;
                var named = inv.TreasureSets.Where(n => !string.IsNullOrEmpty(n)).Select(n => sets.TryGetValue(n, out var set) ? set : null)
                               .Where(s => s != null).ToList();
                var plunder = named.Where(s => PlunderUses.Contains(s.Use)).Select(s => s.Id).ToList();
                var gear = new List<TreasureStack>();
                var weaponry = named.FirstOrDefault(s => s.Use == TreasureUse.Weaponry);
                if (weaponry != null) gear.AddRange(weaponry.Stacks);

                var inWeaponSlot = new HashSet<int>(inv.Slots.Where(s => WeaponSlots.Contains(s.Slot) && s.Stack >= 0).Select(s => s.Stack));
                var stacks = new List<TreasureStack>();
                for (int i = 0; i < inv.Stacks.Count; i++)
                {
                    var s = inv.Stacks[i];
                    string item = items.ItemOf(s.Template);
                    if (item == null) continue; // body parts, unknown templates
                    var stack = new TreasureStack(item, Math.Max(1, s.Amount), s.Quality);
                    if (inWeaponSlot.Contains(i)) gear.Add(stack);
                    else stacks.Add(stack);
                }
                if (stacks.Count == 0 && gear.Count == 0 && plunder.Count == 0) continue;
                if (stacks.Count > 0) report.Count("NPC with static inventory stacks", kv.Key.Name);
                if (plunder.Count > 0) report.Count("NPC with plunder sets", kv.Key.Name);
                list.Add(new NpcInventoryDef(kv.Key, stacks.Count > 0 ? stacks : null, gear.Count > 0 ? gear : null,
                                             plunder.Count > 0 ? plunder : null));
            }
            return list;
        }
    }
}
