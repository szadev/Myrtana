using System;

namespace Myrtana.Ui.Skin
{
    /// <summary>
    /// Text sizes of the original GUI mapped to the Gothic3 face (Docs/ui-skin.md "Fonts"). G3 creates every font with
    /// D3DXCreateFont, whose height is a Win32 LOGFONT height:
    /// <list type="bullet">
    /// <item>res and theme fonts store <b>negative</b> heights (−18 = an 18 px em) with the face "Times New Roman"; the
    /// face goes to D3DX unchanged (eCGUIAdmin::CreateFont → eCGUIDatabase::CreateFont copies it), so those texts are
    /// Times-sized: caps 0.66 em, line 1.107 em — an 18 px label fits its 25 px row;</item>
    /// <item>code-drawn fonts use <b>positive</b> heights = whole cell (ascent + descent): the default fonts 16 bold / 14,
    /// game messages 20 bold, focus names 24 weight 800 — all in Gothic3, whose cell is 2.16 em, so the 24 px focus name
    /// has 11 px caps.</item>
    /// </list>
    /// The skin draws everything with Gothic3. Its em is scaled by <see cref="ResFaceScale"/> so that a res size gives
    /// Times' cap height (and nearly its x-height); sizes in code therefore stay the res numbers, and code-drawn cell
    /// heights convert with <see cref="FromCellHeight"/>.
    /// </summary>
    public static class G3FontMetrics
    {
        const float Gothic3UnitsPerEm = 2048f;
        /// <summary>Gothic3.ttf cap height (H) and hhea ascent + descent in font units.</summary>
        const float Gothic3CapHeight = 2200f, Gothic3Cell = 3644f + 776f;
        /// <summary>Times New Roman cap height per em (1356 / 2048).</summary>
        public const float TimesCapHeightEm = 1356f / 2048f;

        public static float Gothic3CapHeightEm => Gothic3CapHeight / Gothic3UnitsPerEm;
        /// <summary>Gothic3 line (cell) height per em: 2.16.</summary>
        public static float Gothic3LineEm => Gothic3Cell / Gothic3UnitsPerEm;

        /// <summary>Gothic3 em per res font size (0.616): Times' cap height at the res size.</summary>
        public static float ResFaceScale => TimesCapHeightEm / Gothic3CapHeightEm;

        /// <summary>Line height of the scaled Gothic3 face at a res size (1.33 × size; a 15 px message line = 20 px).</summary>
        public static float LineHeight(float size) => size * ResFaceScale * Gothic3LineEm;

        /// <summary>
        /// Times New Roman's GDI line height per em ((winAscent 1825 + winDescent 443) / 2048 = 1.107): the distance of
        /// wrapped lines that D3DXFont/DrawText gives a res text, e.g. 18 px for the 16 px bold theme text.
        /// </summary>
        public const float TimesLineEm = (1825f + 443f) / 2048f;

        /// <summary>
        /// Distance of wrapped lines of a multi-line res text (tip box, documents) at res size <paramref name="size"/>,
        /// as in the original (Times' line, whole pixels); the scaled Gothic3 would set them 20 % further apart.
        /// </summary>
        public static float ParagraphLineHeight(float size) => (float)Math.Ceiling(size * TimesLineEm - 0.01f);

        /// <summary>
        /// Res-equivalent size of a code-drawn Gothic3 font created with a positive LOGFONT (cell) height: the 24 px
        /// focus name → 18, the 20 px game messages → 15.
        /// </summary>
        public static float FromCellHeight(int cellHeight) => cellHeight / Gothic3LineEm / ResFaceScale;

        /// <summary>
        /// Code points of Gothic3.ttf whose outlines are trustworthy: printable ASCII and the Latin-1 characters drawn
        /// in the Gothic3 style (or at least as the right character). Everything else in the font is a placeholder from
        /// the font editor's template — Cyrillic as Greek-like shapes, many Latin-1/Latin Extended-A, quote, dash and
        /// symbol code points as unrelated glyphs (¤ → ź, ² → ŕ, À → ˆ, ’ → Ł, € → ¦) — and comes from the fallback.
        /// Found by rendering every glyph and matching it against Arial (Work/scratch/fonts/visaudit.py) plus a visual check.
        /// </summary>
        public static bool IsTrustedGlyph(int codePoint) => IsTrustedGlyph(codePoint, false);

        /// <summary>
        /// <see cref="IsTrustedGlyph(int)"/>, plus — for the font written by Tools/Fonts/g3cyrillic.py
        /// (<paramref name="withCyrillic"/>) — the <see cref="IsSynthesizedGlyph">synthesised</see> Cyrillic glyphs.
        /// </summary>
        public static bool IsTrustedGlyph(int codePoint, bool withCyrillic)
        {
            if (codePoint >= 0x20 && codePoint <= 0x7E) return true;
            if (withCyrillic && IsSynthesizedGlyph(codePoint)) return true;
            return codePoint >= 0xA0 && codePoint <= 0xFF && Array.IndexOf(TrustedLatin1, codePoint) >= 0;
        }

        /// <summary>
        /// Code points that Tools/Fonts/g3cyrillic.py composes from Gothic3's own Latin outlines: А–я, Ё, ё, №, « », – and —,
        /// the curly quotes ‘ ’ “ ” „ and …. They replace the template placeholders at the same code points, so they are
        /// trusted only in that font.
        /// </summary>
        public static bool IsSynthesizedGlyph(int codePoint) =>
            codePoint >= 0x410 && codePoint <= 0x44F || codePoint == 0x401 || codePoint == 0x451 ||
            codePoint == 0x2116 || codePoint == 0xAB || codePoint == 0xBB || codePoint == 0x2013 || codePoint == 0x2014 ||
            codePoint == 0x2018 || codePoint == 0x2019 || codePoint == 0x201C || codePoint == 0x201D || codePoint == 0x201E ||
            codePoint == 0x2026;

        static readonly int[] TrustedLatin1 =
        {
            0xA0, 0xA2, 0xA3, 0xA7, 0xA9, 0xAD, 0xAE, 0xB0, 0xB4, 0xB6,
            0xC4, 0xC5, 0xC7, 0xC9, 0xD1, 0xD6, 0xDC, 0xDF,
            0xE0, 0xE1, 0xE2, 0xE3, 0xE4, 0xE5, 0xE7, 0xE8, 0xE9, 0xEA, 0xEB, 0xEC, 0xED, 0xEE, 0xEF,
            0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF9, 0xFA, 0xFB, 0xFC,
        };
    }
}
