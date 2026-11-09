using System;
using System.Collections.Generic;

namespace G3.Unity
{
    /// <summary>
    /// The members of one batched per-frame callback (perf-3, <c>G3FrameBatch</c>): hundreds of NPC components step from one
    /// Unity callback instead of one Update each. Order = order of adding (Unity also runs equal-order scripts in the order
    /// they were enabled). A member added while the list runs waits for the next run (Unity starts a new component's Update
    /// the next frame); one removed while it runs is not stepped any more. A step that throws is reported and the run goes
    /// on (as an Update that throws). Engine-free (unit-tested in Tools/G3Tests).
    /// </summary>
    public sealed class FrameBatchList<T> where T : class
    {
        readonly List<T> _items = new List<T>();
        int _holes;
        bool _running;

        /// <summary>Members now.</summary>
        public int Count => _items.Count - _holes;

        /// <summary>Adds <paramref name="item"/> (false when it is null or already a member).</summary>
        public bool Add(T item)
        {
            if (item == null || IndexOf(item) >= 0) return false;
            _items.Add(item);
            return true;
        }

        /// <summary>Removes <paramref name="item"/> (false when it is no member).</summary>
        public bool Remove(T item)
        {
            int i = IndexOf(item);
            if (i < 0) return false;
            if (_running)
            {
                _items[i] = null;
                _holes++;
            }
            else _items.RemoveAt(i);
            return true;
        }

        public bool Contains(T item) => IndexOf(item) >= 0;

        /// <summary>No members any more (not while running).</summary>
        public void Clear()
        {
            if (_running) throw new InvalidOperationException("FrameBatchList.Clear while running");
            _items.Clear();
            _holes = 0;
        }

        int IndexOf(T item)
        {
            if (item == null) return -1;
            for (int i = 0; i < _items.Count; i++)
                if (ReferenceEquals(_items[i], item)) return i;
            return -1;
        }

        /// <summary>Steps every member that was one when the run began (see the class remarks).</summary>
        public void Run(Action<T> step, Action<T, Exception> failed)
        {
            if (_running) throw new InvalidOperationException("FrameBatchList.Run is not re-entrant");
            _running = true;
            int n = _items.Count;
            try
            {
                for (int i = 0; i < n; i++)
                {
                    var item = _items[i];
                    if (item == null) continue;
                    try { step(item); }
                    catch (Exception e) { failed?.Invoke(item, e); }
                }
            }
            finally
            {
                _running = false;
                if (_holes > 0) Compact();
            }
        }

        void Compact()
        {
            int w = 0;
            for (int r = 0; r < _items.Count; r++)
                if (_items[r] != null) _items[w++] = _items[r];
            _items.RemoveRange(w, _items.Count - w);
            _holes = 0;
        }
    }

    /// <summary>
    /// The time a stepped component gathers between two steps (perf-3): what <c>pending += Time.deltaTime</c> every frame
    /// gave, without touching the component on the frames it does not step. A component stepping every frame gets exactly
    /// that frame's delta time. Engine-free (unit-tested).
    /// </summary>
    public struct StepClock
    {
        double _start;
        int _lastFrame;
        bool _set;

        /// <summary>
        /// Gather from now on (was <c>pending = 0</c>). <paramref name="steppedThisFrame"/>: the batch already ran this frame,
        /// so this frame's delta time is not gathered (it would have been added before the reset).
        /// </summary>
        public void Reset(double now, float deltaTime, int frame, bool steppedThisFrame)
        {
            _set = true;
            if (steppedThisFrame)
            {
                _start = now;
                _lastFrame = frame;
            }
            else
            {
                _start = now - deltaTime;
                _lastFrame = frame - 1;
            }
        }

        /// <summary>The time gathered up to this frame (inclusive); gathering starts anew.</summary>
        public float Take(double now, float deltaTime, int frame)
        {
            float dt = !_set || _lastFrame == frame - 1 ? deltaTime : _lastFrame >= frame ? 0f : (float)(now - _start);
            _set = true;
            _start = now;
            _lastFrame = frame;
            return dt;
        }
    }
}
