namespace Myrtana.Unity
{
    /// <summary>
    /// The combat side of a quick-slot key (G3: a weapon, bow or spell in a quick slot is readied, not just put on):
    /// <see cref="QuickSlotInput"/> asks it first; items it does not take go to the inventory's use.
    /// </summary>
    public interface IQuickSlotReadiness
    {
        /// <summary>Readies <paramref name="item"/> (or puts it away when it is in the hand); false when it is no weapon or spell.</summary>
        bool TryReady(string item);
    }
}
