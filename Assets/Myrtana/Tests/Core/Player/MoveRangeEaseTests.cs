using System;
using G3.Player;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-256 (owner: "I run, release the run and the camera zooms in abruptly", at a close wheel zoom): the camera
    /// distance stepped through run → release → stand changes by a bounded amount per frame
    /// (<see cref="CameraStability.MoveRangeWeight"/>, <see cref="CameraModeParams.EasedWishedDistance"/>).
    /// </summary>
    public class MoveRangeEaseTests
    {
        /// <summary>The camera's distance loop (G3PlayerCamera.PlaceThirdPerson, no obstacles); returns the largest
        /// per-frame change, m/s, and the distances while running and at the end.</summary>
        static float Run(float zoom, float dt, bool eased, out float running, out float end)
        {
            var m = CameraModes.Ranged;
            float cur = -1f, still = 0f, lin = 0f, maxSpeed = 0f;
            running = end = 0f;
            int frames = (int)(6f / dt);
            for (int i = 0; i < frames; i++)
            {
                float t = i * dt;
                bool movingNow = t >= 1f && t < 3f;   // stand 1 s, run 2 s, release, stand 3 s
                bool moving = CameraStability.MovingLatch(movingNow, ref still, dt);
                float z = m.ClampZoom(zoom);
                float wanted = eased ? m.EasedWishedDistance(z, CameraStability.MoveRangeWeight(ref lin, moving, dt))
                                     : m.WishedDistance(z, moving);
                float before = cur;
                cur = CameraModes.SmoothDistance(cur, wanted, float.MaxValue, dt);
                if (before >= 0f) maxSpeed = Math.Max(maxSpeed, Math.Abs(cur - before) / dt);
                if (Math.Abs(t - 2.9f) < dt * 0.5f) running = cur;
            }
            end = cur;
            return maxSpeed;
        }

        [TestCase(2f, 1f / 30f)]
        [TestCase(2f, 1f / 60f)]
        [TestCase(2f, 1f / 144f)]
        [TestCase(1.2f, 1f / 60f)]   // below MinRange: clamped to the closest zoom
        [TestCase(3f, 1f / 60f)]
        [TestCase(8f, 1f / 60f)]
        public void RunThenRelease_DistanceChangePerFrameIsBounded(float zoom, float dt)
        {
            float speed = Run(zoom, dt, true, out float running, out float end);
            float z = CameraModes.Ranged.ClampZoom(zoom);
            Assert.That(running, Is.EqualTo(Math.Max(z, CameraModes.Ranged.MinRangeOnMove)).Within(0.02f), "running keeps MinRangeOnMove");
            Assert.That(end, Is.EqualTo(z).Within(0.02f), "standing comes back to the zoom");
            Assert.That(speed, Is.LessThan(3f), "no snap: at most 3 m/s (5 cm per frame at 60 fps)");
            Assert.That(speed * dt, Is.LessThan(0.1f), "per-frame step");
        }

        [Test]
        public void CloseZoom_TheUnEasedLatchSnapped()
        {
            float speed = Run(2f, 1f / 60f, false, out _, out _);
            Assert.That(speed / 60f, Is.GreaterThan(0.2f), "the old path closed 1 m in a few frames (the owner's snap)");
        }

        [Test]
        public void MoveRangeWeight_SmoothstepBothWays()
        {
            float lin = 0f;
            Assert.That(CameraStability.MoveRangeWeight(ref lin, false, 0.1f), Is.EqualTo(0f));
            float w = 0f;
            for (int i = 0; i < 6; i++) w = CameraStability.MoveRangeWeight(ref lin, true, 0.1f);
            Assert.That(w, Is.EqualTo(1f).Within(1e-4f), "full after MoveRangeOutSeconds");
            w = CameraStability.MoveRangeWeight(ref lin, false, 0.6f);
            Assert.That(w, Is.EqualTo(0.5f).Within(1e-4f), "half way after half of MoveRangeInSeconds");
            Assert.That(CameraModes.Ranged.EasedWishedDistance(2f, 0.5f), Is.EqualTo(2.5f).Within(1e-5f));
            Assert.That(CameraModes.Ranged.EasedWishedDistance(5f, 1f), Is.EqualTo(5f), "a far zoom is above the floor");
        }
    }
}
