using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// The combat tables of the content (G3: gameplay-data/damage.json, combat_moves.json, status_tick.json). Formula
    /// shapes and small structural constants live in the rule classes; the numbers a content pack may want to tune are here.
    /// </summary>
    public sealed class CombatRules : IDefinition
    {
        public const string DefinitionId = "combat";

        readonly Dictionary<CombatAction, Balanced<int>> _costs;

        [JsonConstructor]
        public CombatRules(string id, IReadOnlyList<ActionCost> staminaCosts, IReadOnlyList<SpeciesDamage> speciesDamage,
                           DownStateRules downState, int minHitDurationGameSeconds, int unkillableDamageCap,
                           IReadOnlyList<string> burnImmuneSpecies, IReadOnlyList<string> freezeImmuneSpecies)
        {
            Id = id;
            StaminaCosts = staminaCosts ?? Array.Empty<ActionCost>();
            SpeciesDamage = speciesDamage ?? Array.Empty<SpeciesDamage>();
            DownState = downState ?? new DownStateRules(30, 30, 30, 10, 10);
            MinHitDurationGameSeconds = minHitDurationGameSeconds;
            UnkillableDamageCap = unkillableDamageCap;
            BurnImmuneSpecies = burnImmuneSpecies ?? Array.Empty<string>();
            FreezeImmuneSpecies = freezeImmuneSpecies ?? Array.Empty<string>();
            _costs = StaminaCosts.ToDictionary(c => c.Action, c => c.Cost);
        }

        public string Id { get; }
        /// <summary>Stamina paid when a move starts (shared by the player and NPCs).</summary>
        public IReadOnlyList<ActionCost> StaminaCosts { get; }
        /// <summary>Species × damage type multipliers (0 = immune, 2 = double base damage).</summary>
        public IReadOnlyList<SpeciesDamage> SpeciesDamage { get; }
        public DownStateRules DownState { get; }
        /// <summary>CP MinHitDuration: game seconds between an NPC's hit and its next attack (AB or extended AI).</summary>
        public int MinHitDurationGameSeconds { get; }
        /// <summary>An NPC that cannot be killed takes at most this much per hit, and nothing if the hit would be lethal.</summary>
        public int UnkillableDamageCap { get; }
        public IReadOnlyList<string> BurnImmuneSpecies { get; }
        public IReadOnlyList<string> FreezeImmuneSpecies { get; }

        string IDefinition.Key => Id;

        public int StaminaCost(CombatAction action, RuleOptions options) =>
            _costs.TryGetValue(action, out var cost) ? cost.For(options) : 0;

        /// <summary>The species multiplier for a damage type: 1 when the table has no entry.</summary>
        public int SpeciesMultiplier(string species, DamageType type, bool fireOrExplosiveProjectile)
        {
            foreach (var entry in SpeciesDamage)
            {
                if (entry.DamageType != type || !string.Equals(entry.Species, species, StringComparison.Ordinal)) continue;
                if (entry.Multiplier == 0 && entry.UnlessFireProjectile && fireOrExplosiveProjectile) return 1;
                return entry.Multiplier;
            }
            return 1;
        }

        /// <summary>Rules without content tables: nothing costs stamina, no species is special.</summary>
        public static CombatRules Neutral() =>
            new CombatRules(DefinitionId, null, null, null, 0, int.MaxValue, null, null);
    }

    public sealed class ActionCost
    {
        [JsonConstructor]
        public ActionCost(CombatAction action, Balanced<int> cost)
        {
            Action = action;
            Cost = cost;
        }

        public CombatAction Action { get; }
        public Balanced<int> Cost { get; }
    }

    public sealed class SpeciesDamage
    {
        [JsonConstructor]
        public SpeciesDamage(string species, DamageType damageType, int multiplier, bool unlessFireProjectile = false)
        {
            Species = species;
            DamageType = damageType;
            Multiplier = multiplier;
            UnlessFireProjectile = unlessFireProjectile;
        }

        public string Species { get; }
        public DamageType DamageType { get; }
        /// <summary>0 = immune, 2 = double base damage.</summary>
        public int Multiplier { get; }
        /// <summary>The immunity does not hold against projectiles named "Fire…" or "Explosive…".</summary>
        public bool UnlessFireProjectile { get; }
    }

    /// <summary>The knocked-out state (ZS_Unconscious): how long it lasts and what it restores per real second.</summary>
    public sealed class DownStateRules
    {
        [JsonConstructor]
        public DownStateRules(float playerPartySeconds, float arenaLoserSeconds, float otherSeconds, int regenPerSecond,
                              int arenaRegenPerSecond)
        {
            PlayerPartySeconds = playerPartySeconds;
            ArenaLoserSeconds = arenaLoserSeconds;
            OtherSeconds = otherSeconds;
            RegenPerSecond = regenPerSecond;
            ArenaRegenPerSecond = arenaRegenPerSecond;
        }

        /// <summary>The player and the player's companions.</summary>
        public float PlayerPartySeconds { get; }
        public float ArenaLoserSeconds { get; }
        public float OtherSeconds { get; }
        /// <summary>Hit points and stamina regained per real second while down.</summary>
        public int RegenPerSecond { get; }
        public int ArenaRegenPerSecond { get; }
    }
}
