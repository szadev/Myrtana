using System;

namespace G3.Weather
{
    /// <summary>
    /// Wind travel of the sky clouds (G3/Sky r1 layer and v2 volumetric slab): the integral of the wind direction over
    /// time, in seconds x unit direction; the shader multiplies it by the layer's drift speed (r1 plane units/s, v2 km/s).
    /// fix-146 (owner: clouds run faster than normal): the shaders offset the clouds by <c>dir(t) · t · speed</c>, but
    /// G3Wind lets the direction wander, so the apparent speed was <c>speed · |dir + t · dir'(t)|</c> — it grew with the
    /// play time (≈ 4x after 10 min, ≈ 25x after an hour, swirling with the wander). Integrating <c>dir · dt</c> keeps
    /// the speed at <c>speed</c> for any direction change.
    /// G3 (Engine.dll CP 1.75, eCSkydome_PS::CreateShader + eCTexCoordSrcScroller::SetTime, ge_skydome.fx): layer 1 UV
    /// = acos(normalize(v.x, 4 v.y, v.z).xz) / CloudSize1 + Rate · CloudDirection · t with Rate 0.1, World_MCP
    /// CloudDirection (0.1, 0), CloudSize1 0.6 (layer 2, size 0.4, does not scroll); t is the engine's real-time shader
    /// clock (the same one animated textures and water use), not the game clock (gCClock_PS Factor 12), and the weather
    /// does not change the rate.
    /// </summary>
    public sealed class CloudDrift
    {
        /// <summary>Longest clock step integrated at once (s): a hitch or an editor clock jump does not throw the clouds.</summary>
        public const double MaxStep = 1.0;

        double _last = double.NaN;

        /// <summary>Travel along world X / Z (seconds x unit direction).</summary>
        public double X { get; private set; }
        public double Z { get; private set; }

        /// <summary>Advances to clock <paramref name="time"/> (s) with the current wind direction (normalised here;
        /// a zero direction keeps the clouds still). A clock that runs backwards (restart, wrap) only re-bases.</summary>
        public void Advance(double time, double dirX, double dirZ)
        {
            if (double.IsNaN(_last)) { _last = time; return; }
            double dt = time - _last;
            _last = time;
            if (!(dt > 0)) return;
            if (dt > MaxStep) dt = MaxStep;
            double len = Math.Sqrt(dirX * dirX + dirZ * dirZ);
            if (len < 1e-6) return;
            X += dirX / len * dt;
            Z += dirZ / len * dt;
        }

        /// <summary>Deterministic captures (frozen wind clock): travel = direction x time.</summary>
        public void Freeze(double time, double dirX, double dirZ)
        {
            double len = Math.Sqrt(dirX * dirX + dirZ * dirZ);
            if (len < 1e-6) { X = Z = 0; }
            else { X = dirX / len * time; Z = dirZ / len * time; }
            _last = double.NaN;
        }

        /// <summary>Apparent drift speed (layer units/s) of the old <c>dir(t) · t · speed</c> offset between t0 and t1 —
        /// kept for the regression test and the fix notes.</summary>
        public static double LegacySpeed(Func<double, double> azimuthRad, double t0, double t1, double speed)
        {
            double a0 = azimuthRad(t0), a1 = azimuthRad(t1);
            double dx = Math.Sin(a1) * t1 - Math.Sin(a0) * t0, dz = Math.Cos(a1) * t1 - Math.Cos(a0) * t0;
            return Math.Sqrt(dx * dx + dz * dz) * speed / (t1 - t0);
        }
    }
}
