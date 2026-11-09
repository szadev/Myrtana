namespace Myrtana.Ui.Skin
{
    /// <summary>
    /// Where the port's own rows sit on the Video page (page px of 768; <c>G3MenuPortRows.AddVideoRows</c>): above G3's
    /// Resolution row (420), 28 px apart like the page's rows — Arrows finish downed foes (56, fix-241), Show helmet (84, fix-232), Torch glow (112, fix-182), Keep wounds after load (140, fix-175), Show FPS (168, fix-172), Forgive accidental hits (196, fix-162), Freeing by force counts (224, fix-161), Crash recovery (252, fix-155), Continue button (280, fix-138), Camera in windows (308, fix-122), Shield protects from beasts (336, fix-109), Upscaling (364)
    /// and Field of view (392); fix-116 removed fix-102's "Locked chests on map" row (336; Shield took its place). Nothing
    /// of G3's layout lies above Resolution (the "Gothic" logo is only painted into the
    /// background, as on the Audio page whose port rows sit at 312 / 340). Each row: a 25 px label at <c>top</c>, a 25 px
    /// slider at <c>top + 4</c>.
    /// </summary>
    public static class VideoPortRowsLayout
    {
        public const float Pitch = 28f;
        /// <summary>Top of G3's IDC_LABEL_VIDEO_RESOLUTION_2.</summary>
        public const float ResolutionTop = 420f;
        public const float FovTop = ResolutionTop - Pitch;            // 392
        public const float UpscalingTop = FovTop - Pitch;             // 364
        /// <summary>fix-109: the Difficulty strip (2 rows) and the Controls bands are full; one row above Upscaling (fix-116: was 308, above fix-102's row).</summary>
        public const float ShieldCreaturesTop = UpscalingTop - Pitch; // 336
        /// <summary>fix-122 [owner]: "Camera in windows" one row above Shield (fix-102's old 308, free since fix-116).</summary>
        public const float WindowCameraTop = ShieldCreaturesTop - Pitch; // 308
        /// <summary>fix-138 [owner]: "Continue button" one row above Camera in windows (the free 280 row).</summary>
        public const float ContinueButtonTop = WindowCameraTop - Pitch; // 280
        /// <summary>fix-155 [owner]: "Crash recovery" one row above Continue button (the free 252 row).</summary>
        public const float CrashRecoveryTop = ContinueButtonTop - Pitch; // 252
        /// <summary>fix-161 [owner]: "Freeing by force counts" one row above Crash recovery (the free 224 row).</summary>
        public const float ForcedReleaseTop = CrashRecoveryTop - Pitch; // 224
        /// <summary>fix-162 [owner]: "Forgive accidental hits" one row above Freeing by force counts (the free 196 row; nothing of G3's
        /// layout lies above Resolution, G3Cli gui dump IDD_MENU_OPTIONS_VIDEO_2).</summary>
        public const float ForgiveHitsTop = ForcedReleaseTop - Pitch; // 196
        /// <summary>fix-172 [owner]: "Show FPS" one row above Forgive accidental hits (the free 168 row).</summary>
        public const float ShowFpsTop = ForgiveHitsTop - Pitch; // 168
        /// <summary>fix-175 [owner]: "Keep wounds after load" one row above Show FPS (the free 140 row, inside the page's
        /// frame, which starts at ~25 px).</summary>
        public const float KeepWoundsTop = ShowFpsTop - Pitch; // 140
        /// <summary>fix-182 [owner]: "Torch glow" one row above Keep wounds after load (the free 112 row, inside the page's frame).</summary>
        public const float TorchGlowTop = KeepWoundsTop - Pitch; // 112
        /// <summary>fix-232 [owner]: "Show helmet" one row above Torch glow (the free 84 row, inside the page's frame from ~25 px).</summary>
        public const float ShowHelmetTop = TorchGlowTop - Pitch; // 84
        /// <summary>fix-241 [owner]: "Arrows finish downed foes" one row above Show helmet (the free 56 row, inside the page's frame from ~25 px).</summary>
        public const float RangedFinishTop = ShowHelmetTop - Pitch; // 56
        /// <summary>The slider's offset below the row top and its height (<c>G3MenuPortRows.AddRow</c>).</summary>
        public const float SliderOffset = 4f, SliderHeight = 25f;

        /// <summary>The bottom of a row's slider (its lowest element).</summary>
        public static float RowBottom(float top) => top + SliderOffset + SliderHeight;
    }
}
