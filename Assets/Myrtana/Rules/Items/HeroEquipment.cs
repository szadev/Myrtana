using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// The places the hero wears items in; rings have two. <see cref="OffHand"/>: a second one-handed weapon (G3 dual
    /// wielding, gESlot_BackLeft, the slot the shield uses too: one or the other). Saved by number: new slots go last.
    /// </summary>
    public enum WornSlot { MeleeWeapon, RangedWeapon, Shield, Ammo, Armor, Helmet, Amulet, LeftRing, RightRing, OffHand }

    public enum EquipResult { Done, NotEquippable, NotCarried, MissingAttribute, MissingSkill, HelmetWithFullArmor }

    /// <summary>Published when the hero puts on or takes off an item (the armour view swaps the body).</summary>
    public readonly struct EquipmentChanged : IGameEvent
    {
        public EquipmentChanged(WornSlot slot, string item)
        {
            Slot = slot;
            Item = item;
        }

        public WornSlot Slot { get; }
        /// <summary>Null when the slot became empty.</summary>
        public string Item { get; }
    }

    /// <summary>
    /// What the hero wears (items.json "equip"): worn items stay in the inventory; equipping checks the attribute
    /// requirements (forged items need less) and required skills, and refuses a helmet with full-body armour and the
    /// other way round. Worn items' modifiers are the hero's attribute bonuses, and the skills they name (G3 ActivateSkill1/2, e.g.
    /// Perk_OrcSlayer on the Orc Slayer) are active while they are worn (<see cref="Hero.Knows"/>).
    /// </summary>
    public sealed class HeroEquipment
    {
        readonly Hero _hero;
        readonly Inventory _inventory;
        readonly IContentDatabase _content;
        readonly ItemRules _rules;
        readonly IEventBus _events;
        readonly Dictionary<WornSlot, string> _worn = new Dictionary<WornSlot, string>();
        /// <summary>The bonus this equipment added to each attribute, so a recalculation changes only its own share.</summary>
        readonly Dictionary<string, int> _applied = new Dictionary<string, int>(StringComparer.Ordinal);

        public HeroEquipment(Hero hero, Inventory inventory, IContentDatabase content, ItemRules rules, IEventBus events)
        {
            _hero = hero;
            _inventory = inventory;
            _content = content;
            _rules = rules;
            _events = events;
            _inventory.Changed += DropWhatIsNoLongerCarried;
        }

        public IReadOnlyDictionary<WornSlot, string> Worn => _worn;

        public string In(WornSlot slot) => _worn.TryGetValue(slot, out var item) ? item : null;

        public bool IsWorn(string item) => _worn.ContainsValue(item);

        /// <summary>Why the hero cannot wear the item now, or <see cref="EquipResult.Done"/> if he can.</summary>
        public EquipResult Check(string item, out string missing)
        {
            missing = null;
            if (!_content.TryGet(item, out ItemDef def) || !def.IsEquippable) return EquipResult.NotEquippable;
            if (!_inventory.Has(item, CountWorn(item) + 1)) return EquipResult.NotCarried;
            int delta = IsForged(def) ? _rules.ForgedRequirementDelta : 0;
            foreach (var req in def.Requirements)
                if (_hero.Stats.Value(req.Attribute) < Math.Max(0, req.Minimum + delta))
                {
                    missing = req.Attribute;
                    return EquipResult.MissingAttribute;
                }
            foreach (var skill in def.RequiredSkills)
                if (!_hero.Knows(skill))
                {
                    missing = skill;
                    return EquipResult.MissingSkill;
                }
            if (def.Slot == EquipSlot.Helmet && WearsFullBodyArmor()) return EquipResult.HelmetWithFullArmor;
            if (def.Slot == EquipSlot.Armor && (def.Flags & ItemFlags.FullBody) != 0 && In(WornSlot.Helmet) != null) return EquipResult.HelmetWithFullArmor;
            return EquipResult.Done;
        }

        public EquipResult Equip(string item)
        {
            var result = Check(item, out _);
            if (result != EquipResult.Done) return result;
            var def = Definition(item);
            var slot = SlotFor(def);
            if (slot == WornSlot.OffHand || slot == WornSlot.Shield) FreeBackLeft(slot); // G3 gESlot_BackLeft holds one of them
            Put(slot, item);
            return EquipResult.Done;
        }

        /// <summary>
        /// Puts a one-handed weapon into the off-hand slot (G3 dual wielding: the quick-slot key of a second one-handed
        /// weapon, FUN_10002550). Needs the item's requirements, <see cref="DualWieldRules.Perk"/> and a one-handed main
        /// weapon; a shield is taken off.
        /// </summary>
        public EquipResult EquipOffHand(string item)
        {
            var result = Check(item, out _);
            if (result != EquipResult.Done) return result;
            if (!CanDualWield(Definition(item))) return EquipResult.NotEquippable;
            FreeBackLeft(WornSlot.OffHand);
            Put(WornSlot.OffHand, item);
            return EquipResult.Done;
        }

        /// <summary>The worn main and off-hand weapons are both one-handed (the hero can draw them together).</summary>
        public bool DualWieldReady =>
            In(WornSlot.OffHand) != null && Definition(In(WornSlot.MeleeWeapon))?.Kind == DualWieldRules.OneHandedKind;

        /// <summary>The perk is known and the worn main weapon is one-handed: another one-handed weapon may go to the left hand.</summary>
        bool CanDualWield(ItemDef def) =>
            def != null && def.Kind == DualWieldRules.OneHandedKind && _hero.Knows(DualWieldRules.Perk) &&
            Definition(In(WornSlot.MeleeWeapon))?.Kind == DualWieldRules.OneHandedKind;

        void FreeBackLeft(WornSlot taking)
        {
            var other = taking == WornSlot.OffHand ? WornSlot.Shield : WornSlot.OffHand;
            if (In(other) != null) Unequip(other);
        }

        /// <summary>
        /// A conjured weapon (PS_Magic_FlameSword) is put straight into the hand: the spell is the requirement, so the
        /// item's attribute and skill requirements do not apply. It must still be equippable and carried.
        /// </summary>
        public EquipResult EquipConjured(string item)
        {
            if (!_content.TryGet(item, out ItemDef def) || !def.IsEquippable) return EquipResult.NotEquippable;
            if (!_inventory.Has(item, CountWorn(item) + 1)) return EquipResult.NotCarried;
            Put(SlotFor(def.Slot), item);
            return EquipResult.Done;
        }

        public void Unequip(WornSlot slot)
        {
            if (!_worn.Remove(slot)) return;
            Recalculate();
            _events.Publish(new EquipmentChanged(slot, null));
        }

        public void UnequipItem(string item)
        {
            foreach (var slot in _worn.Where(kv => kv.Value == item).Select(kv => kv.Key).ToList()) Unequip(slot);
        }

        /// <summary>
        /// For loading: puts items on without checks or events, then recomputes the bonuses. A save from before fix-192
        /// with a shield beside a two-handed weapon loads without the shield (<see cref="ShieldExcludedBy"/>).
        /// </summary>
        internal void Restore(IEnumerable<KeyValuePair<WornSlot, string>> worn)
        {
            _worn.Clear();
            foreach (var kv in worn) _worn[kv.Key] = kv.Value;
            if (In(WornSlot.Shield) != null && ShieldExcludedBy(Definition(In(WornSlot.MeleeWeapon)))) _worn.Remove(WornSlot.Shield);
            Recalculate();
        }

        /// <summary>
        /// A two-handed main weapon (2H sword, axe, staff, halberd, tool) and a shield are not worn together (fix-192, [owner]
        /// build 22d: the shield stayed on the back and blue in the inventory beside a two-hander; G3 holds a shield only in
        /// the 1H stance). Bows and crossbows have their own slot: the shield stays worn and is hidden while they are drawn
        /// (<see cref="WeaponHold.ShieldSlot"/>).
        /// </summary>
        public static bool ShieldExcludedBy(ItemDef mainWeapon) => mainWeapon != null && WeaponHold.MeleeExcludesShield(mainWeapon.Kind);

        /// <summary>Putting on a two-handed main weapon takes the shield off; putting on a shield takes a two-handed main weapon off.</summary>
        void FreeHandsFor(WornSlot slot, ItemDef def)
        {
            if (slot == WornSlot.MeleeWeapon && ShieldExcludedBy(def) && In(WornSlot.Shield) != null) Unequip(WornSlot.Shield);
            else if (slot == WornSlot.Shield && ShieldExcludedBy(Definition(In(WornSlot.MeleeWeapon)))) Unequip(WornSlot.MeleeWeapon);
        }

        void Put(WornSlot slot, string item)
        {
            FreeHandsFor(slot, Definition(item));
            _worn[slot] = item;
            Recalculate();
            _events.Publish(new EquipmentChanged(slot, item));
        }

        /// <summary>
        /// The slot an item goes to: a one-handed weapon to the off-hand slot when the hero can dual wield and the main
        /// weapon is another stack (G3 FUN_10002550 case 2 / _AI_TakeItem, <see cref="DualWieldRules.EquipSlot"/>).
        /// </summary>
        WornSlot SlotFor(ItemDef def)
        {
            if (def.Slot == EquipSlot.MeleeWeapon && def.Kind == DualWieldRules.OneHandedKind)
            {
                string main = In(WornSlot.MeleeWeapon);
                bool sameStack = main == def.Id && _inventory.Count(def.Id) <= CountWorn(def.Id);
                if (main != null && DualWieldRules.EquipSlot(_hero.Knows(DualWieldRules.Perk), Definition(main)?.Kind, sameStack) == OneHandedUse.OffHand)
                    return WornSlot.OffHand;
            }
            return SlotFor(def.Slot);
        }

        WornSlot SlotFor(EquipSlot slot)
        {
            switch (slot)
            {
                case EquipSlot.MeleeWeapon: return WornSlot.MeleeWeapon;
                case EquipSlot.RangedWeapon: return WornSlot.RangedWeapon;
                case EquipSlot.Shield: return WornSlot.Shield;
                case EquipSlot.Ammo: return WornSlot.Ammo;
                case EquipSlot.Armor: return WornSlot.Armor;
                case EquipSlot.Helmet: return WornSlot.Helmet;
                case EquipSlot.Amulet: return WornSlot.Amulet;
                default: return In(WornSlot.LeftRing) == null ? WornSlot.LeftRing : WornSlot.RightRing;
            }
        }

        void Recalculate()
        {
            var bonus = new Dictionary<string, int>(StringComparer.Ordinal);
            var activations = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in _worn.Values)
            {
                var def = Definition(item);
                if (def == null) continue;
                // G3 gCInventorySlot::OnApplyStats [Game 0x100be5f0]: a worn-out stack (quality bit 0x100) applies half
                // of each modifier and activates no skill; any other stack applies the modifiers and ActivateSkill1/2.
                bool wornOut = IsWornOut(def);
                foreach (var mod in def.EquipModifiers)
                    if (mod.Op == ModifierOp.AddToVal || mod.Op == ModifierOp.AddToMax)
                        bonus[mod.Attribute] = (bonus.TryGetValue(mod.Attribute, out var b) ? b : 0) + (wornOut ? mod.Amount / 2 : mod.Amount);
                if (wornOut) continue;
                foreach (var skill in def.SkillBonuses)
                    if (!string.IsNullOrEmpty(skill))
                        activations[skill] = (activations.TryGetValue(skill, out var n) ? n : 0) + 1;
            }
            _hero.SetActivations(activations);
            // Only the equipment's own share changes: bonuses from other sources (scripts, tests, later timed effects) stay
            // (combat-6: they used to be wiped by every Equip).
            var ids = new HashSet<string>(_applied.Keys, StringComparer.Ordinal);
            ids.UnionWith(bonus.Keys);
            foreach (var id in ids)
            {
                int old = _applied.TryGetValue(id, out var o) ? o : 0, now = bonus.TryGetValue(id, out var n) ? n : 0;
                if (old != now) _hero.Stats.SetBonus(id, _hero.Stats.Bonus(id) - old + now);
            }
            _applied.Clear();
            foreach (var kv in bonus) _applied[kv.Key] = kv.Value;
        }

        void DropWhatIsNoLongerCarried()
        {
            foreach (var item in _worn.Values.Distinct().ToList())
                while (CountWorn(item) > _inventory.Count(item))
                    Unequip(_worn.First(kv => kv.Value == item).Key);
        }

        int CountWorn(string item) => _worn.Values.Count(v => v == item);

        bool WearsFullBodyArmor()
        {
            var armor = In(WornSlot.Armor);
            return armor != null && Definition(armor) is ItemDef def && (def.Flags & ItemFlags.FullBody) != 0;
        }

        bool IsWornOut(ItemDef def) => ((def.Quality | _inventory.QualityOf(def.Id)) & (int)WeaponQuality.Worn) != 0;

        bool IsForged(ItemDef def) =>
            _rules.ForgedQuality != 0 && ((def.Quality | _inventory.QualityOf(def.Id)) & _rules.ForgedQuality) != 0;

        ItemDef Definition(string item) => item != null && _content.TryGet(item, out ItemDef def) ? def : null;
    }
}
