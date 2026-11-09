using System;
using System.Collections.Generic;
using System.Linq;
using G3.Formats.Animation;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;
using G3Slot = G3.Formats.Items.EquipSlot;

namespace Myrtana.Import.G3
{
    /// <summary>
    /// The combat facts of an NPC from its world entity (gCNPC_PS Level, LevelMax, Type, Class, PoliticalAlignment,
    /// AttitudeToPlayer2, Enclave, the CP Immortal property) and its weaponry (G3 RefreshWeaponry: the first Weaponry
    /// treasure set of TreasureSet1..5, plus equipped inventory stacks). The species classes are the G3 script tests
    /// IsHumanoid, IsEvil, IsAnimal, IsGame and IsAmbientCreature.
    /// </summary>
    public static class NpcCombatImporter
    {
        static readonly HashSet<string> Humanoids = Set("Human", "Orc");
        static readonly HashSet<string> EvilSpecies = Set("Skeleton", "Zombie", "Golem", "Demon", "Gargoyle", "FireGolem", "IceGolem", "ScorpionKing");
        static readonly HashSet<string> Animals = Set("Minecrawler", "Scavenger", "Wolf", "Boar", "Sabertooth", "Shadowbeast", "Bison", "Rhino",
                                                      "Ripper", "Lurker", "Varan", "Snapper", "Alligator", "FireVaran", "Bloodfly", "Deer", "Pig",
                                                      "Cow", "SwampLurker");
        static readonly HashSet<string> GameAnimals = Set("Boar", "Bison", "Rhino", "Deer");
        static readonly HashSet<string> AmbientCreatures = Set("Lizard", "Fish", "Meatbug", "Vulture", "Rabbit", "Chicken", "Molerat", "Snake",
                                                               "Spider", "Turtle", "Bird", "Ray", "Scorpion", "Seagull", "Walrus", "Whale", "Eagle");
        /// <summary>gEAttitude 0..6: None, Friendly, Neutral, Angry, Hostile, Predator, Panic.</summary>
        static readonly Attitude?[] Attitudes = { null, Attitude.Friendly, Attitude.Neutral, Attitude.Angry, Attitude.Hostile, Attitude.Predator, Attitude.Panic };
        /// <summary>Inventory slots that hold fighting gear (not the body parts Head/Hair/Beard/Body).</summary>
        static readonly HashSet<G3Slot> GearSlots = new HashSet<G3Slot>
        {
            G3Slot.RightHand, G3Slot.LeftHand, G3Slot.EquipLeftHand, G3Slot.BackLeft, G3Slot.BackRight, G3Slot.Bow, G3Slot.Crossbow,
            G3Slot.Armor, G3Slot.Helmet, G3Slot.Ammo,
        };

        /// <summary>Level, flags and attitude facts of a spawn (weaponry is added by <see cref="AddWeaponry"/>).</summary>
        public static NpcCombatDef FromSpawn(NpcSpawn spawn, string enclave = null)
        {
            string species = G3RoutineTables.SpeciesName(spawn.Species) ?? "Human";
            var role = spawn.NpcType >= 0 && spawn.NpcType <= (int)NpcRole.Friend ? (NpcRole)spawn.NpcType : NpcRole.Personal;
            var npcClass = spawn.Class >= 0 && spawn.Class <= (int)NpcClass.Ranger ? (NpcClass)spawn.Class : NpcClass.None;
            var faction = (global::G3.Formats.Story.PoliticalAlignment)spawn.Political;
            return new NpcCombatDef(
                spawn.Level, spawn.LevelMax, species, Humanoids.Contains(species),
                boss: spawn.HasEnclave && role == NpcRole.Political,
                immortal: spawn.Immortal,
                friendType: role == NpcRole.Friend,
                mage: npcClass == NpcClass.Mage,
                ambientCreature: AmbientCreatures.Contains(species),
                evil: EvilSpecies.Contains(species),
                animal: Animals.Contains(species),
                gameAnimal: GameAnimals.Contains(species),
                role: role,
                @class: npcClass,
                hasEnclave: spawn.HasEnclave,
                faction: faction == global::G3.Formats.Story.PoliticalAlignment.None ? null : faction.ToString(),
                attitudeToPlayer: spawn.AttitudeToPlayer >= 0 && spawn.AttitudeToPlayer < Attitudes.Length ? Attitudes[spawn.AttitudeToPlayer] : null,
                enclave: enclave);
        }

