using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Myrtana.Rules
{
    /// <summary>The role of an NPC in its society (G3 gENPCType); decides boss/guard/friend rules.</summary>
    public enum NpcRole { Personal, Guard, Political, Friend }

    /// <summary>The fighting profession of an NPC (G3 gEClass): mages cast, paladins prefer magic, neither drinks potions.</summary>
    public enum NpcClass { None, Mage, Paladin, Warrior, Ranger }

    /// <summary>
    /// The combat facts of an NPC that come from content (G3: the NPC's Level, LevelMax, type, class, species flags and
    /// weaponry). Derived stats follow from these and the player's level (<see cref="CombatStats"/>).
    /// </summary>
    public sealed class NpcCombatDef
    {
        public static readonly IReadOnlyList<string> NoEquipment = Array.Empty<string>();

        [JsonConstructor]
        public NpcCombatDef(int level, int levelMax, string species, bool humanoid, bool boss = false, bool immortal = false,
                            bool friendType = false, bool mage = false, bool ambientCreature = false, bool evil = false,
                            bool animal = false, bool gameAnimal = false, NpcRole role = NpcRole.Personal,
                            NpcClass @class = NpcClass.None, bool hasEnclave = false, string faction = null,
                            Attitude? attitudeToPlayer = null, IReadOnlyList<string> equipment = null, string enclave = null)
        {
            Level = level;
            LevelMax = levelMax;
            Species = species ?? "Human";
            Humanoid = humanoid;
            Boss = boss;
            Immortal = immortal;
            FriendType = friendType;
            Mage = mage;
            AmbientCreature = ambientCreature;
            Evil = evil;
            Animal = animal;
            GameAnimal = gameAnimal;
            Role = role;
            Class = @class;
            HasEnclave = hasEnclave;
            Faction = faction;
            AttitudeToPlayer = attitudeToPlayer;
            Equipment = equipment ?? NoEquipment;
            Enclave = enclave;
        }

        /// <summary>Offset above the player's level.</summary>
        public int Level { get; }
        public int LevelMax { get; }
        public string Species { get; }
        /// <summary>G3 IsHumanoid: humans and orcs.</summary>
        public bool Humanoid { get; }
        /// <summary>A political NPC of an enclave (G3 IsBoss): a lethal hit kills it, never only knocks it out (IsDeadlyDamage).</summary>
        public bool Boss { get; }
        /// <summary>The CP "Immortal" property: cannot be killed, takes capped damage.</summary>
        public bool Immortal { get; }
        /// <summary>NPC type Friend: protected while the player is below level 3 on Easy/Medium.</summary>
        public bool FriendType { get; }
        public bool Mage { get; }
        public bool AmbientCreature { get; }
        public bool Evil { get; }
        public bool Animal { get; }
        public bool GameAnimal { get; }
        public NpcRole Role { get; }
        public NpcClass Class { get; }
        public bool HasEnclave { get; }
        /// <summary>The enclave (town, camp, clan: an <see cref="EnclaveDef"/> id) the NPC belongs to; null = none or unknown.</summary>
        public string Enclave { get; }
        /// <summary>A guard of an enclave (G3 IsGuard).</summary>
        [JsonIgnore] public bool Guard => HasEnclave && Role == NpcRole.Guard;
        [JsonIgnore] public bool Paladin => Class == NpcClass.Paladin;
        /// <summary>Political alignment ("Orc", "Rebel", "Nomad" …; null = none), for the attitude rules.</summary>
        public string Faction { get; }
        /// <summary>The attitude to the player stored with the NPC (null = none, decided by the rules).</summary>
        public Attitude? AttitudeToPlayer { get; }
        /// <summary>Item ids the NPC fights with: its weaponry (weapons, shield, ammunition) and any equipped armour.</summary>
        public IReadOnlyList<string> Equipment { get; }

        public NpcCombatDef WithEquipment(IReadOnlyList<string> equipment) =>
            new NpcCombatDef(Level, LevelMax, Species, Humanoid, Boss, Immortal, FriendType, Mage, AmbientCreature, Evil, Animal,
                             GameAnimal, Role, Class, HasEnclave, Faction, AttitudeToPlayer, equipment, Enclave);

        /// <summary>The same NPC with another class (G3 SetClass).</summary>
        public NpcCombatDef WithClass(NpcClass npcClass) =>
            npcClass == Class ? this
            : new NpcCombatDef(Level, LevelMax, Species, Humanoid, Boss, Immortal, FriendType, Mage, AmbientCreature, Evil, Animal,
                               GameAnimal, Role, npcClass, HasEnclave, Faction, AttitudeToPlayer, Equipment, Enclave);

        /// <summary>The same NPC as a Friend-type NPC outside any enclave (CP SetPAL Reb on Wenzel, <see cref="StoryAlignmentRules"/>).</summary>
        public NpcCombatDef AsFriendWithoutEnclave() =>
            FriendType && !HasEnclave && Enclave == null ? this
            : new NpcCombatDef(Level, LevelMax, Species, Humanoid, Boss, Immortal, true, Mage, AmbientCreature, Evil, Animal,
                               GameAnimal, Role, Class, false, Faction, AttitudeToPlayer, Equipment, null);

        /// <summary>The same NPC with the Immortal flag set or cleared (CP Humanize).</summary>
        public NpcCombatDef WithImmortal(bool immortal) =>
            immortal == Immortal ? this
            : new NpcCombatDef(Level, LevelMax, Species, Humanoid, Boss, immortal, FriendType, Mage, AmbientCreature, Evil, Animal,
                               GameAnimal, Role, Class, HasEnclave, Faction, AttitudeToPlayer, Equipment, Enclave);

        public VitalsProfile Profile(Difficulty difficulty, bool playerParty = false)
        {
            int levelMax = CombatStats.NpcLevel(LevelMax, difficulty);
            return new VitalsProfile(CombatStats.NpcHitPointsMax(levelMax, AmbientCreature), CombatStats.NpcStaminaMax(levelMax, Humanoid),
                                     CombatStats.NpcMana, Level, AmbientCreature, Species, playerParty);
        }
    }
}
