using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// The magic tables of the content (content kind "magicRules"; G3 values from <c>G3MagicImporter.Rules()</c>, after
    /// Docs/gameplay-data/magic.json and status_tick.json): power casting, cast speeds, focus ranges, burning, summons and
    /// area spells. Docs/combat.md §12.
    /// </summary>
    public sealed class MagicRules : IDefinition
    {
        public const string DefinitionId = "magic";

        [JsonConstructor]
        public MagicRules(string id, float powerCastHoldSeconds, IReadOnlyList<string> alwaysPowerCastSpecies,
                          IReadOnlyList<string> balancedNeverPowerCastSpecies, string flameSwordSpell, CastSpeedRules castSpeeds,
                          int defaultFocusRange, IReadOnlyList<SpellRange> focusRanges, BurnRules burning, SummonRules summons,
                          IReadOnlyList<AreaSpellDef> areaSpells)
        {
            Id = id ?? DefinitionId;
            PowerCastHoldSeconds = powerCastHoldSeconds;
            AlwaysPowerCastSpecies = alwaysPowerCastSpecies ?? Array.Empty<string>();
            BalancedNeverPowerCastSpecies = balancedNeverPowerCastSpecies ?? Array.Empty<string>();
            FlameSwordSpell = flameSwordSpell;
            CastSpeeds = castSpeeds ?? new CastSpeedRules(1, 1, null, 1);
            DefaultFocusRange = defaultFocusRange;
            FocusRanges = focusRanges ?? Array.Empty<SpellRange>();
            Burning = burning ?? new BurnRules(10, 5);
            Summons = summons ?? new SummonRules(4000, 2000, null);
            AreaSpells = areaSpells ?? Array.Empty<AreaSpellDef>();
        }

        public string Id { get; }
        /// <summary>A power-cast spell held this long (real s) is powered up; released earlier it is a normal cast.</summary>
        public float PowerCastHoldSeconds { get; }
        /// <summary>Casters of these species always power-cast, whatever their mana.</summary>
        public IReadOnlyList<string> AlwaysPowerCastSpecies { get; }
        /// <summary>With Alternative Balancing, casters of these species never power-cast.</summary>
        public IReadOnlyList<string> BalancedNeverPowerCastSpecies { get; }
        /// <summary>The spell cast with the FlameSword action.</summary>
        public string FlameSwordSpell { get; }
        public CastSpeedRules CastSpeeds { get; }
        /// <summary>How far a focus spell reaches (cm) when <see cref="FocusRanges"/> has no entry.</summary>
        public int DefaultFocusRange { get; }
        public IReadOnlyList<SpellRange> FocusRanges { get; }
        public BurnRules Burning { get; }
        public SummonRules Summons { get; }
        public IReadOnlyList<AreaSpellDef> AreaSpells { get; }

        string IDefinition.Key => Id;

        /// <summary>The focus range of a spell (cm).</summary>
        public int FocusRange(string spell)
        {
            foreach (var r in FocusRanges)
                if (string.Equals(r.Spell, spell, StringComparison.Ordinal)) return r.Range;
            return DefaultFocusRange;
        }

        public AreaSpellDef AreaSpell(string name) => AreaSpells.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));

        /// <summary>
        /// The area spell a spell's object runs: its Spawn template without "Smn_" names the parameter block
        /// (Smn_HeatWave → Heatwave, Smn_Firerain → Firerain, Smn_Lightning → Lightning …); null for other spells.
        /// </summary>
        public AreaSpellDef AreaSpellFor(SpellDef spell)
        {
            const string prefix = "Smn_";
            var spawn = spell?.Spawn;
            if (spawn == null || !spawn.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            string name = spawn.Substring(prefix.Length);
            return AreaSpells.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Rules for content without magic tables: no power-cast delay, no burning, summons stay.</summary>
        public static MagicRules Neutral() =>
            new MagicRules(DefinitionId, 0.7f, null, null, null, null, 3000, null, new BurnRules(0, 0), new SummonRules(0, 0, null), null);
    }

    /// <summary>Playback speed of the cast motions (G3 cast animation speed helper).</summary>
    public sealed class CastSpeedRules
    {
        [JsonConstructor]
        public CastSpeedRules(float summon, float flameSword, string fastCaster, float fastCasterSpeed)
        {
            Summon = summon;
            FlameSword = flameSword;
            FastCaster = fastCaster;
            FastCasterSpeed = fastCasterSpeed;
        }

        public float Summon { get; }
        public float FlameSword { get; }
        /// <summary>With Alternative Balancing this caster (Xardas) casts and power-casts faster.</summary>
        public string FastCaster { get; }
        public float FastCasterSpeed { get; }
    }

    /// <summary>A spell's focus range (cm).</summary>
    public sealed class SpellRange
    {
        [JsonConstructor]
        public SpellRange(string spell, int range)
        {
            Spell = spell;
            Range = range;
        }

        public string Spell { get; }
        public int Range { get; }
    }

    /// <summary>Burning (status_tick.json): how long it lasts and what it takes per real second.</summary>
    public sealed class BurnRules
    {
        [JsonConstructor]
        public BurnRules(float seconds, int hitPointsPerSecond)
        {
            Seconds = seconds;
            HitPointsPerSecond = hitPointsPerSecond;
        }

        public float Seconds { get; }
        public int HitPointsPerSecond { get; }
    }

    /// <summary>Summoned creatures: how far they may stray from the player, and what each summon spell calls.</summary>
    public sealed class SummonRules
    {
        [JsonConstructor]
        public SummonRules(int leashDistance, int npcSummonReach, IReadOnlyList<SummonSpec> spells)
        {
            LeashDistance = leashDistance;
            NpcSummonReach = npcSummonReach;
            Spells = spells ?? Array.Empty<SummonSpec>();
        }

        /// <summary>A creature summoned by the player dies when it is farther than this from him (cm; 0 = never).</summary>
        public int LeashDistance { get; }
        /// <summary>An NPC's summons attack the player within this distance (cm), else they vanish at once.</summary>
        public int NpcSummonReach { get; }
        public IReadOnlyList<SummonSpec> Spells { get; }

        public SummonSpec For(string effect) => Spells.FirstOrDefault(s => string.Equals(s.Effect, effect, StringComparison.Ordinal));
    }

    /// <summary>What one summon effect calls: creature templates, how many of each by the caster's INT, and who may cast it.</summary>
    public sealed class SummonSpec
    {
        [JsonConstructor]
        public SummonSpec(string effect, IReadOnlyList<string> templates, IReadOnlyList<CountByIntelligence> counts, string onlyCaster = null)
        {
            Effect = effect;
            Templates = templates ?? Array.Empty<string>();
            Counts = counts ?? Array.Empty<CountByIntelligence>();
            OnlyCaster = onlyCaster;
        }

        /// <summary>G3 FuncOnTargetHit ("MagicSummonSkeleton"); "*" = the spell's own Spawn template.</summary>
        public string Effect { get; }
        /// <summary>Creature templates ("*" = the spell's Spawn template).</summary>
        public IReadOnlyList<string> Templates { get; }
        /// <summary>Creatures per template by INT (none = one each).</summary>
        public IReadOnlyList<CountByIntelligence> Counts { get; }
        /// <summary>Only an NPC of this name may cast it (Runak's Shadowbeast); null = anyone.</summary>
        public string OnlyCaster { get; }

        public int CountFor(int intelligence)
        {
            int count = 1;
            foreach (var c in Counts.OrderBy(c => c.MinIntelligence))
                if (intelligence >= c.MinIntelligence) count = c.Count;
            return count;
        }
    }

    public sealed class CountByIntelligence
    {
        [JsonConstructor]
        public CountByIntelligence(int minIntelligence, int count)
        {
            MinIntelligence = minIntelligence;
            Count = count;
        }

        public int MinIntelligence { get; }
        public int Count { get; }
    }

    public enum AreaSpellMode { Wave, Rain }

    /// <summary>
    /// An area spell (MagicAreaDamageTick): a wave hits every target once with damage falling linearly from the centre;
    /// a rain hits random targets every tick. Docs/gameplay-logic.md §4.2.
    /// </summary>
    public sealed class AreaSpellDef
    {
        [JsonConstructor]
        public AreaSpellDef(string name, AreaSpellMode mode, DamageType damageType, int radius, int damageNear, int damageFar,
                            int baseDamage, int randomDamage, int chancePercent, float tickSeconds, float startDelaySeconds)
        {
            Name = name;
            Mode = mode;
            DamageType = damageType;
            Radius = radius;
            DamageNear = damageNear;
            DamageFar = damageFar;
            BaseDamage = baseDamage;
            RandomDamage = randomDamage;
            ChancePercent = chancePercent;
            TickSeconds = tickSeconds;
            StartDelaySeconds = startDelaySeconds;
        }

        /// <summary>The effect name without "Magic" ("Heatwave", "Firerain").</summary>
        public string Name { get; }
        public AreaSpellMode Mode { get; }
        public DamageType DamageType { get; }
        /// <summary>Wave radius, or the rain's candidate radius (cm).</summary>
        public int Radius { get; }
        public int DamageNear { get; }
        public int DamageFar { get; }
        public int BaseDamage { get; }
        public int RandomDamage { get; }
        public int ChancePercent { get; }
        public float TickSeconds { get; }
        public float StartDelaySeconds { get; }

        /// <summary>Wave damage at <paramref name="distance"/> cm: round(near + d/radius · (far − near)).</summary>
        public int WaveDamage(float distance) =>
            (int)Math.Round(DamageNear + distance / Math.Max(1, Radius) * (DamageFar - DamageNear), MidpointRounding.AwayFromZero);

        /// <summary>Rain hit chance (%) with <paramref name="candidates"/> targets around: chance + min(5·n, 70).</summary>
        public int RainHitChance(int candidates) => ChancePercent + Math.Min(5 * candidates, 70);

        /// <summary>A rain hit lands when roll (0..99) ≤ chance; its damage is base + random(0..randomDamage−1).</summary>
        public bool RainHits(int roll, int candidates) => roll <= RainHitChance(candidates);
    }
}
