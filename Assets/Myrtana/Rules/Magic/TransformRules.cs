using System;
using System.Collections.Generic;

namespace Myrtana.Rules
{
    /// <summary>
    /// Animal transformation of the player (G3 MagicAnimalForm / MagicTransform, CP 1.75; Docs/combat.md §12.3,
    /// gameplay-data/magic.json "transformation"). Pure rules only: the form's duration, which forms are allowed, and what
    /// the return to the hero's body does. The body swap itself is not built yet.
    /// </summary>
    public static class TransformRules
    {
        public const string AnimalFormSpell = "Spl_AnimalForm";
        public const int BaseSeconds = 60, IntOffset = 60;
        public const float LargeFormFactor = 0.5f;
        public const string DruidPerk = "Perk_Druid", WatermagePerk = "Perk_Watermage";

        /// <summary>Species the Animal Form pre-check accepts as focus target (CP adds Meatbug).</summary>
        public static readonly HashSet<string> AnimalFormTargets = new HashSet<string>(StringComparer.Ordinal)
        {
            "Minecrawler", "Scavenger", "Wolf", "Boar", "Sabertooth", "Shadowbeast", "Bison", "Rhino", "Ripper", "Lurker",
            "Varan", "Snapper", "Alligator", "FireVaran", "Bloodfly", "SwampLurker", "Meatbug",
        };

        /// <summary>G3 IsAnimal (CanTransformInto also needs it).</summary>
        public static readonly HashSet<string> Animals = new HashSet<string>(StringComparer.Ordinal)
        {
            "Minecrawler", "Scavenger", "Wolf", "Boar", "Sabertooth", "Shadowbeast", "Bison", "Rhino", "Ripper", "Lurker",
            "Varan", "Snapper", "Alligator", "FireVaran", "Bloodfly", "Deer", "Pig", "Cow", "SwampLurker",
        };

        /// <summary>G3 IsLarge: these forms last half as long.</summary>
        public static readonly HashSet<string> LargeForms = new HashSet<string>(StringComparer.Ordinal)
        {
            "Minecrawler", "Sabertooth", "Shadowbeast", "Bison", "Rhino", "Ripper", "Alligator",
        };

        /// <summary>Animal Form pre-check: the focus target's species is in the list (else "DoesntWork", no fizzle).</summary>
        public static bool AnimalFormTargetAllowed(string targetSpecies) =>
            targetSpecies != null && AnimalFormTargets.Contains(targetSpecies);

        /// <summary>CanTransformInto: an animal, not a pig (Meatbug passes the pre-check but fails here: CAST_FAIL).</summary>
        public static bool CanTransformInto(string species) =>
            species != null && Animals.Contains(species) && !string.Equals(species, "Pig", StringComparison.Ordinal);

        /// <summary>
        /// GetTransformDuration: (60 + max(0, INT − 60)) × 0.5 for large forms × (1 + Perk_Druid + 0.5·Perk_Watermage) for
        /// the player; an artefact (item category 3) transformation lasts until cancelled (−1).
        /// </summary>
        public static float DurationSeconds(int intelligence, string form, bool isPlayer, Func<string, bool> knows, bool artefact = false)
        {
            if (artefact) return -1f;
            float seconds = BaseSeconds + Math.Max(0, intelligence - IntOffset);
            if (form != null && LargeForms.Contains(form)) seconds *= LargeFormFactor;
            if (isPlayer && knows != null)
                seconds *= 1f + (knows(DruidPerk) ? 1f : 0f) + (knows(WatermagePerk) ? 0.5f : 0f);
            return seconds;
        }

        /// <summary>
        /// TransformBack (CP): the hero keeps the HP he had (no refill); a level-up earned meanwhile gives +1 level, +10 LP
        /// (+1 with Perk_Learn) and, with Alternative Balancing, +5 max HP and +5 HP.
        /// </summary>
        public static (int HitPointsMax, int HitPoints) LevelUpOnReturnBonus(RuleOptions options) =>
            options != null && options.AlternativeBalancing ? (5, 5) : (0, 0);
    }
}
