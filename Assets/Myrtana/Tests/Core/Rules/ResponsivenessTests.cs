using System.Collections.Generic;
using G3.Player;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// Input-to-motion latency of the hero's melee (fix-45, Docs/combat.md §9.7), frame by frame at 60 fps: physical button
    /// events through <see cref="KeyMap.Default"/> into the real <see cref="MeleeInputInterpreter"/>, the commands applied
    /// to a stand-in of G3CombatAnimator with the Unity update order (PlayerCombat -50 reads the buttons and starts motions
    /// in the same frame; the animator at 150 sees a motion's end one frame after the motion player at 200 reached it).
    /// Timings: the hero's 1H motions (G3Cli anim motion) at their speed factors (<see cref="MotionSpeedRules"/>).
    /// G3 rules checked: the parade waits for the running script move and cuts only a Recover tail; a hit reaction ignores
    /// presses and is over with its Hit (the Recover is a tail); held buttons act right after it; ResetAll on unparried
    /// stumbles; the draw queues moves.
    /// </summary>
    public class ResponsivenessTests
    {
        const double Dt = 1.0 / 60;
        static readonly KeyInput Rmb = KeyInput.Mouse(MouseOffset.Right), Lmb = KeyInput.Mouse(MouseOffset.Left);

        /// <summary>Authored seconds of the hero's None_1H motions used here (G3Cli anim motion).</summary>
        const double AttackHit = 0.32, AttackRecover = 0.44, PowerRaise = 0.76, PowerHit = 0.64, StumbleHit = 0.40,
                     StumbleRecover = 0.36, ParadeStumbleHit = 0.60, ParadeStumbleRecover = 0.36, ParadeRaise = 0.40,
                     DrawBegin = 0.20, DrawEnd = 0.56;

        static double Speed(CombatAction a, bool windUp = false) =>
            MotionSpeedRules.Factor(new MotionSpeedQuery { Motion = MotionSpeedRules.MotionOf(a), WindUp = windUp, IsPlayer = true, RightHand = WeaponClass.OneHanded, Stamina = 100 }, RuleOptions.Default);

        static double Speed(HitReaction r) =>
            MotionSpeedRules.Factor(new MotionSpeedQuery { Motion = MotionSpeedRules.MotionOf(r), IsPlayer = true, RightHand = WeaponClass.OneHanded, Stamina = 100 }, RuleOptions.Default);

        /// <summary>Frames from a motion's start to the frame its follower starts: the end is seen one frame late (G3 the same: one script tick).</summary>
        static int FramesOf(double seconds) => (int)System.Math.Ceiling(seconds / Dt - 1e-6);

        sealed class Hero
        {
            enum Mode { Free, Draw, Move, Recover, Parade, Reaction }

            sealed class Step
            {
                public string Stage;
                public double Length;
                public bool Hold;
            }

            readonly KeyMap _keys = KeyMap.Default();
            readonly MeleeInputInterpreter _input = new MeleeInputInterpreter(MeleeInputTiming.For(RuleOptions.Default));
            readonly List<Step> _steps = new List<Step>();
            readonly Dictionary<int, List<(KeyInput key, bool down)>> _events = new Dictionary<int, List<(KeyInput, bool)>>();
            Mode _mode;
            int _step;
            double _stepStart;
            bool _resume, _tail;
            double _endAt = -1;
            CombatAction _move;

            public int Frame { get; private set; }
            double Now => Frame * Dt;
            double Prev => (Frame - 1) * Dt;
            /// <summary>(frame, what started): "Attack", "PowerAttack", "Parade", "Stumble" …</summary>
            public readonly List<(int frame, string what)> Starts = new List<(int, string)>();
            public readonly List<string> Log = new List<string>();

            public Hero() => _input.Trace = Log.Add;

            public bool HoldsParade => _mode == Mode.Parade || _mode == Mode.Reaction && _resume;
            public bool InTail => _mode == Mode.Recover;
            public bool Reacting => _mode == Mode.Reaction;
            public MeleeInputInterpreter Input => _input;

            MeleeStance Stance => new MeleeStance
            {
                RightHand = WeaponClass.OneHanded, CanParade = true, Reacting = _mode == Mode.Reaction, Drawing = _mode == Mode.Draw,
                BodyOutOfParade = !HoldsParade,
            };

            public void At(int frame, KeyInput key, bool down)
            {
                if (!_events.TryGetValue(frame, out var list)) _events[frame] = list = new List<(KeyInput, bool)>();
                list.Add((key, down));
            }

            /// <summary>Starts the draw (G3 PS_Melee entry, two motions) as the weapon key does.</summary>
            public void Draw()
            {
                Begin(Mode.Draw, "Draw");
                _steps.Add(new Step { Stage = "Begin", Length = DrawBegin });
                _steps.Add(new Step { Stage = "End", Length = DrawEnd });
            }

            /// <summary>CombatFighter.ShowHit for the hero: ReactionStarting → InterruptMove → React.</summary>
            public void Hit(HitReaction reaction)
            {
                bool keep = ParadeHold.KeepsParade(HoldsParade, reaction);
                _input.HitTaken(Now, HitReactionRules.ResetsInput(reaction));
                if (_mode == Mode.Move) Apply(_input.MoveEnded(Now, Stance)); // InterruptMove
                bool parried = reaction == HitReaction.ParadeStumble || reaction == HitReaction.HeavyParadeStumble;
                double speed = Speed(reaction);
                Begin(Mode.Reaction, reaction.ToString());
                _steps.Add(new Step { Stage = "Hit", Length = (parried ? ParadeStumbleHit : StumbleHit) / speed });
                _steps.Add(new Step { Stage = "Recover", Length = (parried ? ParadeStumbleRecover : StumbleRecover) / speed });
                _resume = keep;
            }

            public void RunTo(int frame)
            {
                while (Frame < frame) Tick();
            }

            void Tick()
            {
                Frame++;
                // PlayerCombat.Update (-50): this frame's button events, then the interpreter's per-frame check.
                if (_events.TryGetValue(Frame, out var list))
                    foreach (var (key, down) in list)
                    {
                        var owner = _keys.Owner(key, out _);
                        if (owner == SessionKey.Use1) Apply(down ? _input.PrimaryPressed(Now, Stance) : _input.PrimaryReleased(Now, Stance));
                        else if (owner == SessionKey.Use2) Apply(down ? _input.SecondaryPressed(Now, Stance) : _input.SecondaryReleased(Now, Stance));
                    }
                Apply(_input.Update(Now, Stance));
                Animator();
            }

            /// <summary>G3CombatAnimator.Update (150): a step's end is seen when the player (200) reached it last frame.</summary>
            void Animator()
            {
                if (_mode == Mode.Free || _step >= _steps.Count) return;
                if (_mode == Mode.Move && _endAt >= 0 && Now >= _endAt)
                {
                    _endAt = -1;
                    _mode = Mode.Recover;
                    Apply(_input.MoveEnded(Now, Stance));
                    return;
                }
                var step = _steps[_step];
                if (step.Hold || _stepStart + step.Length > Prev + 1e-9) return;
                if (_step + 1 < _steps.Count)
                {
                    _step++;
                    _stepStart = Prev; // started at 150, advanced by the player's full frame at 200
                    var next = _steps[_step];
                    if (_mode == Mode.Move && _tail && next.Stage == "Recover")
                    {
                        if (MeleeComboRules.ShapeOf(_move).WaitAfterHit > 0) _endAt = Now + MeleeComboRules.ShapeOf(_move).WaitAfterHit;
                        else
                        {
                            _mode = Mode.Recover;
                            Apply(_input.MoveEnded(Now, Stance));
                        }
                    }
                    else if (_mode == Mode.Reaction && next.Stage == "Recover")
                    {
                        // ReactionHitOver: the Recover is a tail (a parried stumble goes on in the parade), then Freed.
                        _mode = _resume ? Mode.Parade : Mode.Recover;
                        if (_resume) _steps.Add(new Step { Stage = "Loop", Hold = true });
                        _resume = false;
                        Apply(_input.Update(Now, Stance));
                    }
                    return;
                }
                var ended = _mode;
                _mode = Mode.Free;
                _steps.Clear();
                if (ended == Mode.Move) Apply(_input.MoveEnded(Now, Stance));
                else if (ended == Mode.Draw || ended == Mode.Reaction) Apply(_input.Update(Now, Stance)); // Freed
            }

            void Begin(Mode mode, string what)
            {
                _steps.Clear();
                _step = 0;
                _mode = mode;
                _resume = _tail = false;
                _endAt = -1;
                _stepStart = Prev;
                Starts.Add((Frame, what));
            }

            void Apply(MeleeCommand c)
            {
                switch (c.Kind)
                {
                    case MeleeCommandKind.RaiseParade:
                        if (_mode == Mode.Parade) return;
                        Begin(Mode.Parade, "Parade");
                        _steps.Add(new Step { Stage = "Begin", Length = ParadeRaise });
                        _steps.Add(new Step { Stage = "Loop", Hold = true });
                        break;
                    case MeleeCommandKind.LowerParade:
                        if (_mode == Mode.Reaction) _resume = false;
                        else if (_mode == Mode.Parade) { _mode = Mode.Free; _steps.Clear(); }
                        break;
                    case MeleeCommandKind.StartMove:
                        StartMove(c.Move);
                        break;
                }
            }

            void StartMove(CombatAction move)
            {
                var shape = MeleeComboRules.ShapeOf(move);
                Begin(Mode.Move, move.ToString());
                _move = move;
                _tail = !shape.RecoverInMove;
                double speed = Speed(move);
                if (move == CombatAction.PowerAttack)
                {
                    _steps.Add(new Step { Stage = "Raise", Length = PowerRaise / Speed(move, windUp: true) });
                    _steps.Add(new Step { Stage = "Hit", Length = PowerHit / speed });
                }
                else _steps.Add(new Step { Stage = "Hit", Length = AttackHit / speed });
                _steps.Add(new Step { Stage = "Recover", Length = AttackRecover / speed });
            }

            public int StartOf(string what, int after = 0) => Starts.Find(s => s.what == what && s.frame > after).frame;
        }

        static void Tap(Hero h, KeyInput key, int frame, int frames = 3)
        {
            h.At(frame, key, true);
            h.At(frame + frames, key, false);
        }

        [Test]
        public void Parade_from_an_attack_Recover_tail_starts_at_the_50_ms_hold()
        {
            var h = new Hero();
            Tap(h, Lmb, 1);
            h.RunTo(4);
            int attack = h.StartOf("Attack");
            Assert.AreEqual(4, attack, "a tap starts on its release frame");
            int hitEnd = attack + FramesOf(AttackHit / Speed(CombatAction.Attack));
            h.RunTo(hitEnd + 2);
            Assert.IsTrue(h.InTail, "the Attack is over with its Hit, the Recover plays as a tail");
            int press = h.Frame + 1;
            h.At(press, Rmb, true);
            h.RunTo(press + 10);
            // G3: held > 50 ms → Parade queued; PS_Melee_Loop starts it at once, cutting the Recover tail.
            Assert.AreEqual(press + 4, h.StartOf("Parade"), "4 frames = the first frame with the button held > 50 ms");
            Assert.IsTrue(h.HoldsParade);
        }

        [Test]
        public void Parade_held_during_an_attack_Hit_waits_for_the_Hit_end_only()
        {
            var h = new Hero();
            Tap(h, Lmb, 1);
            h.At(8, Rmb, true); // during the Hit
            h.RunTo(60);
            int attack = h.StartOf("Attack");
            Assert.AreEqual(attack + FramesOf(AttackHit / Speed(CombatAction.Attack)), h.StartOf("Parade"),
                            "G3: the parade queued at 50 ms starts when the Hit ends (the Recover tail is cut), not after the Recover");
        }

        [Test]
        public void Parade_pressed_during_a_Hit_replaces_the_queued_combo_attack_FixFiftyFive()
        {
            // r30 / owner: "in the original the block cancels my attack". G3 OnPlayerSecondaryAction enqueues the parade
            // with FUN_10007940 (clear + add, one slot): the clicked follow-up attack is dropped, the running Hit is not cut.
            var h = new Hero();
            Tap(h, Lmb, 1);
            Tap(h, Lmb, 8);      // the next attack of the combo, queued during the first Hit
            h.At(14, Rmb, true); // the block, held on, still during the first Hit
            h.RunTo(90);
            int attack = h.StartOf("Attack");
            int hitEnd = attack + FramesOf(AttackHit / Speed(CombatAction.Attack));
            Assert.AreEqual(hitEnd, h.StartOf("Parade"), "the parade comes at the Hit end, in place of the queued attack");
            Assert.AreEqual(0, h.StartOf("Attack", attack), "the queued follow-up attack never starts");
            Assert.IsTrue(h.HoldsParade);
        }

        [Test]
        public void Parade_pressed_during_a_power_wind_up_starts_at_the_power_Hit_end()
        {
            var h = new Hero();
            h.At(1, Lmb, true); // held: PowerAttack at > 100 ms
            h.At(15, Rmb, true); // during the Raise, held on
            h.RunTo(150);
            int power = h.StartOf("PowerAttack");
            Assert.AreEqual(1 + 7, power, "the hold fires on the first frame past 100 ms");
            int raiseEnd = power + FramesOf(PowerRaise / Speed(CombatAction.PowerAttack, windUp: true));
            int hitEnd = raiseEnd + FramesOf(PowerHit / Speed(CombatAction.PowerAttack));
            // G3: OnPlayerSecondaryAction only queues; PS_Melee_PowerAttack runs Raise and Hit, then the loop starts the parade.
            Assert.AreEqual(hitEnd, h.StartOf("Parade"), "no cancel of the wind-up; the parade cuts the Recover tail");
        }

        [Test]
        public void Parade_comes_back_after_a_block_held_quick_attack_FixSeventyFive()
        {
            // G3 FUN_10002130: the parade raised in this press is queued again by the melee loop once the move is over.
            var h = new Hero();
            h.At(1, Rmb, true);  // held on
            Tap(h, Lmb, 20);     // block held + tap = QuickAttack
            h.RunTo(120);
            int first = h.StartOf("Parade");
            Assert.AreEqual(1 + 4, first, "input -> parade up: 4 frames (67 ms) from rest");
            int quick = h.StartOf("QuickAttack");
            Assert.AreEqual(23, quick, "the tap starts on its release");
            int back = h.StartOf("Parade", quick);
            var shape = MeleeComboRules.ShapeOf(CombatAction.QuickAttack);
            int end = quick + FramesOf(AttackHit / Speed(CombatAction.QuickAttack)) + FramesOf(shape.WaitAfterHit);
            Assert.That(back, Is.InRange(end - 1, end + 1), $"the block is up again when the quick attack's Hit (+wait) ends ({(back - quick) * Dt * 1000:0} ms)");
            Assert.IsTrue(h.HoldsParade, "and it stays up while the button is held");
        }

        [Test]
        public void Parade_latency_table_FixSeventyFive()
        {
            // Input -> parade-up, 1H hero, 60 fps (Docs/combat.md §9.7, fix-75): from rest 4 frames (67 ms); during an Attack's
            // Hit at its end (G3 cannot cut a running Hit); the queued click is replaced, never lost.
            var rest = new Hero();
            rest.At(1, Rmb, true);
            rest.RunTo(20);
            Assert.AreEqual(4, rest.StartOf("Parade") - 1);
            var hit = new Hero();
            Tap(hit, Lmb, 1);
            hit.At(6, Rmb, true);
            hit.RunTo(80);
            int attack = hit.StartOf("Attack");
            int hitEnd = attack + FramesOf(AttackHit / Speed(CombatAction.Attack));
            Assert.AreEqual(hitEnd, hit.StartOf("Parade"));
            Assert.LessOrEqual((hitEnd - 6) * Dt, 0.55, "RMB 2 frames into the Attack: parade up after <= 550 ms");
        }

        [Test]
        public void Next_attack_in_the_combo_window_starts_at_the_Hit_end()
        {
            var h = new Hero();
            Tap(h, Lmb, 1);
            Tap(h, Lmb, 12); // clicked during the first Hit
            h.RunTo(80);
            int first = h.StartOf("Attack");
            int second = h.StartOf("Attack", first);
            Assert.AreEqual(first + FramesOf(AttackHit / Speed(CombatAction.Attack)), second); // 1H: 533 ms = 32 frames
            Assert.AreEqual(32, second - first);
        }

        [Test]
        public void Parade_pressed_during_a_stumble_comes_when_the_stumbles_Hit_ends()
        {
            var h = new Hero();
            h.RunTo(10);
            h.Hit(HitReaction.Stumble);
            int hit = h.Frame;
            h.At(hit + 6, Rmb, true); // 100 ms into the stumble, held on
            h.RunTo(hit + 60);
            int hitOver = hit + FramesOf(StumbleHit / Speed(HitReaction.Stumble));
            int parade = h.StartOf("Parade");
            // G3: ZS_Stumble ignores the press; back in PS_Melee the held button (> 50 ms) queues the parade at once.
            Assert.AreEqual(hitOver, parade, "the parade starts the frame the stumble's Hit is over");
            Assert.Less(parade, hit + FramesOf((StumbleHit + StumbleRecover) / Speed(HitReaction.Stumble)),
                        "earlier than the end of the stumble's Recover (the old port's lock)");
        }

        [Test]
        public void Clicks_during_a_stumble_are_ignored_but_a_held_button_acts_after_it()
        {
            var h = new Hero();
            h.RunTo(10);
            h.Hit(HitReaction.Stumble);
            Tap(h, Lmb, 12);       // a tap inside the Hit: ignored (G3 task ZS_Stumble)
            h.At(14, Rmb, true);   // a quick RMB click inside the Hit: no quick attack
            h.At(16, Rmb, false);
            h.RunTo(60);
            Assert.IsFalse(h.Starts.Exists(s => s.what != "Stumble"), string.Join(", ", h.Starts));
            var g = new Hero();
            g.RunTo(10);
            g.Hit(HitReaction.Stumble);
            g.At(12, Lmb, true); // held through the stumble
            g.RunTo(60);
            Assert.AreEqual(10 + FramesOf(StumbleHit / Speed(HitReaction.Stumble)), g.StartOf("PowerAttack"),
                            "the hold (> 100 ms) fires when the stumble's Hit is over");
        }

        [Test]
        public void An_unparried_stumble_drops_the_queued_move_and_the_cut_move_never_restarts()
        {
            var h = new Hero();
            Tap(h, Lmb, 1);
            Tap(h, Lmb, 10); // queued behind the first Attack
            h.RunTo(16);
            Assert.AreEqual(CombatAction.Attack, h.Input.Queued);
            h.Hit(HitReaction.Stumble);
            h.RunTo(90);
            Assert.AreEqual(1, h.Starts.FindAll(s => s.what == "Attack").Count, "G3 ResetAll: the queued click is gone, " + string.Join(", ", h.Starts));
            Assert.IsFalse(h.Input.Busy);
        }

        [Test]
        public void A_parried_stumble_keeps_the_block_every_frame_and_a_tap_after_its_Hit_is_a_quick_attack()
        {
            var h = new Hero();
            h.At(1, Rmb, true);
            h.RunTo(20);
            Assert.IsTrue(h.HoldsParade);
            h.Hit(HitReaction.ParadeStumble);
            int hit = h.Frame;
            for (int f = hit + 1; f <= hit + 40; f++)
            {
                h.RunTo(f);
                Assert.IsTrue(h.HoldsParade, $"frame {f}: the block must hold through the parried stumble and after it");
            }
            Tap(h, Lmb, h.Frame + 1);
            h.RunTo(h.Frame + 6);
            Assert.AreEqual(h.Frame - 2, h.StartOf("QuickAttack"), "the block flag is set: a tap is a quick attack, at once");
        }

        // ------------------------------------------------------------------- fix-203: the block is a held state

        [Test]
        public void Block_held_through_an_unparried_stumble_comes_back_at_the_stumbles_Hit_end_FixTwoHundredThree()
        {
            var h = new Hero();
            h.At(1, Rmb, true);
            h.RunTo(20);
            Assert.IsTrue(h.HoldsParade);
            h.Hit(HitReaction.Stumble); // from behind / unparried: ResetAll lowers the parade, RMB stays down
            int hit = h.Frame;
            Assert.IsFalse(h.HoldsParade);
            h.RunTo(hit + 60);
            int hitOver = hit + FramesOf(StumbleHit / Speed(HitReaction.Stumble));
            Assert.AreEqual(hitOver, h.StartOf("Parade", hit), "held, not re-pressed: the parade comes as soon as the stumble allows");
            Assert.IsTrue(h.HoldsParade);
        }

        [Test]
        public void Block_released_before_the_stumbles_Hit_end_does_not_come_back_FixTwoHundredThree()
        {
            var h = new Hero();
            h.At(1, Rmb, true);
            h.RunTo(20);
            h.Hit(HitReaction.Stumble);
            int hit = h.Frame;
            h.At(hit + 5, Rmb, false);
            h.RunTo(hit + 60);
            Assert.AreEqual(0, h.StartOf("Parade", hit), string.Join(", ", h.Starts));
            Assert.IsFalse(h.HoldsParade);
        }

        [Test]
        public void Block_held_during_an_attack_that_a_stumble_cuts_comes_after_the_stumble_FixTwoHundredThree()
        {
            var h = new Hero();
            Tap(h, Lmb, 1);
            h.At(6, Rmb, true); // held during the swing: Parade queued
            h.RunTo(12);
            Assert.AreEqual(CombatAction.Parade, h.Input.Queued);
            h.Hit(HitReaction.QuickStumble); // ResetAll drops the queued Parade
            int hit = h.Frame;
            h.RunTo(hit + 60);
            Assert.AreEqual(hit + FramesOf(StumbleHit / Speed(HitReaction.QuickStumble)), h.StartOf("Parade", hit), string.Join(", ", h.Starts));
        }

        [Test]
        public void Block_held_when_a_tap_replaced_the_queued_parade_comes_after_the_tap_FixTwoHundredThree()
        {
            var h = new Hero();
            Tap(h, Lmb, 1);
            h.At(6, Rmb, true);
            Tap(h, Lmb, 12); // block flag set: a quick attack replaces the queued Parade
            h.RunTo(120);
            int quick = h.StartOf("QuickAttack");
            Assert.Greater(quick, 0, string.Join(", ", h.Starts));
            Assert.Greater(h.StartOf("Parade", quick), quick, "the held block comes after the quick attack");
            Assert.IsTrue(h.HoldsParade);
        }

        [Test]
        public void Held_power_attack_cut_by_a_stumble_fires_again_at_its_Hit_end_FixTwoHundredThree()
        {
            var h = new Hero();
            h.At(1, Lmb, true);
            h.RunTo(20); // PowerAttack wind-up runs
            Assert.AreEqual(CombatAction.PowerAttack, h.Input.Current);
            h.Hit(HitReaction.Stumble);
            int hit = h.Frame;
            h.RunTo(hit + 40);
            Assert.AreEqual(hit + FramesOf(StumbleHit / Speed(HitReaction.Stumble)), h.StartOf("PowerAttack", hit), string.Join(", ", h.Starts));

            var g = new Hero();
            g.At(1, Lmb, true);
            g.RunTo(20);
            g.Hit(HitReaction.Stumble);
            g.At(g.Frame + 5, Lmb, false); // let go during the stumble: nothing follows it
            g.RunTo(g.Frame + 40);
            Assert.AreEqual(1, g.Starts.FindAll(s => s.what == "PowerAttack").Count, string.Join(", ", g.Starts));
        }

        [Test]
        public void A_finished_power_attack_with_the_button_still_held_does_not_repeat_after_a_stumble_FixTwoHundredThree()
        {
            var input = new MeleeInputInterpreter();
            var s = new MeleeStance { RightHand = WeaponClass.OneHanded, CanParade = true };
            input.PrimaryPressed(0, s);
            Assert.AreEqual(MeleeCommand.Start(CombatAction.PowerAttack), input.Update(0.11, s));
            Assert.AreEqual(MeleeCommand.None, input.MoveEnded(1.5, s));
            input.HitTaken(2.0, true);
            Assert.AreEqual(MeleeCommand.None, input.Update(2.5, s), "the charge was spent: no new power attack");
        }

        [Test]
        public void Input_parade_without_a_body_parade_is_raised_again_FixTwoHundredThree()
        {
            // A hit during the raise resolved as parried (HeroInParade = body || input) keeps the input's parade, but the
            // body (not yet in the parade pose) ends the stumble standing: the held button must raise it again.
            var input = new MeleeInputInterpreter();
            var s = new MeleeStance { RightHand = WeaponClass.OneHanded, CanParade = true };
            input.SecondaryPressed(0, s);
            Assert.AreEqual(MeleeCommand.RaiseParade, input.Update(0.06, s));
            input.HitTaken(0.1, false);
            var reacting = s;
            reacting.Reacting = true;
            reacting.BodyOutOfParade = true;
            Assert.AreEqual(MeleeCommand.None, input.Update(0.2, reacting), "not during the reaction");
            Assert.IsTrue(input.InParade);
            var standing = s;
            standing.BodyOutOfParade = true;
            Assert.AreEqual(MeleeCommand.RaiseParade, input.Update(0.5, standing));
            Assert.AreEqual(MeleeCommand.None, input.Update(0.52, s), "raised once");
        }

        [Test]
        public void A_click_during_the_draw_waits_for_the_draw_end()
        {
            var h = new Hero();
            h.RunTo(1);
            h.Draw();
            Tap(h, Lmb, 4); // the hand has not switched yet (1H Begin 0.20 s)
            h.RunTo(80);
            int drawEnd = 1 + FramesOf(DrawBegin) + FramesOf(DrawEnd);
            Assert.AreEqual(drawEnd, h.StartOf("Attack"), "G3: queued in PS_Melee, started by the loop after _AI_HoldInventoryItems");
        }

        [Test]
        public void A_refused_move_does_not_leave_the_input_busy()
        {
            var input = new MeleeInputInterpreter();
            var s = new MeleeStance { RightHand = WeaponClass.OneHanded, CanParade = true };
            input.PrimaryPressed(0, s);
            var c = input.PrimaryReleased(0.05, s);
            Assert.AreEqual(MeleeCommand.Start(CombatAction.Attack), c);
            input.Refused(c); // PlayerCombat.Feed: !CanFight (weapon switch, control lock)
            Assert.IsFalse(input.Busy);
            input.PrimaryPressed(0.3, s);
            Assert.AreEqual(MeleeCommand.Start(CombatAction.Attack), input.PrimaryReleased(0.35, s), "the next click starts at once");
        }

        [Test]
        public void The_release_after_a_get_up_parade_press_lowers_the_parade()
        {
            var input = new MeleeInputInterpreter();
            var sitting = new MeleeStance { RightHand = WeaponClass.OneHanded, CanParade = true, SittingKnockedDown = true };
            var s = new MeleeStance { RightHand = WeaponClass.OneHanded, CanParade = true };
            Assert.AreEqual(MeleeCommand.Start(CombatAction.GetUpParade), input.SecondaryPressed(0, sitting));
            Assert.AreEqual(MeleeCommand.None, input.MoveEnded(0.5, s));
            Assert.AreEqual(MeleeCommand.RaiseParade, input.Update(0.52, s), "still held after the get-up: the parade (G3: > 50 ms in PS_Melee)");
            Assert.AreEqual(MeleeCommand.LowerParade, input.SecondaryReleased(1.0, s), "the release lowers it (it used to stay up)");
            Assert.IsFalse(input.InParade);
        }

        [Test]
        public void Lost_buttons_lower_the_parade()
        {
            var input = new MeleeInputInterpreter();
            var s = new MeleeStance { RightHand = WeaponClass.OneHanded, CanParade = true };
            input.SecondaryPressed(0, s);
            Assert.AreEqual(MeleeCommand.RaiseParade, input.Update(0.06, s));
            Assert.AreEqual(MeleeCommand.LowerParade, input.ButtonsLost(0.5));
            Assert.IsFalse(input.BlockHeld);
            Assert.AreEqual(MeleeCommand.None, input.SecondaryReleased(0.9, s), "the release that came under the lock is gone");
        }

        [Test]
        public void Aai_drop_is_held_off_only_by_a_fired_primary_hold()
        {
            var s = new MeleeStance { RightHand = WeaponClass.OneHanded, CanParade = true };
            var cp = new MeleeInputTiming { ParadeAutoDrop = MeleeInputTiming.CpAaiParadeDrop }; // the CP mechanics (not used by the game, fix-68)
            var input = new MeleeInputInterpreter(cp);
            input.SecondaryPressed(0, s);
            Assert.AreEqual(MeleeCommand.RaiseParade, input.Update(0.06, s));
            input.PrimaryPressed(2.45, s); // a short LMB press (no hold fired yet) does not stop the AAI drop (G3 DAT_10118b40)
            Assert.AreEqual(MeleeCommand.LowerParade, input.Update(2.51, s));

            input = new MeleeInputInterpreter(cp);
            input.SecondaryPressed(0, s);
            input.Update(0.06, s);
            input.PrimaryPressed(1.0, s);
            Assert.AreEqual(MeleeCommand.Start(CombatAction.PierceAttack), input.Update(1.11, s), "block held + LMB hold → Pierce");
            input.MoveEnded(2.0, s);
            Assert.AreEqual(MeleeCommand.None, input.Update(2.6, s), "no AAI drop while the primary hold has fired and the button is down");
        }
    
        [Test]
        public void FrozenBody_ShowsNoHitReaction_FixFortyEight()
        {
            // r29 wolfclan_01: an IceGolem froze the hero (time scale 0) and his StumbleL stood until the 10 s safety end
            Assert.That(HitReactionRules.ShowsReaction(frozen: false, freezeSeconds: 0f), Is.True);
            Assert.That(HitReactionRules.ShowsReaction(frozen: true, freezeSeconds: 0f), Is.False, "already frozen");
            Assert.That(HitReactionRules.ShowsReaction(frozen: false, freezeSeconds: 5f), Is.False, "this hit freezes");
        }

        [Test]
        public void Fix209_StumbleCutsAHeldBowAim_TheIntentSurvives_AReleaseRaisesAgainAndShoots()
        {
            Assert.That(HitReactionRules.KeepsAimIntent(HitReaction.Stumble, shootHeld: true, nocked: true), Is.True);
            Assert.That(HitReactionRules.KeepsAimIntent(HitReaction.QuickStumble, shootHeld: true, nocked: true), Is.True);
            Assert.That(HitReactionRules.KeepsAimIntent(HitReaction.Stumble, shootHeld: false, nocked: true), Is.False, "not held");
            Assert.That(HitReactionRules.KeepsAimIntent(HitReaction.Stumble, shootHeld: true, nocked: false), Is.False, "no arrow");
            Assert.That(HitReactionRules.KeepsAimIntent(HitReaction.ParadeStumble, shootHeld: true, nocked: true), Is.False, "no ResetAll");

            Assert.That(HitReactionRules.OnReleaseWithoutShot(aimQueued: false, aimCutByHit: true, nocked: true), Is.EqualTo(RangedReleaseQueue.AimAndShot));
            Assert.That(HitReactionRules.OnReleaseWithoutShot(aimQueued: true, aimCutByHit: false, nocked: true), Is.EqualTo(RangedReleaseQueue.Shot));
            Assert.That(HitReactionRules.OnReleaseWithoutShot(aimQueued: false, aimCutByHit: false, nocked: true), Is.EqualTo(RangedReleaseQueue.None));
            Assert.That(HitReactionRules.OnReleaseWithoutShot(aimQueued: false, aimCutByHit: true, nocked: false), Is.EqualTo(RangedReleaseQueue.None));
        }
}
}
