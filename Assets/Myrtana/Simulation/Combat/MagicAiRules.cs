using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Myrtana.Rules;

namespace Myrtana.Simulation
{
    /// <summary>
    /// How NPCs fight with magic (combat_ai.json moveSelection.magic, weaponChoice.mage and .spells, castAction; Docs/combat.md
    /// §12): when a mage or paladin takes the spell rather than the weapon, which spell it holds, how it keeps its distance
    /// and when it casts.
    /// </summary>
    public sealed class MagicAiRules
    {
        [JsonConstructor]
        public MagicAiRules(float mageMeleePlayerWithin, float mageSummonerPlayerWithin, float mageMeleeNpcWithin, float mageStayMeleeBelow,
                            string iceGolem, float iceGolemMeleeBelow, float iceGolemCastCooldownSeconds,
                            float tooClose, int tooCloseBackChance, float maxRange, int kiteChance, float kiteDistance, int waitAbove,
                            IReadOnlyList<string> balancedAlwaysCast, int powerCastChance, IReadOnlyList<string> alwaysPowerCast,
                            int healBelowPercent, int healChance, string healSpell, IReadOnlyList<SpeciesSpell> speciesSpells,
                            IReadOnlyList<SpeciesSpell> versusSpecies, IReadOnlyList<string> defaultSpells,
                            IReadOnlyList<SpeciesSpell> summonerBosses)
        {
            MageMeleePlayerWithin = mageMeleePlayerWithin;
            MageSummonerPlayerWithin = mageSummonerPlayerWithin;
            MageMeleeNpcWithin = mageMeleeNpcWithin;
            MageStayMeleeBelow = mageStayMeleeBelow;
            IceGolem = iceGolem;
            IceGolemMeleeBelow = iceGolemMeleeBelow;
            IceGolemCastCooldownSeconds = iceGolemCastCooldownSeconds;
            TooClose = tooClose;
            TooCloseBackChance = tooCloseBackChance;
            MaxRange = maxRange;
            KiteChance = kiteChance;
            KiteDistance = kiteDistance;
            WaitAbove = waitAbove;
            BalancedAlwaysCast = balancedAlwaysCast ?? Array.Empty<string>();
            PowerCastChance = powerCastChance;
            AlwaysPowerCast = alwaysPowerCast ?? Array.Empty<string>();
            HealBelowPercent = healBelowPercent;
            HealChance = healChance;
            HealSpell = healSpell;
            SpeciesSpells = speciesSpells ?? Array.Empty<SpeciesSpell>();
            VersusSpecies = versusSpecies ?? Array.Empty<SpeciesSpell>();
            DefaultSpells = defaultSpells ?? Array.Empty<string>();
            SummonerBosses = summonerBosses ?? Array.Empty<SpeciesSpell>();
        }

        // --- weapon choice of a mage against a humanoid target
        /// <summary>The player this close (cm): melee.</summary>
        public float MageMeleePlayerWithin { get; }
        /// <summary>The player this close: magic only for a summoner boss that can summon, else melee.</summary>
        public float MageSummonerPlayerWithin { get; }
        /// <summary>An NPC target this close and no room to back off: melee.</summary>
        public float MageMeleeNpcWithin { get; }
        /// <summary>Already in melee and closer than this: stay in melee.</summary>
        public float MageStayMeleeBelow { get; }

        // --- the ice golem (not a mage, but casts)
        public string IceGolem { get; }
        public float IceGolemMeleeBelow { get; }
        /// <summary>Real seconds between an ice golem's casts (600 game s).</summary>
        public float IceGolemCastCooldownSeconds { get; }

        // --- FAI_Magic (focus spells)
        /// <summary>Closer than this the caster backs off (with <see cref="TooCloseBackChance"/> %) or casts.</summary>
        public float TooClose { get; }
        public int TooCloseBackChance { get; }
        /// <summary>Beyond this the caster comes closer.</summary>
        public float MaxRange { get; }
        /// <summary>% of decisions that adjust the distance around <see cref="KiteDistance"/>.</summary>
        public int KiteChance { get; }
        public float KiteDistance { get; }
        /// <summary>Repeating the last spell: a roll (0..99) above this waits instead of casting.</summary>
        public int WaitAbove { get; }
        /// <summary>With AB these casters always cast (Xardas).</summary>
        public IReadOnlyList<string> BalancedAlwaysCast { get; }

        // --- ZS_Attack_Cast
        public int PowerCastChance { get; }
        public IReadOnlyList<string> AlwaysPowerCast { get; }

        // --- spell picker
        public int HealBelowPercent { get; }
        public int HealChance { get; }
        public string HealSpell { get; }
        /// <summary>Monster casters' own spells (goblin, fire golem, dragon: Fireball; ice golem: Icelance).</summary>
        public IReadOnlyList<SpeciesSpell> SpeciesSpells { get; }
        /// <summary>The counter-element against golems (vs IceGolem: Fireball, vs FireGolem: Icelance).</summary>
        public IReadOnlyList<SpeciesSpell> VersusSpecies { get; }
        /// <summary>Otherwise: keep a held one of these, else pick one at random.</summary>
        public IReadOnlyList<string> DefaultSpells { get; }
        /// <summary>Summoner bosses by name (AB or extended AI, fighting the player, no living summons).</summary>
        public IReadOnlyList<SpeciesSpell> SummonerBosses { get; }

        public static string Find(IReadOnlyList<SpeciesSpell> list, string key) =>
            key == null ? null : list.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.Ordinal))?.Spell;

        /// <summary>No magic: nobody casts.</summary>
        public static MagicAiRules None() =>
            new MagicAiRules(250, 400, 400, 470, null, 750, 50, 350, 80, 2000, 49, 1175, 80, null, 0, null, 0, 0, null, null, null, null, null);
    }

    /// <summary>A spell item for a key (a species or an NPC name).</summary>
    public sealed class SpeciesSpell
    {
        [JsonConstructor]
        public SpeciesSpell(string key, string spell)
        {
            Key = key;
            Spell = spell;
        }

        public string Key { get; }
        public string Spell { get; }
    }
}
