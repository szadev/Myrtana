using System;

namespace G3.Audio
{
    /// <summary>
    /// fix-199: where a 3D sound is heard relative to the listener (the camera, as in G3). Unity's axes are left-handed
    /// (x right, y up, z forward), so the listener's right is <c>up × forward</c>; G3's axes are the same (×0.01), no
    /// mirroring. Used for the voice logs and to place speech at the speaker; Unity pans the 3D sources itself from the
    /// AudioListener's transform, this is the same rule written out (tests: Tools/G3Tests/Audio/ListenerPanTests).
    /// </summary>
    public static class ListenerPan
    {
        public static V3 Cross(V3 a, V3 b) => new V3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);

        /// <summary>The listener's right vector (left-handed: up × forward).</summary>
        public static V3 Right(V3 forward, V3 up) => Cross(up, forward).Normalized;

        /// <summary>
        /// Stereo position of a source: −1 fully left … 0 ahead or behind … +1 fully right (the sine of the azimuth, as an
        /// equal-power panner sees it). Independent of the distance: a far source on the right is as far right as a near one.
        /// 0 for a source at the listener.
        /// </summary>
        public static float Pan(V3 listener, V3 forward, V3 up, V3 source)
        {
            var d = (source - listener).Normalized;
            if (d.Length < 0.5f) return 0f;
            return Clamp(d.Dot(Right(forward, up)));
        }

        /// <summary>Azimuth of the source in the listener's horizontal plane, degrees: 0 ahead, +90 right, −90 left, ±180 behind.</summary>
        public static float Azimuth(V3 listener, V3 forward, V3 up, V3 source)
        {
            var d = source - listener;
            var r = Right(forward, up);
            var f = Cross(r, up.Normalized); // forward flattened onto the plane normal to up
            float x = d.Dot(r), z = d.Dot(f);
            if (Math.Abs(x) < 1e-6f && Math.Abs(z) < 1e-6f) return 0f;
            return (float)(Math.Atan2(x, z) * 180.0 / Math.PI);
        }

        /// <summary>"left" / "right" / "ahead" / "behind" for the logs (within ±30° of ahead/behind counts as such).</summary>
        public static string Side(float azimuth)
        {
            float a = Math.Abs(azimuth);
            if (a <= 30f) return "ahead";
            if (a >= 150f) return "behind";
            return azimuth > 0 ? "right" : "left";
        }

        static float Clamp(float v) => v < -1f ? -1f : v > 1f ? 1f : v;
    }
}
