using System;
using System.Collections.Generic;

namespace G3.Unity
{
    /// <summary>
    /// Per-frame limits of <see cref="PacedWork{TKey}"/> (Docs/performance.md, "Native crash with the GPU Resident Drawer").
    /// A frame runs work while it is under both limits; the first item of a frame always runs, so the queue always moves.
    /// </summary>
    public readonly struct PaceBudget
    {
        /// <summary>Weight (renderers touched) per frame; 0 or less = no limit (everything in one frame).</summary>
        public readonly int MaxWeight;
        /// <summary>Milliseconds per frame; 0 or less = no limit.</summary>
        public readonly double MaxMs;

        public PaceBudget(int maxWeight, double maxMs)
        {
            MaxWeight = maxWeight;
            MaxMs = maxMs;
        }

        /// <summary>True when everything goes in one frame (the old behaviour, <c>-g3swaprate 0</c>).</summary>
        public bool Unlimited => MaxWeight <= 0 && MaxMs <= 0;

        /// <summary>May another item run after <paramref name="items"/> items of <paramref name="weight"/> in <paramref name="ms"/>?</summary>
        public bool Allows(int items, int weight, double ms)
        {
            if (items == 0) return true;
            if (MaxWeight > 0 && weight >= MaxWeight) return false;
            if (MaxMs > 0 && ms >= MaxMs) return false;
            return true;
        }
    }

    /// <summary>
    /// A keyed work queue drained a little per frame (engine-free, unit-tested in Tools/G3Tests). Used to spread bulk
    /// renderer changes (remaster material swaps, tree replacement, story-sector content, NPC despawns) over frames instead
    /// of changing tens of thousands of GPU-Resident-Drawer renderers in one frame. Each key is queued once: enqueuing a key
    /// that is still waiting replaces its work and keeps its place (so flipping a switch back and forth while a wave runs
    /// never grows the queue past one entry per renderer). The work reads the current wanted state when it runs and returns
    /// its weight (renderers touched, 0 = nothing to do).
    /// </summary>
    public sealed class PacedWork<TKey>
    {
        readonly List<TKey> _order = new List<TKey>();
        readonly Dictionary<TKey, Func<int>> _work;
        int _head;

        public PacedWork(IEqualityComparer<TKey> comparer = null)
        {
            _work = new Dictionary<TKey, Func<int>>(comparer ?? EqualityComparer<TKey>.Default);
        }

        /// <summary>Keys waiting.</summary>
        public int Count => _work.Count;

        public bool Contains(TKey key) => _work.ContainsKey(key);

        /// <summary>Queues (or replaces) the work of <paramref name="key"/>; true when the key was new.</summary>
        public bool Enqueue(TKey key, Func<int> work)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            bool added = !_work.ContainsKey(key);
            _work[key] = work;
            if (added) _order.Add(key);
            return added;
        }

        /// <summary>Drops the waiting work of <paramref name="key"/>.</summary>
        public bool Remove(TKey key) => _work.Remove(key);

        public void Clear()
        {
            _work.Clear();
            _order.Clear();
            _head = 0;
        }

        /// <summary>
        /// Runs queued work in order while <paramref name="budget"/> allows (<paramref name="elapsedMs"/> = milliseconds
        /// spent since the frame's run started). Returns the items run; <paramref name="weight"/> = their summed weight.
        /// A work item that throws is dropped and the exception rethrown after the queue is consistent again.
        /// </summary>
        public int Run(PaceBudget budget, Func<double> elapsedMs, out int weight)
        {
            weight = 0;
            int items = 0;
            while (_head < _order.Count)
            {
                if (!budget.Allows(items, weight, elapsedMs != null && !budget.Unlimited ? elapsedMs() : 0)) break;
                var key = _order[_head++];
                if (!_work.TryGetValue(key, out var work)) continue;   // removed while waiting
                _work.Remove(key);
                items++;
                weight += Math.Max(0, work());
            }
            Compact();
            return items;
        }

        void Compact()
        {
            if (_head >= _order.Count)
            {
                _order.Clear();
                _head = 0;
            }
            else if (_head > 1024 && _head * 2 > _order.Count)
            {
                _order.RemoveRange(0, _head);
                _head = 0;
            }
        }
    }

    /// <summary>
    /// The GPU Resident Drawer fallback switch (Docs/performance.md, "Native crash with the GPU Resident Drawer"):
    /// player command line <c>-g3nogrd</c> or environment <c>G3_NOGRD=1</c> start the game with the drawer off (URP asset
    /// <c>gpuResidentDrawerMode</c> = Disabled, in memory only). <c>-g3grd</c> / <c>G3_NOGRD=0</c> force it on again.
    /// </summary>
    public static class GrdFallback
    {
        public const string OffSwitch = "-g3nogrd";
        public const string OnSwitch = "-g3grd";
        public const string EnvVar = "G3_NOGRD";

        /// <summary>True when the drawer should be turned off: the last of the two switches on the command line wins over the env.</summary>
        public static bool Disable(IReadOnlyList<string> args, string env)
        {
            if (args != null)
                for (int i = args.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(args[i], OffSwitch, StringComparison.OrdinalIgnoreCase)) return true;
                    if (string.Equals(args[i], OnSwitch, StringComparison.OrdinalIgnoreCase)) return false;
                }
            if (string.IsNullOrWhiteSpace(env)) return false;
            env = env.Trim();
            return env == "1" || string.Equals(env, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(env, "on", StringComparison.OrdinalIgnoreCase) || string.Equals(env, "yes", StringComparison.OrdinalIgnoreCase);
        }
    }
}
