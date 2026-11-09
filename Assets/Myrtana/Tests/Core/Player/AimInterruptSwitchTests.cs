using G3.Player;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-197 (owner: "aiming the bow, an animal bites the hero, then he can neither switch weapons nor sheathe"): a
    /// stumble, fall or another owner cut the put-away of a weapon switch before the weapon left the hand; the draw that
    /// waited for empty hands never ran, and as "a switch runs" every later weapon or quick-slot key waited behind it.
    /// Now the cut switch is dropped (<see cref="PendingDrawRules"/>, G3: the ZS_* task replaced PS_*), and the keys act
    /// again in the loop state. The same flow holds for the crossbow and the spell hand.
    /// </summary>
    public class AimInterruptSwitchTests
    {
        /// <summary>The player's switch flow (PlayerCombat: OfferSwitch, RunPendingDraw, RunWaitingSwitch) on the pure rules.</summary>
        sealed class Hero
        {
            public readonly WeaponSwitchQueue<string> Keys = new WeaponSwitchQueue<string>();
            public bool PendingDraw, Busy, Drawn = true, AimHeld, Reacting;
            public string InHand = "bow", Drew;

            WeaponSwitchState State => new WeaponSwitchState { MoveRuns = Busy, SwitchRuns = PendingDraw, AimHeld = AimHeld };

            /// <summary>A quick-slot key: false = it waits.</summary>
            public bool Key(string item)
            {
                var verdict = Keys.Offer(item, State, 0);
                if (verdict == WeaponSwitchVerdict.Wait) return false;
                Act(verdict, item);
                return true;
            }

            void Act(WeaponSwitchVerdict verdict, string item)
            {
                if (verdict == WeaponSwitchVerdict.EndAimThenNow) AimHeld = false;
                if (item == InHand) { StartSheathe(); return; }
                StartSheathe();
                PendingDraw = true;
                _want = item;
            }

            string _want;

            void StartSheathe()
            {
                AimHeld = false;
                Busy = true; // Mode.Sheathe
            }

            /// <summary>The put-away ends by itself: the hands are empty.</summary>
            public void SheatheEnds()
            {
                Busy = false;
                Drawn = false;
                InHand = null;
                Frame();
            }

            /// <summary>A stumble starts (Begin(Reaction) cuts the sheathe: WeaponHand.Interrupt keeps the weapon), ResetAll clears the queue.</summary>
            public void Bitten(bool resetAll = true)
            {
                AimHeld = false;
                Busy = true;
                Reacting = true;
                if (resetAll) Keys.Clear();
            }

            /// <summary>The reaction's Hit is over: the Recover tail is a loop state (not Busy).</summary>
            public void ReactionOver()
            {
                Busy = false;
                Reacting = false;
                Frame();
            }

            public void Frame()
            {
                if (PendingDraw)
                {
                    var step = PendingDrawRules.Decide(Busy, Drawn);
                    if (step != PendingDrawStep.Wait) PendingDraw = false;
                    if (step == PendingDrawStep.Draw)
                    {
                        Drew = _want;
                        InHand = _want;
                        Drawn = true;
                        Busy = true; // the draw
                    }
                }
                var key = Keys.Poll(State, out var verdict);
                if (key != null) Act(verdict, key);
            }
        }

        [Test]
        public void PendingDraw_WaitsForTheBody_DrawsIntoEmptyHands_DropsWhenTheWeaponStayed()
        {
            Assert.That(PendingDrawRules.Decide(bodyBusy: true, weaponDrawn: true), Is.EqualTo(PendingDrawStep.Wait), "the put-away runs");
            Assert.That(PendingDrawRules.Decide(bodyBusy: true, weaponDrawn: false), Is.EqualTo(PendingDrawStep.Wait));
            Assert.That(PendingDrawRules.Decide(bodyBusy: false, weaponDrawn: false), Is.EqualTo(PendingDrawStep.Draw), "HoldStacks done");
            Assert.That(PendingDrawRules.Decide(bodyBusy: false, weaponDrawn: true), Is.EqualTo(PendingDrawStep.Drop),
                        "cut before HoldStacks: the old weapon is still in the hand");
        }

        [TestCase("bow")]
        [TestCase("crossbow")]
        [TestCase("spell")]
        public void AimHeld_SwitchKey_BittenDuringThePutAway_KeysWorkAgain(string held)
        {
            var hero = new Hero { InHand = held, AimHeld = held != "spell" };
            Assert.That(hero.Key("sword"), Is.True, "PS_Ranged_Aim_Loop takes the key at once");
            Assert.That(hero.PendingDraw && hero.Busy, Is.True, "putting the weapon away, the sword waits");

            hero.Bitten();
            hero.Frame();
            Assert.That(hero.PendingDraw, Is.True, "the stumble has the body: nothing decided yet");
            hero.ReactionOver();
            Assert.That(hero.PendingDraw, Is.False, "the cut switch is over");
            Assert.That(hero.InHand, Is.EqualTo(held), "the weapon stayed in the hand");
            Assert.That(hero.Drew, Is.Null, "nothing is drawn over it");

            Assert.That(hero.Key("sword"), Is.True, "the next key acts at once (it waited for good before fix-197)");
            hero.SheatheEnds();
            Assert.That(hero.Drew, Is.EqualTo("sword"));
        }

        [Test]
        public void AimHeld_Bitten_SheatheKey_ActsAtOnceAfterTheStumble()
        {
            var hero = new Hero { AimHeld = true };
            hero.Bitten();
            Assert.That(hero.Key("bow"), Is.False, "during the stumble the key waits (G3 one-slot queue)");
            hero.ReactionOver();
            Assert.That(hero.Keys.Waiting, Is.Null, "taken in the first loop frame");
            Assert.That(hero.Busy, Is.True, "the bow is being put away");
            hero.SheatheEnds();
            Assert.That(hero.Drawn, Is.False);
        }

        [Test]
        public void PutAwayFinished_BeforeTheBite_TheDrawStillFollows()
        {
            var hero = new Hero { AimHeld = true };
            hero.Key("sword");
            hero.SheatheEnds();
            Assert.That(hero.Drew, Is.EqualTo("sword"), "HoldStacks was done: the draw follows in the same tick");
            Assert.That(hero.PendingDraw, Is.False);
        }

        [Test]
        public void KeyDuringTheCutSwitch_WaitsThenActsOnceTheSwitchIsDropped()
        {
            var hero = new Hero { AimHeld = true };
            hero.Key("sword");
            hero.Bitten(resetAll: false); // a parried stumble keeps the queue
            Assert.That(hero.Key("staff"), Is.False, "a switch and a stumble run: the key waits");
            hero.ReactionOver();
            Assert.That(hero.PendingDraw, Is.True, "the waiting key acted in the same frame: a new switch runs");
            hero.SheatheEnds();
            Assert.That(hero.Drew, Is.EqualTo("staff"), "the latest key wins");
        }
    }
}
