using System;
using System.Collections.Generic;

namespace Myrtana.Rules
{
    /// <summary>What Open Locks does to a focused object (G3 MagicOpenLocks).</summary>
    public enum LockSpellOutcome
    {
        /// <summary>The lock opens.</summary>
        Opens,
        /// <summary>No lock, or it is not locked: the caster says "DoesntWork".</summary>
        DoesntWork,
        /// <summary>Too hard (difficulty 5 or more) or a key-only lock: the caster says "Impossible".</summary>
        Impossible,
    }

    /// <summary>
    /// The numbers and decisions of the CP 1.75 spells that act on NPCs, the world or the hero's body (Docs/combat.md §12.5;
    /// gameplay-data/magic.json). Pure. Values marked [interp] are not in Script_Game.dll
    /// (engine or template data) and were chosen to match what the player sees.
    /// </summary>
    public static class SpellEffectRules
    {
        // ------------------------------------------------------------------------------------------- sleep, hypnosis

        /// <summary>ZS_MagicSleep_Loop: an NPC wakes after 30 s (task time, real s), the player after 5 s.</summary>
        public const float NpcSleepSeconds = 30f, PlayerSleepSeconds = 5f;
        /// <summary>MagicHypnosis: every NPC nearer than 2000 to the caster (sorted, stops at the first beyond).</summary>
        public const float HypnosisRadius = 2000f;
        /// <summary>The voice line of an NPC that wakes from magic sleep (then ZS_FetchWeapon).</summary>
        public const string WakeVoice = "WhatWasThat";

        /// <summary>G3 CanSleep: humanoids and the ordinary beasts; undead, golems, demons, the big ones and insects never sleep.</summary>
        public static readonly HashSet<string> SleepingSpecies = new HashSet<string>(StringComparer.Ordinal)
        {
            "Human", "Orc", "Goblin", "Scavenger", "Wolf", "Boar", "Sabertooth", "Shadowbeast", "Bison", "Rhino", "Ripper", "Lurker",
            "Varan", "Snapper", "Alligator", "FireVaran", "Pig", "Cow", "SwampLurker", "Ogre", "Stalker",
        };

        public static bool CanSleep(string species) => species != null && SleepingSpecies.Contains(species);

        // ------------------------------------------------------------------------------------------- bloodlust, call animals

        /// <summary>MagicBloodlust: the victim attacks the nearest NPC within 1000 of it (it may be the caster).</summary>
        public const float BloodlustRadius = 1000f;
        /// <summary>MagicCallAnimals: world animals within 25000 of the caster are called.</summary>
        public const float CallAnimalsRadius = 25000f;

        /// <summary>MagicCallAnimals: 3 animals at INT &gt; 300, 2 at INT &gt; 200, else 1.</summary>
        public static int CallAnimalsCount(int intelligence) => intelligence > 300 ? 3 : intelligence > 200 ? 2 : 1;

        /// <summary>G3 CanAttack: game animals that never attack (and ambient creatures) are not called.</summary>
        public static readonly HashSet<string> PeacefulAnimals = new HashSet<string>(StringComparer.Ordinal) { "Deer", "Pig", "Cow" };

        // ------------------------------------------------------------------------------------------- mist

        /// <summary>ZS_Mist: radius 1000, a tick every second; the effect stops 5 s before the end.</summary>
        public const float MistRadius = 1000f, MistTickSeconds = 1f, MistEffectEndsEarlySeconds = 5f;

        /// <summary>The mist object's effect (Smn_Mist), shown where it was cast until 5 s before its end.</summary>
        public const string MistEffect = "eff_magic_mist_cast_01";
        /// <summary>The time bubble object's effect (Smn_Timebubble).</summary>
        public const string TimeBubbleEffect = "eff_magic_timebubble_target_01";
        /// <summary>Spl_Light's light ball that follows the caster (Smn_Light; g3.efm: glow, point light, sphere).</summary>
        public const string LightSpell = "Spl_Light", LightSourceEffect = "eff_magic_light_source_01";

        /// <summary>ZS_Mist: 20 s, plus GetPlayerSpellDurationBonus(10) = +5 s with the deity perk (player only).</summary>
        public static float MistSeconds(bool deityPerk) => 20f + (deityPerk ? 5f : 0f);

