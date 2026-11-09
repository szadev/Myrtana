using System;
using System.Collections.Generic;
using G3.Player;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-44: the hero's gait and speed per drawn state. G3 (CP 1.75 <c>FUN_10002130</c>) runs the same gait update in
    /// PS_Normal_Loop, PS_Melee_Loop, PS_Magic_Loop and PS_Ranged_Walk_Loop, so Shift sprints with any hands; the speed is
    /// the speed in the drawn set's motion names (<see cref="HeroGaitSpeeds"/>).
    /// </summary>
    public class HeroDrawnGaitTests
    {
        // CP 1.75 _compiledAnimation: Hero_Stand_<left>_<right>_P0_Move_<gait>_N_<dir>_00_%_00_P*_<cm/s>.
        static readonly Dictionary<string, string> Sets = new Dictionary<string, string>
        {
            ["None_None"] = "Run_Back=400 Run_Fwd=400 Run_Left=300 Run_Right=300 Sprint_Fwd=600 Walk_Back=160 Walk_Fwd=160 Walk_Left=160 Walk_Right=160",
            ["None_1H"] = "Run_Back=400 Run_Fwd=400 Run_Left=300 Run_Right=300 Sprint_Fwd=500 Sprint_Left=400 Sprint_Right=400 Walk_Back=160 Walk_Fwd=160 Walk_Left=160 Walk_Right=160",
            ["None_2H"] = "Run_Back=400 Run_Fwd=400 Run_Left=300 Run_Right=300 Sprint_Fwd=500 Sprint_Left=400 Sprint_Right=400 Walk_Back=160 Walk_Fwd=160 Walk_Left=160 Walk_Right=160",
            ["None_Fist"] = "Run_Back=400 Run_Fwd=400 Run_Left=300 Run_Right=300 Sprint_Fwd=500 Sprint_Left=400 Sprint_Right=400 Walk_Back=160 Walk_Fwd=160 Walk_Left=160 Walk_Right=160",
            ["None_Staff"] = "Run_Back=400 Run_Fwd=400 Run_Left=300 Run_Right=300 Sprint_Fwd=500 Sprint_Left=400 Sprint_Right=400 Walk_Back=160 Walk_Fwd=160 Walk_Left=160 Walk_Right=160",
            ["None_Cast"] = "Run_Back=400 Run_Fwd=400 Run_Left=300 Run_Right=300 Sprint_Fwd=500 Sprint_Left=400 Sprint_Right=400 Walk_Back=160 Walk_Fwd=160 Walk_Left=160 Walk_Right=160",
            ["Shield_1H"] = "Run_Back=400 Run_Fwd=400 Run_Left=300 Run_Right=300 Sprint_Fwd=500 Sprint_Left=400 Sprint_Right=400 Walk_Back=160 Walk_Fwd=160 Walk_Left=160 Walk_Right=160",
            ["1H_1H"] = "Run_Back=400 Run_Fwd=400 Run_Left=300 Run_Right=300 Sprint_Fwd=500 Sprint_Left=400 Sprint_Right=400 Walk_Back=160 Walk_Fwd=160 Walk_Left=160 Walk_Right=160",
            ["Torch_1H"] = "Run_Back=400 Run_Fwd=400 Run_Left=300 Run_Right=300 Sprint_Fwd=500 Sprint_Left=400 Sprint_Right=400 Walk_Back=160 Walk_Fwd=160 Walk_Left=160 Walk_Right=160",
            ["Torch_None"] = "Run_Back=400 Run_Fwd=400 Run_Left=300 Run_Right=300 Walk_Back=160 Walk_Fwd=160 Walk_Left=160 Walk_Right=160",
            ["Staff_Cast"] = "Run_Back=400 Run_Fwd=400 Run_Left=300 Run_Right=300 Sprint_Fwd=600 Walk_Back=160 Walk_Fwd=160 Walk_Left=160 Walk_Right=160",
            ["Bow_Arrow"] = "Run_Back=400 Run_Fwd=400 Run_Left=300 Run_Right=300 Walk_Back=150 Walk_Fwd=150 Walk_Left=150 Walk_Right=150",
            ["CrossBow_Bolt"] = "Run_Back=400 Run_Fwd=400 Run_Left=300 Run_Right=300 Walk_Back=200 Walk_Fwd=200 Walk_Left=200 Walk_Right=200",
        };

        static HeroGaitSpeeds Set(string hands)
        {
            var speeds = new Dictionary<string, float>();
            foreach (var part in Sets[hands].Split(' '))
            {
                var kv = part.Split('=');
                speeds[kv[0]] = float.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture) / 100f;
            }
            return HeroGaitSpeeds.From((g, d) => speeds.TryGetValue(g + "_" + d, out float v) ? v : 0f);
        }

        // The controller's unarmed fields (G3PlayerSetup: the None_None motions): walk 1.6, run 4.0 / back 4.0 / side 3.0, sprint 6.0.
        static float ControllerSpeed(string hands, HeroGait gait, HeroMoveDir dir)
        {
            float field = gait == HeroGait.Walk ? 1.6f
                        : dir == HeroMoveDir.Side ? 3f
                        : dir == HeroMoveDir.Back ? 4f
                        : gait == HeroGait.Sprint ? 6f : 4f;
            return field * HeroGaitSpeeds.Factor(Set(hands), Set("None_None"), gait, dir);
        }

        // ------------------------------------------------------------------ gait (G3 FUN_10002130)

        [TestCase(HeroMoveState.Normal)]
        [TestCase(HeroMoveState.Melee)]
        [TestCase(HeroMoveState.Magic)]
        [TestCase(HeroMoveState.Ranged)]
        public void Shift_WithForwardHeld_SprintsInEveryLoopState(HeroMoveState state)
        {
            Assert.IsTrue(HeroMoveRules.UpdatesGait(state), "the loop runs the gait update");
            Assert.IsFalse(HeroMoveRules.ForcesWalk(state), "nothing but aiming forces walking");
            var gait = HeroMoveRules.KeysGait(sneaking: false, walk: false, sprintFlag: true, forwardKey: true, sprinting: false, stamina: 100);
            Assert.AreEqual(HeroGait.Sprint, HeroMoveRules.Gait(state, gait));
        }

        [Test]
        public void Aiming_StillWalks_FixFortyTwo()
        {
            var gait = HeroMoveRules.KeysGait(false, false, true, true, false, 100);
            Assert.AreEqual(HeroGait.Walk, HeroMoveRules.Gait(HeroMoveState.RangedAim, gait));
            Assert.IsTrue(HeroMoveRules.ForcesWalk(HeroMoveState.RangedAim));
        }

        [Test]
        public void MovesAndCasts_KeepTheGaitOfTheLoop()
        {
            Assert.IsFalse(HeroMoveRules.UpdatesGait(HeroMoveState.MeleeMove));
            Assert.IsFalse(HeroMoveRules.UpdatesGait(HeroMoveState.MagicCast));
            Assert.IsTrue(HeroMoveRules.UpdatesGait(HeroMoveState.MeleeSneak));
        }

        [Test]
        public void Sprint_NeedsTheForwardKey()
        {
            Assert.AreEqual(HeroGait.Run, HeroMoveRules.KeysGait(false, false, true, forwardKey: false, sprinting: false, stamina: 100));
        }

        [Test]
        public void Sprint_StartsAt21Stamina_GoesOnDownTo1()
        {
            Assert.AreEqual(HeroGait.Run, HeroMoveRules.KeysGait(false, false, true, true, sprinting: false, stamina: 20));
            Assert.AreEqual(HeroGait.Sprint, HeroMoveRules.KeysGait(false, false, true, true, sprinting: false, stamina: 21));
            Assert.AreEqual(HeroGait.Sprint, HeroMoveRules.KeysGait(false, false, true, true, sprinting: true, stamina: 1));
            Assert.AreEqual(HeroGait.Run, HeroMoveRules.KeysGait(false, false, true, true, sprinting: true, stamina: 0));
        }

        [Test]
        public void WalkKey_PressedMovingSprints_PressedStandingWalks()
        {
            Assert.AreEqual(WalkKeyFlag.Sprint, HeroMoveRules.WalkKeyPress(moving: true));
            Assert.AreEqual(WalkKeyFlag.Walk, HeroMoveRules.WalkKeyPress(moving: false));
            Assert.AreEqual(HeroGait.Walk, HeroMoveRules.KeysGait(false, walk: true, sprintFlag: false, forwardKey: true, sprinting: false, stamina: 100));
        }

        [Test]
        public void Sneaking_And_Walking_ComeBeforeTheSprint()
        {
            Assert.AreEqual(HeroGait.Sneak, HeroMoveRules.KeysGait(true, false, true, true, false, 100));
            Assert.AreEqual(HeroGait.Walk, HeroMoveRules.KeysGait(false, true, true, true, false, 100));
            Assert.AreEqual(HeroGait.Run, HeroMoveRules.KeysGait(false, false, false, true, false, 100));
        }

        // ------------------------------------------------------------------ speed per drawn set (motion data)

        [TestCase("None_None", 6.0f, 3.0f)]  // unarmed: Sprint Fwd 600, no sideways sprint (Run 300)
        [TestCase("None_1H", 5.0f, 4.0f)]    // 1H (axes too: they draw as 1H/2H)
        [TestCase("None_2H", 5.0f, 4.0f)]
        [TestCase("None_Staff", 5.0f, 4.0f)]
        [TestCase("None_Fist", 5.0f, 4.0f)]
        [TestCase("None_Cast", 5.0f, 4.0f)]  // spell readied
        [TestCase("Shield_1H", 5.0f, 4.0f)]
        [TestCase("1H_1H", 5.0f, 4.0f)]      // dual
        [TestCase("Torch_1H", 5.0f, 4.0f)]
        [TestCase("Staff_Cast", 6.0f, 3.0f)] // mage staff + spell hand: Sprint Fwd 600
        [TestCase("Bow_Arrow", 6.0f, 3.0f)]  // no Sprint motion: the unarmed sprint speed [interp]
        [TestCase("CrossBow_Bolt", 6.0f, 3.0f)]
        [TestCase("Torch_None", 6.0f, 3.0f)]
        public void Sprint_MovesAtTheDrawnSetsSprintMotion(string hands, float fwd, float side)
        {
            Assert.AreEqual(fwd, ControllerSpeed(hands, HeroGait.Sprint, HeroMoveDir.Fwd), 1e-4f, hands + " forward");
            Assert.AreEqual(side, ControllerSpeed(hands, HeroGait.Sprint, HeroMoveDir.Side), 1e-4f, hands + " sideways");
            Assert.AreEqual(4f, ControllerSpeed(hands, HeroGait.Sprint, HeroMoveDir.Back), 1e-4f, hands + " back (Run Back)");
        }

        [TestCase("None_None", 1.6f)]
        [TestCase("None_1H", 1.6f)]
        [TestCase("None_Cast", 1.6f)]
        [TestCase("Bow_Arrow", 1.5f)]
        [TestCase("CrossBow_Bolt", 2.0f)]
        public void Walk_MovesAtTheDrawnSetsWalkMotion(string hands, float walk)
        {
            foreach (HeroMoveDir dir in Enum.GetValues(typeof(HeroMoveDir)))
                Assert.AreEqual(walk, ControllerSpeed(hands, HeroGait.Walk, dir), 1e-4f, hands + " " + dir);
        }

        [TestCase("None_None")]
        [TestCase("None_1H")]
        [TestCase("None_2H")]
        [TestCase("None_Fist")]
        [TestCase("None_Cast")]
        [TestCase("Shield_1H")]
        [TestCase("1H_1H")]
        [TestCase("Bow_Arrow")]
        [TestCase("CrossBow_Bolt")]
        public void Run_IsTheSameWithEveryHand(string hands)
        {
            Assert.AreEqual(4f, ControllerSpeed(hands, HeroGait.Run, HeroMoveDir.Fwd), 1e-4f);
            Assert.AreEqual(4f, ControllerSpeed(hands, HeroGait.Run, HeroMoveDir.Back), 1e-4f);
            Assert.AreEqual(3f, ControllerSpeed(hands, HeroGait.Run, HeroMoveDir.Side), 1e-4f);
        }

        [Test]
        public void MissingSets_LeaveTheUnarmedSpeeds()
        {
            var none = new HeroGaitSpeeds(0, 0, 0, 0, 0, 0, 0, 0);
            Assert.AreEqual(1f, HeroGaitSpeeds.Factor(none, Set("None_None"), HeroGait.Sprint, HeroMoveDir.Fwd));
            Assert.AreEqual(1f, HeroGaitSpeeds.Factor(Set("None_1H"), Set("None_None"), HeroGait.Sneak, HeroMoveDir.Fwd));
        }

        // ------------------------------------------------------------------ fix-58: the pose of the set (shield guard P1)

        // CP 1.75: the shield guard set Hero_Stand_Shield_1H_P1_Move_* has Walk/Run (ending P1) but no Sprint.
        const string ShieldGuardSet = "Run_Back=400 Run_Fwd=400 Run_Left=300 Run_Right=300 Walk_Back=160 Walk_Fwd=160 Walk_Left=160 Walk_Right=160";

        /// <summary>The speeds the hero animator hands the controller: the standing set in the pose
        /// <see cref="StancePose.For"/> picks, gaits it lacks taken from <see cref="StancePose.MoveFallbackPose"/>.</summary>
        static HeroGaitSpeeds PosedSet(string left, string right)
        {
            string hands = left + "_" + right, pose = StancePose.For("Stand", left, "P0"), fallback = StancePose.MoveFallbackPose("Stand", left, "P0");
            HeroGaitSpeeds Lookup(string p) => p == "P0" ? Set(hands) : Parse(hands == "Shield_1H" && p == "P1" ? ShieldGuardSet : "");
            var own = Lookup(pose);
            var fb = fallback != null ? Lookup(fallback) : (HeroGaitSpeeds?)null;
            return HeroGaitSpeeds.From((g, d) =>
            {
                float v = own.Motion(Gait(g), Dir(d));
                if (Has(hands, pose, g, d)) return v;
                return fb.HasValue && Has(hands, fallback, g, d) ? fb.Value.Motion(Gait(g), Dir(d)) : 0f;
            });
        }

        static bool Has(string hands, string pose, string g, string d)
        {
            string data = pose == "P0" ? Sets[hands] : hands == "Shield_1H" && pose == "P1" ? ShieldGuardSet : "";
            return (" " + data + " ").Contains(" " + g + "_" + d + "=");
        }

        static HeroGait Gait(string g) => g == "Walk" ? HeroGait.Walk : g == "Run" ? HeroGait.Run : HeroGait.Sprint;
        static HeroMoveDir Dir(string d) => d == "Fwd" ? HeroMoveDir.Fwd : d == "Back" ? HeroMoveDir.Back : HeroMoveDir.Side;

        static HeroGaitSpeeds Parse(string data)
        {
            var speeds = new Dictionary<string, float>();
            foreach (var part in data.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=');
                speeds[kv[0]] = float.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture) / 100f;
            }
            return HeroGaitSpeeds.From((g, d) => speeds.TryGetValue(g + "_" + d, out float v) ? v : 0f);
        }

        static float PosedSpeed(string left, string right, HeroGait gait, HeroMoveDir dir)
        {
            float field = gait == HeroGait.Walk ? 1.6f : dir == HeroMoveDir.Side ? 3f : dir == HeroMoveDir.Back ? 4f : gait == HeroGait.Sprint ? 6f : 4f;
            return field * HeroGaitSpeeds.Factor(PosedSet(left, right), Set("None_None"), gait, dir);
        }

        [Test]
        public void ShieldGuardPose_FallsBackToP0ForTheMissingSprint()
        {
            Assert.AreEqual("P1", StancePose.For("Stand", "Shield", "P0"));
            Assert.AreEqual("P0", StancePose.MoveFallbackPose("Stand", "Shield", "P0"));
            Assert.IsNull(StancePose.MoveFallbackPose("Stand", "None", "P0"), "other hands keep their own pose: no fallback");
            Assert.IsNull(StancePose.MoveFallbackPose("Parade", "Shield", "P0"), "the parade exists only in P1");
            Assert.IsNull(StancePose.MoveFallbackPose("Sneak", "Shield", "P0"));
        }

        // runner-31 drawn_gaits step 45: 1H + shield drawn sprinted at the unarmed 6.0 m/s (P1 set without Sprint).
        // One case per weapon class: drawn sprint 5 m/s forward / 4 sideways, run 4 / 3, walk 1.6 (bow 1.5, crossbow 2.0).
        [TestCase("None", "1H", 5.0f, 4.0f, 1.6f)]          // 1H sword
        [TestCase("None", "2H", 5.0f, 4.0f, 1.6f)]          // 2H sword, battle axe
        [TestCase("None", "Staff", 5.0f, 4.0f, 1.6f)]
        [TestCase("None", "Fist", 5.0f, 4.0f, 1.6f)]
        [TestCase("None", "Cast", 5.0f, 4.0f, 1.6f)]        // spell readied
        [TestCase("Shield", "1H", 5.0f, 4.0f, 1.6f)]        // 1H + shield (guard pose P1)
        [TestCase("1H", "1H", 5.0f, 4.0f, 1.6f)]            // dual 1H
        [TestCase("Torch", "1H", 5.0f, 4.0f, 1.6f)]
        [TestCase("Bow", "Arrow", 6.0f, 3.0f, 1.5f)]        // no Sprint motion: unarmed sprint [interp]
        [TestCase("CrossBow", "Bolt", 6.0f, 3.0f, 2.0f)]
        [TestCase("None", "None", 6.0f, 3.0f, 1.6f)]        // unarmed
        public void DrawnGaits_PerWeaponClass_WithTheSetsPose(string left, string right, float sprint, float sprintSide, float walk)
        {
            string h = left + "_" + right;
            Assert.AreEqual(sprint, PosedSpeed(left, right, HeroGait.Sprint, HeroMoveDir.Fwd), 1e-4f, h + " sprint");
            Assert.AreEqual(sprintSide, PosedSpeed(left, right, HeroGait.Sprint, HeroMoveDir.Side), 1e-4f, h + " sprint sideways");
            Assert.AreEqual(4f, PosedSpeed(left, right, HeroGait.Run, HeroMoveDir.Fwd), 1e-4f, h + " run");
            Assert.AreEqual(3f, PosedSpeed(left, right, HeroGait.Run, HeroMoveDir.Side), 1e-4f, h + " run sideways");
            Assert.AreEqual(walk, PosedSpeed(left, right, HeroGait.Walk, HeroMoveDir.Fwd), 1e-4f, h + " walk");
        }

        [TestCase("Bow", "Arrow", 1.5f)]
        [TestCase("CrossBow", "Bolt", 2.0f)]
        public void Aiming_WalksAtTheRangedWalkMotion(string left, string right, float walk)
        {
            var gait = HeroMoveRules.Gait(HeroMoveState.RangedAim, HeroMoveRules.KeysGait(false, false, true, true, false, 100));
            Assert.AreEqual(HeroGait.Walk, gait);
            foreach (HeroMoveDir dir in Enum.GetValues(typeof(HeroMoveDir)))
                Assert.AreEqual(walk, PosedSpeed(left, right, gait, dir), 1e-4f, dir.ToString());
        }

        // ------------------------------------------------------------------ fix-48: long frames keep their distance

        [TestCase(0.016f, 1)]
        [TestCase(0.1f, 1)]
        [TestCase(0.15f, 2)]
        [TestCase(0.25f, 3)]
        [TestCase(1.0f, 4)] // capped at Time.maximumDeltaTime's 1/3 s
        public void LongFrame_IsSplitIntoSubSteps_NotCut(float dt, int steps)
        {
            int n = HeroMoveRules.SubSteps(dt);
            Assert.AreEqual(steps, n);
            float covered = Math.Min(dt, HeroMoveRules.MaxFrameSeconds);
            Assert.LessOrEqual(covered / n, HeroMoveRules.MaxStepSeconds + 1e-5f, "each sub-step at most 0.1 s");
            // r29 drawn_gaits: the old cut to 0.1 s moved a 6 m/s sprint 0.6 m in a 0.25 s frame (2.4 m/s)
            Assert.AreEqual(6f * Math.Min(dt, HeroMoveRules.MaxFrameSeconds), 6f * (covered / n) * n, 1e-4f);
        }
}
}
