namespace G3.Unity
{
    /// <summary>
    /// Update rate of an NPC view by distance and visibility (Docs/performance.md, "NPC update LOD", perf-8 / perf-3). The
    /// view's routine (NpcView), animation selector (G3NpcAnimator) and motion player (G3MotionPlayer) run together on the
    /// same frames with the time accumulated since their last run, so walking speed, clip phase and effect events stay right;
    /// only the step size grows. Engine-free (unit-tested in Tools/G3Tests).
    /// <para><see cref="Interval"/> is the view's tier (1..4); the perception scan rate is scaled by it (PerceptionScanner,
    /// unchanged since perf-8):</para>
    /// <list type="bullet">
    /// <item>closer than <see cref="FullDistance"/>, or important (fighting, talking, escorting, a claimed or overlaid
    /// motion player): 1, every frame — unchanged near the player;</item>
    /// <item>visible, up to <see cref="HalfDistance"/>: 2; visible beyond: 3;</item>
    /// <item>not visible (no camera or shadow view renders the body): 4.</item>
    /// </list>
    /// <para>perf-3: the frames between two steps of a tier follow the frame rate (<see cref="Frames"/>): tier 2 steps at
    /// ~30 Hz, tier 3 at ~15 Hz, tier 4 at ~5 Hz, but never more often than every <see cref="Interval"/>-th frame (perf-8's
    /// rate at low frame rates). At 120 FPS an NPC out of view steps every 24th frame instead of every 4th. A calm NPC
    /// between <see cref="NearHiddenDistance"/> and <see cref="FullDistance"/> that no view renders (behind the camera, its
    /// shadow off screen) keeps tier 1 but steps at tier 2's rate (<see cref="PresentationFrames"/>).</para>
    /// </summary>
    public static class NpcUpdateLod
    {
        public const float FullDistance = 30f;
        public const float HalfDistance = 60f;
        public const int MaxInterval = 4;
        /// <summary>Most frames between two steps (a tier-4 view at 240 FPS: 7.5 Hz).</summary>
        public const int MaxFrames = 32;
        /// <summary>Nearer than this (m) a calm NPC steps every frame even out of view (it may come into view any moment).</summary>
        public const float NearHiddenDistance = 10f;

        /// <summary>The view's tier: frames between two updates at perf-8's rates (1 = every frame).</summary>
        public static int Interval(bool enabled, bool important, float distance, bool visible)
        {
            if (!enabled || important || !(distance >= FullDistance)) return 1;   // NaN distance: full rate
            if (!visible) return MaxInterval;
            return distance < HalfDistance ? 2 : 3;
        }

        /// <summary>Seconds between two steps of a tier (0 = every frame).</summary>
        public static float PeriodOf(int interval)
        {
            switch (interval)
            {
                case 1: return 0f;
                case 2: return 1f / 30f;
                case 3: return 1f / 15f;
                default: return interval <= 0 ? 0f : 1f / 5f;
            }
        }

        /// <summary>
        /// Frames between two steps of tier <paramref name="interval"/> at a frame time of <paramref name="frameSeconds"/>:
        /// the tier's period in frames (rounded), at least the tier itself, at most <see cref="MaxFrames"/>.
        /// </summary>
        public static int Frames(int interval, float frameSeconds)
        {
            if (interval <= 1) return 1;
            float period = PeriodOf(interval);
            int frames = frameSeconds > 0f ? (int)(period / frameSeconds + 0.5f) : interval;   // NaN / 0: the tier
            if (frames < interval) frames = interval;
            return frames > MaxFrames ? MaxFrames : frames;
        }

        /// <summary>
        /// Frames between two animation / routine steps of a view: <see cref="Frames"/> of its tier, and for a calm tier-1 view
        /// at <see cref="NearHiddenDistance"/> or more that is not visible, tier 2's.
        /// </summary>
        public static int PresentationFrames(int interval, bool important, float distance, bool visible, float frameSeconds)
        {
            if (interval == 1 && !important && distance >= NearHiddenDistance && !visible) return Frames(2, frameSeconds);
            return Frames(interval, frameSeconds);
        }

        /// <summary>Whether frame <paramref name="frame"/> is an update frame for a view with this interval and phase.</summary>
        public static bool Due(int frame, int interval, int phase)
        {
            if (interval <= 1) return true;
            int m = (frame + phase) % interval;
            return m == 0;
        }
    }
}
