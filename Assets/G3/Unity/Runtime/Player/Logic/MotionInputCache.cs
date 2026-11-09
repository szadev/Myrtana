using System.Collections.Generic;

namespace G3.Player
{
    /// <summary>
    /// crash-2: the inputs of one mixer of a motion player's PlayableGraph, as a cache of clip playables keyed by clip.
    /// Changing a graph's topology (creating, connecting, destroying playables) makes the Animator drop and rebuild its
    /// bindings; doing that on every cross-fade crashed in mecanim (ChainedAllocator::Clear ← DeallocateBindings ←
    /// Animator::OnGraphTopologyChanged, build 22c). So a mixer keeps every clip playable it once needed, connected at a
    /// fixed input: a request first looks for its clips among them (<see cref="Bind"/>: no topology change), and only a
    /// clip seen for the first time gets a new input (<see cref="Place"/>, done by the player at a safe point) — appended
    /// while there is room, else in the place of the least recently used input the current request does not use.
    /// Pure bookkeeping (tested in G3Tests); the player owns the playables.
    /// </summary>
    public sealed class MotionInputCache<TKey> where TKey : class
    {
        readonly List<TKey> _keys = new List<TKey>();
        readonly List<long> _used = new List<long>();
        readonly List<int> _taken = new List<int>();
        long _tick;

        public MotionInputCache(int capacity) => Capacity = capacity < 1 ? 1 : capacity;

        /// <summary>Inputs the mixer should not grow beyond (a request with more clips than this still gets them all).</summary>
        public int Capacity { get; }

        /// <summary>Inputs connected.</summary>
        public int Count => _keys.Count;

        public TKey KeyAt(int input) => _keys[input];

        /// <summary>How many of <paramref name="keys"/> (null = no clip) <see cref="Bind"/> would find (each input counts once).</summary>
        public int Hits(IReadOnlyList<TKey> keys)
        {
            int hits = 0;
            var taken = _taken;
            taken.Clear();
            for (int k = 0; k < keys.Count; k++)
            {
                int i = Find(keys[k], taken);
                if (i >= 0) { taken.Add(i); hits++; }
            }
            return hits;
        }

        /// <summary>
        /// Maps every key of a request to an input holding its clip (<paramref name="inputs"/>[k]), -1 where none is free
        /// (null keys stay -1). Two equal keys get two inputs. Marks the inputs used.
        /// </summary>
        public void Bind(IReadOnlyList<TKey> keys, int[] inputs)
        {
            var taken = _taken;
            taken.Clear();
            for (int k = 0; k < keys.Count; k++)
            {
                int i = Find(keys[k], taken);
                inputs[k] = i;
                if (i >= 0) { taken.Add(i); _used[i] = ++_tick; }
            }
        }

        /// <summary>What <see cref="Place"/> decided: the input, and whether it is new or replaces <see cref="Evicted"/>.</summary>
        public readonly struct Placement
        {
            public readonly int Input;
            public readonly bool Appended;
            public readonly TKey Evicted;
            public Placement(int input, bool appended, TKey evicted) { Input = input; Appended = appended; Evicted = evicted; }
        }

        /// <summary>
        /// Gives <paramref name="key"/> an input (a topology change for the player): a new one while below
        /// <see cref="Capacity"/>, else the least recently used one not in <paramref name="inUse"/> (the current request's
        /// inputs, -1 entries ignored); a new one too when every input is in use.
        /// </summary>
        public Placement Place(TKey key, IReadOnlyList<int> inUse)
        {
            int victim = -1;
            if (_keys.Count >= Capacity)
            {
                long best = long.MaxValue;
                for (int i = 0; i < _keys.Count; i++)
                {
                    if (Contains(inUse, i)) continue;
                    if (_used[i] < best) { best = _used[i]; victim = i; }
                }
            }
            if (victim < 0)
            {
                _keys.Add(key);
                _used.Add(++_tick);
                return new Placement(_keys.Count - 1, true, null);
            }
            var old = _keys[victim];
            _keys[victim] = key;
            _used[victim] = ++_tick;
            return new Placement(victim, false, old);
        }

        /// <summary>Forgets every input (the graph was destroyed).</summary>
        public void Clear()
        {
            _keys.Clear();
            _used.Clear();
        }

        int Find(TKey key, List<int> taken)
        {
            if (key == null) return -1;
            for (int i = 0; i < _keys.Count; i++)
                if (ReferenceEquals(_keys[i], key) && !taken.Contains(i)) return i;
            return -1;
        }

        static bool Contains(IReadOnlyList<int> list, int value)
        {
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++) if (list[i] == value) return true;
            return false;
        }

        /// <summary>
        /// The free mixer for a new request: the one that already holds most of its clips (fewest new inputs), the lowest
        /// index on a tie; -1 when every mixer is busy.
        /// </summary>
        public static int PickSlot(IReadOnlyList<MotionInputCache<TKey>> caches, IReadOnlyList<bool> busy, IReadOnlyList<TKey> keys)
        {
            int best = -1, bestHits = -1;
            for (int s = 0; s < caches.Count; s++)
            {
                if (busy[s]) continue;
                int h = caches[s].Hits(keys);
                if (h > bestHits) { best = s; bestHits = h; }
            }
            return best;
        }
    }

    /// <summary>crash-2: when a motion player may change its graph's topology.</summary>
    public static class MotionTopology
    {
        /// <summary>
        /// Only with the Animator on and evaluated: a disabled Animator, or one culled completely (no renderer visible),
        /// keeps its graph as it is until it is shown again.
        /// </summary>
        public static bool CanChange(bool animatorActive, bool cullsCompletely, bool anyRendererVisible) =>
            animatorActive && (!cullsCompletely || anyRendererVisible);
    }
}
