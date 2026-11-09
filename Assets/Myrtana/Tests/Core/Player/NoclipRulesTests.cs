using G3.Player;
using Myrtana.Session.Dev;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-131: the console noclip (<see cref="NoclipRules"/>, op noclip / alias fly).</summary>
    public class NoclipRulesTests
    {
        [Test]
        public void Toggle_FlipsWithoutArgument_ExplicitWins()
        {
            Assert.That(NoclipRules.Next(false, null), Is.True);
            Assert.That(NoclipRules.Next(true, null), Is.False);
            Assert.That(NoclipRules.Next(true, true), Is.True);
            Assert.That(NoclipRules.Next(false, false), Is.False);
        }

        [TestCase(null, null)]
        [TestCase("", null)]
        [TestCase("toggle", null)]
        [TestCase("on", true)]
        [TestCase("1", true)]
        [TestCase("OFF", false)]
        [TestCase("0", false)]
        public void Parse_OnOffToggle(string arg, bool? want)
        {
            Assert.That(NoclipRules.TryParse(arg, out var got), Is.True);
            Assert.That(got, Is.EqualTo(want));
        }

        [Test]
        public void Parse_RejectsJunk() => Assert.That(NoclipRules.TryParse("maybe", out _), Is.False);

        [Test]
        public void Velocity_ForwardFollowsTheLook_PitchIncluded()
        {
            var (x, y, z) = NoclipRules.Velocity(1, 0, false, false, false, 0, 0);
            Assert.That(z, Is.EqualTo(NoclipRules.Speed).Within(1e-4));
            Assert.That(x, Is.EqualTo(0).Within(1e-4));
            // yaw 90: forward is +x; looking 90 degrees down (Unity pitch +90): forward is straight down
            (x, _, z) = NoclipRules.Velocity(1, 0, false, false, false, 90, 0);
            Assert.That(x, Is.EqualTo(NoclipRules.Speed).Within(1e-4));
            Assert.That(z, Is.EqualTo(0).Within(1e-4));
            (_, y, _) = NoclipRules.Velocity(1, 0, false, false, false, 0, 90);
            Assert.That(y, Is.EqualTo(-NoclipRules.Speed).Within(1e-4));
        }

        [Test]
        public void Velocity_UpDownStrafeFast_AndNoDiagonalBoost()
        {
            Assert.That(NoclipRules.Velocity(0, 0, true, false, false, 30, 10).y, Is.EqualTo(NoclipRules.Speed).Within(1e-4));
            Assert.That(NoclipRules.Velocity(0, 0, false, true, false, 30, 10).y, Is.EqualTo(-NoclipRules.Speed).Within(1e-4));
            Assert.That(NoclipRules.Velocity(0, 0, true, true, false, 0, 0), Is.EqualTo((0f, 0f, 0f)));
            Assert.That(NoclipRules.Velocity(0, 1, false, false, false, 0, 0).x, Is.EqualTo(NoclipRules.Speed).Within(1e-4));
            Assert.That(NoclipRules.Velocity(0, 1, false, false, true, 0, 0).x, Is.EqualTo(NoclipRules.Speed * NoclipRules.FastFactor).Within(1e-3));
            var (x, y, z) = NoclipRules.Velocity(1, 1, true, false, false, 0, 0);
            Assert.That(System.Math.Sqrt(x * x + y * y + z * z), Is.EqualTo(NoclipRules.Speed).Within(1e-3));
            Assert.That(NoclipRules.Velocity(0, 0, false, false, false, 0, 0), Is.EqualTo((0f, 0f, 0f)));
        }

        [Test]
        public void Landing_PrefersTheNavFloorBelow_ThenNearest_ThenFreeBelow_ThenLastSafe()
        {
            Assert.That(NoclipRules.Choose(true, true, true, true), Is.EqualTo(NoclipRules.Landing.NavFloorBelow));
            Assert.That(NoclipRules.Choose(false, true, true, true), Is.EqualTo(NoclipRules.Landing.NearestFloor));
            Assert.That(NoclipRules.Choose(false, false, true, true), Is.EqualTo(NoclipRules.Landing.FreeFloorBelow));
            Assert.That(NoclipRules.Choose(false, false, false, true), Is.EqualTo(NoclipRules.Landing.LastSafe));
            Assert.That(NoclipRules.Choose(false, false, false, false), Is.EqualTo(NoclipRules.Landing.Stay));
        }

        [Test]
        public void ConsoleOp_NoclipWithFlyAlias()
        {
            Assert.That(ScenarioOps.TryGet("noclip", out var spec), Is.True);
            Assert.That(ScenarioOps.TryGet("fly", out var alias), Is.True);
            Assert.That(alias.Name, Is.EqualTo("noclip"));
            Assert.That(spec.Positional, Is.EqualTo(new[] { "on" }));
            Assert.That(spec.Accepts("speed"), Is.True);
        }
    }
}
