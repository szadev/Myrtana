using Myrtana.Rules;

namespace Myrtana.Import.G3
{
    /// <summary>
    /// The magic tables of Gothic 3 (Community Patch 1.75) as content. G3 keeps them in Script_Game.dll; the values were
    /// re-derived from the binaries and are documented in Docs/gameplay-data/magic.json and status_tick.json, which this
    /// class follows one to one. The spells themselves come from the templates (<see cref="SpellImporter"/>).
    /// </summary>
    public static class G3MagicImporter
    {
        public static MagicRules Rules() => new MagicRules(
            MagicRules.DefinitionId,
            powerCastHoldSeconds: 0.7f,
            alwaysPowerCastSpecies: new[] { "IceGolem" },
            balancedNeverPowerCastSpecies: new[] { "Goblin" },
            flameSwordSpell: "Spl_Flamesword",
            castSpeeds: new CastSpeedRules(summon: 1.5f, flameSword: 0.7f, fastCaster: "Xardas", fastCasterSpeed: 1.6f),
            defaultFocusRange: 3000,
            focusRanges: new[]
            {
                R("Spl_Telekinesis", 1000), R("Spl_OpenLocks", 1000), R("Spl_WordOfControl", 1000), R("Spl_HealOther", 1000),
                R("Spl_Sleep", 1000), R("Spl_Charm", 1000), R("Spl_AnimalForm", 2000), R("Spl_TameAnimal", 2000), R("Spl_Fear", 2000),
                R("Spl_Bloodlust", 2000), R("Spl_Disease", 2000), R("Spl_Hypnosis", 2000), R("Spl_Poison", 2000),
            },
            burning: new BurnRules(seconds: 10, hitPointsPerSecond: 5),
            summons: new SummonRules(leashDistance: 4000, npcSummonReach: 2000, spells: new[]
            {
                new SummonSpec("MagicSummonDemon", new[] { "Demon" }, null),
                new SummonSpec("MagicSummonSkeleton", new[] { "Skeleton_Sword" }, null),
                new SummonSpec("MagicSummonGolem", new[] { "Golem" }, null),
                new SummonSpec("MagicSummonGoblin", new[] { "Goblin" }, null),
                new SummonSpec("MagicSummonCompanion", new[] { "*" }, null),
                new SummonSpec("MagicSummonMonster", new[] { "Shadowbeast" }, null, onlyCaster: "Runak"),
                new SummonSpec("MagicSummonArmyOfDarkness", new[] { "Skeleton", "SlaveZombie" }, Counts(3, 4, 5)),
                new SummonSpec("MagicSummonArmyOfCuteness", new[] { "Rabbit" }, Counts(6, 7, 8)),
            }),
            areaSpells: new[]
            {
                Wave("Heatwave", DamageType.Fire, 1000, 400, 0),
                Wave("Frostwave", DamageType.Ice, 1000, 200, 0),
                Wave("IceBomb", DamageType.Missile, 1000, 600, 0),
                Wave("Meteor", DamageType.Fire, 500, 1000, 0.5f),
                Wave("Lightning", DamageType.Lightning, 500, 1000, 0.5f),
                Rain("Firerain", DamageType.Fire),
                Rain("Hailstorm", DamageType.Lightning),
            });

        static SpellRange R(string spell, int range) => new SpellRange(spell, range);

        /// <summary>Army of Darkness and Army of Cuteness: count at INT below 250 / from 250 / from 300.</summary>
        static CountByIntelligence[] Counts(int low, int mid, int high) =>
            new[] { new CountByIntelligence(0, low), new CountByIntelligence(250, mid), new CountByIntelligence(300, high) };

        static AreaSpellDef Wave(string name, DamageType type, int radius, int near, float delay) =>
            new AreaSpellDef(name, AreaSpellMode.Wave, type, radius, near, 1, 0, 0, 0, 0, delay);

        /// <summary>Rains: a random target within 1000 every 0.4 s, 50 + random(50) damage, hit chance 33 % + min(5 % · n, 70 %).</summary>
        static AreaSpellDef Rain(string name, DamageType type) =>
            new AreaSpellDef(name, AreaSpellMode.Rain, type, 1000, 0, 0, 50, 50, 33, 0.4f, 0);
    }
}
