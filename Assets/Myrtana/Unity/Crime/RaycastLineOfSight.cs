using System;
using System.Collections.Generic;
using System.Diagnostics;
using Myrtana.Simulation;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// Line of sight for perception (Docs/perception-crime.md §2.1): rays from the observer's eyes to the target's head and
    /// chest against the world's colliders - terrain and the static world (G3CollisionBuilder gives foliage no collider,
    /// trees only a trunk capsule). Characters (the player's controller, NPC and fighter bodies) do not block the view
    /// <b>[interp]</b>. Poses are feet positions; the heights come from the bodies (<see cref="ISightLines"/>, fix-204: a wolf
    /// looks from knee height, a troll from far above).
    /// <para>Performance (fix-204): one Linecast per sample point (the multi-hit cast only when a character is in the way), the
    /// head first and the chest only when the head is hidden; answers are cached for <see cref="CacheSeconds"/> per quantised
    /// pair of points (the scans of one second, the broadcast receivers of one event); at most <see cref="MaxRaysPerFrame"/> rays (≈ 3-8 µs each in Nordmar, fix-204 probe)
    /// per frame - beyond, a stale answer, else the chest ray alone (a murder seen by a crowd). The rays' time per frame is in
    /// <see cref="LastFrameMs"/>.</para>
    /// </summary>
    public sealed class RaycastLineOfSight : ISightLines
    {
        /// <summary>Eye height of humans and orcs, metres.</summary>
        public const float EyeHeight = SightLines.HumanEyeHeight;
        /// <summary>Where a human target is looked at (chest), metres above its feet.</summary>
        public const float TargetHeight = 1.2f;
        /// <summary>Sample points as shares of the target's top: head, then chest.</summary>
        const float HeadShare = 0.88f, ChestShare = 0.6f;
        public const float CacheSeconds = 0.5f;
        public const int MaxRaysPerFrame = 32;
        /// <summary>Cache grid, m (eye and top heights in 0.1 m).</summary>
        const float Quantum = 0.25f;
        /// <summary>Every layer a ray may stop at: not Ignore Raycast, not water.</summary>
        static readonly int Mask = Physics.DefaultRaycastLayers & ~(1 << 4);

        readonly RaycastHit[] _hits = new RaycastHit[16];
        readonly Dictionary<Key, Entry> _cache = new Dictionary<Key, Entry>();
        readonly List<Key> _expired = new List<Key>();
        readonly Stopwatch _watch = new Stopwatch();
        int _frame = -1, _rays;
        double _frameMs;

        /// <summary>Rays cast in the last finished frame, its time (ms), the peak, and the totals (dev probes).</summary>
        public static int LastFrameRays { get; private set; }
        public static float LastFrameMs { get; private set; }
        public static float PeakFrameMs { get; private set; }
        public static int PeakFrameRays { get; private set; }
        public static long TotalQueries, CacheHits, OverBudget, Blocked;

        struct Entry
        {
            public bool Clear;
            public float Time;
        }

        readonly struct Key : IEquatable<Key>
        {
            readonly int _ax, _ay, _az, _bx, _by, _bz;
            readonly short _eye, _top;

            public Key(Vector3 a, Vector3 b, float eye, float top)
            {
                _ax = Mathf.RoundToInt(a.x / Quantum); _ay = Mathf.RoundToInt(a.y / Quantum); _az = Mathf.RoundToInt(a.z / Quantum);
                _bx = Mathf.RoundToInt(b.x / Quantum); _by = Mathf.RoundToInt(b.y / Quantum); _bz = Mathf.RoundToInt(b.z / Quantum);
                _eye = (short)Mathf.RoundToInt(eye * 10f);
                _top = (short)Mathf.RoundToInt(top * 10f);
            }

            public bool Equals(Key o) => _ax == o._ax && _ay == o._ay && _az == o._az && _bx == o._bx && _by == o._by && _bz == o._bz &&
                                         _eye == o._eye && _top == o._top;
            public override bool Equals(object obj) => obj is Key k && Equals(k);
            public override int GetHashCode()
            {
                unchecked
                {
                    int h = _ax * 73856093 ^ _ay * 19349663 ^ _az * 83492791;
                    h = h * 31 + (_bx * 73856093 ^ _by * 19349663 ^ _bz * 83492791);
                    return h * 31 + (_eye << 8 ^ _top);
                }
            }
        }

        public bool IsClear(WorldPose from, WorldPose to) => IsClear(from, EyeHeight, to, SightLines.HumanTop);

        public bool IsClear(WorldPose from, float eyeHeight, WorldPose to, float targetTop)
        {
            NextFrame();
            TotalQueries++;
            var feetA = new Vector3(from.X, from.Y, from.Z);
            var feetB = new Vector3(to.X, to.Y, to.Z);
            var key = new Key(feetA, feetB, eyeHeight, targetTop);
            float now = Time.unscaledTime;
            bool cached = _cache.TryGetValue(key, out var entry);
            if (cached && now - entry.Time <= CacheSeconds)
            {
                CacheHits++;
                return entry.Clear;
            }
            bool over = _rays >= MaxRaysPerFrame;
            if (over)
            {
                OverBudget++;
                if (cached) return entry.Clear; // a stale answer
            }
            _watch.Restart();
            var eye = feetA + Vector3.up * eyeHeight;
            // over the budget (a murder seen by a crowd): the chest ray alone
            bool clear = !over && Ray(eye, feetB + Vector3.up * (targetTop * HeadShare)) || Ray(eye, feetB + Vector3.up * (targetTop * ChestShare));
            _watch.Stop();
            _frameMs += _watch.Elapsed.TotalMilliseconds;
            if (!clear) Blocked++;
            _cache[key] = new Entry { Clear = clear, Time = now };
            return clear;
        }

        void NextFrame()
        {
            int f = Time.frameCount;
            if (f == _frame) return;
            if (_frame >= 0)
            {
                LastFrameRays = _rays;
                LastFrameMs = (float)_frameMs;
                if (LastFrameMs > PeakFrameMs) PeakFrameMs = LastFrameMs;
                if (_rays > PeakFrameRays) PeakFrameRays = _rays;
            }
            _frame = f;
            _rays = 0;
            _frameMs = 0;
            if ((f & 127) == 0) Prune();
        }

        void Prune()
        {
            float now = Time.unscaledTime;
            _expired.Clear();
            foreach (var kv in _cache)
                if (now - kv.Value.Time > CacheSeconds * 4) _expired.Add(kv.Key);
            for (int i = 0; i < _expired.Count; i++) _cache.Remove(_expired[i]);
        }

        /// <summary>Resets the peaks and totals (dev probes).</summary>
        public static void ResetStats()
        {
            PeakFrameMs = 0;
            PeakFrameRays = 0;
            TotalQueries = CacheHits = OverBudget = Blocked = 0;
        }

        bool Ray(Vector3 from, Vector3 to)
        {
            _rays++;
            var ray = to - from;
            float length = ray.magnitude;
            if (length < 0.01f) return true;
            if (!Physics.Linecast(from, to, out var first, Mask, QueryTriggerInteraction.Ignore)) return true;
            if (!IsCharacter(first.collider)) return false;
            int n = Physics.RaycastNonAlloc(from, ray / length, _hits, length, Mask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (!IsCharacter(_hits[i].collider)) return false;
            return true;
        }

        /// <summary>Character or not, per collider (the hierarchy walk costs more than the ray; colliders never change sides).</summary>
        static readonly Dictionary<Collider, bool> s_character = new Dictionary<Collider, bool>();

        static bool IsCharacter(Collider c)
        {
            if (c is CharacterController) return true;
            if (s_character.TryGetValue(c, out bool yes)) return yes;
            if (s_character.Count > 8192) s_character.Clear(); // streamed cells come and go
            yes = c.GetComponentInParent<CombatFighter>() != null || c.GetComponentInParent<NpcView>() != null;
            s_character[c] = yes;
            return yes;
        }
    }
}
