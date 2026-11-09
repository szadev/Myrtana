using System;
using System.Collections.Generic;

namespace Myrtana.Rules
{
    /// <summary>
    /// What an actor can fight with, from its equipment items: the melee weapon (fists when none), a shield, a ranged
    /// weapon with its ammunition, and whether it holds a staff. The NPC combat AI chooses between them by distance.
    /// </summary>
    public sealed class CombatLoadout
    {
        public static readonly CombatLoadout Fists = new CombatLoadout();

        public WeaponClass Melee { get; private set; } = WeaponClass.Fist;
        public ItemDef MeleeItem { get; private set; }
        /// <summary>
        /// Quality bits of this actor's copy of <see cref="MeleeItem"/> beyond the item's own (G3 inventory stack quality): the
        /// worn species weapon a creature is handed (<see cref="WithNaturalWeapon"/>, fix-237).
        /// </summary>
        public WeaponQuality MeleeQuality { get; private set; }
        /// <summary>
        /// A second one-handed weapon for the left hand (G3 dual wielding, gESlot_BackLeft): an NPC's weaponry lists the
        /// main one-handed weapon twice (EquipWeaponry splits a stack of two, <see cref="DualWieldRules.DualWeaponOf"/>); null = none.
        /// </summary>
        public ItemDef OffHandItem { get; private set; }
        /// <summary>Fights with a one-handed weapon in each hand (the 1H_1H stance).</summary>
        public bool DualWield => Melee == WeaponClass.OneHanded && OffHandItem != null;
        public bool Shield { get; private set; }
        /// <summary>The first shield (carried on Slot_ShieldBack, in the left hand with a one-handed weapon drawn); null = none.</summary>
        public ItemDef ShieldItem { get; private set; }
        /// <summary>Bow or crossbow; null = none.</summary>
        public WeaponClass? Ranged { get; private set; }
        public ItemDef RangedItem { get; private set; }
        public ItemDef Ammo { get; private set; }

        public bool HasRanged => Ranged != null;
        /// <summary>A ranged weapon with ammunition (G3 falls back to melee when the ammunition is missing).</summary>
        public bool CanShoot => Ranged != null && Ammo != null;
        public bool HeavyMelee => Melee == WeaponClass.TwoHanded || Melee == WeaponClass.Staff || Melee == WeaponClass.Halberd || Melee == WeaponClass.Axe;

        /// <summary>The first melee weapon, shield, ranged weapon and ammunition among <paramref name="items"/>.</summary>
        public static CombatLoadout From(IEnumerable<ItemDef> items)
        {
            var loadout = new CombatLoadout();
            foreach (var item in items)
            {
                if (item == null) continue;
                var weapon = WeaponClassOf(item.Kind);
                if (weapon == WeaponClass.Bow || weapon == WeaponClass.Crossbow)
                {
                    if (loadout.Ranged == null) { loadout.Ranged = weapon; loadout.RangedItem = item; }
                }
                else if (weapon != null)
                {
                    if (loadout.MeleeItem == null) { loadout.Melee = weapon.Value; loadout.MeleeItem = item; }
                    else if (loadout.OffHandItem == null && weapon == WeaponClass.OneHanded && loadout.Melee == WeaponClass.OneHanded &&
                             item.Id == loadout.MeleeItem.Id)
                        loadout.OffHandItem = item;
                }
                else if (item.Kind == "Shield")
                {
                    loadout.Shield = true;
                    if (loadout.ShieldItem == null) loadout.ShieldItem = item;
                }
                else if (item.Kind == "Arrow" || item.Kind == "Bolt")
                {
                    if (loadout.Ammo == null) loadout.Ammo = item;
                }
            }
            if (loadout.Ammo != null && loadout.Ranged != null && !AmmoFits(loadout.Ranged.Value, loadout.Ammo.Kind)) loadout.Ammo = null;
            return loadout;
        }

        /// <summary>The same loadout without the items in <paramref name="lost"/> (weapons taken from a knocked-out NPC).</summary>
        public CombatLoadout Without(ICollection<string> lost)
        {
            if (lost == null || lost.Count == 0) return this;
            var items = new List<ItemDef>();
            foreach (var item in new[] { MeleeItem, OffHandItem, RangedItem, Ammo })
                if (item != null && !lost.Contains(item.Id)) items.Add(item);
            var loadout = From(items);
            if (loadout.MeleeItem != null && loadout.MeleeItem == MeleeItem) loadout.MeleeQuality = MeleeQuality;
            // fix-164: a looted shield is lost too (it stayed on the looted skeleton's back: carried=1 after Take all)
            bool shieldLost = ShieldItem != null && lost.Contains(ShieldItem.Id);
            loadout.Shield = Shield && !shieldLost;
            loadout.ShieldItem = shieldLost ? null : ShieldItem;
            return loadout;
        }

        /// <summary>
        /// fix-237: a creature without a melee weapon item takes its species weapon (G3 Script_Game 0x1000b200 AssureItems,
        /// <see cref="SpeciesWeaponry.NaturalWeapon"/>: the Stalker's worn spiked club, the Ogre's worn morning star …) with
        /// that stack quality; the same loadout when it already holds one, or <paramref name="weapon"/> is null / no melee weapon.
        /// </summary>
        public CombatLoadout WithNaturalWeapon(ItemDef weapon, WeaponQuality quality)
        {
            if (weapon == null || MeleeItem != null) return this;
            var cls = WeaponClassOf(weapon.Kind);
            if (cls == null || cls == WeaponClass.Bow || cls == WeaponClass.Crossbow) return this;
            var items = new List<ItemDef> { weapon };
            foreach (var item in new[] { RangedItem, Ammo }) if (item != null) items.Add(item);
            var loadout = From(items);
            loadout.Shield = Shield;
            loadout.ShieldItem = ShieldItem;
            loadout.MeleeQuality = quality;
            return loadout;
        }

        /// <summary>The weapon class of an item use type ("OneHanded", "Axe", "CrossBow" …); null = not a weapon.</summary>
        public static WeaponClass? WeaponClassOf(string kind)
        {
            switch (kind)
            {
                case "OneHanded": return WeaponClass.OneHanded;
                case "TwoHanded": return WeaponClass.TwoHanded;
                case "Axe": return WeaponClass.Axe;
                case "Halberd": return WeaponClass.Halberd;
                case "Staff": return WeaponClass.Staff;
                case "Bow": return WeaponClass.Bow;
                case "CrossBow": return WeaponClass.Crossbow;
                case "Fist":
                case "PhysicalFist": return WeaponClass.Fist;
                case "Broom":
                case "Rake":
                case "Shovel":
                case "Pickaxe": return WeaponClass.Tool;
                default: return null;
            }
        }

        static bool AmmoFits(WeaponClass ranged, string ammoKind) =>
            ranged == WeaponClass.Bow ? string.Equals(ammoKind, "Arrow", StringComparison.Ordinal) : string.Equals(ammoKind, "Bolt", StringComparison.Ordinal);
    }
}
