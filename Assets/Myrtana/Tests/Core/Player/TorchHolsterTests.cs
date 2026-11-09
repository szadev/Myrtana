using System.Collections.Generic;
using G3.Player;
using Myrtana.Core;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-252 (owner: torch in hand, then a weapon with a shield; after a strike key 1 sometimes left it in the hands):
    /// the hero's weapon hand (<see cref="WeaponHand"/>), the one-slot weapon key queue (<see cref="WeaponSwitchQueue{T}"/>)
    /// and the torch (<see cref="HeroTorch"/>, <see cref="TorchHolsterRules"/>) stepped frame by frame as PlayerCombat,
    /// G3CombatAnimator and HeroTorchView run them. The torch's take overlay shares the upper-body layer with the sheathe's
    /// overlay variant (used while the body still moves, as after a strike): started over it, it cut the sheathe short.
    /// </summary>
    public class TorchHolsterTests
    {
        const string TorchItem = "It_Torch_Burning";

        sealed class Hero
        {
            public readonly WeaponHand Hand = new WeaponHand();
            public readonly WeaponSwitchQueue<string> Queue = new WeaponSwitchQueue<string>();
            public readonly HeroTorch Torch = new HeroTorch();
            public readonly Inventory Inventory = new Inventory();
            public readonly List<string> Log = new List<string>();
            /// <summary>Reproduces the code before fix-252: the torch comes back on the sheathe's first frame.</summary>
            public bool Legacy;
            /// <summary>The body still slides after a strike: the sheathe uses its overlay variant.</summary>
            public bool Moving = true;
            public int AttackFrames, ParadeHeld, StumbleFrames;
            /// <summary>The torch overlay started while the sheathe ran (the bug's mechanism).</summary>
            public bool TorchTakeOverSheathe;
            int _holdFrame = -1; // frame within the running draw / sheathe (2 frames per motion)
            bool _sheatheOverlay, _wasDrawn, _holsterPending, _torchOverlay;
            int _torchFrames;

            public Hero()
            {
                var torchDef = new ItemDef(TorchItem, new TextId("FO_Torch"), default, ItemCategory.Misc, "Torch", EquipSlot.None, 0, 0, null, null,
                                           null, null, null, null, null, null, default);
                Inventory.Add(TorchItem, 1);
                Torch.Bind(Inventory, id => id == TorchItem ? torchDef : null, new EventBus(), null);
                Assert.That(Torch.Take(TorchItem), Is.EqualTo(TorchTakeResult.Taken));
            }

            public bool Busy => AttackFrames > 0 || ParadeHeld > 0 || StumbleFrames > 0 || !Hand.Settled;

            WeaponSwitchState State() => new WeaponSwitchState { MoveRuns = Busy };

            /// <summary>Key 1: the quick slot of the equipped 1H weapon (draw or holster, PlayerCombat.ReadyNow).</summary>
            public void Press1()
            {
                if (Queue.Offer("1", State(), 0) == WeaponSwitchVerdict.Wait) Log.Add("1 waits");
                else Act();
            }

            void Act()
            {
                if (Hand.Drawn)
                {
                    Assert.That(Hand.BeginSheathe(), Is.True);
                    _sheatheOverlay = Moving;
                    Log.Add("sheathe");
                }
                else
                {
                    Assert.That(Hand.BeginDraw("1H", "It_Sword"), Is.True);
                    Log.Add("draw");
                }
                _holdFrame = 0;
            }

            public void Frame()
            {
                // G3CombatAnimator: two motions around HoldStacks (2 frames each)
                if (!Hand.Settled)
                {
                    _holdFrame++;
                    if (_holdFrame == 2) Hand.SwitchHands();
                    if (_holdFrame >= 4)
                    {
                        Hand.Finish();
                        _holdFrame = -1;
                    }
                }
                if (AttackFrames > 0) AttackFrames--;
                if (StumbleFrames > 0) StumbleFrames--;
                // PlayerCombat.RunWaitingSwitch
                if (Queue.Poll(State(), out _) != null) Act();
                TorchView();
            }

            /// <summary>HeroTorchView.Update.</summary>
            void TorchView()
            {
                bool drawn = Hand.Drawn;
                if (drawn && !_torchOverlay && (Torch.InHand || !_wasDrawn) && Torch.OnWeaponDrawn("1H", leftHandBusy: true))
                    Log.Add("torch released (shield into the left hand)");
                if (!drawn && _wasDrawn)
                {
                    if (Legacy)
                    {
                        if (Torch.OnWeaponHolstered("1H")) StartTorchTake();
                    }
                    else _holsterPending = true;
                }
                _wasDrawn = drawn;
                if (_holsterPending)
                {
                    var step = TorchHolsterRules.Decide(Hand.Drawn, Hand.InHand, Hand.Settled, Busy && Hand.Settled);
                    if (step != TorchHolsterStep.Wait)
                    {
                        _holsterPending = false;
                        if (step == TorchHolsterStep.Take && Torch.OnWeaponHolstered("1H")) StartTorchTake();
                    }
                }
                if (_torchOverlay && --_torchFrames <= 0) _torchOverlay = false;
            }

            void StartTorchTake()
            {
                _torchOverlay = true;
                _torchFrames = 4;
                Log.Add("torch take");
                if (Hand.Settled) return;
                TorchTakeOverSheathe = true;
                // G3CombatAnimator.UpdateHold: "lost its overlay ... cut short", the hand keeps what it holds
                if (_sheatheOverlay)
                {
                    Hand.Interrupt();
                    _holdFrame = -1;
                    Log.Add("sheathe cut short by the torch overlay");
                }
            }

            public void Run(int frames)
            {
                for (int i = 0; i < frames; i++) Frame();
            }

            public void Settle() => Run(30);
        }

        static Hero DrawnWithShield(bool legacy = false)
        {
            var h = new Hero { Legacy = legacy };
            h.Press1();
            h.Settle();
            Assert.That(h.Hand.Drawn && h.Hand.InHand, Is.True, "drawn");
            Assert.That(h.Torch.InHand, Is.False, "the shield took the left hand");
            Assert.That(h.Torch.HeldBeforeDraw, Is.EqualTo(TorchItem));
            return h;
        }

        static void AssertSheathedWithTorch(Hero h)
        {
            Assert.That(h.Hand.InHand, Is.False, "the weapon is put away: " + string.Join(", ", h.Log));
            Assert.That(h.Hand.Drawn, Is.False);
            Assert.That(h.Torch.InHand, Is.True, "the torch is back in the left hand");
            Assert.That(h.TorchTakeOverSheathe, Is.False, "the torch take never runs over the sheathe");
        }

        [Test]
        public void Legacy_KeyAfterAStrike_LeftTheWeaponInTheHand()
        {
            var h = DrawnWithShield(legacy: true);
            h.AttackFrames = 3;
            h.Press1();
            h.Settle();
            Assert.That(h.TorchTakeOverSheathe, Is.True);
            Assert.That(h.Hand.InHand && h.Hand.Drawn, Is.True, "the owner's bug: still in the hands");
            Assert.That(h.Torch.InHand, Is.False, "and the torch released again");
        }

        [Test]
        public void KeyDuringTheStrike_WaitsAndPutsTheWeaponAway()
        {
            var h = DrawnWithShield();
            h.AttackFrames = 3;
            h.Press1();
            Assert.That(h.Queue.Waiting, Is.EqualTo("1"), "queued while the attack runs, not dropped");
            Assert.That(h.Hand.InHand, Is.True);
            h.Settle();
            AssertSheathedWithTorch(h);
        }

        [Test]
        public void KeyInTheRecoverWindow_PutsTheWeaponAway()
        {
            var h = DrawnWithShield();
            h.AttackFrames = 2;
            h.Run(2); // the Hit is over, the Recover tail plays (not Busy)
            h.Press1();
            Assert.That(h.Log, Does.Contain("sheathe"));
            h.Settle();
            AssertSheathedWithTorch(h);
        }

        [Test]
        public void KeyWhileStanding_AlsoClean()
        {
            var h = DrawnWithShield();
            h.Moving = false;
            h.Press1();
            h.Settle();
            AssertSheathedWithTorch(h);
        }

        [Test]
        public void KeyDuringTheTorchToShieldSwap_ActsAfterTheDraw()
        {
            var h = new Hero();
            h.Press1();
            h.Frame(); // the draw runs: the torch went, the shield comes
            Assert.That(h.Torch.InHand, Is.False);
            h.Press1();
            Assert.That(h.Queue.Waiting, Is.EqualTo("1"));
            h.Settle();
            AssertSheathedWithTorch(h);
        }

        [Test]
        public void KeyWhileBlocking_ActsWhenTheBlockEnds()
        {
            var h = DrawnWithShield();
            h.ParadeHeld = 1;
            h.Press1();
            h.Run(10);
            Assert.That(h.Hand.InHand, Is.True, "the held parade keeps the key waiting (G3: no loop state)");
            Assert.That(h.Queue.Waiting, Is.EqualTo("1"));
            h.ParadeHeld = 0;
            h.Settle();
            AssertSheathedWithTorch(h);
        }

        [Test]
        public void KeyTwiceQuickly_PutsAwayThenDrawsAgain_TorchFollows()
        {
            var h = DrawnWithShield();
            h.AttackFrames = 2;
            h.Press1();
            h.Run(3);
            h.Press1(); // during the sheathe: waits, then draws again
            h.Settle();
            Assert.That(h.Hand.InHand && h.Hand.Drawn, Is.True, "drawn again: " + string.Join(", ", h.Log));
            Assert.That(h.Torch.InHand, Is.False);
            Assert.That(h.Torch.HeldBeforeDraw, Is.EqualTo(TorchItem), "the torch is still remembered for the next holster");
            Assert.That(h.TorchTakeOverSheathe, Is.False);
            h.Press1();
            h.Settle();
            AssertSheathedWithTorch(h);
        }

        [Test]
        public void StumbleAfterTheHandChange_TorchWaitsForTheBody()
        {
            var h = DrawnWithShield();
            h.Press1();
            h.Run(2); // the weapon left the hand (HoldStacks)
            Assert.That(h.Hand.InHand, Is.False);
            h.Hand.Interrupt(); // a stumble cuts the rest of the sheathe
            h.StumbleFrames = 5;
            h.Run(2);
            Assert.That(h.Torch.InHand, Is.False, "not over the stumble");
            h.Settle();
            AssertSheathedWithTorch(h);
        }

        [Test]
        public void Rules()
        {
            Assert.That(TorchHolsterRules.Decide(drawn: false, weaponInHand: false, handSettled: true, bodyBusy: true), Is.EqualTo(TorchHolsterStep.Wait));
            Assert.That(TorchHolsterRules.Decide(false, false, true, false), Is.EqualTo(TorchHolsterStep.Take));
            Assert.That(TorchHolsterRules.Decide(false, true, false, true), Is.EqualTo(TorchHolsterStep.Wait), "the sheathe runs");
            Assert.That(TorchHolsterRules.Decide(true, true, true, false), Is.EqualTo(TorchHolsterStep.Cancel), "cut short: the weapon is back");
            Assert.That(TorchHolsterRules.TorchKeyWaits(handSettled: false), Is.True);
            Assert.That(TorchHolsterRules.TorchKeyWaits(handSettled: true), Is.False);
        }
    }
}
