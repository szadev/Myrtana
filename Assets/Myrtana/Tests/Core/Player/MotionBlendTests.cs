using System.Collections.Generic;
using G3.Player;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>G3 CP 1.75 motion cross-fade times (Docs/combat.md §9.4, Docs/formats-animation.md §2.1).</summary>
    public class MotionBlendTests
    {
        const string Hit = "Hero_Stand_None_1H_P0_Attack_Hit_N_Fwd_00_%_00_P1_0_R";
        const string Recover = "Hero_Stand_None_1H_P1_Attack_Recover_N_Fwd_00_%_00_P0_0";
        const string Hit2 = "Hero_Stand_None_1H_P1_Attack_Hit_N_Fwd_00_%_00_P2_0_R";
        const string ParadeBegin = "Hero_Stand_None_1H_P0_Parade_Begin_N_Fwd_00_%_00_P0_0";
        const string Walk = "Hero_Stand_None_None_P0_Move_Walk_N_Fwd_00_%_00_P0_160";

        [TearDown]
        public void ClearTables()
        {
            MotionBlendRules.IntroTable = null;
            MotionBlendRules.ExtroTable = null;
        }

        [Test]
        public void Command_FromARunningMotion_UsesTheStandardFade()
        {
            // Next attack starts while the auto Recover plays: min(extro, intro) = 0.2 s (no blend table in the data).
            Assert.AreEqual(0.2f, MotionBlendRules.FadeFor(MotionStarter.Command, Recover, true, Hit2), 1e-6);
        }

        [Test]
        public void Command_AfterTheOldMotionEnded_CutsIn()
        {
            // Hit → its Recover, Raise → Hit: the old one-shot is over, nothing fades.
            Assert.AreEqual(0f, MotionBlendRules.FadeFor(MotionStarter.Command, Hit, false, Recover));
            Assert.AreEqual(0f, MotionBlendRules.FadeFor(MotionStarter.Command, null, true, Hit));
        }

        [Test]
        public void Command_BeginMotion_FadesInATenth()
        {
            Assert.AreEqual(0.1f, MotionBlendRules.FadeFor(MotionStarter.Command, Walk, true, ParadeBegin), 1e-6);
        }

        [Test]
        public void Movement_Fades()
        {
            Assert.AreEqual(0.2f, MotionBlendRules.FadeFor(MotionStarter.Locomotion, Recover, true, Walk), 1e-6);
            Assert.AreEqual(0f, MotionBlendRules.FadeFor(MotionStarter.Locomotion, Recover, false, Walk));
            Assert.AreEqual(0.5f, MotionBlendRules.FadeFor(MotionStarter.StandEntry, Walk, true, "Hero_Stand_None_None_P0_Ambient_Loop_N_Fwd_00"), 1e-6);
            Assert.AreEqual(0.2f, MotionBlendRules.FadeFor(MotionStarter.StandRepick, "a", true, "b"), 1e-6);
        }

        [Test]
        public void BlendTable_TakesTheShorterOfExtroAndIntro()
        {
            MotionBlendRules.ExtroTable = new Dictionary<string, float> { [MotionBlendRules.TableKey(Recover)] = 3 * MotionBlendRules.BlendTableUnit };
            MotionBlendRules.IntroTable = new Dictionary<string, float> { [MotionBlendRules.TableKey(Hit2)] = 6 * MotionBlendRules.BlendTableUnit };
            Assert.AreEqual(0.2f, MotionBlendRules.FadeFor(MotionStarter.Command, Recover, true, Hit2), 1e-6); // min(0.2, 0.4)
            MotionBlendRules.IntroTable = null;
            Assert.AreEqual(0.2f, MotionBlendRules.FadeFor(MotionStarter.Command, Recover, true, Hit2), 1e-6);
            MotionBlendRules.ExtroTable = new Dictionary<string, float> { [MotionBlendRules.TableKey(Recover)] = 0.1f };
            Assert.AreEqual(0.1f, MotionBlendRules.FadeFor(MotionStarter.Command, Recover, true, Hit2), 1e-6);
            Assert.AreEqual("HERO_STAND_NONE_1H_P1_ATTACK_RECOVER_N_FWD_00", MotionBlendRules.TableKey(Recover));
        }
    }
}
