namespace Myrtana.Ui.Skin
{
    /// <summary>
    /// Where the port's own rows sit on the Audio page (page px of 768; <c>G3MenuPortRows.AddAudioRows</c>; G3's layout from
    /// <c>G3Cli gui dump IDD_MENU_OPTIONS_AUDIO_2</c>): Music transitions (312) and Combat music (340) above the voice
    /// language row (368); "NPC shout subtitles" (fix-156) in G3's own empty row between Ambient Volume (label 508..533,
    /// slider 512..537) and Text Language (label 564..589), next to G3's Subtitles row (592). Each row: a 25 px label at
    /// <c>top</c>, a 25 px slider at <c>top + 4</c>.
    /// </summary>
    public static class AudioPortRowsLayout
    {
        public const float Pitch = 28f;
        public const float MusicTransitionsTop = 312f, CombatMusicTop = MusicTransitionsTop + Pitch; // 340
        /// <summary>Top of G3's IDC_LABEL_AUDIO_VOLUME_AMBIENT_2 and IDC_LABEL_AUDIO_LANGUAGE_SUBTITLES_2.</summary>
        public const float AmbientTop = 508f, TextLanguageTop = 564f;
        /// <summary>Bottom of G3's ambient slider (512 + 25).</summary>
        public const float AmbientSliderBottom = 537f;
        /// <summary>fix-156 [owner]: the free row between Ambient Volume and Text Language.</summary>
        public const float NpcShoutSubtitlesTop = AmbientTop + Pitch; // 536
        /// <summary>The slider's offset below the row top and its height (<c>G3MenuPortRows.AddRow</c>).</summary>
        public const float SliderOffset = 4f, SliderHeight = 25f;
        /// <summary>The bottom of a row's slider (its lowest element).</summary>
        public static float RowBottom(float top) => top + SliderOffset + SliderHeight;
    }
}
