namespace Myrtana.Rules
{
    /// <summary>
    /// How long G3's block stays up (CP 1.75 Script_Game, fix-43). The parade is an ani state, not a move: <c>_AI_Parade</c>
    /// sets gEAniState Parade (4) and the player's block flag (DAT_10118ab0); only the release of the secondary key
    /// (OnPlayerSecondaryAction → FUN_10001d90) or the CP extended-AI drop clears them. A parried hit plays
    /// <c>ZS_ParadeStumble</c> / <c>ZS_HeavyParadeStumble</c>, whose <c>_AI_ParadeStumble</c> / <c>_AI_HeavyParadeStumble</c>
    /// set the ani state Parade again: the block is still up during and after the stumble, so a held button keeps blocking
    /// hit after hit (IsInParadeMode = ani state Parade + parade pose; the stumbles end in that pose). An unparried hit
    /// (Stumble, knock-down …) leaves the parade.
    /// </summary>
    public static class ParadeHold
    {
        /// <summary>The reaction to a hit taken in the parade keeps the block up (it was parried).</summary>
        public static bool KeepsParade(bool inParade, HitReaction reaction) =>
            inParade && (reaction == HitReaction.ParadeStumble || reaction == HitReaction.HeavyParadeStumble);
    }
}
