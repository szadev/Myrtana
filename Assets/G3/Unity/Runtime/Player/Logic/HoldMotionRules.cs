using System;

namespace G3.Player
{
    /// <summary>The two halves of a G3 draw/sheathe motion pair (<c>…_Hold*_Begin_…</c>, <c>…_Hold*_End_…</c>).</summary>
    public enum HoldPhase { Begin, End }

    /// <summary>One motion of a draw or sheathe: which half, played backwards or not, and whether the hand changes after it.</summary>
    public readonly struct HoldPart
    {
        public readonly HoldPhase Phase;
        public readonly bool Reverse;
        /// <summary>The items change hands (G3 <c>HoldStacks</c>) when this motion ends.</summary>
        public readonly bool SwitchAfter;

        public HoldPart(HoldPhase phase, bool reverse, bool switchAfter)
        {
            Phase = phase;
            Reverse = reverse;
            SwitchAfter = switchAfter;
        }

        /// <summary>The motion-name phase word ("Begin", "End").</summary>
        public string Word => Phase == HoldPhase.Begin ? "Begin" : "End";

        public override string ToString() => $"{Word}{(Reverse ? " reversed" : "")}{(SwitchAfter ? ", then the hand changes" : "")}";
    }

    /// <summary>
    /// How G3 (CP 1.75) draws and sheathes a hand item (Script_Game <c>_AI_HoldInventoryItems_Base</c> and its left/right
    /// variants; Docs/combat.md §9.5). One command plays two motions around <c>PSInventory::HoldStacks</c>:
    /// <list type="bullet">
    /// <item>Draw: <c>Hold*_Begin</c> forwards (the hand reaches for the weapon on the back or hip; it is still on its carry
    /// slot), <c>HoldStacks</c> (the weapon is in the hand), <c>Hold*_End</c> forwards (the weapon comes to the front).</item>
    /// <item>Sheathe: <c>Hold*_End</c> backwards (the weapon goes back over the shoulder), <c>HoldStacks</c> (onto its carry
    /// slot), <c>Hold*_Begin</c> backwards (the empty hand returns).</item>
    /// </list>
    /// Each motion is the overlay variant (<c>_O_</c>, upper body: the legs keep walking) when the body moves as it starts,
    /// else the full-body variant (<c>_N_</c>; G3 movement follows the motion, so the hero stands while it plays).
    /// Overlay fades (<c>sAIPlayAniStart</c>): the first motion fades in 0.1 s, the last fades out 0.1 s, none between.
    /// </summary>
    public static class HoldMotionRules
    {
        /// <summary>Fade of an overlay hold motion's outer edge, s.</summary>
        public const float OverlayEdgeFade = 0.1f;
        /// <summary>
        /// Planar speed above which the body counts as moving, m/s (G3: any velocity ≠ 0; a small threshold keeps
        /// collision jitter of a standing body out [interp]).
        /// </summary>
        public const float MovingSpeed = 0.05f;

        static readonly HoldPart[] DrawParts = { new HoldPart(HoldPhase.Begin, false, true), new HoldPart(HoldPhase.End, false, false) };
        static readonly HoldPart[] SheatheParts = { new HoldPart(HoldPhase.End, true, true), new HoldPart(HoldPhase.Begin, true, false) };

        /// <summary>The motions of a draw (<paramref name="draw"/>) or sheathe, in playing order.</summary>
        public static HoldPart[] Parts(bool draw) => draw ? DrawParts : SheatheParts;

        /// <summary>The overlay (<c>_O_</c>) variant is used: the body moves as the motion starts (G3 FUN_100031a0).</summary>
        public static bool UseOverlay(float planarSpeed) => planarSpeed > MovingSpeed;

        /// <summary>Fade-in of an overlay hold motion, s: the opening motion of the pair (Begin forwards, End backwards).</summary>
        public static float OverlayIntro(HoldPhase phase, bool reverse) => (phase == HoldPhase.Begin) != reverse ? OverlayEdgeFade : 0f;

        /// <summary>Fade-out of an overlay hold motion, s: the closing motion of the pair (End forwards, Begin backwards).</summary>
        public static float OverlayExtro(HoldPhase phase, bool reverse) => (phase == HoldPhase.End) != reverse ? OverlayEdgeFade : 0f;

        /// <summary>
        /// fix-105: a take or put-away overlay still running this long (s) is given up and its hand change done at once. Longer
        /// than every G3 hold pair (1H, 2H, bow pairs 0.76 s; the torch HoldLeft pairs: Tools/G3Tests
        /// HeroTorchHoldTests), so it only ends one whose motion never started or never reports its end (the owner's
        /// Demo_2026-10-18c: the overlay threw and the torch keys did nothing for the rest of the game).
        /// </summary>
        public const float OverlayGiveUpSeconds = 3f;

        /// <summary>Whether an overlay started at <paramref name="started"/> is stuck at <paramref name="now"/> (s, same clock).</summary>
        public static bool OverlayStuck(float started, float now) => now - started > OverlayGiveUpSeconds;

        /// <summary>
        /// Where in the whole command the hand changes (0..1 of its time): after the first motion. A missing first motion
        /// changes the hand at once (0); a missing second one at the end (1).
        /// </summary>
        public static float SwitchFraction(float firstLength, float secondLength)
        {
            firstLength = Math.Max(0f, firstLength);
            secondLength = Math.Max(0f, secondLength);
            float total = firstLength + secondLength;
            return total <= 0f ? 1f : firstLength / total;
        }
    }
}
