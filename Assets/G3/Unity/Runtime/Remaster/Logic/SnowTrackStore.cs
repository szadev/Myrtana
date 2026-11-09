using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace G3.Unity
{
    /// <summary>
    /// Persistent snow tracks (fix-178, owner: "может оставить следы условно постоянными, чтобы было проще найти животных"),
    /// engine-free: every print made in a snow region, world-wide, so a wolf's trail can be followed later. A print lasts until
    /// fresh snowfall covers it (its age on the <b>track clock</b>, which only snowfall advances: one unit = one cover,
    /// <c>snowTrackRefillSnowing</c> s of full snowfall; <see cref="SnowV2.AgeGone"/> = gone) or until it is
    /// <c>snowTrackDays</c> game days old. Budget: a ring of <see cref="Capacity"/> records, the oldest dropped first. A
    /// spatial hash of <see cref="BucketSize"/> m buckets answers "which prints lie round the camera" (the 64 m GPU window,
    /// <see cref="SnowPrintGrid"/>, is fed from it). Saved compactly (<see cref="Write"/>: ~10 bytes per print after deflate).
    /// </summary>
    public sealed class SnowTrackStore
    {
        /// <summary>Side of a spatial-hash bucket (m).</summary>
        public const float BucketSize = 16f;

        public SnowTrackStore(int capacity)
        {
            Capacity = Math.Max(16, capacity);
            X = new float[Capacity]; Z = new float[Capacity]; Y = new float[Capacity];
            DirX = new float[Capacity]; DirZ = new float[Capacity]; HalfLength = new float[Capacity]; HalfWidth = new float[Capacity];
            Depth = new float[Capacity]; Rim = new float[Capacity]; Kind = new byte[Capacity]; Side = new sbyte[Capacity];
            Made = new double[Capacity]; Stamp = new double[Capacity]; Alive = new bool[Capacity]; Window = new int[Capacity];
            _bucketOf = new long[Capacity];
            Clear();
        }

        public int Capacity { get; }
        public readonly float[] X, Z, Y, DirX, DirZ, HalfLength, HalfWidth, Depth, Rim;
        public readonly byte[] Kind;
        public readonly sbyte[] Side;
        /// <summary>Game time (s) the print was made.</summary>
        public readonly double[] Made;
        /// <summary>Track clock when the print was made (<see cref="Clock"/>).</summary>
        public readonly double[] Stamp;
        public readonly bool[] Alive;
        /// <summary>Runtime only: the print's ring index in the GPU window (<see cref="SnowPrintGrid"/>), −1 = not in it.</summary>
        public readonly int[] Window;
        readonly long[] _bucketOf;
        readonly Dictionary<long, List<int>> _buckets = new Dictionary<long, List<int>>();
        int _next, _sweep;

        /// <summary>The track clock: advanced by snowfall only (one unit covers a print).</summary>
        public double Clock;
        /// <summary>Live prints.</summary>
        public int Count { get; private set; }
        /// <summary>Prints added since the store was created / cleared (statistics).</summary>
        public long Added { get; private set; }

        public void Clear()
        {
            Array.Clear(Alive, 0, Capacity);
            for (int i = 0; i < Capacity; i++) Window[i] = -1;
            _buckets.Clear();
            _next = 0; _sweep = 0; Count = 0; Added = 0; Clock = 0;
        }

        /// <summary>Advances the track clock: <paramref name="snowfall"/> (0..1.2) for <paramref name="dt"/> s; a print is
        /// covered after <paramref name="coverSeconds"/> s of full snowfall.</summary>
        public void Step(float snowfall, float dt, float coverSeconds)
        {
            if (snowfall > 0 && dt > 0) Clock += Math.Min(snowfall, 1f) * dt / Math.Max(coverSeconds, 1f);
        }

        public static long BucketKey(float x, float z)
        {
            long bx = (long)Math.Floor(x / BucketSize), bz = (long)Math.Floor(z / BucketSize);
            return (bx << 32) ^ (bz & 0xFFFFFFFFL);
        }

        /// <summary>
        /// Adds a print at game time <paramref name="now"/> (stamped with the current <see cref="Clock"/>); the oldest print
        /// makes room when the ring is full (<paramref name="evictedWindow"/> = its GPU window index if it had one, else −1).
        /// </summary>
        public int Add(float x, float z, float dirX, float dirZ, float halfLength, float halfWidth, float groundY, double now,
                       float depth, int kind, float side, float rim, out int evictedWindow)
        {
            int i = _next;
            _next = (_next + 1) % Capacity;
            evictedWindow = Alive[i] ? Window[i] : -1;
            if (Alive[i]) Unlink(i);
            float len = (float)Math.Sqrt(dirX * dirX + dirZ * dirZ);
            if (len < 1e-4f) { dirX = 0; dirZ = 1; } else { dirX /= len; dirZ /= len; }
            X[i] = x; Z[i] = z; Y[i] = groundY; DirX[i] = dirX; DirZ[i] = dirZ; HalfLength[i] = halfLength; HalfWidth[i] = halfWidth;
            Depth[i] = depth; Rim[i] = rim; Kind[i] = (byte)kind; Side[i] = (sbyte)(side < 0 ? -1 : 1);
            Made[i] = now; Stamp[i] = Clock; Window[i] = -1;
            Link(i);
            Added++;
            return i;
        }

        void Link(int i)
        {
            long k = BucketKey(X[i], Z[i]);
            if (!_buckets.TryGetValue(k, out var l)) _buckets[k] = l = new List<int>(8);
            l.Add(i);
            _bucketOf[i] = k;
            Alive[i] = true;
            Count++;
        }

        void Unlink(int i)
        {
            if (_buckets.TryGetValue(_bucketOf[i], out var l))
            {
                int at = l.IndexOf(i);
                if (at >= 0) { l[at] = l[l.Count - 1]; l.RemoveAt(l.Count - 1); }
                if (l.Count == 0) _buckets.Remove(_bucketOf[i]);
            }
            Alive[i] = false;
            Count--;
        }

        /// <summary>Age of print <paramref name="i"/> on the track clock (covers).</summary>
        public float Age(int i) => (float)(Clock - Stamp[i]);

        /// <summary>True when print <paramref name="i"/> is covered by snowfall or older than <paramref name="maxAge"/> game s (0 = no limit).</summary>
        public bool Gone(int i, double now, double maxAge) =>
            Clock - Stamp[i] >= SnowV2.AgeGone || (maxAge > 0 && now - Made[i] > maxAge);

        /// <summary>
        /// Drops covered / too old prints, looking at up to <paramref name="steps"/> ring entries from where the last call
        /// stopped (a little every frame, no hitch). The dropped indices that were in the GPU window go to
        /// <paramref name="removed"/> with their window index.
        /// </summary>
        public int Expire(double now, double maxAge, int steps, List<(int index, int window)> removed)
        {
            int n = 0;
            for (int k = 0; k < steps && k < Capacity; k++)
            {
                int i = _sweep;
                _sweep = (_sweep + 1) % Capacity;
                if (!Alive[i] || !Gone(i, now, maxAge)) continue;
                if (Window[i] >= 0) removed?.Add((i, Window[i]));
                Window[i] = -1;
                Unlink(i);
                n++;
            }
            return n;
        }

        /// <summary>Live prints whose centre lies within <paramref name="radius"/> m of (x, z) (spatial hash).</summary>
        public void Near(float x, float z, float radius, List<int> into)
        {
            into.Clear();
            long b0x = (long)Math.Floor((x - radius) / BucketSize), b1x = (long)Math.Floor((x + radius) / BucketSize);
            long b0z = (long)Math.Floor((z - radius) / BucketSize), b1z = (long)Math.Floor((z + radius) / BucketSize);
            float r2 = radius * radius;
            for (long bz = b0z; bz <= b1z; bz++)
                for (long bx = b0x; bx <= b1x; bx++)
                {
                    if (!_buckets.TryGetValue((bx << 32) ^ (bz & 0xFFFFFFFFL), out var l)) continue;
                    foreach (int i in l)
                    {
                        float dx = X[i] - x, dz = Z[i] - z;
                        if (dx * dx + dz * dz <= r2) into.Add(i);
                    }
                }
        }

        // ------------------------------------------------------------------ save

        const int FormatVersion = 1;

        /// <summary>
        /// perf-2: the live prints copied out of the ring (oldest first), already in their saved units, so the deflate of
        /// <see cref="Encode"/> can run on a worker thread (the crash-recovery snapshot every 2 min spent 16-37 ms of main
        /// thread here). Reused: <see cref="Capture"/> overwrites it.
        /// </summary>
        public sealed class Snapshot
        {
            public double Clock;
            public int Count;
            internal int[] Cx = new int[0], Cy = new int[0], Cz = new int[0];
            internal byte[] Heading = new byte[0], Hl = new byte[0], Hw = new byte[0], KindSide = new byte[0], Dep = new byte[0], RimQ = new byte[0];
            internal ushort[] AgeMin = new ushort[0], Cover = new ushort[0];

            internal void Ensure(int n)
            {
                if (Cx.Length >= n) return;
                int c = Math.Max(n, 256);
                Cx = new int[c]; Cy = new int[c]; Cz = new int[c];
                Heading = new byte[c]; Hl = new byte[c]; Hw = new byte[c]; KindSide = new byte[c]; Dep = new byte[c]; RimQ = new byte[c];
                AgeMin = new ushort[c]; Cover = new ushort[c];
            }

            // perf-5: the ring as it was (plain array copies on the main thread); quantised by Quantize on the encoding thread.
            float[] _x = new float[0], _y = new float[0], _z = new float[0], _dx = new float[0], _dz = new float[0];
            float[] _hl = new float[0], _hw = new float[0], _dep = new float[0], _rim = new float[0];
            byte[] _kind = new byte[0];
            sbyte[] _side = new sbyte[0];
            double[] _made = new double[0], _stamp = new double[0];
            bool[] _alive = new bool[0];
            int _start, _capacity;
            double _now;
            bool _raw;

            internal void CopyRing(SnowTrackStore s, int start, double now)
            {
                int c = s.Capacity;
                if (_x.Length != c)
                {
                    _x = new float[c]; _y = new float[c]; _z = new float[c]; _dx = new float[c]; _dz = new float[c];
                    _hl = new float[c]; _hw = new float[c]; _dep = new float[c]; _rim = new float[c];
                    _kind = new byte[c]; _side = new sbyte[c]; _made = new double[c]; _stamp = new double[c]; _alive = new bool[c];
                }
                Array.Copy(s.X, _x, c); Array.Copy(s.Y, _y, c); Array.Copy(s.Z, _z, c);
                Array.Copy(s.DirX, _dx, c); Array.Copy(s.DirZ, _dz, c);
                Array.Copy(s.HalfLength, _hl, c); Array.Copy(s.HalfWidth, _hw, c);
                Array.Copy(s.Depth, _dep, c); Array.Copy(s.Rim, _rim, c);
                Array.Copy(s.Kind, _kind, c); Array.Copy(s.Side, _side, c);
                Array.Copy(s.Made, _made, c); Array.Copy(s.Stamp, _stamp, c); Array.Copy(s.Alive, _alive, c);
                _start = start; _capacity = c; _now = now; _raw = true;
            }

            /// <summary>The copied ring in saved units, oldest first (any thread; once per <see cref="Capture"/>).</summary>
            internal void Quantize()
            {
                if (!_raw) return;
                _raw = false;
                Ensure(Count);
                int n = 0;
                for (int k = 0; k < _capacity; k++)
                {
                    int i = (_start + k) % _capacity;   // oldest first: the ring order survives a load
                    if (!_alive[i]) continue;
                    Cx[n] = (int)Math.Round(_x[i] * 100); Cy[n] = (int)Math.Round(_y[i] * 100); Cz[n] = (int)Math.Round(_z[i] * 100);
                    double ang = Math.Atan2(_dz[i], _dx[i]);
                    Heading[n] = (byte)((int)Math.Round((ang + Math.PI) / (2 * Math.PI) * 256) & 0xFF);
                    Hl[n] = Q(_hl[i] * 100, 255); Hw[n] = Q(_hw[i] * 100, 255);
                    KindSide[n] = (byte)(_kind[i] & 0x7F | (_side[i] < 0 ? 0x80 : 0));
                    Dep[n] = Q(_dep[i] * 255, 255); RimQ[n] = Q(_rim[i] * 255, 255);
                    AgeMin[n] = (ushort)Math.Max(0, Math.Min(65535, Math.Round((_now - _made[i]) / 60)));
                    Cover[n] = (ushort)Math.Max(0, Math.Min(65535, Math.Round((Clock - _stamp[i]) * 50000)));
                    n++;
                }
                Count = n;
            }
        }

        /// <summary>
        /// Copies the live prints into <paramref name="into"/> (ages relative to <paramref name="now"/>); <see cref="Encode"/>
        /// quantises them oldest first as saved. Cheap on the main thread (perf-5: plain array copies of the ring, ~0.1 ms for
        /// 65k prints, instead of the per-print quantisation that cost 4.5-8.5 ms of the recovery snapshot), no allocation
        /// once <paramref name="into"/> has grown.
        /// </summary>
        public Snapshot Capture(double now, Snapshot into = null)
        {
            into ??= new Snapshot();
            into.Clock = Clock;
            into.Count = Count;
            into.CopyRing(this, _next, now);
            return into;
        }

        /// <summary>
        /// Compact form of the live prints, oldest first (deflated): per print the position as cm deltas to the previous one,
        /// heading (1 byte), size (2 bytes, cm), kind/side (1), depth and rim (1 each), its age in game minutes and on the
        /// track clock (2 bytes each). Ages are relative to <paramref name="now"/>, so loading at another game time works.
        /// </summary>
        public byte[] Write(double now) => Encode(Capture(now));

        /// <summary>The saved bytes of a <see cref="Capture"/> (any thread; the snapshot must not be captured into meanwhile).</summary>
        public static byte[] Encode(Snapshot s)
        {
            s.Quantize();
            var raw = new MemoryStream(16 + s.Count * 16);
            using (var w = new BinaryWriter(raw, System.Text.Encoding.UTF8, true))
            {
                w.Write(FormatVersion);
                w.Write(s.Clock);
                w.Write(s.Count);
                int px = 0, py = 0, pz = 0;
                for (int n = 0; n < s.Count; n++)
                {
                    WriteVar(w, s.Cx[n] - px); WriteVar(w, s.Cy[n] - py); WriteVar(w, s.Cz[n] - pz);
                    px = s.Cx[n]; py = s.Cy[n]; pz = s.Cz[n];
                    w.Write(s.Heading[n]);
                    w.Write(s.Hl[n]); w.Write(s.Hw[n]);
                    w.Write(s.KindSide[n]);
                    w.Write(s.Dep[n]); w.Write(s.RimQ[n]);
                    w.Write(s.AgeMin[n]);
                    w.Write(s.Cover[n]);
                }
            }
            var packed = new MemoryStream();
            using (var d = new DeflateStream(packed, CompressionLevel.Optimal, true)) { raw.Position = 0; raw.CopyTo(d); }
            return packed.ToArray();
        }

        /// <summary>Replaces the store by a saved one (<see cref="Write"/>); prints are re-stamped relative to <paramref name="now"/>.
        /// A save holding more prints than <see cref="Capacity"/> keeps its newest.</summary>
        public void Read(byte[] data, double now)
        {
            Clear();
            if (data == null || data.Length == 0) return;
            var raw = new MemoryStream();
            using (var d = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress)) d.CopyTo(raw);
            raw.Position = 0;
            using var r = new BinaryReader(raw);
            if (r.ReadInt32() != FormatVersion) return;
            Clock = r.ReadDouble();
            int n = r.ReadInt32();
            int px = 0, py = 0, pz = 0;
            for (int k = 0; k < n; k++)
            {
                px += ReadVar(r); py += ReadVar(r); pz += ReadVar(r);
                double ang = r.ReadByte() / 256.0 * 2 * Math.PI - Math.PI;
                float hl = r.ReadByte() / 100f, hw = r.ReadByte() / 100f;
                byte ks = r.ReadByte();
                float depth = r.ReadByte() / 255f, rim = r.ReadByte() / 255f;
                double age = r.ReadUInt16() * 60.0;
                double cover = r.ReadUInt16() / 50000.0;
                int i = Add(px / 100f, pz / 100f, (float)Math.Cos(ang), (float)Math.Sin(ang), hl, hw, py / 100f, now - age, depth,
                            ks & 0x7F, (ks & 0x80) != 0 ? -1 : 1, rim, out _);
                Stamp[i] = Clock - cover;
            }
            Added = 0;
        }

        static byte Q(double v, int max) => (byte)Math.Max(0, Math.Min(max, (int)Math.Round(v)));

        static void WriteVar(BinaryWriter w, int v)
        {
            uint u = (uint)((v << 1) ^ (v >> 31));   // zigzag
            while (u >= 0x80) { w.Write((byte)(u | 0x80)); u >>= 7; }
            w.Write((byte)u);
        }

        static int ReadVar(BinaryReader r)
        {
            uint u = 0; int sh = 0;
            while (true)
            {
                byte b = r.ReadByte();
                u |= (uint)(b & 0x7F) << sh;
                if (b < 0x80) break;
                sh += 7;
            }
            return (int)(u >> 1) ^ -(int)(u & 1);
        }

        // ------------------------------------------------------------------ coarse trails of unseen walkers

        /// <summary>Bipeds (boot prints); everything else leaves paws (G3 species names, <c>NpcDef.Species</c>).</summary>
        public static bool IsBiped(string species) =>
            species == null || species == "Human" || species == "Orc" || species == "Skeleton" || species == "Zombie" ||
            species == "Goblin" || species == "Demon" || species == "Ogre" || species == "Troll" || species == "Golem" ||
            species == "Stalker" || species.StartsWith("Human", StringComparison.Ordinal);

        /// <summary>
        /// Print centres along a walked segment (a, b) of an unseen walker, one every <paramref name="stride"/> m, alternating
        /// feet <paramref name="lateral"/> m to either side; <paramref name="carry"/> holds the distance walked since the last
        /// print and the foot (sign) across calls. Segments longer than <paramref name="maxJump"/> (teleports, a load) print
        /// nothing and restart the stride.
        /// </summary>
        public static int StrideSteps(float ax, float az, float bx, float bz, float stride, float lateral, float maxJump,
                                      ref float carry, ref float foot, List<(float x, float z, float dx, float dz, float side)> into)
        {
            into.Clear();
            float dx = bx - ax, dz = bz - az;
            float len = (float)Math.Sqrt(dx * dx + dz * dz);
            if (len > maxJump) { carry = 0; return 0; }
            if (len < 1e-4f || stride <= 0) return 0;
            float ux = dx / len, uz = dz / len;
            float t = stride - carry;
            while (t <= len)
            {
                foot = foot < 0 ? 1 : -1;
                into.Add((ax + ux * t + uz * lateral * foot, az + uz * t - ux * lateral * foot, ux, uz, foot));   // right = (uz, -ux)
                t += stride;
            }
            carry = len - (t - stride);
            return into.Count;
        }
    }
}
