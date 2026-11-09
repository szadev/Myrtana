namespace G3.Unity
{
    // Engine-free texts and timing of the in-game world remaster switch (options row and hotkey; Docs/build.md),
    // compiled into G3.Unity and into Tools/G3Tests.

    /// <summary>Texts of the world remaster switch in the two text languages (other languages use English).</summary>
    public static class WorldRemasterToggle
    {
        /// <summary>How long the on-screen state label stays after a flip.</summary>
        public const float LabelSeconds = 2f;

        static bool Russian(string language) => language == "Russian";

        /// <summary>The short state label shown after a flip: Original / Remastered.</summary>
        public static string Label(bool on, string language) =>
            Russian(language) ? (on ? "Ремастер" : "Оригинал") : (on ? "Remastered" : "Original");

        /// <summary>The options row name.</summary>
        public static string OptionName(string language) => Russian(language) ? "Ремастер" : "Remaster";

        /// <summary>The options row value: Original / Remastered world.</summary>
        public static string OptionValue(bool on, string language) =>
            Russian(language) ? (on ? "Обновлённый мир" : "Оригинал") : (on ? "Remastered world" : "Original");

        /// <summary>True while the label of a flip at <paramref name="flippedAt"/> is on screen at <paramref name="now"/> (-1: never flipped).</summary>
        public static bool LabelVisible(float flippedAt, float now) => flippedAt >= 0 && now >= flippedAt && now - flippedAt < LabelSeconds;
    }
}
