using System;

namespace Myrtana.Rules
{
    /// <summary>
    /// Side effects G3 CP 1.75 ties to one story alignment change (info command SetPAL) [fix-94]. Only one exists:
    /// SetPAL Reb on Wenzel (the Kap Dun massacre, quest Wenzel_KapDunMetzel) also makes him a Friend-type NPC, takes him
    /// out of his enclave and sets the enclave Kap Dun's Raid flag, so he fights on the hero's side and the town's
    /// unwilling members (hostile to its orc alignment) stand with the hero while Kap Dun is raided.
    /// </summary>
    public static class StoryAlignmentRules
    {
        public const string WenzelNpc = "Wenzel";
        public const string WenzelAlignment = "Reb";
        public const string WenzelRaidEnclave = "KapDun";

        /// <summary>The NPC changed by SetPAL to <paramref name="alignment"/> becomes a Friend NPC without an enclave.</summary>
        public static bool LeavesEnclaveAsFriend(string npc, string alignment) =>
            string.Equals(npc, WenzelNpc, StringComparison.Ordinal) && string.Equals(alignment, WenzelAlignment, StringComparison.Ordinal);

        /// <summary>The enclave whose Raid flag the change sets, or null.</summary>
        public static string RaidEnclaveOf(string npc, string alignment) => LeavesEnclaveAsFriend(npc, alignment) ? WenzelRaidEnclave : null;
    }
}
