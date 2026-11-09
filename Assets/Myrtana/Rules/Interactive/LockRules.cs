using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// How locks open (Docs/gameplay-logic.md §7.4, gameplay-data/items.json "locks"). G3 has no lockpicking minigame:
    /// the hero opens a lock with its key, or with a lockpick when he knows the perk of the lock's difficulty; every pick
    /// attempt uses up one lockpick and CP 1.75 lets it fail when the hero lacks the next perk up. Each attempt is a fresh,
    /// independent roll (G3 <c>rand()</c>, not part of the save; nothing carries over from the last attempt).
    /// </summary>
    public sealed class LockRules : IDefinition
    {
        public const string DefinitionId = "locks";

        [JsonConstructor]
        public LockRules(string id, string lockpickItem, IReadOnlyList<string> perksByDifficulty, int maxPickDifficulty,
                         int breakChancePercent, float sleepUntilHour)
        {
            Id = id;
            LockpickItem = lockpickItem;
            PerksByDifficulty = perksByDifficulty ?? Array.Empty<string>();
            MaxPickDifficulty = maxPickDifficulty;
            BreakChancePercent = breakChancePercent;
            SleepUntilHour = sleepUntilHour;
        }

        public string Id { get; }
        public string LockpickItem { get; }
        /// <summary>The perk needed to pick difficulty i + 1 (Perk_LockPick_1..3).</summary>
        public IReadOnlyList<string> PerksByDifficulty { get; }
        /// <summary>Locks above this difficulty cannot be picked.</summary>
        public int MaxPickDifficulty { get; }
        /// <summary>
        /// CP 1.75: chance that a pick fails when the hero lacks the perk of the next difficulty (0 = old rule). G3
        /// <c>_AI_Unlock</c> fails when <c>GetRandomNumber(100) &gt; 70</c> (<c>rand() % 100</c>), i.e. 29 of the 100 rolls.
        /// </summary>
        public int BreakChancePercent { get; }
        /// <summary>A bed always sleeps until this hour (G3 PlayerSleep(8)).</summary>
        public float SleepUntilHour { get; }

        string IDefinition.Key => Id;

        public static LockRules Default { get; } = new LockRules(DefinitionId, "It_Key_Lockpick",
            new[] { "Perk_LockPick_1", "Perk_LockPick_2", "Perk_LockPick_3" }, 3, 29, 8f);

        /// <summary>The perk for picking <paramref name="difficulty"/>, or null outside 1..<see cref="MaxPickDifficulty"/>.</summary>
        public string PerkFor(int difficulty) =>
            difficulty >= 1 && difficulty <= PerksByDifficulty.Count ? PerksByDifficulty[difficulty - 1] : null;
    }

    /// <summary>Outcome of trying a lock (the hero's refusal lines are the SVMs of the same names).</summary>
    public enum LockVerdict
    {
        /// <summary>Not locked (any more).</summary>
        Open,
        /// <summary>The hero carries the key.</summary>
        Key,
        /// <summary>He may pick it: one lockpick is used up and the attempt may fail.</summary>
        Pick,
        /// <summary>Only the key opens it.</summary>
        NeedKey,
        /// <summary>He has the perk but no lockpick.</summary>
        NeedLockpick,
        /// <summary>No lockpick, and the lock also has a key.</summary>
        NeedKeyOrLockpick,
        /// <summary>He lacks the perk for difficulty 1 (HUD "Missing skill").</summary>
        MissingSkill,
        /// <summary>He lacks the perk for difficulty 2.</summary>
        TooHard,
        /// <summary>Difficulty 3 without its perk, or above the pickable range.</summary>
        Impossible,
    }

    /// <summary>What the hero knows and carries, as the lock check needs it.</summary>
    public readonly struct LockPicker
    {
        public LockPicker(Func<string, int> count, Func<string, bool> knows)
        {
            Count = count;
            Knows = knows;
        }

        public Func<string, int> Count { get; }
        public Func<string, bool> Knows { get; }
    }

    /// <summary>The pure lock decision of G3's <c>CanOpen</c> and the CP 1.75 pick-failure roll of <c>_AI_Unlock</c>.</summary>
    public static class LockCheck
    {
        public static LockVerdict Check(LockRules rules, LockDef lockDef, bool locked, LockPicker hero)
        {
            if (lockDef == null || !locked) return LockVerdict.Open;
            bool hasKey = !string.IsNullOrEmpty(lockDef.Key);
            if (hasKey && hero.Count(lockDef.Key) >= lockDef.KeyAmount) return LockVerdict.Key;
            int d = lockDef.Difficulty;
            if (d < 1) return LockVerdict.NeedKey; // a lock without a picking level only opens with a key
            if (d > rules.MaxPickDifficulty) return hasKey ? LockVerdict.NeedKey : LockVerdict.Impossible;
            string perk = rules.PerkFor(d);
            if (perk != null && !hero.Knows(perk))
                return d == 1 ? LockVerdict.MissingSkill : d == 2 ? LockVerdict.TooHard : LockVerdict.Impossible;
            if (hero.Count(rules.LockpickItem) < 1) return hasKey ? LockVerdict.NeedKeyOrLockpick : LockVerdict.NeedLockpick;
            return LockVerdict.Pick;
        }

        /// <summary>
        /// Chance in percent that a pick of <paramref name="difficulty"/> fails: CP 1.75 fails when the hero does not know
        /// the perk one level up (difficulty 1 without Perk_LockPick_2, 2 without Perk_LockPick_3); the top level never fails.
        /// </summary>
        public static int BreakChance(LockRules rules, int difficulty, Func<string, bool> knows)
        {
            string next = rules.PerkFor(difficulty + 1);
            return difficulty >= 1 && next != null && !knows(next) ? rules.BreakChancePercent : 0;
        }

        /// <summary>Rolls one pick attempt: true = the lock opens.</summary>
        public static bool RollPick(LockRules rules, int difficulty, Func<string, bool> knows, IRandom random) =>
            RollPick(rules, difficulty, knows, random, out _, out _);

        /// <summary>
        /// Rolls one pick attempt like G3 (<c>roll = rand() % 100</c>, the pick fails when <c>roll &gt;= 100 - chance</c>,
        /// so the G3 rule "roll &gt; 70" is a 29 % chance): true = the lock opens. <paramref name="roll"/> is -1 when no roll
        /// was needed (the hero knows the next perk, or the top level).
        /// </summary>
        public static bool RollPick(LockRules rules, int difficulty, Func<string, bool> knows, IRandom random, out int roll, out int chance)
        {
            chance = BreakChance(rules, difficulty, knows);
            roll = -1;
            if (chance <= 0) return true;
            roll = random.Next(100);
            return roll < 100 - chance;
        }
    }
}
