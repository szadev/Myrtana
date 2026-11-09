using System;
using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>One value per damage type (protections).</summary>
    public sealed class DamageTypeValues
    {
        readonly int[] _values = new int[7];

        public int this[DamageType type]
        {
            get => _values[(int)type];
            set => _values[(int)type] = value;
        }

        /// <summary>The same value for the six real damage types.</summary>
        public static DamageTypeValues All(int value)
        {
            var v = new DamageTypeValues();
            for (int i = 1; i < v._values.Length; i++) v._values[i] = value;
            return v;
        }
    }

    /// <summary>
    /// What the combat rules need to know about one fighter at the moment of a hit: a plain snapshot built by the
    /// simulation (NPCs) or the player adapter. The resolver only reads it; changes go through <see cref="ActorVitals"/>.
    /// </summary>
    public sealed class Combatant
    {
        static readonly HashSet<string> NoPerks = new HashSet<string>(StringComparer.Ordinal);

        public ActorId Id { get; set; }
        public bool IsPlayer { get; set; }
        /// <summary>The player in an animal body (Animal Form): uses the NPC damage rules.</summary>
        public bool IsTransformed { get; set; }

        // --- identity used by the rules
        /// <summary>"Human", "Orc", "Wolf", "Skeleton" … (species table, skill bonuses).</summary>
        public string Species { get; set; } = "Human";
        public bool Humanoid { get; set; } = true;
        /// <summary>Undead and demons (Perk_Paladin bonus, Destroy Evil).</summary>
        public bool Evil { get; set; }
        public bool Animal { get; set; }
        /// <summary>Huntable game (Boar, Bison, Rhino, Deer) for Perk_Hunter_1.</summary>
        public bool GameAnimal { get; set; }
        public bool Boss { get; set; }
        /// <summary>The CP Immortal property: can be knocked out, never killed.</summary>
        public bool Immortal { get; set; }
        /// <summary>NPC type "friend" (protected like the player on low levels).</summary>
        public bool FriendType { get; set; }
        public bool Summoned { get; set; }
        /// <summary>NPC of the mage class (may block magic with a staff).</summary>
        public bool Mage { get; set; }

        // --- levels and attributes (combat values)
        /// <summary>NPCs: the Level property with the difficulty offset (weapon skill level). The player: the hero level.</summary>
        public int Level { get; set; }
        public int Strength { get; set; }
        public int Dexterity { get; set; }
        public int Intelligence { get; set; }
        /// <summary>Protection per damage type in percent (after the player/NPC formula and cap, before armour perks).</summary>
        public DamageTypeValues Protection { get; set; } = new DamageTypeValues();
        public ArmorKind Armor { get; set; }
        /// <summary>The worn armour item's own PROT_* modifiers (AB armour perks add a share of them).</summary>
        public DamageTypeValues ArmorItemProtection { get; set; } = new DamageTypeValues();
        public ISet<string> Perks { get; set; } = NoPerks;

        // --- equipment and stance
        public WeaponClass Weapon { get; set; } = WeaponClass.Fist;
        public bool Shield { get; set; }
        /// <summary>
        /// G3 weapon config "melee" (FUN_10003240 == 1: a melee weapon or the fists drawn). Only such a humanoid is knocked
        /// into sitting (AssessHit); with a bow, a spell or nothing drawn a heavy hit is a Stumble.
        /// </summary>
        public bool MeleeConfig { get; set; } = true;
        public bool DualWield { get; set; }
        /// <summary>Dual-wield power attacks have two poses; only the second one doubles damage.</summary>
        public bool SecondPose { get; set; }
        public CombatAction Action { get; set; }
        /// <summary>In the parade animation with the right pose for the weapon set.</summary>
        public bool InParade { get; set; }
        /// <summary>Holding a staff in the cast stance (magic block).</summary>
        public bool CastStance { get; set; }

        // --- current state
        public int HitPoints { get; set; }
        public int Stamina { get; set; }
        public int Mana { get; set; }
        public VitalState State { get; set; }
        /// <summary>Lying or sitting after a knock-down (SitKnockDown / LieKnockDown animation states).</summary>
        public bool KnockedDown { get; set; }
        public bool Talking { get; set; }
        public bool Swimming { get; set; }
        public bool Frozen { get; set; }
        public bool Ghost { get; set; }
        public bool GodMode { get; set; }
        /// <summary>The player has a GUI page open.</summary>
        public bool InMenu { get; set; }
        /// <summary>In routine mode (not reacting or fighting): sneak attacks of the player apply.</summary>
        public bool InRoutine { get; set; }

        public bool Has(string perk) => perk != null && Perks.Contains(perk);

        public bool IsOrc => string.Equals(Species, "Orc", StringComparison.Ordinal);

        /// <summary>Shield in the left hand and a one-handed weapon in the right.</summary>
        public bool ShieldAndOneHanded => Shield && Weapon == WeaponClass.OneHanded;
    }
}
