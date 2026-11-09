using System;

namespace Myrtana.Core
{
    /// <summary>
    /// Game clock advanced by the session tick. The G3 game-time speed is still unverified
    /// (gameplay-architecture.md §13 Q5), so <see cref="TimeScale"/> is a setting, not a constant.
    /// </summary>
    public sealed class GameClock : IGameClock
    {
        const double SecondsPerDay = 24 * 3600;

        readonly double _startSecondOfDay;

        public GameClock(float startHour = 8f, float timeScale = 1f)
        {
            _startSecondOfDay = startHour * 3600.0;
            TimeScale = timeScale;
        }

        public double GameSeconds { get; private set; }
        public float TimeScale { get; set; }

        double TotalSeconds => _startSecondOfDay + GameSeconds;
        public int Day => (int)Math.Floor(TotalSeconds / SecondsPerDay);
        public float HourOfDay => (float)(TotalSeconds % SecondsPerDay / 3600.0);

        public void Advance(float realSeconds)
        {
            if (realSeconds > 0) GameSeconds += realSeconds * TimeScale;
        }

        /// <summary>Jumps forward by game time (sleeping, waiting); ignores non-positive values.</summary>
        public void Skip(double gameSeconds)
        {
            if (gameSeconds > 0) GameSeconds += gameSeconds;
        }

        /// <summary>Game seconds from now until the next time the clock shows <paramref name="hour"/> (a full day when it is that hour now).</summary>
        public double SecondsUntilHour(float hour)
        {
            double now = TotalSeconds % SecondsPerDay, target = ((hour % 24 + 24) % 24) * 3600.0;
            double wait = target - now;
            return wait > 0 ? wait : wait + SecondsPerDay;
        }

        /// <summary>Sets the time from a save game.</summary>
        public void Restore(double gameSeconds) => GameSeconds = Math.Max(0, gameSeconds);
    }
}
