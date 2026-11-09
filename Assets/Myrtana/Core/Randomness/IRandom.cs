namespace Myrtana.Core
{
    /// <summary>
    /// Injected source of randomness. fix-119: the game's generator is not restored from a save (like G3's C runtime
    /// <c>rand()</c>): a seed pinned for a test or a scenario makes a run reproducible, a loaded game rolls anew.
    /// </summary>
    public interface IRandom
    {
        /// <summary>0 ≤ result &lt; <paramref name="maxExclusive"/>.</summary>
        int Next(int maxExclusive);
        /// <summary>0 ≤ result &lt; 1.</summary>
        float NextFloat();
        RandomState Save();
        void Load(RandomState state);
    }

    /// <summary>Serializable generator state.</summary>
    public readonly struct RandomState
    {
        public ulong Value { get; }
        public RandomState(ulong value) => Value = value;
    }
}
