using System;

namespace Myrtana.Rules
{
    /// <summary>
    /// Button presses and releases are timed by their input events, not by the frame that reads them: G3 measures the
    /// real press duration (DurationPressedMSecs), so at a low frame rate an 80 ms tap stays a tap instead of crossing the
    /// 100 ms hold threshold between two frames. The event time (input clock, real seconds) is moved onto the game clock
    /// the melee input runs on.
    /// </summary>
    public static class InputEventTime
    {
        /// <summary>
        /// The game time of an input event: <paramref name="gameNow"/> (game clock now) minus the event's age on the input
        /// clock (<paramref name="inputNow"/> - <paramref name="eventTime"/>), scaled by <paramref name="timeScale"/>.
        /// An event from the future or without a stamp (eventTime &lt;= 0) counts as now; never earlier than
        /// <paramref name="notBefore"/> (the previous frame: the event was read in this one).
        /// </summary>
        public static double ToGameTime(double gameNow, double inputNow, double eventTime, double timeScale = 1, double notBefore = double.NegativeInfinity)
        {
            if (eventTime <= 0) return gameNow;
            double age = Math.Max(0, inputNow - eventTime) * Math.Max(0, timeScale);
            return Math.Max(gameNow - age, Math.Min(notBefore, gameNow));
        }
    }
}
