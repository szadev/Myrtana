namespace Myrtana.Simulation
{
    /// <summary>
    /// How many users an interactive object takes at once (Docs/interactive-objects.md §9). G3 keeps one user per object
    /// (<c>gCInteraction_PS.User</c>, set by <c>SetCurrentDestinationPoint</c> for an NPC and by the hero's
    /// <c>_AI_Goto_Ext</c>): the several use poses of an anvil, a bed or a grindstone (<c>gCNavOffset_PS.OffsetPose</c>) are
    /// the sides it is used from, not extra places. The campfire is shared: everyone fries meat at it at once, one per
    /// use pose (and the hero joins even when every pose is taken).
    /// </summary>
    public static class ObjectOccupancy
    {
        /// <summary>The interaction of an object several actors use at once.</summary>
        public const string Campfire = "Campfire";

        /// <summary>The object's use poses are separate places (true only for the campfire).</summary>
        public static bool IsShared(string interaction) => interaction == Campfire;
    }
}
