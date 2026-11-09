using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;

namespace Myrtana.Session
{
    /// <summary>
    /// Builds the session's magic (Docs/combat.md §12): the spells and magic rules from content, burning and freezing on
    /// every resolved hit, scroll use from the inventories, the NPC brains' spell lookup, and the summon groups.
    /// </summary>
    public static class MagicWiring
    {
        public static MagicService Create(IContentDatabase content, ActorRegistry actors, CombatService combat, NpcCombatSystem npcCombat,
                                          ItemServices items, RuleOptions options, IEventBus events, IGameLog log)
        {
            if (!content.TryGet(MagicRules.DefinitionId, out MagicRules rules))
            {
                log.Warning("The content has no magic rules; spells are cast without power-cast rules, burning or summon leash.");
                rules = MagicRules.Neutral();
            }
            var status = new StatusEffects(combat.Vitals, rules.Burning, id => IsHumanoid(actors, id), events,
                                           id => PoisonSeconds(actors, id), id => IsEvil(actors, id));
            combat.Status = status;
            var magic = new MagicService(rules, content.All<SpellDef>(), options, combat.Vitals, status,
                                         id => content.TryGet(id, out ItemDef item) ? item : null,
                                         (actor, item, amount) => items.Inventories.Consume(actor, item, amount), events);
            npcCombat.Spells = item => magic.TryGetSpell(item, out var spell) ? spell : null;
            return magic;
        }

        /// <summary>CP OnTick: the player 30 s, an NPC max(10, 60 - (LevelMax + Level)/2) s.</summary>
        static float PoisonSeconds(ActorRegistry actors, ActorId id)
        {
            if (id.IsPlayer || !actors.TryGet(id, out var actor)) return StatusEffects.PlayerPoisonSeconds;
            var def = CombatWiring.CombatDefOf(actor.Definition);
            return StatusEffects.NpcPoisonSeconds(def.Level, def.LevelMax);
        }

        /// <summary>CanBePoisoned / CanBeDiseased: evil creatures are immune.</summary>
        static bool IsEvil(ActorRegistry actors, ActorId id) =>
            !id.IsPlayer && actors.TryGet(id, out var actor) && CombatWiring.CombatDefOf(actor.Definition).Evil;

        static bool IsHumanoid(ActorRegistry actors, ActorId id) =>
            id.IsPlayer || actors.TryGet(id, out var actor) && CombatWiring.CombatDefOf(actor.Definition).Humanoid;
    }
}
