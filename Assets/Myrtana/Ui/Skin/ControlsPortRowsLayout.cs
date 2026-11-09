namespace Myrtana.Ui.Skin
{
    /// <summary>
    /// Where the port's own rows sit on the Controls page (page px of 768; <c>G3MenuPortRows.AddControlsRows</c>): the free
    /// band between the keyboard layout radios (Standard 532, Custom 568, which ends at ~593) and the Back button (top 682).
    /// Two rows share it — Q / E (fix-73) and Focus decorative objects (fix-79) — at the port rows' 28 px pitch, centred in
    /// the band: each row is a 25 px label at <c>top</c> and a 25 px slider at <c>top + 4</c> (609..666). fix-107 removed the
    /// third row, Quick saves (fix-82), with the quicksave rotation.
    /// </summary>
    public static class ControlsPortRowsLayout
    {
        /// <summary>Bottom of the Custom keyboard layout radio (568 + 25).</summary>
        public const float CustomRadioBottom = 593f;
        /// <summary>Top of the Back button.</summary>
        public const float BackTop = 682f;
        /// <summary>Row pitch (as the port's other rows).</summary>
        public const float Pitch = 28f;
        /// <summary>The slider's offset below the row top and its height (<c>G3MenuPortRows.AddRow</c>).</summary>
        public const float SliderOffset = 4f, SliderHeight = 25f;

        /// <summary>fix-205: three rows centred in the band 593..682 (595 / 623 / 651, the last slider ending at 680); two were at 609 / 637.</summary>
        public const float TurnKeysTop = 595f;
        public const float DecorativeFocusTop = TurnKeysTop + Pitch;   // 623
        /// <summary>"Take all: Ctrl or RMB + click" (fix-205, [owner] default Yes).</summary>
        public const float QuickLootChordTop = DecorativeFocusTop + Pitch; // 651

        /// <summary>Top of G3's "Mouse" heading (IDC_LABEL_CONTROLS_MOUSE_2, 141..166), the first element of the page.</summary>
        public const float MouseHeadingTop = 141f;
        /// <summary>Top of the inner frame painted into the page's background (y ~65).</summary>
        public const float FrameTop = 65f;
        /// <summary>
        /// "Cook on any fire" (fix-88): the free band at the top of the page between the frame (~65) and the Mouse heading
        /// (141), in the mouse rows' columns; fix-143 moved it from 104 down to 107 (slider ends at 136, 5 px above the
        /// heading) to share the band with "Late trophies".
        /// </summary>
        public const float CookFireTop = 107f;
        /// <summary>"Late trophies" (fix-143, [owner]): one 28 px row above Cook on any fire (label 79..104, 14 px below the frame).</summary>
        public const float LateTrophiesTop = CookFireTop - Pitch; // 79

        /// <summary>The bottom of a row's slider (its lowest element).</summary>
        public static float RowBottom(float top) => top + SliderOffset + SliderHeight;
    }
}
