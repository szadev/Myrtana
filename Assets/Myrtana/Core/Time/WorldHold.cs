using System;
using System.Collections.Generic;

namespace Myrtana.Core
{
    /// <summary>
    /// A set of owners that keep something held (paused, frozen); held while at least one owner is in it. Owners nest
    /// freely: acquiring twice or releasing an owner that is not in the set changes nothing.
    /// </summary>
    public sealed class HoldSet
    {
        readonly HashSet<object> _owners = new HashSet<object>();

        public bool IsHeld => _owners.Count > 0;
        public int Count => _owners.Count;
        public bool IsHeldBy(object owner) => owner != null && _owners.Contains(owner);

        /// <summary>Adds or removes <paramref name="owner"/>; true when <see cref="IsHeld"/> changed.</summary>
        public bool Set(object owner, bool hold)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            bool was = IsHeld;
            if (hold) _owners.Add(owner);
            else _owners.Remove(owner);
            return was != IsHeld;
        }

        /// <summary>Drops every owner; true when it was held.</summary>
        public bool Clear()
        {
            bool was = IsHeld;
            _owners.Clear();
            return was;
        }
    }

    /// <summary>Why the world is held: the loading screen states the host observes each frame.</summary>
    [Flags]
    public enum LoadingSignal
    {
        None = 0,
        /// <summary>The start-up loading state (Play / game start) is still waiting for its area.</summary>
        StartupLoading = 1,
        /// <summary>The loading screen is shown (load-game or teleport area wait, start-up).</summary>
        LoadingScreen = 2,
        /// <summary>The streamer froze the hero until the cells under him are loaded.</summary>
        StreamerWait = 4,
        /// <summary>The program flow is in its loading phase (new game or load, until the world is ready).</summary>
        FlowLoading = 8,
    }

    public enum WorldHoldChange { None, Began, Ended }

    /// <summary>
    /// The loading signals the host reports from what it observes (fix-98). At the title (logos, main menu, credits
    /// before a game is started or loaded) the streamer loads no cells, so it holds the scene's hero for ever because
    /// the cells under him never load: that is no load (G3's title has no world at all) and must neither count as a
    /// loading screen nor hide the menu's pointer; the title holds the world itself. Likewise a start-up loader left
    /// over under the title. A loading screen or the flow's loading phase always counts.
    /// </summary>
    public static class LoadingSignals
    {
        public static LoadingSignal Of(bool startupLoading, bool loadingScreen, bool streamerWait, bool flowLoading, bool atTitle)
        {
            var s = LoadingSignal.None;
            if (startupLoading && !atTitle) s |= LoadingSignal.StartupLoading;
            if (loadingScreen) s |= LoadingSignal.LoadingScreen;
            if (streamerWait && !atTitle) s |= LoadingSignal.StreamerWait;
            if (flowLoading) s |= LoadingSignal.FlowLoading;
            return s;
        }
    }

    /// <summary>
    /// The "world hold" while a loading screen is up (Work/handoff/loading-freeze.md): the game does not run, the hero takes no
    /// input, world sounds are paused, music goes on. Held while any loading signal is set or any explicit owner holds
    /// it; it begins once and ends once, when the last reason is gone. The host applies the effects on the changes.
    /// </summary>
    public sealed class WorldHold
    {
        readonly HoldSet _owners = new HoldSet();
        LoadingSignal _signals;
        double _beganAt;

        public bool IsHeld => _signals != LoadingSignal.None || _owners.IsHeld;
        public LoadingSignal Signals => _signals;
        /// <summary>Seconds (host clock) the last hold lasted, valid after <see cref="WorldHoldChange.Ended"/>.</summary>
        public double LastDuration { get; private set; }

        /// <summary>Replaces the observed loading signals (once per frame).</summary>
        public WorldHoldChange Observe(LoadingSignal signals, double now) => Change(() => _signals = signals, now);

        /// <summary>Holds on behalf of <paramref name="owner"/> in addition to the observed signals (nests).</summary>
        public WorldHoldChange Acquire(object owner, double now) => Change(() => _owners.Set(owner, true), now);

        public WorldHoldChange Release(object owner, double now) => Change(() => _owners.Set(owner, false), now);

        /// <summary>Ends the hold whatever holds it (host shut down).</summary>
        public WorldHoldChange ReleaseAll(double now) => Change(() => { _signals = LoadingSignal.None; _owners.Clear(); }, now);

        WorldHoldChange Change(Action apply, double now)
        {
            bool was = IsHeld;
            apply();
            if (was == IsHeld) return WorldHoldChange.None;
            if (IsHeld)
            {
                _beganAt = now;
                return WorldHoldChange.Began;
            }
            LastDuration = Math.Max(0, now - _beganAt);
            return WorldHoldChange.Ended;
        }
    }
}
