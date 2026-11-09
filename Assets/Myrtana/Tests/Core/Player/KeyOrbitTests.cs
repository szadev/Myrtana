using G3.Player;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-73: the port's option "Q / E orbit the camera" (KeyOrbitRules; Docs/controls-options.md section 7).</summary>
    public class KeyOrbitTests
    {
        const float Dt = 1f / 60f;

        [Test]
        public void Off_TheKeysTurnTheHero_AsG3()
        {
            Assert.That(KeyOrbitRules.Orbits(false, HeroMoveState.Normal, true, false), Is.False);
            Assert.That(KeyOrbitRules.Orbits(true, HeroMoveState.Normal, true, false), Is.True);
        }

        [TestCase(HeroMoveState.Melee)]
        [TestCase(HeroMoveState.MeleeParade)]
        [TestCase(HeroMoveState.Ranged)]
        [TestCase(HeroMoveState.RangedAim)]
        [TestCase(HeroMoveState.Magic)]
        [TestCase(HeroMoveState.MagicCast)]
        public void InCombatOrAiming_TheKeysTurnTheHero(HeroMoveState state) =>
            Assert.That(KeyOrbitRules.Orbits(true, state, true, false), Is.False);

        [Test]
        public void FirstPersonOrLookingBehind_TheKeysTurnTheHero()
        {
            Assert.That(KeyOrbitRules.Orbits(true, HeroMoveState.Normal, false, false), Is.False);
            Assert.That(KeyOrbitRules.Orbits(true, HeroMoveState.Normal, true, true), Is.False);
        }

        [Test]
        public void HeldKey_OrbitsAtTheSpeed_BothWays()
        {
            float v = 0f, yaw = 0f;
            for (int i = 0; i < 60; i++) yaw = KeyOrbitRules.Step(yaw, 1f, false, Dt, ref v);
            Assert.That(yaw, Is.EqualTo(KeyOrbitRules.Speed).Within(0.01f), "E: one second to the right");
            for (int i = 0; i < 30; i++) yaw = KeyOrbitRules.Step(yaw, -1f, false, Dt, ref v);
            Assert.That(yaw, Is.EqualTo(KeyOrbitRules.Speed / 2f).Within(0.01f), "Q: half a second back");
            Assert.That(KeyOrbitRules.Speed, Is.InRange(90f, 120f));
        }

        [Test]
        public void TheOffset_WrapsRoundTheHero()
        {
            float v = 0f, yaw = 170f;
            yaw = KeyOrbitRules.Step(yaw, 1f, false, 0.2f, ref v);
            Assert.That(yaw, Is.EqualTo(170f + KeyOrbitRules.Speed * 0.2f - 360f).Within(0.01f));
            Assert.That(KeyOrbitRules.Wrap(-190f), Is.EqualTo(170f).Within(1e-4f));
        }

        [Test]
        public void Released_HoldsWhileTheHeroStands()
        {
            float v = 0f;
            Assert.That(KeyOrbitRules.Step(135f, 0f, false, 1f, ref v), Is.EqualTo(135f), "to look at him from the front");
        }

        [Test]
        public void Moving_EasesBackBehindTheHero_TheShortWay()
        {
            float v = 0f, yaw = 160f;
            yaw = KeyOrbitRules.Step(yaw, 0f, true, Dt, ref v);
            Assert.That(yaw, Is.LessThan(160f).And.GreaterThan(0f));
            for (int i = 0; i < 120; i++) yaw = KeyOrbitRules.Step(yaw, 0f, true, Dt, ref v);
            Assert.That(yaw, Is.EqualTo(0f), "behind him within two seconds, no overshoot");
            v = 0f;
            yaw = -100f;
            for (int i = 0; i < 10; i++)
            {
                float next = KeyOrbitRules.Step(yaw, 0f, true, Dt, ref v);
                Assert.That(next, Is.GreaterThanOrEqualTo(yaw).And.LessThanOrEqualTo(0f));
                yaw = next;
            }
        }

        [Test]
        public void ZeroTime_ChangesNothing()
        {
            float v = 0f;
            Assert.That(KeyOrbitRules.Step(40f, 1f, true, 0f, ref v), Is.EqualTo(40f));
        }

        [Test]
        public void Label_NamesTheBoundKeys()
        {
            var keys = KeyMap.Default();
            Assert.That(KeyOrbitRules.KeysText(keys), Is.EqualTo("Q / E"), "G3 default: the mouse axes are left out");
            Assert.That(keys.Get(SessionKey.RotateCamLeft, 1), Is.EqualTo(KeyInput.Key(Dik.Q)));
            keys.Assign(SessionKey.RotateCamLeft, 1, KeyInput.Key(Dik.Home));
            Assert.That(KeyOrbitRules.KeysText(keys), Is.EqualTo(Dik.Name(Dik.Home) + " / E"), "a rebinding shows its key");
            keys.Clear(SessionKey.RotateCamRight, 0);
            keys.Clear(SessionKey.RotateCamRight, 1);
            Assert.That(KeyOrbitRules.KeysText(keys), Does.EndWith(" / -"));
            Assert.That(KeyOrbitRules.KeysText(null), Is.EqualTo("- / -"));
        }
    }
}
