using System;

namespace G3.Player
{
    /// <summary>How a landing is shown (Script_Game OnMovementModeChanged → ZS_Land / ZS_HeavyLand / ZS_Acrobat).</summary>
    public enum LandingKind
    {
        /// <summary>ZS_Land: <c>Fall_End</c> (or straight on when moving). No damage.</summary>
        Soft,
        /// <summary>ZS_HeavyLand: <c>Fall_Recover</c> (deep crouch) and, without the perk, the pain cry SVM AARGH_03.</summary>
        Heavy,
        /// <summary>ZS_Acrobat: combat move "Fall" = <c>Fall_Hit</c> (forward roll, 430 cm), damage halved.</summary>
        Acrobatic,
        /// <summary>Landed knocked out: the damage is taken, the down state is kept (no landing motion).</summary>
        KeepState,
    }

    /// <summary>Result of one landing: hit points lost, death, and the landing shown.</summary>
    public readonly struct FallOutcome
    {
        public FallOutcome(int damage, bool killed, LandingKind kind)
        {
            Damage = damage;
            Killed = killed;
            Kind = kind;
        }

        public int Damage { get; }
        public bool Killed { get; }
        public LandingKind Kind { get; }
        public bool PainCry => Kind == LandingKind.Heavy;
    }

    /// <summary>
    /// G3 CP 1.75 fall damage (Script_Game OnMovementModeChanged, read for understanding only; Docs/gameplay-logic.md
    /// §3.10, Docs/hero.md "Falling"). t = seconds in the FALL movement mode:
    /// <c>damage = floor(160·(t − 0.9)²)</c> when t &gt; 0.9 s, none in water (wade or swim). Perk_Acrobatic halves it and rolls
    /// (not when knocked out); with the perk and Alternative Balancing off the fall cannot kill. Otherwise HP &lt; 1 kills
    /// (ZS_RagDollDead) when the character can be killed. Immune: ghosts, bloodflies, the god cheat.
    /// </summary>
    public static class FallRules
    {
        /// <summary>No damage up to this fall time, s.</summary>
        public const float DamageStartSeconds = 0.9f;
        public const float DamageFactor = 160f;
        /// <summary>
        /// The acrobatic roll's travel, m (<c>Fall_Hit_N_Fwd_00</c>: 430 cm in the motion's data, Docs/hero.md), spread
        /// over the whole motion like every combat move.
        /// </summary>
        public const float RollDistance = 4.3f;

        /// <summary>
        /// Metres of the roll travelled between two normalised motion times (G3 moves combat motions evenly over the
        /// motion, as the combat lunges): <paramref name="distance"/> · (to − from), never backwards.
        /// </summary>
        public static float RollStep(float fromPhase, float toPhase, float distance = RollDistance)
        {
            if (toPhase > 1f) toPhase = 1f;
            if (fromPhase < 0f) fromPhase = 0f;
            return toPhase > fromPhase ? distance * (toPhase - fromPhase) : 0f;
        }
        /// <summary>A fall that lasted longer than this into water teaches Perk_Acrobatic (CP, player only).</summary>
        public const float AcrobaticTeachSeconds = SwimRules.AcrobaticFallSeconds;

        /// <summary>Fall damage before perks: floor(160·(t − 0.9)²) for t &gt; 0.9 s.</summary>
        public static int Damage(float fallSeconds)
        {
            if (!(fallSeconds > DamageStartSeconds)) return 0;
            double x = fallSeconds - DamageStartSeconds;
            return (int)Math.Floor(DamageFactor * x * x + 1e-4); // tolerance for float seconds (1.4 s → 40, not 39)
        }

        /// <summary>Shortest fall (s) that does at least <paramref name="damage"/> points (for tests and tools).</summary>
        public static float SecondsFor(int damage) =>
            damage <= 0 ? 0 : DamageStartSeconds + (float)Math.Sqrt(damage / (double)DamageFactor);

        /// <summary>The landing after a fall of <paramref name="fallSeconds"/>.</summary>
        /// <param name="water">Water mode at the landing: wading or swimming takes no damage (swimming is a dive-in).</param>
        /// <param name="hitPoints">Current hit points (before the damage).</param>
        public static FallOutcome Land(float fallSeconds, WaterMode water, bool acrobatic, bool knockedOut,
            bool alternativeBalancing, int hitPoints, bool canBeKilled = true, bool immune = false)
        {
            if (immune || water == WaterMode.Wade || water == WaterMode.Swim) return new FallOutcome(0, false, LandingKind.Soft);
            int damage = Damage(fallSeconds);
            if (damage <= 0) return new FallOutcome(0, false, knockedOut ? LandingKind.KeepState : LandingKind.Soft);
            bool rolls = acrobatic && !knockedOut;
            if (rolls) damage /= 2;
            bool deathCheck = !(acrobatic && !alternativeBalancing);
            bool killed = deathCheck && canBeKilled && hitPoints - damage < 1;
            var kind = knockedOut ? LandingKind.KeepState : rolls ? LandingKind.Acrobatic : LandingKind.Heavy;
            return new FallOutcome(damage, killed, kind);
        }

        /// <summary>The landing motion without game state (no perk, not knocked out): Heavy when the fall hurts.</summary>
        public static LandingKind DefaultKind(float fallSeconds) => Damage(fallSeconds) > 0 ? LandingKind.Heavy : LandingKind.Soft;
    }
}
