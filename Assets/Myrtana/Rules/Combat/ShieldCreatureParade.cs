using System;
using System.Collections.Generic;

namespace Myrtana.Rules
{
    /// <summary>
    /// The port's combat options that can change while a session runs (menu rows, <c>PortOptions</c>); one instance per
    /// session (<see cref="CombatService.Port"/>), read by the hit resolver and the NPC combat AI. Defaults are G3 CP 1.75.
    /// </summary>
    public sealed class CombatPortOptions
    {
        /// <summary>[owner] row "Shield protects from beasts" (fix-109): <see cref="ShieldCreatureParade"/>; false = CP 1.75.</summary>
        public bool ShieldBlocksCreatures { get; set; }

        /// <summary>
        /// [owner] row "Forgive accidental hits" (fix-162): the hero's stray hits on an NPC that fights his enemy, and on
        /// his party, start no fight (<c>Myrtana.Simulation.AccidentalHits</c>); false = CP 1.75. The menu's default is on.
        /// </summary>
        public bool ForgiveAccidentalHits { get; set; }

        /// <summary>
        /// [owner] row "Arrows finish downed foes" (fix-241): an arrow, bolt or damaging spell projectile of the hero or his party
        /// on a knocked-out NPC that can be killed finishes it like the melee finishing move (<c>CombatResolver.FinishesDowned</c>);
        /// false = CP 1.75 (a downed victim takes no damage). The menu's default is on.
        /// </summary>
        public bool RangedFinishesDowned { get; set; }
    }

    /// <summary>
    /// fix-109 [owner] option "Shield protects from beasts" = the Russian Update Pack 1.04 blocking change (changelog
    /// item 53; gameplay-logic.md §13). CP 1.75 CanParadeMoveOf (0x1000d640) never lets a fist-mode attacker that is not
    /// humanoid be parried (every animal and monster strikes with PhysicalFist), under every CP option and with or without
    /// a shield or the shield perks [fix-81]. UP 1.04 changes it to:
    /// <list type="bullet">
    /// <item>a shield (shield + one-handed weapon) blocks the attacks of animals and of fist fighters, except trolls and
    /// monsters;</item>
    /// <item>the Minecrawler's attacks are no longer blocked by a weapon (shield only);</item>
    /// <item>demon and ogre attacks only with Perk_Shield_2 ("powerful shield defence").</item>
    /// </list>
    /// UP's code is not available (its DLLs were never in the old install), so the species lists are [interp]: "monsters"
    /// = the golems, the dragon and the scorpion king; an NPC blocker needs no perk (as in G3's DefenseLevel, where the
    /// shield counts for NPCs without one). Everything else is G3's shield block (CombatResolver.TryBlock: stamina cost
    /// with the shield discount, the stumble by power difference — a power attack or a strong creature still throws the
    /// blocker into the heavy parade stumble). A parade without a shield still blocks no creature.
    /// </summary>
    public static class ShieldCreatureParade
    {
        public const string Minecrawler = "Minecrawler";

        /// <summary>Never blocked, not even with a shield (UP: "trolls and monsters") [interp list].</summary>
        static readonly HashSet<string> Unblockable = new HashSet<string>(StringComparer.Ordinal)
            { "Troll", "Golem", "FireGolem", "IceGolem", "Dragon", "ScorpionKing" };

        /// <summary>Blocked only with Perk_Shield_2 (the hero; NPCs need no perk) [UP; NPC part interp].</summary>
        static readonly HashSet<string> PerkOnly = new HashSet<string>(StringComparer.Ordinal) { "Demon", "Ogre" };

        /// <summary>
        /// Whether a fist-mode attacker (an animal, a monster or a humanoid with fists) is blocked by a weapon-holding
        /// defender with the option on. <paramref name="shieldAndOneHanded"/>: the shield in the left hand.
        /// </summary>
        public static bool BlocksFistAttack(string attackerSpecies, bool shieldAndOneHanded, bool defenderIsPlayer, Func<string, bool> defenderHas)
        {
            if (!shieldAndOneHanded) return false;
            string species = attackerSpecies ?? "Human";
            if (Unblockable.Contains(species)) return false;
            if (PerkOnly.Contains(species)) return !defenderIsPlayer || defenderHas != null && defenderHas(CombatStats.ShieldPerk);
            return true;
        }

        /// <summary>UP: the Minecrawler's claws (not fist mode in G3) are no longer parried by a weapon alone.</summary>
        public static bool NeedsShield(string attackerSpecies) => string.Equals(attackerSpecies, Minecrawler, StringComparison.Ordinal);
    }
}
