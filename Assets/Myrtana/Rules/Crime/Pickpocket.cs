using System;
using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// An NPC the hero can pickpocket in a dialog (G3: the NPC's inventory carries treasure sets of use Pickpocket):
    /// the sets its loot comes from.
    /// </summary>
    public sealed class PickpocketDef : IDefinition
    {
        [System.Text.Json.Serialization.JsonConstructor]
        public PickpocketDef(ActorId npc, IReadOnlyList<string> treasureSets)
        {
            Npc = npc;
            TreasureSets = treasureSets ?? Array.Empty<string>();
        }

        public ActorId Npc { get; }
        public IReadOnlyList<string> TreasureSets { get; }

        string IDefinition.Key => Npc.Name;
    }

    /// <summary>Use on a (possibly) helpless NPC (<see cref="PickpocketRules.UseOnHelpless"/>).</summary>
    public enum HelplessUse
    {
        /// <summary>AssessTalk (an awake NPC, or a routine sleeper the hero cannot rob).</summary>
        Talk,
        /// <summary>G3: the sneaking master thief loots it like a body; no crime.</summary>
        Rob,
        /// <summary>[owner] the sleep spell holds it: loot page, each stack taken is a theft.</summary>
        RobAsTheft,
        /// <summary>[owner] the sleep spell holds a beast: neither talk nor loot.</summary>
        Nothing,
    }

    /// <summary>The outcome of a pickpocket attempt.</summary>
    public enum PickpocketVerdict
    {
        /// <summary>The hero got the loot.</summary>
        Success,
        /// <summary>The victim noticed: it attacks the hero (G3 AssessTarget, reason Theft: "DirtyThief", the crime is
        /// memorized) and the dialog ends.</summary>
        Caught,
        /// <summary>Level 30–44 without Perk_PickPocket_2: the hero says "TooHard".</summary>
        TooHard,
        /// <summary>Level 45 or more without Perk_PickPocket_3: the hero says "Impossible".</summary>
        Impossible,
    }

    /// <summary>
    /// One pickpocket attempt and how it ended (published by the session's pickpocket service). fix-248 [port]: the HUD
    /// explains a refusal (G3 only plays the hero's "TooHard" / "Impossible": the missing perk is named with
    /// HUD_MissingSkill) and a failure (G3 only lets the victim shout and attack: "You were caught stealing!").
    /// </summary>
    public readonly struct PickpocketAttempted : IGameEvent
    {
        public PickpocketAttempted(ActorId victim, PickpocketVerdict verdict, string missingPerk)
        {
            Victim = victim;
            Verdict = verdict;
            MissingPerk = missingPerk;
        }

        public ActorId Victim { get; }
        public PickpocketVerdict Verdict { get; }
        /// <summary>The perk a refusal asks for (<see cref="PickpocketRules.MissingPerk"/>); null otherwise.</summary>
        public string MissingPerk { get; }
    }

    /// <summary>
    /// G3 CP 1.75 pickpocketing (script PickPocket, condition type PickPocket; Docs/gameplay-logic.md §7.4,
    /// gameplay-data/items.json "pickpocket"):
    /// <list type="bullet">
    /// <item>offered when the hero has Perk_PickPocket_1 and the NPC has Pickpocket treasure sets and was not robbed yet;</item>
    /// <item>L = min(victim LevelMax, 50); L ≥ 45 needs Perk_PickPocket_3, 30–44 Perk_PickPocket_2;</item>
    /// <item>success iff rand(100) ≤ THF/2 − L + 85; otherwise AssessTarget(victim, hero, Theft): the victim shouts "DirtyThief", attacks
    /// and memorizes the theft (enclave or personal), and the dialog ends;</item>
    /// <item>loot: one random stack per Pickpocket set, a/2 + rand(a/2) units (at least 1); the victim's enclave counts a
    /// theft (AB: not for a master thief); AB: 5·⌊L/7⌋ XP.</item>
    /// </list>
    /// </summary>
    public static class PickpocketRules
    {
        public const string OfferPerk = "Perk_PickPocket_1", HardPerk = "Perk_PickPocket_2", MasterPerk = "Perk_PickPocket_3";
        public const string MasterThiefPerk = "Perk_MasterThief";
        public const string TheftAttribute = "THF";
        public const int LevelCap = 50, HardLevel = 30, MasterLevel = 45, SuccessConstant = 85;
        /// <summary>The hero's shared lines when the victim is too strong.</summary>
        public const string TooHardLine = HeroSpokenLines.TooHard, ImpossibleLine = HeroSpokenLines.Impossible;

        /// <summary>L = min(LevelMax, 50).</summary>
        public static int VictimLevel(int levelMax) => Math.Max(0, Math.Min(levelMax, LevelCap));

        /// <summary>The perk gate: null when the hero may try.</summary>
        public static PickpocketVerdict? Gate(int level, Func<string, bool> knows)
        {
            if (level >= MasterLevel) return knows(MasterPerk) ? (PickpocketVerdict?)null : PickpocketVerdict.Impossible;
            if (level >= HardLevel) return knows(HardPerk) ? (PickpocketVerdict?)null : PickpocketVerdict.TooHard;
            return null;
        }

        /// <summary>The perk a refusal lacks: TooHard → Perk_PickPocket_2, Impossible → Perk_PickPocket_3, else null.</summary>
        public static string MissingPerk(PickpocketVerdict verdict) =>
            verdict == PickpocketVerdict.TooHard ? HardPerk : verdict == PickpocketVerdict.Impossible ? MasterPerk : null;

        /// <summary>Success iff <paramref name="roll"/> (0–99) ≤ THF/2 − L + 85.</summary>
        public static bool Succeeds(int theft, int level, int roll) => roll <= theft / 2 - level + SuccessConstant;

        /// <summary>One attempt: the gate, then the roll.</summary>
        public static PickpocketVerdict Attempt(int levelMax, int theft, Func<string, bool> knows, IRandom random)
        {
            int level = VictimLevel(levelMax);
            if (Gate(level, knows) is PickpocketVerdict refused) return refused;
            return Succeeds(theft, level, random.Next(100)) ? PickpocketVerdict.Success : PickpocketVerdict.Caught;
        }

        /// <summary>
        /// CP 1.75 PS_Normal_Loop, Use on a helpless NPC (AIMode 7: asleep in bed or on the ground, magic sleep): a sneaking
        /// hero with <see cref="MasterThiefPerk"/> loots a humanoid like a body (CleanUpPlunderInv, then the loot page or
        /// "NothingToGet"); otherwise the hero talks to it (AssessTalk). No roll, no crime.
        /// </summary>
        public static bool CanRobHelpless(bool helpless, bool heroSneaking, bool masterThief, bool humanoid) =>
            helpless && heroSneaking && masterThief && humanoid;

        /// <summary>
        /// What Use does on an NPC that may be helpless. G3 (CP 1.75 PS_Normal_Loop, AIMode 7): <see cref="CanRobHelpless"/>
        /// robs without a crime, anything else talks (AssessTalk; G3 has no talk block for sleepers). [owner] fix-113: an NPC
        /// held by the sleep spell is never talked to; Use opens a humanoid's loot page all the same, but then every stack
        /// taken is a theft (as taking an owned item: counted, witnesses react); a sleeping beast offers nothing.
        /// Routine sleepers (bed / ground) keep the G3 rule.
        /// </summary>
        public static HelplessUse UseOnHelpless(bool helpless, bool spellbound, bool heroSneaking, bool masterThief, bool humanoid)
        {
            if (CanRobHelpless(helpless, heroSneaking, masterThief, humanoid)) return HelplessUse.Rob;
            if (helpless && spellbound) return humanoid ? HelplessUse.RobAsTheft : HelplessUse.Nothing;
            return HelplessUse.Talk;
        }

        /// <summary>AB: 5·⌊L/7⌋ experience for a success (G3 GiveXP(L/7)).</summary>
        public static int AlternativeBalancingXp(int levelMax) => 5 * (VictimLevel(levelMax) / 7);

        /// <summary>One random stack of the set, a/2 + rand(a/2) units, at least 1; null for an empty set.</summary>
        public static TreasureStack Loot(TreasureSetDef set, IRandom random, out int amount)
        {
            amount = 0;
            if (set == null || set.Stacks.Count == 0) return null;
            var stack = set.Stacks[random.Next(set.Stacks.Count)];
            int half = stack.Amount / 2;
            amount = Math.Max(1, half + (half > 0 ? random.Next(half) : 0));
            return stack;
        }
    }
}
