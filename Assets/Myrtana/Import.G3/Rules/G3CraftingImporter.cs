using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Import.G3
{
    /// <summary>
    /// G3 CP 1.75 station rules as data (Docs/interactive-objects.md §6). The numbers and names are G3's script behaviour
    /// (<c>PS_Interact</c> use-type cases, <c>OnPlayerFryStack</c>, <c>Bookstand.cpp</c>), not template data.
    /// </summary>
    public static class G3CraftingImporter
    {
        public static CraftingRules Rules() => new CraftingRules(
            CraftingRules.DefinitionId,
            new[]
            {
                Station("Anvil", StationPage.Synth, CraftKind.Smithing, "It_Hammer_Smith", null, false, "HL_Smithy"),
                Station("Cauldron", StationPage.Synth, CraftKind.Cooking, "It_Scoop", null, false, "HL_Cauldron"),
                Station("Alchemy", StationPage.Synth, CraftKind.Alchemy, null, null, false, "HL_AlchemyTable"),
                Station("GrindStone", StationPage.Sharpen, CraftKind.Smithing, null, "Perk_SharpenBlade", true, "HL_GrindStone"),
                Station("Campfire", StationPage.Fry, CraftKind.Cooking, null, null, false, "HL_FryMeat"),
                Station("Barbecue", StationPage.None, CraftKind.Cooking, null, null, false, null),
                Station("Bookstand", StationPage.Read, CraftKind.Smithing, null, null, false, null),
                Station("Stoneplate", StationPage.Read, CraftKind.Smithing, null, null, false, null),
            },
            forgedQuality: (int)WeaponQuality.Forged,
            sharpQuality: (int)WeaponQuality.Sharp,
            magicQualities: (int)(WeaponQuality.Burning | WeaponQuality.Frozen),
            bladeDamageTypes: new[] { "Blade" },
            fry: new[] { new FryRule("It_Meat_Raw", "It_Meat_Fried"), new FryRule("It_Meatbug_Raw", "It_Meatbug_Fried") },
            bookstands: new BookstandRules(
                new Balanced<int>(5, 3), 2, "INT", "BS_AncientWisdom",
                new[]
                {
                    new StandBook("BS_BookOfFlood", "BookOfFlood", 18),
                    new StandBook("BS_BookOfRhobar", "BookOfRhobar", 8),
                    new StandBook("BS_BookOfZuben", "BookOfZuben", 8),
                },
                new[] { "BS_BookOfFlood", "BS_BookOfRhobar", "BS_BookOfZuben" },
                new[] { new StandLesson("BS_Alchemy", "AlchemyBook", "ALC"), new StandLesson("BS_Smith", "SmithBook", "SMT") },
                new[]
                {
                    new StandStory("BS_Chronicles", "Dargoth_Chronicles", "NotReadyYet",
                                   new[] { "Chronicles_00", "Chronicles_01", "Chronicles_02", "Chronicles_03" },
                                   new[] { "PC_Hero_FindChumanas" }, new[] { "PC_Hero_FifthTemple" },
                                   new[] { "G3_Nordmar_01_Monastery_Chronicles_Story_01" }),
                },
                new[] { "NothingNew_00", "NothingNew_01", "NothingNew_02" }));

        static StationRule Station(string useType, StationPage page, CraftKind craft, string tool, string skill, bool blade, string title) =>
            new StationRule(useType, page, craft, tool, skill, blade, title == null ? default : new TextId(title));
    }
}
