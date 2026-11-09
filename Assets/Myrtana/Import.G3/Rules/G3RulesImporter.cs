using System;
using System.Collections.Generic;
using G3.Formats.Story;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Story;

namespace Myrtana.Import.G3
{
    /// <summary>
    /// The Gothic 3 (Community Patch 1.75) rule numbers as content: the attributes and the progression rules. G3 keeps
    /// these in its DLLs, not in data files; the values were re-derived from the binaries and are documented in
    /// Docs/gameplay-data/progression.json and character.json, which this class follows one to one.
    /// </summary>
    public static class G3RulesImporter
    {
        /// <summary>Attribute tags as G3 items, teaching and quests write them; names are the "ATTRIB_" strings.</summary>
        static readonly string[] Vitals = { "HP", "MP", "SP" };
        static readonly string[] Attributes =
        {
            "STR", "DEX", "INT", "SMT", "THF", "ALC",
            "PROT_BLADE", "PROT_IMPACT", "PROT_MISSILE", "PROT_FIRE", "PROT_ICE", "PROT_LIGHTNING",
        };

        /// <summary>Names: "ATTRIB_" + tag, maxima "ATTRIB_" + tag + "Max", item requirement labels "HUD_Req_" + tag.</summary>
        public static List<AttributeDef> AttributeDefinitions(StringsImporter strings)
        {
            var defs = new List<AttributeDef>();
            int order = 0;
            foreach (var id in Vitals)
                defs.Add(new AttributeDef(id, new TextId("ATTRIB_" + id), new TextId("ATTRIB_" + id + "Max"), AttributeKind.Vital, order++,
                                          strings.Resolve("HUD_Req_" + id)));
            foreach (var id in Attributes)
                defs.Add(new AttributeDef(id, new TextId("ATTRIB_" + id), default, AttributeKind.Attribute, order++, strings.Resolve("HUD_Req_" + id)));
            return defs;
        }

        public static ProgressionRules Progression() => new ProgressionRules(
            ProgressionRules.DefinitionId,
            xpCurveFactor: 250,
            maxLevelsPerAward: 2,
            learningPointsPerLevel: 10,
            learningPerk: "Perk_Learn",
            learningPerkBonus: 1,
            killXp: new KillXpRules(perLevel: 10, minimum: 50, ambientCreature: new Balanced<int>(25, 10),
                                    livestock: new Balanced<int>(0, 20), livestockSpecies: new[] { "Pig", "Cow" },
                                    modifierRounding: 5),
            heroStart: new[]
            {
                new StartingAttribute("HP", 200), new StartingAttribute("MP", 100), new StartingAttribute("SP", new Balanced<int>(100, 150)),
                new StartingAttribute("STR", 100), new StartingAttribute("DEX", 100), new StartingAttribute("INT", 0),
                new StartingAttribute("SMT", 10), new StartingAttribute("THF", 10), new StartingAttribute("ALC", 10),
                new StartingAttribute("PROT_BLADE", 0), new StartingAttribute("PROT_IMPACT", 0), new StartingAttribute("PROT_MISSILE", 0),
                new StartingAttribute("PROT_FIRE", 0), new StartingAttribute("PROT_ICE", 0), new StartingAttribute("PROT_LIGHTNING", 0),
            },
            attributeBoosts: new Dictionary<string, Balanced<IReadOnlyList<AttributeGain>>>
            {
                ["STR"] = Boost("STR", "SMT", "HP", 25, 50),
                ["DEX"] = Boost("DEX", "THF", "SP", 25, 50),
                ["INT"] = Boost("INT", "ALC", "MP", 25, 50),
                ["XAR"] = new Balanced<IReadOnlyList<AttributeGain>>(new AttributeGain[0], new[]
                {
                    Add("STR", 5), Add("DEX", 5), Add("INT", 5), AddMax("HP", 20), AddMax("SP", 20), AddMax("MP", 20),
                }),
            });

        /// <summary>
        /// The factions (G3 political alignments with a "GO_PoliticalFame_PAL_" name) and every enclave that quests give
        /// fame with (named by "GO_Enclave_"), so screens can show fame without building string keys at runtime.
        /// </summary>
        public static List<FameGroupDef> FameGroups(StringsImporter strings, IEnumerable<QuestDef> quests, ImportReport report)
        {
            var groups = new List<FameGroupDef>();
            foreach (var faction in Enum.GetNames(typeof(PoliticalAlignment)))
            {
                var name = strings.Resolve("GO_PoliticalFame_PAL_" + faction);
                if (!name.IsEmpty) groups.Add(new FameGroupDef(faction, FameKind.Political, name));
            }
            var enclaves = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var q in quests)
            foreach (var change in q.Rewards.Fame)
                if (change.Kind == FameKind.Enclave) enclaves.Add(change.Group);
            foreach (var enclave in enclaves)
            {
                var name = strings.Resolve("GO_Enclave_" + enclave);
                if (name.IsEmpty) report.Count("enclave without a GO_Enclave_ name", enclave);
                groups.Add(new FameGroupDef(enclave, FameKind.Enclave, name));
            }
            return groups;
        }

