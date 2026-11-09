using System.Collections.Generic;
using G3.Unity;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// Suspends the hero's controls while anything needs the mouse and keyboard (a conversation, an inventory or trade
    /// screen): the hero stops and the controller is switched off. Controls come back when the last holder releases its
    /// lock. The cursor is not this lock's business: CursorDirector shows it only for what takes the mouse (fix-87).
    /// </summary>
    public sealed class HeroControlLock
    {
        readonly G3Player _player;
        readonly HashSet<object> _holders = new HashSet<object>();
        readonly HashSet<object> _walkers = new HashSet<object>();

        public HeroControlLock(G3Player player) => _player = player;

        public G3Player Player => _player;

        public bool IsLocked => _holders.Count > 0;

        /// <summary>Who holds the lock (type names; the dev probe, fix-39).</summary>
        public string HoldersText
        {
            get
            {
                var names = new List<string>();
                foreach (var h in _holders) names.Add(h?.GetType().Name ?? "null");
                return string.Join(",", names);
            }
        }

        public void Acquire(object holder)
        {
            if (!_holders.Add(holder)) return;
            Apply();
        }

        readonly G3.Player.ActionLockWatchdog _watchdog = new G3.Player.ActionLockWatchdog();

        /// <summary>
        /// fix-201: locks the controls for an action that <paramref name="ownsAction"/> reports as still running (its motion
        /// plays, its coroutine steps). Once it has been false for longer than <paramref name="grace"/> s, <see cref="Sweep"/>
        /// ends the action (<see cref="IActionLockOwner.ActionOrphaned"/>) and releases the lock: no action lock outlives its
        /// motion owner.
        /// </summary>
        public void Acquire(object holder, System.Func<bool> ownsAction, float grace = G3.Player.ActionLockWatchdog.DefaultGrace)
        {
            _watchdog.Watch(holder, ownsAction, grace);
            Acquire(holder);
        }

        /// <summary>
        /// fix-201: once a frame (GameBootstrap): watched holders whose action is gone are ended and released. Returns their
        /// type names (empty: none).
        /// </summary>
        public string Sweep(float dt)
        {
            var expired = _watchdog.Sweep(dt);
            if (expired.Count == 0) return string.Empty;
            var names = new List<string>();
            foreach (var h in new List<object>(expired))
            {
                names.Add(h?.GetType().Name ?? "null");
                if (!_holders.Contains(h)) continue;
                try { (h as IActionLockOwner)?.ActionOrphaned(); }
                catch (System.Exception e) { Debug.LogException(e); }
                Release(h);
            }
            string text = string.Join(",", names);
            Debug.LogWarning($"[Myrtana] control lock outlived its action, released: {text}");
            return text;
        }

        /// <summary>
        /// Locks the controls but leaves the body to a scripted walk (<see cref="G3Player.scriptedOnly"/>): the keys and
        /// the mouse no longer steer the hero, he walks and turns as the holder sets <see cref="G3Player.scriptedMove"/>
        /// and looks (fix-75: the approach to a used object, G3 PS_Interact → _AI_Goto_Ext). <see cref="Acquire"/> by the
        /// same or another holder takes the body as usual; <see cref="Release"/> ends both.
        /// </summary>
        public void AcquireWalking(object holder)
        {
            if (!_holders.Add(holder)) return;
            _walkers.Add(holder);
            Apply();
        }

        /// <summary>
        /// fix-105: a load or new game ends what held the controls for the old game (<see cref="ISessionControlHolder"/>: a
        /// conversation, an object use, a knock-down, eating, a form change); the menus, pages, loading screens and the
        /// start flow keep theirs. Returns the released holders' type names (empty: none was left).
        /// </summary>
        public string ReleaseSessionHolders()
        {
            var names = new List<string>();
            foreach (var h in new List<object>(_holders))
                if (h is ISessionControlHolder)
                {
                    names.Add(h.GetType().Name);
                    _holders.Remove(h);
                    _walkers.Remove(h);
                    _watchdog.Forget(h);
                }
            if (names.Count > 0) Apply();
            return string.Join(",", names);
        }

        public void Release(object holder)
        {
            _watchdog.Forget(holder);
            if (!_holders.Remove(holder)) return;
            _walkers.Remove(holder);
            Apply();
        }

        /// <summary>Turns a walking lock into a full one (the walk is over, the use takes the body).</summary>
        public void StopWalking(object holder)
        {
            if (!_walkers.Remove(holder)) return;
            Apply();
        }

        /// <summary>Whether a scripted walk holds the controls (no full lock).</summary>
        public bool Walking => _walkers.Count > 0 && _walkers.Count == _holders.Count;

        bool _full;

        void Apply()
        {
            if (_player == null) return;
            bool full = _holders.Count > _walkers.Count;
            bool walking = !full && _walkers.Count > 0;
            if (full && !_full)
            {
                _player.scriptedOnly = false;
                if (_player.enabled) _player.Step(Vector2.zero, G3MoveMode.Walk, false, Time.deltaTime);
                _player.enabled = false;
            }
            else if (!full && _full)
            {
                _player.enabled = true;
            }
            _full = full;
            if (!full) _player.scriptedOnly = walking;
            if (_walked && !walking)
            {
                // the walk is over: its input goes (a scenario's own scripted input is never touched otherwise)
                _player.scriptedMove = Vector2.zero;
                _player.scriptedWalk = false;
            }
            _walked = walking;
        }

        bool _walked;
    }

    /// <summary>
    /// fix-105: a <see cref="HeroControlLock"/> holder whose lock belongs to the running game (a conversation, an object use, a
    /// knock-down, eating, a form change): a load or new game releases it (<see cref="HeroControlLock.ReleaseSessionHolders"/>).
    /// </summary>
    public interface ISessionControlHolder
    {
    }

    /// <summary>
    /// fix-201: a holder whose lock is watched (<see cref="HeroControlLock.Acquire(object, System.Func{bool}, float)"/>): its
    /// action was found gone while the lock was still held; it ends the action cleanly (hands, props, flags) before the lock
    /// is released.
    /// </summary>
    public interface IActionLockOwner
    {
        void ActionOrphaned();
    }
}
