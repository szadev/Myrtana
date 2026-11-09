using System;

namespace G3.Effects
{
    /// <summary>
    /// The screen parts of G3 effects (VFX_*_Screen*, VFX_*_WhiteBlend*: soul travel load/ghost, Dusk to Dawn and Time Bubble
    /// loads, Heaven Zap cast). G3 spawns them as ordinary sprites, but 20–140 m wide, without depth test or fog, at the caster:
    /// the camera sits inside them and sees one window of their texture over the whole view — a full-screen tint whose look
    /// is the texture's part around the view (the soul travel white blend: the nearly clear middle of VFX_Magic_InnosCloud_01,
    /// a faint white haze). The runtime draws them as screen overlays (G3ScreenOverlay) with this projection, never as world
    /// particles (editor-runner-9/14: a dense white cloud around the camera) [interp: the G3 effect files carry no screen
    /// flag; the template names say what they are for].
    /// </summary>
    public static class ScreenSpriteRules
    {
        /// <summary>A screen-overlay emitter by its template name.</summary>
        public static bool IsScreenTemplate(string name) =>
            !string.IsNullOrEmpty(name) &&
            (name.IndexOf("_Screen", StringComparison.OrdinalIgnoreCase) >= 0 ||
             name.IndexOf("_WhiteBlend", StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>
        /// A camera-facing sprite of width/height <paramref name="size"/> centred at camera-space (x, y, z) (z forward),
        /// projected onto the plane at depth <paramref name="planeDepth"/>: the same screen rectangle, drawn where the near
        /// plane cannot clip it. False when the sprite is behind the camera (G3 does not draw it either).
        /// </summary>
        public static bool ProjectToPlane(float x, float y, float z, float size, float planeDepth,
                                          out float cx, out float cy, out float half)
        {
            cx = cy = half = 0f;
            if (z <= 1e-3f || size <= 0f) return false;
            float k = planeDepth / z;
            cx = x * k;
            cy = y * k;
            half = 0.5f * size * k;
            return true;
        }

        /// <summary>
        /// Corner <paramref name="corner"/> (0 bottom-left, 1 top-left, 2 top-right, 3 bottom-right) of the projected quad
        /// centred at (cx, cy) with half size <paramref name="half"/>, rolled by <paramref name="spinDegrees"/> in the view
        /// plane. The G3 screen sprites spin (WhiteBlend: a random start roll, ±0.02 rev/s); unrolled, every sprite showed the
        /// same few texels of its texture centre magnified over the view, so their noise blobs stacked into a dense cloud
        /// (editor-runner-16) where G3's randomly rolled layers average to an even haze.
        /// </summary>
        public static void Corner(float cx, float cy, float half, float spinDegrees, int corner, out float x, out float y)
        {
            float sx = corner >= 2 ? half : -half, sy = corner == 1 || corner == 2 ? half : -half;
            double a = spinDegrees * (Math.PI / 180.0);
            float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
            x = cx + sx * c - sy * s;
            y = cy + sx * s + sy * c;
        }

        /// <summary>
        /// Colour gain of screen sprites: 1, not the additive <see cref="EmitterRules.AdditiveGain"/> [interp]. That gain lifts
        /// dense stacks of flame sprites to G3's gamma-space sum; a screen part is a thin tint (soul travel white blend:
        /// ≈ 13 live sprites × colour alpha ≈ 0.25 × texture centre alpha ≈ 0.05 ≈ 0.15 added in G3), where the plain linear
        /// sum already matches G3 over mid-bright scenes and 1.5× turned the faint haze into a white wall with bloom.
        /// </summary>
        public const float ScreenGain = 1f;
    }
}
