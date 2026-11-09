namespace G3.Formats.Gui
{
    /// <summary>Small measurements on decoded GUI bitmaps (RGBA32, top row first).</summary>
    public static class GuiImageAnalysis
    {
        /// <summary>
        /// Detects a frame image — an opaque border around a (nearly) transparent centre, like the bronze tile frame
        /// <c>listctrl_item_tiles_front</c> — and returns its border widths measured along the middle row/column.
        /// Returns false for anything else (filled images, bars, icons).
        /// </summary>
        public static bool TryDetectFrame(byte[] rgba, int width, int height, out int left, out int top, out int right, out int bottom,
                                          int alphaThreshold = 64)
        {
            left = top = right = bottom = 0;
            if (width < 8 || height < 8) return false;
            int A(int x, int y) => rgba[(y * width + x) * 4 + 3];
            int cx = width / 2, cy = height / 2;
            if (A(cx, cy) >= alphaThreshold) return false;
            if (A(0, cy) < alphaThreshold || A(width - 1, cy) < alphaThreshold || A(cx, 0) < alphaThreshold || A(cx, height - 1) < alphaThreshold)
                return false;
            while (left < cx && A(left, cy) >= alphaThreshold) left++;
            while (right < cx && A(width - 1 - right, cy) >= alphaThreshold) right++;
            while (top < cy && A(cx, top) >= alphaThreshold) top++;
            while (bottom < cy && A(cx, height - 1 - bottom) >= alphaThreshold) bottom++;
            // a frame is thin compared to the image
            return left > 0 && right > 0 && top > 0 && bottom > 0
                   && left < width / 3 && right < width / 3 && top < height / 3 && bottom < height / 3;
        }

        /// <summary>
        /// Turns a single-colour image whose shape lives only in its alpha (the soft list-row bars) into the same alpha on
        /// white, so a tint colour shows as itself instead of being multiplied by the bar's own colour. Returns false and
        /// leaves the pixels alone when the visible pixels have more than one colour.
        /// </summary>
        public static bool TryWhitenAlphaMask(byte[] rgba)
        {
            int r = -1, g = -1, b = -1;
            for (int i = 0; i < rgba.Length; i += 4)
            {
                if (rgba[i + 3] == 0) continue;
                if (r < 0) { r = rgba[i]; g = rgba[i + 1]; b = rgba[i + 2]; }
                else if (rgba[i] != r || rgba[i + 1] != g || rgba[i + 2] != b) return false;
            }
            if (r < 0) return false;
            for (int i = 0; i < rgba.Length; i += 4) rgba[i] = rgba[i + 1] = rgba[i + 2] = 255;
            return true;
        }
    }
}
