using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// What an NPC carries according to the content (G3 gCInventory_PS of its world entity, Docs/formats-items.md §3.6):
    /// <list type="bullet">
    /// <item><see cref="Stacks"/>: the static stacks of the entity (present from the start: a quest item, potions …);</item>
    /// <item><see cref="Gear"/>: its weaponry (the first Weaponry treasure set and the equipped weapon slots), which G3
    /// keeps as inventory stacks (EquipWeaponry); the port keeps them with the combat loadout and lists them as loot;</item>
    /// <item><see cref="PlunderSets"/>: treasure sets of use Plunder, Unique, Trophy and Mining, rolled once at the first
    /// knock-out or death (G3 CleanUpPlunderInv → GeneratePlunderInvFromTreasureSets).</item>
    /// </list>
    /// Trade and pickpocket sets are not here (<see cref="MerchantDef"/>, <see cref="PickpocketDef"/>).
    /// </summary>
    public sealed class NpcInventoryDef : IDefinition
    {
        [JsonConstructor]
        public NpcInventoryDef(ActorId npc, IReadOnlyList<TreasureStack> stacks = null, IReadOnlyList<TreasureStack> gear = null,
                               IReadOnlyList<string> plunderSets = null)
        {
            Npc = npc;
            Stacks = stacks ?? Array.Empty<TreasureStack>();
            Gear = gear ?? Array.Empty<TreasureStack>();
            PlunderSets = plunderSets ?? Array.Empty<string>();
        }

        public ActorId Npc { get; }
        public IReadOnlyList<TreasureStack> Stacks { get; }
        public IReadOnlyList<TreasureStack> Gear { get; }
        public IReadOnlyList<string> PlunderSets { get; }

        string IDefinition.Key => Npc.Name;
    }
}
