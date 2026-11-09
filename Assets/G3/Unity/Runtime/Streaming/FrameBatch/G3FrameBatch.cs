using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// Batched per-frame callbacks (perf-3, Docs/performance.md "Scripts: batched, tiered NPC ticks"): components that exist
    /// hundreds of times (NPC views, animators, combat parts) do not get one Unity Update / LateUpdate each; they join a
    /// <see cref="Batch{T}"/> in OnEnable, leave it in OnDisable, and one driver component per execution order steps them all
    /// (a Unity callback per component costs more than most of these steps; skipped LOD frames now cost a field read).
    /// The drivers keep the components' own execution orders (<see cref="Slot"/>): 0 (views, combat), 100 (NPC animation
    /// selectors), 150 (combat animators), 200 (motion players) for Update, 0 and 270 (relief footing) for LateUpdate.
    /// Created on first use in play mode on one hidden object kept across scene loads.
    /// </summary>
    public static class G3FrameBatch
    {
        public enum Slot { Update0, Update100, Update150, Update200, LateUpdate0, LateUpdate270 }

        internal interface IRunner
        {
            void Run();
            void Clear();
        }

        /// <summary>Play mode starts without a domain reload (Enter Play Mode options): no member of the last session is left.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetForPlay()
        {
            foreach (var slot in s_slots)
                foreach (var runner in slot) runner.Clear();
            for (int i = 0; i < s_ranFrame.Length; i++) s_ranFrame[i] = -1;
            s_host = null;
            s_frame = -1;
        }

        static readonly List<IRunner>[] s_slots = NewSlots();
        static readonly int[] s_ranFrame = { -1, -1, -1, -1, -1, -1 };
        static GameObject s_host;

        static List<IRunner>[] NewSlots()
        {
            var slots = new List<IRunner>[6];
            for (int i = 0; i < slots.Length; i++) slots[i] = new List<IRunner>();
            return slots;
        }

        static int s_frame = -1;
        static float s_deltaTime;
        static double s_now;

        /// <summary>
        /// <see cref="Time.frameCount"/>, <see cref="Time.deltaTime"/> and <see cref="Time.timeAsDouble"/> of the frame, read
        /// once per slot run: members read these instead of three engine calls each (hundreds of members, perf-3). Valid
        /// inside the batched steps (and anywhere after the first slot of the frame).
        /// </summary>
        public static int Frame => s_frame;
        public static float DeltaTime => s_deltaTime;
        public static double Now => s_now;

        /// <summary>The slot already ran in the current frame (a reset of a step clock then does not gather this frame).</summary>
        public static bool RanThisFrame(Slot slot) => s_ranFrame[(int)slot] == Time.frameCount;

        internal static void Run(Slot slot)
        {
            s_frame = Time.frameCount;
            s_deltaTime = Time.deltaTime;
            s_now = Time.timeAsDouble;
            s_ranFrame[(int)slot] = s_frame;
            var runners = s_slots[(int)slot];
            for (int i = 0; i < runners.Count; i++) runners[i].Run();
        }

        static void EnsureHost()
        {
            if (s_host != null || !Application.isPlaying) return;
            s_host = new GameObject("G3FrameBatch") { hideFlags = HideFlags.HideInHierarchy | HideFlags.NotEditable };
            UnityEngine.Object.DontDestroyOnLoad(s_host);
            s_host.AddComponent<G3FrameBatchDriver0>();
            s_host.AddComponent<G3FrameBatchDriver100>();
            s_host.AddComponent<G3FrameBatchDriver150>();
            s_host.AddComponent<G3FrameBatchDriver200>();
            s_host.AddComponent<G3FrameBatchDriver270>();
        }

        static readonly Action<MonoBehaviour, Exception> s_failed = (c, e) => Debug.LogException(e, c);

        /// <summary>The members of one batched callback of component type <typeparamref name="T"/>.</summary>
        public sealed class Batch<T> : IRunner where T : MonoBehaviour
        {
            readonly FrameBatchList<T> _list = new FrameBatchList<T>();
            readonly Action<T> _step;
            readonly Action<T, Exception> _failed = (c, e) => s_failed(c, e);
            readonly ProfilerMarker _marker;
            readonly Slot _slot;

            public Batch(Slot slot, string name, Action<T> step)
            {
                _slot = slot;
                _step = step ?? throw new ArgumentNullException(nameof(step));
                _marker = new ProfilerMarker("G3FrameBatch." + name);
                s_slots[(int)slot].Add(this);
            }

            public int Count => _list.Count;

            /// <summary>
            /// mt-1: called once before the members step (only when there are members), e.g. to read every member's
            /// transform in one job at the point the members would read them one by one (Docs/multithreading-plan.md, C).
            /// </summary>
            public Action BeforeRun { get; set; }

            /// <summary>mt-1: called once after the members stepped (when <see cref="BeforeRun"/> ran).</summary>
            public Action AfterRun { get; set; }

            /// <summary>The slot already ran this frame (<see cref="StepClock.Reset"/>).</summary>
            public bool RanThisFrame => G3FrameBatch.RanThisFrame(_slot);

            /// <summary>Joins the batch (OnEnable; play mode only).</summary>
            public void Add(T component)
            {
                if (!Application.isPlaying) return;
                EnsureHost();
                _list.Add(component);
            }

            /// <summary>Leaves the batch (OnDisable).</summary>
            public void Remove(T component) => _list.Remove(component);

            void IRunner.Clear() => _list.Clear();

            void IRunner.Run()
            {
                if (_list.Count == 0) return;
                using (_marker.Auto())
                {
                    BeforeRun?.Invoke();
                    try { _list.Run(_step, _failed); }
                    finally { AfterRun?.Invoke(); }
                }
            }
        }
    }
}
