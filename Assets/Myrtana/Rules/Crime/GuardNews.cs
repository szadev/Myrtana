using System;
using System.Collections.Generic;

namespace Myrtana.Rules
{
    /// <summary>How much the guards of an enclave have heard about one kind of crime (G3 _AI_SayGuardNews).</summary>
    public enum NewsLevel { None, First, Second, Known }

    /// <summary>
    /// Guard news, bribe and excuse (crime.json guardNews/bribe/excuse; Docs/perception-crime.md §6.2). Pure functions
    /// over an <see cref="EnclaveCrimeRecord"/>.
    /// </summary>
    public static class GuardNews
    {
        /// <summary>
        /// The news level of <paramref name="crime"/> (Theft or Murder): the number of thresholds its count has reached,
        /// and at least Known when a witness reported a crime at least this bad.
        /// </summary>
        public static NewsLevel LevelOf(EnclaveCrimeRecord record, CrimeType crime, CrimeRules rules)
        {
            if (record == null || rules == null || crime == CrimeType.None) return NewsLevel.None;
            if (record.Known != CrimeType.None && record.Known >= crime) return NewsLevel.Known;
            return Level(record.CountOf(crime), ThresholdsOf(crime, rules));
        }

        /// <summary>The level only while something happened since the guards last talked about it; otherwise None.</summary>
        public static NewsLevel NewsToTell(EnclaveCrimeRecord record, CrimeType crime, CrimeRules rules) =>
            record != null && record.CountOf(crime) > record.DiscussedOf(crime) ? LevelOf(record, crime, rules) : NewsLevel.None;

        /// <summary>A bribe still works while the guards do not know for sure.</summary>
        public static bool CanBribe(NewsLevel level, CrimeRules rules) => (int)level < rules.BribeBelowLevel;

        /// <summary>
        /// The player talks his way out (needs the excuse perk). Below Known it always works; at Known the roll
        /// (uniform 0..ExcuseRoll-1) must stay below ExcuseRollBelow and at most the player's Theft skill.
        /// </summary>
        public static bool ExcuseSucceeds(NewsLevel level, bool hasExcusePerk, int theftSkill, int roll, CrimeRules rules)
        {
            if (!hasExcusePerk) return false;
            if (level < NewsLevel.Known) return true;
            return roll < rules.ExcuseRollBelow && roll <= theftSkill;
        }

        static IReadOnlyList<int> ThresholdsOf(CrimeType crime, CrimeRules rules) =>
            crime == CrimeType.Murder ? rules.MurderNewsThresholds : rules.OtherNewsThresholds;

        static NewsLevel Level(int count, IReadOnlyList<int> thresholds)
        {
            int level = 0;
            foreach (int t in thresholds)
                if (count >= t) level++;
            return (NewsLevel)Math.Min(level, (int)NewsLevel.Known);
        }
    }
}
