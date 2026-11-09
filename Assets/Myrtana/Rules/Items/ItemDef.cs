using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>Inventory page an item belongs to (G3 gEItemCategory).</summary>
    public enum ItemCategory { Misc, Weapon, Armor, Artefact, Alchemy, Written, Spellbook, Skill }

    /// <summary>The equipment slot an item is worn in.</summary>
    public enum EquipSlot { None, MeleeWeapon, RangedWeapon, Shield, Ammo, Armor, Helmet, Amulet, Ring }

    /// <summary>
    /// An item of the content (G3 item template): names, value, how it is worn or used, and what it changes.
    /// <see cref="Kind"/> is the item's use type ("OneHanded", "Potion", "Valuable" …) as data, used by trade
    /// specialists and views; behaviour is decided by <see cref="Slot"/>, <see cref="UseEffects"/> and the flags.
    /// </summary>
    public sealed class ItemDef : IDefinition
    {
        [JsonConstructor]
        public ItemDef(string id, TextId name, TextId description, ItemCategory category, string kind, EquipSlot slot,
                       int goldValue, int quality, string icon, ItemVisual visual,
                       IReadOnlyList<AttributeRequirement> requirements, IReadOnlyList<string> requiredSkills,
                       IReadOnlyList<AttributeGain> equipModifiers, IReadOnlyList<AttributeGain> useEffects,
                       IReadOnlyList<string> grantedSkills, ItemDamage damage, ItemFlags flags, string takenAs = null,
                       string material = null, IReadOnlyList<AttributeGain> modifiers = null, IReadOnlyList<string> skillBonuses = null,
                       ItemProjectile projectile = null)
        {
            Id = id;
            Name = name;
            Description = description;
            Category = category;
            Kind = kind;
            Slot = slot;
            GoldValue = goldValue;
            Quality = quality;
            Icon = icon;
            Visual = visual;
            Requirements = requirements ?? Array.Empty<AttributeRequirement>();
            RequiredSkills = requiredSkills ?? Array.Empty<string>();
            EquipModifiers = equipModifiers ?? Array.Empty<AttributeGain>();
            UseEffects = useEffects ?? Array.Empty<AttributeGain>();
            GrantedSkills = grantedSkills ?? Array.Empty<string>();
            Damage = damage;
            Flags = flags;
            TakenAs = takenAs;
            Material = material;
            Modifiers = modifiers ?? EquipModifiers.Concat(UseEffects).ToList();
            SkillBonuses = skillBonuses ?? Array.Empty<string>();
            Projectile = projectile;
        }

        public string Id { get; }
        public TextId Name { get; }
        public TextId Description { get; }
        public ItemCategory Category { get; }
        public string Kind { get; }
        public EquipSlot Slot { get; }
        public int GoldValue { get; }
        /// <summary>Quality bits of the template (forged, blessed …).</summary>
        public int Quality { get; }
        /// <summary>Key of the inventory icon (resolved by the presentation layer).</summary>
        public string Icon { get; }
        /// <summary>How the item looks in the world and, for armour, on the hero; null = no visual.</summary>
        public ItemVisual Visual { get; }
        public IReadOnlyList<AttributeRequirement> Requirements { get; }
        public IReadOnlyList<string> RequiredSkills { get; }
        /// <summary>Applied while worn (protections, maximum mana …).</summary>
        public IReadOnlyList<AttributeGain> EquipModifiers { get; }
        /// <summary>Applied permanently when the item is used (food, potions).</summary>
        public IReadOnlyList<AttributeGain> UseEffects { get; }
        /// <summary>Skills learned when the item is used (perk potions, skill items).</summary>
        public IReadOnlyList<string> GrantedSkills { get; }
        /// <summary>
        /// All modifiers of the template in slot order (G3 <c>ModAttrib1..6</c>), whatever the item does with them; the item
        /// panel lists them for every item. Content imported before this field: the equip modifiers and use effects.
        /// </summary>
        public IReadOnlyList<AttributeGain> Modifiers { get; }
        /// <summary>
        /// Skills the item names as its bonus (G3 <c>ActivateSkill1/2</c>, e.g. Perk_OrcSlayer on the Orc Slayer); the item
        /// panel shows them as "HUD_Bonus_Skill". Empty in content imported before this field.
        /// </summary>
        public IReadOnlyList<string> SkillBonuses { get; }
        /// <summary>Null for items that do no damage.</summary>
        public ItemDamage Damage { get; }
        public ItemFlags Flags { get; }
        /// <summary>A world-only look (a gold stack on a table): taking it gives this item instead, in the same amount
        /// (G3 <c>gCItem_PS.ItemInventory</c>). Null = the item itself.</summary>
        public string TakenAs { get; }
        /// <summary>What the item is made of for impact sounds: the eEShapeMaterial of its template's collision shape
        /// ("Metal", "Wood" …; weapons, shields and ammunition only). Null = unknown.</summary>
        public string Material { get; }
        /// <summary>How an arrow or bolt ends its flight (G3 gCProjectile_PS, fix-176); null for other items and for content
        /// imported before this field (<see cref="ProjectileTouchRules.Of"/> then knows the CP 1.75 ammunition).</summary>
        public ItemProjectile Projectile { get; }

        [JsonIgnore] public bool IsEquippable => Slot != EquipSlot.None;
        [JsonIgnore] public bool IsUsable => UseEffects.Count > 0 || GrantedSkills.Count > 0;
        [JsonIgnore] public bool IsMission => (Flags & ItemFlags.Mission) != 0;

        string IDefinition.Key => Id;
    }

    [Flags]
    public enum ItemFlags
    {
        None = 0,
        /// <summary>Quest item: cannot be sold.</summary>
        Mission = 1,
        /// <summary>A mage robe (armour perks treat robes differently).</summary>
        Robe = 2,
        /// <summary>Body armour that covers the head: no helmet with it.</summary>
        FullBody = 4,
    }

    /// <summary>An attribute an item needs (the hero's current value ≥ <see cref="Minimum"/>).</summary>
    public sealed class AttributeRequirement
    {
        [JsonConstructor]
        public AttributeRequirement(string attribute, int minimum)
        {
            Attribute = attribute;
            Minimum = minimum;
        }

        public string Attribute { get; }
        public int Minimum { get; }
    }

    public sealed class ItemDamage
    {
        [JsonConstructor]
        public ItemDamage(string type, int amount, TextId name)
        {
            Type = type;
            Amount = amount;
            Name = name;
        }

        /// <summary>"Blade", "Impact", "Missile", "Fire" …</summary>
        public string Type { get; }
        public int Amount { get; }
        /// <summary>Label of the damage type ("Blade damage").</summary>
        public TextId Name { get; }
    }

    /// <summary>The projectile part of an ammunition template (gCProjectile_PS, fix-176).</summary>
    public sealed class ItemProjectile
    {
        [JsonConstructor]
        public ItemProjectile(string touch, bool linkToBones)
        {
            Touch = touch;
            LinkToBones = linkToBones;
        }

        /// <summary>TouchBehavior: "KillSelf", "Reflect", "Stop" or "MaterialDependant" (<see cref="ProjectileTouch"/>).</summary>
        public string Touch { get; }
        /// <summary>Sticks to the touched bone of a character.</summary>
        public bool LinkToBones { get; }
    }

    /// <summary>
    /// Asset keys of an item's look: a static mesh (lying in the world, held) or a body actor (worn armour), with the
    /// texture variant. Resolved by the presentation layer; the core never loads assets.
    /// </summary>
    public sealed class ItemVisual
    {
        [JsonConstructor]
        public ItemVisual(string mesh, string body, int materialSwitch)
        {
            Mesh = mesh;
            Body = body;
            MaterialSwitch = materialSwitch;
        }

        public string Mesh { get; }
        public string Body { get; }
        public int MaterialSwitch { get; }
    }
}
