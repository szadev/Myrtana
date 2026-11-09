using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// The G3 CP 1.75 melee combo graph per weapon class (Docs/combat.md §9.5), walked with simulated button events:
    /// <see cref="MeleeInputInterpreter"/> → the combo pose (<see cref="MeleeComboRules.NextComboPose"/>), the quick
    /// attack's side, the move's timeline from the hero's Fwd motion lengths (G3Cli anim motion) and speed factors → the
    /// next queued move at the move's end. Each started stage is checked: action, pose, start time and its combatlog line.
    /// </summary>
    public class MeleeComboGraphTests
    {
        // ------------------------------------------------------------------------------------------- the simulated body

        /// <summary>(seconds, end pose) of a hero motion: set / action word / phase / start pose.</summary>
        static readonly Dictionary<string, (double len, int end)> Motions = new Dictionary<string, (double, int)>
        {
            // One-handed (None_1H; Shield_1H has the same lengths).
            ["1H Attack Hit P0"] = (0.32, 1), ["1H Attack Hit P1"] = (0.36, 2), ["1H Attack Hit P2"] = (0.32, 3), ["1H Attack Hit P3"] = (0.44, 0),
            ["1H Attack Recover P1"] = (0.44, 1), ["1H Attack Recover P2"] = (0.56, 2), ["1H Attack Recover P3"] = (0.64, 3), ["1H Attack Recover P0"] = (0.56, 0),
            ["1H PowerAttack Raise P0"] = (0.76, 0), ["1H PowerAttack Hit P0"] = (0.64, 1), ["1H PowerAttack Recover P1"] = (1.12, 1),
            ["1H QuickAttackR Hit P0"] = (0.40, 0), ["1H QuickAttackL Hit P0"] = (0.44, 0), ["1H QuickAttackL Hit P1"] = (0.40, 0),
            ["1H QuickAttackR Hit P2"] = (0.40, 0), ["1H QuickAttackL Hit P3"] = (0.40, 0),
            ["1H QuickAttackR Recover P0"] = (0.48, 0), ["1H QuickAttackL Recover P0"] = (0.44, 0),
            ["1H PierceAttack Raise P0"] = (0.84, 0), ["1H PierceAttack Hit P0"] = (0.52, 1), ["1H PierceAttack Recover P1"] = (0.80, 0),
            ["1H TwirlAttack Hit P0"] = (1.28, 0),
            ["1H GetUpAttack Hit P0"] = (0.40, 1), ["1H GetUpAttack Recover P1"] = (0.24, 1),
            // Two-handed (None_2H, also axes); staves and halberds (None_Staff) have the same lengths.
            ["2H Attack Hit P0"] = (0.52, 1), ["2H Attack Hit P1"] = (0.40, 0), ["2H Attack Recover P1"] = (0.92, 1), ["2H Attack Recover P0"] = (0.76, 0),
            ["2H PowerAttack Raise P0"] = (0.88, 0), ["2H PowerAttack Hit P0"] = (0.40, 1), ["2H PowerAttack Recover P1"] = (1.04, 1),
            ["2H QuickAttackR Hit P0"] = (0.52, 1), ["2H QuickAttackL Hit P1"] = (0.40, 0),
            ["2H QuickAttackR Recover P1"] = (0.92, 1), ["2H QuickAttackL Recover P0"] = (0.76, 0),
            ["2H WhirlAttack Hit P0"] = (0.80, 0), ["2H WhirlAttack Recover P0"] = (0.96, 0),
            ["2H GetUpAttack Hit P0"] = (0.40, 1), ["2H GetUpAttack Recover P1"] = (0.24, 1),
            // Fists (None_Fist).
            ["Fist Attack Hit P0"] = (0.32, 1), ["Fist Attack Hit P1"] = (0.32, 0), ["Fist Attack Recover P1"] = (0.36, 1), ["Fist Attack Recover P0"] = (0.36, 0),
            ["Fist PowerAttack Raise P0"] = (0.44, 0), ["Fist PowerAttack Hit P0"] = (0.32, 1), ["Fist PowerAttack Recover P1"] = (0.36, 1),
            ["Fist GetUpAttack Hit P0"] = (0.68, 0), ["Fist GetUpAttack Recover P0"] = (0.16, 0),
            ["Any JumpBack Hit P0"] = (0.50, 0),
            // Dual one-handed (1H_1H, fix-32): P0 and P1 sets; Attack P0 → P1 → P0, PowerAttack P0 → P0 (right swing) / P1 → P1
            // (left swing), quick attacks keep their pose (P3 QuickL → P61 = P0), WhirlAttack (the SimpleWhirl) P0 ↔ P1,
            // PierceAttack (Parade set) keeps the pose.
            ["1H_1H Attack Hit P0"] = (0.32, 1), ["1H_1H Attack Hit P1"] = (0.36, 0),
            ["1H_1H Attack Recover P0"] = (0.56, 0), ["1H_1H Attack Recover P1"] = (0.64, 1),
            ["1H_1H PowerAttack Raise P0"] = (0.72, 0), ["1H_1H PowerAttack Hit P0"] = (0.72, 0), ["1H_1H PowerAttack Recover P0"] = (1.12, 0),
            ["1H_1H PowerAttack Raise P1"] = (0.72, 1), ["1H_1H PowerAttack Hit P1"] = (0.76, 1), ["1H_1H PowerAttack Recover P1"] = (1.08, 1),
            ["1H_1H QuickAttackR Hit P0"] = (0.40, 0), ["1H_1H QuickAttackL Hit P0"] = (0.44, 0),
            ["1H_1H QuickAttackR Hit P1"] = (0.44, 1), ["1H_1H QuickAttackL Hit P1"] = (0.44, 1), ["1H_1H QuickAttackL Hit P3"] = (0.44, 0),
            ["1H_1H QuickAttackR Recover P0"] = (0.48, 0), ["1H_1H QuickAttackL Recover P0"] = (0.44, 0),
            ["1H_1H QuickAttackR Recover P1"] = (0.44, 1), ["1H_1H QuickAttackL Recover P1"] = (0.40, 1),
            ["1H_1H WhirlAttack Hit P0"] = (0.60, 1), ["1H_1H WhirlAttack Hit P1"] = (0.60, 0),
            ["1H_1H WhirlAttack Recover P0"] = (0.36, 0), ["1H_1H WhirlAttack Recover P1"] = (0.36, 1),
            ["1H_1H PierceAttack Raise P0"] = (0.56, 0), ["1H_1H PierceAttack Hit P0"] = (0.40, 0), ["1H_1H PierceAttack Recover P0"] = (0.56, 0),
            ["1H_1H PierceAttack Raise P1"] = (0.56, 1), ["1H_1H PierceAttack Hit P1"] = (0.40, 1), ["1H_1H PierceAttack Recover P1"] = (0.56, 1),
            ["1H_1H TwirlAttack Hit P0"] = (1.28, 0),
            ["1H_1H GetUpAttack Hit P0"] = (0.40, 1), ["1H_1H GetUpAttack Recover P1"] = (0.24, 1),
        };

        static string SetOf(WeaponClass w) =>
            w == WeaponClass.Fist ? "Fist" : w == WeaponClass.OneHanded ? "1H" : "2H"; // axe → 2H set, staff/halberd → Staff (same lengths as 2H)

        /// <summary>The motion word(s) of a move as CombatMotionNames tries them.</summary>
        static string[] Words(CombatAction a)
        {
            switch (a)
            {
                case CombatAction.QuickAttackRight: return new[] { "QuickAttackR", "QuickAttackL" };
                case CombatAction.QuickAttackLeft: return new[] { "QuickAttackL", "QuickAttackR" };
                case CombatAction.HackAttack: return new[] { "PowerAttack" };
                case CombatAction.SimpleWhirl: return new[] { "WhirlAttack", "TwirlAttack" };
                default: return new[] { a.ToString() };
            }
        }

        sealed class Sim
        {
            public readonly MeleeInputInterpreter Input = new MeleeInputInterpreter();
            public readonly List<string> Stages = new List<string>();
            public readonly List<string> Lines = new List<string>();
            /// <summary>Per stage: which blades strike (DualWieldRules.Blades), "R", "L" or "RL".</summary>
            public readonly List<string> Blades = new List<string>();
            readonly WeaponClass _weapon;
            readonly bool _dual;
            MeleeStance _stance;
            double _end = double.MaxValue, _hitStart, _tailEnd = -1, _now;
            int _pose, _hitEndPose = -1, _stage;
            bool _hitDirection, _moving;

            public Sim(MeleeStance stance, bool dual = false)
            {
                _stance = stance;
                _weapon = stance.RightHand;
                _dual = dual;
            }

            public void Run(string events, int untilMs)
            {
                var list = events.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                                 .Select(e => (button: e[0], down: e[1] == '+', ms: int.Parse(e.Substring(2), CultureInfo.InvariantCulture)))
                                 .ToList();
                for (int ms = 0; ms <= untilMs; ms++)
                {
                    _now = ms / 1000.0;
                    foreach (var e in list.Where(e => e.ms == ms))
                    {
                        if (e.button == 'L') Feed(e.down ? Input.PrimaryPressed(_now, _stance) : Input.PrimaryReleased(_now, _stance));
                        else Feed(e.down ? Input.SecondaryPressed(_now, _stance) : Input.SecondaryReleased(_now, _stance));
                        _stance.SittingKnockedDown = false; // the first press gets him up
                    }
                    Feed(Input.Update(_now, _stance));
                    if (_moving && _now >= _end - 1e-9)
                    {
                        _moving = false;
                        Feed(Input.MoveEnded(_now, _stance));
                    }
                }
            }

            void Feed(MeleeCommand c)
            {
                for (int guard = 0; guard < 4 && c.Kind == MeleeCommandKind.StartMove; guard++)
                {
                    if (Start(c.Move)) return;
                    c = Input.MoveEnded(_now, _stance);
                }
            }

            bool Start(CombatAction asked)
            {
                // PlayerCombat: chained while the last attack's Hit (or its wait) or its Recover tail plays.
                bool chained = (_moving && _now >= _hitStart) || (!_moving && _now < _tailEnd);
                _pose = MeleeComboRules.NextComboPose(chained, _hitEndPose, _pose);
                var action = asked == CombatAction.QuickAttack ? MeleeComboRules.QuickAttackSide(_pose, _hitDirection, _stance.LeftOneHandedOrTorch) : asked;
                string set = action == CombatAction.JumpBack ? "Any" : _dual ? "1H_1H" : SetOf(_weapon);
                (double len, int end) hit = default, raise = default, recover = default;
                bool found = false;
                foreach (var w in Words(action))
                    if (Motions.TryGetValue($"{set} {w} Hit P{_pose}", out hit) || Motions.TryGetValue($"{set} {w} Hit P0", out hit))
                    {
                        found = true;
                        Motions.TryGetValue($"{set} {w} Raise P{_pose}", out raise);
                        if (!Motions.TryGetValue($"{set} {w} Recover P{hit.end}", out recover)) recover = (0.5, hit.end);
                        break;
                    }
                if (!found) return false;
                var shape = MeleeComboRules.ShapeOf(action);
                double speed = Speed(action, false), raiseSpeed = Speed(action, true);
                var t = MeleeComboRules.Timeline(shape, raise.len, hit.len, recover.len, raiseSpeed, speed);
                _hitStart = _now + t.HitStart;
                _end = _now + t.End;
                _tailEnd = _now + t.TailEnd;
                _hitEndPose = hit.end;
                _moving = true;
                _hitDirection = MeleeComboRules.HitDirectionAfter(action, _pose, _hitDirection);
                _stage = MeleeComboRules.NextStage(chained, _stage);
                Stages.Add($"{action} P{_pose} @{Math.Round(_now * 1000)}");
                Lines.Add(MeleeComboRules.StageLine(_stage, action, _pose, asked));
                var blades = DualWieldRules.Blades(action, _pose, _dual);
                Blades.Add(blades == MeleeBlades.Both ? "RL" : blades == MeleeBlades.Left ? "L" : "R");
                return true;
            }

            double Speed(CombatAction a, bool windUp) =>
                MotionSpeedRules.Factor(new MotionSpeedQuery { Motion = MotionSpeedRules.MotionOf(a), WindUp = windUp && a == CombatAction.PowerAttack, IsPlayer = true, RightHand = _weapon, DualOneHanded = _dual, Stamina = 100 },
                                        new RuleOptions());
        }

        static MeleeStance Hands(WeaponClass w, bool torch = false, bool sitting = false) =>
            new MeleeStance { RightHand = w, CanParade = true, LeftOneHandedOrTorch = torch, SittingKnockedDown = sitting };

        // ------------------------------------------------------------------------------------------- the graph

        // Events: L/R = left/right button, + press, - release, at ms. Expected: "Action Pose @startMs" per stage.
        static IEnumerable<TestCaseData> Graph()
        {
            // Left-button combo (tuned earlier, unchanged): 1H 533/1133 ms, 2H/axe/staff/halberd 743/1314, fists 320/640.
            yield return Case("1H taps chain Attack P0-P1-P2", WeaponClass.OneHanded, "L+0 L-50 L+100 L-150 L+600 L-650", 2000,
                              "Attack P0 @50; Attack P1 @584; Attack P2 @1184");
            yield return Case("2H taps chain Attack P0-P1-P0", WeaponClass.TwoHanded, "L+0 L-50 L+100 L-150 L+800 L-850", 2500,
                              "Attack P0 @50; Attack P1 @793; Attack P0 @1365");
            yield return Case("Axe = 2H set", WeaponClass.Axe, "L+0 L-50 L+100 L-150", 1500, "Attack P0 @50; Attack P1 @793");
            yield return Case("Staff", WeaponClass.Staff, "L+0 L-50 L+100 L-150", 1500, "Attack P0 @50; Attack P1 @793");
            yield return Case("Halberd", WeaponClass.Halberd, "L+0 L-50 L+100 L-150", 1500, "Attack P0 @50; Attack P1 @793");
            yield return Case("Fists chain Attack P0-P1-P0", WeaponClass.Fist, "L+0 L-50 L+100 L-150 L+400 L-450", 1500,
                              "Attack P0 @50; Attack P1 @370; Attack P0 @690");

            // Right-button quick attacks: each quick release is queued like a tap (owner report: always one hit).
            yield return Case("1H right clicks chain QuickAttack R/L (short clicks)", WeaponClass.OneHanded,
                              "R+0 R-40 R+150 R-190 R+600 R-640 R+1100 R-1140", 2500,
                              "QuickAttackRight P0 @40; QuickAttackLeft P0 @540; QuickAttackRight P0 @1080; QuickAttackLeft P0 @1580");
            yield return Case("1H right clicks (80 ms: parade up, riposte, then queued)", WeaponClass.OneHanded,
                              "R+0 R-80 R+150 R-230 R+600 R-680", 2000,
                              "QuickAttackRight P0 @80; QuickAttackLeft P0 @580; QuickAttackRight P0 @1120");
            yield return Case("Shield + 1H right clicks", WeaponClass.OneHanded, "R+0 R-40 R+150 R-190", 1500,
                              "QuickAttackRight P0 @40; QuickAttackLeft P0 @540");
            yield return Case("2H riposte chain QuickAttack R P0 → L P1 → R P0", WeaponClass.TwoHanded,
                              "R+0 R-40 R+300 R-340 R+900 R-940", 2500,
                              "QuickAttackRight P0 @40; QuickAttackLeft P1 @660; QuickAttackRight P0 @1160");
            yield return Case("Fists right click = JumpBack (no quick attack)", WeaponClass.Fist, "R+0 R-40", 800, "JumpBack P0 @40");

            // Block held (right button down) + left clicks.
            yield return Case("1H block + left taps chain quick attacks (no parade between)", WeaponClass.OneHanded,
                              "R+0 L+300 L-340 L+500 L-540 L+1000 L-1040", 2500,
                              "QuickAttackRight P0 @340; QuickAttackLeft P0 @840; QuickAttackRight P0 @1380");
            yield return Case("2H block + left taps chain whirls", WeaponClass.TwoHanded, "R+0 L+300 L-340 L+500 L-540", 2500,
                              "WhirlAttack P0 @340; WhirlAttack P0 @1140");
            yield return Case("Fists block + left tap: nothing (no quick attack)", WeaponClass.Fist, "R+0 L+300 L-340", 1000, "");
            yield return Case("Fists block + left hold: nothing (no hack)", WeaponClass.Fist, "R+0 L+300 L-700", 1500, "");
            yield return Case("1H block + hold = Pierce, release ≤ 0.7 s → QuickAttack at once", WeaponClass.OneHanded,
                              "R+0 L+300 L-700", 2000, "PierceAttack P0 @401; QuickAttackRight P0 @700");
            yield return Case("2H block + hold = Hack, release ≤ 0.2 s → Whirl at once", WeaponClass.TwoHanded,
                              "R+0 L+300 L-550", 2500, "HackAttack P0 @401; WhirlAttack P0 @550");
            yield return Case("Left tap within 50 ms of the right press: no block flag yet → Attack", WeaponClass.OneHanded,
                              "R+0 L+10 L-40", 1000, "Attack P0 @40");

            // Hold moves and their release combos.
            yield return Case("1H power released ≤ 0.2 s → Attack at once", WeaponClass.OneHanded, "L+0 L-250", 1500,
                              "PowerAttack P0 @101; Attack P0 @250");
            yield return Case("1H power + torch released 0.2–0.5 s → SimpleWhirl", WeaponClass.OneHanded, "L+0 L-500", 2500,
                              "PowerAttack P0 @101; SimpleWhirl P0 @500", torch: true);
            yield return Case("1H power: a fresh tap after 0.5 s of it is dropped (G3 reads the power state)", WeaponClass.OneHanded,
                              "L+0 L-700 L+800 L-850", 2500, "PowerAttack P0 @101");

            // Knocked into sitting.
            yield return Case("Sitting: left press = GetUpAttack", WeaponClass.OneHanded, "L+0 L-50", 1000, "GetUpAttack P0 @0", sitting: true);

            // Dual one-handed (1H_1H, fix-32): the left blade counts as "1H in the left hand" (SimpleWhirl, P1 quick sides).
            yield return Dual("1H_1H taps chain Attack P0-P1-P0", "L+0 L-50 L+100 L-150 L+600 L-650", 2000,
                              "Attack P0 @50; Attack P1 @584; Attack P0 @1184");
            yield return Dual("1H_1H right clicks chain QuickAttack R/L in P0", "R+0 R-40 R+150 R-190 R+600 R-640", 2000,
                              "QuickAttackRight P0 @40; QuickAttackLeft P0 @540; QuickAttackRight P0 @1080");
            yield return Dual("1H_1H Attack then a right click: QuickAttack from P1 (left blade)", "L+0 L-50 R+200 R-240", 1500,
                              "Attack P0 @50; QuickAttackLeft P1 @584");
            yield return Dual("1H_1H power released 0.2–0.5 s → SimpleWhirl (left blade = 1H in the left hand)", "L+0 L-500", 2500,
                              "PowerAttack P0 @101; SimpleWhirl P0 @500");
            yield return Dual("1H_1H power released ≤ 0.2 s → Attack", "L+0 L-250", 1500, "PowerAttack P0 @101; Attack P0 @250");
            yield return Dual("1H_1H block + hold = Pierce, release ≤ 0.7 s → QuickAttack", "R+0 L+300 L-700", 2000,
                              "PierceAttack P0 @401; QuickAttackRight P0 @700");
            yield return Dual("1H_1H block + left taps chain quick attacks (no whirl: not heavy)", "R+0 L+300 L-340 L+500 L-540", 2000,
                              "QuickAttackRight P0 @340; QuickAttackLeft P0 @840");
            yield return Dual("1H_1H sitting: left press = GetUpAttack", "L+0 L-50", 1000, "GetUpAttack P0 @0", sitting: true);
        }

        static TestCaseData Case(string name, WeaponClass w, string events, int untilMs, string expected, bool torch = false, bool sitting = false) =>
            new TestCaseData(Hands(w, torch, sitting), events, untilMs, expected, false).SetName("ComboGraph: " + name);

        /// <summary>A dual wielder: a one-handed weapon in each hand (the 1H_1H motion set).</summary>
        static TestCaseData Dual(string name, string events, int untilMs, string expected, bool sitting = false) =>
            new TestCaseData(Hands(WeaponClass.OneHanded, torch: true, sitting), events, untilMs, expected, true).SetName("ComboGraph: " + name);

        [TestCaseSource(nameof(Graph))]
        public void Walk(MeleeStance stance, string events, int untilMs, string expected, bool dual)
        {
            var sim = new Sim(stance, dual);
            sim.Run(events, untilMs);
            Assert.AreEqual(expected, string.Join("; ", sim.Stages));
            // One combatlog line per stage, numbered 1, 2, 3 … along a chain.
            Assert.AreEqual(sim.Stages.Count, sim.Lines.Count);
            for (int i = 0; i < sim.Lines.Count; i++)
                StringAssert.StartsWith("COMBO ", sim.Lines[i]);
        }

        [Test]
        public void Dual_BladesPerStage_AsG3AttackCommands()
        {
            // Attack and PowerAttack strike with both blades; a quick attack from P1 with the left one, from P0 with the right.
            var sim = new Sim(Hands(WeaponClass.OneHanded, torch: true), dual: true);
            sim.Run("L+0 L-50 R+200 R-240", 1500);
            Assert.AreEqual("RL; L", string.Join("; ", sim.Blades));
            sim = new Sim(Hands(WeaponClass.OneHanded, torch: true), dual: true);
            sim.Run("R+0 R-40", 800);
            Assert.AreEqual("R", string.Join("; ", sim.Blades));
            sim = new Sim(Hands(WeaponClass.OneHanded));
            sim.Run("L+0 L-50", 800);
            Assert.AreEqual("R", string.Join("; ", sim.Blades), "one blade without the second weapon");
        }

        [Test]
        public void StageLines_CountAlongTheChain_AndNameTheSide()
        {
            var sim = new Sim(Hands(WeaponClass.OneHanded));
            sim.Run("R+0 R-40 R+150 R-190 R+600 R-640", 2000);
            Assert.AreEqual("COMBO 1: QuickAttackRight P0 [Hit+wait 100 ms (+Recover tail)] (asked QuickAttack)", sim.Lines[0]);
            StringAssert.StartsWith("COMBO 2: QuickAttackLeft P0", sim.Lines[1]);
            StringAssert.StartsWith("COMBO 3: QuickAttackRight P0", sim.Lines[2]);
        }

        [Test]
        public void ParadeComesBackAfterAMove_WhileTheBlockIsHeld_FixSeventyFive()
        {
            // G3 FUN_10002130 (PS_Melee_Loop): parade flag DAT_10118ab0 + block flag + empty queue → Parade queued again.
            var input = new MeleeInputInterpreter();
            var sword = Hands(WeaponClass.OneHanded);
            input.SecondaryPressed(0, sword);
            Assert.AreEqual(MeleeCommand.RaiseParade, input.Update(0.06, sword));
            input.PrimaryPressed(0.3, sword);
            Assert.AreEqual(MeleeCommand.Start(CombatAction.QuickAttack), input.PrimaryReleased(0.34, sword));
            Assert.AreEqual(MeleeCommand.RaiseParade, input.MoveEnded(0.84, sword), "the block comes back at the move's end");
            Assert.IsTrue(input.InParade);
            Assert.AreEqual(MeleeCommand.None, input.Update(1.0, sword), "raised once, not every frame");
            Assert.AreEqual(MeleeCommand.LowerParade, input.SecondaryReleased(1.2, sword));
            Assert.IsFalse(input.ParadeWanted, "the release clears the parade flag");
        }

        [Test]
        public void RightHeldDuringAMove_QueuesTheParade_ATapReplacesIt()
        {
            var input = new MeleeInputInterpreter();
            var sword = Hands(WeaponClass.OneHanded);
            input.PrimaryPressed(0, sword);
            Assert.AreEqual(MeleeCommand.Start(CombatAction.Attack), input.PrimaryReleased(0.05, sword));
            input.SecondaryPressed(0.1, sword);
            Assert.AreEqual(MeleeCommand.None, input.Update(0.16, sword));
            Assert.AreEqual(CombatAction.Parade, input.Queued, "busy: the parade waits in the queue");
            Assert.AreEqual(MeleeCommand.RaiseParade, input.MoveEnded(0.6, sword), "still held at the move's end");

            input = new MeleeInputInterpreter();
            input.PrimaryPressed(0, sword);
            input.PrimaryReleased(0.05, sword);
            input.SecondaryPressed(0.1, sword);
            input.Update(0.16, sword);
            Assert.AreEqual(MeleeCommand.None, input.SecondaryReleased(0.2, sword));
            Assert.AreEqual(CombatAction.QuickAttack, input.Queued, "the quick release replaces the queued parade");
        }

        // ------------------------------------------------------------------------------------------- pure rules

        [TestCase(0, false, false, CombatAction.QuickAttackRight)]
        [TestCase(0, true, false, CombatAction.QuickAttackLeft)]
        [TestCase(1, false, false, CombatAction.QuickAttackLeft)]
        [TestCase(1, true, false, CombatAction.QuickAttackLeft)]
        [TestCase(1, false, true, CombatAction.QuickAttackRight)]
        [TestCase(1, true, true, CombatAction.QuickAttackLeft)]
        [TestCase(2, false, false, CombatAction.QuickAttackRight)]
        [TestCase(3, false, false, CombatAction.QuickAttackLeft)]
        public void QuickAttackSide_AsG3AiQuickAttack(int pose, bool hitDirection, bool leftHand, CombatAction expected) =>
            Assert.AreEqual(expected, MeleeComboRules.QuickAttackSide(pose, hitDirection, leftHand));

        [Test]
        public void HitDirection_AfterEachMove()
        {
            Assert.IsTrue(MeleeComboRules.HitDirectionAfter(CombatAction.Attack, 0, false));
            Assert.IsFalse(MeleeComboRules.HitDirectionAfter(CombatAction.Attack, 1, true));
            Assert.IsFalse(MeleeComboRules.HitDirectionAfter(CombatAction.PierceAttack, 0, true));
            Assert.IsTrue(MeleeComboRules.HitDirectionAfter(CombatAction.SimpleWhirl, 3, false));
            Assert.IsTrue(MeleeComboRules.HitDirectionAfter(CombatAction.QuickAttackRight, 0, false));
            Assert.IsFalse(MeleeComboRules.HitDirectionAfter(CombatAction.QuickAttackLeft, 0, true));
            Assert.IsTrue(MeleeComboRules.HitDirectionAfter(CombatAction.GetUpAttack, 0, false));
            Assert.IsTrue(MeleeComboRules.HitDirectionAfter(CombatAction.WhirlAttack, 0, true), "kept");
        }

        [TestCase("P0", 0)]
        [TestCase("P3", 3)]
        [TestCase("P31", 1)]
        [TestCase("P50", 0)]
        [TestCase("P60", 0)]
        [TestCase("P70", 0)]
        [TestCase("P4", -1)]
        [TestCase("R", -1)]
        public void PoseWords_AsG3TrackCurrentPose(string word, int pose) => Assert.AreEqual(pose, MeleeComboRules.PoseOf(word));

        [Test]
        public void QuickAttack_EndsAHundredMsAfterItsHit()
        {
            var shape = MeleeComboRules.ShapeOf(CombatAction.QuickAttackRight);
            Assert.AreEqual(MeleeComboRules.QuickAttackWait, shape.WaitAfterHit, 1e-9);
            var t = MeleeComboRules.Timeline(shape, 0, 0.40, 0.48);
            Assert.AreEqual(0.50, t.End, 1e-9);
            Assert.AreEqual(0.88, t.TailEnd, 1e-9);
            Assert.AreEqual(0, MeleeComboRules.ShapeOf(CombatAction.Attack).WaitAfterHit, "the left combo is untouched");
        }

        [Test]
        public void SitKnockDown_GetUpChoices()
        {
            Assert.AreEqual(CombatAction.GetUpParade, SitKnockDownRules.NpcGetUp(CombatAction.PowerAttack, true));
            Assert.AreEqual(CombatAction.GetUpParade, SitKnockDownRules.NpcGetUp(CombatAction.PierceAttack, true));
            Assert.AreEqual(CombatAction.GetUpParade, SitKnockDownRules.NpcGetUp(CombatAction.HackAttack, true));
            Assert.AreEqual(CombatAction.GetUpAttack, SitKnockDownRules.NpcGetUp(CombatAction.Attack, true));
            Assert.AreEqual(CombatAction.GetUpAttack, SitKnockDownRules.NpcGetUp(CombatAction.None, true));
            Assert.AreEqual(CombatAction.None, SitKnockDownRules.NpcGetUp(CombatAction.Attack, false), "not melee: _AI_StandUp");
            Assert.AreEqual(CombatAction.GetUpAttack, SitKnockDownRules.PlayerGetUp(true, true));
            Assert.AreEqual(CombatAction.GetUpParade, SitKnockDownRules.PlayerGetUp(false, true));
            Assert.IsFalse(SitKnockDownRules.GetsUpByItself(isPlayer: true), "the player sits until a button (G3; owner confirmed, fix-36)");
            Assert.IsTrue(SitKnockDownRules.GetsUpByItself(isPlayer: false));
            Assert.AreEqual(new CombatMoveShape(false, false), MeleeComboRules.ShapeOf(CombatAction.GetUpAttack), "Hit only, Recover tail");
        }

        /// <summary>The input side while sitting: every press edge is a get-up, even with a move left over from before the fall.</summary>
        [Test]
        public void SitKnockDown_PressesAlwaysGetUp()
        {
            var sit = new MeleeStance { RightHand = WeaponClass.OneHanded, SittingKnockedDown = true, CanParade = true };
            var input = new MeleeInputInterpreter();
            // A move was running when the hit came (its end never reported) and a press was held.
            input.PrimaryPressed(0.0, new MeleeStance { RightHand = WeaponClass.OneHanded, CanParade = true });
            Assert.AreEqual(MeleeCommand.Start(CombatAction.GetUpAttack), input.PrimaryPressed(1.0, sit), "LMB while sitting");
            Assert.AreEqual(MeleeCommand.None, input.PrimaryReleased(1.05, sit));
            Assert.AreEqual(MeleeCommand.None, input.Update(1.5, sit), "no hold move while sitting");
            Assert.AreEqual(MeleeCommand.Start(CombatAction.GetUpParade), input.SecondaryPressed(2.0, sit), "RMB while sitting");
            Assert.AreEqual(MeleeCommand.None, input.Update(2.3, sit), "no parade raise while sitting");
            Assert.AreEqual(MeleeCommand.None, input.SecondaryReleased(2.4, sit), "the release is no riposte");
            input.Reset(); // ZS_SitKnockDown ResetAll (PlayerCombat on SatDown)
            Assert.AreEqual(MeleeCommand.Start(CombatAction.GetUpAttack), input.PrimaryPressed(3.0, sit));
        }
    }
}
