using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// fix-115: one log line per start-up step with the seconds since the player started
    /// (<see cref="Time.realtimeSinceStartupAsDouble"/>): splash, logo prepare and play, content load, game build, title.
    /// Lets the owner's Player.log show where the time between the Unity splash and the title goes
    /// (Docs/game-start.md "Start-up timeline").
    /// </summary>
    public static class StartupTimeline
    {
        static bool _splashLogged, _over;

        /// <summary>Logs a step; nothing after the <paramref name="last"/> one (the title), so later videos stay quiet.</summary>
        public static void Mark(string what, bool last = false)
        {
            if (_over) return;
            _over = last;
            Debug.Log($"[Myrtana] start-up {Time.realtimeSinceStartupAsDouble:0.00} s: {what}");
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
        static void BeforeSplash()
        {
            _splashLogged = _over = false;
            Mark("before the Unity splash");
        }

        /// <summary>Logs the end of the Unity splash once (polled by the start-up video).</summary>
        public static void PollSplash()
        {
            if (_splashLogged || !(Application.isEditor || UnityEngine.Rendering.SplashScreen.isFinished)) return;
            _splashLogged = true;
            Mark("Unity splash finished");
        }
    }
}
