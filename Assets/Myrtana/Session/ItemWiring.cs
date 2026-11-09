using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;

namespace Myrtana.Session
{
    /// <summary>
    /// Creates the item, trade and teaching services of a session from the content's rule definitions and connects
    /// them to the story events they react to (quest item deliveries, the end of a merchant conversation).
    /// </summary>
    public static class ItemWiring
    {
        public static ItemServices Create(IContentDatabase content, Hero hero, ProgressionService progression, FameLedger fame,
                                          RuleOptions options, IRandom random, IEventBus events, IGameLog log)
        {
            var itemRules = Definition(content, ItemRules.DefinitionId, ItemRules.None, log);
            var inventories = new Inventories(events);
            var heroInventory = inventories.Of(ActorId.Player);
            var equipment = new HeroEquipment(hero, heroInventory, content, itemRules, events);
            var roles = new ActorRoles(content);
            var tradeRules = Definition(content, TradeRules.DefinitionId, NeutralTrade, log);
            var trade = new TradeService(tradeRules, itemRules, options, content, inventories, new TreasureRoller(random), hero,
                                         faction => faction == null ? 0 : fame.Of(FameKind.Political, faction));
            var teaching = new TeachingService(Definition(content, TeachingRules.DefinitionId, TeachingRules.None, log), options,
                                               content, progression, roles, heroInventory, itemRules.GoldItem, events);
            var world = new WorldItems(content, inventories, events);
            // NPC inventories (npc-inv): the static stacks are there from the start; the plunder is rolled at the first
            // knock-out or death (G3 Defeat/Kill → CleanUpPlunderInv).
            var npcLoot = new NpcLoot(content, inventories, world, trade, random, hero.Knows, events)
            {
                Species = npc => content.TryGet(npc.Name, out NpcDef def) ? def.Combat?.Species : null,
            };
            npcLoot.SeedStartStacks();
            events.Subscribe<ActorDefeated>(e => npcLoot.GeneratePlunder(e.Victim));
            var items = new ItemServices(itemRules, inventories, equipment, new ItemUse(progression, inventories, content, events),
                                         world, roles, trade, teaching, npcLoot);
            return items;
        }

        /// <summary>Prices at gold value both ways when the content has no trade rules.</summary>
        static readonly TradeRules NeutralTrade = new TradeRules(TradeRules.DefinitionId, 1, 1, 1, null, null, 1, 1, 0, 0, 1, 0, default);

        static T Definition<T>(IContentDatabase content, string id, T fallback, IGameLog log) where T : class, IDefinition
        {
            if (content.TryGet(id, out T definition)) return definition;
            log.Warning($"The content has no '{id}' rules ({typeof(T).Name}); defaults are used.");
            return fallback;
        }
    }
}
