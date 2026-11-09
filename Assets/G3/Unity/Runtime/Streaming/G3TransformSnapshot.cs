using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Jobs;

namespace G3.Unity
{
    /// <summary>
    /// mt-1 (Docs/multithreading-plan.md, item C): the world poses of a set of transforms (NPC view roots, combat bodies)
    /// read by one Burst <see cref="IJobParallelForTransform"/> instead of one managed engine call per transform. A reader
    /// calls <see cref="Read"/> at the point of the frame where it used to read the transforms one by one (the job is
    /// completed before it returns, so no reader sees older data than before and no transform write waits on it) and then
    /// looks its members up by their <see cref="Slot"/>. Members join in OnEnable and leave in OnDisable; the order of the
    /// slots does not matter (a reader walks its own list in its own order). The job only copies positions / rotations, so
    /// the values are bitwise those of <c>Transform.position</c> / <c>rotation</c> (checked in <see cref="MtBits.Verify"/>
    /// mode). No arithmetic runs in the job: Mono evaluates float expressions partly in double precision, Burst rounds every
    /// step, so even a distance differed in the last bit (mt-1 editor check: ~20 % of the views).
    /// </summary>
    public sealed class G3TransformSnapshot : IDisposable
    {
        /// <summary>A member's place in the snapshot (-1 = not a member).</summary>
        public sealed class Slot
        {
            public int Index = -1;
            internal int AddedAtRead;
        }

        readonly string _name;
        readonly ProfilerMarker _marker;
        TransformAccessArray _transforms;
        readonly List<Slot> _slots = new List<Slot>();
        NativeArray<Vector3> _positions;
        NativeArray<Quaternion> _rotations;
        // the job's output copied into managed arrays at once (one memcpy each): the readers then index plain arrays instead of
        // a NativeArray per value (its safety checks cost more than the transform read in the editor)
        Vector3[] _pos = Array.Empty<Vector3>();
        Quaternion[] _rot = Array.Empty<Quaternion>();
        int _reads;
        static readonly List<G3TransformSnapshot> s_all = new List<G3TransformSnapshot>();

        public G3TransformSnapshot(string name)
        {
            _name = name;
            _marker = new ProfilerMarker("Mt.Snapshot." + name);
            s_all.Add(this);
        }

        public int Count => _slots.Count;
        /// <summary><see cref="Time.frameCount"/> of the last <see cref="Read"/> (-1 = none).</summary>
        public int ReadFrame { get; private set; } = -1;
        /// <summary><see cref="Time.timeAsDouble"/> of the last <see cref="Read"/>.</summary>
        public double ReadTime { get; private set; }
        /// <summary>The last read also took the rotations.</summary>
        public bool HasRotations { get; private set; }

        /// <summary>Adds <paramref name="t"/>; <paramref name="slot"/> keeps its index (renewed when another member leaves).</summary>
        public void Add(Transform t, Slot slot)
        {
            if (slot == null || slot.Index >= 0 || t == null || !Application.isPlaying) return;
            if (!_transforms.isCreated) _transforms = new TransformAccessArray(256);
            slot.Index = _slots.Count;
            slot.AddedAtRead = _reads;
            _slots.Add(slot);
            _transforms.Add(t);
        }

        public void Remove(Slot slot)
        {
            if (slot == null) return;
            int i = slot.Index;
            if (i < 0) return;
            slot.Index = -1;
            if (i >= _slots.Count || _slots[i] != slot) return; // the snapshot was disposed meanwhile
            int last = _slots.Count - 1;
            if (_transforms.isCreated) _transforms.RemoveAtSwapBack(i);
            if (i != last)
            {
                var moved = _slots[last];
                _slots[i] = moved;
                moved.Index = i;
                // the moved member's values of the last read go with it (a reader between two reads finds them at the new index)
                if (last < _pos.Length)
                {
                    _pos[i] = _pos[last];
                    _rot[i] = _rot[last];
                }
            }
            _slots.RemoveAt(last);
        }

        /// <summary>The member was in the snapshot at its last read (its values are from that read).</summary>
        public bool Has(Slot slot) =>
            slot != null && slot.Index >= 0 && slot.Index < _slots.Count && slot.AddedAtRead < _reads && slot.Index < _pos.Length;

        /// <summary><see cref="Has"/> and the last read was in this frame.</summary>
        public bool HasThisFrame(Slot slot) => ReadFrame == Time.frameCount && Has(slot);

        public Vector3 Position(Slot slot) => _pos[slot.Index];
        public Quaternion Rotation(Slot slot) => _rot[slot.Index];

        /// <summary>
        /// Run the read job on the main thread (Burst, no worker hand-off: a few hundred transforms take microseconds, the
        /// wake-up of the workers took ~0.1 ms in the editor) instead of scheduling it on the workers.
        /// </summary>
        public static bool RunOnMainThread = true;

        /// <summary>
        /// Reads every member now (one job, completed before this returns); <paramref name="rotations"/>: also the rotations.
        /// </summary>
        public void Read(bool rotations)
        {
            _reads++;
            ReadFrame = Time.frameCount;
            ReadTime = Time.timeAsDouble;
            HasRotations = rotations;
            int n = _slots.Count;
            if (n == 0 || !_transforms.isCreated) return;
            using (_marker.Auto())
            {
                Ensure(n);
                var job = new ReadJob { Positions = _positions, Rotations = _rotations, WantRotations = rotations };
                if (RunOnMainThread) job.RunReadOnly(_transforms);
                else job.ScheduleReadOnly(_transforms, 64).Complete();
                NativeArray<Vector3>.Copy(_positions, _pos, n);
                if (rotations) NativeArray<Quaternion>.Copy(_rotations, _rot, n);
            }
        }

        void Ensure(int n)
        {
            if (_positions.IsCreated && _positions.Length >= n) return;
            int size = Math.Max(256, Mathf.NextPowerOfTwo(n));
            Array.Resize(ref _pos, size);
            Array.Resize(ref _rot, size);
            var p = new NativeArray<Vector3>(size, Allocator.Persistent);
            var r = new NativeArray<Quaternion>(size, Allocator.Persistent);
            DisposeArrays();
            _positions = p;
            _rotations = r;
        }

        void DisposeArrays()
        {
            if (_positions.IsCreated) _positions.Dispose();
            if (_rotations.IsCreated) _rotations.Dispose();
        }

        public void Dispose()
        {
            DisposeArrays();
            if (_transforms.isCreated) _transforms.Dispose();
            foreach (var s in _slots) s.Index = -1;
            _slots.Clear();
            _pos = Array.Empty<Vector3>();
            _rot = Array.Empty<Quaternion>();
            ReadFrame = -1;
        }

        /// <summary>Play mode starts / the player quits: native memory back (members re-join on their next OnEnable).</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Hook()
        {
            Application.quitting -= DisposeAll;
            Application.quitting += DisposeAll;
            DisposeAll();
        }

        static void DisposeAll()
        {
            foreach (var s in s_all) s.Dispose();
        }

        public override string ToString() => $"{_name}: {Count} members, read frame {ReadFrame}";

        /// <summary>The read job: copies only (bitwise the transforms' values).</summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        struct ReadJob : IJobParallelForTransform
        {
            [WriteOnly] public NativeArray<Vector3> Positions;
            [WriteOnly] public NativeArray<Quaternion> Rotations;
            public bool WantRotations;

            public void Execute(int index, TransformAccess transform)
            {
                if (!transform.isValid) return;
                Positions[index] = transform.position;
                if (WantRotations) Rotations[index] = transform.rotation;
            }
        }
    }
}
