using System.Collections.Generic;
using G3.Player;
using Myrtana.Core;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// The block on the right mouse button end to end (fix-43): physical button events through the default key map
    /// (Use2 = right mouse button) into the real <see cref="MeleeInputInterpreter"/>, frame by frame, its commands applied to
    /// a body that keeps the parade the way G3CombatAnimator does (raise, hold, lower, moves, stumbles; a parried stumble
    /// keeps the block: <see cref="ParadeHold"/>), and hits checked by the real <see cref="CombatResolver"/>.
    /// </summary>
    public class ParadeHoldTests
    {
        const double Frame = 1.0 / 60;
        static readonly KeyInput Rmb = KeyInput.Mouse(MouseOffset.Right), Lmb = KeyInput.Mouse(MouseOffset.Left);

        /// <summary>The hero's side: key map, interpreter and a body with G3CombatAnimator's parade modes.</summary>
        sealed class Hero
        {
            enum Body { Free, Parade, Move, Stumble }

            const double MoveSeconds = 0.5, StumbleSeconds = 0.4;
            readonly KeyMap _keys = KeyMap.Default();
            readonly MeleeInputInterpreter _input;
            readonly RuleOptions _options;
            MeleeStance _stance = new MeleeStance { RightHand = WeaponClass.OneHanded, CanParade = true };
            Body _body;
            bool _resumeParade;
            double _until;

            public Hero(RuleOptions options = null)
            {
                _options = options ?? RuleOptions.Default;
                _input = new MeleeInputInterpreter(MeleeInputTiming.For(_options));
            }

            public double Now { get; private set; }
            public WeaponClass Weapon { get => _stance.RightHand; set => _stance.RightHand = value; }
            public bool Shield { get; set; }
            public readonly List<CombatAction> Started = new List<CombatAction>();

            /// <summary>G3CombatAnimator.HoldsParade: the parade is held or a parried stumble plays.</summary>
            public bool HoldsParade => _body == Body.Parade || _body == Body.Stumble && _resumeParade;
            public bool Stumbling => _body == Body.Stumble;
            public bool BlockHeld => _input.BlockHeld;

            public void Press(KeyInput button) => Route(button, true);
            public void Release(KeyInput button) => Route(button, false);

            void Route(KeyInput button, bool down)
            {
                var key = _keys.Owner(button, out _);
                if (key == SessionKey.Use1) Apply(down ? _input.PrimaryPressed(Now, _stance) : _input.PrimaryReleased(Now, _stance));
                else if (key == SessionKey.Use2) Apply(down ? _input.SecondaryPressed(Now, _stance) : _input.SecondaryReleased(Now, _stance));
            }

            /// <summary>One frame: PlayerCombat.Update feeds the interpreter, the body plays on.</summary>
            public void Tick()
            {
                Now += Frame;
                if ((_body == Body.Move || _body == Body.Stumble) && Now >= _until)
                {
                    bool move = _body == Body.Move;
                    _body = _body == Body.Stumble && _resumeParade ? Body.Parade : Body.Free; // ResumeParade
                    _resumeParade = false;
                    if (move) Apply(_input.MoveEnded(Now, _stance));
                }
                Apply(_input.Update(Now, _stance));
            }

            /// <summary>Frames until <paramref name="time"/>; <paramref name="each"/> checks every one of them.</summary>
            public void RunTo(double time, System.Action each = null)
            {
                while (Now + Frame <= time + 1e-9)
                {
                    Tick();
                    each?.Invoke();
                }
            }

            void Apply(MeleeCommand c)
            {
                switch (c.Kind)
                {
                    case MeleeCommandKind.RaiseParade:
                        _body = Body.Parade;
                        break;
                    case MeleeCommandKind.LowerParade:
                        if (_body == Body.Stumble) _resumeParade = false;
                        else if (_body == Body.Parade) _body = Body.Free;
                        break;
                    case MeleeCommandKind.StartMove:
                        Started.Add(c.Move);
                        _body = Body.Move;
                        _resumeParade = false;
                        _until = Now + MoveSeconds;
                        break;
                }
            }

            /// <summary>CombatPresenter.Snapshot of the hero as the victim.</summary>
            public Combatant Snapshot() => new Combatant
            {
                Id = ActorId.Player, IsPlayer = true, Level = 10, Strength = 100, Dexterity = 100,
                Weapon = Weapon, Shield = Shield, Action = CombatAction.None, InParade = HoldsParade,
                HitPoints = 200, Stamina = 150, Mana = 100, Perks = new HashSet<string>(),
            };

            /// <summary>The resolved hit shown on the body (CombatFighter.ShowHit).</summary>
            public void Show(HitOutcome o)
            {
                if (o.Reaction == HitReaction.None) return;
                bool keep = ParadeHold.KeepsParade(HoldsParade, o.Reaction);
                if (_body == Body.Move) Apply(_input.MoveEnded(Now, _stance)); // InterruptMove
                _body = Body.Stumble;
                _resumeParade = keep;
                _until = Now + StumbleSeconds;
            }
        }

        static Combatant Orc(WeaponClass weapon = WeaponClass.OneHanded, CombatAction action = CombatAction.Attack) => new Combatant
        {
            Id = new ActorId("Orc_Warrior"), Species = "Human", Humanoid = true, Level = 0, Strength = 40,
            Protection = DamageTypeValues.All(CombatStats.NpcProtection(20)), Weapon = weapon, Action = action,
            HitPoints = 400, Stamina = 100, Mana = 1000,
        };

        static HitOutcome Hit(Hero hero, bool seen = true, WeaponClass weapon = WeaponClass.OneHanded, RuleOptions options = null)
        {
            var hit = new HitContext
            {
                Attacker = Orc(weapon), Victim = hero.Snapshot(), Delivery = HitDelivery.Melee, DamageType = DamageType.Blade,
                DamageAmount = 50, PlayerLevel = 10, VictimSeesAttacker = seen, AttackerSeesVictim = true,
            };
            var o = new CombatResolver(TestRules.Combat(), options ?? RuleOptions.Default, new SeededRandom(7)).Resolve(hit);
            hero.Show(o);
            return o;
        }

        [Test]
        public void DefaultKeys_RightMouseButtonIsUse2()
        {
            Assert.That(KeyMap.Default().Owner(Rmb, out _), Is.EqualTo(SessionKey.Use2));
            Assert.That(KeyMap.Default().Owner(Lmb, out _), Is.EqualTo(SessionKey.Use1));
        }

        [TestCase(WeaponClass.OneHanded, false)]
        [TestCase(WeaponClass.OneHanded, true)]
        [TestCase(WeaponClass.TwoHanded, false)]
        [TestCase(WeaponClass.Staff, false)]
        [TestCase(WeaponClass.Fist, false)]
        public void HoldRmbTwoSeconds_ParadeUpTheWholeTime_ReleaseLowersIt(WeaponClass weapon, bool shield)
        {
            var hero = new Hero { Weapon = weapon, Shield = shield };
            hero.Press(Rmb);
            hero.RunTo(0.04);
            Assert.That(hero.HoldsParade, Is.False, "not before the 50 ms hold (G3 DurationPressedMSecs > 50)");
            hero.RunTo(0.07);
            Assert.That(hero.HoldsParade, Is.True, "raised once held > 50 ms");
            hero.RunTo(2.0, () => Assert.That(hero.HoldsParade, Is.True, $"parade dropped at {hero.Now:0.000} s while RMB held"));
            hero.Release(Rmb);
            hero.Tick();
            Assert.That(hero.HoldsParade, Is.False, "release lowers it");
            Assert.That(hero.Started, Is.Empty, "a long hold is no quick attack");
        }

        [Test]
        public void NextPress_RaisesTheParadeAgain()
        {
            var hero = new Hero();
            for (int i = 0; i < 3; i++)
            {
                hero.Press(Rmb);
                hero.RunTo(hero.Now + 0.5);
                Assert.That(hero.HoldsParade, Is.True, $"press {i + 1}");
                hero.Release(Rmb);
                hero.RunTo(hero.Now + 0.3);
                Assert.That(hero.HoldsParade, Is.False, $"release {i + 1}");
            }
        }

        [Test]
        public void HitsWhileHoldingRmb_AreAllBlocked_TheParadeOutlastsEachStumble()
        {
            var hero = new Hero();
            hero.Press(Rmb);
            hero.RunTo(0.3);
            for (int i = 0; i < 3; i++)
            {
                var o = Hit(hero);
                Assert.That(o.Result, Is.EqualTo(HitResult.Blocked), $"hit {i + 1}");
                Assert.That(o.Reaction == HitReaction.ParadeStumble || o.Reaction == HitReaction.HeavyParadeStumble, Is.True, o.Reaction.ToString());
                Assert.That(o.Damage, Is.EqualTo(0));
                Assert.That(o.StaminaCost, Is.GreaterThan(0), "the block costs stamina");
                hero.RunTo(hero.Now + 0.1);
                Assert.That(hero.Stumbling && hero.HoldsParade, Is.True, "the parried stumble still blocks (G3 ani state Parade)");
                Assert.That(Hit(hero).Result, Is.EqualTo(HitResult.Blocked), "a hit during the stumble");
                hero.RunTo(hero.Now + 0.6);
                Assert.That(hero.Stumbling, Is.False);
                Assert.That(hero.HoldsParade, Is.True, $"block back up after stumble {i + 1}");
            }
            hero.Release(Rmb);
            hero.Tick();
            Assert.That(Hit(hero).Result, Is.Not.EqualTo(HitResult.Blocked), "released: the hit lands");
        }

        [Test]
        public void ReleasedDuringTheStumble_TheParadeDoesNotComeBack()
        {
            var hero = new Hero();
            hero.Press(Rmb);
            hero.RunTo(0.3);
            Hit(hero);
            hero.RunTo(hero.Now + 0.1);
            hero.Release(Rmb);
            hero.RunTo(hero.Now + 0.6);
            Assert.That(hero.HoldsParade, Is.False);
            Assert.That(hero.Started, Is.Empty, "held > 200 ms: no riposte");
        }

        [Test]
        public void UnparriedHit_EndsTheParade_NoReRaiseWhileHeld()
        {
            var hero = new Hero();
            hero.Press(Rmb);
            hero.RunTo(0.3);
            var o = Hit(hero, seen: false); // from behind: G3 CanParade needs the attacker in view
            Assert.That(o.Result, Is.Not.EqualTo(HitResult.Blocked));
            hero.RunTo(1.5);
            Assert.That(hero.HoldsParade, Is.False, "G3: the block flag stays set until the release, no second parade per press");
            hero.Release(Rmb);
            hero.Tick();
            hero.Press(Rmb);
            hero.RunTo(hero.Now + 0.2);
            Assert.That(hero.HoldsParade, Is.True, "a new press raises it");
        }

        [Test]
        public void HeldAsLongAsTheButtonIsDown_UnderEveryRuleOption_ReleaseLowersIt()
        {
            // fix-68 [owner]: no CP AAI 2.5 s drop; the block stays up while the button is held, the release lowers it.
            foreach (var options in new[] { RuleOptions.Default, new RuleOptions(alternativeBalancing: false, extendedAI: false),
                                            new RuleOptions(alternativeBalancing: true, extendedAI: true) })
            {
                var hero = new Hero(options);
                hero.Press(Rmb);
                hero.RunTo(60, () => Assert.That(hero.Now < 0.06 || hero.HoldsParade, Is.True, $"down at {hero.Now:0.000} (AAI {options.ExtendedAI})"));
                hero.Release(Rmb);
                hero.Tick();
                Assert.That(hero.HoldsParade, Is.False, "the release lowers it");
            }
        }

        [Test]
        public void LmbTapWhileHoldingRmb_QuickAttack_ThenTheParadeComesBack()
        {
            var hero = new Hero();
            hero.Press(Rmb);
            hero.RunTo(0.3);
            Assert.That(hero.BlockHeld, Is.True);
            hero.Press(Lmb);
            hero.Tick();
            hero.Release(Lmb);
            Assert.That(hero.Started, Is.EqualTo(new[] { CombatAction.QuickAttack }), "block held + tap = QuickAttack (fix-31)");
            Assert.That(hero.HoldsParade, Is.False);
            hero.RunTo(1.5);
            Assert.That(hero.HoldsParade, Is.True, "G3 FUN_10002130: the block raised in this press comes back after the move (fix-75)");
            hero.Release(Rmb);
            hero.Tick();
            Assert.That(hero.HoldsParade, Is.False);
        }

        [Test]
        public void QuickRmbRelease_InTheParade_Ripostes()
        {
            var hero = new Hero();
            hero.Press(Rmb);
            hero.RunTo(0.15);
            Assert.That(hero.HoldsParade, Is.True);
            hero.Release(Rmb);
            Assert.That(hero.Started, Is.EqualTo(new[] { CombatAction.QuickAttack }), "released < 200 ms in PS_Melee_Parade");
        }

        [TestCase(true, HitReaction.ParadeStumble, true)]
        [TestCase(true, HitReaction.HeavyParadeStumble, true)]
        [TestCase(true, HitReaction.Stumble, false)]
        [TestCase(true, HitReaction.SitKnockDown, false)]
        [TestCase(false, HitReaction.ParadeStumble, false)]
        public void KeepsParade_OnlyAParriedStumbleInTheParade(bool inParade, HitReaction reaction, bool keeps) =>
            Assert.That(ParadeHold.KeepsParade(inParade, reaction), Is.EqualTo(keeps));
    }
}