        /// <summary>items.json: gold is It_Gold; forged items (quality bit 128) need 10 less of each attribute.</summary>
        public static ItemRules ItemRules() => new ItemRules(Myrtana.Rules.ItemRules.DefinitionId, InfoImporter.GoldItem, 128, -10);

        /// <summary>trading.json: price factors, specialists, barter, AB sell penalty, refresh interval, armour limits.</summary>
        public static TradeRules TradeRules() => new TradeRules(
            Myrtana.Rules.TradeRules.DefinitionId, buyFactor: 1.0, sellFactor: 0.3, specialistSellFactor: 0.6,
            specialties: new[]
            {
                new TradeSpecialty("Valuable", new[] { "Valuable" }),
                new TradeSpecialty("Trophy", new[] { "TrophySkin", "TrophyFur" }),
            },
            barterPerk: "Perk_Barter", barterSellFactor: 1.1, barterBuyFactor: 0.9, friendlyAdjustment: 0.1,
            sellPenalty: new Balanced<double>(0, 0.05), minimumPrice: 1, refreshHours: 24,
            armorSales: new Balanced<IReadOnlyList<ArmorSaleLimit>>(
                new[] { new ArmorSaleLimit(10000, 0), new ArmorSaleLimit(65000, 25), new ArmorSaleLimit(0, 50) },
                new[]
                {
                    new ArmorSaleLimit(15000, 0), new ArmorSaleLimit(22000, 20), new ArmorSaleLimit(39000, 25),
                    new ArmorSaleLimit(79000, 40), new ArmorSaleLimit(120000, 50), new ArmorSaleLimit(0, 60),
                }));

        /// <summary>progression.json "teaching": 4 points per LP for HP/MP/SP; AB doubles STR/DEX/INT lessons from 250 on.</summary>
        public static TeachingRules TeachingRules() => new TeachingRules(
            Myrtana.Rules.TeachingRules.DefinitionId,
            new Dictionary<string, int> { ["HP"] = 4, ["MP"] = 4, ["SP"] = 4 },
            new Balanced<TeachingSurcharge>(null, new TeachingSurcharge(new[] { "STR", "DEX", "INT" }, 250, 2, 2)));

        static Balanced<IReadOnlyList<AttributeGain>> Boost(string attribute, string craft, string vital, int amount, int vitalAmount) =>
            new Balanced<IReadOnlyList<AttributeGain>>(new[] { Add(attribute, amount), Add(craft, amount), AddMax(vital, vitalAmount) });

        /// <summary>The combat tables (Docs/gameplay-data/damage.json, combat_moves.json, status_tick.json "downState").</summary>
        public static CombatRules Combat()
        {
            var light = new Balanced<int>(10, 12);
            var costs = new[]
            {
                new ActionCost(CombatAction.Attack, light), new ActionCost(CombatAction.QuickAttack, light),
                new ActionCost(CombatAction.QuickAttackLeft, light), new ActionCost(CombatAction.QuickAttackRight, light),
                new ActionCost(CombatAction.SimpleWhirl, light), new ActionCost(CombatAction.PierceAttack, light),
                new ActionCost(CombatAction.PowerAttack, 20), new ActionCost(CombatAction.SprintAttack, 20),
                new ActionCost(CombatAction.HackAttack, 40), new ActionCost(CombatAction.WhirlAttack, 40),
                new ActionCost(CombatAction.Jump, 10), new ActionCost(CombatAction.JumpBack, new Balanced<int>(10, 0)),
            };
            var species = new[]
            {
                new SpeciesDamage("Skeleton", DamageType.Impact, 2), new SpeciesDamage("Golem", DamageType.Impact, 2),
                new SpeciesDamage("Golem", DamageType.Missile, 0), new SpeciesDamage("FireGolem", DamageType.Missile, 0),
                new SpeciesDamage("Skeleton", DamageType.Missile, 0, unlessFireProjectile: true),
                new SpeciesDamage("IceGolem", DamageType.Missile, 0, unlessFireProjectile: true),
                new SpeciesDamage("IceGolem", DamageType.Fire, 2), new SpeciesDamage("FireGolem", DamageType.Fire, 0),
                new SpeciesDamage("Dragon", DamageType.Fire, 0), new SpeciesDamage("FireVaran", DamageType.Fire, 0),
                new SpeciesDamage("FireGolem", DamageType.Ice, 2), new SpeciesDamage("IceGolem", DamageType.Ice, 0),
                new SpeciesDamage("Golem", DamageType.Lightning, 2),
            };
            var elementalImmune = new[] { "Golem", "Demon", "Troll", "FireGolem", "IceGolem", "Dragon" };
            return new CombatRules(CombatRules.DefinitionId, costs, species,
                                   new DownStateRules(playerPartySeconds: 5, arenaLoserSeconds: 20, otherSeconds: 30,
                                                      regenPerSecond: 10, arenaRegenPerSecond: 15),
                                   minHitDurationGameSeconds: 6, unkillableDamageCap: 30,
                                   burnImmuneSpecies: elementalImmune, freezeImmuneSpecies: elementalImmune);
        }

        static AttributeGain Add(string attribute, int amount) => new AttributeGain(attribute, ModifierOp.AddToVal, amount);
        static AttributeGain AddMax(string vital, int amount) => new AttributeGain(vital, ModifierOp.AddToMax, amount);
    }
}
