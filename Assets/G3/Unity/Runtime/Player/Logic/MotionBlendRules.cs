using System;
using System.Collections.Generic;

namespace G3.Player
{
    /// <summary>Who starts a motion, as G3's motion owners tell them apart (each has its own blend rule).</summary>
    public enum MotionStarter
    {
        /// <summary>An AI motion command: combat moves, hit reactions, falls, get-ups, draw/sheathe, parade, interactions
        /// (sAICombatMoveStart, sAICombatMoveStartRecover, sAICombatMoveItlLoop, sAIPlayAniStart, Entity::StartPlayAni).</summary>
        Command,
        /// <summary>The movement module's locomotion (walk/run/strafe sets) or turn motions.</summary>
        Locomotion,
        /// <summary>The movement module's stand idle entered from any other motion.</summary>
        StandEntry,
        /// <summary>The stand idle picking its next loop or fidget, or following a hand change.</summary>
        StandRepick,
    }

    /// <summary>
    /// How long G3 (CP 1.75) cross-fades from the running motion to a new one (Docs/combat.md §9.4,
    /// Docs/formats-animation.md §2.1). The new motion starts at once at full speed; the old one fades out linearly over
    /// the returned time on top of it (eCWrapper_emfx2Actor::SwitchFadeOut), which is a cross-fade of that length. Nothing
    /// fades when the old motion is no longer running (a one-shot that reached its end): the new motion cuts in, and the
    /// G3 pose system makes the end and start poses match.
    /// <list type="bullet">
    /// <item>The engine's standard fade (eCAnimationAdmin standard ani fade time) is <see cref="StandardFade"/> = 0.2 s.</item>
    /// <item>Per-motion intro/extro blend times would come from <c>G3_Ani_Blendings.txt</c> (a byte in 1/15 s per motion
    /// name); the game ships no such file, so every motion's intro and extro is the standard fade.</item>
    /// <item>A command starts its motion with min(old extro, new intro); a motion with <c>_Begin_</c> in its name with
    /// <see cref="BeginFade"/> = 0.1 s.</item>
    /// <item>The movement module fades with the standard fade into locomotion and turns, and when the idle picks its next
    /// motion; it enters the idle from any other motion with <see cref="StandEntryFade"/> = 0.5 s.</item>
    /// </list>
    /// </summary>
    public static class MotionBlendRules
    {
        /// <summary>The engine's standard fade, s (eCAnimationAdmin, set to 0.2 when the admin is (re)initialised).</summary>
        public const float StandardFade = 0.2f;
        /// <summary>Fade into a motion whose name contains <c>_Begin_</c> started by a command, s.</summary>
        public const float BeginFade = 0.1f;
        /// <summary>Fade from any other motion into the stand idle, s.</summary>
        public const float StandEntryFade = 0.5f;
        /// <summary>Unit of the per-motion blend table <c>G3_Ani_Blendings.txt</c>: one byte = 1/15 s.</summary>
        public const float BlendTableUnit = 1f / 15f;

        /// <summary>
        /// Per-motion intro/extro times (key: motion name without its <c>%</c> suffix, upper case; value in s). Empty in
        /// CP 1.75 (no <c>G3_Ani_Blendings.txt</c> in the data); a mod table can be put here.
        /// </summary>
        public static IReadOnlyDictionary<string, float> IntroTable { get; set; }
        public static IReadOnlyDictionary<string, float> ExtroTable { get; set; }

        /// <summary>Time over which <paramref name="motion"/> blends in, s (its table entry, else the standard fade).</summary>
        public static float IntroOf(string motion) => Lookup(IntroTable, motion);

        /// <summary>Time over which <paramref name="motion"/> blends out when another motion replaces it, s.</summary>
        public static float ExtroOf(string motion) => Lookup(ExtroTable, motion);

        /// <summary>
        /// Cross-fade time when <paramref name="starter"/> starts <paramref name="next"/> while <paramref name="previous"/>
        /// is on (null = nothing). <paramref name="previousRunning"/>: the previous motion still plays (a loop, or a one-shot
        /// before its end); false = it ended, the new motion cuts in (0).
        /// </summary>
        public static float FadeFor(MotionStarter starter, string previous, bool previousRunning, string next)
        {
            if (previous == null || !previousRunning) return 0f;
            switch (starter)
            {
                case MotionStarter.Command:
                    if (next != null && next.IndexOf("_Begin_", StringComparison.OrdinalIgnoreCase) >= 0) return BeginFade;
                    return Math.Min(ExtroOf(previous), IntroOf(next));
                case MotionStarter.StandEntry:
                    return StandEntryFade;
                default:
                    return StandardFade;
            }
        }

        /// <summary>The blend-table key of a motion name: the part before <c>%</c> (its weight/pose/speed suffix), upper case
        /// (G3 cuts the name at its '%' and upper-cases it; which side it keeps is not verified — no table exists).</summary>
        public static string TableKey(string motion)
        {
            if (string.IsNullOrEmpty(motion)) return motion;
            int p = motion.IndexOf('%');
            return (p > 0 ? motion.Substring(0, p) : motion).TrimEnd('_').ToUpperInvariant();
        }

        /// <summary>
        /// fix-76: the phase a new locomotion set starts at. A switch between two looping locomotion sets (the hands change
        /// during a draw or sheathe while running, a stance change from walking to sneaking) keeps the gait cycle's phase:
        /// the hero Move sets of one gait share their leg and head tracks (Hero_Stand_None_None/1H/2H/Fist/Staff/Cast
        /// _P0_Move_Run_N_Fwd are identical below the arms, Tools/G3Tests HeroDrawRunTests), so the legs go on without a
        /// hitch. Starting the new set at 0 cross-faded an out-of-phase cycle for 0.2 s: the head dropped by up to 9.4 cm
        /// and the stride stumbled ("drawing a weapon while running jerks the hero down"). Anything else starts at 0.
        /// [interp: G3's set switch in eCWrapper_emfx2Actor is not traced; continuing the phase is what the shared tracks need]
        /// </summary>
        public static float StartPhase(bool previousIsLocomotionSet, bool previousLoops, bool nextLoops, float previousPhase)
        {
            if (!previousIsLocomotionSet || !previousLoops || !nextLoops || float.IsNaN(previousPhase)) return 0f;
            float p = previousPhase % 1f;
            return p < 0f ? p + 1f : p;
        }

        static float Lookup(IReadOnlyDictionary<string, float> table, string motion)
        {
            if (table == null || table.Count == 0 || string.IsNullOrEmpty(motion)) return StandardFade;
            return table.TryGetValue(TableKey(motion), out float t) ? t : StandardFade;
        }
    }
}
