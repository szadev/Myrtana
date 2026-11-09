using System;
using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// Which items may go on the quick bar (G3 CP 1.75 Script_Game <c>OnPlayerAssignStackKey</c> 0x1006b900, run by the
    /// digit keys and Backspace on a selected inventory stack and by dragging a stack onto the bar): the stack's use type
    /// must be a weapon, ammunition, shield, staff, spell, potion, food or drink, torch, pipe or letter; any other (armour,
    /// rings, amulets, trophies, keys, tools, maps, books …) is refused with the sound <c>eff_inv_deny_01</c>. A spell item
    /// also needs its spell learned (<see cref="HeroSpells.CanReady"/>). The stack already in that slot: nothing happens.
    /// </summary>
    public static class QuickSlotRules
    {
        /// <summary>The refusal sound (OnPlayerAssignStackKey default case).</summary>
        public const string DenyEffect = "eff_inv_deny_01";

        /// <summary>gEUseType 2–7, 9, 12, 15–24, 32, 44, 51, 52 (the switch's accepted cases).</summary>
        static readonly HashSet<string> Kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "OneHanded", "TwoHanded", "Arrow", "Bow", "CrossBow", "Bolt", "Shield", "Staff", "Cast", "Potion", "Plant", "Meat",
            "Fruit", "Bread", "Bottle", "Cup", "Bowl", "Torch", "Smoke", "Letter", "Halberd", "Axe",
        };

        /// <summary>Whether an item of this use type (<see cref="ItemDef.Kind"/>) may go on the bar.</summary>
        public static bool AllowsKind(string kind) => kind != null && Kinds.Contains(kind);

        /// <summary>
        /// Whether the item may go on the bar. An item without a definition or without a use type has nothing to refuse
        /// it on and is allowed [interp: every G3 item has a use type].
        /// </summary>
        public static bool Allows(IContentDatabase db, string item)
        {
            if (item == null) return false;
            if (db == null || !db.TryGet(item, out ItemDef def) || string.IsNullOrEmpty(def.Kind)) return true;
            return AllowsKind(def.Kind);
        }
    }
}
