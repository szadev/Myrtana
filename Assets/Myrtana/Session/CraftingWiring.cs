using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Story;

namespace Myrtana.Session
{
    /// <summary>The documents page tabs, in G3's order (IDC_TABCTRL_DOCUMENTS_2).</summary>
    public enum DocumentTab { Maps, Books, AlchemyRecipes, CookingRecipes, SmithingRecipes }

    /// <summary>
    /// Stations, recipes, bookstands and documents of one session (Docs/interactive-objects.md §6). Built from the session's
    /// content and hero; the bookstand memory is the only own state (saved by <see cref="BookstandSaveSection"/>).
    /// </summary>
    public sealed class CraftingServices
    {
        readonly GameSession _session;

        CraftingServices(GameSession session, CraftingService crafting, Bookstands bookstands)
        {
            _session = session;
            Crafting = crafting;
            Bookstands = bookstands;
        }

        public CraftingService Crafting { get; }
        public Bookstands Bookstands { get; }

        public static CraftingServices Create(GameSession session, BookstandMemory memory)
        {
            var db = session.Content.Database;
            var rules = db.TryGet(CraftingRules.DefinitionId, out CraftingRules r) ? r : CraftingRules.None;
            var hero = session.Items.Inventories.Of(ActorId.Player);
            var crafting = new CraftingService(rules, db, hero, session.Hero.Knows, session.Events);
            var bookstands = new Bookstands(rules.Bookstands, session.Settings.Rules, memory ?? new BookstandMemory(), session.Flags, session.Random,
                                            session.Events);
            return new CraftingServices(session, crafting, bookstands);
        }

        /// <summary>
        /// Reads a bookstand object and applies what it teaches (attribute, quests, sectors). With
        /// <paramref name="deferGain"/> the attribute raise waits for <see cref="Learn"/>: G3's BS_* states raise it (and
        /// print "&lt;attribute&gt; + n = value") only after the hero has read the pages aloud.
        /// </summary>
        public BookstandReading Read(string objectId, bool deferGain = false)
        {
            string useFunc = _session.Interaction.Objects.TryGet(objectId, out var def) ? def.UseFunc : null;
            var reading = Bookstands.Read(objectId, useFunc);
            if (!deferGain) Learn(reading);
            foreach (var quest in reading.RunQuests) _session.Quests.TrySetStatus(new Id<QuestDef>(quest), QuestStatus.Running);
            foreach (var quest in reading.SucceedQuests) _session.Quests.TrySetStatus(new Id<QuestDef>(quest), QuestStatus.Success);
            foreach (var sector in reading.EnableSectors) _session.WorldState.Sectors.Set(sector, true);
            return reading;
        }

        /// <summary>
        /// The reading's attribute raise (G3 AddIntelligence / AddAlchemy / AddSmithing → PrintAddValue, a
        /// <see cref="AttributeRaised"/> message); once — a second call does nothing.
        /// </summary>
        public void Learn(BookstandReading reading)
        {
            if (reading?.Gain == null || reading.Learned) return;
            reading.Learned = true;
            _session.Progression.Raise(reading.Gain);
        }

        public bool TryGetDocument(string item, out DocumentDef document) => _session.Content.Database.TryGet(item, out document);

        public bool TryGetRecipe(string item, out RecipeDef recipe) => _session.Content.Database.TryGet(item, out recipe);

        /// <summary>
        /// The items a documents tab lists: the hero's carried maps, letters and books, or recipes of one craft
        /// (G3 <c>gCHUDListCtrlInvDocuments</c>: a filter over the inventory, nothing is remembered).
        /// </summary>
        public IReadOnlyList<string> Documents(DocumentTab tab)
        {
            switch (tab)
            {
                case DocumentTab.Maps: return _session.Maps.Owned().Select(m => m.Id).ToList();
                case DocumentTab.AlchemyRecipes: return Crafting.Recipes(CraftKind.Alchemy).Select(r => r.Id).ToList();
                case DocumentTab.CookingRecipes: return Crafting.Recipes(CraftKind.Cooking).Select(r => r.Id).ToList();
                case DocumentTab.SmithingRecipes: return Crafting.Recipes(CraftKind.Smithing).Select(r => r.Id).ToList();
                default:
                    var hero = _session.Items.Inventories.Of(ActorId.Player);
                    return hero.Stacks.Where(s => s.Amount > 0).Select(s => s.Item).Distinct().Where(i => TryGetDocument(i, out _) && !_session.Maps.IsMap(i)).ToList();
            }
        }
    }
}
