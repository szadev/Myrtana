namespace G3.Player
{
    /// <summary>
    /// fix-133: how often one NPC's cave guard (NpcView.CaveGuard, every 2 s) may act and log. fix-128's guard logged every
    /// rescue: a Near body whose simulated pose lay in the rock was put back there each frame and rescued (and logged) every
    /// 2 s, ~12 times per NPC. Now: a rescue is logged once per <see cref="LogWindow"/>; more than <see cref="MaxRescues"/>
    /// rescues within a window, or <see cref="MaxFailures"/> checks in a row without a true floor to go to, switch the guard
    /// off for that body (one warning) until it is bound again. A successful rescue clears the failures. Pure.
    /// </summary>
    public sealed class CaveGuardBudget
    {
        /// <summary>Window of the rescue count and of the log rate, s.</summary>
        public const float LogWindow = 60f;
        /// <summary>Rescues within one window before the guard gives up (the body keeps being put back).</summary>
        public const int MaxRescues = 4;
        /// <summary>Checks in a row without a true floor within reach before the guard gives up.</summary>
        public const int MaxFailures = 3;

        public enum Log
        {
            /// <summary>Act quietly.</summary>
            None = 0,
            /// <summary>Log this one (the first in its window).</summary>
            Once = 1,
            /// <summary>The guard gives up for this body now: warn once.</summary>
            GiveUp = 2,
        }

        double _windowStart = double.NegativeInfinity;
        int _rescues, _failures;
        bool _loggedFailure;

        /// <summary>The guard still checks this body.</summary>
        public bool Active { get; private set; } = true;

        /// <summary>Rescues in the current window.</summary>
        public int Rescues => _rescues;
        /// <summary>Failed checks in a row.</summary>
        public int Failures => _failures;

        public void Reset()
        {
            _windowStart = double.NegativeInfinity;
            _rescues = _failures = 0;
            _loggedFailure = false;
            Active = true;
        }

        /// <summary>The body was moved onto a true floor at <paramref name="now"/> (s).</summary>
        public Log Rescued(double now)
        {
            if (!Active) return Log.None;
            _failures = 0;
            _loggedFailure = false;
            if (now - _windowStart > LogWindow)
            {
                _windowStart = now;
                _rescues = 0;
            }
            _rescues++;
            if (_rescues > MaxRescues)
            {
                Active = false;
                return Log.GiveUp;
            }
            return _rescues == 1 ? Log.Once : Log.None;
        }

        /// <summary>The body stands on a roof top / in the rock and no true floor is within reach.</summary>
        public Log Failed()
        {
            if (!Active) return Log.None;
            _failures++;
            if (_failures >= MaxFailures)
            {
                Active = false;
                return Log.GiveUp;
            }
            if (_loggedFailure) return Log.None;
            _loggedFailure = true;
            return Log.Once;
        }
    }
}
