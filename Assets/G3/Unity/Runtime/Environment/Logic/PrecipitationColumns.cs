using System;
using System.Collections.Generic;

namespace G3.Weather
{
    /// <summary>
    /// Where rain and snow may fall (G3 eCPrecipitation_PS::Update, Engine.dll CP 1.75 0x10293190 [verified calls]): G3 keeps
    /// spawn points over the precipitation cube and traces each one once with a physics ray along the fall direction
    /// from far above (start = point − dir · 20 · cube, length 22 · cube, eCPhysicsScene::TraceRayNearestHit); a drop of
    /// that point dies where the ray hit and leaves a splash there. Under a roof the ray hits the roof, so no rain falls
    /// indoors — there is no sector/portal test. Here the points are world-anchored columns of <see cref="CellSize"/>;
    /// each column is traced once (cached until it leaves the cube) through the <c>trace</c> delegate, at most
    /// <see cref="TracesPerFrame"/> new traces per frame (drops of untraced columns wait).
    /// fix-130 (owner: rain inside a mill): a column was traced once and kept while the camera stayed near it, so a
    /// column traced before the building around it had streamed in (a loaded save, a cell coming in) rained through
    /// the roof for good. Columns older than <see cref="MaxAge"/> seconds are traced again (from the frame's budget,
    /// the stale height is used meanwhile).
    /// </summary>
    public sealed class PrecipitationColumns
    {
        /// <summary>Returns the height of the first hit below the top of the world at (x, z), or null for none.</summary>
        readonly Func<float, float, float?> _trace;
        readonly Dictionary<long, (float y, double at)> _hits = new Dictionary<long, (float, double)>();
        int _tracesLeft;
        double _now;

        public const float NoHit = float.NegativeInfinity;

        public PrecipitationColumns(float cellSize, Func<float, float, float?> trace, int tracesPerFrame = 48, float maxAge = 3f)
        {
            CellSize = cellSize > 0 ? cellSize : 0.5f;
            MaxAge = maxAge;
            _trace = trace ?? throw new ArgumentNullException(nameof(trace));
            TracesPerFrame = Math.Max(1, tracesPerFrame);
            _tracesLeft = TracesPerFrame;
        }

        public float CellSize { get; }
        public int TracesPerFrame { get; }
        /// <summary>Seconds a traced column is trusted; older ones are traced again when the budget allows (≤ 0: never).</summary>
        public float MaxAge { get; }
        public int Cached => _hits.Count;

        public static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;
        public int Cell(float v) => (int)Math.Floor(v / CellSize);

        /// <summary>A new frame: the trace budget is refilled.</summary>
        public void BeginFrame() => _tracesLeft = TracesPerFrame;

        /// <summary>A new frame at <paramref name="now"/> seconds: the trace budget is refilled, columns age.</summary>
        public void BeginFrame(double now)
        {
            _now = now;
            _tracesLeft = TracesPerFrame;
        }

        /// <summary>
        /// The ground (or roof) height under (x, z): true with <paramref name="hitY"/> (<see cref="NoHit"/> = nothing below),
        /// false when the column is not traced yet and the frame's budget is spent.
        /// </summary>
        public bool TryGround(float x, float z, out float hitY)
        {
            long k = Key(Cell(x), Cell(z));
            bool known = _hits.TryGetValue(k, out var e);
            hitY = e.y;
            if (known && (MaxAge <= 0 || _now - e.at <= MaxAge)) return true;
            if (_tracesLeft <= 0) return known; // stale: used until the budget allows a new trace
            _tracesLeft--;
            float cx = (Cell(x) + 0.5f) * CellSize, cz = (Cell(z) + 0.5f) * CellSize;
            hitY = _trace(cx, cz) ?? NoHit;
            _hits[k] = (hitY, _now);
            return true;
        }

        /// <summary>Forgets columns farther than <paramref name="radius"/> from (x, z) (they are traced again when needed).</summary>
        public void Prune(float x, float z, float radius)
        {
            int cx = Cell(x), cz = Cell(z), r = (int)Math.Ceiling(radius / CellSize) + 1;
            List<long> drop = null;
            foreach (var k in _hits.Keys)
            {
                int kx = (int)(k >> 32), kz = (int)(k & 0xffffffffL);
                if (Math.Abs(kx - cx) <= r && Math.Abs(kz - cz) <= r) continue;
                (drop ??= new List<long>()).Add(k);
            }
            if (drop != null) foreach (var k in drop) _hits.Remove(k);
        }

        public void Clear() => _hits.Clear();

        /// <summary>
        /// Seconds a drop spawned at <paramref name="spawnY"/> falling at <paramref name="speed"/> lives: until the hit, at
        /// most until <paramref name="bottomY"/> (the cube's floor). 0 = no drop (the hit is above the spawn: indoors).
        /// <paramref name="splash"/>: the drop ends on the hit (a splash there).
        /// </summary>
        public static float FallSeconds(float spawnY, float hitY, float bottomY, float speed, out bool splash) =>
            FallSeconds(spawnY, hitY, bottomY, speed, 0f, out splash);

        /// <summary>
        /// <see cref="FallSeconds(float,float,float,float,out bool)"/> for a drop drawn <paramref name="halfLength"/> m
        /// below its position (a streak is centred on it): it dies that much above the hit, so no part of it is drawn
        /// under a roof (fix-130).
        /// </summary>
        public static float FallSeconds(float spawnY, float hitY, float bottomY, float speed, float halfLength, out bool splash)
        {
            splash = false;
            if (halfLength > 0 && hitY > float.NegativeInfinity) hitY += halfLength;
            if (speed <= 0 || hitY >= spawnY) return 0f;
            float end = hitY;
            if (hitY >= bottomY) splash = true;
            else end = bottomY;
            return (spawnY - end) / speed;
        }
    }

    /// <summary>Splashes due when their drop lands (eCPrecipitation_PS SplashDuration / SplashTextureTile / SplashSizeScale).</summary>
    public sealed class SplashQueue
    {
        readonly List<(double at, float x, float y, float z)> _due = new List<(double, float, float, float)>();
        public int Capacity { get; }
        public int Count => _due.Count;

        public SplashQueue(int capacity = 1024) => Capacity = Math.Max(1, capacity);

        /// <summary>Queues a splash at <paramref name="at"/> (seconds); dropped when full.</summary>
        public bool Add(double at, float x, float y, float z)
        {
            if (_due.Count >= Capacity) return false;
            _due.Add((at, x, y, z));
            return true;
        }

        /// <summary>Takes every splash due at <paramref name="now"/> into <paramref name="into"/>.</summary>
        public int TakeDue(double now, List<(float x, float y, float z)> into)
        {
            int n = 0;
            for (int i = _due.Count - 1; i >= 0; i--)
            {
                if (_due[i].at > now) continue;
                into.Add((_due[i].x, _due[i].y, _due[i].z));
                _due[i] = _due[_due.Count - 1];
                _due.RemoveAt(_due.Count - 1);
                n++;
            }
            return n;
        }

        public void Clear() => _due.Clear();
    }
}
