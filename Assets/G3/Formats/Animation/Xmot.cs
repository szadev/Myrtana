using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using G3.Formats.Genome;

namespace G3.Formats.Animation
{
    public struct VecKey { public float Time; public V3 Value; }
    public struct RotKey { public float Time; public Q4 Value; }

    /// <summary>One animated node (chunk 1 "motion part" + following chunk-2 key tracks).</summary>
    public sealed class MotionTrack
    {
        public string Node;
        public V3 PosePosition, PoseScale, BindPosition, BindScale;
        public Q4 PoseRotation, BindRotation;
        public VecKey[] Position = Array.Empty<VecKey>();
        public RotKey[] Rotation = Array.Empty<RotKey>();
        public VecKey[] Scale = Array.Empty<VecKey>();
        public char PositionInterp = 'L', RotationInterp = 'L', ScaleInterp = 'L';

        /// <summary>True for tool helpers that have no actor node ("!FH_…IKGoalHelper", "!DMW_…Spline").</summary>
        public bool IsHelper => Node.StartsWith("!", StringComparison.Ordinal);

        public V3 SamplePosition(float t) => Sample(Position, t, PosePosition);
        public V3 SampleScale(float t) => Sample(Scale, t, PoseScale);

        public Q4 SampleRotation(float t)
        {
            var k = Rotation;
            if (k.Length == 0) return PoseRotation;
            if (t <= k[0].Time) return k[0].Value;
            if (t >= k[k.Length - 1].Time) return k[k.Length - 1].Value;
            int i = Find(k.Length, j => k[j].Time, t);
            float f = (t - k[i].Time) / Math.Max(1e-9f, k[i + 1].Time - k[i].Time);
            return Q4.Nlerp(k[i].Value, k[i + 1].Value, f);
        }

        static V3 Sample(VecKey[] k, float t, V3 def)
        {
            if (k.Length == 0) return def;
            if (t <= k[0].Time) return k[0].Value;
            if (t >= k[k.Length - 1].Time) return k[k.Length - 1].Value;
            int i = Find(k.Length, j => k[j].Time, t);
            float f = (t - k[i].Time) / Math.Max(1e-9f, k[i + 1].Time - k[i].Time);
            return V3.Lerp(k[i].Value, k[i + 1].Value, f);
        }

        /// <summary>Last index i with time[i] &lt;= t (n ≥ 2, time[0] &lt; t &lt; time[n-1]).</summary>
        static int Find(int n, Func<int, float> time, float t)
        {
            int lo = 0, hi = n - 1;
            while (hi - lo > 1) { int m = (lo + hi) >> 1; if (time(m) <= t) lo = m; else hi = m; }
            return lo;
        }
    }

    public struct FrameEffect
    {
        public int KeyFrame;
        public string Name;
    }

    /// <summary>.xmot — eCResourceAnimationMotion_PS (Docs/formats-animation.md §3).</summary>
    public sealed class MotionResource
    {
        public string Name;
        public int Version;
        public uint ResourceSize;
        public float Priority;
        public long NativeFileTime, SecondFileTime;
        public uint NativeFileSize;
        public readonly List<FrameEffect> Effects = new List<FrameEffect>();
        public readonly List<MotionTrack> Tracks = new List<MotionTrack>();
        public float Duration;
        /// <summary>Most common gap between rotation keys (1/30 s if none).</summary>
        public float KeyStep;
        /// <summary>Rotation keys that were renormalised (|q| off by more than 1e-3) or replaced (all-zero).</summary>
        public int FixedKeys;

