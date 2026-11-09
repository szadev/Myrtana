using System;

namespace Myrtana.Rules
{
    /// <summary>The three CP potion keys (session keys UsePotionHP / MP / SP).</summary>
    public enum QuickPotion { Health, Mana, Stamina }

    /// <summary>
    /// CP 1.75 potion quick keys (OnPlayerPotionQuickUse, Script_Game 0x10064b50; actions 0x8d-0x8f; default keys Num 7 /
    /// Num 8 / Num 9): the key drinks the hero's <c>It_Potion_Health</c>, <c>It_Potion_Mana</c> or <c>It_Potion_Stamina</c>
    /// (that exact template, the small potion; PSInventory::FindStackIndex) through PS_QuickUse, like a quick slot. Refused
    /// while transformed (animal form, ghost) and when the hero carries none. AniState 3/4 (not ported: our hero has no
    /// such ani states) refuse too.
    /// </summary>
    public static class PotionQuickUse
    {
        public const string Health = "It_Potion_Health", Mana = "It_Potion_Mana", Stamina = "It_Potion_Stamina";

        public static string Template(QuickPotion potion)
        {
            switch (potion)
            {
                case QuickPotion.Health: return Health;
                case QuickPotion.Mana: return Mana;
                case QuickPotion.Stamina: return Stamina;
                default: throw new ArgumentOutOfRangeException(nameof(potion));
            }
        }

        /// <summary>The item the key drinks, or null when nothing happens.</summary>
        /// <param name="count">Amount of an item the hero carries.</param>
        public static string ItemToDrink(QuickPotion potion, bool transformed, Func<string, int> count)
        {
            if (transformed) return null;
            string item = Template(potion);
            return count(item) > 0 ? item : null;
        }
    }
}
