using System;
using System.Collections.Generic;

namespace Myrtana.Rules
{
    /// <summary>What a fear, control or taming spell does to its target.</summary>
    public enum ControlOutcome
    {
        /// <summary>The spell takes (the target flees / obeys / is tamed).</summary>
        Works,
        /// <summary>The target resists and attacks the caster (G3 AssessTarget, reason ReactToDamage).</summary>
        AttacksCaster,
        /// <summary>The caster says "DoesntWork" (wrong target state).</summary>
        DoesntWork,
        /// <summary>The caster says "Impossible" (boss, mage, not an animal …).</summary>
        Impossible,
        /// <summary>The caster says "TooHard" (not enough INT).</summary>
        TooHard,
    }

    /// <summary>The target facts the control spells look at.</summary>
    public struct ControlTarget
    {
        public bool IsPlayer;
        public string Species;
        public bool Evil;
        public bool Boss;
        public bool Mage;
        public bool GateGuard;
        public bool Animal;
        public bool InCombat;
        /// <summary>G3 AIMode is Routine, Reaction or magic-paralysed (asleep, frozen).</summary>
        public bool CalmOrParalysed;
    }

    /// <summary>
    /// Fear, Word of Control, Tame Animal and Charm as G3 CP 1.75 decides them (Docs/combat.md §12.3,
    /// gameplay-data/magic.json "control"; B_part_magic.md behaviour notes). Pure.
    /// </summary>
    public static class ControlSpells
    {
        public const int ControlOrcIntelligence = 200, ControlGateGuardIntelligence = 200, TameStrongIntelligence = 100;

        /// <summary>G3 IsBig (Troll, T-Rex, Dragon).</summary>
        public static readonly HashSet<string> BigSpecies = new HashSet<string>(StringComparer.Ordinal) { "Troll", "Trex", "Dragon" };

        static readonly HashSet<string> Controllable = new HashSet<string>(StringComparer.Ordinal) { "Human", "Orc", "Goblin" };
        static readonly HashSet<string> StrongAnimals = new HashSet<string>(StringComparer.Ordinal)
            { "Minecrawler", "Sabertooth", "Shadowbeast", "Ripper", "Alligator" };

        /// <summary>MagicFear: not the player, not big, not evil, not a boss, not a mage → flees; otherwise attacks the caster.</summary>
        public static ControlOutcome Fear(in ControlTarget t) =>
            t.IsPlayer || (t.Species != null && BigSpecies.Contains(t.Species)) || t.Evil || t.Boss || t.Mage
                ? ControlOutcome.AttacksCaster : ControlOutcome.Works;

        /// <summary>
        /// CanControl (Word of Control): player caster only; target calm or paralysed (else DoesntWork); bosses and mages
        /// "Impossible"; gate guards and orcs need INT 200 ("TooHard"); only humans, orcs and goblins.
        /// </summary>
        public static ControlOutcome WordOfControl(in ControlTarget t, bool casterIsPlayer, int casterIntelligence)
        {
            if (!casterIsPlayer || !t.CalmOrParalysed) return ControlOutcome.DoesntWork;
            if (t.Boss || t.Mage) return ControlOutcome.Impossible;
            if (t.GateGuard && casterIntelligence < ControlGateGuardIntelligence) return ControlOutcome.TooHard;
            if (t.Species == "Orc" && casterIntelligence < ControlOrcIntelligence) return ControlOutcome.TooHard;
            return t.Species != null && Controllable.Contains(t.Species) ? ControlOutcome.Works : ControlOutcome.Impossible;
        }

        /// <summary>CanTameAnimal: an animal ("Impossible" otherwise); the strong ones need INT 100 ("TooHard").</summary>
        public static ControlOutcome TameAnimal(in ControlTarget t, int casterIntelligence)
        {
            if (!t.Animal) return ControlOutcome.Impossible;
            if (t.Species != null && StrongAnimals.Contains(t.Species) && casterIntelligence < TameStrongIntelligence) return ControlOutcome.TooHard;
            return ControlOutcome.Works;
        }

        /// <summary>MagicCharm (CP): a target in combat → DoesntWork; a boss attacks the caster; guards can be charmed now.</summary>
        public static ControlOutcome Charm(in ControlTarget t)
        {
            if (t.InCombat) return ControlOutcome.DoesntWork;
            return t.Boss ? ControlOutcome.AttacksCaster : ControlOutcome.Works;
        }
    }
}
