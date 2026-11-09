using System.Collections.Generic;

namespace Myrtana.Rules
{
    /// <summary>
    /// The hero's own shared lines (<c>SVM_Player_&lt;line&gt;</c>) the port says outside the dialog scripts: refusals of
    /// object uses, locks and pickpocketing, crafting stations. The line names of the content rules (boulders, object
    /// uses, bookstands) come from the content; these are the ones the code names. fix-235: each has a subtitle (CP 1.75
    /// dropped the texts of most of them; <c>PatchStrings</c> carries G3's English and German and our Russian).
    /// </summary>
    public static class HeroSpokenLines
    {
        /// <summary>Numbered variants (<c>_00</c> … ) picked at random (G3 StartSayDoesntWork / StartSayNothingToGet).</summary>
        public const string DoesntWork = "DoesntWork", NothingToGet = "NothingToGet";
        /// <summary>Lock refusals (LockVerdict names) and the broken lockpick.</summary>
        public const string NeedKey = "NeedKey", NeedLockpick = "NeedLockpick", NeedKeyOrLockpick = "NeedKeyOrLockpick",
                            TooHard = "TooHard", Impossible = "Impossible", PickBroke = "PickBroke";
        /// <summary>Crafting station refusals.</summary>
        public const string MissingIngredients = "MissingIngredients", NotEnoughLP = "NotEnoughLP";

        /// <summary>The lines said as named.</summary>
        public static IReadOnlyList<string> Plain { get; } = new[]
        {
            NeedKey, NeedLockpick, NeedKeyOrLockpick, TooHard, Impossible, PickBroke, MissingIngredients, NotEnoughLP,
        };

        /// <summary>The lines said as one of their numbered variants.</summary>
        public static IReadOnlyList<string> Numbered { get; } = new[] { DoesntWork, NothingToGet };
    }
}
