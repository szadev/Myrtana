using System.Collections.Generic;

namespace G3.Audio
{
    /// <summary>Piecewise-linear gain over absolute (DSP) time; constant before the first and after the last point.</summary>
    public sealed class GainEnvelope
    {
        readonly List<(double t, float g)> _points = new List<(double, float)>();

        public GainEnvelope(float initial = 1f) { _points.Add((double.NegativeInfinity, initial)); }

        public float Evaluate(double t)
        {
            var prev = _points[0];
            for (int i = 1; i < _points.Count; i++)
            {
                var p = _points[i];
                if (t < p.t)
                {
                    if (double.IsNegativeInfinity(prev.t) || p.t <= prev.t) return prev.g;
                    return prev.g + (p.g - prev.g) * (float)((t - prev.t) / (p.t - prev.t));
                }
                prev = p;
            }
            return prev.g;
        }

        /// <summary>Ramps from the current gain at <paramref name="t0"/> to <paramref name="g"/> at <paramref name="t1"/>, replacing later points.</summary>
        public void RampTo(double t0, double t1, float g)
        {
            float g0 = Evaluate(t0);
            _points.RemoveAll(p => p.t >= t0);
            _points.Add((t0, g0));
            _points.Add((t1 > t0 ? t1 : t0, g));
        }

        /// <summary>Gain <paramref name="from"/> until <paramref name="t0"/>, then linear to <paramref name="to"/> at <paramref name="t1"/>.</summary>
        public void SetRamp(double t0, double t1, float from, float to)
        {
            _points.Clear();
            _points.Add((double.NegativeInfinity, from));
            _points.Add((t0, from));
            _points.Add((t1 > t0 ? t1 : t0, to));
        }
    }
}
