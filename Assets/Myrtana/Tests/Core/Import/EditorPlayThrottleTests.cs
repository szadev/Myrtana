using G3.Unity;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>perf-6: editor Play is capped only while a game player runs, the editor is in the background and nothing measures.</summary>
    public class EditorPlayThrottleTests
    {
        [Test]
        public void Caps_OnlyInTheBackground_WhileAPlayerRuns()
        {
            Assert.That(EditorPlayThrottle.Cap(true, false, false, false), Is.EqualTo(EditorPlayThrottle.ThrottledFps));
            Assert.That(EditorPlayThrottle.Cap(false, false, false, false), Is.Null);
            Assert.That(EditorPlayThrottle.Cap(true, true, false, false), Is.Null);
        }

        [Test]
        public void NeverCaps_AMeasurement_OrWhenSwitchedOff()
        {
            Assert.That(EditorPlayThrottle.Cap(true, false, true, false), Is.Null);
            Assert.That(EditorPlayThrottle.Cap(true, false, false, true), Is.Null);
        }
    }
}
