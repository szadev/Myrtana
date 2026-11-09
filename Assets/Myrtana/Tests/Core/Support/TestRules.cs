using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;

namespace Myrtana.Tests
{
    /// <summary>Rule definitions with the G3 CP 1.75 numbers (gameplay-data/progression.json), built in code for tests.</summary>
    public static class TestRules
    {
        public static ProgressionRules Progression() => new ProgressionRules(
            ProgressionRules.DefinitionId, xpCurveFactor: 250, maxLevelsPerAward: 2, learningPointsPerLevel: 10,
            learningPerk: "Perk_Learn", learningPerkBonus: 1,
            killXp: new KillXpRules(10, 50, new Balanced<int>(25, 10), new Balanced<int>(0, 20), new[] { "Pig", "Cow" }, 5),
            heroStart: new[]
            {
                new StartingAttribute("HP", 200), new StartingAttribute("MP", 100), new StartingAttribute("SP", new Balanced<int>(100, 150)),
                new StartingAttribute("STR", 100), new StartingAttribute("DEX", 100), new StartingAttribute("INT", 0),
                new StartingAttribute("SMT", 10), new StartingAttribute("THF", 10), new StartingAttribute("ALC", 10),
            },
            attributeBoosts: new Dictionary<string, Balanced<IReadOnlyList<AttributeGain>>>
            {
                ["STR"] = new Balanced<IReadOnlyList<AttributeGain>>(new[]
                {
                    new AttributeGain("STR", ModifierOp.AddToVal, 25), new AttributeGain("SMT", ModifierOp.AddToVal, 25),
                    new AttributeGain("HP", ModifierOp.AddToMax, 50),
                }),
                ["XAR"] = new Balanced<IReadOnlyList<AttributeGain>>(new AttributeGain[0], new[]
                {
                    new AttributeGain("STR", ModifierOp.AddToVal, 5), new AttributeGain("HP", ModifierOp.AddToMax, 20),
                }),
            });

        /// <summary>The G3 combat tables exactly as the importer writes them.</summary>
        public static CombatRules Combat() => Myrtana.Import.G3.G3RulesImporter.Combat();

        public static CombatAiRules CombatAi() => Myrtana.Import.G3.G3CombatAiImporter.CombatAi();

        public static ItemRules Items() => new ItemRules(ItemRules.DefinitionId, "It_Gold", forgedQuality: 128, forgedRequirementDelta: -10);

        public static TradeRules Trade() => new TradeRules(
            TradeRules.DefinitionId, buyFactor: 1.0, sellFactor: 0.3, specialistSellFactor: 0.6,
            specialties: new[] { new TradeSpecialty("Valuable", new[] { "Valuable" }), new TradeSpecialty("Trophy", new[] { "TrophySkin", "TrophyFur" }) },
            barterPerk: "Perk_Barter", barterSellFactor: 1.1, barterBuyFactor: 0.9, friendlyAdjustment: 0.1,
            sellPenalty: new Balanced<double>(0, 0.05), minimumPrice: 1, refreshHours: 24,
            armorSales: new Balanced<IReadOnlyList<ArmorSaleLimit>>(
                new[] { new ArmorSaleLimit(10000, 0), new ArmorSaleLimit(65000, 25), new ArmorSaleLimit(0, 50) },
                new[] { new ArmorSaleLimit(15000, 0), new ArmorSaleLimit(22000, 20), new ArmorSaleLimit(39000, 25),
                        new ArmorSaleLimit(79000, 40), new ArmorSaleLimit(120000, 50), new ArmorSaleLimit(0, 60) }));

        public static TeachingRules Teaching() => new TeachingRules(
            TeachingRules.DefinitionId, new Dictionary<string, int> { ["HP"] = 4, ["MP"] = 4, ["SP"] = 4 },
            new Balanced<TeachingSurcharge>(null, new TeachingSurcharge(new[] { "STR", "DEX", "INT" }, 250, 2, 2)));

        public static IDefinition[] RuleDefinitions() => new IDefinition[] { Items(), Trade(), Teaching() };

        public static AttributeDef[] Attributes() => new[]
        {
            Vital("HP"), Vital("MP"), Vital("SP"), Plain("STR"), Plain("DEX"), Plain("INT"), Plain("SMT"), Plain("THF"), Plain("ALC"),
        };

        static AttributeDef Vital(string id) => new AttributeDef(id, new Myrtana.Core.TextId("ATTRIB_" + id), new Myrtana.Core.TextId("ATTRIB_" + id + "Max"), AttributeKind.Vital, 0);
        static AttributeDef Plain(string id) => new AttributeDef(id, new Myrtana.Core.TextId("ATTRIB_" + id), default, AttributeKind.Attribute, 0);
    }
}
