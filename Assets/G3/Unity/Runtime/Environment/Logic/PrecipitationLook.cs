using System;

namespace G3.Weather
{
    /// <summary>
    /// How a precipitation state is drawn (fix-224, owner: the rain was thick, bright, blobby white dashes — several pixels
    /// wide near the camera, at night as bright as by day). G3's CP Rainy draws a 1 cm × (1 cm · DirectionScale 14) streak
    /// unlit; a 1 cm wide drop half a metre from the camera is ~19 px at 1080p. [interp] Rain is now a thin, camera-facing
    /// ribbon along the drop's velocity (<see cref="Streak"/>): <see cref="Width"/> ≈ a quarter of G3's size (2.5 mm), length =
    /// speed × <see cref="StreakSeconds"/> (G3's size · DirectionScale at the mean fall speed, ≈ one frame of motion), never
    /// thinner than <see cref="MinPixels"/> on screen — a drop that would be thinner is drawn that wide with its alpha scaled
    /// by the coverage (<see cref="Coverage"/>), so far drops fade instead of aliasing — faded out near the camera
    /// (<see cref="NearFade"/>, no big drops in front of the lens), lit by the scene's ambient and lights (dim at night) and
    /// <see cref="Opacity"/>. Snow keeps G3's flake size as a billboard. The same math runs in G3Precipitation.shader.
    /// </summary>
    public readonly struct PrecipitationLook
    {
        /// <summary>Rain drop width as a share of G3's Size [interp].</summary>
        public const float RainWidthShare = 0.25f;
        public const float RainMinWidth = 0.0008f, RainMaxWidth = 0.003f;

        public readonly bool Streak;
        /// <summary>World width (m) of a drop / flake.</summary>
        public readonly float Width;
        /// <summary>Streak length = speed × this (s); 0 for billboards.</summary>
        public readonly float StreakSeconds;
        /// <summary>Screen width floor in pixels; below it the alpha is scaled by the coverage.</summary>
        public readonly float MinPixels;
        /// <summary>Fully transparent closer than this (m) ...</summary>
        public readonly float NearFadeStart;
        /// <summary>... fully visible from here (m).</summary>
        public readonly float NearFadeEnd;
        public readonly float Opacity;

        public PrecipitationLook(bool streak, float width, float streakSeconds, float minPixels, float nearStart, float nearEnd, float opacity)
        {
            Streak = streak;
            Width = width;
            StreakSeconds = streakSeconds;
            MinPixels = minPixels;
            NearFadeStart = nearStart;
            NearFadeEnd = Math.Max(nearStart + 0.01f, nearEnd);
            Opacity = opacity;
        }

        /// <summary>
        /// The look of a state from its eCWeatherStates values: Size (cm), DirectionScale (> 0 = rain streak) and SpeedMinMax
        /// (cm/s).
        /// </summary>
        public static PrecipitationLook For(float sizeCm, float directionScale, float speedMinCm, float speedMaxCm)
        {
            float size = Math.Max(0.0005f, sizeCm * 0.01f);
            if (directionScale > 0)
            {
                float width = Clamp(size * RainWidthShare, RainMinWidth, RainMaxWidth);
                float mean = Math.Max(0.3f, (speedMinCm + Math.Max(speedMinCm, speedMaxCm)) * 0.005f);
                float length = size * directionScale;
                return new PrecipitationLook(true, width, length / mean, 1f, 0.75f, 2f, 0.55f);
            }
            return new PrecipitationLook(false, size, 0f, 1f, 0.35f, 1.2f, 0.85f);
        }

        /// <summary>The look of the splashes of a state (billboards of the drop size × SplashSizeScale; faded like its drops).</summary>
        public static PrecipitationLook Splash(PrecipitationLook drops) =>
            new PrecipitationLook(false, drops.Width, 0f, 1f, drops.NearFadeStart, drops.NearFadeEnd, drops.Streak ? 0.45f : drops.Opacity);

        /// <summary>Streak length (m) of a drop falling at <paramref name="speed"/> m/s (at least its width).</summary>
        public float Length(float speed) => Streak ? Math.Max(Width, speed * StreakSeconds) : Width;

        /// <summary>Half of the drawn drop along its fall plus a margin (a drop ends this far above the surface it hits).</summary>
        public float DropHalf(float maxSpeed) => 0.5f * Length(maxSpeed) + 0.05f;

        /// <summary>0 at <see cref="NearFadeStart"/> and closer .. 1 from <see cref="NearFadeEnd"/>.</summary>
        public float NearFade(float distance) => Clamp((distance - NearFadeStart) / (NearFadeEnd - NearFadeStart), 0f, 1f);

        /// <summary>
        /// The drawn width (m) of a <paramref name="width"/> m drop at <paramref name="distance"/> m and the alpha factor for
        /// it: never thinner than <paramref name="minPixels"/> on screen; a widened drop's alpha is its coverage.
        /// <paramref name="pixelsPerMeterAt1m"/> = screen height / (2 · tan(fov / 2)).
        /// </summary>
        public static float Coverage(float width, float distance, float pixelsPerMeterAt1m, float minPixels, out float drawnWidth)
        {
            float metersPerPixel = Math.Max(1e-4f, distance) / Math.Max(1e-3f, pixelsPerMeterAt1m);
            drawnWidth = Math.Max(width, minPixels * metersPerPixel);
            return drawnWidth > 0 ? width / drawnWidth : 0f;
        }

        /// <summary>Pixels per metre at 1 m for a vertical field of view (degrees) and a screen height.</summary>
        public static float PixelsPerMeter(float fovDegrees, float screenHeight) =>
            screenHeight / (2f * (float)Math.Tan(fovDegrees * Math.PI / 360.0));

        static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
