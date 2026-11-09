using System;
using G3.Weather;
using NUnit.Framework;

namespace Myrtana.Tests.Effects
{
    /// <summary>fix-146: the sky clouds drift at a constant speed (integrated wind direction), not dir(t)·t.</summary>
    public class CloudDriftTests
    {
        // G3Wind's direction: azimuth 60° ± 15° wander; a smooth stand-in for its Perlin wander (same rate 0.013/s).
        static double Azimuth(double t) => (60 + 15 * Math.Sin(t * 0.013 * 2.5)) * Math.PI / 180;

        static double Speed(CloudDrift d, double t0, double t1, double step)
        {
            for (double t = t0; t <= t0 + 1e-9; t += step) d.Advance(t, Math.Sin(Azimuth(t)), Math.Cos(Azimuth(t)));
            double x0 = d.X, z0 = d.Z;
            for (double t = t0 + step; t <= t1 + 1e-9; t += step) d.Advance(t, Math.Sin(Azimuth(t)), Math.Cos(Azimuth(t)));
            return Math.Sqrt((d.X - x0) * (d.X - x0) + (d.Z - z0) * (d.Z - z0)) / (t1 - t0);
        }

        [Test]
        public void Speed_StaysConstant_OverPlayTime()
        {
            var d = new CloudDrift();
            Speed(d, 0, 3600, 0.05);                               // an hour of play
            double v = Speed(d, 3600, 3610, 0.05);                  // seconds 3600..3610
            Assert.That(v, Is.EqualTo(1.0).Within(0.01), "travel per second = 1 x unit direction");
        }

        [Test]
        public void LegacyFormula_GrewWithPlayTime()
        {
            // the old shader offset dir(t)·t·speed: at the start ~ speed, after an hour many times faster
            double early = CloudDrift.LegacySpeed(Azimuth, 1, 11, 0.02);
            double late = CloudDrift.LegacySpeed(Azimuth, 3600, 3610, 0.02);
            Assert.That(early, Is.EqualTo(0.02).Within(0.004));
            Assert.That(late / 0.02, Is.GreaterThan(5), "the bug: apparent speed grew with time");
        }

        [Test]
        public void SpeedIndependentOfFrameRate_AndMultipleAppliesPerFrame()
        {
            var a = new CloudDrift(); var b = new CloudDrift();
            for (int i = 0; i <= 600; i++) { double t = i / 60.0; a.Advance(t, 1, 0); }
            for (int i = 0; i <= 300; i++) { double t = i / 30.0; b.Advance(t, 1, 0); b.Advance(t, 1, 0); b.Advance(t, 1, 0); }
            Assert.That(a.X, Is.EqualTo(10).Within(1e-6));
            Assert.That(b.X, Is.EqualTo(10).Within(1e-6));
        }

        [Test]
        public void ClockJumps_AreClampedOrRebased()
        {
            var d = new CloudDrift();
            d.Advance(0, 0, 2); d.Advance(100, 0, 2);               // 100 s hitch -> one MaxStep
            Assert.That(d.Z, Is.EqualTo(CloudDrift.MaxStep).Within(1e-9));
            d.Advance(5, 0, 1);                                      // clock restarted: no motion
            d.Advance(5.5, 0, 1);
            Assert.That(d.Z, Is.EqualTo(CloudDrift.MaxStep + 0.5).Within(1e-9));
            d.Advance(6, 0, 0);                                      // no wind direction: still
            Assert.That(d.Z, Is.EqualTo(CloudDrift.MaxStep + 0.5).Within(1e-9));
        }

        [Test]
        public void Freeze_IsDeterministic()
        {
            var d = new CloudDrift();
            d.Freeze(50, 3, 4);
            Assert.That(d.X, Is.EqualTo(30).Within(1e-9));
            Assert.That(d.Z, Is.EqualTo(40).Within(1e-9));
        }
    }
}
