namespace Myrtana.Core
{
    /// <summary>Game time. Injected everywhere instead of reading a global timer, so tests and saves are deterministic.</summary>
    public interface IGameClock
    {
        /// <summary>Game seconds since the start of the game.</summary>
        double GameSeconds { get; }
        /// <summary>0 ≤ hour &lt; 24.</summary>
        float HourOfDay { get; }
        /// <summary>Day number, starting at 0.</summary>
        int Day { get; }
        /// <summary>Game seconds per real second.</summary>
        float TimeScale { get; set; }
    }
}