        // ------------------------------------------------------------------------------------------- time bubble

        /// <summary>ZS_Timebubble: (8 + rand(4)) × 10 s = 80–110 s, +50 % with the player's deity perk.</summary>
        public static float TimeBubbleSeconds(int roll0To3, bool deityPerk) => (8 + Math.Max(0, Math.Min(3, roll0To3))) * 10f * (deityPerk ? 1.5f : 1f);

        /// <summary>
        /// [interp] The bubble's slowing is engine/template data (MagicTimeBubble is a stub; CP's teleport resets a time scale
        /// to 1): everyone but the caster and his party within 1000 of the bubble moves at half speed.
        /// </summary>
        public const float TimeBubbleRadius = 1000f, TimeBubbleScale = 0.5f;

        // ------------------------------------------------------------------------------------------- flame sword

        /// <summary>PS_Magic_FlameSword: the conjured sword, created with the Burning quality and held in the right hand.</summary>
        public const string FlameSwordItem = "It_1H_MagicFlameSword_01";
        public const int FlameSwordQuality = (int)WeaponQuality.Burning;

        // ------------------------------------------------------------------------------------------- telekinesis, open locks

        /// <summary>MagicTelekinesis: an item hovers to the caster's position + forward·250 + 150 up, at 220 per second.</summary>
        public const float TelekinesisForward = 250f, TelekinesisUp = 150f, TelekinesisSpeed = 220f;
        /// <summary>The focus range of Telekinesis and Open Locks (PS_Magic_Loop).</summary>
        public const float ObjectFocusRange = 1000f;

        /// <summary>
        /// MagicOpenLocks: no lock or not locked → DoesntWork; difficulty 0 with a key → refused; difficulty below 5 → unlocked;
        /// otherwise "Impossible".
        /// </summary>
        public static LockSpellOutcome OpenLocks(bool hasLock, bool locked, int difficulty, bool hasKey)
        {
            if (!hasLock || !locked) return LockSpellOutcome.DoesntWork;
            if (difficulty < 1 && hasKey) return LockSpellOutcome.Impossible;
            return difficulty < 5 ? LockSpellOutcome.Opens : LockSpellOutcome.Impossible;
        }

        // ------------------------------------------------------------------------------------------- dusk to dawn

        /// <summary>PS_DuskToDawn: the world clock runs at 3840 (320 × the normal 12) with the engine time scale 0.3 until 08:00.</summary>
        public const float DuskToDawnClockFactor = 3840f, DuskToDawnEngineTimeScale = 0.3f;
        public const double DuskToDawnEndHour = 8.0;

        /// <summary>_Loop: the night is over once the clock shows 08:00 (and it is still day, before 20:00).</summary>
        public static bool DuskToDawnOver(double hour) => hour >= DuskToDawnEndHour && hour < 20.0;

        // ------------------------------------------------------------------------------------------- soul travel

        /// <summary>MagicSoulTravel: the ghost PC_Hero_Ghost, alpha 0.2, time scale 0.55, unlimited.</summary>
        public const string GhostTemplate = "PC_Hero_Ghost";
        public const float GhostAlpha = 0.2f, GhostTimeScale = 0.55f;

        // ------------------------------------------------------------------------------------------- weapons from the inventory

        /// <summary>OnPlayerUseStack: bless with the scroll (used up) or the learned spell (100 MP).</summary>
        public const string BlessScroll = "It_Scroll_BlessWeapon", BlessSpellItem = "It_Spell_BlessWeapon", BlessSpell = "Spl_BlessWeapon";
        public const int BlessMana = 100;
        /// <summary>The poison potion; opening its page needs Perk_PoisonBlade.</summary>
        public const string PoisonPotion = "It_Potion_Poison", PoisonPerk = "Perk_PoisonBlade";
        /// <summary>CP: one potion poisons as many arrows as the poison arrow recipe makes.</summary>
        public const string PoisonArrowRecipe = "It_Recipe_PoisonArrow";
        public const string BlessEffect = "eff_inv_bless_01", PoisonEffect = "eff_inv_poison_01";
    }
}
