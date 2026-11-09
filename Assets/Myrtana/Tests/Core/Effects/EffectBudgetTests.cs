using G3.Effects;
using NUnit.Framework;

namespace Myrtana.Tests.Effects
{
    /// <summary>
    /// fix-58 (runner-31 "[G3Effects] 384 live effect parts: skipping new effects"): a part whose emission ended counts as
    /// finished after its longest particle life even when its (culled, paused) particle system still reports particles; the
    /// dev report groups the live effects by name.
    /// </summary>
    public class EffectBudgetTests
    {
        [Test]
        public void AgedOut_OnlyAfterTheEmissionEndedAndTheLongestLife()
        {
            Assert.IsFalse(EmitterRules.AgedOut(-1f, 1000f, 2f), "still emitting: never");
            Assert.IsFalse(EmitterRules.AgedOut(10f, 12.4f, 2f), "within the longest life + slack");
            Assert.IsTrue(EmitterRules.AgedOut(10f, 12.6f, 2f));
            Assert.IsTrue(EmitterRules.AgedOut(0f, 0.6f, 0f), "no lifetime: the slack alone");
        }

        [Test]
        public void Report_GroupsByName_MostPartsFirst_FlagsEndlessAndGoneTargets()
        {
            var live = new[]
            {
                new LiveEffectInfo("eff_col_wh_02_me_fl", 2, 0, stopped: true, follows: true, targetGone: false, age: 3f, maxSeconds: 12f),
                new LiveEffectInfo("VFX_Fire_Torch_01", 3, 0, stopped: false, follows: false, targetGone: false, age: 400f, maxSeconds: 0f),
                new LiveEffectInfo("VFX_Fire_Torch_01", 3, 0, stopped: false, follows: false, targetGone: false, age: 100f, maxSeconds: 0f),
                new LiveEffectInfo("eff_magic_fireball_hand_01", 1, 0, stopped: false, follows: true, targetGone: true, age: 9f, maxSeconds: 0f),
            };
            string r = EffectBudgetReport.Build(live, 384);
            StringAssert.StartsWith("effects: 4 live, 9/384 parts", r);
            var lines = r.Split('\n');
            StringAssert.Contains("VFX_Fire_Torch_01: 2 x, 6 parts, 0 stopped, 2 endless, 0 target gone, oldest 400 s", lines[1]);
            StringAssert.Contains("eff_col_wh_02_me_fl: 1 x, 2 parts, 1 stopped", lines[2]);
            StringAssert.Contains("eff_magic_fireball_hand_01: 1 x, 1 parts, 0 stopped, 0 endless, 1 target gone", lines[3]);
        }

        [Test]
        public void Report_CutsAfterTop()
        {
            var live = new[]
            {
                new LiveEffectInfo("a", 1, 0, false, false, false, 1f, 1f),
                new LiveEffectInfo("b", 2, 0, false, false, false, 1f, 1f),
                new LiveEffectInfo("c", 3, 0, false, false, false, 1f, 1f),
            };
            string r = EffectBudgetReport.Build(live, 384, top: 2);
            StringAssert.Contains("... 1 more names", r);
            StringAssert.DoesNotContain("\n  a:", r);
        }
    }
}
