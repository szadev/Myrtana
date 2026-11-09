using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Simulation
{
    /// <summary>
    /// An NPC drinks a healing potion in a fight (G3 ZS_Attack_Loop → ZS_Attack_DrinkPotion → _AI_UseItem, Docs/combat.md
    /// §6): the brain decides when (<see cref="NpcCombatBrain"/>: below 40 % HP, not a mage or paladin, the target
    /// farther than 7 m and not holding a bow or spell); this takes one potion out of the NPC's inventory and applies its
    /// use effects to the NPC's vitals (HP, stamina, mana; the same items.json "use" the hero's potions apply). No cooldown:
    /// the brain asks again on its next pass while potions and the conditions last.
    /// </summary>
    public static class NpcPotions
    {
        /// <summary>Whether <paramref name="npc"/> carries <paramref name="potion"/>.</summary>
        public static bool Has(Inventories inventories, ActorId npc, string potion) =>
            !string.IsNullOrEmpty(potion) && inventories.Of(npc).Has(potion);

        /// <summary>Drinks one <paramref name="potion"/>: false when none is carried or the item has no use effects.</summary>
        public static bool Drink(Inventories inventories, IContentDatabase content, ActorVitals vitals, ActorId npc, string potion)
        {
            if (!Has(inventories, npc, potion) || !content.TryGet(potion, out ItemDef def) || def.UseEffects.Count == 0) return false;
            if (inventories.Consume(npc, potion, 1) == 0) return false;
            foreach (var effect in def.UseEffects) Apply(vitals, npc, effect);
            return true;
        }

        static void Apply(ActorVitals vitals, ActorId npc, AttributeGain effect)
        {
            switch (effect.Attribute)
            {
                case ActorVitals.HitPointsId:
                    vitals.AddHitPoints(npc, Delta(vitals.HitPoints(npc), vitals.HitPointsMax(npc), effect));
                    break;
                case ActorVitals.StaminaId:
                    vitals.AddStamina(npc, Delta(vitals.Stamina(npc), vitals.StaminaMax(npc), effect));
                    break;
                case ActorVitals.ManaId:
                    vitals.AddMana(npc, Delta(vitals.Mana(npc), vitals.ManaMax(npc), effect));
                    break;
            }
        }

        /// <summary>The change of a vital's current value; NPC maxima stay as they are (only the value moves, capped).</summary>
        static int Delta(int value, int max, AttributeGain effect)
        {
            var next = ModifierMath.Apply(new StatValue(value, max), effect.Op, effect.Amount, hasMaximum: true);
            return System.Math.Min(next.Value, max) - value;
        }
    }

    /// <summary>How an NPC heals itself after a fight (G3 ZS_HealSelf).</summary>
    public enum HealSelfMethod { None, Potion, Spell }

    /// <summary>
    /// G3 ContinueRoutine → ZS_HealSelf (ContinueRoutine.cpp, CP 1.75): back to the routine (after a fight or a
    /// reaction, or entering the processing range) an NPC below 61 % hit points that can heal itself does so: mages and
    /// paladins (class 1/2) cast It_Spell_Healing on themselves (ZS_HealSelf_Cast), everyone else drinks an
    /// It_Potion_Health (_AI_UseItem: one potion, the item's effect); the player's party members always try (CanHealSelf),
    /// without a potion that does nothing. It repeats on every return to the routine while the potions last.
    /// </summary>
    public static class HealSelfRules
    {
        public const int BelowPercent = 61;
        public const string Potion = "It_Potion_Health", Spell = "It_Spell_Healing";

        public static HealSelfMethod Choose(int hitPointsPercent, NpcClass npcClass, bool hasPotion, bool ledByPlayer)
        {
            if (hitPointsPercent >= BelowPercent) return HealSelfMethod.None;
            if (npcClass == NpcClass.Mage || npcClass == NpcClass.Paladin) return HealSelfMethod.Spell;
            return hasPotion || ledByPlayer ? HealSelfMethod.Potion : HealSelfMethod.None;
        }

        public static int Percent(int hitPoints, int hitPointsMax) => hitPointsMax > 0 ? hitPoints * 100 / hitPointsMax : 100;
    }
}
