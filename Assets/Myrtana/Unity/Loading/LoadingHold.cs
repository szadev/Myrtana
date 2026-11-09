using System;
using G3.Unity;
using Myrtana.Core;
using Myrtana.Session;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// Stops the game while a loading screen is up (start-up, new game, load-game, teleport, cell waits), as G3 does:
    /// <list type="bullet">
    /// <item>the session is not ticked (<see cref="GameBootstrap"/> asks <see cref="Refresh"/>): game clock, NPC
    /// simulation, vitals, crime, the start script, dialog and world state stand still;</item>
    /// <item><see cref="GamePause"/> sets <see cref="Time.timeScale"/> to 0: physics, animation, NPC/combat views,
    /// projectiles, effects and weather blends stop. The loading screen, the streamer, the start-up loader, NPC/item
    /// view spawning and the music run on unscaled/real/DSP time and go on;</item>
    /// <item>the hero's controls are locked (<see cref="HeroControlLock"/>);</item>
    /// <item><see cref="WorldSounds"/> (the listener pause) pauses every world sound, voice and ambience; the music voices (and the
    /// video screen) ignore the listener pause and keep playing.</item>
    /// </list>
    /// The loading states are observed every frame (<see cref="WorldHold"/>), so every way into a loading screen is
    /// covered without each caller pairing a hold with a release; everything is released at once when the last one ends.
    /// Runs before the other game scripts so a loading screen opened in the previous frame's LateUpdate (teleport)
    /// already holds the world in the next frame.
    /// </summary>
    [DefaultExecutionOrder(-900)]
    public sealed class LoadingHold : MonoBehaviour
    {
        static LoadingHold s_current;

        readonly WorldHold _hold = new WorldHold();
        G3WorldStreamer _streamer;
        G3StartupLoader _startup;
        G3LoadingOverlay _overlay;
        GameStartFlow _flow;
        HeroControlLock _controls;
        Func<GameSession> _session;
        string _before;

        /// <summary>True while a loading screen holds the world (any loading hold in the game).</summary>
        public static bool IsHeld => s_current != null && s_current._hold.IsHeld;

        /// <summary>A load is running (a loading screen, the startup or flow load, a streamer wait) — not just an owner's
        /// hold like the title's: the cursor hides (fix-87).</summary>
        public static bool IsLoading => s_current != null && s_current._hold.Signals != LoadingSignal.None;

        public void Init(G3WorldStreamer streamer, Func<GameSession> session)
        {
            s_current = this;
            _streamer = streamer;
            _session = session;
            Refresh();
        }

        /// <summary>The controls and the program flow exist once the content is loaded.</summary>
        public void Attach(HeroControlLock controls, GameStartFlow flow)
        {
            _flow = flow;
            _controls = controls;
            if (_hold.IsHeld) _controls?.Acquire(this);
        }

        /// <summary>Holds the world on behalf of <paramref name="owner"/> in addition to the observed loading screens.</summary>
        public void Acquire(object owner) => Apply(_hold.Acquire(owner, Time.realtimeSinceStartupAsDouble));

        public void Release(object owner) => Apply(_hold.Release(owner, Time.realtimeSinceStartupAsDouble));

        /// <summary>Observes the loading states now; true while the world is held.</summary>
        public bool Refresh()
        {
            Apply(_hold.Observe(Observe(), Time.realtimeSinceStartupAsDouble));
            return _hold.IsHeld;
        }

        void Update() => Refresh();

        /// <summary>The title stage while it keeps the world out (the streamer's self-hold there is no load, fix-98).</summary>
        public TitleStage Title { get; set; }

        LoadingSignal Observe()
        {
            bool startup = false, screen = false, wait = false;
            if (_streamer != null)
            {
                if (_startup == null) _startup = _streamer.GetComponent<G3StartupLoader>();
                if (_overlay == null) _overlay = _streamer.GetComponent<G3LoadingOverlay>(); // added by the first area wait
                startup = _startup != null && _startup.isActiveAndEnabled && _startup.IsLoading;
                screen = _overlay != null && _overlay.IsVisible;
                wait = _streamer.IsHeldBy(_streamer);
            }
            bool flowLoading = _flow != null && _flow.Flow != null && _flow.Flow.Phase == GamePhase.Loading;
            return LoadingSignals.Of(startup, screen, wait, flowLoading, Title != null && Title.IsActive);
        }

        void Apply(WorldHoldChange change)
        {
            if (change == WorldHoldChange.Began)
            {
                GamePause.Set(this, true);
                _controls?.Acquire(this);
                WorldSounds.Mute(this, true);
                _before = Snapshot();
                Debug.Log($"[Myrtana] loading hold on ({_hold.Signals}): world stopped, world sounds paused, music on; {_before}");
            }
            else if (change == WorldHoldChange.Ended)
            {
                WorldSounds.Mute(this, false);
                _controls?.Release(this);
                GamePause.Set(this, false);
                Debug.Log($"[Myrtana] loading hold off after {_hold.LastDuration:F1} s: before {_before}; now {Snapshot()}");
            }
        }

        /// <summary>Game clock and hero hit points for the log (unchanged across a hold).</summary>
        string Snapshot()
        {
            var session = _session?.Invoke();
            if (session == null) return "no session";
            var clock = session.Clock;
            return $"day {clock.Day} {clock.HourOfDay:00.000} h, hero HP {session.Combat.Vitals.HitPoints(ActorId.Player)}";
        }

        void OnDisable()
        {
            Apply(_hold.ReleaseAll(Time.realtimeSinceStartupAsDouble));
            if (s_current == this) s_current = null;
        }

        void OnEnable()
        {
            if (s_current == null) s_current = this;
        }
    }
}
