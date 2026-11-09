using System;
using System.Collections.Generic;
using System.Globalization;

namespace G3.Player
{
    /// <summary>
    /// fix-89 ("drawing or sheathing while running crumples the hero"): torso shape per frame — the distance from the pelvis
    /// (<c>Spine_1</c>) to the head (<c>Head_1</c>) and the bend between the lower spine (<c>Spine_1</c>→<c>Spine_3</c>) and
    /// the upper body (<c>Spine_3</c>→head) — split into plain running frames (the run's own range) and frames with an
    /// upper-body overlay (a draw or sheathe while running). An overlay must keep the torso inside the run's range:
    /// <list type="bullet">
    /// <item><c>shrink</c>: largest amount the pelvis–head distance fell below the run's minimum (m);</item>
    /// <item><c>bend</c>: largest amount the bend exceeded the run's maximum (degrees);</item>
    /// <item><c>runmin</c>, <c>runmax</c>, <c>runbend</c>: the run's own range; <c>overlaymin</c>, <c>overlaybend</c>:
    /// the overlay frames' extremes; <c>runframes</c>, <c>overlayframes</c>: counts.</item>
    /// </list>
    /// A metric is NaN without frames of both kinds. Pure (the dev probe and the G3-data test feed it).
    /// </summary>
    public sealed class TorsoPoseStats
    {
        readonly List<(float dist, float bend, bool overlay)> _frames = new List<(float, float, bool)>();

        public int Count => _frames.Count;
        public void Clear() => _frames.Clear();

        /// <summary>Adds a frame from three joint positions (any unit; distances come out in that unit).</summary>
        public void Add(float px, float py, float pz, float cx, float cy, float cz, float hx, float hy, float hz, bool overlay)
        {
            float dx = hx - px, dy = hy - py, dz = hz - pz;
            Add((float)Math.Sqrt(dx * dx + dy * dy + dz * dz), Bend(px, py, pz, cx, cy, cz, hx, hy, hz), overlay);
        }

        /// <summary>Adds a frame: pelvis–head distance and bend (degrees).</summary>
        public void Add(float distance, float bendDegrees, bool overlay) => _frames.Add((distance, bendDegrees, overlay));

        /// <summary>Angle between pelvis→chest and chest→head, degrees (0 = straight).</summary>
        public static float Bend(float px, float py, float pz, float cx, float cy, float cz, float hx, float hy, float hz)
        {
            float ax = cx - px, ay = cy - py, az = cz - pz, bx = hx - cx, by = hy - cy, bz = hz - cz;
            float la = (float)Math.Sqrt(ax * ax + ay * ay + az * az), lb = (float)Math.Sqrt(bx * bx + by * by + bz * bz);
            if (la < 1e-6f || lb < 1e-6f) return 180f; // a joint collapsed onto the next: fully folded
            float c = (ax * bx + ay * by + az * bz) / (la * lb);
            return (float)(Math.Acos(Math.Max(-1.0, Math.Min(1.0, c))) * 180.0 / Math.PI);
        }

        public float Metric(string name)
        {
            float runMin = float.MaxValue, runMax = float.MinValue, runBend = float.MinValue;
            float ovMin = float.MaxValue, ovBend = float.MinValue;
            int run = 0, ov = 0;
            foreach (var f in _frames)
            {
                if (f.overlay)
                {
                    ov++;
                    ovMin = Math.Min(ovMin, f.dist);
                    ovBend = Math.Max(ovBend, f.bend);
                }
                else
                {
                    run++;
                    runMin = Math.Min(runMin, f.dist);
                    runMax = Math.Max(runMax, f.dist);
                    runBend = Math.Max(runBend, f.bend);
                }
            }
            switch (name)
            {
                case "runframes": return run;
                case "overlayframes": return ov;
            }
            bool both = run > 0 && ov > 0;
            switch (name)
            {
                case "shrink": return both ? Math.Max(0f, runMin - ovMin) : float.NaN;
                case "bend": return both ? Math.Max(0f, ovBend - runBend) : float.NaN;
                case "runmin": return run > 0 ? runMin : float.NaN;
                case "runmax": return run > 0 ? runMax : float.NaN;
                case "runbend": return run > 0 ? runBend : float.NaN;
                case "overlaymin": return ov > 0 ? ovMin : float.NaN;
                case "overlaybend": return ov > 0 ? ovBend : float.NaN;
                default: return float.NaN;
            }
        }

        public static readonly string[] Names = { "shrink", "bend", "runmin", "runmax", "runbend", "overlaymin", "overlaybend", "runframes", "overlayframes" };

        public string Summary()
        {
            var parts = new List<string>();
            foreach (var n in Names)
            {
                float v = Metric(n);
                parts.Add(n + " " + (float.IsNaN(v) ? "-" : v.ToString("0.###", CultureInfo.InvariantCulture)));
            }
            return string.Join(", ", parts);
        }
    }
}
