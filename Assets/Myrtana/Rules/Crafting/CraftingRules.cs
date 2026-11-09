using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>What using a station opens.</summary>
    public enum StationPage
    {
        /// <summary>The hero only plays his use motion (barbecue spit; stations the player cannot use).</summary>
        None,
        /// <summary>The recipe page of <see cref="StationRule.Craft"/> (anvil, cauldron, alchemy bench).</summary>
        Synth,
        /// <summary>The sharpen page: a blade weapon gets the sharp quality (grindstone).</summary>
        Sharpen,
        /// <summary>The fry page: raw meat becomes fried meat (campfire).</summary>
        Fry,
        /// <summary>A bookstand or stone plate: the hero reads aloud and may learn something (<see cref="BookstandRules"/>).</summary>
        Read,
    }

    /// <summary>One station use type: its page and what the hero needs before it opens.</summary>
    public sealed class StationRule
    {
        [JsonConstructor]
        public StationRule(string useType, StationPage page, CraftKind craft, string tool, string requiredSkill, bool needsBladeWeapon,
                           TextId title)
        {
            UseType = useType;
            Page = page;
            Craft = craft;
            Tool = tool;
            RequiredSkill = requiredSkill;
            NeedsBladeWeapon = needsBladeWeapon;
            Title = title;
        }

        /// <summary>G3 gEUseType name of the station ("Anvil", "Cauldron" …).</summary>
        public string UseType { get; }
        public StationPage Page { get; }
        /// <summary>The recipes a <see cref="StationPage.Synth"/> station lists.</summary>
        public CraftKind Craft { get; }
        /// <summary>An item the hero must carry (smith's hammer, scoop); null = none.</summary>
        public string Tool { get; }
        /// <summary>A skill the hero must know (sharpening); null = none.</summary>
        public string RequiredSkill { get; }
        /// <summary>The hero must carry a blade weapon.</summary>
        public bool NeedsBladeWeapon { get; }
        /// <summary>The page title.</summary>
        public TextId Title { get; }
    }

    /// <summary>A transformation of the fry page: one raw item into one cooked item.</summary>
    public sealed class FryRule
    {
        [JsonConstructor]
        public FryRule(string raw, string fried)
        {
            Raw = raw;
            Fried = fried;
        }

        public string Raw { get; }
        public string Fried { get; }
    }

    /// <summary>
    /// The stations' rules (Docs/interactive-objects.md §6): the page each use type opens and its preconditions, the quality bits
    /// crafting and sharpening set, the fry pairs and the bookstands.
    /// </summary>
    public sealed class CraftingRules : IDefinition
    {
        public const string DefinitionId = "crafting";

        [JsonConstructor]
        public CraftingRules(string id, IReadOnlyList<StationRule> stations, int forgedQuality, int sharpQuality, int magicQualities,
                             IReadOnlyList<string> bladeDamageTypes, IReadOnlyList<FryRule> fry, BookstandRules bookstands)
        {
            Id = id;
            Stations = stations ?? Array.Empty<StationRule>();
            ForgedQuality = forgedQuality;
            SharpQuality = sharpQuality;
            MagicQualities = magicQualities;
            BladeDamageTypes = bladeDamageTypes ?? Array.Empty<string>();
            Fry = fry ?? Array.Empty<FryRule>();
            Bookstands = bookstands ?? BookstandRules.None;
        }

        public string Id { get; }
        public IReadOnlyList<StationRule> Stations { get; }
        /// <summary>Added to smithed weapons.</summary>
        public int ForgedQuality { get; }
        /// <summary>Set by sharpening.</summary>
        public int SharpQuality { get; }
        /// <summary>A weapon with any of these bits cannot be sharpened (burning, frozen).</summary>
        public int MagicQualities { get; }
        /// <summary>Damage types of the weapons that can be sharpened.</summary>
        public IReadOnlyList<string> BladeDamageTypes { get; }
        public IReadOnlyList<FryRule> Fry { get; }
        public BookstandRules Bookstands { get; }

        string IDefinition.Key => Id;

        public static readonly CraftingRules None = new CraftingRules(DefinitionId, null, 0, 0, 0, null, null, null);

        public StationRule StationFor(string useType) => Stations.FirstOrDefault(s => s.UseType == useType);
    }

    /// <summary>A book read in parts at bookstands: each reading speaks <see cref="PagesPerReading"/> lines and raises an attribute.</summary>
    public sealed class StandBook
    {
        [JsonConstructor]
        public StandBook(string useFunc, string line, int pages)
        {
            UseFunc = useFunc;
            Line = line;
            Pages = pages;
        }

        /// <summary>The bookstand's use function ("BS_BookOfFlood").</summary>
        public string UseFunc { get; }
        /// <summary>Hero SVM line prefix; page n is <c>&lt;Line&gt;_nn</c>.</summary>
        public string Line { get; }
        public int Pages { get; }
    }

    /// <summary>A one-time lesson stand: one line, one attribute raise.</summary>
    public sealed class StandLesson
    {
        [JsonConstructor]
        public StandLesson(string useFunc, string line, string attribute)
        {
            UseFunc = useFunc;
            Line = line;
            Attribute = attribute;
        }

        public string UseFunc { get; }
        public string Line { get; }
        public string Attribute { get; }
    }

    /// <summary>A story stand: needs a game event, speaks its lines, starts and finishes quests, switches sectors on.</summary>
    public sealed class StandStory
    {
        [JsonConstructor]
        public StandStory(string useFunc, string requiredFlag, string notReadyLine, IReadOnlyList<string> lines,
                          IReadOnlyList<string> runQuests, IReadOnlyList<string> succeedQuests, IReadOnlyList<string> sectors = null)
        {
            Sectors = sectors ?? Array.Empty<string>();
            UseFunc = useFunc;
            RequiredFlag = requiredFlag;
            NotReadyLine = notReadyLine;
            Lines = lines ?? Array.Empty<string>();
            RunQuests = runQuests ?? Array.Empty<string>();
            SucceedQuests = succeedQuests ?? Array.Empty<string>();
        }

        public string UseFunc { get; }
        public string RequiredFlag { get; }
        public string NotReadyLine { get; }
        public IReadOnlyList<string> Lines { get; }
        public IReadOnlyList<string> RunQuests { get; }
        public IReadOnlyList<string> SucceedQuests { get; }
        /// <summary>World sectors the reading switches on (BS_Chronicles: the library's story sector).</summary>
        public IReadOnlyList<string> Sectors { get; }
    }

    /// <summary>
    /// Bookstands and stone plates (G3 <c>Bookstand.cpp</c>, CP 1.75). No page opens: the hero reads aloud (his SVM lines)
    /// and a stand can be used once. "Ancient wisdom" stands read the next unfinished book of <see cref="WisdomOrder"/>;
    /// every reading of a book gives <see cref="AttributePoints"/> of <see cref="BookAttribute"/>.
    /// </summary>
    public sealed class BookstandRules
    {
        [JsonConstructor]
        public BookstandRules(Balanced<int> attributePoints, int pagesPerReading, string bookAttribute, string wisdomUseFunc,
                              IReadOnlyList<StandBook> books, IReadOnlyList<string> wisdomOrder, IReadOnlyList<StandLesson> lessons,
                              IReadOnlyList<StandStory> stories, IReadOnlyList<string> nothingNewLines)
        {
            AttributePoints = attributePoints;
            PagesPerReading = pagesPerReading < 1 ? 1 : pagesPerReading;
            BookAttribute = bookAttribute;
            WisdomUseFunc = wisdomUseFunc;
            Books = books ?? Array.Empty<StandBook>();
            WisdomOrder = wisdomOrder ?? Array.Empty<string>();
            Lessons = lessons ?? Array.Empty<StandLesson>();
            Stories = stories ?? Array.Empty<StandStory>();
            NothingNewLines = nothingNewLines ?? Array.Empty<string>();
        }

        public Balanced<int> AttributePoints { get; }
        public int PagesPerReading { get; }
        public string BookAttribute { get; }
        public string WisdomUseFunc { get; }
        public IReadOnlyList<StandBook> Books { get; }
        /// <summary>Use functions of <see cref="Books"/>, in the order wisdom stands read them.</summary>
        public IReadOnlyList<string> WisdomOrder { get; }
        public IReadOnlyList<StandLesson> Lessons { get; }
        public IReadOnlyList<StandStory> Stories { get; }
        /// <summary>One is spoken at random when a stand has nothing (more) to teach.</summary>
        public IReadOnlyList<string> NothingNewLines { get; }

        public static readonly BookstandRules None = new BookstandRules(default, 1, null, null, null, null, null, null, null);
    }
}
