namespace G3.Unity
{
    /// <summary>
    /// perf-6: frame cap of an editor Play session while the owner plays a built game on the same PC (Docs/performance.md,
    /// "Fire Clan: the editor shares the GPU"). An unfocused editor in Play renders its Game view at full rate (runInBackground)
    /// and takes about half of the GPU from the player: measured, the editor's camera GPU time went from 12 ms alone to
    /// 30-55 ms while the player rendered, so the player lost as much. Engine-free; driven by the editor's
    /// <c>G3EditorPlayThrottleDriver</c>.
    /// </summary>
    public static class EditorPlayThrottle
    {
        /// <summary>Frames per second of a throttled editor Play session.</summary>
        public const int ThrottledFps = 10;

        /// <summary>
        /// The editor's <c>Application.targetFrameRate</c>: <see cref="ThrottledFps"/> while a game player runs, the editor is
        /// not the active app and nothing measures; otherwise null (leave the game's own value).
        /// </summary>
        public static int? Cap(bool playerRunning, bool editorFocused, bool measuring, bool disabled) =>
            playerRunning && !editorFocused && !measuring && !disabled ? ThrottledFps : (int?)null;
    }
}