        public MotionTrack Find(string node)
        {
            foreach (var t in Tracks) if (string.Equals(t.Node, node, StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }
    }

    public static class Xmot
    {
        static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        public static MotionResource Read(byte[] data, string name = null)
        {
            if (data == null || data.Length == 0) return null; // 0-byte entries in the .p00 patch volume delete a clip
            var g = new GenomeReader(data);
            if (!g.IsGenomFile) throw new InvalidDataException($"{name}: not a GENOMFLE resource");
            var r = g.R;
            var m = new MotionResource { Name = name };
            m.Version = r.ReadUInt16();
            m.ResourceSize = r.ReadUInt32();
            m.Priority = r.ReadSingle();
            m.NativeFileTime = r.ReadInt64();
            m.NativeFileSize = r.ReadUInt32();
            m.SecondFileTime = m.Version >= 3 ? r.ReadInt64() : m.NativeFileTime;
            if (m.Version >= 2)
            {
                int n = r.ReadUInt16();
                for (int i = 0; i < n; i++) m.Effects.Add(new FrameEffect { KeyFrame = r.ReadUInt16(), Name = g.Str() });
            }

            int size = r.ReadInt32();
            long end = g.Position + size;
            if (Latin1.GetString(r.ReadBytes(4)) != "LMA ") throw new InvalidDataException($"{name}: no LMA");
            int hi = r.ReadByte(), lo = r.ReadByte();
            if (hi != 1 || lo != 1) throw new InvalidDataException($"{name}: LMA {hi}.{lo}");
            if (r.ReadByte() != 0) throw new InvalidDataException($"{name}: LMA flagged as actor");

            MotionTrack cur = null;
            while (g.Position < end)
            {
                int id = r.ReadInt32();
                int csize = r.ReadInt32();
                int cver = r.ReadInt32();
                long cend = g.Position + csize;
                if (cend > end) throw new InvalidDataException($"{name}: chunk {id} runs past the motion");
                if (id == 1 && cver == 3)
                {
                    cur = new MotionTrack
                    {
                        PosePosition = Xact.V(r), PoseRotation = Xact.Q(r).Normalized, PoseScale = Xact.V(r),
                        BindPosition = Xact.V(r), BindRotation = Xact.Q(r).Normalized, BindScale = Xact.V(r),
                    };
                    cur.Node = Xact.S32(r);
                    m.Tracks.Add(cur);
                }
                else if (id == 2 && cver == 1)
                {
                    if (cur == null) throw new InvalidDataException($"{name}: key track before any motion part");
                    int n = r.ReadInt32();
                    char interp = (char)r.ReadByte(), kind = (char)r.ReadByte();
                    r.ReadUInt16();
                    if (kind == 'R')
                    {
                        var keys = new RotKey[n];
                        for (int i = 0; i < n; i++)
                        {
                            float t = r.ReadSingle();
                            var q = Xact.Q(r);
                            float l = q.Length;
                            if (l < 1e-3f) { q = i > 0 ? keys[i - 1].Value : cur.PoseRotation; m.FixedKeys++; }
                            else { if (Math.Abs(l - 1) > 1e-3f) m.FixedKeys++; q = q.Normalized; }
                            keys[i] = new RotKey { Time = t, Value = q };
                        }
                        cur.Rotation = keys; cur.RotationInterp = interp;
                    }
                    else if (kind == 'P' || kind == 'S')
                    {
                        var keys = new VecKey[n];
                        for (int i = 0; i < n; i++) keys[i] = new VecKey { Time = r.ReadSingle(), Value = Xact.V(r) };
                        if (kind == 'P') { cur.Position = keys; cur.PositionInterp = interp; }
                        else { cur.Scale = keys; cur.ScaleInterp = interp; }
                    }
                    else throw new InvalidDataException($"{name}: key track kind '{kind}'");
                }
                else throw new InvalidDataException($"{name}: chunk {id} v{cver} in a motion");
                if (g.Position != cend) throw new InvalidDataException($"{name}: chunk {id} size mismatch ({g.Position - cend})");
            }
            if (g.Position != end || end != g.PayloadEnd)
                throw new InvalidDataException($"{name}: motion ends at 0x{g.Position:x}, LMA end 0x{end:x}, payload end 0x{g.PayloadEnd:x}");

            float dur = 0;
            var gaps = new Dictionary<int, int>();
            foreach (var t in m.Tracks)
            {
                foreach (var k in t.Position) dur = Math.Max(dur, k.Time);
                foreach (var k in t.Scale) dur = Math.Max(dur, k.Time);
                for (int i = 0; i < t.Rotation.Length; i++)
                {
                    dur = Math.Max(dur, t.Rotation[i].Time);
                    if (i > 0)
                    {
                        int gap = (int)Math.Round((t.Rotation[i].Time - t.Rotation[i - 1].Time) * 1e5);
                        if (gap > 0) gaps[gap] = gaps.TryGetValue(gap, out var c) ? c + 1 : 1;
                    }
                }
            }
            m.Duration = dur;
            int best = 0, bestGap = 0;
            foreach (var kv in gaps) if (kv.Value > best) { best = kv.Value; bestGap = kv.Key; }
            m.KeyStep = best == 0 ? 1f / 30 : bestGap / 1e5f;
            return m;
        }
    }
}
