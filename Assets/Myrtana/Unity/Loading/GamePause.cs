using Myrtana.Core;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// The one owner of <see cref="Time.timeScale"/>: the game menu, the tutorial box, the intro and the loading hold each
    /// pause on their own behalf and the scale comes back only when the last of them lets go (before, each saved and
    /// restored the scale itself, so a load from the menu or an intro over the loading screen restored a 0 and froze the
    /// game). While paused the scale stays 0 (<see cref="Guard"/>, every frame from the bootstrap) and the session does
    /// not tick, so AI, combat, projectiles, effects, NPC movement and timers stand still (G3 gCSession::Pause).
    /// Nothing else writes <see cref="Time.timeScale"/>; a dev step that wants normal speed calls <see cref="NormalSpeed"/>.
    /// </summary>
    public static class GamePause
    {
        static readonly PauseScale State = new PauseScale();
        static bool s_warned;

        public static bool IsPaused => State.IsPaused;

        public static void Set(object owner, bool pause)
        {
            if (State.Set(owner, pause, Time.timeScale) is float scale) Time.timeScale = scale;
        }

        /// <summary>Once per frame: keeps the scale at 0 while paused, whoever wrote it (logged once).</summary>
        public static void Guard()
        {
            if (!(State.Guard(Time.timeScale) is float scale)) return;
            if (!s_warned)
            {
                s_warned = true;
                Debug.LogWarning($"[Myrtana] Time.timeScale was set to {Time.timeScale} while the game is paused; back to 0 (GamePause owns the scale)");
            }
            Time.timeScale = scale;
        }

        /// <summary>Normal speed: at once when not paused, else after the last pause owner lets go.</summary>
        public static void NormalSpeed()
        {
            if (State.NormalSpeed() is float scale) Time.timeScale = scale;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()   // domain reload is off in the editor
        {
            if (State.Clear() is float scale) Time.timeScale = scale;
            s_warned = false;
        }
    }
}
