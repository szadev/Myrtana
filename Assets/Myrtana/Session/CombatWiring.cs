using System;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;

namespace Myrtana.Session
{
    /// <summary>Builds the session's combat service: rules from content, vitals over the hero and the NPC registry.</summary>
    public static class CombatWiring
    {
        public const string BlockedByKnockOut = "knockedOut";
        public const string BlockedByCombat = "inCombat";
        /// <summary>G3 refuses every save (quick save included) once the hero is dead; only loading is offered.</summary>
        public const string BlockedByDeath = "dead";

        /// <param name="inPlayerParty">Whether an NPC is led by the player (companions stay down as briefly as the hero).</param>
        public static CombatService Create(IContentDatabase content, ActorRegistry actors, Hero hero, RuleOptions options,
                                           IRandom random, IEventBus events, IGameLog log, Func<ActorId, bool> inPlayerParty = null)
        {
            if (!content.TryGet(CombatRules.DefinitionId, out CombatRules rules))
            {
                log.Warning("The content has no combat rules; moves cost no stamina and no species is special.");
                rules = CombatRules.Neutral();
            }
            Func<ActorId, VitalsProfile> profiles = id =>
                actors.TryGet(id, out var actor) ? CombatDefOf(actor.Definition).Profile(options.Difficulty, inPlayerParty?.Invoke(id) ?? false) : null;
            var vitals = new ActorVitals(hero.Stats, profiles, rules, events);
            return new CombatService(rules, options, random, vitals);
        }

        /// <summary>The NPC combat AI: tables from content (neutral with a warning when missing), brains created on demand.</summary>
        public static NpcCombatSystem CreateNpcCombat(IContentDatabase content, ActorRegistry actors, CombatService combat, RuleOptions options,
                                                      IRandom random, Func<double> now, Func<double> gameNow, IEventBus events, IGameLog log)
        {
            if (!content.TryGet(CombatAiRules.DefinitionId, out CombatAiRules ai))
            {
                log.Warning("The content has no combat AI tables; NPCs fight without tactics.");
                ai = CombatAiRules.Neutral();
            }
            return new NpcCombatSystem(ai, combat.Rules, options, random,
                                       id => actors.TryGet(id, out var actor) && !id.IsPlayer ? CombatDefOf(actor) : null,
                                       id => actors.TryGet(id, out var actor) ? LoadoutOf(actor, content) : CombatLoadout.Fists,
                                       now, gameNow, events) { Port = combat.Port };
        }

        /// <summary>
        /// The combat facts of an NPC from its content. NPCs without imported facts (hand-made content, the hero's
        /// world entity) are level-0 fighters of their species.
        /// </summary>
        /// <summary>The NPC's combat facts with what the story changed (G3 SetClass).</summary>
        public static NpcCombatDef CombatDefOf(ActorState actor)
        {
            var def = CombatDefOf(actor.Definition);
            if (actor.ClassChanged) def = def.WithClass(actor.Class);
            if (actor.ImmortalOverride is bool immortal) def = def.WithImmortal(immortal);
            // CP 1.75 SetPAL Reb on Wenzel: Friend NPC, no enclave (fix-94); derived from the saved alignment change
            if (actor.AlignmentChanged && StoryAlignmentRules.LeavesEnclaveAsFriend(actor.Id.Name, actor.Alignment)) def = def.AsFriendWithoutEnclave();
            return def;
        }

        /// <summary>
        /// The NPC's weapons: what the story had it put on (G3 Wear, the latest first) before its own equipment, so a worn
        /// weapon replaces the one of its kind.
        /// </summary>
        public static CombatLoadout LoadoutOf(ActorState actor, IContentDatabase content)
        {
            if (actor.Worn.Count == 0) return LoadoutOf(actor.Definition, content);
            var own = CombatDefOf(actor.Definition).Equipment;
            var ids = actor.Worn.Reverse().Concat(own);
            var items = ids.Select(id => content.TryGet(id, out ItemDef item) ? item : null).ToList();
            var loadout = CombatLoadout.From(items);
            if (loadout.Ranged != null && loadout.Ammo == null &&
                content.TryGet(RangedRules.NpcAmmoItem(loadout.Ranged.Value), out ItemDef ammo))
                loadout = CombatLoadout.From(items.Append(ammo));
            return WithSpeciesWeapon(loadout, actor.Definition, content);
        }

        /// <summary>fix-237: a weaponless Stalker / Ogre / Goblin / Demon fights with its species weapon (G3 0x1000b200).</summary>
        static CombatLoadout WithSpeciesWeapon(CombatLoadout loadout, NpcDef npc, IContentDatabase content)
        {
            if (loadout.MeleeItem != null) return loadout;
            var natural = SpeciesWeaponry.NaturalWeapon(CombatDefOf(npc).Species ?? npc.Species);
            if (natural == null || !content.TryGet(natural.Value.item, out ItemDef weapon)) return loadout;
            return loadout.WithNaturalWeapon(weapon, natural.Value.quality);
        }

        public static NpcCombatDef CombatDefOf(NpcDef npc)
        {
            if (npc.Combat != null) return npc.Combat;
            string species = npc.Species ?? "Human";
            return new NpcCombatDef(level: 0, levelMax: 0, species, humanoid: species == "Human" || species == "Orc");
        }

        /// <summary>
        /// The NPC's weapons from its equipment items (items missing from the content are skipped). An archer without
        /// ammunition gets plain arrows or bolts: G3 assures 10–19 of them whenever it starts shooting (ZS_Attack_Shoot).
        /// </summary>
        public static CombatLoadout LoadoutOf(NpcDef npc, IContentDatabase content)
        {
            var equipment = CombatDefOf(npc).Equipment;
            if (equipment.Count == 0) return WithSpeciesWeapon(CombatLoadout.Fists, npc, content);
            var items = equipment.Select(id => content.TryGet(id, out ItemDef item) ? item : null).ToList();
            var loadout = CombatLoadout.From(items);
            if (loadout.Ranged != null && loadout.Ammo == null &&
                content.TryGet(RangedRules.NpcAmmoItem(loadout.Ranged.Value), out ItemDef ammo))
                loadout = CombatLoadout.From(items.Append(ammo));
            return WithSpeciesWeapon(loadout, npc, content);
        }
    }
}
