using G3.Unity;
using Myrtana.Core;
using Myrtana.Session;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// Makes the game clock the one time of day: the sky and light (<see cref="G3Environment"/>) show the session clock,
    /// which also drives the NPC routines. When something else moves the environment's time (the [ and ] debug keys of
    /// <see cref="G3Player"/>), the game clock skips forward to that hour, as sleeping does in G3 — time never runs back.
    /// </summary>
    public sealed class EnvironmentClock : MonoBehaviour, ISessionView
    {
        G3Environment _environment;
        GameClock _clock;
        float _shownHour = -1;

        public void Init(G3Environment environment) => _environment = environment;

        public void Bind(GameSession session)
        {
            _clock = session.Clock as GameClock;
            _shownHour = -1;
        }

        void LateUpdate()
        {
            if (_environment == null || _clock == null) return;
            _environment.animate = false;
            if (_shownHour >= 0 && Mathf.Abs(Mathf.DeltaAngle(_environment.timeOfDay * 15f, _shownHour * 15f)) > 0.01f)
                SkipTo(_environment.timeOfDay);
            _shownHour = _clock.HourOfDay;
            _environment.timeOfDay = _shownHour;
        }

        void SkipTo(float hour)
        {
            double hours = Mathf.Repeat(hour - _clock.HourOfDay, 24f);
            _clock.Restore(_clock.GameSeconds + hours * 3600);
        }
    }
}
