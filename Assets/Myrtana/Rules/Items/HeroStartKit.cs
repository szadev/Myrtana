using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// What the hero carries, wears and knows when a new game starts (content id "heroStart"; G3: the player start kit,
    /// Docs/formats-items.md §3.5). Attributes are not here: they are <see cref="ProgressionRules.HeroStart"/>.
    /// </summary>
    public sealed class HeroStartKit : IDefinition
    {
        public const string DefinitionId = "heroStart";

        [JsonConstructor]
        public HeroStartKit(string id, IReadOnlyList<StartItem> items, IReadOnlyList<string> skills, int startXp = 0)
        {
            Id = id;
            Items = items ?? Array.Empty<StartItem>();
            Skills = skills ?? Array.Empty<string>();
            StartXp = Math.Max(0, startXp);
        }

        /// <summary>
        /// Experience the hero gets as the game starts (CP 1.75: the spawned helper <c>Ardea_Spawn_Point</c> whose only
        /// info gives 500 G3 XP, Docs/game-start.md).
        /// </summary>
        public int StartXp { get; }

        public string Id { get; }
        public IReadOnlyList<StartItem> Items { get; }
        /// <summary>Skills (perks, spells) the hero knows from the start, by skill id.</summary>
        public IReadOnlyList<string> Skills { get; }

        string IDefinition.Key => Id;
    }

    /// <summary>One stack of the start kit.</summary>
    public sealed class StartItem
    {
        [JsonConstructor]
        public StartItem(string item, int amount, int quality, int quickSlot, bool equipped)
        {
            Item = item;
            Amount = amount;
            Quality = quality;
            QuickSlot = quickSlot;
            Equipped = equipped;
        }

        public string Item { get; }
        public int Amount { get; }
        public int Quality { get; }
        /// <summary>Quick-bar slot index (0…9, see <see cref="QuickSlots.SlotOfDigit"/>), −1 = not on the bar.</summary>
        public int QuickSlot { get; }
        /// <summary>The hero wears or wields it from the start.</summary>
        public bool Equipped { get; }
    }

    /// <summary>Gives a <see cref="HeroStartKit"/> to the hero of a new game.</summary>
    public static class HeroStart
    {
        /// <summary>
        /// Skills first (equipping can require them), then the stacks into the inventory, then equipment and quick bar.
        /// Items the hero cannot wear are kept in the inventory and logged.
        /// </summary>
        public static void Apply(HeroStartKit kit, ProgressionService progression, Inventory inventory, HeroEquipment equipment,
                                 QuickSlots quickSlots, IGameLog log)
        {
            if (kit == null) return;
            foreach (var skill in kit.Skills) progression.LearnSkill(skill);
            if (kit.StartXp > 0) progression.AwardXp(kit.StartXp);
            foreach (var stack in kit.Items) inventory.Add(stack.Item, stack.Amount, stack.Quality);
            foreach (var stack in kit.Items)
            {
                if (stack.Equipped)
                {
                    var result = equipment.Equip(stack.Item);
                    if (result != EquipResult.Done) log?.Warning($"start kit: cannot equip {stack.Item} ({result})");
                }
                if (stack.QuickSlot >= 0 && !quickSlots.Assign(stack.QuickSlot, stack.Item))
                    log?.Warning($"start kit: cannot put {stack.Item} on quick slot {stack.QuickSlot}");
            }
        }
    }
}