        /// <summary>The NPCs with their weaponry item ids filled in.</summary>
        public static List<NpcDef> AddWeaponry(IReadOnlyList<NpcDef> npcs, IReadOnlyDictionary<ActorId, NpcSpawn> spawns,
                                               G3ItemContent items, ImportReport report)
        {
            var sets = items.TreasureSets.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
            var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in items.Items ?? new List<ItemDef>()) kinds[item.Id] = item.Kind;
            var result = new List<NpcDef>(npcs.Count);
            foreach (var npc in npcs)
            {
                if (npc.Combat == null || !spawns.TryGetValue(npc.Id, out var spawn) || spawn.Inventory == null)
                {
                    result.Add(npc);
                    continue;
                }
                var gear = new List<string>();
                var weaponry = spawn.Inventory.TreasureSets
                    .Select(n => !string.IsNullOrEmpty(n) && sets.TryGetValue(n, out var set) ? set : null)
                    .FirstOrDefault(s => s != null && s.Use == TreasureUse.Weaponry);
                if (weaponry != null) gear.AddRange(weaponry.Stacks.Select(s => s.Item));
                foreach (var slot in spawn.Inventory.Slots.Where(s => GearSlots.Contains(s.Slot) && s.Stack >= 0 && s.Stack < spawn.Inventory.Stacks.Count))
                {
                    string item = items.ItemOf(spawn.Inventory.Stacks[slot.Stack].Template);
                    if (item != null) gear.Add(item);
                    else report.Count("equipped NPC stack of an unknown item", npc.Id.Name);
                }
                if (gear.Count == 0) report.Count("NPCs without weaponry (fight with fists)", npc.Combat.Species);
                var equipment = gear.Distinct(StringComparer.Ordinal).ToList();
                // G3 EquipWeaponry: a one-handed stack of two (after MergeStacks) is split into BackRight and BackLeft — a dual
                // wielder (assassins, Zuben). The loadout reads the second listing as the left-hand weapon.
                string dual = weaponry == null ? null : DualWieldRules.DualWeaponOf(weaponry.Stacks.Select(st =>
                    (st.Item, kinds.TryGetValue(st.Item ?? "", out var kind) ? kind : null, st.Amount)));
                if (dual != null)
                {
                    equipment.Insert(equipment.IndexOf(dual) + 1, dual);
                    report.Count("NPC dual wielders (two one-handed weapons)", dual);
                }
                result.Add(gear.Count == 0 ? npc : npc.WithCombat(npc.Combat.WithEquipment(equipment)));
            }
            return result;
        }

        /// <summary>
        /// NPCs the hero can pickpocket: those whose inventory carries treasure sets of use Pickpocket (G3 PickPocket
        /// condition and GeneratePickpocketInventory).
        /// </summary>
        public static List<PickpocketDef> Pickpockets(IReadOnlyDictionary<ActorId, NpcSpawn> spawns, G3ItemContent items, ImportReport report)
        {
            var sets = items.TreasureSets.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
            var list = new List<PickpocketDef>();
            foreach (var kv in spawns.OrderBy(k => k.Key.Name, StringComparer.Ordinal))
            {
                if (kv.Value.Inventory == null) continue;
                var pick = kv.Value.Inventory.TreasureSets
                    .Where(n => !string.IsNullOrEmpty(n) && sets.TryGetValue(n, out var set) && set.Use == TreasureUse.Pickpocket)
                    .Select(n => sets[n].Id).Distinct(StringComparer.Ordinal).ToList();
                if (pick.Count == 0) continue;
                list.Add(new PickpocketDef(kv.Key, pick));
                report.Count("NPC with pickpocket loot", kv.Key.Name);
            }
            return list;
        }

        static HashSet<string> Set(params string[] names) => new HashSet<string>(names, StringComparer.Ordinal);
    }
}
