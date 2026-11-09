using System;
using System.Collections.Generic;

namespace G3.Player
{
    /// <summary>
    /// fix-201: no action lock may outlive the motion that owns it. A holder of the hero's controls (eating, an object use, a
    /// form change) registers what it is waiting for (<c>ownsAction</c>: its motion still runs, its coroutine is alive); a
    /// holder whose action has been gone for longer than its grace is reported by <see cref="Sweep"/> so the owner of the lock
    /// can end it cleanly and release it. G3 has no such lock: its ZS_* tasks (a stumble, a fall) replace the PS_* state and
    /// the PS state's own exit runs; here the lock and the motion are separate and a cut motion left the lock behind
    /// (owner: "eating, an arrow hits him, then no action works until the menu"). Pure, frame-free (seconds in).
    /// </summary>
    public sealed class ActionLockWatchdog
    {
        /// <summary>Default grace before an orphaned lock is reported, seconds (a fade or one frame of hand-over) [interp].</summary>
        public const float DefaultGrace = 0.5f;

        sealed class Entry
        {
            public Func<bool> owns;
            public float grace, orphaned;
        }

        readonly Dictionary<object, Entry> _watched = new Dictionary<object, Entry>();
        readonly List<object> _expired = new List<object>();

        /// <summary>Watches <paramref name="holder"/>: <paramref name="ownsAction"/> is false once its action is gone.</summary>
        public void Watch(object holder, Func<bool> ownsAction, float grace = DefaultGrace)
        {
            if (holder == null || ownsAction == null) return;
            _watched[holder] = new Entry { owns = ownsAction, grace = Math.Max(0f, grace) };
        }

        /// <summary>The holder released its lock itself.</summary>
        public void Forget(object holder)
        {
            if (holder != null) _watched.Remove(holder);
        }

        public bool Watches(object holder) => holder != null && _watched.ContainsKey(holder);

        public int Count => _watched.Count;

        /// <summary>
        /// Advances by <paramref name="dt"/> seconds (0 while paused: nothing expires) and returns the holders whose action has
        /// been gone longer than their grace; they are no longer watched. A holder whose check throws counts as gone.
        /// </summary>
        public IReadOnlyList<object> Sweep(float dt)
        {
            _expired.Clear();
            foreach (var pair in _watched)
            {
                bool owns;
                try { owns = pair.Value.owns(); }
                catch (Exception) { owns = false; }
                if (owns)
                {
                    pair.Value.orphaned = 0f;
                    continue;
                }
                pair.Value.orphaned += Math.Max(0f, dt);
                if (pair.Value.orphaned > pair.Value.grace) _expired.Add(pair.Key);
            }
            foreach (var h in _expired) _watched.Remove(h);
            return _expired;
        }
    }
}
